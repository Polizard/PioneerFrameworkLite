using System;
using PioneerFramework;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewPilotCameraTests
{
    [Fact]
    public void AFullHullRollDoesNotRotateTheStableCameraUpDirection()
    {
        var stable = PFViewPilotCameraCore.TransportHeading(Quaternion.identity, Vector3.forward);
        var rolledHull = new Quaternion(0, 0, 1, 0);
        stable = PFViewPilotCameraCore.TransportHeading(stable, rolledHull * Vector3.forward);
        Assert.Equal(1, (stable * Vector3.up).y, 5);
        Assert.Equal(0, (stable * Vector3.up).x, 5);
    }

    [Fact]
    public void HeadingTransportRemainsContinuousAcrossPolesAndReturnsTheDesiredForward()
    {
        var frame = Quaternion.identity;
        Vector3 previousUp = Vector3.up;
        for (int angle = 1; angle <= 360; angle++)
        {
            float half = angle * MathF.PI / 360;
            var hull = new Quaternion(MathF.Sin(half), 0, 0, MathF.Cos(half));
            Vector3 nose = hull * Vector3.forward;
            frame = PFViewPilotCameraCore.TransportHeading(frame, nose);
            Vector3 forward = frame * Vector3.forward, up = frame * Vector3.up;
            Assert.True(Vector3.Dot(previousUp, up) > .99f);
            Assert.Equal(1, Vector3.Dot(forward, nose), 4);
            Assert.Equal(0, Vector3.Dot(forward, up), 4);
            previousUp = up;
        }
    }

    [Fact]
    public void AntiparallelHeadingKeepsAValidContinuousUpReference()
    {
        var frame = PFViewPilotCameraCore.TransportHeading(Quaternion.identity, Vector3.back);
        Assert.Equal(-1, (frame * Vector3.forward).z, 5);
        Assert.Equal(1, (frame * Vector3.up).y, 5);
        var retained = PFViewPilotCameraCore.TransportHeading(frame, new(float.NaN, 0, 0));
        Assert.Equal(-1, (retained * Vector3.forward).z, 5);
        var zero = PFViewPilotCameraCore.TransportHeading(frame, Vector3.zero);
        Assert.Equal(-1, (zero * Vector3.forward).z, 5);
    }

    [Fact]
    public void StableFrameCanBeRenderedInAnotherReferenceFrameWithoutLosingItsAbsolutePose()
    {
        var stable = PFViewPilotCameraCore.TransportHeading(Quaternion.identity, new(1, 0, 0));
        var view = new Quaternion(0, .70710678f, 0, .70710678f);
        var rendered = view * stable;
        var restored = Quaternion.Inverse(view) * rendered;
        Assert.Equal(1, Vector3.Dot(restored * Vector3.up, stable * Vector3.up), 5);
        Assert.Equal(1, Vector3.Dot(restored * Vector3.forward, stable * Vector3.forward), 5);
    }

    [Fact]
    public void FollowSmoothingUsesElapsedTimeAndZeroSmoothingIsImmediate()
    {
        var target = new Quaternion(0, .70710678f, 0, .70710678f);
        var once = PFViewPilotCameraCore.SmoothFollow(Quaternion.identity, target, .1f, .1f);
        var twice = PFViewPilotCameraCore.SmoothFollow(Quaternion.identity, target, .05f, .1f);
        twice = PFViewPilotCameraCore.SmoothFollow(twice, target, .05f, .1f);
        Assert.Equal(1, Vector3.Dot(once * Vector3.forward, twice * Vector3.forward), 5);
        Assert.Equal(1, Vector3.Dot(target * Vector3.forward,
            PFViewPilotCameraCore.SmoothFollow(Quaternion.identity, target, .1f, 0) * Vector3.forward), 5);
    }

    [Fact]
    public void LookAroundPassesTheOldSideLimitAndWrapsAWholeRevolution()
    {
        Assert.Equal(120, PFViewPilotCameraCore.LookYaw(120));
        Assert.Equal(20, PFViewPilotCameraCore.LookYaw(380));
        Assert.Equal(-85, PFViewPilotCameraCore.LookPitch(-180));
        Assert.Equal(85, PFViewPilotCameraCore.LookPitch(180));
    }

    [Fact]
    public void RightAndUpDragLookRightAndUpInBothFreeAndTailFrames()
    {
        foreach (var frame in new[] { Quaternion.identity, PFViewCameraCore.GroupOrbitFrame(Quaternion.identity) })
        {
            var drag = PFViewPilotCameraCore.LookDrag(0, 0, 20, -20, .3f, false);
            Vector3 before = -(frame * PFViewCameraCore.OrbitDir(0, 0));
            Vector3 after = -(frame * PFViewCameraCore.OrbitDir(drag.Yaw, drag.Pitch));
            Vector3 right = Vector3.Cross(Vector3.up, before);
            Assert.True(Vector3.Dot(after, right) > 0);
            Assert.True(after.y > 0);
        }
        Assert.True(PFViewPilotCameraCore.LookDrag(0, 0, 0, -20, .3f, true).Pitch > 0);
    }

    [Fact]
    public void RecenterUsesTheShortestYawAndPanContentFollowsCursorVertically()
    {
        var look = PFViewPilotCameraCore.RecenterLook(350, 45, .18f);
        Assert.InRange(look.Yaw, -10, 0);
        Assert.InRange(look.Pitch, 15, 45);
        var pan = PFViewPilotCameraCore.PanDelta(Vector3.right, Vector3.up, 10, 20, 2);
        Assert.Equal(-20, pan.x);
        Assert.Equal(40, pan.y);
    }
}
