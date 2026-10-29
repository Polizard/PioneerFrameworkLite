using System.Linq;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialRenderProjectionTests
{
    [Fact]
    public void NativeRenderInputsAreDetachedInstanceFactsAndEffectsComeFromComponents()
    {
        var fixture = new CelestialViewAdapterTests.Fixture();
        var ordinary = fixture.Adapter.Capture(CelestialViewAdapterTests.Fixture.One, fixture.Runtime.CurrentStamp).Value;
        var stellar = fixture.Adapter.Capture(CelestialViewAdapterTests.Fixture.StarTwo, fixture.Runtime.CurrentStamp).Value;
        var first = CelestialRenderProjection.Create(ordinary, default, 0);
        var second = CelestialRenderProjection.Create(stellar, default, 0);
        Assert.Equal(1000, first.radius); Assert.Equal(ordinary.Description.Handle, first.bodyId);
        Assert.Equal(CelestialBodyType.Planet, first.bodyType);
        Assert.Equal(CelestialBodyType.Star, second.bodyType);
        var changed = first with { radius = 1 };
        Assert.Equal(1, changed.radius);
        Assert.Equal(1000, first.radius);
        Assert.Equal(1000, ordinary.Description.RadiusMeters);
        Assert.Equal(1000, fixture.Runtime.Query(ordinary.Description.Id).Value.Description.RadiusMeters);
        Assert.Equal(ordinary.Visuals.Single(visual => visual.Kind == CelestialVisualKind.Surface).Profile.Red, first.baseSphereColor.r, 6);
    }
}
