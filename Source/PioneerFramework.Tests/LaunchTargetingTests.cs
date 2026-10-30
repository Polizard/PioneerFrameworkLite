using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 自动发射的目标高度夹取（2026-10-27）：基本低轨、SOI 一半、邻居 1/4、下限拒绝。
    /// 数值取地球/月球量级做回归，另加两条"放不下"的合成用例。
    /// </summary>
    public class LaunchTargetingTests
    {
        private const double EarthR = 5_000_000.0;
        private const double EarthMu = 2.0e14;        // vC(5500km) ≈ 6030
        private const double EarthAtmo = 100_000.0;
        private const double EarthSoi = 1.5e9;        // 远大于目标
        private const double EarthMoonDistance = 3.844e8;

        [Fact]
        public void EarthLike_DefaultLowOrbit_Is500km()
        {
            var r = LaunchTargeting.Compute(new LaunchTargetInputs(
                EarthR, EarthMu, EarthAtmo, EarthSoi, EarthMoonDistance));

            Assert.True(r.Feasible);
            Assert.Equal(500_000.0, r.TargetAltitude, 0);
            Assert.Equal(6_030.0, r.VCircular, 0);
            Assert.False(r.ClampedBySoi);
            Assert.False(r.ClampedByNeighbor);
            Assert.Equal(8.0, r.SurfaceGravity, 1);
        }

        [Fact]
        public void MoonLike_Airless_TargetIs10PercentRadius()
        {
            var r = LaunchTargeting.Compute(new LaunchTargetInputs(
                1.737e6, 4.9e12, 0.0, 6.6e7, EarthMoonDistance));

            Assert.True(r.Feasible);
            Assert.Equal(173_700.0, r.TargetAltitude, 0);
            Assert.Equal(1_601.0, r.VCircular, 0);
        }

        [Fact]
        public void SmallSoi_ClampsToHalfSoi()
        {
            // SOI = R + 300 km ⇒ hSoi = 150 km < hBase 500 km，但 > 下限 100 km
            var r = LaunchTargeting.Compute(new LaunchTargetInputs(
                EarthR, EarthMu, 0.0, EarthR + 300_000.0, double.PositiveInfinity));

            Assert.True(r.Feasible);
            Assert.Equal(150_000.0, r.TargetAltitude, 0);
            Assert.True(r.ClampedBySoi);
            Assert.Contains("SOI", r.Reason);
        }

        [Fact]
        public void TinySoi_IsRejected()
        {
            // SOI = R + 10 km ⇒ hSoi = 5 km < 下限 100 km
            var r = LaunchTargeting.Compute(new LaunchTargetInputs(
                EarthR, EarthMu, 0.0, EarthR + 10_000.0, double.PositiveInfinity));

            Assert.False(r.Feasible);
            Assert.Contains("下限", r.Reason);
        }

        [Fact]
        public void CloseNeighbor_IsRejected()
        {
            // 邻居中心距 2R ⇒ hNear = 0.25·2R − R = −0.5R
            var r = LaunchTargeting.Compute(new LaunchTargetInputs(
                1.0e6, 1.0e10, 0.0, double.NaN, 2.0e6));

            Assert.False(r.Feasible);
        }

        [Fact]
        public void ForBody_EarthLike_MatchesLegacyShape()
        {
            var prms = LaunchGuidanceParams.ForBody(
                new LaunchTargetInputs(EarthR, EarthMu, EarthAtmo, EarthSoi, EarthMoonDistance),
                out LaunchTargetResult target);

            Assert.True(target.Feasible);
            Assert.Equal(5_500_000.0, prms.rTarget, 0);
            Assert.Equal(600.0, prms.tau, 0);
            Assert.Equal(500.0, prms.vAscent, 0);
            // 2026-10-31（合并期重定基线）：旧期望 20 km / 450 km 是 10-27 的"0.2×大气 / 0.9×目标高度"。
            // `LaunchGuidanceCore.ForBody` 在 10-28/10-29 两次重写后（见该函数注释）实际给的是：
            //   起点 = 1 km（"只到清空发射台"，火箭 10 km 出生 ⇒ 出生即越线、直接进转平段）；
            //   终点 = min(0.85×大气, max(起点+1, 0.85×目标高度)) = min(85 km, 425 km) = 85 km。
            // 这条测试是**我们自己的**（未跟踪），当初没跟着改 ⇒ 一直红；不是合并引入的。
            Assert.Equal(1_000.0, prms.altPitchStart, 0);
            Assert.Equal(85_000.0, prms.altPitchEnd, 0);
            Assert.Equal(10_000.0, prms.tolR, 0);
            Assert.Equal(10.0, prms.tolV, 1);
        }

        [Fact]
        public void ForBody_Infeasible_ReturnsZeroTarget()
        {
            var prms = LaunchGuidanceParams.ForBody(
                new LaunchTargetInputs(EarthR, EarthMu, 0.0, EarthR + 10_000.0, double.PositiveInfinity),
                out LaunchTargetResult target);

            Assert.False(target.Feasible);
            Assert.Equal(0.0, prms.rTarget, 6);
        }
    }
}
