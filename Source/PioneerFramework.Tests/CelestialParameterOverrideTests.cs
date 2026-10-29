using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialParameterOverrideTests
{
    [Fact]
    public void DataOnlyOverrideCreatesFrozenPhysicalParametersAndSnapshotsCallerLists()
    {
        var runtime = CelestialBenchFactory.Create(3);
        var service = new CelestialWorldServices(runtime).Bodies;
        var overrides = new[] { new CelestialParameterOverride("shape", "radiusMeters", Value: 77) };
        var request = new CelestialCreateBatchRequest(new("override", runtime.WorldSessionId, runtime.Catalog.Revision), new[] {
            new CelestialCreateItem("probe", "PF_BenchBody", InitializationReferences: new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = CelestialBenchFactory.Id(0) }, ParameterOverrides: overrides) });
        overrides[0] = new("shape", "radiusMeters", Value: 99);
        var created = service.CreateBatch(request);
        Assert.Equal(CelestialOperationStatus.Pending, created.Status);
        var id = Assert.Single(created.InstanceIds); Assert.Equal(77, service.Query(id).Value.Description.RadiusMeters);
        CelestialBenchFactory.Advance(runtime, runtime.CurrentStamp.PhysicalSeconds);
        Assert.Equal(CelestialOperationStatus.Applied, service.GetOperation(created.OperationId).Status);
        var saved = new CelestialSaveAdapter(runtime).CaptureCheckpoint().Value;
        var decoded = CelestialSaveCodec.Decode(CelestialSaveCodec.Encode(saved).Value).Value;
        Assert.Equal(77, System.Linq.Enumerable.Single(decoded.Bodies, b => b.Description.Id == id).Description.RadiusMeters);
        Assert.Equal(CelestialOperationStatus.Applied, service.CreateBatch(request).Status);
    }

    [Fact]
    public void RangesUseTheExistingDeterministicSamplerAndUnknownOrInvalidOverridesReject()
    {
        var runtime = CelestialBenchFactory.Create(3); var service = new CelestialWorldServices(runtime).Bodies;
        CelestialOperation Create(string name, CelestialParameterOverride value) => service.Create(new(new(name, runtime.WorldSessionId, runtime.Catalog.Revision), "PF_BenchBody", "probe",
            InitializationReferences: new Dictionary<string, CelestialInstanceId> { ["frameOrigin"] = CelestialBenchFactory.Id(0) }, ParameterOverrides: new[] { value }));
        var first = Create("range", new("shape", "radiusMeters", Min: 77, Max: 88));
        Assert.Equal(CelestialOperationStatus.Pending, first.Status);
        Assert.InRange(service.Query(Assert.Single(first.InstanceIds)).Value.Description.RadiusMeters, 77, 88);
        Assert.Equal(first, Create("range", new("shape", "radiusMeters", Min: 77, Max: 88)));
        Assert.Equal("UnknownParameterOverride", Assert.Single(Create("typo", new("shape", "radiusTypo", Value: 77)).Errors).Code);
        Assert.Equal("InvalidParameterOverride", Assert.Single(Create("bad", new("shape", "radiusMeters", Value: 1, Min: 2, Max: 3)).Errors).Code);
        var context = new CelestialGenerationContext(1, "integer", parameterOverrides: new[] { new CelestialParameterOverride("comp", "count", Min: 2, Max: 4) }).ForComp("comp");
        Assert.InRange(context.SampleInt("count", IntParameterSpec.Fixed(99)), 2, 4);
        var invalid = new CelestialGenerationContext(1, "integer", parameterOverrides: new[] { new CelestialParameterOverride("comp", "count", Value: 2.5) }).ForComp("comp");
        Assert.Throws<ArgumentException>(() => invalid.SampleInt("count", IntParameterSpec.Fixed(99)));
    }
}
