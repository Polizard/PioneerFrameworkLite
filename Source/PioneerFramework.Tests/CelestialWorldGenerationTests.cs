using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PioneerFramework.Celestial;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialWorldGenerationTests
{
    [Fact]
    public void ParameterizedProfileRunsExplicitGlobalStepsOnceOnTheSelectedRootOnly()
    {
        using var fixture = new CelestialWorldBootstrapTests.Fixture();
        var samples = new List<(string Name, int Value, bool Root)>();
        var step = new WorldGenStepDef { defName = "PF_ProbeLatitudeRootStep", order = 3, worldGenStep = new RecordingStep("global", samples, fixture.Primary, false) };
        fixture.Native.AddDefs(step);
        fixture.Profile.rootOnlySteps = new() { step };
        using var scope = CelestialWorldBootstrap.Begin(fixture.System, 42, CelestialWorldBootstrapTests.Fixture.Connect).Value;
        fixture.Native.Grid.GenerateFreshWorld();
        var root = (CelestialSurfaceLayer)fixture.Native.Grid.Surface;
        WorldGenerator.GeneratePlanetLayer(root, "latitude-root", 1);
        Assert.True(scope.Complete().Success);
        var sample = Assert.Single(samples);
        Assert.True(sample.Root);
        Rand.PushState(Gen.HashCombineInt(root.Header.GenerationSeed, step.worldGenStep.SeedPart));
        try { Assert.Equal(Rand.Int, sample.Value); }
        finally { Rand.PopState(); }
        var other = scope.Manifest.Items.First(item => item.EntryKey == "optional").Id;
        var extra = scope.Runtime.RequestSurface(new(new("latitude-extra", scope.Runtime.WorldSessionId, scope.Runtime.Catalog.Revision), other));
        Assert.Equal(CelestialOperationStatus.Applied, extra.Operation.Status);
        Assert.Single(samples);
    }

    [Fact]
    public void ParameterizedNativeStepsRunOnceAndOnlyFromWorldGenSteps()
    {
        using var fixture = new CelestialWorldBootstrapTests.Fixture();
        var samples = new List<(string Name, int Value, bool Root)>();
        var rivers = new WorldGenStepDef { defName = "Rivers", order = 1, worldGenStep = new RecordingStep("rivers", samples, fixture.Primary, false) };
        var lakes = new WorldGenStepDef { defName = "Lakes", order = 2, worldGenStep = new RecordingStep("lakes", samples, fixture.Primary, false) };
        var mutators = new WorldGenStepDef { defName = "Mutators", order = 3, worldGenStep = new RecordingStep("mutators", samples, fixture.Primary, false) };
        fixture.Profile.worldGenSteps = new List<WorldGenStepDef> { rivers, lakes, mutators };
        fixture.Profile.terrain.allowRivers = true; fixture.Profile.terrain.allowLakes = true;
        fixture.Native.AddDefs(rivers, lakes, mutators);
        riversImplicitCalls = lakesImplicitCalls = 0;
        var harmony = new Harmony("PFL.Tests.ParameterizedSingleNativeChannel");
        try
        {
            harmony.Patch(NativeGenerateFresh(typeof(WorldGenStep_Rivers)), prefix: new(typeof(CelestialWorldGenerationTests), nameof(CountImplicitRiver)));
            harmony.Patch(NativeGenerateFresh(typeof(WorldGenStep_Lakes)), prefix: new(typeof(CelestialWorldGenerationTests), nameof(CountImplicitLake)));
            using var scope = CelestialWorldBootstrap.Begin(fixture.System, 42, CelestialWorldBootstrapTests.Fixture.Connect).Value;
            fixture.Native.Grid.GenerateFreshWorld();
            var root = (CelestialSurfaceLayer)fixture.Native.Grid.Surface;
            WorldGenerator.GeneratePlanetLayer(root, "single-native-channel", 1);
            Assert.True(scope.Complete().Success);
            // 唯一通道：隐式 new WorldGenStep_Rivers()/Lakes() 已删除，只能由 worldGenSteps 触发录制步骤。
            Assert.Equal(0, riversImplicitCalls);
            Assert.Equal(0, lakesImplicitCalls);
            Assert.Equal(3, samples.Count);
            Assert.Single(samples, sample => sample.Name == "rivers");
            Assert.Single(samples, sample => sample.Name == "lakes");
            Assert.Single(samples, sample => sample.Name == "mutators");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }

    [Fact]
    public void ParameterizedProfileRejectsNativeTerrainAndHydrologyStepMismatch()
    {
        CelestialPlanetTerrainConfiguration Terrain(bool rivers = false, bool lakes = false) => new(allowRivers: rivers, allowLakes: lakes,
            biomeRules: new[] { new CelestialPlanetBiomeRule("fallback", "plain", fallback: true) });
        CelestialGenerationProfile Profile(string[] steps, bool rivers = false, bool lakes = false) => new("steps",
            CelestialBiomeGenerationMode.Parameterized, 0, 0, 0, steps, Array.Empty<string>(), Terrain(rivers, lakes));
        var nativeTerrain = Assert.Throws<ArgumentException>(() => Profile(new[] { "Terrain" }));
        Assert.Contains("ParameterizedNativeTerrainForbidden", nativeTerrain.Message);
        Assert.Contains("RiverStepMismatch", Assert.Throws<ArgumentException>(() => Profile(Array.Empty<string>(), rivers: true)).Message);
        Assert.Contains("RiverStepMismatch", Assert.Throws<ArgumentException>(() => Profile(new[] { "Rivers" })).Message);
        Assert.Contains("LakeStepMismatch", Assert.Throws<ArgumentException>(() => Profile(Array.Empty<string>(), lakes: true)).Message);
        Assert.Contains("LakeStepMismatch", Assert.Throws<ArgumentException>(() => Profile(new[] { "Lakes" })).Message);
        Assert.NotNull(Profile(new[] { "Rivers", "Lakes" }, rivers: true, lakes: true));
    }

    private static int riversImplicitCalls, lakesImplicitCalls;
    private static void CountImplicitRiver(WorldGenStep __instance) { if (__instance is WorldGenStep_Rivers) riversImplicitCalls++; }
    private static void CountImplicitLake(WorldGenStep __instance) { if (__instance is WorldGenStep_Lakes) lakesImplicitCalls++; }
    private static System.Reflection.MethodInfo NativeGenerateFresh(Type stepType) =>
        AccessTools.Method(stepType, nameof(WorldGenStep.GenerateFresh))
        ?? throw new InvalidOperationException("Native hydrology step GenerateFresh not found: " + stepType.Name);

    [Theory]
    [InlineData(75, .9, "PF_ProbePolar", Hilliness.Flat)]
    [InlineData(-75, .1, "PF_ProbePolar", Hilliness.Flat)]
    [InlineData(74.9, .1, "PF_ProbePrimary", Hilliness.SmallHills)]
    [InlineData(-74.9, .9, "PF_ProbeSecondary", Hilliness.Flat)]
    public void ProfileKeepsPolarBoundaryWeightedCandidatesAndPerBiomeHilliness(double latitude, double pick, string biome, Hilliness hills)
    {
        using var fixture = new CelestialWorldBootstrapTests.Fixture();
        var selection = CelestialPlanetTerrain.SelectBiome(fixture.Profile.ToProfile().Value.Terrain, 0, 0, 0, latitude, .5, pick);
        Assert.Equal(biome, selection.BiomeId);
        Assert.Equal((int)hills, selection.Hilliness);
    }

    [Fact]
    public void NativeGeneratedTilesUseInstanceProfileWithoutLookingForMoonDefinitionName()
    {
        using var fixture = new CelestialWorldBootstrapTests.Fixture();
        using var scope = CelestialWorldBootstrap.Begin(fixture.System, 42, CelestialWorldBootstrapTests.Fixture.Connect).Value;
        fixture.Native.Grid.GenerateFreshWorld();
        var root = (CelestialSurfaceLayer)fixture.Native.Grid.Surface;
        WorldGenerator.GeneratePlanetLayer(root, "profile-generation", 1);
        Assert.Equal("PF_ProbeRootSurface", root.Def.defName);
        Assert.All(root.Tiles, tile => {
            double latitude = root.LongLatOf(tile.tile).y;
            if (Math.Abs(latitude) >= 75) Assert.Same(fixture.Polar, tile.PrimaryBiome);
            else Assert.Contains(tile.PrimaryBiome, new[] { fixture.Primary, fixture.Secondary });
            Assert.Equal(tile.PrimaryBiome == fixture.Primary ? Hilliness.SmallHills : Hilliness.Flat, tile.hilliness);
            Assert.Equal(0f, tile.rainfall);
        });
        Assert.Equal(root.TilesCount, root.Tiles.Count);
        Assert.True(scope.Complete().Success);
        var optional = scope.Manifest.Items.First(item => item.EntryKey == "optional").Id;
        var operation = scope.Runtime.RequestSurface(new(new("optional-surface", scope.Runtime.WorldSessionId, scope.Runtime.CaptureCatalog().Revision), optional));
        Assert.Equal(CelestialOperationStatus.Applied, operation.Operation.Status);
        var second = (CelestialSurfaceLayer)fixture.Native.Grid.PlanetLayers[operation.Handle!.LayerId];
        Assert.NotEqual(root.Header.OwnerId, second.Header.OwnerId);
        Assert.All(second.Tiles, tile => Assert.Contains(tile.PrimaryBiome, new[] { fixture.Polar, fixture.Primary, fixture.Secondary }));
    }

    [Fact]
    public void ProductionSphereUsesExistingBodyFixedAxesAndDoesNotReadNativeDisplayRadius()
    {
        var properties = new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { min = 1000000, max = 1800000 } };
        var resolved = properties.Resolve(new CelestialGenerationContext(1, "moon[0]").ForComp("shape")).Value;
        var geometry = Assert.IsType<SphericalBodyResolvedProperties>(resolved);
        Assert.InRange(geometry.RadiusMeters, 1000000, 1800000);
        var point = geometry.FixedPosition(0, 0);
        Assert.Equal(0, point.x);
        Assert.Equal(0, point.y);
        Assert.Equal(-geometry.RadiusMeters, point.z);
    }

    [Fact]
    public void BuiltinSurfaceRejectsSubdivisionRangesBeyondTheSupportedNativeResolution()
    {
        Assert.Equal(WorldGrid.SubdivisionsCount, SurfaceResolvedProperties.MaxBuiltinSubdivisions);
        var properties = new CompProperties_Surface { key = "surface", subdivisions = new() { min = 1, max = WorldGrid.SubdivisionsCount + 1 } };
        Assert.Contains("InvalidSurfaceSubdivisions", properties.ConfigErrors());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SurfaceResolvedProperties("surface", CelestialSurfaceGenerationMode.OnDemand, int.MaxValue, 50000));
    }

    [Fact]
    public void NativeProfileRetainsOfficialStepSeedsAndRunsGlobalStepsOnlyForTheRoot()
    {
        var fillBiome = new BiomeDef { defName = "PF_ProbeFillBiome", shortHash = 63004 };
        var samples = new List<(string Name, int Value, bool Root)>();
        var first = new WorldGenStepDef { defName = "PF_ProbeNativeFirst", order = 1, worldGenStep = new RecordingStep("first", samples, fillBiome, true) };
        var second = new WorldGenStepDef { defName = "PF_ProbeNativeSecond", order = 2, worldGenStep = new RecordingStep("second", samples, fillBiome, false) };
        var rootOnly = new WorldGenStepDef { defName = "PF_ProbeNativeRootOnly", order = 3, worldGenStep = new RecordingStep("root", samples, fillBiome, false) };
        using var fixture = new CelestialWorldBootstrapTests.Fixture(biomeMode: CelestialBiomeGenerationMode.VanillaSurface,
            worldGenSteps: new[] { first, second }, rootOnlySteps: new[] { rootOnly });
        var started = CelestialWorldBootstrap.Begin(fixture.System, 42, CelestialWorldBootstrapTests.Fixture.Connect);
        Assert.True(started.Success, string.Join("|", started.Errors.Select(error => error.Code + ":" + error.Detail)));
        using var scope = started.Value;
        fixture.Native.Grid.GenerateFreshWorld();
        var root = (CelestialSurfaceLayer)fixture.Native.Grid.Surface;
        WorldGenerator.GeneratePlanetLayer(root, "native-profile", 1);
        Assert.True(scope.Complete().Success);
        var nativeSeedPart = AccessTools.MethodDelegate<Func<List<WorldGenStepDef>, int, int>>(AccessTools.Method(typeof(WorldGenerator), "GetSeedPart"));
        var steps = new List<WorldGenStepDef> { first, second, rootOnly };
        for (int index = 0; index < 3; index++)
        {
            Rand.PushState(Gen.HashCombineInt(root.Header.GenerationSeed, nativeSeedPart(steps, index)));
            try { Assert.Equal(Rand.Int, samples[index].Value); }
            finally { Rand.PopState(); }
            Assert.True(samples[index].Root);
        }
        var other = scope.Manifest.Items.First(item => item.EntryKey == "optional").Id;
        var requested = scope.Runtime.RequestSurface(new(new("optional-native", scope.Runtime.WorldSessionId, scope.Runtime.CaptureCatalog().Revision), other));
        Assert.Equal(CelestialOperationStatus.Applied, requested.Operation.Status);
        Assert.Equal(5, samples.Count);
        Assert.False(samples[3].Root);
        Assert.False(samples[4].Root);
        Assert.Single(samples.Where(sample => sample.Name == "root"));
    }

    [Fact]
    public void FailedNativeStepCannotPublishAReadyHeaderEvenWhenTilesAlreadyExist()
    {
        var fillBiome = new BiomeDef { defName = "PF_ProbeFillBiome", shortHash = 63004 };
        var samples = new List<(string Name, int Value, bool Root)>();
        var first = new WorldGenStepDef { defName = "PF_ProbeFill", order = 1, worldGenStep = new RecordingStep("fill", samples, fillBiome, true) };
        var second = new WorldGenStepDef { defName = "PF_ProbeFail", order = 2, worldGenStep = new ThrowingStep() };
        using var fixture = new CelestialWorldBootstrapTests.Fixture(biomeMode: CelestialBiomeGenerationMode.VanillaSurface,
            worldGenSteps: new[] { first, second });
        var started = CelestialWorldBootstrap.Begin(fixture.System, 42, CelestialWorldBootstrapTests.Fixture.Connect);
        Assert.True(started.Success, string.Join("|", started.Errors.Select(error => error.Code + ":" + error.Detail)));
        using var scope = started.Value;
        fixture.Native.Grid.GenerateFreshWorld();
        var root = (CelestialSurfaceLayer)fixture.Native.Grid.Surface;
        var generated = CelestialWorldGeneration.Generate(root, fixture.Profile.ToProfile().Value);
        Assert.False(generated.Success);
        Assert.Equal(root.TilesCount, root.Tiles.Count);
        Assert.Equal(CelestialSurfaceState.Failed, root.Header.State);
        Assert.False(scope.Complete().Success);
        Assert.Empty(scope.Runtime.CaptureCatalog().Bodies);
    }

    private sealed class RecordingStep(string name, List<(string Name, int Value, bool Root)> samples, BiomeDef biome, bool fill) : WorldGenStep
    {
        public override int SeedPart => 31;
        public override void GenerateFresh(string seed, PlanetLayer layer)
        {
            samples.Add((name, Rand.Int, layer.IsRootSurface));
            if (!fill) return;
            layer.Tiles.Clear();
            for (int index = 0; index < layer.TilesCount; index++) layer.Tiles.Add(new SurfaceTile(new PlanetTile(index, layer)) { PrimaryBiome = biome });
        }
    }
    private sealed class ThrowingStep : WorldGenStep
    {
        public override int SeedPart => 19;
        public override void GenerateFresh(string seed, PlanetLayer layer) => throw new InvalidOperationException("fixture native step failure");
    }
}
