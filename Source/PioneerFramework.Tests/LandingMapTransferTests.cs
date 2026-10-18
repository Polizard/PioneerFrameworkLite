using PioneerFramework.Landing;
using Xunit;
namespace PioneerFramework.Tests;
public class LandingMapTransferTests
{
    [Fact]
    public void CompletedLedgerWithTargetEvidenceSkipsTransfer()
    {
        var source = new LandingSourceMapData { SourceKey = "first" };
        Assert.True(LandingMapTransferService.ReconcileEntry(source, true, true));
        Assert.Equal(LandingMapStatus.Transferred, source.Status);
    }
    [Fact]
    public void LedgerWithoutEvidenceCannotBecomeCompleted()
    {
        var source = new LandingSourceMapData();
        Assert.False(LandingMapTransferService.ReconcileEntry(source, true, false));
        Assert.Equal(LandingMapStatus.Pending, source.Status);
    }
    [Fact]
    public void ClaimedTransferWithoutLedgerIsBlocked()
    {
        var source = new LandingSourceMapData { Status = LandingMapStatus.Transferred };
        Assert.False(LandingMapTransferService.ReconcileEntry(source, false, false));
    }
    [Fact]
    public void InterruptedTransferReconcilesEvidenceBeforeChoosingNextMap()
    {
        var task = new LandingTaskData { Status = LandingTransitStatus.Transferring };
        task.SourceMaps.Add(new LandingSourceMapData { SourceKey = "first" });
        task.SourceMaps.Add(new LandingSourceMapData { SourceKey = "second" });
        Assert.True(LandingMapTransferService.Reconcile(task, s => s.SourceKey == "first" ? (true, false, true) : (false, false, false)).Success);
        Assert.Equal(LandingMapStatus.Transferred, task.SourceMaps[0].Status);
        Assert.Equal(LandingMapStatus.Pending, task.SourceMaps[1].Status);
        Assert.Equal(LandingTransitStatus.AwaitingPlacement, task.Status);
    }
    [Fact]
    public void InterruptedLastTransferResumesCompletionWithoutRecollectingEmptyDeck()
    {
        var task = new LandingTaskData { Status = LandingTransitStatus.Transferring };
        task.SourceMaps.Add(new LandingSourceMapData());
        Assert.True(LandingMapTransferService.Reconcile(task, _ => (true, false, true)).Success);
        Assert.Equal(LandingTransitStatus.Completing, task.Status);
    }
}
