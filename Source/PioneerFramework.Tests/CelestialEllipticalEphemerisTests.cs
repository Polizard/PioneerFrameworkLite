using System;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public sealed class CelestialEllipticalEphemerisTests
{
    [Fact]
    public void PeriapsisAndApoapsisHaveExpectedPositionAndVelocity()
    {
        var parsed = Parse();
        const double a = 1000;
        const double e = .4;
        double n = 2 * Math.PI / 80;
        var periapsis = parsed.SampleParent(20);
        var apoapsis = parsed.SampleParent(60);

        Assert.Equal(new DVector3(a * (1 - e), 0, 0), periapsis.Position);
        Assert.Equal(-n * a * Math.Sqrt((1 + e) / (1 - e)), periapsis.Velocity.z, 10);
        Assert.Equal(-a * (1 + e), apoapsis.Position.x, 10);
        Assert.Equal(n * a * Math.Sqrt((1 - e) / (1 + e)), apoapsis.Velocity.z, 10);
        Assert.True((parsed.SampleParent(100).Position - periapsis.Position).magnitude < 1e-9);
    }

    [Fact]
    public void RetrogradeOrbitReversesVelocityAndSupportsInclination()
    {
        var normal = new DVector3(0, 1, 1).normalized;
        var direct = Parse(normal: normal);
        var reverse = Parse(normal: normal, retrograde: true);
        var position = direct.SampleParent(27).Position;
        Assert.Equal(direct.SampleParent(20).Velocity, -reverse.SampleParent(20).Velocity);
        Assert.True((reverse.SampleParent(13).Position - position).magnitude < 1e-9);
        Assert.InRange(Math.Abs(DVector3.Dot(position, normal)), 0, 1e-9);
    }

    [Theory]
    [InlineData(-.2)]
    [InlineData(1)]
    [InlineData(1.2)]
    public void UnboundOrInvalidEccentricityIsRejected(double e)
    {
        var data = ExistingEphemerisParameters.CreateEllipticalInitialization(Motion(), e, 20, DVector3.right, false);
        Assert.False(ExistingEphemerisParameters.Parse(data).Success);
    }

    private static ExistingEphemerisParameters Parse(DVector3? normal = null, bool retrograde = false)
    {
        var motion = Motion();
        var axis = normal ?? DVector3.up;
        motion.orbitNormalX = axis.x;
        motion.orbitNormalY = axis.y;
        motion.orbitNormalZ = axis.z;
        var parsed = ExistingEphemerisParameters.Parse(ExistingEphemerisParameters.CreateEllipticalInitialization(
            motion, .4, 20, DVector3.right, retrograde));
        Assert.True(parsed.Success, string.Join(", ", parsed.Errors));
        return parsed.Value;
    }

    private static CelestialMotionXml Motion() => new()
    {
        orbitRadiusMeters = 1000,
        orbitPeriodSeconds = 80,
        rotation = new() { periodSeconds = 42 }
    };
}
