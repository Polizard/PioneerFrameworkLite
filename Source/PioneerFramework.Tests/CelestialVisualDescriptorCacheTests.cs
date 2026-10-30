using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialVisualDescriptorCacheTests
{
    [Fact]
    public void ExplicitVisualRevisionInvalidatesOnlyItsOwnerAndBatchMatchesIndividualSamples()
    {
        var runtime = CelestialBenchFactory.Create(3, visuals: true, transform: template => {
            var descriptor = template.Components.Single(item => item.Descriptor.Key == "visual").Descriptor;
            var profile = new VisualResolvedProperties("visual", CelestialVisualKind.Surface,
                new CelestialVisualProfileDef { defName = "PF_CacheTest" }.ToProfile().Value);
            return template.WithOverrides(new[] { new CelestialCompDefinition(descriptor, new VisualFactory(descriptor),
                _ => CelestialResult.Ok<ICelestialResolvedProperties>(profile)) }).Value;
        });
        var ids = Enumerable.Range(0, 3).Select(CelestialBenchFactory.Id).ToArray();
        var adapter = new CelestialViewAdapter(runtime);
        var first = adapter.CapturePresentationBatch(ids, runtime.CurrentStamp, 0);
        using var measurement = CelestialCostCounters.Measure();
        Assert.IsType<MutableVisual>(runtime.GetCapabilities<ICelestialVisualCapability>(ids[1]).Single()).Revision++;
        var next = adapter.CapturePresentationBatch(ids, runtime.CurrentStamp, 0);
        Assert.Same(first[0].Value.Visuals, next[0].Value.Visuals);
        Assert.NotSame(first[1].Value.Visuals, next[1].Value.Visuals);
        Assert.Same(first[2].Value.Visuals, next[2].Value.Visuals);
        Assert.Equal(1, CelestialCostCounters.Snapshot().VisualHashes);
        for (int i = 0; i < ids.Length; i++) Assert.Equal(next[i].Value.Motion, adapter.CapturePresentation(ids[i], 0).Value.Motion);
        Assert.All(adapter.CapturePresentationBatch(ids, runtime.CurrentStamp, 1), result => Assert.Equal("TimeUnavailable", result.Errors.Single().Code));
    }
    private sealed class VisualFactory(CelestialCompDescriptor descriptor) : ICelestialCompFactory
    { public CelestialBodyComp Create(ICelestialResolvedProperties properties) => new MutableVisual(descriptor); }
    private sealed class MutableVisual(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor), ICelestialVisualCapability
    {
        internal long Revision;
        public long VisualRevision => Revision;
        public CelestialVisualDescription GetVisualDescription() => new(Descriptor.Key, CelestialVisualKind.Surface, ((VisualResolvedProperties)Context.Properties).Profile);
    }
    [Fact]
    public void ThousandFramesReuseStaticVisualsWhileMotionSamplingRemainsAuthoritative()
    {
        var runtime = CelestialBenchFactory.Create(3, visuals: true);
        var adapter = new CelestialViewAdapter(runtime);
        var id = CelestialBenchFactory.Id(1);
        using var measurement = CelestialCostCounters.Measure();
        var first = adapter.CapturePresentation(id, 0).Value;
        for (int frame = 1; frame <= 1000; frame++)
        {
            CelestialBenchFactory.Advance(runtime, frame);
            var current = adapter.CapturePresentation(id, frame).Value;
            Assert.Equal(frame, current.Stamp.PhysicalSeconds);
            Assert.Equal(first.CacheKey, current.CacheKey);
            Assert.Same(first.Visuals, current.Visuals);
        }
        var cost = CelestialCostCounters.Snapshot();
        Assert.Equal(1, cost.VisualProfileSerializations);
        Assert.Equal(1, cost.VisualHashes);
        Assert.Equal(0, cost.ConfigurationSerializations);
    }
}
