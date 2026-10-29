using System.Linq;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialVisualCapabilityTests
{
    [Fact]
    public void BuiltinVisualHandlersRestoreExactResolvedProfiles()
    {
        var fixture = new CelestialViewAdapterTests.Fixture();
        var registry = CelestialBuiltinCompRegistry.Create();
        var records = fixture.Runtime.Catalog.CaptureInstances().SelectMany(instance => instance.CompStateRecords.Values)
            .Where(record => record.HandlerId.StartsWith("PFL.Visual.", System.StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(records);
        foreach (var record in records)
        {
            var restored = CelestialCompStateCodec.Decode(record, registry).Value;
            Assert.True(restored.IsReady);
            Assert.Equal(record.ConfigurationPayload.ToArray(), restored.Properties.Serialize().ToArray());
            restored.Comp.Release();
        }
    }
    [Fact]
    public void VisualEntriesFollowExplicitComponentsForNonEarthCloudsAndMultipleStars()
    {
        var f = new CelestialViewAdapterTests.Fixture();
        var one = f.Adapter.Capture(CelestialViewAdapterTests.Fixture.One, f.Runtime.CurrentStamp).Value;
        var two = f.Adapter.Capture(CelestialViewAdapterTests.Fixture.Two, f.Runtime.CurrentStamp).Value;
        Assert.Contains(one.Visuals, visual => visual.Kind == CelestialVisualKind.Clouds);
        Assert.DoesNotContain(two.Visuals, visual => visual.Kind == CelestialVisualKind.Clouds);
        var stars = new[] { CelestialViewAdapterTests.Fixture.StarOne, CelestialViewAdapterTests.Fixture.StarTwo }
            .Select(id => f.Adapter.Capture(id, f.Runtime.CurrentStamp).Value).ToArray();
        Assert.All(stars, star => Assert.Single(star.Visuals.Where(visual => visual.Kind == CelestialVisualKind.Stellar)));
        Assert.NotEqual(stars[0].CacheKey, stars[1].CacheKey);
        Assert.All(stars, star => Assert.DoesNotContain(star.Visuals, visual => visual.Kind == CelestialVisualKind.Clouds));
    }
}
