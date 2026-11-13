using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialWorldBootstrapTests
{
    [Fact]
    public void UnmappedScenarioPartCannotClaimRootOwnershipByReusingItsDefinition()
    {
        using var fixture = new Fixture();
        using var scope = CelestialWorldBootstrap.Begin(fixture.System, 17, Fixture.Connect).Value;
        var root = (ScenPart_PlanetLayer)AccessTools.Field(typeof(Scenario), "surfaceLayer").GetValue(Find.Scenario);
        var extra = new ScenPart_PlanetLayer { layer = root.layer, settingsDef = root.settingsDef, tag = "unmapped" };
        // The native non-root branch uses precisely this settings overload without view overrides.
        // Call it directly: patching AllParts after native callers were JIT-compiled can miss an inlined getter.
        Assert.Null(CelestialSurfaceCreationScope.CurrentHeader);
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Native.Grid.RegisterPlanetLayer(extra.layer, extra.Settings));
        Assert.Contains("MissingCelestialConstructionSession", error.Message);
        Assert.Null(scope.ConstructedRootHeader);
        fixture.Native.Grid.GenerateFreshWorld();
        Assert.NotNull(scope.ConstructedRootHeader);
    }
    [Fact]
    public void TwoWorldsWithTheSameDefinitionAndLayerNumberKeepSeparateSessions()
    {
        using var fixture = new Fixture();
        using var first = CelestialWorldBootstrap.Begin(fixture.System, 17, Fixture.Connect).Value;
        fixture.Native.Grid.GenerateFreshWorld();
        var firstRoot = (CelestialSurfaceLayer)fixture.Native.Grid.Surface;
        using (var secondWorld = new CelestialNativeWorldTestScope())
        {
            secondWorld.SetRoot(fixture.RootDefinition);
            using var second = CelestialWorldBootstrap.Begin(fixture.System, 23, Fixture.Connect).Value;
            secondWorld.Grid.GenerateFreshWorld();
            var secondRoot = (CelestialSurfaceLayer)secondWorld.Grid.Surface;
            Assert.Equal(firstRoot.LayerID, secondRoot.LayerID);
            Assert.NotEqual(first.Runtime.WorldSessionId, second.Runtime.WorldSessionId);
            Assert.Same(second, CelestialWorldBootstrap.Current);
            Assert.Same(first, CelestialWorldGenSessionRegistry.Find(fixture.Native.Grid, fixture.Native.Game, fixture.Native.World));
        }
        Assert.Same(first, CelestialWorldBootstrap.Current);
        Assert.Null(CelestialSurfaceCreationScope.CurrentHeader);
    }
    [Fact]
    public void NativeFailureIsPreservedWhenWorldScopeCleanupAlsoFails()
    {
        using var fixture = new Fixture();
        var scope = CelestialWorldBootstrap.Begin(fixture.System, 17, Fixture.Connect).Value;
        var call = new CelestialWorldBootstrapHooks.Invocation(null, fixture.Native.Game);
        call.Owned.Add(scope);
        CelestialWorldGenSessionRegistry.Remove(scope);
        var failure = new InvalidOperationException("original native failure");
        var returned = CelestialWorldBootstrapHooks.FinalizeWorld(failure, call);
        Assert.Same(failure, returned);
        Assert.Single(failure.Data.Keys.Cast<object>());
        Assert.Null(CelestialWorldBootstrap.Current);
        Assert.False(fixture.Native.Game.GetComponent<CelestialBodyManager>().TryGetCelestialRuntime(out _));
    }
    [Fact]
    public void ExplicitWorldSessionCanBeResolvedOnTheNativeGenerationThread()
    {
        using var fixture = new Fixture();
        using var scope = CelestialWorldBootstrap.Begin(fixture.System, 17, Fixture.Connect).Value;
        CelestialWorldBootstrapScope observed = null; int workerId = 0;
        var worker = new System.Threading.Thread(() => { workerId = Environment.CurrentManagedThreadId; observed = CelestialWorldBootstrap.Current; });
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(1)));
        Assert.NotEqual(Environment.CurrentManagedThreadId, workerId); Assert.Same(scope, observed);
    }
    [Fact]
    public void RootDefinitionDoesNotDependOnAllPartsEnumerationOrder()
    {
        using var fixture = new Fixture();
        var patch = new Harmony("PFL.Celestial.RootOrderProbe");
        var root = (ScenPart_PlanetLayer)AccessTools.Field(typeof(Scenario), "surfaceLayer").GetValue(Find.Scenario);
        reorderedParts = new ScenPart[] { new ScenPart_PlanetLayer { layer = new PlanetLayerDef { layerType = typeof(SurfaceLayer) } }, root };
        patch.Patch(AccessTools.PropertyGetter(typeof(Scenario), "AllParts"), prefix: new HarmonyMethod(typeof(CelestialWorldBootstrapTests), nameof(ReorderedParts)));
        try
        {
            var started = CelestialWorldBootstrap.Begin(fixture.System, 17, Fixture.Connect);
            Assert.True(started.Success, string.Join(",", started.Errors));
            started.Value.Dispose();
        }
        finally { patch.UnpatchAll(patch.Id); reorderedParts = null; }
    }
    private static IEnumerable<ScenPart> reorderedParts;
    private static bool ReorderedParts(ref IEnumerable<ScenPart> __result) { __result = reorderedParts; return false; }
    [Fact]
    public void ManifestPrecedesNativeRootAndChosenMoonDoesNotFallBackToEarthOrCreateOnDemandLayers()
    {
        using var fixture = new Fixture();
        using var scope = CelestialWorldBootstrap.Begin(fixture.System, 17, Fixture.Connect).Value;
        fixture.RootDefinition.worldDrawLayers.Add(typeof(RootHeaderObserver));
        RootHeaderObserver.Owner = null;
        Assert.Empty(fixture.Native.Grid.PlanetLayers);
        Assert.Equal(3, scope.Manifest.Items.Count);
        Assert.Equal(scope.Manifest.RootSurfaceId, scope.Manifest.Items.Single(item => item.EntryKey == "chosen").Id);
        fixture.Native.Grid.GenerateFreshWorld();
        var root = Assert.IsType<CelestialSurfaceLayer>(fixture.Native.Grid.Surface);
        Assert.True(root.IsRootSurface);
        Assert.Equal(scope.Manifest.RootSurfaceId, root.Header.OwnerId);
        Assert.Equal(30f, root.Radius);
        Assert.Equal(1500000, root.Header.PhysicalRadiusMeters);
        Assert.Equal(scope.Manifest.RootSurfaceId, RootHeaderObserver.Owner);
        Assert.True(scope.BindRoot(root).Success);
        Assert.True(scope.BindRoot(root).Success);
        Assert.Equal(1, scope.RootBindingCount);
        WorldGenerator.GeneratePlanetLayer(root, "celestial-world-probe", 31);
        Assert.True(scope.Complete().Success);
        Assert.Equal(3, scope.Runtime.CaptureCatalog().Bodies.Count);
        Assert.Single(fixture.Native.Grid.PlanetLayers);
        Assert.Equal(CelestialSurfaceState.Ready, root.Header.State);
        Assert.Equal(CelestialPlanetTerrain.AlgorithmVersion, root.Header.TerrainAlgorithmVersion);
        var checkpoint = new CelestialSaveAdapter(scope.Runtime).CaptureCheckpoint();
        Assert.True(checkpoint.Success, string.Join(";", checkpoint.Errors.Select(error => error.Code)));
        Assert.Equal(CelestialPlanetTerrain.AlgorithmVersion, Assert.Single(checkpoint.Value.Surfaces).TerrainAlgorithmVersion);
        var decoded = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(checkpoint.Value).Value);
        Assert.True(decoded.Success);
        Assert.Equal(CelestialPlanetTerrain.AlgorithmVersion, Assert.Single(decoded.Value.Surfaces).TerrainAlgorithmVersion);
    }

    [Fact]
    public void FailureAndDisposalClearBootstrapContextForTheNextWorld()
    {
        using var fixture = new Fixture();
        var rejected = CelestialWorldBootstrap.Begin(fixture.System, 17, _ => CelestialResult.Reject<CelestialPhysicsConnection>(new CelestialError("ProviderUnavailable")));
        Assert.False(rejected.Success);
        Assert.Null(CelestialWorldBootstrap.Current);
        using (var incomplete = CelestialWorldBootstrap.Begin(fixture.System, 17, Fixture.Connect).Value)
        {
            Assert.NotNull(CelestialWorldBootstrap.Current);
            Assert.Equal("RootNotBound", Assert.Single(incomplete.Complete().Errors).Code);
        }
        Assert.Null(CelestialWorldBootstrap.Current);
        using var next = CelestialWorldBootstrap.Begin(fixture.System, 17, Fixture.Connect).Value;
        Assert.Same(next, CelestialWorldBootstrap.Current);
    }

    [Fact]
    public void GeneratedProfileIsFrozenBeforeRootConstruction()
    {
        using var fixture = new Fixture();
        using var scope = CelestialWorldBootstrap.Begin(fixture.System, 17, Fixture.Connect).Value;
        fixture.Profile.temperature = 123;
        fixture.Native.Grid.GenerateFreshWorld();
        var root = (CelestialSurfaceLayer)fixture.Native.Grid.Surface;
        var configuration = CelestialGenerationConfiguration.Decode(root.Header.GenerationConfiguration).Value;
        var biome = configuration.Single(record => record.HandlerId == BiomeGenerationResolvedProperties.Handler);
        Assert.Equal(0, BiomeGenerationResolvedProperties.Deserialize(biome.Key, biome.ConfigurationVersion, biome.ConfigurationPayload).Value.Profile.Temperature);
        WorldGenerator.GeneratePlanetLayer(root, "profile-freeze", 1);
        Assert.True(scope.Complete().Success);
        Assert.Contains(root.Tiles, tile => tile.PrimaryBiome == fixture.Primary || tile.PrimaryBiome == fixture.Secondary);
    }

    [Fact]
    public void AnotherNativeLayerCannotClaimTheReservedRootOwnerBeforeExplicitBinding()
    {
        using var fixture = new Fixture();
        using var scope = CelestialWorldBootstrap.Begin(fixture.System, 17, Fixture.Connect).Value;
        fixture.Native.Grid.GenerateFreshWorld();
        Assert.ThrowsAny<Exception>(() => fixture.Native.Grid.RegisterPlanetLayer(fixture.RootDefinition, radius: 100, subdivisions: 0));
        Assert.Single(fixture.Native.Grid.PlanetLayers);
    }

    [Fact]
    public void DisposingIncompleteBootstrapReleasesItsUncommittedComponentResources()
    {
        using var fixture = new Fixture();
        var resource = new ResourceFactory();
        var descriptor = new CelestialCompDescriptor("resource", "test.bootstrap.resource", 1);
        var added = new CelestialCompDefinition(descriptor, resource, _ => CelestialResult.Ok<ICelestialResolvedProperties>(new ResourceProperties()));
        var selected = fixture.System.Entries.Single(entry => entry.Key == "chosen");
        var body = CelestialBodyTemplate.Create(selected.Body.DefinitionId, selected.Body.Label, selected.Body.Category, selected.Body.Tags,
            selected.Body.Components.Append(added)).Value;
        var system = new CelestialSystemTemplate(fixture.System.DefinitionId, "chosen", fixture.System.Entries.Select(entry =>
            entry.Key == "chosen" ? new CelestialSystemEntry(entry.Key, body, entry.Count) : entry));
        using (var scope = CelestialWorldBootstrap.Begin(system, 17, Fixture.Connect).Value) Assert.True(resource.Created.Buffer.CanRead);
        Assert.False(resource.Created.Buffer.CanRead);
    }

    [Fact]
    public void ThrowingProviderFactoryReturnsAnErrorWithoutInstallingBootstrapState()
    {
        using var fixture = new Fixture();
        var result = CelestialWorldBootstrap.Begin(fixture.System, 17, _ => throw new InvalidOperationException("fixture provider initialization"));
        Assert.False(result.Success);
        Assert.Equal("ProviderInitializationFailed", Assert.Single(result.Errors).Code);
        Assert.Null(CelestialWorldBootstrap.Current);
    }

    public sealed class RootHeaderObserver : WorldDrawLayer
    {
        public static CelestialInstanceId? Owner;
        public RootHeaderObserver() => Owner = CelestialSurfaceLayer.InitializingHeader?.OwnerId;
    }
    private sealed record ResourceProperties : ICelestialResolvedProperties
    {
        public string Key => "resource";
        public string HandlerId => "test.bootstrap.resource";
        public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => ReadOnlyMemory<byte>.Empty;
    }
    private sealed class ResourceFactory : ICelestialCompFactory
    {
        public ResourceComp Created { get; private set; }
        public CelestialBodyComp Create(ICelestialResolvedProperties properties) => Created = new ResourceComp();
    }
    private sealed class ResourceComp() : CelestialBodyComp(new CelestialCompDescriptor("resource", "test.bootstrap.resource", 1))
    {
        public MemoryStream Buffer { get; } = new();
        protected override void OnRelease() => Buffer.Dispose();
    }

    internal sealed class Fixture : IDisposable
    {
        public CelestialNativeWorldTestScope Native { get; } = new();
        public BiomeDef Polar { get; } = new() { defName = "PF_ProbePolar", shortHash = 63001 };
        public BiomeDef Primary { get; } = new() { defName = "PF_ProbePrimary", shortHash = 63002 };
        public BiomeDef Secondary { get; } = new() { defName = "PF_ProbeSecondary", shortHash = 63003 };
        public CelestialBiomeProfileDef Profile { get; }
        public PlanetLayerDef RootDefinition { get; }
        public CelestialSystemTemplate System { get; }
        public Fixture(CelestialMapProfileDef mapProfile = null, Func<CelestialBodyTemplate, CelestialBodyTemplate> transform = null,
            CelestialBiomeGenerationMode biomeMode = CelestialBiomeGenerationMode.Parameterized,
            IReadOnlyList<WorldGenStepDef> worldGenSteps = null, IReadOnlyList<WorldGenStepDef> rootOnlySteps = null)
        {
            Native.AddDefs(Polar, Primary, Secondary);
            Profile = new CelestialBiomeProfileDef { defName = "PF_ProbeProfile", mode = biomeMode,
                worldGenSteps = worldGenSteps?.ToList() ?? new(),
                rootOnlySteps = rootOnlySteps?.ToList() ?? new(),
                terrain = biomeMode == CelestialBiomeGenerationMode.Parameterized ? new() { biomeRules = new() {
                    new() { key = "waterIce", biome = Polar, priority = 10, minimumAbsoluteLatitude = 75, hilliness = Hilliness.Flat },
                    new() { key = "basalt", biome = Primary, weight = .5, hilliness = Hilliness.SmallHills },
                    new() { key = "regolith", biome = Secondary, weight = .5, hilliness = Hilliness.Flat },
                    new() { key = "fallback", biome = Secondary, fallback = true, hilliness = Hilliness.Flat } } } : null };
            Native.AddDefs(Profile);
            if (worldGenSteps != null || rootOnlySteps != null)
                Native.AddDefs((worldGenSteps ?? Array.Empty<WorldGenStepDef>()).Concat(rootOnlySteps ?? Array.Empty<WorldGenStepDef>()).ToArray());
            var definition = new PlanetLayerDef { defName = "PF_ProbeRootSurface", layerType = typeof(CelestialSurfaceLayer),
                tileType = typeof(SurfaceTile), worldDrawLayers = new List<Type>() };
            RootDefinition = definition;
            var generation = new WorldGenStepDef { defName = "PF_ProbeCelestialTiles", worldGenStep = new CelestialWorldGenStep() };
            AccessTools.Field(typeof(PlanetLayerDef), "worldGenSteps").SetValue(definition, new List<WorldGenStepDef> { generation });
            var generic = new PlanetLayerDef { defName = "PF_CelestialSurface", layerType = typeof(CelestialSurfaceLayer),
                tileType = typeof(SurfaceTile), worldDrawLayers = new List<Type>() };
            AccessTools.Field(typeof(PlanetLayerDef), "worldGenSteps").SetValue(generic, new List<WorldGenStepDef> { generation });
            Native.AddDefs(definition, generic);
            Native.AddDefs(generation);
            Native.SetRoot(definition);
            var body = new CelestialBodyDef { defName = "PF_ProbeMoon", label = "chosen moon", category = "Moon", comps = new() {
                new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 1500000 } },
                new CompProperties_Surface { key = "surface", subdivisions = new() { value = 1 } },
                new CompProperties_BiomeGeneration { key = "biomes", profile = Profile },
                new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = DeterministicTestPhysicsBridge.Id, modeId = "replay" } } };
            if (mapProfile != null) body.comps.Add(new CompProperties_MapGeneration { key = "maps", profile = mapProfile });
            var template = body.ToTemplate().Value;
            if (transform != null) template = transform(template);
            System = new CelestialSystemTemplate("PF_ProbeSystem", "chosen", new[] {
                new CelestialSystemEntry("chosen", template, IntParameterSpec.Fixed(1)),
                new CelestialSystemEntry("optional", template, IntParameterSpec.Fixed(2)) });
        }
        public static CelestialResult<CelestialPhysicsConnection> Connect(CelestialPhysicsWorldRequest request)
        {
            var bridge = new DeterministicTestPhysicsBridge(request.WorldSessionId, request.InitialSeconds);
            bridge.SetFrame(request.InitialSeconds, request.Manifest.Items.ToDictionary(item => item.Id,
                _ => new CelestialMotionState(1, DVector3.zero, DVector3.zero, DQuaternion.identity, DVector3.zero)));
            return CelestialResult.Ok(new CelestialPhysicsConnection(bridge, bridge.CommittedStamp));
        }
        public void Dispose() => Native.Dispose();
    }
}
