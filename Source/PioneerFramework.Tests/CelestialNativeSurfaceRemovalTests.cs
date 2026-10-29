using System;
using System.IO;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialNativeSurfaceRemovalTests
{
    [Fact]
    public void NativeDeletionOfAnExtraSurfaceKeepsCheckpointAndAllBodyIdentitiesRestorable()
    {
        using var fixture = new CelestialMapIdentityTests.Fixture();
        var runtime = fixture.Scope.Runtime;
        var id = fixture.Scope.Manifest.Items.First(item => item.Id != fixture.Root.Header.OwnerId).Id;
        var created = runtime.RequestSurface(new(new("surface-before-delete", runtime.WorldSessionId, runtime.CaptureCatalog().Revision), id));
        Assert.Equal(CelestialOperationStatus.Applied, created.Operation.Status);
        var expected = runtime.CaptureCatalog().Bodies.Select(body => body.Id).ToArray();
        var layer = fixture.Native.Grid.PlanetLayers[created.Handle.LayerId];
        fixture.Native.Grid.RemovePlanetLayer(layer);
        Assert.DoesNotContain(created.Handle.LayerId, fixture.Native.Grid.PlanetLayers.Keys);
        var checkpoint = new CelestialSaveAdapter(runtime).CaptureCheckpoint();
        Assert.True(checkpoint.Success, string.Join(";", checkpoint.Errors.Select(error => error.Code + ":" + error.Detail)));
        Assert.DoesNotContain(checkpoint.Value.Surfaces, surface => surface.OwnerId == id);
        Assert.Equal(expected, checkpoint.Value.PublishedBodies);

        string path = Path.Combine(Path.GetTempPath(), "PFL-native-surface-delete-" + Guid.NewGuid().ToString("N") + ".xml");
        var loaded = new CelestialSaveAdapter();
        try
        {
            Scribe.saver.InitSaving(path, "celestial"); new CelestialSaveAdapter(runtime).ExposeData(); Scribe.saver.FinalizeSaving();
            Scribe.loader.InitLoading(path); loaded.ExposeData(); Scribe.loader.FinalizeLoading();
            var provider = new DeterministicTestPhysicsBridge(Guid.NewGuid());
            var restore = new CelestialRestoreCoordinator(provider.SessionId, provider, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No XML"));
            Assert.True(restore.RestoreIdentities(loaded.Document).Success);
            Assert.True(restore.BindNativeObjects().Success);
            Assert.True(restore.CompleteRestore().Success);
            Assert.Equal(expected, restore.Runtime.CaptureCatalog().Bodies.Select(body => body.Id));
            Assert.All(expected, body => Assert.Equal(CelestialLifecycle.Ready, restore.Runtime.Query(body).Value.Lifecycle));
            Assert.False(restore.Runtime.IsRestoring);
            var replay = restore.Runtime.RequestSurface(new(new("surface-before-delete", provider.SessionId, restore.Runtime.CaptureCatalog().Revision), id));
            Assert.Equal(CelestialOperationStatus.Rejected, replay.Operation.Status);
            Assert.Contains(replay.Operation.Errors, error => error.Code == "SurfaceRetired");
            var regenerated = restore.Runtime.RequestSurface(new(new("surface-after-delete", provider.SessionId, restore.Runtime.CaptureCatalog().Revision), id));
            Assert.Equal(CelestialOperationStatus.Applied, regenerated.Operation.Status);
            Assert.Equal(CelestialOperationStatus.Rejected, restore.Runtime.RequestSurface(new(new("surface-before-delete", provider.SessionId, restore.Runtime.CaptureCatalog().Revision), id)).Operation.Status);
        }
        finally { Scribe.ForceStop(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void NativeSurfaceRemovalCancelsQueuedMapGenerationBeforeAnyNativeSideEffect()
    {
        using var fixture = new CelestialMapIdentityTests.Fixture();
        var runtime = fixture.Scope.Runtime;
        var id = fixture.Scope.Manifest.Items.First(item => item.Id != fixture.Root.Header.OwnerId).Id;
        var surface = runtime.RequestSurface(new(new("surface", runtime.WorldSessionId, runtime.CaptureCatalog().Revision), id)).Handle;
        var address = new CelestialMapAddress(id, surface.LayerId, 0);
        CelestialMapOperation queued = null;
        var worker = new System.Threading.Thread(() => queued = runtime.RequestMap(new(new("queued-map", runtime.WorldSessionId, runtime.CaptureCatalog().Revision), address, CelestialMapAction.Generate, 250, 250)));
        worker.Start(); worker.Join();
        Assert.Equal(CelestialOperationStatus.Pending, queued.Operation.Status);
        fixture.Native.Grid.RemovePlanetLayer(fixture.Native.Grid.PlanetLayers[surface.LayerId]);
        runtime.ProcessHostWork();
        Assert.Equal(0, fixture.Backend.Created);
        Assert.Equal(0, fixture.Backend.Generated);
        var receipt = runtime.GetOperation("queued-map");
        Assert.Equal(CelestialOperationStatus.Rejected, receipt.Status);
        Assert.Contains(receipt.Errors, error => error.Code == "SurfaceIdentityMismatch");
    }
}
