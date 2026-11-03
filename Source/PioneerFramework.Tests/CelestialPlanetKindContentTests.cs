using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using PioneerFramework.Celestial;
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
        foreach (var biome in values.OfType<BiomeGenerationResolvedProperties>().Where(p => p.Profile.Mode == CelestialBiomeGenerationMode.LatitudeBands))
        {
            var environment = Assert.Single(values.OfType<ExistingEnvironmentResolvedProperties>()).Profile;
            foreach (double latitude in new[] { -90.0, 0, 90 })
                foreach (bool secondary in new[] { false, true })
                {
                    var selection = content.Biomes[biome.Profile.SelectBiome(latitude, secondary).BiomeId];
                    Assert.Equal(selection.inVacuum, environment.VacuumBiomeIds.Contains(selection.defName));
                }
        }
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
        foreach (string step in new[] { "Plants", "Animals", "WildPlantSpawner", "WildAnimalSpawner", "River", "AncientUtilityBuilding", "ScatterRuinsSimple" })
            Assert.True(CelestialMapGeneration.ShouldSkip(maps, primary, new GenStepDef { defName = step }), step);
        Assert.True(maps.ShouldReplace(primary, "Soil", false));
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
        public Dictionary<string, CelestialBodyDef> Bodies { get; } = new();
        public Dictionary<string, BiomeDef> Biomes { get; } = new();
        private readonly Dictionary<string, XmlNode> nodes = new();
        public Content()
        {
            try { Load(); }
            catch { scope.Dispose(); throw; }
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
            foreach (var pair in environments) pair.Value.vacuumBiomes = Items(nodes[pair.Key], "vacuumBiomes").Select(n => Biome(n.InnerText)).ToList();
            var biomes = nodes.Where(p => p.Value.Name.EndsWith(".CelestialBiomeProfileDef", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => Parse<CelestialBiomeProfileDef>(p.Value));
            foreach (var pair in biomes)
            {
                var n = nodes[pair.Key];
                pair.Value.polarBiome = n["polarBiome"] is { } polar ? Biome(polar.InnerText) : null;
                pair.Value.primaryBiome = n["primaryBiome"] is { } primary ? Biome(primary.InnerText) : null;
                pair.Value.secondaryBiome = n["secondaryBiome"] is { } secondary ? Biome(secondary.InnerText) : null;
                pair.Value.worldGenSteps = Items(n, "worldGenSteps").Select(s => new WorldGenStepDef { defName = s.InnerText, worldGenStep = new WorldGenStep_Terrain() }).ToList();
                pair.Value.rootOnlySteps = Items(n, "rootOnlySteps").Select(s => new WorldGenStepDef { defName = s.InnerText, worldGenStep = new WorldGenStep_Terrain() }).ToList();
                if (pair.Value.terrain != null)
                    foreach (var rule in pair.Value.terrain.biomeRules.Select((rule, i) => (rule, i)))
                        rule.rule.biome = Biome(Items(n["terrain"], "biomeRules").ElementAt(rule.i)["biome"].InnerText);
            }
            var maps = nodes.Where(p => p.Value.Name.EndsWith(".CelestialMapProfileDef", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => Parse<CelestialMapProfileDef>(p.Value));
            TerrainDef Terrain(string name) { var value = (TerrainDef)RuntimeHelpers.GetUninitializedObject(typeof(TerrainDef)); value.defName = name; return value; }
            foreach (var pair in maps)
            {
                pair.Value.replaceTerrains = Items(nodes[pair.Key], "replaceTerrains").Select(n => Terrain(n.InnerText)).ToList();
                pair.Value.replacementTerrain = nodes[pair.Key]["replacementTerrain"] is { } n ? Terrain(n.InnerText) : null;
                foreach (var rule in pair.Value.biomeRules.Select((rule, i) => (rule, i))) rule.rule.biome = Biome(Items(nodes[pair.Key], "biomeRules").ElementAt(rule.i)["biome"].InnerText);
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
        public void Dispose() => scope.Dispose();
    }
}
