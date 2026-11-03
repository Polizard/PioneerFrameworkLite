using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RimWorld;
using RimWorld.Planet;
using HarmonyLib;
using Verse;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPlanetTerrainTests
{
    private static readonly string[] sampleFields = { "ElevationMeters", "Roughness", "MountainScore", "Geology", "Moisture" };
    [Fact]
    public void ParameterizedTerrainRequiresAnExplicitNewGenerationMode()
    {
        Assert.True(Enum.IsDefined(typeof(CelestialBiomeGenerationMode), "Parameterized"));
        Assert.Equal(0, (int)CelestialBiomeGenerationMode.LatitudeBands);
        Assert.Equal(1, (int)CelestialBiomeGenerationMode.VanillaSurface);
    }

    private static Type TerrainType()
    {
        var type = typeof(CelestialGenerationProfile).Assembly.GetType("PioneerFramework.Celestial.CelestialPlanetTerrain");
        Assert.NotNull(type); return type;
    }
    private static object Configuration(string json = "{}")
    {
        var type = typeof(CelestialGenerationProfile).Assembly.GetType("PioneerFramework.Celestial.CelestialPlanetTerrainConfiguration");
        Assert.NotNull(type); return JsonSerializer.Deserialize(json, type);
    }
    private static double[] Sample(double lon, double lat, int seed, string json = "{}")
    {
        var type = TerrainType(); var method = type.GetMethod("Sample"); Assert.NotNull(method);
        var result = method.Invoke(null, new[] { Configuration(json), (object)lon, lat, seed });
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(result));
        return sampleFields.Select(key => data.RootElement.GetProperty(key).GetDouble()).ToArray();
    }
    [Fact]
    public void ContinuousSphereHasNoLongitudeSeamOrPolarSingularity()
    {
        foreach (double latitude in new[] { -90, -60d, 0, 60, 90 })
        {
            var a = Sample(-180, latitude, 61); var b = Sample(180, latitude, 61);
            for (int i = 0; i < a.Length; i++) Assert.InRange(Math.Abs(a[i] - b[i]), 0, 1e-8);
        }
        foreach (double latitude in new[] { -90d, 90d })
        {
            var a = Sample(-170, latitude, 61); var b = Sample(31, latitude, 61);
            for (int i = 0; i < a.Length; i++) Assert.InRange(Math.Abs(a[i] - b[i]), 0, 1e-8);
        }
        var c = Sample(22, 40, 61); var d = Sample(22.00000001, 40, 61);
        for (int i = 0; i < c.Length; i++) Assert.InRange(Math.Abs(c[i] - d[i]), 0, .0001);
    }
    [Fact]
    public void TerrainIsStableBySeedAndMountainsDoNotReplaceElevation()
    {
        Assert.Equal(Sample(12, 34, 5), Sample(12, 34, 5));
        Assert.NotEqual(Sample(12, 34, 5), Sample(12, 34, 6));
        var low = Sample(12, 34, 5, "{\"MountainWeight\":0}");
        var high = Sample(12, 34, 5, "{\"MountainWeight\":1}");
        Assert.Equal(low[0], high[0]); Assert.Equal(0, low[2]); Assert.True(high[2] > 0);
        Assert.Equal(0, Sample(12, 34, 5, "{\"Roughness\":0}")[1]);
    }
    [Fact]
    public void OceanCoverageUsesExactDiscreteRankAndRejectsAmbiguousWaterModes()
    {
        var type = TerrainType(); var method = type.GetMethod("SelectOceanTiles"); Assert.NotNull(method);
        var elevations = Enumerable.Range(0, 101).Select(i => (double)(i % 17)).ToArray();
        var ocean = (bool[])method.Invoke(null, new[] { Configuration("{\"OceanCoverage\":0.37}"), elevations });
        Assert.Equal(37, ocean.Count(value => value));
        Assert.InRange(Math.Abs(ocean.Count(v => v) / 101d - .37), 0, 1d / 101);
        var error = Assert.ThrowsAny<Exception>(() => Configuration("{\"OceanCoverage\":0.5,\"SeaLevelMeters\":0}"));
        Assert.Contains("water", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData("{\"ContinentScale\":0}")]
    [InlineData("{\"OceanCoverage\":1.1}")]
    [InlineData("{\"MountainWeight\":-1}")]
    public void InvalidTerrainDomainsAreRejected(string json)
    {
        TerrainType(); Assert.ThrowsAny<Exception>(() => Configuration(json));
    }
    [Fact]
    public void ParameterizedNativeWorldWritesPhysicalFieldsAndOnlyRequestedLayer()
    {
        using var fixture = new CelestialWorldBootstrapTests.Fixture();
        fixture.Profile.mode = CelestialBiomeGenerationMode.Parameterized;
        fixture.Profile.terrain = new() { oceanCoverage = new() { value = .3 }, oceanBiome = fixture.Polar,
            biomeRules = new() { new() { key = "fallback", biome = fixture.Primary, fallback = true },
                new() { key = "high", biome = fixture.Secondary, minimumElevationMeters = 0, priority = 2 } } };
        var body = new CelestialBodyDef { defName = "PD_TerrainBody", comps = new() {
            new CompProperties_SphericalBody { key = "shape" }, new CompProperties_Surface { key = "surface", subdivisions = new() { value = 1 } },
            new CompProperties_BiomeGeneration { key = "biomes", profile = fixture.Profile } } }.ToTemplate().Value;
        var system = new CelestialSystemTemplate("PD_TerrainSystem", "chosen", new[] { new CelestialSystemEntry("chosen", body, IntParameterSpec.Fixed(1)), new CelestialSystemEntry("optional", body, IntParameterSpec.Fixed(1)) });
        var started = CelestialWorldBootstrap.Begin(system, 43, CelestialWorldBootstrapTests.Fixture.Connect);
        Assert.True(started.Success, string.Join(",", started.Errors)); using var scope = started.Value;
        fixture.Native.Grid.GenerateFreshWorld(); var layer = (CelestialSurfaceLayer)fixture.Native.Grid.Surface;
        var result = CelestialWorldGeneration.Generate(layer, fixture.Profile.ToProfile().Value, "parameterized");
        Assert.True(result.Success, string.Join(",", result.Errors));
        Assert.Equal(CelestialSurfaceState.Ready, layer.Header.State);
        Assert.InRange(Math.Abs(layer.Tiles.Count(t => t.PrimaryBiome == fixture.Polar) / (double)layer.TilesCount - .3), 0, 1d / layer.TilesCount);
        Assert.True(layer.Tiles.Select(t => t.elevation).Distinct().Count() > 2);
        Assert.Contains(layer.Tiles, t => t.elevation < 0);
        Assert.All(layer.Tiles, t => Assert.Equal(t.PrimaryBiome == fixture.Polar, t.WaterCovered));
        Assert.Single(fixture.Native.Grid.PlanetLayers);
        var bytes = layer.Header.GenerationConfiguration.ToArray();
        fixture.Profile.terrain.elevationOffsetMeters = 50000;
        float elevation = layer[0].elevation;
        Assert.True(CelestialWorldGeneration.Generate(layer, fixture.Profile.ToProfile().Value).Success);
        Assert.Equal(elevation, layer[0].elevation); Assert.Equal(bytes, layer.Header.GenerationConfiguration.ToArray());
    }
    [Fact]
    public void RulesHaveStableKeysPriorityWeightedTiesAndUniqueUnconditionalFallback()
    {
        var fallback = new CelestialPlanetBiomeRule("fallback", "plain", fallback: true);
        var a = new CelestialPlanetBiomeRule("a", "warm", priority: 5, weight: 1, minimumTemperatureCelsius: 0);
        var b = new CelestialPlanetBiomeRule("b", "wet", priority: 5, weight: 3, minimumRainfallMillimeters: 50);
        var lower = new CelestialPlanetBiomeRule("lower", "other", priority: 4, weight: 10000);
        var c = new CelestialPlanetTerrainConfiguration(biomeRules: new[] { b, fallback, lower, a });
        Assert.Same(a, CelestialPlanetTerrain.SelectBiome(c, 10, 100, 0, 0, .5, .1));
        Assert.Same(b, CelestialPlanetTerrain.SelectBiome(c, 10, 100, 0, 0, .5, .5));
        var only = new CelestialPlanetTerrainConfiguration(biomeRules: new[] { a, fallback });
        Assert.Same(fallback, CelestialPlanetTerrain.SelectBiome(only, -10, 0, 0, 0, .5, .5));
        Assert.Throws<ArgumentException>(() => new CelestialPlanetTerrainConfiguration(biomeRules: new[] { a }));
        Assert.Throws<ArgumentException>(() => new CelestialPlanetTerrainConfiguration(biomeRules: new[] { fallback, fallback }));
        Assert.Throws<ArgumentException>(() => new CelestialPlanetBiomeRule("bad", "plain", fallback: true, minimumAbsoluteLatitude: 10));
    }
    [Fact]
    public void WholeRuleIntervalsAndOverridesAreValidatedBeforeSampling()
    {
        var biome = new BiomeDef { defName = "plain" };
        var profile = new CelestialBiomeProfileDef { defName = "PD", mode = CelestialBiomeGenerationMode.Parameterized, terrain = new() { biomeRules = new() {
            new() { key = "fallback", biome = biome, fallback = true }, new() { key = "temperature", biome = biome,
                parameters = new() { new() { key = "minimumTemperatureCelsius", value = new() { min = 0, max = 20 } },
                    new() { key = "maximumTemperatureCelsius", value = new() { min = 10, max = 30 } } } } } } };
        Assert.False(profile.ToProfile().Success);
        profile.terrain.biomeRules.RemoveAt(1);
        var context = new CelestialGenerationContext(5, "body", "biomes", new[] { new CelestialParameterOverride("biomes", "terrain.mountainWeight", Min: 0, Max: 2) });
        Assert.False(profile.ToProfile(context).Success);
    }
    [Fact]
    public void ActualLayerEnvironmentAndGeneratedTilesShareFrozenClimateAndMoistureField()
    {
        var xml = new CelestialClimateXml { meanTemperatureCelsius = 20, lapseCelsiusPerMeter = 0, rainfallMillimeters = 1000, humidity = .4,
            latitudeCurve = new() { new() { latitudeDegrees = 0 }, new() { latitudeDegrees = 90 } } };
        using var f = new CelestialNativeWorldTestScope();
        var biome = new BiomeDef { defName = "PD_ClimateBiome" }; f.AddDefs(biome);
        var surface = new CelestialBiomeProfileDef { defName = "PD_ClimateSurface", mode = CelestialBiomeGenerationMode.Parameterized,
            terrain = new() { biomeRules = new() { new() { key = "fallback", biome = biome, fallback = true } } } };
        var body = new CelestialBodyDef { defName = "PD_ClimateBody", comps = new() {
            new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 1500000 } },
            new CompProperties_Surface { key = "surface", subdivisions = new() { value = 0 } },
            new CompProperties_BiomeGeneration { key = "biomes", profile = surface, useEnvironmentClimate = true },
            new CompProperties_ExistingEnvironment { key = "env", profile = new() { defName = "PD_Climate", primaryLightReference = "sun", climate = xml } } } }.ToTemplate().Value;
        var star = new CelestialBodyDef { defName = "PD_ClimateSun", comps = new() { new CompProperties_SphericalBody { key = "shape" }, new CompProperties_LightSource { key = "light" } } }.ToTemplate().Value;
        var planetId = CelestialInstanceId.Parse("climate:planet"); var sunId = CelestialInstanceId.Parse("climate:sun");
        CelestialSpawnItem Spawn(CelestialInstanceId id, CelestialBodyTemplate template) => new(id, id.Value, 0, id.Value, template, template.Resolve(new(43, id.Value)).Value,
            initializationReferences: new Dictionary<string, CelestialInstanceId> { ["sun"] = sunId });
        var bridge = new PioneerFramework.TestSupport.Celestial.DeterministicTestPhysicsBridge(Guid.NewGuid()); bridge.SetFrame(0, new Dictionary<CelestialInstanceId, CelestialMotionState>());
        var runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, new EmptyResources(), 43, _ => throw new Exception("No XML"));
        var step = new WorldGenStepDef { defName = "PD_ClimateStep", worldGenStep = new CelestialWorldGenStep() };
        var definition = new PlanetLayerDef { defName = "PF_CelestialSurface", layerType = typeof(CelestialSurfaceLayer), tileType = typeof(SurfaceTile), worldDrawLayers = new() };
        AccessTools.Field(typeof(PlanetLayerDef), "worldGenSteps").SetValue(definition, new List<WorldGenStepDef> { step }); f.AddDefs(definition); f.AddDefs(step);
        var surfaceService = new CelestialSurfaceService(runtime, definition);
        runtime.CreateBatch(new(new("climate", runtime.WorldSessionId, 0), new("climate", 43, null, new[] { Spawn(sunId, star), Spawn(planetId, body) })));
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        Assert.True(f.Game.GetComponent<CelestialBodyManager>().AttachCelestialRuntime(runtime).Success);
        var capability = Assert.Single(runtime.GetCapabilities<ICelestialSurfaceEnvironmentCapability>(planetId));
        var before = capability.SampleSurface(new(0, 15, 30, 0)); Assert.Empty(f.Grid.PlanetLayers);
        Assert.NotEqual(1000, before.RainfallMillimeters); Assert.Null(before.PressurePascals);
        var requested = runtime.RequestSurface(new(new("climate-layer", runtime.WorldSessionId, runtime.Catalog.Revision), planetId));
        Assert.True(requested.Operation.Status == CelestialOperationStatus.Applied, string.Join(",", requested.Operation.Errors));
        var layer = (CelestialSurfaceLayer)f.Grid.PlanetLayers[requested.Handle.LayerId];
        Assert.Equal(layer.Header.GenerationSeed, Assert.Single(runtime.GetCapabilities<ICelestialBiomeGenerationCapability>(planetId)).GetProfile().Terrain.FieldSeed);
        foreach (var tile in layer.Tiles)
        {
            var coords = layer.LongLatOf(tile.tile.tileId);
            var query = new CelestialSurfaceEnvironmentQuery(0, coords.x, coords.y, tile.elevation, biome.defName);
            Assert.True(CelestialEnvironmentBridge.TrySample(layer, query, out var sample));
            Assert.Equal(capability.SampleSurface(query), sample);
            Assert.InRange(Math.Abs(tile.rainfall - sample.RainfallMillimeters), 0, .001);
            Assert.InRange(Math.Abs(tile.swampiness - sample.Humidity), 0, .000001);
            Assert.Equal(20, tile.temperature); Assert.Equal(20, sample.TemperatureCelsius);
            Assert.Null(sample.PressurePascals); Assert.Null(sample.OxygenPartialPressurePascals);
        }
    }
    private sealed class EmptyResources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
    [Fact]
    public void FrozenTerrainRejectsAContradictoryOrMissingClimateSource()
    {
        CelestialClimateConfiguration Climate(double temperature) => new CelestialClimateXml { meanTemperatureCelsius = temperature }.Resolve();
        var climate = Climate(10);
        CelestialGenerationProfile Profile(CelestialClimateConfiguration source) => new("PD_Binding", CelestialBiomeGenerationMode.Parameterized, null, null, null, 75, .5, 1, 1, 1,
            0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), new(climate: source, biomeRules: new[] { new CelestialPlanetBiomeRule("fallback", "plain", fallback: true) }));
        var properties = new BiomeGenerationResolvedProperties("biomes", Profile(climate));
        var validator = Assert.IsAssignableFrom<ICelestialResolvedConfigurationValidator>(properties);
        ExistingEnvironmentResolvedProperties Environment(CelestialClimateConfiguration source) => new("env", new("PD_Env", 60000, "sun", Array.Empty<string>(), source));
        Assert.False(validator.Validate(new ICelestialResolvedProperties[] { properties, Environment(Climate(20)) }).Success);
        Assert.False(validator.Validate(new ICelestialResolvedProperties[] { properties }).Success);
        Assert.True(validator.Validate(new ICelestialResolvedProperties[] { properties, Environment(climate) }).Success);
        var unbound = new BiomeGenerationResolvedProperties("biomes", Profile(null));
        Assert.False(Assert.IsAssignableFrom<ICelestialResolvedConfigurationValidator>(unbound).Validate(new ICelestialResolvedProperties[] { unbound, Environment(climate) }).Success);
    }
    [Fact]
    public void ParameterizedNativeFloatAndHydrologyDomainsRejectWholeUnsafeConfigurations()
    {
        var biome = new BiomeDef { defName = "plain" };
        CelestialBiomeProfileDef Profile() => new() { defName = "PD_Domain", mode = CelestialBiomeGenerationMode.Parameterized,
            terrain = new() { biomeRules = new() { new() { key = "fallback", biome = biome, fallback = true } } } };
        var unsafeRain = Profile(); unsafeRain.rainfall = float.MaxValue;
        Assert.False(unsafeRain.ToProfile().Success);
        var unsafeRiver = Profile(); unsafeRiver.temperature = -273; unsafeRiver.terrain.allowRivers = true;
        Assert.False(unsafeRiver.ToProfile().Success);
        Assert.Throws<ArgumentException>(() => new CelestialClimateConfiguration(CelestialClimateMode.Authored, 20,
            new[] { new CelestialClimatePoint(0, 0), new CelestialClimatePoint(90, 0) }, 0, 0, 0, 3600000, 0, 0, 1, 1, double.MaxValue, .5));
        var invalidPriority = Profile(); invalidPriority.terrain.biomeRules[0].parameters.Add(new() { key = "priority", value = new() { min = 0, max = .5 } });
        Assert.False(invalidPriority.ToProfile().Success);
        var rangedPriority = Profile(); rangedPriority.terrain.biomeRules[0].parameters.Add(new() { key = "priority", value = new() { min = 2, max = 5 } });
        Assert.True(rangedPriority.ToProfile(new CelestialGenerationContext(1, "pd", "biomes")).Success);
    }
    [Fact]
    public void CoupledClimateAndAltitudeRejectUnsafeWholeIntervalsEvenWhenSomeSeedsWouldFit()
    {
        var climate = new CelestialClimateXml { meanTemperatureCelsius = 20, lapseCelsiusPerMeter = 0,
            latitudeCurve = new() { new() { latitudeDegrees = 0 }, new() { latitudeDegrees = 90 } },
            parameters = new() { new() { key = "rainfallMillimeters", value = new() { min = 600000, max = 700000 } } } };
        var terrain = new CelestialPlanetTerrainXml { elevationAmplitudeMeters = 0,
            biomeRules = new() { new() { key = "fallback", biome = new BiomeDef { defName = "plain" }, fallback = true } } };
        CelestialResult<CelestialBodyTemplate> Template() => new CelestialBodyDef { defName = "PD_CoupledDomain", comps = new() {
            new CompProperties_SphericalBody { key = "shape" }, new CompProperties_Surface { key = "surface" },
            new CompProperties_ExistingEnvironment { key = "env", profile = new() { defName = "PD_CoupledClimate", climate = climate } },
            new CompProperties_BiomeGeneration { key = "biomes", profile = new() { defName = "PD_CoupledTerrain", mode = CelestialBiomeGenerationMode.Parameterized, terrain = terrain }, useEnvironmentClimate = true } } }.ToTemplate();
        Assert.False(Template().Success);
        climate.parameters.Clear(); climate.lapseCelsiusPerMeter = .01; terrain.allowRivers = true;
        terrain.parameters.Add(new() { key = "elevationOffsetMeters", value = new() { min = 0, max = 20000 } });
        var template = Template().Value;
        Assert.All(Enumerable.Range(1, 32), seed => Assert.False(template.Resolve(new((ulong)seed, "planet")).Success));
        terrain.parameters.Clear();
        template = Template().Value;
        var overrides = new[] { new CelestialParameterOverride("env", "climate.rainfallMillimeters", Min: 600000, Max: 700000) };
        Assert.All(Enumerable.Range(1, 32), seed => Assert.False(template.Resolve(new((ulong)seed, "planet", parameterOverrides: overrides)).Success));
    }
    [Fact]
    public void FallbackRejectsAConditionalRangeEvenWhenItsLowerEndpointIsTheDefault()
    {
        var profile = new CelestialBiomeProfileDef { defName = "PD_ConditionalFallback", mode = CelestialBiomeGenerationMode.Parameterized,
            terrain = new() { biomeRules = new() { new() { key = "fallback", biome = new BiomeDef { defName = "plain" }, fallback = true,
                parameters = new() { new() { key = "minimumTemperatureCelsius", value = new() { min = -273.15, max = -273.14 } } } } } } };
        Assert.False(profile.ToProfile().Success);
    }
}
