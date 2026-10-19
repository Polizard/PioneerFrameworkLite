using System;
using Xunit;

namespace PioneerFramework.Tests
{
    public class PFViewZoomTests
    {
        [Theory]
        [InlineData(1f)]
        [InlineData(-1f)]
        public void OneWheelStepMovesOverSeveralFramesWithoutOvershoot(float delta)
        {
            float target = PFViewCameraCore.WheelTargetDistance(100f, 100f, delta);
            float distance = 100f;
            float initialError = Math.Abs(target - distance);
            for (int frame = 0; frame < 12; frame++)
            {
                float next = PFViewCameraCore.SmoothZoomDistance(distance, target, 1f / 60f);
                Assert.InRange(next, Math.Min(distance, target), Math.Max(distance, target));
                Assert.NotEqual(distance, next);
                if (frame == 0) Assert.True(Math.Abs(next - distance) < initialError * .5f);
                distance = next;
            }
            Assert.True(Math.Abs(target - distance) < initialError * .06f, "Most zoom movement should finish within 0.2 seconds.");
        }

        [Fact]
        public void RepeatedWheelEventsAccumulateBeforeTheNextRender()
        {
            float target = 100f;
            for (int input = 0; input < 3; input++)
                target = PFViewCameraCore.WheelTargetDistance(target, 100f, 1f);
            Assert.Equal(137.1742f, target, 3);
            Assert.InRange(PFViewCameraCore.SmoothZoomDistance(100f, target, 1f / 60f), 100.01f, 137f);
        }

        [Theory]
        [InlineData(130f, 100f, -1f, 90f)]
        [InlineData(70f, 100f, 1f, 111.11111f)]
        public void ReversingWheelRespondsFromTheDisplayedDistance(float oldTarget, float displayed, float delta, float expected)
        {
            float target = PFViewCameraCore.WheelTargetDistance(oldTarget, displayed, delta);
            Assert.Equal(expected, target, 3);
            float next = PFViewCameraCore.SmoothZoomDistance(displayed, target, 1f / 60f);
            Assert.True((next - displayed) * delta > 0f);
        }

        [Fact]
        public void FractionalWheelInputRetainsItsDirectionAndMagnitude()
        {
            float partial = PFViewCameraCore.WheelTargetDistance(100f, 100f, .25f);
            Assert.InRange(partial, 102f, 103f);
            Assert.Equal(100f, PFViewCameraCore.WheelTargetDistance(partial, partial, -.25f), 3);
        }

        [Theory]
        [InlineData(30, 100f)]
        [InlineData(60, 100f)]
        [InlineData(144, 100f)]
        [InlineData(30, 500000f)]
        [InlineData(144, 80000000f)]
        public void EqualRealTimeProducesTheSameZoomAtDifferentFrameRates(int fps, float start)
        {
            float target = start * .75f;
            float distance = start;
            for (int frame = 0; frame < fps; frame++)
                distance = PFViewCameraCore.SmoothZoomDistance(distance, target, .2f / fps);
            float singleStep = PFViewCameraCore.SmoothZoomDistance(start, target, .2f);
            Assert.True(Math.Abs(singleStep - distance) <= start * .00001f);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void InvalidElapsedTimeDoesNotMoveOrPoisonTheCamera(float dt)
            => Assert.Equal(100f, PFViewCameraCore.SmoothZoomDistance(100f, 200f, dt));

        [Fact]
        public void ZoomEventuallySettlesExactlyOnTarget()
        {
            float distance = 100f;
            for (int frame = 0; frame < 120; frame++)
                distance = PFViewCameraCore.SmoothZoomDistance(distance, 90f, 1f / 60f);
            Assert.Equal(90f, distance);
        }

        [Fact]
        public void FreeGroupZoomStaysWithinLimitsAndReversesImmediatelyAfterRepeatedLimitInput()
        {
            float target = 100f, displayed = 100f;
            for (int input = 0; input < 60; input++)
            {
                target = PFViewCameraCore.ClampDistance(PFViewFocusKind.Group,
                    PFViewCameraCore.WheelTargetDistance(target, displayed, 1f), .1f, 500000f);
                displayed = PFViewCameraCore.SmoothZoomDistance(displayed, target, 1f / 60f);
                Assert.InRange(displayed, 30f, 300f);
            }
            Assert.Equal(300f, target);
            target = PFViewCameraCore.ClampDistance(PFViewFocusKind.Group,
                PFViewCameraCore.WheelTargetDistance(target, displayed, -1f), .1f, 500000f);
            Assert.True(PFViewCameraCore.SmoothZoomDistance(displayed, target, 1f / 60f) < displayed);
        }

        [Theory]
        [InlineData(PFViewGroupCameraMode.ThirdPersonHull)]
        [InlineData(PFViewGroupCameraMode.ThirdPersonVelocity)]
        public void BothThirdPersonModesKeepSizeAwareZoomLimits(PFViewGroupCameraMode mode)
        {
            Assert.True(PFViewGroupCameraCore.IsThirdPerson(mode));
            var limits = PFViewGroupCameraCore.Distances(100f, 45f, 16f / 9f);
            float displayed = limits.DefaultMeters;
            foreach (float delta in new[] { -3f, -3f, -3f, -3f, 3f, 3f, 3f, 3f })
            {
                float target = limits.ClampMeters(PFViewCameraCore.WheelTargetDistance(displayed, displayed, delta));
                displayed = PFViewCameraCore.SmoothZoomDistance(displayed, target, .2f);
                Assert.InRange(displayed, limits.MinMeters, limits.MaxMeters);
            }
        }
    }
}
