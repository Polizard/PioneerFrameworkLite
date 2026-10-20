using System;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewCloudsTests
{
    [Theory]
    [InlineData(105.0, 100.0, 80.0)]
    [InlineData(105.0, 50.0, 40.0)]
    [InlineData(10.0, 100.0, 10.0)]
    [InlineData(5000.0, 1000000.0, 5000.0)]
    [InlineData(105.0, 0.0, 105.0)]
    public void NearClipPreservesVisibleCloudsWithoutMovingAnAlreadyCloserPlane(double near, double gap, double expected)
        => Assert.Equal(expected, PFViewClouds.LimitNearClip(near, gap, 0.01), 9);

    [Fact]
    public void CloudTrianglesFaceOutwardAndStayAboveEvenTheLargestSurfacePatchOffset()
    {
        PFViewClouds.BuildShellGeometry(out var vertices, out var triangles);
        double minimumRadius = double.MaxValue;
        int validTriangles = 0;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            DVector3 a = vertices[triangles[i]];
            DVector3 b = vertices[triangles[i + 1]];
            DVector3 c = vertices[triangles[i + 2]];
            DVector3 normal = DVector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 1e-20) continue;
            double signedDistance = DVector3.Dot(normal.normalized, a);
            Assert.True(signedDistance > 0.0, "Back-face culling must retain the outside of the cloud sphere");
            minimumRadius = Math.Min(minimumRadius, signedDistance * 10020000.0);
            validTriangles++;
        }
        Assert.True(validTriangles > 0);
        // Maximum baked patch offset is ~6.1 km for thetaMax=pi and 64 rings.
        Assert.True(minimumRadius > 5006100.0, $"Cloud mesh intersects the surface: radius={minimumRadius}");
    }

    [Theory]
    [InlineData(5000000.0, 1.002)]
    [InlineData(1000000.0, 1.01)]
    [InlineData(0.0, 1.0)]
    public void CloudShellStaysTenKilometersAboveTheRenderedSurface(double radius, double expected)
        => Assert.Equal(expected, PFViewClouds.RadiusFactor(radius), 12);

    [Theory]
    [InlineData(-1000.0, 0.0)]
    [InlineData(10000.0, 0.0)]
    [InlineData(11250.0, 0.15625)]
    [InlineData(12500.0, 0.5)]
    [InlineData(13750.0, 0.84375)]
    [InlineData(15000.0, 1.0)]
    [InlineData(1000000.0, 1.0)]
    [InlineData(double.NaN, 0.0)]
    public void CloudsFadeOutBeforeTheCameraEntersTheShell(double altitude, double expected)
        => Assert.Equal(expected, PFViewClouds.Opacity(altitude), 12);

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(150000.0, 0.25)]
    [InlineData(600000.0, 0.0)]
    [InlineData(-150000.0, 0.75)]
    [InlineData(600000000150000.0, 0.25)]
    public void DriftPhaseWrapsWithoutLosingPrecisionInLongSaves(double tick, double expected)
        => Assert.Equal(expected, PFViewClouds.AnimationPhase(tick, 600000.0), 12);

    [Fact]
    public void RepeatedRenderAtAPausedTickCannotAdvanceTheClouds()
    {
        double initial = PFViewClouds.AnimationPhase(123456.75, 6000.0);
        for (int frame = 0; frame < 120; frame++)
            Assert.Equal(initial, PFViewClouds.AnimationPhase(123456.75, 6000.0));
        Assert.NotEqual(initial, PFViewClouds.AnimationPhase(123457.0, 6000.0));
    }

    [Theory]
    [InlineData(double.NaN, 6000.0)]
    [InlineData(double.PositiveInfinity, 6000.0)]
    [InlineData(123.0, 0.0)]
    [InlineData(123.0, double.PositiveInfinity)]
    public void InvalidClockInputsCannotSendNaNsToTheShader(double tick, double period)
        => Assert.Equal(0.0, PFViewClouds.AnimationPhase(tick, period));
}
