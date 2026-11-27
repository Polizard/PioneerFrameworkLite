using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// 2026-11-25：PFL 自带的最小星系（<c>1.7/Defs/CelestialBuiltin/CelestialBuiltinSystem.xml</c>）。
/// 内容包 NFexp 未启用时由 <see cref="CelestialWorldBootstrapHooks.BeginFreshWorld"/> 回退到它，
/// 否则原版 Surface 的 CelestialSurfaceLayer 缺构造会话会直接把建世界打断。
/// </summary>
public class CelestialBuiltinContentTests
{
    [Fact]
    public void BuiltinContentIsAValidMinimalSystemWithARootSurface()
    {
        using var scope = new CelestialXmlTestScope();
        var document = XDocument.Load(CelestialDefaultContentTests.DataPath("CelestialBuiltin/CelestialBuiltinSystem.xml"));
        var root = document.Root;
        var visuals = root.Elements("PioneerFramework.Celestial.CelestialVisualProfileDef").ToDictionary(
            element => element.Element("defName").Value, element =>
            {
                var profile = Parse<CelestialVisualProfileDef>(element);
                if (element.Element("backgroundBiome") is { } biome) profile.backgroundBiome = new BiomeDef { defName = biome.Value };
                return profile;
            });
        var environments = root.Elements("PioneerFramework.Celestial.CelestialEnvironmentProfileDef").ToDictionary(
            element => element.Element("defName").Value, element => Parse<CelestialEnvironmentProfileDef>(element));
        var biomes = root.Elements("PioneerFramework.Celestial.CelestialBiomeProfileDef").ToDictionary(
            element => element.Element("defName").Value, element =>
            {
                var profile = Parse<CelestialBiomeProfileDef>(element);
                // 夹具不经过 Def 交叉引用：按 XML 文本补齐 worldGenStep，否则 ToProfile 判 InvalidBiomeProfile。
                List<WorldGenStepDef> Steps(string field) => element.Element(field)?.Elements("li")
                    .Select(node => new WorldGenStepDef { defName = node.Value, worldGenStep = new WorldGenStep_Terrain() }).ToList() ?? new();
                profile.worldGenSteps = Steps("worldGenSteps");
                profile.rootOnlySteps = Steps("rootOnlySteps");
                return profile;
            });
        var maps = root.Elements("PioneerFramework.Celestial.CelestialMapProfileDef").ToDictionary(
            element => element.Element("defName").Value, element =>
            {
                var profile = Parse<CelestialMapProfileDef>(element);
                profile.localRecipe = new LocalMapRecipeDef { defName = element.Element("localRecipe").Value };
                return profile;
            });
        var bodies = root.Elements("PioneerFramework.CelestialBodyDef").ToDictionary(
            element => element.Element("defName").Value, element =>
            {
                var body = scope.ParseBody(element.ToString());
                foreach (var properties in body.comps)
                {
                    var source = element.Element("comps").Elements("li").Single(node => node.Element("key").Value == properties.key);
                    if (properties is CompProperties_ExistingEnvironment environment) environment.profile = environments[source.Element("profile").Value];
                    if (properties is CompProperties_BiomeGeneration biome) biome.profile = biomes[source.Element("profile").Value];
                    if (properties is CompProperties_MapGeneration map) map.profile = maps[source.Element("profile").Value];
                    if (properties is CelestialVisualCompProperties visual) visual.profile = visuals[source.Element("profile").Value];
                }
                var template = body.ToTemplate();
                Assert.True(template.Success, string.Join(",", body.ConfigErrors()));
                return body;
            });
        var systemElement = root.Element("PioneerFramework.Celestial.CelestialSystemDef");
        var system = Parse<CelestialSystemDef>(systemElement);
        for (int index = 0; index < system.entries.Count; index++)
            system.entries[index].bodyDef = bodies[systemElement.Element("entries").Elements("li").ElementAt(index).Element("bodyDef").Value];
        var template = system.ToTemplate();
        Assert.True(template.Success, string.Join(",", template.Errors.Select(error => error.Code + " " + error.Detail)));
        // Def 名与 CelestialWorldBootstrap.BuiltinSystem 必须一致，否则回退拿不到这个 Def。
        Assert.Equal(CelestialWorldBootstrap.BuiltinSystem, template.Value.DefinitionId);
        var manifest = CelestialSystemGenerator.Expand(template.Value, 987);
        Assert.True(manifest.Success, string.Join(",", manifest.Errors.Select(error => error.Code + " " + error.Detail)));
        Assert.Equal(2, manifest.Value.Items.Count);
        var star = manifest.Value.Items.Single(item => item.Template.DefinitionId == "PF_BuiltinStar");
        var planet = manifest.Value.Items.Single(item => item.Template.DefinitionId == "PF_BuiltinPlanet");
        Assert.Equal(planet.Id, manifest.Value.RootSurfaceId);
        Assert.True(planet.Template.HasCapability("surface"));
        Assert.Equal(star.Id, planet.InitializationReferences["primaryLight"]);
        Assert.Equal(planet.Id, planet.InitializationReferences["frameOrigin"]);
        Assert.Equal(star.Id, star.InitializationReferences["frameOrigin"]);
    }

    [Fact]
    public void MissingDefaultSystemFallsBackToTheBuiltinSystemAndStillGeneratesTheRootLayer()
    {
        using var fixture = new CelestialWorldBootstrapTests.Fixture();
        var body = new CelestialBodyDef { defName = "PF_BuiltinProbe", label = "builtin probe", category = "Planet", comps = new() {
            new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 1500000 } },
            new CompProperties_Surface { key = "surface", subdivisions = new() { value = 1 } },
            new CompProperties_BiomeGeneration { key = "biomes", profile = fixture.Profile },
            new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id, modeId = "existing.circular.v1",
                initializationPayload = Convert.ToBase64String(CelestialExistingEphemerisTests.Registration(CelestialInstanceId.Parse("unused"), 0).Initialization.Payload.Span) } } };
        fixture.Native.AddDefs(body);
        // 只提供内置系统名：PF_DefaultCelestialSystem 缺席，模拟 NFexp 未启用。
        fixture.Native.AddDefs(new CelestialSystemDef { defName = CelestialWorldBootstrap.BuiltinSystem, rootSurfaceEntry = "root", entries = new() {
            new() { key = "root", bodyDef = body } } });
        CelestialFrameworkStartup.InitializeProviders();
        var harmony = new Harmony("PFL.Tests.BuiltinCelestialFallback");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(WorldGrid), nameof(WorldGrid.GenerateFreshWorld)),
                prefix: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(CelestialWorldBootstrapHooks.BeginFreshWorld)));
            harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GeneratePlanetLayer)),
                postfix: new HarmonyMethod(typeof(CelestialWorldBootstrapHooks), nameof(CelestialWorldBootstrapHooks.PlanetLayerGenerated)));
            fixture.Native.Grid.GenerateFreshWorld();
            Assert.NotNull(CelestialWorldBootstrap.Current);
            var root = Assert.IsType<CelestialSurfaceLayer>(fixture.Native.Grid.Surface);
            Assert.StartsWith(CelestialWorldBootstrap.BuiltinSystem + ":root[0]", root.Header.OwnerId.Value);
            WorldGenerator.GeneratePlanetLayer(root, fixture.Native.World.info.seedString, 1);
            Assert.Null(CelestialWorldBootstrap.Current);
            var manager = fixture.Native.Game.GetComponent<CelestialBodyManager>();
            Assert.True(manager.TryGetCelestialRuntime(out var runtime));
            Assert.Single(runtime.CaptureCatalog().Bodies);
            Assert.Single(fixture.Native.Grid.PlanetLayers);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            CelestialWorldBootstrapHooks.FinalizeWorld(new InvalidOperationException("test cleanup"));
        }
    }

    private static T Parse<T>(XElement element) where T : new()
    {
        var xml = new XmlDocument(); xml.LoadXml(element.ToString());
        return DirectXmlToObject.ObjectFromXml<T>(xml.DocumentElement, false);
    }
}
