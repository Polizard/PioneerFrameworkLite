using System;
using System.IO;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialRestoreTests
{
    [Fact]
    public void ManagerRejectsSavesWithoutTheNewFrameworkDocument()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        string path = Path.Combine(Path.GetTempPath(), "PFL-celestial-unsupported-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(path, "<?xml version=\"1.0\"?><manager />");
            Scribe.loader.InitLoading(path);
            var manager = new CelestialBodyManager(f.Native.Game);
            Assert.Contains("UnsupportedSaveVersion", Assert.Throws<InvalidOperationException>(() => manager.ExposeData()).Message);
        }
        finally { Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ChangedHandlerMetadataBlocksOnlyAffectedBodiesAndKeepsTheirRawManifest()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var document = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint().Value;
        var original = CelestialBuiltinCompRegistry.Create();
        var registry = new CelestialCompRegistry();
        foreach (string handler in new[] { SurfaceResolvedProperties.Handler, BiomeGenerationResolvedProperties.Handler, MapGenerationResolvedProperties.Handler, ExternalPhysicsResolvedProperties.Handler })
        { Assert.True(original.TryGet(handler, out var registered)); Assert.True(registry.Register(registered).Success); }
        var bad = new ChangedGeometryHandler();
        Assert.True(registry.Register(new(SphericalBodyResolvedProperties.Handler, 1, 1, bad, bad)).Success);
        var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(bridge.SessionId, bridge, registry, _ => throw new Exception("No XML"));
        Assert.True(restore.RestoreIdentities(document).Success);
        Assert.All(restore.Runtime.Catalog.CaptureInstances(), instance => Assert.Equal(CelestialLifecycle.Blocked, instance.Lifecycle));
        Assert.Equal(document.Bodies.SelectMany(body => body.Components).Select(comp => Convert.ToBase64String(comp.ConfigurationPayload.Span)),
            new CelestialSaveAdapter(restore.Runtime).CaptureCheckpoint().Value.Bodies.SelectMany(body => body.Components).Select(comp => Convert.ToBase64String(comp.ConfigurationPayload.Span)));
    }
    private sealed class ChangedGeometryHandler : ICelestialCompFactory, ICelestialCompStateHandler
    {
        public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new ChangedGeometryComp(properties.Key);
        public CelestialResult<ICelestialResolvedProperties> DecodeProperties(string key, int version, ReadOnlyMemory<byte> payload) =>
            CelestialResult.Ok<ICelestialResolvedProperties>(SphericalBodyResolvedProperties.Deserialize(key, version, payload).Value);
        public CelestialResult<bool> RestoreState(CelestialBodyComp component, int version, ReadOnlyMemory<byte> payload) => CelestialResult.Ok(true);
    }
    private sealed class ChangedGeometryComp(string key) : CelestialBodyComp(new(key, SphericalBodyResolvedProperties.Handler, 1, new[] { "renamed.geometry" }, exclusiveSlot: "geometry")) { }
    [Fact]
    public void PendingCommandFifoSurvivesRestoreIndependentOfOperationIdSortOrder()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var runtime = f.Scope.Runtime;
        var optional = f.Scope.Manifest.Items.Where(item => item.Id != f.Root.Header.OwnerId).ToArray();
        Assert.True(runtime.Submit(new(CompSurface.RequestCommand, "z-first", optional[0].Id, runtime.WorldSessionId, 1)).Success);
        Assert.True(runtime.Submit(new(CompSurface.RequestCommand, "a-second", optional[1].Id, runtime.WorldSessionId, 1)).Success);
        var document = new CelestialSaveAdapter(runtime).CaptureCheckpoint().Value;
        var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(bridge.SessionId, bridge, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No XML"));
        Assert.True(restore.RestoreIdentities(document).Success);
        Assert.True(restore.BindNativeObjects().Success);
        Assert.True(restore.CompleteRestore().Success);
        bridge.SetFrame(0, document.Bodies.ToDictionary(body => body.Description.Id, _ => new CelestialMotionState(1, DVector3.zero, DVector3.zero, DQuaternion.identity, DVector3.zero)));
        Assert.True(restore.Runtime.Advance(new(restore.Runtime.WorldSessionId, restore.Runtime.CurrentStamp.TopologyRevision, 0, 0)).Success);
        var layers = f.Native.Grid.PlanetLayers.Values.OfType<CelestialSurfaceLayer>().OrderBy(layer => layer.LayerID).ToArray();
        Assert.Equal(optional[0].Id, layers[1].Header.OwnerId);
        Assert.Equal(optional[1].Id, layers[2].Header.OwnerId);
    }
    [Fact]
    public void ManagerExposeDataRestoresIdentityBeforeLoadedGameAndRetainsMissingProviderDiagnostics()
    {
        using var diagnostics = new CelestialExpectedRestoreErrorScope();
        using var f = new CelestialMapIdentityTests.Fixture();
        var manager = f.Native.Game.GetComponent<CelestialBodyManager>();
        string path = Path.Combine(Path.GetTempPath(), "PFL-celestial-manager-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Scribe.saver.InitSaving(path, "manager"); manager.ExposeData(); Scribe.saver.FinalizeSaving();
            var loaded = new CelestialBodyManager(f.Native.Game);
            Scribe.loader.InitLoading(path); loaded.ExposeData(); Scribe.loader.FinalizeLoading();
            Assert.True(loaded.TryGetCelestialRuntime(out var runtime));
            Assert.True(runtime.IsRestoring);
            loaded.LoadedGame();
            Assert.Single(diagnostics.Messages);
            Assert.Contains(loaded.CelestialRestoreErrors, error => error.Code == "ProviderUnavailable");
            Assert.Equal(f.Scope.Runtime.CaptureCatalog().Bodies.Select(body => body.Id), runtime.CaptureCatalog().Bodies.Select(body => body.Id));
            Assert.Single(f.Native.Grid.PlanetLayers);
        }
        finally { Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PendingRegistrationAndRemovalResumeWithOriginalIdsOnlyAfterBinding(bool cleanupPending, bool savedWithoutModules)
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var runtime = f.Scope.Runtime;
        var source = f.Scope.Manifest.Items.First(item => item.Id != f.Root.Header.OwnerId);
        if (cleanupPending) Assert.Equal(CelestialOperationStatus.Applied, runtime.RequestSurface(new(new("empty-surface", runtime.WorldSessionId, runtime.Catalog.Revision), source.Id)).Operation.Status);
        var pendingId = CelestialInstanceId.Parse("dynamic:pending");
        var spawn = new CelestialSpawnItem(pendingId, "pending", 0, "pending[0]", source.Template, source.Components);
        var manifest = new CelestialSpawnManifest("pending", 123, null, new[] { spawn });
        if (cleanupPending)
        {
            var removal = runtime.Remove(new(new("remove", runtime.WorldSessionId, runtime.Catalog.Revision), source.Id));
            Assert.Equal(CelestialOperationStatus.Pending, removal.Status);
            var patch = new HarmonyLib.Harmony("pfl.tests.celestial-restore.cleanup");
            patch.Patch(HarmonyLib.AccessTools.Method(typeof(RuntimePlanetLayerService), nameof(RuntimePlanetLayerService.RemoveCelestialInstance)),
                prefix: new HarmonyLib.HarmonyMethod(typeof(CelestialRestoreTests), nameof(RejectCleanup)));
            try { Assert.True(runtime.Advance(new(runtime.WorldSessionId, runtime.CurrentStamp.TopologyRevision, 0, 0)).Success); }
            finally { patch.UnpatchAll(patch.Id); }
            Assert.Equal(CelestialRemovalPhase.Cleanup, Assert.Single(runtime.Removal.Capture()).Phase);
            Assert.False(runtime.Query(source.Id).Value.CommittedMembership);
        }
        Assert.Equal(CelestialOperationStatus.Pending, runtime.CreateBatch(new(new("create-pending", runtime.WorldSessionId, runtime.Catalog.Revision), manifest)).Status);
        if (!cleanupPending) Assert.Equal(CelestialOperationStatus.Pending, runtime.Remove(new(new("remove", runtime.WorldSessionId, runtime.Catalog.Revision), source.Id)).Status);
        var saved = new CelestialSaveAdapter(runtime).CaptureCheckpoint().Value;
        Assert.Equal(!cleanupPending, saved.CommittedBodies.Contains(source.Id));
        Assert.DoesNotContain(pendingId, saved.CommittedBodies);
        if (savedWithoutModules)
        {
            var unavailable = new CelestialRestoreCoordinator(Guid.NewGuid(), null, new CelestialCompRegistry(), _ => throw new Exception("No resampling"));
            Assert.True(unavailable.RestoreIdentities(saved).Success);
            var degraded = new CelestialSaveAdapter(unavailable.Runtime).CaptureCheckpoint();
            Assert.True(degraded.Success);
            saved = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(degraded.Value).Value).Value;
        }
        var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(bridge.SessionId, bridge, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No resampling"), mapBackend: f.Backend);
        Assert.True(restore.RestoreIdentities(saved).Success);
        Assert.Equal(0, bridge.RegistrationCount(pendingId));
        Assert.Contains(restore.Runtime.CreateBatch(new(new("early", restore.Runtime.WorldSessionId, restore.Runtime.Catalog.Revision), manifest)).Errors, e => e.Code == "RestorePending");
        Assert.True(restore.BindNativeObjects().Success);
        Assert.True(restore.CompleteRestore().Success);
        Assert.Equal(CelestialOperationStatus.Pending, restore.Runtime.GetOperation("create-pending").Status);
        Assert.Equal(0, bridge.RegistrationCount(pendingId));
        bridge.SetFrame(0, saved.Bodies.Select(body => body.Description.Id).Append(pendingId).Distinct().ToDictionary(id => id,
            _ => new CelestialMotionState(1, DVector3.zero, DVector3.zero, DQuaternion.identity, DVector3.zero)));
        Assert.True(restore.Runtime.Advance(new(restore.Runtime.WorldSessionId, restore.Runtime.CurrentStamp.TopologyRevision, 0, 0)).Success);
        Assert.Equal(CelestialLifecycle.Ready, restore.Runtime.Query(pendingId).Value.Lifecycle);
        Assert.Equal(CelestialLifecycle.Removed, restore.Runtime.Query(source.Id).Value.Lifecycle);
        Assert.Equal(1, bridge.RegistrationCount(pendingId));
        Assert.Equal(1, bridge.RegistrationCount(source.Id));
        Assert.Contains(restore.Runtime.Remove(new(new("old-callback", runtime.WorldSessionId, runtime.Catalog.Revision), pendingId)).Errors, e => e.Code == "StaleSession");
    }
    private static bool RejectCleanup(ref CelestialResult<bool> __result)
    { __result = CelestialResult.Reject<bool>(new CelestialError("FixtureCleanupPending")); return false; }

    [Fact]
    public void MissingNativeMapBlocksRestoreAndRecaptureRetainsEverySavedAssociation()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        f.Request("map", CelestialMapAction.Generate);
        var saved = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint().Value;
        f.Native.Game.Maps.Clear();
        var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(bridge.SessionId, bridge, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No XML"));
        Assert.True(restore.RestoreIdentities(saved).Success);
        Assert.Contains(restore.BindNativeObjects().Errors, e => e.Code == "MapIdentityMismatch");
        var captured = new CelestialSaveAdapter(restore.Runtime).CaptureCheckpoint().Value;
        Assert.Equal(saved.Maps, captured.Maps);
        Assert.Equal(saved.Surfaces.Select(surface => surface.LayerId), captured.Surfaces.Select(surface => surface.LayerId));
        Assert.Empty(f.Native.Game.Maps);
    }
    [Fact]
    public void RealScribeDocumentRestoresKnownNativeMapAndConfigurationWithoutResolvingXml()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var first = f.Request("settle", CelestialMapAction.Settle);
        var original = new CelestialSaveAdapter(f.Scope.Runtime);
        var checkpoint = original.CaptureCheckpoint();
        Assert.True(checkpoint.Success);
        string path = Path.Combine(Path.GetTempPath(), "PFL-celestial-save-" + Guid.NewGuid().ToString("N") + ".xml");
        var restored = new CelestialSaveAdapter();
        try
        {
            Scribe.saver.InitSaving(path, "celestial-save");
            original.ExposeData();
            Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path);
            restored.ExposeData();
            Scribe.loader.FinalizeLoading();
            Assert.NotNull(restored.Document);
            var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid());
            var coordinator = new CelestialRestoreCoordinator(bridge.SessionId, bridge, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("XML resolution during restore"), mapBackend: f.Backend);
            Assert.True(coordinator.RestoreIdentities(restored.Document).Success);
            Assert.False(coordinator.Runtime.Advance(new(coordinator.Runtime.WorldSessionId, 0, 0, 0)).Success);
            Assert.True(coordinator.BindNativeObjects().Success);
            Assert.True(coordinator.CompleteRestore().Success);
            Assert.NotEqual(f.Scope.Runtime.WorldSessionId, coordinator.Runtime.WorldSessionId);
            var request = coordinator.Runtime.RequestMap(new(new("restored-map", coordinator.Runtime.WorldSessionId, coordinator.Runtime.CaptureCatalog().Revision),
                f.Address, CelestialMapAction.Generate, 250, 250));
            Assert.Equal(first.Handle, request.Handle);
            Assert.True(coordinator.Runtime.Maps.TryGetNativeMap(first.Handle.MapId, out var map));
            Assert.Same(f.Backend.LastMap, map);
            Assert.Equal(1, f.Backend.Generated);
            var again = new CelestialSaveAdapter(coordinator.Runtime).CaptureCheckpoint().Value;
            Assert.Equal(restored.Document.Bodies.Select(body => body.Description.RadiusMeters), again.Bodies.Select(body => body.Description.RadiusMeters));
        }
        finally { Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MissingProviderOrHandlerRetainsRawDataAndNeverRegeneratesKnownMaps()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        f.Request("map", CelestialMapAction.Generate);
        var document = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint().Value;
        var unavailable = new CelestialRestoreCoordinator(Guid.NewGuid(), null, new CelestialCompRegistry(), _ => throw new Exception("No XML during restore"));
        Assert.True(unavailable.RestoreIdentities(document).Success);
        Assert.False(unavailable.CompleteRestore().Success);
        var retained = new CelestialSaveAdapter(unavailable.Runtime).CaptureCheckpoint();
        Assert.True(retained.Success);
        Assert.Equal(document.PhysicsCheckpoint.Payload.ToArray(), retained.Value.PhysicsCheckpoint.Payload.ToArray());
        foreach (var body in retained.Value.Bodies)
        {
            Assert.Equal(CelestialLifecycle.Blocked, body.Lifecycle);
            var saved = document.Bodies.Single(saved => saved.Description.Id == body.Description.Id);
            Assert.Equal(saved.Components.Select(comp => Convert.ToBase64String(comp.ConfigurationPayload.Span)),
                body.Components.Select(comp => Convert.ToBase64String(comp.ConfigurationPayload.Span)));
        }
        Assert.Single(f.Native.Game.Maps);
        Assert.Equal(1, f.Backend.Generated);
    }

    [Fact]
    public void ReenablingMissingModulesAfterSavingDegradedWorldRestoresPreviousLifecycleAndMapIdentity()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        f.Request("map", CelestialMapAction.Generate);
        var original = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint().Value;
        var unavailable = new CelestialRestoreCoordinator(Guid.NewGuid(), null, new CelestialCompRegistry(), _ => throw new Exception("No resampling"));
        Assert.True(unavailable.RestoreIdentities(original).Success);
        var degraded = new CelestialSaveAdapter(unavailable.Runtime).CaptureCheckpoint().Value;
        var encoded = CelestialSaveCodec.Encode(degraded);
        Assert.True(encoded.Success);
        degraded = CelestialSaveCodec.Decode(encoded.Value).Value;
        var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid());
        var recovered = new CelestialRestoreCoordinator(bridge.SessionId, bridge, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No resampling"), mapBackend: f.Backend);
        Assert.True(recovered.RestoreIdentities(degraded).Success);
        Assert.All(recovered.Runtime.Catalog.CaptureInstances(), instance => Assert.Equal(CelestialLifecycle.Blocked, instance.Lifecycle));
        var facade = new CelestialBodyManager(f.Native.Game);
        Assert.True(facade.AttachCelestialRuntime(recovered.Runtime).Success);
        int rootHandle = recovered.Runtime.Query(f.Root.Header.OwnerId).Value.Description.Handle;
        Assert.False(facade.GetBodyById(rootHandle).hasSurface);
        Assert.True(recovered.BindNativeObjects().Success);
        Assert.True(recovered.CompleteRestore().Success);
        Assert.False(recovered.Runtime.IsRestoring);
        Assert.True(facade.GetBodyById(rootHandle).hasSurface);
        foreach (var before in original.Bodies)
        {
            var after = recovered.Runtime.Query(before.Description.Id).Value;
            Assert.Equal(before.Lifecycle, after.Lifecycle);
            Assert.Equal(before.Errors, after.Errors);
            Assert.Equal(before.Description.RadiusMeters, after.Description.RadiusMeters);
        }
        var savedMap = Assert.Single(original.Maps);
        Assert.True(recovered.Runtime.Maps.TryGetNativeMap(savedMap.MapId.Value, out var actual));
        Assert.Same(f.Backend.LastMap, actual);
        Assert.Equal(1, f.Backend.Generated);
    }
}
