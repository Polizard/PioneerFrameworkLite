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
    public void SolarCameraFrameAndUpAreConnectedToTheRenderedSolarDirection()
    {
        var owner = typeof(MainTabWindow_PFView);
        var orbit = owner.GetMethod("ResolveOrbitFrame", Flags);
        Assert.Contains(PFViewUiIntegrationTests.ReferencedMembers(orbit), m => m.Name == "SunFocusOrbitFrame");

        var solarFrame = owner.GetMethod("SunFocusOrbitFrame", Flags);
        Assert.NotNull(solarFrame);
        var members = PFViewUiIntegrationTests.ReferencedMembers(solarFrame);
        Assert.Contains(members, m => m.Name == "SunDirectionInView");
        Assert.Contains(members, m => m.Name == "viewFrameRot");
        Assert.Contains(members, m => m.Name == "FocusOrbitFrame");

        var camera = owner.GetMethod("UpdateCamera", Flags);
        Assert.Contains(PFViewUiIntegrationTests.ReferencedMembers(camera), m => m.Name == "get_IsStarFocus");
    }

}
