using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class LaunchTransitPathTests
{
    [Fact]
    public void Position_UsesExactEndpoints()
    {
        var center = new DVector3(200, 30, -50);
        var start = center + new DVector3(100, 0, 0);
        var target = center + new DVector3(0, 0, 140);

        Assert.Equal(start, LaunchTransitPath.Position(center, start, target, 0));
        Assert.Equal(target, LaunchTransitPath.Position(center, start, target, 1));
    }

    [Fact]
    public void Position_RemainsOutsidePlanetForFarSideTarget()
    {
        var center = DVector3.zero;
        var start = new DVector3(100, 0, 0);
        var target = new DVector3(-140, 0, 0);

        for (int i = 0; i <= 100; i++)
        {
            DVector3 position = LaunchTransitPath.Position(center, start, target, i / 100.0);
            Assert.True(position.magnitude >= 100.0 - 1e-8, $"path entered planet at step {i}: {position.magnitude}");
            Assert.False(double.IsNaN(position.x) || double.IsNaN(position.y) || double.IsNaN(position.z));
        }
    }

    [Fact]
    public void MovingBodyPathFollowsCurrentCenterAndKeepsValidArrivalEndpoint()
    {
        var launchCenter = new DVector3(80_000_000, 0, 0);
        var start = launchCenter + new DVector3(0, 0, 1_500_000);
        var target = launchCenter + new DVector3(0, 0, 1_800_000);
        double angle = 2 * Math.PI / 317_900 * 60;
        var currentCenter = new DVector3(80_000_000 * Math.Cos(angle), 0,
            80_000_000 * Math.Sin(angle));

        DVector3 position = LaunchTransitPath.PositionFromMovingBody(
            launchCenter, currentCenter, start, target, 0.1);

        Assert.True((position - currentCenter).magnitude >= 1_500_000 - 1e-6);
        Assert.Equal(start, LaunchTransitPath.PositionFromMovingBody(launchCenter,
            launchCenter, start, target, 0));
        Assert.Equal(target, LaunchTransitPath.PositionFromMovingBody(launchCenter,
            currentCenter, start, target, 1));
    }

    [Fact]
    public void MovingBodyPathKeepsMarkerOutsideIfTargetHasMovedInsideBody()
    {
        var launchCenter = new DVector3(80_000_000, 0, 0);
        var start = launchCenter + new DVector3(0, 0, 1_500_000);
        var target = launchCenter + new DVector3(0, 0, 1_800_000);
        double angle = 2 * Math.PI / 317_900 * 600;
        var currentCenter = new DVector3(80_000_000 * Math.Cos(angle), 0,
            80_000_000 * Math.Sin(angle));

        Assert.True((target - currentCenter).magnitude < 1_500_000);
        for (int i = 0; i <= 100; i++)
        {
            DVector3 position = LaunchTransitPath.PositionFromMovingBody(
                launchCenter, currentCenter, start, target, i / 100.0);
            Assert.True((position - currentCenter).magnitude >= 1_500_000 - 1e-6);
        }
    }

    [Theory]
    [InlineData(99, 0)]
    [InlineData(100, 0)]
    [InlineData(150, 0.5)]
    [InlineData(200, 1)]
    [InlineData(250, 1)]
    public void Progress_ClampsAndResumesFromSavedTicks(int now, double expected)
    {
        Assert.Equal(expected, LaunchTransitPath.Progress(100, 100, now), 10);
    }
}
