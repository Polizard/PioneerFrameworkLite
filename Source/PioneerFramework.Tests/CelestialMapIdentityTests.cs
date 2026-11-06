using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PioneerFramework.Celestial;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialMapIdentityTests
{
    [Fact]
    public void NativeGenerationRequiresSupportedScopeAndExactReadyProvenance()
    {
        using var f = new Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        var unsupported = new MapGeneratorDef { defName = "Base_Player", isUnderground = true };
        Assert.Contains("LocalMapUnsupported", Assert.Throws<InvalidOperationException>(() => CelestialMapService.RequireNativeGenerator(parent, new IntVec3(50, 1, 50), unsupported)).Message);
        var map = f.Backend.Generate(parent, 50, 50, null);
        Assert.Contains("LocalMapProvenanceMissing", Assert.Throws<InvalidOperationException>(() => f.Service.RequireNativeReady(f.Address, parent, map)).Message);
        var generator = new MapGeneratorDef { defName = "Encounter" };
        var frozen = PioneerFramework.MapGeneration.LocalMapSourceResolver.Resolve(parent, new IntVec3(50, 1, 50), generator, false).Value;
        var ready = PioneerFramework.MapGeneration.LocalMapGenerationRecord.Capture(new(frozen.Source, frozen.Specification, PioneerFramework.MapGeneration.MapGenerationPurpose.Encounter),
            PioneerFramework.MapGeneration.MapGenerationState.Ready, "Done", null, null);
        f.Service.RecordNativeGeneration(f.Address with { MapParentId = parent.ID }, map.uniqueID, ready);
        f.Service.RequireNativeReady(f.Address, parent, map);
        map.uniqueID++;
        Assert.Contains("LocalMapProvenanceMissing", Assert.Throws<InvalidOperationException>(() => f.Service.RequireNativeReady(f.Address, parent, map)).Message);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledMapCapabilityCannotGenerateEitherDirectlyOrFromAnAlreadyQueuedJob(bool queued)
    {
        using var f = new Fixture(template => CelestialBodyTemplate.Create(template.DefinitionId, template.Label, template.Category, template.Tags,
            template.Components.Select(component => {
                var d = component.Descriptor;
                if (!d.Provides.Contains("map.generation")) return component;
                return new CelestialCompDefinition(new(d.Key, d.HandlerId, d.SchemaVersion, d.Provides, d.Requires, d.ExclusiveSlot, d.Before, d.After, d.UpdatePolicy,
                    CelestialCompFailurePolicy.DisableOptional), component.Factory, context => CelestialResult.Ok(component.Resolve(context).Value.Properties));
            })).Value);
        var runtime = f.Scope.Runtime;
        if (queued)
        {
            CelestialMapOperation submitted = null;
            var worker = new System.Threading.Thread(() => submitted = f.Request("map-probe", CelestialMapAction.Generate)); worker.Start(); worker.Join();
            Assert.Equal(CelestialOperationStatus.Pending, submitted.Operation.Status);
        }
        var capability = (CelestialBodyComp)Assert.Single(runtime.GetCapabilities<ICelestialMapGenerationCapability>(f.Root.Header.OwnerId));
        Assert.True(runtime.Catalog.TryGetInstance(f.Root.Header.OwnerId, out var body));
        runtime.WithPublication(() => { runtime.Creation.HandleComponentFault(body, capability, "ComponentEventFailed", new InvalidOperationException("map fixture")); return true; });
        Assert.Equal(CelestialLifecycle.Ready, runtime.Query(body.Id).Value.Lifecycle);
        Assert.Empty(runtime.GetCapabilities<ICelestialMapGenerationCapability>(body.Id));
        if (queued) runtime.ProcessHostWork(); else f.Request("map-probe", CelestialMapAction.Generate);
        var receipt = runtime.GetOperation("map-probe");
        Assert.Equal(CelestialOperationStatus.Rejected, receipt.Status);
        Assert.Contains(receipt.Errors, error => error.Code == "MapGenerationCapabilityMissing");
        Assert.Equal(0, f.Backend.Created); Assert.Equal(0, f.Backend.Generated);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulNativeRetirementSurvivesSaveAndRejectsOldRequestsAfterExplicitReplacement(bool removeParent)
    {
        using var f = new Fixture();
        var first = f.Request("first-map", CelestialMapAction.Generate);
        var map = f.Backend.LastMap;
        var parent = map.Parent;
        CelestialNativeMapLifecycle.CaptureMapRemoval(f.Native.Game, map, out var mapState);
        Assert.NotNull(mapState);
        // A postfix alone cannot authorize removal while the exact native object is still present.
        CelestialNativeMapLifecycle.MapRemoved(f.Native.Game, mapState);
        Assert.Equal(map.uniqueID, Assert.Single(f.Service.CaptureSites()).MapId);
        f.Native.Game.Maps.Remove(map);
        CelestialNativeMapLifecycle.MapRemoved(f.Native.Game, mapState);
        Assert.Null(Assert.Single(f.Service.CaptureSites()).MapId);
        if (removeParent)
        {
            CelestialNativeMapLifecycle.CaptureParentRemoval(f.Native.World.worldObjects, parent, out var parentState);
            Assert.NotNull(parentState);
            f.Backend.RemoveEmptyParent(parent);
            CelestialNativeMapLifecycle.ParentRemoved(f.Native.World.worldObjects, parentState);
            Assert.True(Assert.Single(f.Service.CaptureSites()).ParentRetired);
        }
        var saved = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint();
        Assert.True(saved.Success, string.Join(",", saved.Errors));
        var decoded = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(saved.Value).Value).Value;
        var bridge = new PioneerFramework.TestSupport.Celestial.DeterministicTestPhysicsBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(bridge.SessionId, bridge, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No resampling"), mapBackend: f.Backend);
        Assert.True(restore.RestoreIdentities(decoded).Success);
        Assert.True(restore.BindNativeObjects().Success);
        Assert.True(restore.CompleteRestore().Success);
        var replacement = restore.Runtime.RequestMap(new(new("replacement", restore.Runtime.WorldSessionId, restore.Runtime.Catalog.Revision), f.Address, CelestialMapAction.Generate, 250, 250));
        Assert.Equal(CelestialOperationStatus.Applied, replacement.Operation.Status);
        Assert.NotEqual(first.Handle.MapId, replacement.Handle.MapId);
        var stale = restore.Runtime.RequestMap(new(new("first-map", restore.Runtime.WorldSessionId, restore.Runtime.Catalog.Revision), f.Address, CelestialMapAction.Generate, 250, 250));
        Assert.Contains(stale.Operation.Errors, error => error.Code == "MapIdentityMismatch");
        Assert.Equal(2, f.Backend.Generated);
        Assert.True(new CelestialSaveAdapter(restore.Runtime).CaptureCheckpoint().Success);
    }

    [Fact]
    public void NativeRetirementTokenFromAnotherSessionCannotRetireLiveMapIdentity()
    {
        using var f = new Fixture();
        f.Request("first", CelestialMapAction.Generate);
        var map = f.Backend.LastMap;
        CelestialNativeMapLifecycle.CaptureMapRemoval(f.Native.Game, map, out var state);
        Assert.NotNull(state);
        var game = Current.Game;
        try
        {
            Current.Game = null;
            CelestialNativeMapLifecycle.MapRemoved(f.Native.Game, state);
        }
        finally { Current.Game = game; }
        Assert.Equal(map.uniqueID, Assert.Single(f.Service.CaptureSites()).MapId);
    }

    [Fact]
    public void SavingLearnsNativeColonyMapsBeforeAnyFrameworkRequestAndNeverRegeneratesTheirLostIdentity()
    {
        using var f = new Fixture();
        var parent = f.Backend.CreateParent(f.Address);
        var map = f.Backend.Generate(parent, 250, 250, null);
        Assert.Empty(f.Service.CaptureSites());
        var capture = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint();
        Assert.True(capture.Success, string.Join(",", capture.Errors));
        var saved = Assert.Single(capture.Value.Maps);
        Assert.Equal(map.uniqueID, saved.MapId);
        Assert.Equal(parent.ID, saved.MapParentId);
        Assert.Equal(f.Address.InstanceId, saved.Address.InstanceId);
        f.Native.Game.Maps.Remove(map);
        var missingCapture = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint();
        Assert.True(missingCapture.Success);
        Assert.Equal(saved, Assert.Single(missingCapture.Value.Maps));
        Assert.Contains(f.Request("lost-native", CelestialMapAction.Generate).Operation.Errors, error => error.Code == "MapIdentityMismatch");
        Assert.Equal(1, f.Backend.Generated);
    }

    [Fact]
    public void QueriesAndInspectDoNotGenerateAndExplicitActionsReuseTheSameMap()
    {
        using var f = new Fixture();
        Assert.True(f.Scope.Runtime.Query(f.Root.Header.OwnerId).Success);
        Assert.Single(f.Native.Grid.PlanetLayers);
        Assert.Equal(CelestialOperationStatus.Rejected, f.Request("inspect", CelestialMapAction.Inspect).Operation.Status);
        Assert.Equal(0, f.Backend.Created);
        var first = f.Request("settle", CelestialMapAction.Settle);
        Assert.Equal(CelestialOperationStatus.Applied, first.Operation.Status);
        foreach (var action in Enum.GetValues<CelestialMapAction>())
        {
            var next = f.Request(action.ToString(), action);
            Assert.Equal(first.Handle, next.Handle);
            Assert.True(f.Service.TryGetNativeMap(next.Handle.MapId, out var map));
            Assert.Same(f.Backend.LastMap, map);
        }
        Assert.Equal(1, f.Backend.Created);
        Assert.Equal(1, f.Backend.Generated);
    }

    [Fact]
    public void KnownMissingOrReplacedParentOrMapNeverCreatesASubstitute()
    {
        using var f = new Fixture();
        var first = f.Request("first", CelestialMapAction.Generate);
        var record = Assert.Single(f.Service.CaptureSites());
        f.Native.Game.Maps.Remove(f.Backend.LastMap);
        Assert.Contains(f.Request("lost-map", CelestialMapAction.Generate).Operation.Errors, e => e.Code == "MapIdentityMismatch");
        f.Native.Game.Maps.Add(f.Backend.LastMap);
        var parent = f.Backend.LastMap.Parent;
        f.Native.World.worldObjects.MapParents.Remove(parent);
        f.Native.World.worldObjects.AllWorldObjects.Remove(parent);
        f.Backend.CreateParent(f.Address);
        Assert.Contains(f.Request("lost-parent", CelestialMapAction.Generate).Operation.Errors, e => e.Code == "MapIdentityMismatch");
        Assert.Equal(record, Assert.Single(f.Service.CaptureSites()));
        Assert.Equal(1, f.Backend.Generated);
    }

    [Fact]
    public void SameTileOnDifferentInstancesHasSeparateNativeObjects()
    {
        using var f = new Fixture();
        var one = f.Request("root-map", CelestialMapAction.Generate);
        var other = f.Scope.Manifest.Items.First(item => item.Id != f.Root.Header.OwnerId).Id;
        var surface = f.Service.RequestSurface(new(new("view", f.Scope.Runtime.WorldSessionId, f.Scope.Runtime.CaptureCatalog().Revision), other));
        Assert.Equal(CelestialOperationStatus.Applied, surface.Operation.Status);
        var two = f.Scope.Runtime.RequestMap(new(new("other-map", f.Scope.Runtime.WorldSessionId, f.Scope.Runtime.CaptureCatalog().Revision),
            new(other, surface.Handle.LayerId, 0), CelestialMapAction.Land, 250, 250));
        Assert.Equal(CelestialOperationStatus.Applied, two.Operation.Status);
        Assert.NotEqual(one.Handle.MapId, two.Handle.MapId);
        Assert.Equal(2, f.Backend.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailurePreservesParentMapAndRandAndSurvivesSaveRebindRestore(bool makeMap)
    {
        using var f = new Fixture();
        f.Backend.Fail = true;
        f.Backend.MakeMapThenFail = makeMap;
        Rand.PushState(1337); float expected = Rand.Value; Rand.PopState();
        Rand.PushState(1337);
        try
        {
            Assert.Equal(CelestialOperationStatus.Rejected, f.Request("fail", CelestialMapAction.Generate).Operation.Status);
            Assert.Equal(expected, Rand.Value);
        }
        finally { Rand.PopState(); }
        Assert.Equal(0, f.Backend.Removed);
        var existing = Assert.Single(f.Native.World.worldObjects.MapParents);
        Assert.Equal(makeMap, existing.Map != null);
        Assert.NotNull(Assert.Single(f.Service.CaptureSites()).Failure);
        Assert.Contains(f.Request("incomplete", CelestialMapAction.Generate).Operation.Errors, error => error.Code == "MapGenerationIncomplete");
        Assert.Equal(1, f.Backend.Generated);
        var captured = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint();
        Assert.True(captured.Success, string.Join(",", captured.Errors));
        Assert.NotNull(Assert.Single(captured.Value.Maps).Failure);
        var encoded = CelestialSaveCodec.Encode(captured.Value); Assert.True(encoded.Success, string.Join(",", encoded.Errors));
        var decoded = CelestialSaveCodec.Decode(encoded.Value); Assert.True(decoded.Success, string.Join(",", decoded.Errors));
        var bridge = new PioneerFramework.TestSupport.Celestial.DeterministicTestPhysicsBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(bridge.SessionId, bridge, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No resampling"), mapBackend: f.Backend);
        Assert.True(restore.RestoreIdentities(decoded.Value).Success);
        Assert.True(restore.BindNativeObjects().Success); Assert.True(restore.CompleteRestore().Success);
        var rejected = restore.Runtime.RequestMap(new(new("after-load", restore.Runtime.WorldSessionId, restore.Runtime.Catalog.Revision), f.Address, CelestialMapAction.Generate, 250, 250));
        Assert.Null(rejected.Handle); Assert.Contains(rejected.Operation.Errors, error => error.Code == "MapGenerationIncomplete");
        Assert.Equal(1, f.Backend.Generated); Assert.Equal(0, f.Backend.Removed);
        if (makeMap)
        {
            f.Backend.LastMap.generatorDef = new MapGeneratorDef { defName = "Encounter" };
            Assert.Throws<InvalidOperationException>(() => GetOrGenerateMapUtility.GetOrGenerateMap(existing.Tile, new IntVec3(250, 1, 250), null));
        }
    }

    [Fact]
    public void LostKnownMapAlsoBlocksBodyRemovalAndNativeLookup()
    {
        using var f = new Fixture();
        var operation = f.Request("map", CelestialMapAction.Generate);
        f.Native.Game.Maps.Remove(f.Backend.LastMap);
        Assert.False(f.Service.TryGetNativeMap(operation.Handle.MapId, out _));
        Assert.Contains(f.Scope.Runtime.CanRemove(f.Root.Header.OwnerId).Blockers, error => error.Code == "MapIdentityMismatch");
    }

    [Fact]
    public void RestoredSiteRequiresExactParentMapAndSessionAndSize()
    {
        using var f = new Fixture();
        var map = f.Request("map", CelestialMapAction.Generate);
        Assert.True(f.Service.BindExisting(f.Address with { MapParentId = map.Handle.MapParentId }, f.Backend.LastMap.Parent, f.Backend.LastMap).Success);
        Assert.False(f.Service.BindExisting(f.Address with { MapParentId = map.Handle.MapParentId + 1 }, f.Backend.LastMap.Parent, f.Backend.LastMap).Success);
        var stale = f.Scope.Runtime.RequestMap(new(new("stale", Guid.NewGuid(), 0), f.Address, CelestialMapAction.Generate, 250, 250));
        Assert.Contains(stale.Operation.Errors, e => e.Code == "StaleSession");
        var size = f.Scope.Runtime.RequestMap(new(new("size", f.Scope.Runtime.WorldSessionId, f.Scope.Runtime.CaptureCatalog().Revision), f.Address,
            CelestialMapAction.Generate, 501, 250));
        Assert.Contains(size.Operation.Errors, e => e.Code == "InvalidMapSize");
        Assert.Equal(1, f.Backend.Created);
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly CelestialWorldBootstrapTests.Fixture world;
        public CelestialNativeWorldTestScope Native => world.Native;
        public CelestialWorldBootstrapScope Scope { get; }
        public CelestialSurfaceLayer Root { get; }
        public Backend Backend { get; } = new();
        public CelestialMapService Service => Scope.Runtime.Maps;
        public CelestialMapAddress Address => new(Root.Header.OwnerId, Root.LayerID, 0);
        public Fixture(Func<CelestialBodyTemplate, CelestialBodyTemplate> transform = null)
        {
            world = new(new CelestialMapProfileDef { defName = "PF_ProbeMaps" }, transform);
            Scope = CelestialWorldBootstrap.Begin(world.System, 17, CelestialWorldBootstrapTests.Fixture.Connect, Backend).Value;
            Native.Grid.GenerateFreshWorld();
            Root = (CelestialSurfaceLayer)Native.Grid.Surface;
            WorldGenerator.GeneratePlanetLayer(Root, "maps", 1);
            Assert.True(Scope.Complete().Success);
        }
        public CelestialMapOperation Request(string id, CelestialMapAction action) => Scope.Runtime.RequestMap(new(
            new(id, Scope.Runtime.WorldSessionId, Scope.Runtime.CaptureCatalog().Revision), Address, action, 250, 250));
        public void Dispose() { Scope.Dispose(); world.Dispose(); }
    }
    internal sealed class Backend : ICelestialMapBackend
    {
        public int Created, Generated, Removed;
        public bool Fail, MakeMapThenFail;
        public Map LastMap;
        public MapParent CreateParent(CelestialMapAddress address)
        {
            var patch = new Harmony("pfl.tests.celestial-map.textures");
            patch.Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>), "Get"), prefix: new HarmonyMethod(typeof(Backend), nameof(SkipTexture)));
            try { RuntimeHelpers.RunClassConstructor(typeof(WorldObject).TypeHandle); } finally { patch.UnpatchAll(patch.Id); }
            var parent = (MapParent)RuntimeHelpers.GetUninitializedObject(typeof(MapParent));
            parent.ID = 7000 + ++Created;
            AccessTools.Field(typeof(WorldObject), "tile").SetValue(parent, new PlanetTile(address.TileId, Find.WorldGrid.PlanetLayers[address.LayerId]));
            Find.WorldObjects.AllWorldObjects.Add(parent);
            Find.WorldObjects.MapParents.Add(parent);
            return parent;
        }
        public Map Generate(MapParent parent, int sizeX, int sizeZ, string generatorDefName)
        {
            Generated++;
            _ = Rand.Value;
            if (!Fail || MakeMapThenFail)
            {
                LastMap = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
                LastMap.uniqueID = 8000 + Generated;
                LastMap.info = new MapInfo { parent = parent };
                Current.Game.Maps.Add(LastMap);
            }
            if (Fail) throw new InvalidOperationException("generation fixture");
            return LastMap;
        }
        public void RemoveEmptyParent(MapParent parent)
        {
            Assert.False(parent.HasMap);
            Removed++;
            Find.WorldObjects.MapParents.Remove(parent);
            Find.WorldObjects.AllWorldObjects.Remove(parent);
        }
        private static bool SkipTexture() => false;
    }
}
