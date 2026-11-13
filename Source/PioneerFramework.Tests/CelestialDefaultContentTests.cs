using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialDefaultContentTests
{
    [Fact]
    public void ActiveRootPatchInstallsOwnedTypeAndTheSingleProfileDispatcher()
    {
        using var scope = new CelestialXmlTestScope();
        var native = new XmlDocument();
        native.LoadXml("<Defs><PlanetLayerDef><defName>Surface</defName><layerType>SurfaceLayer</layerType><worldGenSteps><li>Terrain</li><li>Factions</li></worldGenSteps></PlanetLayerDef></Defs>");
        var patch = new XmlDocument();
        patch.Load(DataPath("../Patches/PlanetLayerDefs_CelestialSurface.xml"));
        var operation = DirectXmlToObject.ObjectFromXml<PatchOperation>(patch.SelectSingleNode("Patch/Operation"), false);
        Assert.True(operation.Apply(native));
        Assert.Equal("PioneerFramework.Celestial.CelestialSurfaceLayer", native.SelectSingleNode("//layerType").InnerText);
        Assert.Equal("PF_CelestialTiles", Assert.Single(native.SelectNodes("//worldGenSteps/li").Cast<XmlNode>()).InnerText);
        var profile = XDocument.Load(DataPath("CelestialFramework/CelestialProfiles.xml")).Root.Elements().Single(node => node.Element("defName").Value == "PF_EarthBiomes");
        Assert.Null(profile.Element("worldGenSteps").Elements("li").Single(node => node.Value == "AncientSites").Attribute("MayRequire"));
        Assert.Null(profile.Element("worldGenSteps").Elements("li").Single(node => node.Value == "AncientRoads").Attribute("MayRequire"));
    }

    // Exercise the actual runtime package, including inherited XML and the external sample patch.
    internal static string DataPath(string relative)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root.FullName, "1.7", "Defs", relative);
    }
    private static T Parse<T>(XElement element) where T : new()
    {
        var xml = new XmlDocument(); xml.LoadXml(element.ToString());
        return DirectXmlToObject.ObjectFromXml<T>(xml.DocumentElement, false);
    }
    [Fact]
    public void DefaultSystemUsesFixedExistingNumbersAndExplicitProfilesAndLightBindings()
    {
        using var scope = new CelestialXmlTestScope();
        using var mapRecipes = new ShippedLocalMapRecipeDefs();
        var profilesXml = XDocument.Load(DataPath("CelestialFramework/CelestialProfiles.xml"));
        var visualsXml = XDocument.Load(DataPath("CelestialFramework/CelestialVisualProfiles.xml"));
        var visuals = visualsXml.Root.Elements("PioneerFramework.Celestial.CelestialVisualProfileDef").ToDictionary(element => element.Element("defName").Value, element => {
            var profile = Parse<CelestialVisualProfileDef>(element);
            profile.backgroundBiome = element.Element("backgroundBiome") is { } biome ? new BiomeDef { defName = biome.Value } : null;
            return profile;
        });
        var environments = profilesXml.Root.Elements("PioneerFramework.Celestial.CelestialEnvironmentProfileDef").ToDictionary(
            element => element.Element("defName").Value, element => {
                var profile = Parse<CelestialEnvironmentProfileDef>(element);
                profile.vacuumBiomes = element.Element("vacuumBiomes")?.Elements("li").Select(item => new BiomeDef { defName = item.Value, inVacuum = true }).ToList() ?? new();
                return profile;
            });
        var biomes = profilesXml.Root.Elements("PioneerFramework.Celestial.CelestialBiomeProfileDef").ToDictionary(
            element => element.Element("defName").Value, element => {
                var profile = Parse<CelestialBiomeProfileDef>(element);
                // 夹具不经过 Def 交叉引用：按内容约定补齐原生 inVacuum（月球三群落为真空），供 P4 真空声明跨校验使用。
                BiomeDef Biome(string name) => new() { defName = name, inVacuum = name.StartsWith("PF_MoonBiome", StringComparison.Ordinal) };
                List<WorldGenStepDef> Steps(string field) => element.Element(field)?.Elements("li").Select(node => new WorldGenStepDef { defName = node.Value, worldGenStep = new WorldGenStep_Terrain() }).ToList() ?? new();
                profile.worldGenSteps = Steps("worldGenSteps"); profile.rootOnlySteps = Steps("rootOnlySteps");
                // Def 交叉引用在测试夹具里不经过 ResolveAllWantedCrossReferences；Parameterized 规则的 biome 引用按 XML 文本显式补齐。
                if (element.Element("terrain")?.Element("biomeRules") is { } rules)
                    for (int index = 0; index < profile.terrain.biomeRules.Count; index++)
                        profile.terrain.biomeRules[index].biome = Biome(rules.Elements("li").ElementAt(index).Element("biome").Value);
                return profile;
            });
        var maps = profilesXml.Root.Elements("PioneerFramework.Celestial.CelestialMapProfileDef").ToDictionary(
            element => element.Element("defName").Value, element => {
                var profile = Parse<CelestialMapProfileDef>(element);
                profile.localRecipe = mapRecipes.Recipes[element.Element("localRecipe").Value];
                return profile;
            });
        var bodyXml = XDocument.Load(DataPath("CelestialBodies/CelestialBodies_SolarSystem.xml"));
        var bodies = bodyXml.Root.Elements("PioneerFramework.CelestialBodyDef").ToDictionary(element => element.Element("defName").Value, element => {
            var body = scope.ParseBody(element.ToString());
            foreach (var properties in body.comps)
            {
                var source = element.Element("comps").Elements("li").Single(node => node.Element("key").Value == properties.key);
                if (properties is CompProperties_ExistingEnvironment environment) environment.profile = environments[source.Element("profile").Value];
                if (properties is CompProperties_BiomeGeneration biome) biome.profile = biomes[source.Element("profile").Value];
                if (properties is CompProperties_MapGeneration map) map.profile = maps[source.Element("profile").Value];
                if (properties is CelestialVisualCompProperties visual) visual.profile = visuals[source.Element("profile").Value];
            }
            Assert.True(body.ToTemplate().Success, string.Join(",", body.ConfigErrors()));
            return body;
        });
        var systemXml = XDocument.Load(DataPath("CelestialFramework/CelestialSystems.xml")).Root.Element("PioneerFramework.Celestial.CelestialSystemDef");
        var system = Parse<CelestialSystemDef>(systemXml);
        for (int i = 0; i < system.entries.Count; i++) system.entries[i].bodyDef = bodies[systemXml.Element("entries").Elements("li").ElementAt(i).Element("bodyDef").Value];
        var manifest = CelestialSystemGenerator.Expand(system.ToTemplate().Value, 987).Value;
        Assert.Equal(4, manifest.Items.Count);
        var earth = manifest.Items.Single(item => item.Template.DefinitionId == "Earth");
        var moon = manifest.Items.Single(item => item.Template.DefinitionId == "Moon");
        var sun = manifest.Items.Single(item => item.Template.DefinitionId == "Sun");
        var zheSha = manifest.Items.Single(item => item.Template.DefinitionId == "ZheSha");
        Assert.Equal(earth.Id, manifest.RootSurfaceId);
        Assert.Equal(5000000, earth.Components.Select(c => c.Properties).OfType<ICelestialGeometryProperties>().Single().RadiusMeters);
        Assert.Equal(2e14, earth.Components.Select(c => c.Properties).OfType<ICelestialPhysicsBindingProperties>().Single().GravitationalParameter);
        var air = earth.Components.Select(c => c.Properties).OfType<AtmosphereResolvedProperties>().Single();
        Assert.Equal(1.2, air.Rho0); Assert.Equal(8000, air.ScaleHeight); Assert.Equal(100000, air.TopAltitude);
        Assert.Equal(1500000, moon.Components.Select(c => c.Properties).OfType<ICelestialGeometryProperties>().Single().RadiusMeters);
        Assert.Equal(5e12, moon.Components.Select(c => c.Properties).OfType<ICelestialPhysicsBindingProperties>().Single().GravitationalParameter);
        Assert.Equal(317900, moon.Components.Select(c => c.Properties).OfType<ExistingEnvironmentResolvedProperties>().Single().Profile.DayLengthTicks);
        Assert.Equal(new[] { "Factions", "Roads", "Features" }, moon.Components.Select(c => c.Properties).OfType<BiomeGenerationResolvedProperties>().Single().Profile.RootOnlySteps);
        Assert.Equal(5300000, zheSha.Components.Select(c => c.Properties).OfType<ICelestialGeometryProperties>().Single().RadiusMeters);
        Assert.Equal(2.4e14, zheSha.Components.Select(c => c.Properties).OfType<ICelestialPhysicsBindingProperties>().Single().GravitationalParameter);
        Assert.Contains(zheSha.Components, component => component.Properties is VisualResolvedProperties);
        var zheShaSurface = zheSha.Components.Select(c => c.Properties).OfType<SurfaceResolvedProperties>().Single();
        Assert.Equal(CelestialSurfaceGenerationMode.Creation, zheShaSurface.GenerationMode);
        Assert.Equal(sun.Id, zheSha.InitializationReferences["primaryLight"]);
        Assert.Equal(zheSha.Id, zheSha.InitializationReferences["frameOrigin"]);

        // 月球环境（2026-11-07 修复）：必须是 v2（climate+exposure），昼夜温差与真空通道真的生效，且参数化地表绑定同一份冻结 climate。
        var moonEnvironment = moon.Components.Select(c => c.Properties).OfType<ExistingEnvironmentResolvedProperties>().Single().Profile;
        Assert.NotNull(moonEnvironment.Climate); Assert.NotNull(moonEnvironment.Exposure);
        Assert.Equal(3, moonEnvironment.VacuumBiomeIds.Count);
        Assert.True(moonEnvironment.Exposure.HypoxiaPerDay > 0 && moonEnvironment.Exposure.PressurePerDay > 0);
        // Authored 相位：t=0 为最冷、t=dayLength/2 为最热（赤道，热响应 600s 只带来微小滞后）。
        Assert.InRange(CelestialClimateModel.Temperature(moonEnvironment.Climate, new(0, 0, 0), 317900, 1), -171, -169);
        Assert.InRange(CelestialClimateModel.Temperature(moonEnvironment.Climate, new(317900 * 0.5, 0, 0), 317900, 1), 119, 121);
        var moonGeneration = moon.Components.Select(c => c.Properties).OfType<BiomeGenerationResolvedProperties>().Single().Profile;
        Assert.NotNull(moonGeneration.Terrain.Climate);
        Assert.Equal(moonEnvironment.Climate.DiurnalAmplitudeCelsius, moonGeneration.Terrain.Climate.DiurnalAmplitudeCelsius);
        Assert.Equal(moonEnvironment.Climate.MeanTemperatureCelsius, moonGeneration.Terrain.Climate.MeanTemperatureCelsius);
        Assert.Equal(sun.Id, earth.InitializationReferences["primaryLight"]);
        Assert.Equal(sun.Id, moon.InitializationReferences["primaryLight"]);
        Assert.Equal(sun.Id, sun.InitializationReferences["frameOrigin"]);
        Assert.Equal(earth.Id, earth.InitializationReferences["frameOrigin"]);
        // 2026-11-11：月球改真潮汐锁定 —— 同步目标必须是 Parent 作用域的 frameOrigin（⇒ 母星 Earth），
        // 且载荷走 v2 权威自转（旧版是"日长=公转周期"的数值近似）。
        Assert.Equal(earth.Id, moon.InitializationReferences["frameOrigin"]);
        var moonMotion = ExistingEphemerisParameters.Parse(
            moon.Components.Select(component => component.Properties).OfType<ICelestialPhysicsBindingProperties>().Single().Initialization).Value;
        Assert.True(moonMotion.UsesAuthoritativeRotation);
        Assert.Equal("frameOrigin", moonMotion.SynchronousTargetReference);
        Assert.Equal(317900, moonMotion.DayLengthTicks);
        Assert.Equal(earth.Id, moon.InitializationReferences["frameOrigin"]);
        Assert.Single(sun.Components.Where(c => c.Properties is LightSourceResolvedProperties));
        Assert.Contains(sun.Components, component => component.Properties is VisualResolvedProperties { Kind: CelestialVisualKind.Stellar });
        Assert.Contains(earth.Components, component => component.Properties is VisualResolvedProperties { Kind: CelestialVisualKind.Clouds });
        Assert.DoesNotContain(moon.Components, component => component.Properties is VisualResolvedProperties { Kind: CelestialVisualKind.Clouds });
        Assert.Equal(CelestialSurfaceGenerationMode.OnDemand, earth.Components.Select(c => c.Properties).OfType<SurfaceResolvedProperties>().Single().GenerationMode);
        Assert.Equal(CelestialSurfaceGenerationMode.Creation, moon.Components.Select(c => c.Properties).OfType<SurfaceResolvedProperties>().Single().GenerationMode);
        var moonMap = moon.Components.Select(c => c.Properties).OfType<MapGenerationResolvedProperties>().Single().Profile;
        var steps = moonMap.LocalSpecification.Recipe.Steps;
        var rules = Assert.Single(steps, step => step.HandlerId == LocalMapBuiltinConfigurations.NativeRules);
        Assert.Contains(LocalMapBuiltinConfigurations.Decode<NativeRulesConfiguration>(rules.Configuration).Skip,
            selector => NativeMapStepSelection.Matches(selector, "PF_MoonBiomeBasalt", new GenStepDef { defName = "Snow" }));
        var replacement = LocalMapBuiltinConfigurations.Decode<TerrainReplacementConfiguration>(
            Assert.Single(steps, step => step.HandlerId == LocalMapBuiltinConfigurations.TerrainReplacement).Configuration);
        Assert.True(replacement.ShouldReplace("PF_MoonBiomeRegolith", "AnyRock", true));
        Assert.False(replacement.ShouldReplace("PF_MoonBiomeBasalt", "AnyRock", true));
    }

    [Fact]
    public void ExistingVacuumBiomeRulesRemainExplicitNativeData()
    {
        var root = new DirectoryInfo(DataPath("BiomesDefs/Biome_Planets.xml"));
        while (root != null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        var biomes = XDocument.Load(Path.Combine(root.FullName, "1.7", "Defs", "BiomesDefs", "Biome_Planets.xml")).Root.Elements("BiomeDef").ToArray();
        Assert.Equal(3, biomes.Length);
        foreach (var biome in biomes)
        {
            Assert.Equal("true", biome.Element("inVacuum").Value);
            Assert.Equal("0", biome.Element("plantDensity").Value); Assert.Equal("0", biome.Element("animalDensity").Value);
            Assert.Equal("false", biome.Element("wildAnimalsCanWanderInto").Value);
            Assert.Equal("1", biome.Element("baseWeatherCommonalities").Element("PF_Vaccum").Value);
        }
    }
}
