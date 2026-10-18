using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **PFView 视图系统 v2 的纯核心钉子**（2026-10-18）：焦点分档夹取 / 距离投影 / 默认取景 /
    /// 滚轮限幅 / 俯仰夹取 / 过渡收敛 / 开火语义。
    /// 只钉可机械复查的算术；观感/手感仍须游戏内验（本文件不声称）。
    /// </summary>
    public class PFViewCameraCoreTests
    {
        const float M2V = 0.1f;                 // 1 m = 0.1 view 单位
        const float EarthRadiusView = 500_000f; // 地球 R=5000 km（= 5e6 m）× 0.1

        [Fact]
        public void GroupDistance_StaysInsideUserLimits()
        {
            // 2026-10-10 用户口径：300 m ~ 3 km（只许加强，不许软化）
            Assert.Equal(PFViewCameraLimits.GroupOrbitMinMeters * M2V,
                PFViewCameraCore.ClampDistance(PFViewFocusKind.Group, 1f, M2V, EarthRadiusView));
            Assert.Equal(PFViewCameraLimits.GroupOrbitMaxMeters * M2V,
                PFViewCameraCore.ClampDistance(PFViewFocusKind.Group, 1e6f, M2V, EarthRadiusView));
            Assert.Equal(150f,
                PFViewCameraCore.ClampDistance(PFViewFocusKind.Group, 150f, M2V, EarthRadiusView));
        }

        [Fact]
        public void BodyDistance_IsClampedByRadiusAndFarClipGuard()
        {
            // 下限 = max(1.2R, 10 单位)：不钻到球心；上限 = MaxBodyDistanceView（< FarClip，U25）
            float min = PFViewCameraCore.ClampDistance(PFViewFocusKind.Body, 0f, M2V, EarthRadiusView);
            Assert.True(min >= EarthRadiusView * 1.2f - 1f);
            float max = PFViewCameraCore.ClampDistance(PFViewFocusKind.Body, 1e12f, M2V, EarthRadiusView);
            Assert.Equal(PFViewCameraCore.MaxBodyDistanceView, max);
            Assert.True(PFViewCameraCore.MaxBodyDistanceView < 1e8f, "远景上限必须小于 FarClip=1e8，否则拉远变空屏");
        }

        [Fact]
        public void ProjectDistance_BodyToGroup_LandsOnFarLimit_NotDefaultOneKm()
        {
            // 从行星档换到编队档：保留朝向，距离投影到 3 km（默认 1 km 只用于首次聚焦）
            float projected = PFViewCameraCore.ProjectDistance(
                PFViewFocusKind.Body, PFViewFocusKind.Group, EarthRadiusView, M2V, EarthRadiusView);
            Assert.Equal(PFViewCameraLimits.GroupOrbitMaxMeters * M2V, projected);
        }

        [Fact]
        public void ProjectDistance_GroupToBody_RespectsNewRadius()
        {
            float projected = PFViewCameraCore.ProjectDistance(
                PFViewFocusKind.Group, PFViewFocusKind.Body, 300f, M2V, EarthRadiusView);
            Assert.True(projected >= EarthRadiusView * 1.2f - 1f, "换到行星档必须退到半径外");
            Assert.True(projected <= PFViewCameraCore.MaxBodyDistanceView);
        }

        [Fact]
        public void DefaultBodyFraming_FitsSphereIntoVerticalFov()
        {
            const float fov = 45f;
            const float aspect = 16f / 9f;
            float d = PFViewCameraCore.DefaultBodyDistanceView(EarthRadiusView, fov, aspect);
            float apparentDeg = 2f * Mathf.Asin(Mathf.Clamp(EarthRadiusView / d, 0f, 1f)) * Mathf.Rad2Deg;

            Assert.True(apparentDeg < fov, $"默认取景视直径 {apparentDeg:F1}° 应小于垂直 FOV {fov}°");
            // 旧行为 2R 时视直径 60° > 45°：反证这条修的是真裁切
            float oldApparent = 2f * Mathf.Asin(0.5f) * Mathf.Rad2Deg;
            Assert.True(oldApparent > fov);
        }

        [Fact]
        public void DefaultGroupDistance_MatchesLimitsDefault()
        {
            Assert.Equal(PFViewCameraLimits.GroupInitOrbitMeters * M2V,
                PFViewCameraCore.DefaultGroupDistanceView(M2V), 3);
        }

        [Fact]
        public void WheelDelta_IsClampedPerFrame_AndZoomIsMultiplicative()
        {
            Assert.Equal(3f, PFViewCameraCore.ClampWheelDelta(10f));
            Assert.Equal(-3f, PFViewCameraCore.ClampWheelDelta(-10f));
            Assert.Equal(0.25f, PFViewCameraCore.ClampWheelDelta(0.25f));

            // 2026-10-18 用户再次切换：滚轮向上（delta>0）= 拉远；向下 = 拉近。
            Assert.Equal(100f / 0.9f, PFViewCameraCore.WheelDistance(100f, 1f), 3);
            Assert.Equal(90f, PFViewCameraCore.WheelDistance(100f, -1f), 3);
            // 暴冲（delta=100）被限到 3 格：100 × (1/0.9)³ ≈ 137.17
            Assert.Equal(100f * 1.371742f, PFViewCameraCore.WheelDistance(100f, 100f), 2);
        }

        [Fact]
        public void Pitch_ClampKeepsLegacyFiveToEightyFive()
        {
            Assert.Equal(5f, PFViewCameraCore.ClampPitch(-20f));
            Assert.Equal(85f, PFViewCameraCore.ClampPitch(120f));
            Assert.Equal(45f, PFViewCameraCore.ClampPitch(45f));
        }

        [Theory]
        [InlineData(0f, 0f, 100f)]
        [InlineData(90f, 100f, 0f)]
        [InlineData(-90f, -100f, 0f)]
        [InlineData(180f, 0f, -100f)]
        [InlineData(270f, -100f, 0f)]
        [InlineData(450f, 100f, 0f)]
        [InlineData(-450f, -100f, 0f)]
        public void GroupOrbitCanReachBothPolesAndContinueAroundFocus(float requestedPitch, float expectedY, float expectedZ)
        {
            float pitch = PFViewCameraCore.ResolvePitch(PFViewFocusKind.Group, requestedPitch);
            Vector3 position = PFViewCameraCore.CameraViewPos(Vector3.zero, 0f, pitch, 100f);
            Assert.Equal(expectedY, position.y, 3);
            Assert.Equal(expectedZ, position.z, 3);
            Assert.Equal(100f, position.magnitude, 3);
        }

        [Theory]
        [InlineData(-120f, 5f)]
        [InlineData(120f, 85f)]
        [InlineData(45f, 45f)]
        public void BodyOrbitKeepsItsExistingPitchBounds(float requestedPitch, float expectedPitch)
            => Assert.Equal(expectedPitch, PFViewCameraCore.ResolvePitch(PFViewFocusKind.Body, requestedPitch));

        [Fact]
        public void FrozenGroupViewKeepsItsUnrestrictedViewingDirection()
        {
            float pitch = PFViewCameraCore.ResolvePitch(PFViewFocusKind.Frozen, -120f);
            Vector3 direction = PFViewCameraCore.OrbitDir(0f, pitch);
            Assert.True(direction.y < 0f);
            Assert.True(direction.z < 0f);
        }

        [Theory]
        [InlineData(0f, 90f)]
        [InlineData(90f, 90f)]
        [InlineData(225f, -90f)]
        [InlineData(45f, 270f)]
        [InlineData(180f, -270f)]
        public void OrbitUpStaysValidAndContinuousWhenCrossingEitherPole(float yaw, float pole)
        {
            Vector3 atPole = PFViewCameraCore.OrbitUp(yaw, pole);
            Assert.Equal(1f, atPole.magnitude, 4);
            Assert.Equal(0f, Vector3.Dot(atPole, PFViewCameraCore.OrbitDir(yaw, pole)), 4);
            Vector3 before = PFViewCameraCore.OrbitUp(yaw, pole - .1f);
            Vector3 after = PFViewCameraCore.OrbitUp(yaw, pole + .1f);
            Assert.True(Vector3.Dot(before, after) > .9999f, "Crossing a pole must not reverse camera up.");
        }

        [Fact]
        public void FullRevolutionWrapKeepsPositionAndOrientationContinuous()
        {
            float beforePitch = PFViewCameraCore.ResolvePitch(PFViewFocusKind.Group, 179.9f);
            float afterPitch = PFViewCameraCore.ResolvePitch(PFViewFocusKind.Group, 180.1f);
            Assert.True(Vector3.Dot(PFViewCameraCore.OrbitDir(30f, beforePitch), PFViewCameraCore.OrbitDir(30f, afterPitch)) > .9999f);
            Assert.True(Vector3.Dot(PFViewCameraCore.OrbitUp(30f, beforePitch), PFViewCameraCore.OrbitUp(30f, afterPitch)) > .9999f);
        }

        [Theory]
        [InlineData(180f)]
        [InlineData(-135f)]
        [InlineData(90f)]
        [InlineData(-90f)]
        public void ResetFromUnrestrictedPitchKeepsTransitionAwayFromFocusAndUpPerpendicular(float startPitch)
        {
            Vector3? previousUp = null;
            for (int step = 0; step <= 100; step++)
            {
                var pose = PFViewCameraCore.InterpolateOrbit(0f, startPitch, 100f, 0f, 45f, 100f, step / 100f);
                Assert.Equal(100f, pose.Offset.magnitude, 3);
                Assert.Equal(1f, pose.Up.magnitude, 4);
                Assert.Equal(0f, Vector3.Dot(pose.Offset.normalized, pose.Up), 4);
                if (previousUp.HasValue) Assert.True(Vector3.Dot(previousUp.Value, pose.Up) > .999f);
                previousUp = pose.Up;
            }
        }

        [Fact]
        public void InvertedResetAtOldSingularityHasStableOrthogonalUp()
        {
            var pose = PFViewCameraCore.InterpolateOrbit(0f, 180f, 100f, 0f, 45f, 100f, .41421356f);
            Assert.Equal(0f, Vector3.Dot(pose.Offset.normalized, pose.Up), 4);
            Assert.True(pose.Up.y < 0f);
        }

        [Fact]
        public void OrbitTransitionCrossesAngleWrapByTheShortPathAndBlendsDistance()
        {
            var pose = PFViewCameraCore.InterpolateOrbit(0f, 170f, 100f, 0f, -170f, 200f, .5f);
            Assert.Equal(0f, pose.Offset.y, 3);
            Assert.Equal(-150f, pose.Offset.z, 3);
            Assert.Equal(-1f, pose.Up.y, 4);
        }

        [Fact]
        public void CameraViewPos_MatchesSphericalOrbitFormula()
        {
            // yaw=0、pitch=45、distance=1：offset = (0, sin45, cos45)
            Vector3 p = PFViewCameraCore.CameraViewPos(Vector3.zero, 0f, 45f, 1f);
            Assert.Equal(0f, p.x, 4);
            Assert.Equal(Mathf.Sin(45f * Mathf.Deg2Rad), p.y, 4);
            Assert.Equal(Mathf.Cos(45f * Mathf.Deg2Rad), p.z, 4);
        }

        [Fact]
        public void CriticallyDampedStep_ConvergesMonotonically()
        {
            float value = 0f;
            float velocity = 0f;
            float prevError = 100f;
            for (int i = 0; i < 120; i++)
            {
                value = PFViewCameraCore.CriticallyDampedStep(value, 100f, ref velocity, 0.2f, 1f / 60f);
                float err = Mathf.Abs(100f - value);
                Assert.True(err <= prevError + 1e-3f, "临界阻尼不应过冲/发散");
                prevError = err;
            }
            Assert.True(Mathf.Abs(100f - value) < 1f, $"2 秒后应基本到位，实际 {value:F2}");
        }

        [Fact]
        public void SmoothStep_HasExpectedEndpoints()
        {
            Assert.Equal(0f, PFViewCameraCore.SmoothStep01(0f));
            Assert.Equal(1f, PFViewCameraCore.SmoothStep01(1f));
            Assert.Equal(0.5f, PFViewCameraCore.SmoothStep01(0.5f), 4);
        }

        [Fact]
        public void CanFire_RequiresTargetDistinctFromShooter()
        {
            Assert.False(PFViewCameraCore.CanFire(-1, 2));
            Assert.False(PFViewCameraCore.CanFire(1, -1));
            Assert.False(PFViewCameraCore.CanFire(1, 1));
            Assert.True(PFViewCameraCore.CanFire(1, 2));
        }
        [Fact]
        public void CameraOutsideSphere_IsPushedToSurface()
        {
            Vector3 center = new Vector3(0f, -5f, 0f);
            // 相机在球内（离球心 1 单位，半径 10）⇒ 推到 10×1.01
            Vector3 pushed = PFViewCameraCore.ClampCameraOutsideSphere(new Vector3(0f, -4f, 0f), center, 10f, 1.01f);
            Assert.Equal(10.1f, Vector3.Distance(pushed, center), 3);
            // 球外原样
            Vector3 outside = new Vector3(0f, -20f, 0f);
            Assert.Equal(outside, PFViewCameraCore.ClampCameraOutsideSphere(outside, center, 10f));
        }
        [Fact]
        public void NearClip_ShrinksNearSurface()
        {
            // 看行星最近档：D=1.2R=600k、表面余量 0.2R=100k ⇒ near 不得大于 80k，否则近侧表面被裁
            float near = PFViewCameraCore.NearClipForCamera(600_000f, 100_000f, 0.01f, 5000f);
            Assert.True(near <= 80_000f);
            Assert.True(near >= 0.01f);
            // 远离表面时仍是 0.35×D（上限 5000）
            Assert.Equal(5000f, PFViewCameraCore.NearClipForCamera(600_000f, 200_000f, 0.01f, 5000f));
        }
    }
}
