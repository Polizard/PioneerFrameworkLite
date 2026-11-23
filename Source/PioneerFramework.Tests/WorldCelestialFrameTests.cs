using PioneerFramework.Celestial;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests;

public sealed class WorldCelestialFrameTests
{
    [Fact]
    public void PreviewFadesOnlyWhenNativeTerrainCanCoverIt()
    {
        Assert.Equal(1f, WorldCelestialNavigation.SurfacePreviewOpacity(.07, true));
        Assert.InRange(WorldCelestialNavigation.SurfacePreviewOpacity(.115, true), .49f, .51f);
        Assert.Equal(0f, WorldCelestialNavigation.SurfacePreviewOpacity(.16, true));
        Assert.Equal(1f, WorldCelestialNavigation.SurfacePreviewOpacity(.20, false));
    }

    [Fact]
    public void NativeSurfaceFramePreservesCameraRelativePositionAndAlignsTheNativeMesh()
    {
        var body = new DVector3(5010, -3020, 1030);
        var layerOrigin = new DVector3(8, 600, 12);
        var bodyOrientation = DQuaternion.AngleAxis(.8, new DVector3(1, 2, 3));
        var cameraOrientation = DQuaternion.AngleAxis(.3, DVector3.right);
        var navigation = new WorldCelestialFrame(body + DVector3.right * 80, DVector3.one,
            DQuaternion.AngleAxis(.4, DVector3.up), .25);
        var native = navigation.ForNativeSurface(body, bodyOrientation, layerOrigin, .5);
        var point = body + DVector3.Rotate(DVector3.forward * 100, bodyOrientation);
        Assert.True((native.ToView(point) - (layerOrigin + DVector3.forward * 50)).magnitude < 1e-9);

        var cameraFromAim = DVector3.Rotate(DVector3.back * 400, cameraOrientation);
        var nativeRotation = native.Rotation * DQuaternion.Inverse(navigation.Rotation) * cameraOrientation;
        var nativeCamera = native.ToView(navigation.PhysicsAnchor) + DVector3.Rotate(DVector3.back * 800, nativeRotation);
        var oldView = DVector3.Rotate(navigation.ToView(point) - navigation.ViewAnchor - cameraFromAim,
            DQuaternion.Inverse(cameraOrientation));
        var newView = DVector3.Rotate(native.ToView(point) - nativeCamera, DQuaternion.Inverse(nativeRotation));
        Assert.True((newView / native.Scale - oldView / navigation.Scale).magnitude < 1e-9);
    }

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
        Assert.False(WorldCelestialFrame.ShouldEnterSystem(.010, .009, false, false, .010));
        Assert.False(WorldCelestialFrame.ShouldEnterSystem(.009, .010, false, false, .010));
        Assert.True(WorldCelestialFrame.ShouldEnterSystem(.009, .009, false, false, .010));
        Assert.False(WorldCelestialFrame.ShouldEnterSystem(.009, .009, true, false, .010));
        Assert.False(WorldCelestialFrame.CanRearm(.013));
        Assert.True(WorldCelestialFrame.CanRearm(.015));
    }

    [Fact]
    public void PlanetViewLockPreventsAutomaticSystemEntryBelowBothZoomThresholds()
    {
        Assert.False(WorldCelestialFrame.ShouldEnterSystem(.004, .003, false, true, .005));
        Assert.True(WorldCelestialFrame.ShouldEnterSystem(.004, .003, false, false, .005));
    }

    [Fact]
    public void LockedViewStopsAtTwiceTheHillRadiusAndKeepsAValidMinimumDistance()
    {
        Assert.Equal(2e8, WorldCelestialFrame.LockedDistanceLimit(5e6, 1e9, 3e9, 1e12), 5);
        Assert.Equal(1e9, WorldCelestialFrame.LockedDistanceLimit(5e6, 1e9, null, 1e12));
        Assert.True(WorldCelestialFrame.LockedDistanceLimit(5e6, 1, 1, 1e12) >= 5e6 / .88);
    }

    [Fact]
    public void OrbitDragFollowsPointerMovementAcrossEventRatesAndUiSizes()
    {
        var start = new Vector2(200, 100);
        var middle = new Vector2(230, 115);
        var end = new Vector2(300, 150);
        var full = WorldCelestialFrame.OrbitDragDegrees(end, start, 720, 1);
        var split = WorldCelestialFrame.OrbitDragDegrees(middle, start, 720, 1) +
            WorldCelestialFrame.OrbitDragDegrees(end, middle, 720, 1);
        Assert.True((full - split).sqrMagnitude < 1e-8f);
        Assert.Equal(45f, full.x);
        Assert.Equal(22.5f, full.y);
        Assert.Equal(full, WorldCelestialFrame.OrbitDragDegrees(end * 2, start * 2, 1440, 1));
        Assert.Equal(full * 2, WorldCelestialFrame.OrbitDragDegrees(end, start, 720, 2));
    }

    [Fact]
    public void LocalWorldGuiOffersGroupSelectionBeforeConsumingCelestialMouseInput()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var method = typeof(WorldCelestialHost).GetMethod("AfterWorldGui", flags)!;
        var calls = PFViewUiIntegrationTests.ReferencedMembers(method).OfType<MethodBase>().ToArray();
        int groups = Array.FindIndex(calls, call => call.DeclaringType == typeof(SpaceGroupVisualManager));
        int celestialInput = Array.FindIndex(calls, call =>
            call.DeclaringType == typeof(WorldCelestialNavigation) && call.Name == "HandleInput");
        Assert.True(groups >= 0 && groups < celestialInput,
            "Group picking and panel input must run before the celestial camera consumes the event.");
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
