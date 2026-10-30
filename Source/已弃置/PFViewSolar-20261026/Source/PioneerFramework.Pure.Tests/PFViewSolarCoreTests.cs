using System;
using System.Reflection;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewSolarCoreTests
{
    [Theory]
    [InlineData(0d, -.2d, .98d)]
    [InlineData(1d, 0d, 0d)]
    [InlineData(-1d, 0d, 0d)]
    [InlineData(0d, 1d, 0d)]
    [InlineData(0d, -1d, 0d)]
    [InlineData(0d, 0d, 1d)]
    [InlineData(0d, 0d, -1d)]
    [InlineData(3d, -5d, 7d)]
    [InlineData(1e300, -2e300, 3e300)]
    public void InitialSolarAnglesPreserveEntryDirectionWithoutTiltingTheOrbitAxis(double x, double y, double z)
    {
        var angles = ((double Yaw, double Pitch))Call("InitialOrbitAngles", new object[] { x, y, z });
        double yaw = angles.Yaw * Math.PI / 180, pitch = angles.Pitch * Math.PI / 180;
        double scale = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
        x /= scale; y /= scale; z /= scale;
        double length = Math.Sqrt(x * x + y * y + z * z);
        Assert.Equal(-x / length, Math.Cos(pitch) * Math.Sin(yaw), 10);
        Assert.Equal(-y / length, Math.Sin(pitch), 10);
        Assert.Equal(-z / length, Math.Cos(pitch) * Math.Cos(yaw), 10);
    }

    [Theory]
    [InlineData(0d, 0d, 0d)]
    [InlineData(double.NaN, 0d, 1d)]
    [InlineData(0d, double.PositiveInfinity, 0d)]
    public void InitialSolarAnglesHaveAFiniteFallback(double x, double y, double z)
    {
        var angles = ((double Yaw, double Pitch))Call("InitialOrbitAngles", new object[] { x, y, z });
        Assert.Equal(0d, angles.Yaw);
        Assert.Equal(45d, angles.Pitch);
    }

    [Fact]
    public void SolarZoomKeepsTheCoronaOutsideTheCameraAndPreservesTheFarLimit()
    {
        Assert.Equal(495000d, Number("ClampDistance", 1d, 300000d, 80000000d), 7);
        Assert.Equal(80000000d, Number("ClampDistance", 1e12, 300000d, 80000000d));
        Assert.Equal(1200000d, Number("ClampDistance", double.NaN, 300000d, 80000000d));
        Assert.Equal(1200000d, Number("ClampDistance", double.PositiveInfinity, 300000d, 80000000d));
    }

    [Theory]
    [InlineData(.0001)]
    [InlineData(.01)]
    [InlineData(1.43)]
    [InlineData(5d)]
    [InlineData(12d)]
    public void FarVolumePreservesTheOriginalAngularRadius(double degrees)
    {
        double eye = Number("FarEyeRadius", degrees);
        Assert.InRange(eye, 8d, 128d);
        double projection = Number("ProjectionHalfTangent", eye);
        double quad = Number("FarQuadHalfTangent", degrees);
        double modelCore = 1d / Math.Sqrt(eye * eye - 1d);
        double reconstructed = Math.Atan(quad * modelCore / projection) * 180d / Math.PI;
        Assert.Equal(degrees, reconstructed, 9);
    }

    [Fact]
    public void FocusedRenderTargetIsCappedAndPreservesViewportAspect()
    {
        Assert.Equal((768, 432), Size(2560, 1440, true, 60d));
        Assert.Equal((432, 768), Size(1440, 2560, true, 60d));
        Assert.Equal((320, 180), Size(320, 180, true, 60d));
        Assert.Equal((64, 64), Size(0, -1, true, double.NaN));
    }

    [Theory]
    [InlineData(0d, 64)]
    [InlineData(1d, 64)]
    [InlineData(30d, 64)]
    [InlineData(65d, 256)]
    [InlineData(100000d, 256)]
    public void FarTargetWorkIsBoundedByApparentSize(double diameter, int expected)
        => Assert.Equal((expected, expected), Size(2560, 1440, false, diameter));

    [Fact]
    public void SimulationPhaseIsFinitePeriodicAndHandlesOldSaves()
    {
        Assert.Equal(0d, Number("Phase", 0d));
        Assert.Equal(0d, Number("Phase", 36000d));
        Assert.Equal(Number("Phase", 1234d), Number("Phase", 37234d), 12);
        Assert.Equal(Number("Phase", 1e12), Number("Phase", 1e12 + 36000d), 12);
        Assert.InRange(Number("Phase", -1d), 0d, Math.PI * 2d);
        Assert.Equal(0d, Number("Phase", double.NaN));
        Assert.Equal(0d, Number("Phase", double.PositiveInfinity));
        Assert.Equal(Math.Cos(Number("Phase", 35999.999)), Math.Cos(Number("Phase", .001)), 10);
    }

    private static double Number(string name, params object[] args) => (double)Call(name, args);
    private static (int, int) Size(int w, int h, bool focused, double diameter)
        => ((int, int))Call("TextureSize", new object[] { w, h, focused, diameter });

    private static object Call(string name, object[] args)
    {
        var core = typeof(PFViewSolarCoreTests).Assembly.GetType("PioneerFramework.PFViewSolarCore");
        Assert.NotNull(core);
        var method = core.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(null, args);
    }
}
