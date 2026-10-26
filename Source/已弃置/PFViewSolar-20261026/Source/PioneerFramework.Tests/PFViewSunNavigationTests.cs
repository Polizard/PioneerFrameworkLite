using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewSunNavigationTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(-1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, -1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, 0f, -1f)]
    [InlineData(0f, 1f, 1f)]
    [InlineData(0f, -1f, -1f)]
    [InlineData(3f, -5f, 7f)]
    public void SolarFocusCentersTheRenderedDiscWithOrthogonalUp(float x, float y, float z)
    {
        Vector3 toSun = new Vector3(x, y, z).normalized;
        Quaternion frame = PFViewSunDisc.FocusOrbitFrame(toSun);
        Vector3 offset = frame * PFViewCameraCore.OrbitDir(0, PFViewCameraCore.DefaultPitch);
        Vector3 up = frame * PFViewCameraCore.OrbitUp(0, PFViewCameraCore.DefaultPitch);

        // The star has no sphere at the logical focus. The camera must face the background disc.
        Vector3 camera = offset * 823131f;
        Vector3 disc = camera + toSun * 40000000f;
        Vector3 forward = -offset.normalized;
        Assert.True(Vector3.Dot(forward, (disc - camera).normalized) > .99999f);
        Assert.Equal(1f, up.magnitude, 4);
        Assert.Equal(0f, Vector3.Dot(forward, up), 4);
    }

    [Fact]
    public void SolarFocusUsesTheDirectionInTheActiveViewFrame()
    {
        var viewFrame = new Quaternion(0, .7071067812f, 0, .7071067812f);
        Vector3 toSun = viewFrame * new Vector3(0, 0, -1);
        Quaternion frame = PFViewSunDisc.FocusOrbitFrame(toSun);
        Vector3 forward = -(frame * PFViewCameraCore.OrbitDir(0, PFViewCameraCore.DefaultPitch));
        Assert.True(Vector3.Dot(forward, toSun) > .99999f);
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(float.NaN, 0f, 1f)]
    [InlineData(float.PositiveInfinity, 0f, 1f)]
    public void MissingSolarDirectionHasAFiniteVisibleFallback(float x, float y, float z)
    {
        Quaternion frame = PFViewSunDisc.FocusOrbitFrame(new(x, y, z));
        Vector3 forward = -(frame * PFViewCameraCore.OrbitDir(0, PFViewCameraCore.DefaultPitch));
        Assert.Equal(0f, forward.x, 4);
        Assert.Equal(0f, forward.y, 4);
        Assert.Equal(-1f, forward.z, 4);
    }

    [Fact]
    public void SolarNavigationReorientsTheCameraInsteadOfRestoringAnOffscreenPose()
    {
        var method = typeof(MainTabWindow_PFView).GetMethod("FocusBody", Flags);
        Assert.Contains(PFViewUiIntegrationTests.ReferencedMembers(method), m => m.Name == "InitializeSunCameraPose");
    }

    [Fact]
    public void SolarCameraUsesThePlanetOrbitFrameAndStandardEntryAngles()
    {
        var owner = typeof(MainTabWindow_PFView);
        var orbit = owner.GetMethod("ResolveOrbitFrame", Flags);
        var members = PFViewUiIntegrationTests.ReferencedMembers(orbit);
        Assert.DoesNotContain(members, m => m.Name == "SunFocusOrbitFrame");
        Assert.DoesNotContain(members, m => m.Name == "get_IsStarFocus");

        var initialize = owner.GetMethod("InitializeSunCameraPose", Flags);
        var initialization = PFViewUiIntegrationTests.ReferencedMembers(initialize);
        Assert.DoesNotContain(initialization, m => m.Name == "SunDirectionInView");
        Assert.DoesNotContain(initialization, m => m.Name == "InitialOrbitAngles");
        var source = ReadSource("MainTabWindow_PFView.Sun.cs");
        Assert.Contains("camYaw = 0f;", source);
        Assert.Contains("camPitch = PFViewCameraCore.DefaultPitch;", source);

        var camera = owner.GetMethod("UpdateCamera", Flags);
        Assert.Contains(PFViewUiIntegrationTests.ReferencedMembers(camera), m => m.Name == "get_IsStarFocus");
    }

    [Fact]
    public void SolarPitchAndScreenUpUseTheSameBodyRulesAsPlanets()
    {
        var source = ReadSource("MainTabWindow_PFView.Camera.cs");
        Assert.Contains("camPitch = PFViewCameraCore.ResolvePitch(focusKind, camPitch);", source);
        Assert.DoesNotContain("IsStarFocus ? PFViewFocusKind.Frozen : focusKind", source);
        Assert.Contains("up = focusKind == PFViewFocusKind.Body ? Vector3.up :", source);
        var drag = typeof(MainTabWindow_PFView).GetMethod("HandleCameraDrag", Flags);
        Assert.DoesNotContain(PFViewUiIntegrationTests.ReferencedMembers(drag), m => m.Name == "get_IsStarFocus");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    [InlineData(60f)]
    [InlineData(90f)]
    [InlineData(120f)]
    [InlineData(180f)]
    [InlineData(270f)]
    public void BodyOrbitHasALevelHorizonAtEveryHorizontalAngle(float yaw)
    {
        float pitch = PFViewCameraCore.ResolvePitch(PFViewFocusKind.Body, PFViewCameraCore.DefaultPitch);
        Vector3 radial = PFViewCameraCore.OrbitDir(yaw, pitch);
        Vector3 up = PFViewCameraCore.OrbitUp(yaw, pitch);
        Vector3 north = Vector3.up;
        Vector3 projectedNorth = (north - Vector3.Dot(north, radial) * radial).normalized;

        Assert.True(Vector3.Dot(up, projectedNorth) > .99999f, "Horizontal solar orbit must preserve inertial north as screen up.");
        Assert.Equal(0f, Vector3.Dot(up, radial), 5);
    }

    [Theory]
    [InlineData(84f, 720f, 85f)]
    [InlineData(6f, -720f, 5f)]
    [InlineData(45f, 720f, 85f)]
    [InlineData(45f, -720f, 5f)]
    public void BodyDragStopsAtThePlanetPitchLimitAndReversesImmediately(float start, float delta, float expected)
    {
        var drag = new PFViewOrbitDragState();
        (float Yaw, float Pitch) look = (0f, start);
        for (int input = 0; input < 8; input++)
        {
            drag.AddDrag(look.Yaw, look.Pitch, 0f, delta, PFViewFocusKind.Body, false);
            look = drag.Advance(look.Yaw, look.Pitch, .1f);
            Assert.InRange(look.Pitch, 5f, 85f);
        }
        look = drag.Advance(look.Yaw, look.Pitch, 1f);
        Assert.Equal(expected, look.Pitch, 3);
        drag.AddDrag(look.Yaw, look.Pitch, 0f, delta > 0 ? -1f : 1f, PFViewFocusKind.Body, false);
        var reversed = drag.Advance(look.Yaw, look.Pitch, 1f / 60f);
        Assert.True(delta > 0 ? reversed.Pitch < look.Pitch : reversed.Pitch > look.Pitch);
        Assert.InRange(reversed.Pitch, 5f, 85f);
    }

    private static string ReadSource(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Source", "PioneerFramework", "PFView", name));
}
