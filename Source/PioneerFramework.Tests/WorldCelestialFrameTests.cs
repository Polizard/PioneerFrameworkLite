using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public sealed class WorldCelestialFrameTests
{
    [Fact]
    public void NonzeroStarAndTranslatedSystemGiveIdenticalDrawAndPickCoordinates()
    {
        var shift = new DVector3(1e12, -2e12, 3e12);
        var focus = new DVector3(5010, -3020, 1030);
        var nativeOrigin = new DVector3(9, 11, 15);
        var star = new DVector3(29, 71, 193);
        var rotation = DQuaternion.AngleAxis(.8, DVector3.up);
        var frame = new WorldCelestialFrame(focus, nativeOrigin, rotation, .25);
        var moved = frame with { PhysicsAnchor = focus + shift };

        Assert.Equal(nativeOrigin, frame.ToView(focus));
        Assert.NotEqual(DVector3.zero, frame.ToView(star));
        Assert.Equal(frame.ToView(star), moved.ToView(star + shift));
        Assert.True((frame.ToPhysics(frame.ToView(star)) - star).magnitude < 1e-9);
    }

    [Fact]
    public void ScaleThresholdUsesHysteresisAndPointerDirection()
    {
        Assert.False(WorldCelestialFrame.ShouldEnterSystem(.010, false));
        Assert.True(WorldCelestialFrame.ShouldEnterSystem(.009, false));
        Assert.False(WorldCelestialFrame.ShouldEnterSystem(.009, true));
        Assert.False(WorldCelestialFrame.CanRearm(.013));
        Assert.True(WorldCelestialFrame.CanRearm(.015));
    }

    [Fact]
    public void RayHitsTheSameSurfacePointAfterTranslatedProjection()
    {
        var rotation = DQuaternion.AngleAxis(1.1, new DVector3(1, 2, 3));
        var center = new DVector3(5010, -3020, 1030);
        var frame = new WorldCelestialFrame(center + new DVector3(4, 0, 2), new DVector3(6, -1, 2), rotation, .3);
        var camera = frame.ToView(center + DVector3.forward * 18);
        var target = frame.ToView(center);
        var ray = (target - camera).normalized;
        double hit = WorldCelestialFrame.RaySphere(camera, ray, target, 4 * frame.Scale);
        var physicalHit = frame.ToPhysics(camera + ray * hit);

        Assert.True((physicalHit - center - DVector3.forward * 4).magnitude < 1e-10);
        Assert.Equal(double.PositiveInfinity, WorldCelestialFrame.RaySphere(camera, -ray, target, 4 * frame.Scale));
    }
}
