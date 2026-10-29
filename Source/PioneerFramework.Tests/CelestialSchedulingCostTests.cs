using System;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialSchedulingCostTests
{
    [Fact]
    public void ThousandCommitsDoNotEncodeStaticConfigurationOrCaptureImplicitState()
    {
        var runtime = CelestialBenchFactory.Create(32, stateful: true);
        using var measurement = CelestialCostCounters.Measure();
        for (int tick = 1; tick <= 1000; tick++) CelestialBenchFactory.Advance(runtime, tick);
        var steady = CelestialCostCounters.Snapshot();
        Assert.Equal(0, steady.ConfigurationSerializations);
        Assert.Equal(0, steady.StateCaptures);
        Assert.Equal(0, steady.CatalogSorts);
        var checkpoint = new CelestialSaveAdapter(runtime).CaptureCheckpoint();
        Assert.True(checkpoint.Success);
        Assert.All(checkpoint.Value.Bodies, body => Assert.Equal(1000,
            BitConverter.ToInt64(body.Components.Single(record => record.Key == "counter").StatePayload.Span)));
        Assert.Equal(0, CelestialCostCounters.Snapshot().ConfigurationSerializations);
    }
}
