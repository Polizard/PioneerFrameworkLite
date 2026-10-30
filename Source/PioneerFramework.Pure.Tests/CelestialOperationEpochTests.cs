using System;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class CelestialOperationEpochTests
{
    [Fact]
    public void ClosingReclaimsReceiptsAndRejectsEveryOldReplay()
    {
        var store = new CelestialOperationStore(Guid.NewGuid());
        var context = new CelestialOperationContext("old", store.WorldSessionId, 0);
        Assert.True(store.Begin(context, "hash", 0, Array.Empty<CelestialInstanceId>()).Success);
        Assert.Equal("EpochHasPendingOperations", Assert.Single(store.CloseEpoch(0).Errors).Code);
        store.Update(new("old", CelestialOperationStatus.Applied));
        Assert.True(store.CloseEpoch(0).Success);
        Assert.Empty(store.Capture());
        Assert.Equal("OperationExpired", Assert.Single(store.Begin(context, "hash", 0, Array.Empty<CelestialInstanceId>()).Errors).Code);
        Assert.Equal("OperationExpired", Assert.Single(store.Get("old").Errors).Code);
        Assert.Equal(1, store.OpenEpoch().Value);
    }
    [Fact]
    public void HundredThousandTerminalOperationsHaveBoundedRetentionAndConflictChecks()
    {
        var store = new CelestialOperationStore(Guid.NewGuid());
        long epoch = 0;
        for (int index = 0; index < 100000; index++)
        {
            if (index != 0 && index % 4000 == 0) { Assert.True(store.CloseEpoch(epoch).Success); epoch = store.OpenEpoch().Value; }
            string id = CelestialOperationEpochs.Qualify(epoch, "op/" + index);
            var context = new CelestialOperationContext(id, store.WorldSessionId, 0);
            Assert.True(store.Begin(context, "hash", 0, Array.Empty<CelestialInstanceId>()).Value.IsNew);
            store.Update(new(id, CelestialOperationStatus.Applied));
            Assert.False(store.Begin(context, "hash", 0, Array.Empty<CelestialInstanceId>()).Value.IsNew);
            Assert.Equal("OperationConflict", Assert.Single(store.Begin(context, "different", 0, Array.Empty<CelestialInstanceId>()).Errors).Code);
        }
        Assert.InRange(store.Capture().Count, 1, 4000);
        Assert.True(store.CloseEpoch(epoch).Success); Assert.Empty(store.Capture());
    }
    [Fact]
    public void OpenPendingAndRetainedLimitsRejectWithoutRecordingExtraReceipts()
    {
        var store = new CelestialOperationStore(Guid.NewGuid());
        for (int index = 1; index < 8; index++) Assert.True(store.OpenEpoch().Success);
        Assert.Equal("EpochLimitReached", Assert.Single(store.OpenEpoch().Errors).Code);
        for (int index = 0; index < 256; index++) Assert.True(store.Begin(new("op/" + index, store.WorldSessionId, 0), "hash", 0, Array.Empty<CelestialInstanceId>()).Success);
        Assert.Equal("EpochPendingLimitReached", Assert.Single(store.Begin(new("extra", store.WorldSessionId, 0), "hash", 0, Array.Empty<CelestialInstanceId>()).Errors).Code);
        for (int index = 0; index < 256; index++) store.Update(new("op/" + index, CelestialOperationStatus.Applied));
        for (int index = 256; index < 4096; index++)
        {
            string id = "op/" + index;
            Assert.True(store.Begin(new(id, store.WorldSessionId, 0), "hash", 0, Array.Empty<CelestialInstanceId>()).Success);
            store.Update(new(id, CelestialOperationStatus.Applied));
        }
        Assert.Equal("EpochReceiptLimitReached", Assert.Single(store.Begin(new("extra", store.WorldSessionId, 0), "hash", 0, Array.Empty<CelestialInstanceId>()).Errors).Code);
        Assert.Equal(4096, store.Capture().Count);
    }
}
