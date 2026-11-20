using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Xunit;

namespace PioneerFramework.Tests;

public sealed class WorldCameraInteractionTests
{
    private static T Tuning<T>(string name) where T : Delegate
    {
        var type = typeof(WorldCelestialHost).Assembly.GetType("PioneerFramework.WorldCameraTuning");
        Assert.NotNull(type);
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.CreateDelegate<T>();
    }

    [Fact]
    public void SurfaceDragHasAResponsiveFloorAndBoundedOverviewSpeedOnDifferentSizedLayers()
    {
        var gain = Tuning<Func<double, double, float, float, float>>("DragDegreesPerPixel");
        Assert.InRange(gain(100, 125, 45, 720), .23f, .49f);
        Assert.InRange(gain(16, 125, 45, 720), .23f, .49f);
        Assert.InRange(gain(100, 50000, 45, 720), .23f, .49f);
        Assert.Equal(gain(100, 400, 45, 720), gain(200, 800, 45, 720));
        Assert.Equal(gain(100, 400, 45, 720), 2 * gain(100, 400, 45, 1440));
    }

    [Fact]
    public void DirectionalMovementRemainsResponsiveAtTheSurfaceAndBoundedAtHillScale()
    {
        var scale = Tuning<Func<double, double, float>>("KeyboardScale");
        Assert.InRange(scale(100, 125), .45f, .6f);
        Assert.InRange(scale(100, 50000), .9f, 1f);
        Assert.InRange(scale(16, 125), .45f, 1f);
        Assert.Equal(scale(100, 500), scale(200, 1000));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void RotationSettlesQuicklyWithoutSnappingOrDependingOnFrameRate(int frameRate)
    {
        var blend = Tuning<Func<float, float>>("RotationBlend");
        double remaining = 1;
        for (int i = 0; i < frameRate; i++) remaining *= 1 - blend(1f / frameRate);
        Assert.InRange(remaining, 2.7e-10, 2.9e-10);
        Assert.InRange(blend(.05f), .6f, .75f);
        Assert.InRange(blend(1f / frameRate), .01f, .6f);
        Assert.Equal(0, blend(0));
    }

    [Fact]
    public void NativeZoomLimitConvertsTwiceHillDistanceToLayerUnitsAndSubtractsLayerOffset()
    {
        var maximum = Tuning<Func<double, float, float, double, double, double?, double?, float>>("MaximumAltitude");
        // Hill radius = 1e8 m, native radius = 100 for a 5e6 m body: camera distance = 4000.
        Assert.Equal(3980f, maximum(100, 20, 125, 5e6, 1e9, 3e9, 1e12));
        Assert.Equal(125f, maximum(1, 20, 125, 5e6, 1, 1, 1e12));
    }

    [Fact]
    public void SurfaceHandoffKeepsTheTerrainCapInViewDuringAnUnfinishedFrameTransition()
    {
        var forward = Tuning<Func<DQuaternion, DQuaternion, DVector3, DVector3>>("SurfaceCameraForward");
        var frame = DQuaternion.AngleAxis(-Math.PI / 4, DVector3.up);
        var body = DQuaternion.AngleAxis(Math.PI / 2, DVector3.up);
        var center = DVector3.forward;
        var cameraForward = forward(frame, body, center);
        Assert.True((cameraForward - new DVector3(-Math.Sqrt(.5), 0, -Math.Sqrt(.5))).magnitude < 1e-12);
        var nativeBack = DVector3.Rotate(-cameraForward, DQuaternion.Inverse(frame * body));
        Assert.True((nativeBack - center).magnitude < 1e-12);

        var aim = typeof(WorldCelestialNavigation).GetMethod("AimAtAvailableTerrain", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var calls = PFViewUiIntegrationTests.ReferencedMembers(aim).OfType<MethodBase>();
        Assert.Contains(calls, call => call.Name == "get_FrameRotation");
    }

    [Fact]
    public void LockButtonCompletesANativeHandoffInsteadOfOnlyRefocusingTheProxy()
    {
        var button = typeof(WorldCelestialHost).GetMethod("DrawPlanetViewLock", BindingFlags.NonPublic | BindingFlags.Static)!;
        var calls = PFViewUiIntegrationTests.ReferencedMembers(button).OfType<MethodBase>().ToArray();
        Assert.Contains(calls, call => call.DeclaringType == typeof(WorldCelestialHost) && call.Name == "Close");
        Assert.DoesNotContain(calls, call => call.DeclaringType == typeof(WorldCelestialNavigation) && call.Name == "LockToLayer");
    }

    [Theory]
    [InlineData("WorldCameraDriverOnGUI", "OnGui", new[] { "DragTileSize", "AllowLayerZoom", "MaximumAltitude" })]
    [InlineData("Update", "Update", new[] { "KeyboardScale", "PresentRotation" })]
    public void NativeCameraPatchesReachEveryActualGameCallSite(string nativeMethod, string patchMethod, string[] hooks)
    {
        var type = typeof(WorldCelestialHost).Assembly.GetType("PioneerFramework.WorldNativeCameraPatches");
        Assert.NotNull(type);
        var original = AccessTools.Method(typeof(WorldCameraDriver), nativeMethod);
        var patch = AccessTools.Method(type, patchMethod);
        var patched = ((IEnumerable<CodeInstruction>)patch.Invoke(null,
            new object[] { PatchProcessor.GetOriginalInstructions(original) })).ToArray();
        foreach (string hook in hooks)
            Assert.Single(patched.Where(code => code.operand is MethodInfo method && method.Name == hook));
    }
}
