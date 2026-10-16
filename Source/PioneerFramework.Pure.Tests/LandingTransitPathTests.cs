using System;
using PioneerFramework;
using PioneerFramework.Landing;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class LandingTransitPathTests
{
    [Theory]
    [InlineData(90, 0)] [InlineData(100, 0)] [InlineData(190, .5)] [InlineData(280, 1)] [InlineData(300, 1)]
    public void ProgressClampsAtEndpoints(int tick, double expected) =>
        Assert.Equal(expected, LandingTransitPath.Progress(100, 180, tick), 10);

    [Theory]
    [InlineData(1, 0, 0)] [InlineData(-1, 0, 0)] [InlineData(0, 0, 1)]
    public void PathKeepsOutsideAndTracksMovingCenter(double x, double y, double z)
    {
        var departure = new DVector3(500, 0, 0);
        var start = departure + new DVector3(160, 0, 0);
        for (int i = 0; i <= 100; i++)
        {
            double p = i / 100.0;
            var center = departure + new DVector3(10 * p, 20 * p, 0);
            var target = center + new DVector3(x, y, z) * 100;
            var actual = LandingTransitPath.Position(departure, center, start, target, 100, p);
            Assert.True((actual - center).magnitude >= 100 - 1e-8);
            Assert.True(double.IsFinite(actual.x + actual.y + actual.z));
            if (i == 0) Assert.Equal(start, actual);
            if (i == 100) Assert.Equal(target, actual);
        }
    }
}
