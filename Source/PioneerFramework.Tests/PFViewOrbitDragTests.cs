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
        drag.AddDrag(0f, 15f, 6f, -6f, PFViewFocusKind.Group, true);
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
        drag.AddDrag(0f, 0f, 60f, 0f, PFViewFocusKind.Group, true);
        (float Yaw, float Pitch) look = (0f, 0f);
        for (int frame = 0; frame < steps; frame++)
            look = drag.Advance(look.Yaw, look.Pitch, .12f / steps);
        Assert.Equal(57.0128f, look.Yaw, 3);
    }

    [Fact]
    public void ReleaseHasOnlyAShortSettlingTail()
    {
        var drag = CreateDrag();
        drag.AddDrag(0f, 15f, 6f, 0f, PFViewFocusKind.Group, true);
        (float Yaw, float Pitch) look = drag.Advance(0f, 15f, .2f);
        Assert.InRange(look.Yaw, 5.95f, 6f);
    }

    [Fact]
    public void ReversingEitherAxisRespondsImmediatelyFromTheDisplayedAngle()
    {
        var drag = CreateDrag();
        drag.AddDrag(0f, 15f, 20f, -20f, PFViewFocusKind.Group, true);
        (float Yaw, float Pitch) before = drag.Advance(0f, 15f, 1f / 60f);
        drag.AddDrag(before.Yaw, before.Pitch, -1f, 1f, PFViewFocusKind.Group, true);
        (float Yaw, float Pitch) after = drag.Advance(before.Yaw, before.Pitch, 1f / 60f);
        Assert.InRange(after.Yaw, before.Yaw - 1f, before.Yaw - .01f);
        Assert.InRange(after.Pitch, before.Pitch + .01f, before.Pitch + 1f);
    }

    [Theory]
    [InlineData(84f, 10f, 85f, -85f, 85f, (int)PFViewFocusKind.Group, true)]
    [InlineData(-84f, -10f, -85f, -85f, 85f, (int)PFViewFocusKind.Group, true)]
    [InlineData(6f, -10f, 5f, 5f, 85f, (int)PFViewFocusKind.Body, false)]
    public void RepeatedPitchLimitInputCannotAccumulateHiddenTravel(float start, float delta,
        float limit, float minimum, float maximum, int kind, bool thirdPerson)
    {
        var drag = CreateDrag();
        (float Yaw, float Pitch) look = (0f, start);
        for (int input = 0; input < 30; input++)
            drag.AddDrag(look.Yaw, look.Pitch, 0f, delta, (PFViewFocusKind)kind, thirdPerson);
        look = drag.Advance(look.Yaw, look.Pitch, .5f);
        Assert.Equal(limit, look.Pitch, 3);
        drag.AddDrag(look.Yaw, look.Pitch, 0f, -MathF.Sign(delta), (PFViewFocusKind)kind, thirdPerson);
        (float Yaw, float Pitch) next = drag.Advance(look.Yaw, look.Pitch, 1f / 60f);
        Assert.True((next.Pitch - look.Pitch) * delta < 0);
        Assert.InRange(next.Pitch, minimum, maximum);
    }

    [Fact]
    public void WrappingDisplayedAnglesPreservesTheRemainingDragDirection()
    {
        var drag = CreateDrag();
        drag.AddDrag(179f, 179f, 6f, 6f, PFViewFocusKind.Group, false);
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
        drag.AddDrag(0f, 15f, 6f, 0f, PFViewFocusKind.Group, true);
        Assert.Equal((0f, 15f), ((float, float))drag.Advance(0f, 15f, dt));
        (float Yaw, float Pitch) look = drag.Advance(0f, 15f, 1f);
        Assert.Equal(6f, look.Yaw, 3);
    }

    [Fact]
    public void CancellingADragDropsItsTailBeforeAnotherCameraPoseIsUsed()
    {
        var drag = CreateDrag();
        drag.AddDrag(0f, 15f, 60f, 30f, PFViewFocusKind.Group, true);
        drag.Reset();
        Assert.Equal((120f, 45f), ((float, float))drag.Advance(120f, 45f, 1f));
    }
}
