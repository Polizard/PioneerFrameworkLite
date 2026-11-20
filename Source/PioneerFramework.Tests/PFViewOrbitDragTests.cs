using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewOrbitDragTests
{
    private static PFViewOrbitDragState CreateDrag() => new();

    [Fact]
    public void DragMovesOnTheFirstFrameAndSettlesWithoutOvershoot()
    {
        var drag = CreateDrag();
        drag.AddDrag(0f, 15f, 6f, -6f);
        (float Yaw, float Pitch) look = drag.Advance(0f, 15f, 1f / 60f);
        Assert.InRange(look.Yaw, 1f, 5f);
        Assert.InRange(look.Pitch, 10f, 14f);
        for (int frame = 0; frame < 120; frame++)
        {
            look = drag.Advance(look.Yaw, look.Pitch, 1f / 60f);
            Assert.InRange(look.Yaw, 0f, 6f);
            Assert.InRange(look.Pitch, 9f, 15f);
        }
        Assert.Equal(6f, look.Yaw, 3);
        Assert.Equal(9f, look.Pitch, 3);
        Assert.Equal(look, ((float, float))drag.Advance(look.Yaw, look.Pitch, 1f));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void RotationUsesRealElapsedTimeAtDifferentFrameRates(int steps)
    {
        var drag = CreateDrag();
        drag.AddDrag(0f, 0f, 60f, 0f);
        (float Yaw, float Pitch) look = (0f, 0f);
        for (int frame = 0; frame < steps; frame++)
            look = drag.Advance(look.Yaw, look.Pitch, .12f / steps);
        Assert.Equal(57.0128f, look.Yaw, 3);
    }

    [Fact]
    public void ReleaseHasOnlyAShortSettlingTail()
    {
        var drag = CreateDrag();
        drag.AddDrag(0f, 15f, 6f, 0f);
        (float Yaw, float Pitch) look = drag.Advance(0f, 15f, .2f);
        Assert.InRange(look.Yaw, 5.95f, 6f);
    }

    [Fact]
    public void ReversingEitherAxisRespondsImmediatelyFromTheDisplayedAngle()
    {
        var drag = CreateDrag();
        drag.AddDrag(0f, 15f, 20f, -20f);
        (float Yaw, float Pitch) before = drag.Advance(0f, 15f, 1f / 60f);
        drag.AddDrag(before.Yaw, before.Pitch, -1f, 1f);
        (float Yaw, float Pitch) after = drag.Advance(before.Yaw, before.Pitch, 1f / 60f);
        Assert.InRange(after.Yaw, before.Yaw - 1f, before.Yaw - .01f);
        Assert.InRange(after.Pitch, before.Pitch + .01f, before.Pitch + 1f);
    }

    [Fact]
    public void PitchPassesBothPolesAndReversesWithoutAnAngleStop()
    {
        // 2026-11-20：天体 5°–85° 与第三人称 ±85° 的俯仰终点都已取消。
        // 连续向下输入应当越过正下方（-90°）继续走，而不是在限位处停下并吞掉余量。
        var drag = CreateDrag();
        (float Yaw, float Pitch) look = (0f, 15f);
        for (int input = 0; input < 40; input++)
        {
            drag.AddDrag(look.Yaw, look.Pitch, 0f, -10f);
            look = drag.Advance(look.Yaw, look.Pitch, .5f);
        }
        Assert.True(look.Pitch < -90f, "Pitch must pass the pole instead of stopping at a fixed limit.");
        Assert.InRange(look.Pitch, -400f, -380f);

        float before = look.Pitch;
        drag.AddDrag(look.Yaw, look.Pitch, 0f, 1f);
        (float Yaw, float Pitch) next = drag.Advance(look.Yaw, look.Pitch, 1f / 60f);
        Assert.True(next.Pitch > before);
    }

    [Fact]
    public void WrappingDisplayedAnglesPreservesTheRemainingDragDirection()
    {
        var drag = CreateDrag();
        drag.AddDrag(179f, 179f, 6f, 6f);
        (float Yaw, float Pitch) look = drag.Advance(179f, 179f, 1f / 60f);
        Assert.InRange(look.Yaw, 180f, 185f);
        look = drag.Advance(look.Yaw - 360f, look.Pitch - 360f, 1f);
        Assert.Equal(-175f, look.Yaw, 3);
        Assert.Equal(-175f, look.Pitch, 3);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidFrameTimeDoesNotMoveOrDiscardPendingInput(float dt)
    {
        var drag = CreateDrag();
        drag.AddDrag(0f, 15f, 6f, 0f);
        Assert.Equal((0f, 15f), ((float, float))drag.Advance(0f, 15f, dt));
        (float Yaw, float Pitch) look = drag.Advance(0f, 15f, 1f);
        Assert.Equal(6f, look.Yaw, 3);
    }

    [Fact]
    public void CancellingADragDropsItsTailBeforeAnotherCameraPoseIsUsed()
    {
        var drag = CreateDrag();
        drag.AddDrag(0f, 15f, 60f, 30f);
        drag.Reset();
        Assert.Equal((120f, 45f), ((float, float))drag.Advance(120f, 45f, 1f));
    }
}
