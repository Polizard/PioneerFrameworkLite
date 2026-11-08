using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests;

public class LocalMapSurfaceIsolationTests(ITestOutputHelper output)
{
    [Fact]
    public void SuccessfulAndFailedLocalSessionsLeaveAnotherWorldsSurfaceIdentical()
    {
        var expected = GenerateSurface(runLocalMaps: false);
        Assert.Contains(expected, tile => tile.Biome == "Ocean" && tile.Water);
        Assert.Contains(expected, tile => tile.Biome == "Lake" && tile.Water);
        Assert.Contains(expected, tile => tile.Caves && tile.Mutators == "PFL_IsolationCaves");
        Assert.Contains(expected, tile => tile.NativeRivers.Length > 0 && tile.Rivers.Length > 0);

        Assert.Equal(expected, GenerateSurface(runLocalMaps: true));
        output.WriteLine($"Compared {expected.Length} tiles. Ocean {expected.Count(t => t.Biome == "Ocean")}, lake {expected.Count(t => t.Biome == "Lake")}, caves {expected.Count(t => t.Caves)}, native river edges on {expected.Count(t => t.NativeRivers.Length > 0)} tiles, exposed rivers on {expected.Count(t => t.Rivers.Length > 0)}.");
    }

    private static void RunLocalSession(CelestialPlanetMapDiversityTests.NativeFixture fixture, CelestialSurfaceLayer layer, int tileId, bool fail)
    {
        var map = fixture.Map(layer, tileId);
        var source = new MapSourceContext(layer.Header.OwnerId.Value, 9, tileId, map.Size.x, map.Size.z, fixture.Biome.defName);
        var inputs = new MapGenerationStepSpec("session-inputs", LocalMapBuiltinConfigurations.InitialInputs, 1, 1,
            MapGenerationStage.Fields, MapGenerationDomain.Natural,
            LocalMapBuiltinConfigurations.Encode(new InitialMapInputs(soilFertility: 3, allowCaves: false)));
        var frozen = new FrozenLocalMapContext(source, new(new LocalMapRecipe("isolation", new[] { inputs })));
        var session = new NativeLocalMapSession(frozen, MapGenerationPurpose.Encounter, map.Parent);
        var probe = new ObservingGenStep(fail);
        var native = new GenStepDef { defName = "PFL_IsolationObserver", genStep = probe, order = 500 };
        var patch = new Harmony("PFL.Tests.IsolationMapNoise");
        patch.Patch(AccessTools.Method(typeof(RockNoises), nameof(RockNoises.Init)), prefix: new(typeof(LocalMapSurfaceIsolationTests), nameof(SkipNoise)));
        var previous = MapGenerator.mapBeingGenerated;
        session.Activate();
        MapGenerator.mapBeingGenerated = map;
        try
        {
            if (fail)
            {
                Assert.Contains("isolation-failure", Assert.Throws<InvalidOperationException>(() => session.Generate(new[] { new GenStepWithParams(native, default) }, map)).Message);
                Assert.Equal(MapGenerationState.Failed, session.Executor.Report.State);
            }
            else
            {
                session.Generate(new[] { new GenStepWithParams(native, default) }, map);
                session.PostInitialize(map);
                Assert.Equal(MapGenerationState.Ready, session.Executor.Report.State);
            }
            Assert.True(probe.Observed);
            Assert.True(CelestialMapGeneration.TryGetDiversity(map.Tile, out var diversity));
            Assert.True(diversity.AllowCaves);
            Assert.Equal(2, diversity.SoilFertility);
        }
        finally
        {
            session.Release();
            MapGenerator.mapBeingGenerated = previous;
            NativeLocalMapHooks.ClearWorkingData();
            patch.UnpatchAll(patch.Id);
        }
        Assert.True(CelestialPlanetMapHooks.Caves(map));
        Assert.False(NativeLocalMapHooks.IsActive);
    }

    private static TileSnapshot[] GenerateSurface(bool runLocalMaps)
    {
        using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
        var sourceLayer = fixture.Layer("isolation-local", 9, new(allowCaves: true, soilFertility: 2));
        if (runLocalMaps)
        {
            RunLocalSession(fixture, sourceLayer, 0, false);
            RunLocalSession(fixture, sourceLayer, 1, true);
        }

        var ocean = new BiomeDef { defName = "Ocean" };
        var lake = new BiomeDef { defName = "Lake" };
        fixture.Native.AddDefs(ocean, lake);
        fixture.Native.AddDefs(new RiverDef { defName = "PFL_IsolationRiver", spawnFlowThreshold = 1, spawnChance = 1 });
        fixture.Native.AddDefs(new WorldGenStepDef { defName = "Rivers", worldGenStep = new WorldGenStep_Rivers(), order = 10 },
            new WorldGenStepDef { defName = "Lakes", worldGenStep = new WorldGenStep_Lakes(), order = 20 });
        var caves = new TileMutatorDef { defName = "PFL_IsolationCaves", categories = new() { "Caves" } };
        AccessTools.Field(typeof(TileMutatorDef), "workerClass").SetValue(caves, typeof(TileMutatorWorker_Caves));
        fixture.Native.AddDefs(caves);
        var previousOcean = BiomeDefOf.Ocean;
        var previousLake = BiomeDefOf.Lake;
        BiomeDefOf.Ocean = ocean;
        BiomeDefOf.Lake = lake;
        var patch = new Harmony("PFL.Tests.IsolationRiverTemperature");
        patch.Patch(AccessTools.Method(typeof(GenTemperature), nameof(GenTemperature.GetTemperatureFromSeasonAtTile)),
            prefix: new(typeof(LocalMapSurfaceIsolationTests), nameof(TemperatureInput)));
        try
        {
            fixture.Native.World.pathGrid = new WorldPathGrid();
            fixture.Native.World.reachability = new WorldReachability();
            var terrain = new CelestialPlanetTerrainConfiguration(continentScale: 8, elevationAmplitudeMeters: 200, oceanCoverage: .45,
                allowRivers: true, allowLakes: true, biomeRules: new[] {
                    new CelestialPlanetBiomeRule("land", fixture.Biome.defName, fallback: true, hilliness: (int)Hilliness.Flat) },
                landmarks: new[] { new CelestialWeightedReference(caves.defName) }, fieldSeed: 23);
            var profile = new CelestialGenerationProfile("PFL_IsolationSurface", CelestialBiomeGenerationMode.Parameterized, 20, 1000, .5f,
                new[] { "Rivers", "Lakes" }, Array.Empty<string>(), terrain);
            var props = new BiomeGenerationResolvedProperties("biomes", profile);
            var map = new MapGenerationResolvedProperties("maps", new CelestialMapProfile("PFL_IsolationCompatibility", new(allowCaves: true),
                new LocalMapGenerationSpec(new LocalMapRecipe("PFL_IsolationLocal", Array.Empty<MapGenerationStepSpec>()))));
            var configuration = CelestialGenerationConfiguration.Encode(new[] {
                new CelestialCompSaveRecord(props.Key, props.HandlerId, props.Version, 1, props.Serialize().Span, ReadOnlySpan<byte>.Empty),
                new CelestialCompSaveRecord(map.Key, map.HandlerId, map.Version, 1, map.Serialize().Span, ReadOnlySpan<byte>.Empty) });
            var header = new CelestialSurfaceHeader(CelestialInstanceId.Parse("isolation:comparison"), -1, 100000, 30, 2, 37, configuration);
            var surface = RuntimePlanetLayerService.RegisterCelestialInstance(fixture.Native.Grid, fixture.Definition, header);
            new CelestialWorldGenStep().GenerateFresh("isolation-seed", surface);
            Assert.Equal(CelestialSurfaceState.Ready, surface.Header.State);
            return surface.Tiles.Cast<SurfaceTile>().Select(tile => new TileSnapshot(tile.PrimaryBiome.defName, tile.elevation,
                tile.hilliness, tile.temperature, tile.rainfall, tile.swampiness, tile.WaterCovered,
                fixture.Native.World.HasCaves(tile.tile), string.Join(",", tile.Mutators.Select(m => m.defName)),
                Edges(tile.potentialRivers), Edges(tile.Rivers))).ToArray();
        }
        finally
        {
            patch.UnpatchAll(patch.Id);
            BiomeDefOf.Ocean = previousOcean;
            BiomeDefOf.Lake = previousLake;
        }
    }

    private static string Edges(IEnumerable<SurfaceTile.RiverLink> links) => links == null ? "" :
        string.Join(",", links.OrderBy(link => link.neighbor.tileId).Select(link => $"{link.neighbor.tileId}/{link.river.defName}"));

    private sealed record TileSnapshot(string Biome, float Elevation, Hilliness Hilliness, float Temperature, float Rain,
        float Swamp, bool Water, bool Caves, string Mutators, string NativeRivers, string Rivers);

    private sealed class ObservingGenStep(bool fail) : GenStep
    {
        public override int SeedPart => 542818;
        public bool Observed { get; private set; }
        public override void Generate(Map map, GenStepParams parms)
        {
            if (!NativeLocalMapSession.TryInputs(map, out var values, out _) || values.AllowCaves || values.SoilFertility != 3)
                throw new InvalidOperationException("Initial override missing from exact map session.");
            Observed = true;
            if (fail) throw new InvalidOperationException("isolation-failure");
        }
    }
    private static bool SkipNoise() => false;
    private static bool TemperatureInput(ref float __result) { __result = 20; return false; }
}
