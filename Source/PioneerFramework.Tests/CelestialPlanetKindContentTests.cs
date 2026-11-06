using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPlanetKindContentTests
{
    [Theory]
    [InlineData("PF_BarrenPlanet", true, false)]
    [InlineData("PF_TemperatePlanet", true, true)]
    [InlineData("PF_GreenhousePlanet", true, true)]
    [InlineData("PF_FrozenPlanet", true, false)]
    [InlineData("PF_GasGiant", false, true)]
    [InlineData("PF_IceGiant", false, true)]
    public void ShippedKindsResolveUsableComponentsWithoutGrantingGiantsLanding(string id, bool surface, bool air)
    {
        using var content = new Content();
        var body = content.Bodies[id];
        var template = body.ToTemplate();
        Assert.True(template.Success, string.Join(",", body.ConfigErrors()));
        var resolved = template.Value.Resolve(new(42, "kinds/" + id));
        Assert.True(resolved.Success, string.Join(",", resolved.Errors.Select(error => error.Code)));
        var values = resolved.Value.Select(component => component.Properties).ToArray();
        Assert.True(Assert.Single(values.OfType<ICelestialGeometryProperties>()).RadiusMeters > 0);
        var physics = Assert.Single(values.OfType<ICelestialPhysicsBindingProperties>());
        Assert.True(physics.GravitationalParameter > 0);
        Assert.Equal("PFL.ExistingEphemeris", physics.Initialization.ProviderId);
        var bridge = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var star = CelestialInstanceId.Parse("kind-test-star");
        var instance = CelestialInstanceId.Parse("kind-test-body");
        var registration = new CelestialBodyRegistration(id,
            new(instance, 1, values.OfType<ICelestialGeometryProperties>().Single().RadiusMeters, physics.GravitationalParameter),
            physics.Initialization, star, new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = star, ["primaryLight"] = star }, 1);
        bridge.Submit(new("kind-test", bridge.SessionId, 0, new[] {
            PhysicsBodyChange.Register(CelestialExistingEphemerisTests.Registration(star, 0, type: "Star")),
            PhysicsBodyChange.Register(registration) }));
        var prepared = bridge.PrepareInterval(new(bridge.SessionId, 0, 0, 0, new[] { "kind-test" }));
        Assert.Equal(PhysicsPreparationStatus.Prepared, prepared.Status);
        bridge.CommitPreparedInterval(prepared.Token.Value);
        using (var snapshot = bridge.Sample(new(bridge.SessionId, bridge.CommittedStamp.TopologyRevision, new[] { instance })).Snapshot)
        {
            Assert.NotNull(snapshot);
            Assert.True(snapshot.TryGetBodyState(instance, out var state));
            Assert.True(state.Position.IsFinite());
            Assert.True(state.LinearVelocity.IsFinite());
        }
        Assert.Equal(surface, values.OfType<SurfaceResolvedProperties>().Any());
        Assert.Equal(surface, values.OfType<MapGenerationResolvedProperties>().Any());
        Assert.Equal(surface, values.OfType<BiomeGenerationResolvedProperties>().Any());
        Assert.Equal(air, values.OfType<AtmosphereResolvedProperties>().Any());
        Assert.Contains(values.OfType<VisualResolvedProperties>(), visual => visual.Kind == CelestialVisualKind.Surface);
        Assert.DoesNotContain(values.OfType<VisualResolvedProperties>(), visual => visual.Kind == CelestialVisualKind.Stellar);
        if (surface)
            Assert.Equal(CelestialSurfaceGenerationMode.OnDemand, values.OfType<SurfaceResolvedProperties>().Single().GenerationMode);
        else
            Assert.DoesNotContain(resolved.Value, component => component.Descriptor.Provides.Contains("surface") ||
                component.Descriptor.Provides.Contains("map.generation"));
        var registry = CelestialBuiltinCompRegistry.Create();
        foreach (var value in values)
        {
            Assert.True(registry.TryGet(value.HandlerId, out var handler));
            var restored = handler.Codec.DecodeProperties(value.Key, value.Version, value.Serialize());
            Assert.True(restored.Success, string.Join(",", restored.Errors.Select(error => error.Code)));
            Assert.Equal(value.Serialize().ToArray(), restored.Value.Serialize().ToArray());
        }
    }

    [Fact]
    public void MoonBiomesExpressLatitudeBandsThroughParameterizedRulesAndPerBiomeHilliness()
    {
        using var content = new Content();
        var moon = content.BiomeProfiles["PF_MoonBiomes"].ToProfile().Value;
        Assert.Equal(CelestialBiomeGenerationMode.Parameterized, moon.Mode);
        Assert.Equal(0, moon.Temperature); Assert.Equal(0, moon.Rainfall); Assert.Equal(0, moon.Swampiness);
        Assert.Equal(new[] { "Factions", "Roads", "Features" }, moon.RootOnlySteps);
        var environment = content.Environments["PF_MoonEnvironment"].ToProfile().Value;
        Assert.All(moon.Terrain.BiomeRules.Select(rule => rule.BiomeId).Distinct(), biome => Assert.Contains(biome, environment.VacuumBiomeIds));
        var waterIce = moon.Terrain.BiomeRules.Single(rule => rule.BiomeId == "PF_MoonBiomeWater");
        Assert.Equal(10, waterIce.Priority); Assert.Equal(75, waterIce.MinimumAbsoluteLatitude); Assert.Equal((int)Hilliness.Flat, waterIce.Hilliness);
        // 极区优先：waterIce 的 priority 10 高于 basalt/regolith，抽样值落点不影响结果。
        foreach (double pick in new[] { .1, .9 })
        {
            var polar = CelestialPlanetTerrain.SelectBiome(moon.Terrain, 0, 0, 0, 75, .5, pick);
            Assert.Equal("PF_MoonBiomeWater", polar.BiomeId); Assert.Equal((int)Hilliness.Flat, polar.Hilliness);
        }
        // 非极区：basalt/regolith 同优先级同权重 50/50，hilliness 由规则给出。
        Assert.Equal(.5, moon.Terrain.BiomeRules.Single(rule => rule.BiomeId == "PF_MoonBiomeBasalt").Weight);
        Assert.Equal(.5, moon.Terrain.BiomeRules.Single(rule => rule.BiomeId == "PF_MoonBiomeRegolith" && !rule.Fallback).Weight);
        var basalt = CelestialPlanetTerrain.SelectBiome(moon.Terrain, 0, 0, 0, 10, .5, .1);
        Assert.Equal("PF_MoonBiomeBasalt", basalt.BiomeId); Assert.Equal((int)Hilliness.SmallHills, basalt.Hilliness);
        var regolith = CelestialPlanetTerrain.SelectBiome(moon.Terrain, 0, 0, 0, 10, .5, .9);
        Assert.Equal("PF_MoonBiomeRegolith", regolith.BiomeId); Assert.Equal((int)Hilliness.Flat, regolith.Hilliness);
        var fallback = moon.Terrain.BiomeRules.Single(rule => rule.Fallback);
        Assert.Equal("PF_MoonBiomeRegolith", fallback.BiomeId); Assert.Equal((int)Hilliness.Flat, fallback.Hilliness);
    }

    [Fact]
    public void ShippedParameterizedProfilesKeepNativeStepsOffTerrainAndInSyncWithWaterFlags()
    {
        using var content = new Content();
        var parameterized = content.BiomeProfiles.Where(pair => pair.Value.mode == CelestialBiomeGenerationMode.Parameterized).ToArray();
        Assert.NotEmpty(parameterized);
        foreach (var pair in parameterized)
        {
            var steps = pair.Value.worldGenSteps.Select(step => step.defName).ToArray();
            Assert.DoesNotContain("Terrain", steps);
            Assert.Equal(pair.Value.terrain.allowRivers, steps.Contains("Rivers"));
            Assert.Equal(pair.Value.terrain.allowLakes, steps.Contains("Lakes"));
            var profile = pair.Value.ToProfile();
            Assert.True(profile.Success, pair.Key + ": " + string.Join(",", profile.Errors.Select(error => error.Detail)));
        }
        var temperate = content.BiomeProfiles["PF_TemperateBiomes"];
        var temperateSteps = temperate.worldGenSteps.Select(step => step.defName).ToArray();
        Assert.Contains("Lakes", temperateSteps);
        Assert.Contains("Rivers", temperateSteps);
        Assert.True(temperate.terrain.allowRivers);
        Assert.True(temperate.terrain.allowLakes);
    }

    [Theory]
    [InlineData("PF_BarrenPlanet", "PF_BarrenRock", "PF_BarrenRegolith", -30f)]
    [InlineData("PF_FrozenPlanet", "PF_FrozenPlain", "PF_BarrenRock", -160f)]
    [InlineData("PF_GreenhousePlanet", "PF_GreenhousePlain", "PF_GreenhousePlain", 460f)]
    public void AlienProfilesSelectTheirOwnTerrainAndSuppressEarthEcology(string id, string primary, string secondary, float temperature)
    {
        using var content = new Content();
        var values = content.Bodies[id].ToTemplate().Value.Resolve(new(42, id)).Value.Select(c => c.Properties).ToArray();
        var biomes = values.OfType<BiomeGenerationResolvedProperties>().Single().Profile;
        Assert.Equal(primary, CelestialPlanetTerrain.SelectBiome(biomes.Terrain, temperature, 0, 0, 0, 0, .5).BiomeId);
        Assert.Equal(secondary, CelestialPlanetTerrain.SelectBiome(biomes.Terrain, temperature, 0, 0, 0, .5, .5).BiomeId);
        Assert.Equal(temperature, biomes.Temperature);
        Assert.Equal(0, biomes.Rainfall);
        var maps = values.OfType<MapGenerationResolvedProperties>().Single().Profile;
        var rules = Assert.Single(maps.LocalSpecification.Recipe.Steps, step => step.HandlerId == LocalMapBuiltinConfigurations.NativeRules);
        var selectors = LocalMapBuiltinConfigurations.Decode<NativeRulesConfiguration>(rules.Configuration).Skip;
        foreach (string step in new[] { "Plants", "Animals", "WildPlantSpawner", "WildAnimalSpawner", "River", "AncientUtilityBuilding", "ScatterRuinsSimple" })
            Assert.Contains(selectors, selector => NativeMapStepSelection.Matches(selector, primary, new GenStepDef { defName = step }));
        var replacement = Assert.Single(maps.LocalSpecification.Recipe.Steps, step => step.HandlerId == LocalMapBuiltinConfigurations.TerrainReplacement);
        Assert.True(LocalMapBuiltinConfigurations.Decode<TerrainReplacementConfiguration>(replacement.Configuration).ShouldReplace(primary, "Soil", false));
        Assert.All(new[] { primary, secondary }.Distinct(), name => {
            Assert.Equal(0, content.Biomes[name].plantDensity);
            Assert.Equal(0, content.Biomes[name].animalDensity);
        });
    }

    [Fact]
    public void NativeEarthBiomeSelectionCannotPickProfileOnlyAlienBiomes()
    {
        using var content = new Content();
        using var native = new CelestialNativeWorldTestScope();
        var candidate = new BiomeDef { defName = "PF_TestNativeDesert", implemented = true, generatesNaturally = true, workerClass = typeof(BiomeWorker_Desert) };
        native.AddDefs(candidate);
        // Limit the native selector to one vanilla candidate and the actual new content.
        DefDatabase<BiomeDef>.Clear();
        DefDatabase<BiomeDef>.Add(candidate);
        foreach (var biome in content.Biomes.Values.Where(b => b.defName is "PF_BarrenRock" or "PF_BarrenRegolith" or "PF_FrozenPlain" or "PF_GreenhousePlain"))
            DefDatabase<BiomeDef>.Add(biome);
        var definition = new PlanetLayerDef { defName = "PF_TestNativeLayer", layerType = typeof(SurfaceLayer), tileType = typeof(SurfaceTile), worldDrawLayers = new() };
        var layer = native.Grid.RegisterPlanetLayer(definition, radius: 100f, subdivisions: 0);
        var tile = new SurfaceTile(new PlanetTile(0, layer)) { temperature = 460, rainfall = 0 };
        var select = AccessTools.MethodDelegate<Func<WorldGenStep_Terrain, Tile, PlanetTile, PlanetLayer, BiomeDef>>(
            AccessTools.Method(typeof(WorldGenStep_Terrain), "BiomeFrom"));
        // Alien candidates are first so an accidental natural-generation flag wins the tie.
        var aliens = DefDatabase<BiomeDef>.AllDefsListForReading.Where(b => b != candidate).ToArray();
        DefDatabase<BiomeDef>.Clear();
        foreach (var biome in aliens) DefDatabase<BiomeDef>.Add(biome);
        DefDatabase<BiomeDef>.Add(candidate);
        Assert.Same(candidate, select(new WorldGenStep_Terrain(), tile, tile.tile, layer));
    }

    internal sealed class Content : IDisposable
    {
        private readonly CelestialXmlTestScope scope = new();
        private readonly ShippedLocalMapRecipeDefs mapRecipes;
        public Dictionary<string, CelestialBodyDef> Bodies { get; } = new();
        public Dictionary<string, CelestialMapProfileDef> Maps { get; } = new();
        public Dictionary<string, BiomeDef> Biomes { get; } = new();
        public Dictionary<string, CelestialBiomeProfileDef> BiomeProfiles { get; } = new();
        public Dictionary<string, CelestialEnvironmentProfileDef> Environments { get; } = new();
        private readonly Dictionary<string, XmlNode> nodes = new();
        public Content()
        {
            try { mapRecipes = new(); Load(); }
            catch { Dispose(); throw; }
        }
        private void Load()
        {
            scope.InheritanceUsed = true;
            foreach (string file in new[] { "BiomesDefs/Biome_PlanetKinds.xml", "CelestialFramework/CelestialProfiles.xml", "CelestialFramework/CelestialVisualProfiles.xml",
                "CelestialFramework/CelestialPlanetKindProfiles.xml", "CelestialFramework/CelestialPlanetKindVisuals.xml", "CelestialBodies/CelestialBodies_PlanetKinds.xml" })
            {
                string path = CelestialDefaultContentTests.DataPath(file);
                Assert.True(File.Exists(path), "Missing shipped planet-kind content: " + file);
                var document = new XmlDocument(); document.Load(path);
                foreach (XmlNode node in document.DocumentElement.ChildNodes)
                    if (node.NodeType == XmlNodeType.Element)
                    {
                        XmlInheritance.TryRegister(node, null);
                        if (node["defName"] != null) nodes.Add(node["defName"].InnerText, node);
                    }
            }
            XmlInheritance.Resolve();
            foreach (var pair in nodes.Where(p => p.Value.Name == "BiomeDef")) Biomes.Add(pair.Key, Parse<BiomeDef>(pair.Value));
            BiomeDef Biome(string name) => Biomes.TryGetValue(name, out var biome) ? biome : new BiomeDef { defName = name, inVacuum = name.StartsWith("PF_MoonBiome", StringComparison.Ordinal) };
            var visuals = nodes.Where(p => p.Value.Name.EndsWith(".CelestialVisualProfileDef", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => Parse<CelestialVisualProfileDef>(p.Value));
            foreach (var pair in visuals) if (nodes[pair.Key]["backgroundBiome"] is { } n) pair.Value.backgroundBiome = Biome(n.InnerText);
            var environments = nodes.Where(p => p.Value.Name.EndsWith(".CelestialEnvironmentProfileDef", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => Parse<CelestialEnvironmentProfileDef>(p.Value));
            foreach (var pair in environments)
            {
                pair.Value.vacuumBiomes = Items(nodes[pair.Key], "vacuumBiomes").Select(n => Biome(n.InnerText)).ToList();
                Environments.Add(pair.Key, pair.Value);
            }
            var biomes = nodes.Where(p => p.Value.Name.EndsWith(".CelestialBiomeProfileDef", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => Parse<CelestialBiomeProfileDef>(p.Value));
            foreach (var pair in biomes)
            {
                var n = nodes[pair.Key];
                pair.Value.worldGenSteps = Items(n, "worldGenSteps").Select(s => new WorldGenStepDef { defName = s.InnerText, worldGenStep = new WorldGenStep_Terrain() }).ToList();
                pair.Value.rootOnlySteps = Items(n, "rootOnlySteps").Select(s => new WorldGenStepDef { defName = s.InnerText, worldGenStep = new WorldGenStep_Terrain() }).ToList();
                if (pair.Value.terrain != null)
                    foreach (var rule in pair.Value.terrain.biomeRules.Select((rule, i) => (rule, i)))
                        rule.rule.biome = Biome(Items(n["terrain"], "biomeRules").ElementAt(rule.i)["biome"].InnerText);
                BiomeProfiles.Add(pair.Key, pair.Value);
            }
            var maps = nodes.Where(p => p.Value.Name.EndsWith(".CelestialMapProfileDef", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => Parse<CelestialMapProfileDef>(p.Value));
            foreach (var pair in maps)
            {
                Maps.Add(pair.Key, pair.Value);
                pair.Value.localRecipe = mapRecipes.Recipes[nodes[pair.Key]["localRecipe"].InnerText];
                if (pair.Value.diversity != null)
                    foreach (var item in pair.Value.diversity.weather.Select((item, i) => (item, i)))
                        item.item.def = new WeatherDef { defName = Items(nodes[pair.Key]["diversity"], "weather").ElementAt(item.i)["def"].InnerText };
            }
            foreach (var pair in nodes.Where(p => p.Value.Name == "PioneerFramework.CelestialBodyDef"))
            {
                var body = Parse<CelestialBodyDef>(pair.Value);
                foreach (var comp in body.comps)
                {
                    string profile = pair.Value.SelectSingleNode("comps/li[key='" + comp.key + "']/profile")?.InnerText;
                    if (comp is CelestialVisualCompProperties v) v.profile = visuals[profile];
                    if (comp is CompProperties_ExistingEnvironment e) e.profile = environments[profile];
                    if (comp is CompProperties_BiomeGeneration b) b.profile = biomes[profile];
                    if (comp is CompProperties_MapGeneration m) m.profile = maps[profile];
                }
                Bodies.Add(pair.Key, body);
            }
        }
        private static IEnumerable<XmlNode> Items(XmlNode node, string field) => XmlInheritance.GetResolvedNodeFor(node).SelectNodes(field + "/li").Cast<XmlNode>();
        private static T Parse<T>(XmlNode node) where T : new() => DirectXmlToObject.ObjectFromXml<T>(node, false);
        public void Dispose() { mapRecipes?.Dispose(); scope.Dispose(); }
    }
}
