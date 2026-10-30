using System;
using System.Linq;
using System.Threading;
using PioneerFramework.Celestial;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialConcurrentNativeRequestsTests
{
    [Fact]
    public void DistinctQueuedSurfaceRequestsShareOneOwnedLayerAndRemainSaveable()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var runtime = f.Scope.Runtime;
        var id = f.Scope.Manifest.Items.First(item => item.Id != f.Root.Header.OwnerId).Id;
        var worker = new Thread(() => {
            foreach (string operation in new[] { "one", "two" })
                Assert.Equal(CelestialOperationStatus.Pending, runtime.RequestSurface(new(new(operation, runtime.WorldSessionId, runtime.CaptureCatalog().Revision), id)).Operation.Status);
        });
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
        runtime.ProcessHostWork();
        Assert.Equal(CelestialOperationStatus.Applied, runtime.GetOperation("one").Status);
        Assert.Equal(CelestialOperationStatus.Applied, runtime.GetOperation("two").Status);
        Assert.Equal(2, f.Native.Grid.PlanetLayers.Count);
        var checkpoint = new CelestialSaveAdapter(runtime).CaptureCheckpoint();
        Assert.True(checkpoint.Success);
        Assert.Equal(f.Native.Grid.PlanetLayers.Count, checkpoint.Value.Surfaces.Count);
        var handles = new[] { "one", "two" }.Select(operation => runtime.RequestSurface(new(new(operation, runtime.WorldSessionId, runtime.CaptureCatalog().Revision), id)).Handle).ToArray();
        Assert.NotNull(handles[0]); Assert.Equal(handles[0], handles[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctQueuedMapRequestsShareOneParentAndMapAndKeepIndependentReceipts(bool existingParent)
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        if (existingParent) f.Backend.CreateParent(f.Address);
        var worker = new Thread(() => {
            foreach (string operation in new[] { "one", "two" })
                Assert.Equal(CelestialOperationStatus.Pending, f.Request(operation, CelestialMapAction.Generate).Operation.Status);
        });
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
        f.Scope.Runtime.ProcessHostWork();
        Assert.Equal(1, f.Backend.Created);
        Assert.Equal(1, f.Backend.Generated);
        Assert.Single(Current.Game.Maps);
        Assert.Equal(f.Request("one", CelestialMapAction.Generate).Handle, f.Request("two", CelestialMapAction.Generate).Handle);
        Assert.Equal(CelestialOperationStatus.Applied, f.Scope.Runtime.GetOperation("two").Status);
        Assert.True(new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint().Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASecondQueuedMapRequestCannotRegenerateAPartiallyFailedNativeMap(bool existingParent)
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        if (existingParent) f.Backend.CreateParent(f.Address);
        f.Backend.MakeMapThenFail = true;
        f.Backend.Fail = true;
        var worker = new Thread(() => {
            foreach (string operation in new[] { "partial-one", "partial-two" }) f.Request(operation, CelestialMapAction.Generate);
        });
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(2)));
        f.Scope.Runtime.ProcessHostWork();
        Assert.Equal(1, f.Backend.Generated);
        Assert.Single(Current.Game.Maps);
        Assert.Equal(CelestialOperationStatus.Rejected, f.Scope.Runtime.GetOperation("partial-one").Status);
        Assert.Contains(f.Scope.Runtime.GetOperation("partial-two").Errors, error => error.Code == "MapGenerationIncomplete");
    }
}
