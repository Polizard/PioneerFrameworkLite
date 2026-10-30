using System;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialCostProbeTests
{
    [Fact]
    public void MeasurementsObserveActualEncodingWithoutChangingThePayload()
    {
        var runtime = CelestialBenchFactory.Create(3, stateful: true);
        var id = CelestialBenchFactory.Id(0);
        var component = (CelestialBodyComp)runtime.GetCapabilities<ICelestialGeometryCapability>(id).Single();
        var properties = runtime.Creation.GetProperties(id).Single(item => item.Key == "shape");
        var before = CelestialCompStateCodec.Encode(component, properties);
        using (CelestialCostCounters.Measure())
        {
            var measured = CelestialCompStateCodec.Encode(component, properties);
            var cost = CelestialCostCounters.Snapshot();
            Assert.Equal(0, cost.ConfigurationSerializations);
            Assert.Equal(1, cost.StateCaptures);
            Assert.Equal(before.ConfigurationPayload.ToArray(), measured.ConfigurationPayload.ToArray());
            Assert.Equal(before.StatePayload.ToArray(), measured.StatePayload.ToArray());
        }
        var stopped = CelestialCostCounters.Snapshot();
        CelestialCompStateCodec.Encode(component, properties);
        Assert.Equal(stopped, CelestialCostCounters.Snapshot());
    }

    [Fact]
    public void RealScheduledStateRemainsCurrentAtTheExplicitCheckpoint()
    {
        var runtime = CelestialBenchFactory.Create(3, stateful: true);
        using var measurement = CelestialCostCounters.Measure();
        for (int index = 1; index <= 4; index++) CelestialBenchFactory.Advance(runtime, index);
        var document = new CelestialSaveAdapter(runtime).CaptureCheckpoint().Value;
        Assert.Equal(4, CelestialCostCounters.Snapshot().Advances);
        Assert.All(document.Bodies, body => Assert.Equal(4,
            BitConverter.ToInt64(body.Components.Single(record => record.Key == "counter").StatePayload.Span)));
    }
}
