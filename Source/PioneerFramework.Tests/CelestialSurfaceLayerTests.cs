using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialSurfaceLayerTests
{
    [Fact]
    public void WorkerRequestQueuesNativeWorkUntilTheGameHostUpdate()
    {
        using var context = new Context();
        var manager = new CelestialBodyManager(Current.Game); Current.Game.components.Add(manager);
        Assert.True(manager.AttachCelestialRuntime(context.Runtime).Success);
        CelestialSurfaceOperation pending = null;
        var worker = new System.Threading.Thread(() => pending = context.Request("worker-surface", context.First));
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(1)));
        Assert.Equal(CelestialOperationStatus.Pending, pending!.Operation.Status); Assert.Empty(context.Grid.PlanetLayers);
        manager.GameComponentUpdate();
        Assert.Equal(CelestialOperationStatus.Applied, context.Runtime.GetOperation("worker-surface").Status);
        Assert.Single(context.Grid.PlanetLayers);
    }
    [Fact]
    public void BuiltinHeaderRejectsAnOversizedMeshBeforeNativeInitialization()
    {
        Assert.Throws<InvalidDataException>(() => new CelestialSurfaceHeader(CelestialInstanceId.Parse("header"), 0, 10, 1,
            SurfaceResolvedProperties.MaxBuiltinSubdivisions + 1, 1, ReadOnlyMemory<byte>.Empty));
    }
    [Fact]
    public void NativeGeneratorRunsOutsidePublicationAndReadersReturnWithoutWaiting()
    {
        using var context = new Context();
        context.Generator.AfterGeneration = _ => {
            Assert.False(context.Runtime.IsPublicationHeld);
            var reader = System.Threading.Tasks.Task.Run(() => context.Runtime.Query(context.First));
            Assert.True(reader.Wait(TimeSpan.FromSeconds(1)));
            Assert.True(reader.Result.Success);
            Assert.True(context.Runtime.HostWork.IsBusy);
        };
        Assert.Equal(CelestialOperationStatus.Applied, context.Request("unlocked-native", context.First).Operation.Status);
        Assert.False(context.Runtime.HostWork.IsBusy);
    }
    [Fact]
    public void ChangedDescriptorDuringGenerationRejectsAndCleansOnlyTheNewLayer()
    {
        using var context = new Context();
        context.Generator.AfterGeneration = _ => {
            context.Runtime.Catalog.TryGetInstance(context.First, out var instance);
            instance.Lifecycle = CelestialLifecycle.Blocked;
        };
        var result = context.Request("stale-native", context.First);
        Assert.Equal(CelestialOperationStatus.Rejected, result.Operation.Status);
        Assert.Contains(result.Operation.Errors, error => error.Detail.Contains("HostWorkIdentityChanged", StringComparison.Ordinal));
        Assert.Empty(context.Grid.PlanetLayers);
    }
    [Fact]
    public void NativeRemovalDuringGenerationCannotPublishOrResurrectTheRemovedLayer()
    {
        using var context = new Context();
        context.Generator.AfterGeneration = layer => context.Grid.RemovePlanetLayer(layer);
        var result = context.Request("removed-native", context.First);
        Assert.Equal(CelestialOperationStatus.Rejected, result.Operation.Status);
        Assert.Empty(context.Grid.PlanetLayers);
        Assert.Empty(context.Surfaces.CaptureHeaders());
        Assert.Equal("SurfaceNotCreated", Assert.Single(context.Surfaces.Find(context.First).Errors).Code);
    }
    [Fact]
    public void SameTemplateCreatesSeparateOwnedNativeLayersAndRepeatedRequestsReuseOnlyTheirOwner()
    {
        using var context = new Context();
        Assert.Empty(context.Grid.PlanetLayers);
        Assert.True(context.Runtime.Query(context.First).Success);
        Assert.Empty(context.Grid.PlanetLayers);
        var first = context.Request("surface-a", context.First);
        var second = context.Request("surface-b", context.Second);
        Assert.Equal(CelestialOperationStatus.Applied, first.Operation.Status);
        Assert.Equal(CelestialOperationStatus.Applied, second.Operation.Status);
        Assert.NotEqual(first.Handle!.LayerId, second.Handle!.LayerId);
        var firstLayer = context.Layer(first.Handle.LayerId);
        var secondLayer = context.Layer(second.Handle.LayerId);
        Assert.Same(firstLayer.Def, secondLayer.Def);
        Assert.Equal(context.First, firstLayer.Header.OwnerId);
        Assert.Equal(context.Second, secondLayer.Header.OwnerId);
        Assert.Same(firstLayer, context.Layer(context.Request("surface-a-again", context.First).Handle!.LayerId));
        Assert.Equal(2, context.Grid.PlanetLayers.Count);
        Assert.True(firstLayer.TilesCount > 0);
        Assert.Equal(firstLayer.TilesCount, firstLayer.Tiles.Count);
    }

    [Fact]
    public void GenerationFailurePreservesRandAndCleansOnlyTheNewEmptyLayer()
    {
        using var context = new Context();
        var first = context.Request("surface-first", context.First);
        Assert.True(first.Operation.Status == CelestialOperationStatus.Applied, string.Join("; ", first.Operation.Errors.Select(error => error.Code + ": " + error.Detail)));
        context.Generator.Fail = true;
        int expected;
        Rand.PushState(8721);
        try { expected = Rand.Int; }
        finally { Rand.PopState(); }
        Rand.PushState(8721);
        try
        {
            var failed = context.Request("broken", context.Second);
            Assert.Equal(CelestialOperationStatus.Rejected, failed.Operation.Status);
            Assert.Equal(expected, Rand.Int);
            Assert.Single(context.Grid.PlanetLayers);
            Assert.True(context.Grid.PlanetLayers.ContainsKey(first.Handle!.LayerId));
            Assert.Equal(CelestialOperationStatus.Rejected, context.Request("broken", context.Second).Operation.Status);
            Assert.Single(context.Grid.PlanetLayers);
        }
        finally { Rand.PopState(); }
    }

    [Fact]
    public void FailureWithAnActualMapRetainsItsLayerAndBlocksReplacement()
    {
        using var context = new Context();
        context.Generator.AfterGeneration = layer => context.AddMap(layer);
        context.Generator.Fail = true;
        var failed = context.Request("broken", context.First);
        Assert.Equal(CelestialOperationStatus.Rejected, failed.Operation.Status);
        Assert.Contains(failed.Operation.Errors, error => error.Code == "SurfaceCleanupBlocked");
        Assert.Single(context.Grid.PlanetLayers);
        Assert.Single(Current.Game.Maps);
        var retained = Assert.Single(context.Grid.PlanetLayers.Values);
        Assert.Equal(CelestialOperationStatus.Rejected, context.Request("retry", context.First).Operation.Status);
        Assert.Same(retained, Assert.Single(context.Grid.PlanetLayers.Values));
        Assert.Equal(Current.Game.Maps[0].Tile.Layer, retained);
    }

    [Fact]
    public void RealHeaderIsLoadedBeforeNativeInitializeAndRestoresExactOwnerAndConfiguration()
    {
        using var context = new Context();
        context.Definition.worldDrawLayers.Add(typeof(HeaderObserver));
        var result = context.Request("surface", context.First);
        var original = context.Layer(result.Handle!.LayerId);
        CelestialSurfaceLayer restored = null;
        string path = Path.Combine(Path.GetTempPath(), "PFL-celestial-surface-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Scribe.saver.InitSaving(path, "celestial-surface");
            Scribe_Deep.Look(ref original, "layer");
            Scribe.saver.FinalizeSaving();
            HeaderObserver.Owner = null;
            // 独立层先于世界管理器绑定恢复；完整世界的寻路缓存由世界加载验收覆盖。
            var world = Current.CreatingWorld;
            var worldGrid = world.grid;
            try
            {
                world.grid = null;
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref restored, "layer");
                Scribe.loader.FinalizeLoading();
            }
            finally { world.grid = worldGrid; }
            Assert.Equal(context.First, HeaderObserver.Owner);
            Assert.Equal(original.Header.OwnerId, restored.Header.OwnerId);
            Assert.Equal(original.LayerID, restored.Header.LayerId);
            Assert.Equal(original.Header.PhysicalRadiusMeters, restored.Header.PhysicalRadiusMeters);
            Assert.Equal(original.Header.GenerationConfiguration.ToArray(), restored.Header.GenerationConfiguration.ToArray());
            Assert.Equal(original.Tiles.Count, restored.Tiles.Count);
            Assert.All(restored.Tiles, tile => Assert.Same(context.Generator.Biome, tile.PrimaryBiome));
        }
        finally { Scribe.ForceStop(); restored?.Dispose(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RuntimeRemovalChecksActualMapsAndReleasesOnlyTheOwnedEmptyLayer()
    {
        using var context = new Context();
        var first = context.Request("surface-a", context.First).Handle!;
        var second = context.Request("surface-b", context.Second).Handle!;
        context.AddMap(context.Layer(first.LayerId));
        Assert.Contains(context.Runtime.CanRemove(context.First).Blockers, blocker => blocker.Code == "MapExists");
        Current.Game.Maps.Clear();
        var operation = context.Runtime.Remove(new RemoveBodyRequest(new("remove", context.Runtime.WorldSessionId, context.Runtime.Catalog.Revision), context.First));
        Assert.Equal(CelestialOperationStatus.Pending, operation.Status);
        Assert.True(context.Runtime.Advance(new PhysicsIntervalRequest(context.Runtime.WorldSessionId, 0, 0, 0)).Success);
        Assert.Equal(CelestialOperationStatus.Applied, context.Runtime.GetOperation("remove").Status);
        Assert.False(context.Grid.PlanetLayers.ContainsKey(first.LayerId));
        Assert.True(context.Grid.PlanetLayers.ContainsKey(second.LayerId));
    }

    [Fact]
    public void RestoreRejectsAHeaderWhoseGenerationIdentityDiffersFromItsNativeLayer()
    {
        using var context = new Context();
        var handle = context.Request("surface", context.First).Handle!;
        var original = context.Layer(handle.LayerId).Header;
        var altered = new CelestialSurfaceHeader(original.OwnerId, original.LayerId, original.PhysicalRadiusMeters, original.NativeRadius,
            original.Subdivisions, unchecked(original.GenerationSeed + 1), original.GenerationConfiguration);
        Assert.Equal("SurfaceIdentityMismatch", Assert.Single(context.Surfaces.RestoreHeader(altered).Errors).Code);
    }

    [Fact]
    public void MissingKnownLayerCannotBeReplacedOrSilentlyOmittedFromSaveCapture()
    {
        using var context = new Context();
        var handle = context.Request("surface", context.First).Handle!;
        var layer = context.Layer(handle.LayerId);
        ((Dictionary<int, PlanetLayer>)context.Grid.PlanetLayers).Remove(handle.LayerId);
        try
        {
            Assert.Equal("SurfaceIdentityMismatch", Assert.Single(context.Surfaces.Find(context.First).Errors).Code);
            Assert.Equal(CelestialOperationStatus.Rejected, context.Request("replacement", context.First).Operation.Status);
            Assert.Empty(context.Grid.PlanetLayers);
            Assert.Throws<InvalidOperationException>(() => context.Surfaces.CaptureHeaders());
        }
        finally { layer.Dispose(); }
    }

    public sealed class HeaderObserver : WorldDrawLayer
    {
        public static CelestialInstanceId? Owner;
        public HeaderObserver() { if (Scribe.mode == LoadSaveMode.LoadingVars) Owner = CelestialSurfaceLayer.InitializingHeader?.OwnerId; }
    }
    private sealed class Generator : ICelestialSurfaceGenerator
    {
        public bool Fail { get; set; }
        public Action<CelestialSurfaceLayer> AfterGeneration { get; set; }
        public BiomeDef Biome { get; } = new() { defName = "PF_SurfaceProbeBiome", shortHash = 63102 };
        public CelestialResult<bool> Generate(CelestialSurfaceLayer layer, CelestialSurfaceHeader header)
        {
            _ = Rand.Int;
            if (!Fail || AfterGeneration != null)
                for (int index = 0; index < layer.TilesCount; index++)
                    layer.Tiles.Add(new SurfaceTile(new PlanetTile(index, layer)) { PrimaryBiome = Biome });
            AfterGeneration?.Invoke(layer);
            if (Fail) throw new InvalidOperationException("fixture world generation failure");
            return CelestialResult.Ok(true);
        }
    }
    private sealed class Context : IDisposable
    {
        private readonly Game previousGame = Current.Game;
        private readonly World previousWorld = Current.CreatingWorld;
        private readonly CelestialXmlTestScope preferences = new();
        private readonly PlanetLayerDef[] previousLayerDefs = DefDatabase<PlanetLayerDef>.AllDefsListForReading.ToArray();
        private readonly BiomeDef[] previousBiomeDefs = DefDatabase<BiomeDef>.AllDefsListForReading.ToArray();
        private readonly Dictionary<ushort, BiomeDef> previousBiomeHashes = new(
            (Dictionary<ushort, BiomeDef>)AccessTools.Field(typeof(DefDatabase<BiomeDef>), "defsByShortHash").GetValue(null));
        public WorldGrid Grid { get; } = new();
        public PlanetLayerDef Definition { get; } = new() { defName = "PF_CelestialSurfaceProbe", layerType = typeof(CelestialSurfaceLayer),
            tileType = typeof(SurfaceTile), worldDrawLayers = new List<Type>() };
        public Generator Generator { get; } = new();
        public CelestialRuntime Runtime { get; }
        public CelestialSurfaceService Surfaces { get; }
        public CelestialInstanceId First { get; }
        public CelestialInstanceId Second { get; }
        public Context()
        {
            var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            game.components = new List<GameComponent>();
            AccessTools.Field(typeof(Game), "maps").SetValue(game, new List<Map>());
            var world = new World { grid = Grid, worldObjects = new WorldObjectsHolder() };
            AccessTools.Field(typeof(Game), "worldInt").SetValue(game, world);
            Current.Game = game;
            Current.CreatingWorld = world;
            DefDatabase<PlanetLayerDef>.Add(Definition);
            DefDatabase<BiomeDef>.Add(Generator.Biome);
            ((Dictionary<ushort, BiomeDef>)AccessTools.Field(typeof(DefDatabase<BiomeDef>), "defsByShortHash").GetValue(null))[Generator.Biome.shortHash] = Generator.Biome;
            var geometry = new TestCelestialGeometryProperties { key = "shape", radiusMeters = new() { value = 5000000 } };
            var surface = new CompProperties_Surface { key = "surface", subdivisions = new() { value = 0 } };
            var template = CelestialBodyTemplate.Create("PF_SurfaceBody", "surface", "Planet", Array.Empty<string>(),
                new[] { geometry.ToDefinition(), surface.ToDefinition() }).Value;
            var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid());
            bridge.SetFrame(0, new Dictionary<CelestialInstanceId, CelestialMotionState>());
            Runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, new Resources(), 1, _ => CelestialResult.Ok(template));
            Surfaces = new CelestialSurfaceService(Runtime, Definition, Generator);
            First = Runtime.Create(new CreateBodyRequest(new("first", bridge.SessionId, 0), template.DefinitionId, "body")).InstanceIds[0];
            Second = Runtime.Create(new CreateBodyRequest(new("second", bridge.SessionId, 0), template.DefinitionId, "body")).InstanceIds[0];
            Assert.True(Runtime.Advance(new PhysicsIntervalRequest(bridge.SessionId, 0, 0, 0)).Success);
        }
        public CelestialSurfaceOperation Request(string operationId, CelestialInstanceId id) => Runtime.RequestSurface(
            new CelestialSurfaceRequest(new(operationId, Runtime.WorldSessionId, Runtime.Catalog.Revision), id));
        public CelestialSurfaceLayer Layer(int layerId) => (CelestialSurfaceLayer)Grid.PlanetLayers[layerId];
        public void AddMap(CelestialSurfaceLayer layer)
        {
            var patch = new Harmony("PFL.Tests.CelestialSurfaceTexture");
            patch.Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>), "Get"), prefix: new HarmonyMethod(typeof(Context), nameof(SkipTextureLoad)));
            try { RuntimeHelpers.RunClassConstructor(typeof(WorldObject).TypeHandle); }
            finally { patch.UnpatchAll(patch.Id); }
            var parent = (MapParent)RuntimeHelpers.GetUninitializedObject(typeof(MapParent));
            parent.ID = 9201;
            AccessTools.Field(typeof(WorldObject), "tile").SetValue(parent, new PlanetTile(0, layer));
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.uniqueID = 9202;
            map.info = new MapInfo { parent = parent };
            Current.Game.Maps.Add(map);
        }
        private static bool SkipTextureLoad() => false;
        public void Dispose()
        {
            Grid.Dispose();
            Current.Game = previousGame;
            Current.CreatingWorld = previousWorld;
            DefDatabase<PlanetLayerDef>.Clear();
            foreach (var definition in previousLayerDefs) DefDatabase<PlanetLayerDef>.Add(definition);
            DefDatabase<BiomeDef>.Clear();
            foreach (var definition in previousBiomeDefs) DefDatabase<BiomeDef>.Add(definition);
            var hashes = (Dictionary<ushort, BiomeDef>)AccessTools.Field(typeof(DefDatabase<BiomeDef>), "defsByShortHash").GetValue(null);
            hashes.Clear();
            foreach (var pair in previousBiomeHashes) hashes.Add(pair.Key, pair.Value);
            preferences.Dispose();
        }
    }
    private sealed class Resources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId instanceId) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId instanceId) => CelestialResult.Ok(true);
    }
}
