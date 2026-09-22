using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 交会"恢复/兜底"纯核心的护栏（2026-09-16）。
    /// 动机是游戏内实测的一次失败：精确交会用"冻结到达时刻 + 单脉冲修正"，在近地点要逆行修正
    /// （会撞行星）→ 整轮跳过 → 误差涨到 2733.7 m/s → 判不可行终止。MechJeb 的做法是
    /// "时间当自由变量 + 修正放节点 + 饱和执行 + 失败就重规划"，本核心实现其中可纯测的部分。
    /// </summary>
    public class RendezvousCoreTests
    {
        private const double MuEarth = 2.0e14;                 // 与 Def 一致
        private const double EarthRadius = 5.0e6;
        private const double SafeRadius = EarthRadius + 1.0e5; // 地表 + 大气 100km

        /// <summary>圆轨状态：半径 r、相位角 angle（XZ 平面、顺行）。</summary>
        private static (DVector3 pos, DVector3 vel) Circular(double r, double angle)
        {
            var pos = new DVector3(Math.Cos(angle) * r, 0.0, Math.Sin(angle) * r);
            double v = Math.Sqrt(MuEarth / r);
            var vel = new DVector3(-Math.Sin(angle) * v, 0.0, Math.Cos(angle) * v);
            return (pos, vel);
        }

        // ── S2：安全缩放 ──────────────────────────────────────────────────────

        /// <summary>完全安全的修正 ⇒ 缩放上限 = 1（不做无谓裁剪）。</summary>
        [Fact]
        public void MaxSafeDvScale_SafeCorrection_ReturnsOne()
        {
            var (pos, vel) = Circular(5.5e6, 0.0);
            var dv = vel.normalized * 10.0;                    // 顺行小修正：抬近地点，安全
            double k = RendezvousCore.MaxSafeDvScale(pos, vel, dv, SafeRadius, MuEarth);
            Assert.Equal(1.0, k, 6);
        }

        /// <summary>
        /// 大幅逆行（近地点掉进地表）⇒ 必须被裁到安全范围内（不是硬拒，也不是放行）。
        /// 物理复核：圆轨上小幅逆行其实**安全**（当前点变成新远地点、近地点只掉一点），
        /// 大概要从 ~100 m/s 起才危险 ⇒ 参考系用"圆轨 + −400 m/s"而不是"椭圆近地点 + −300"。
        /// </summary>
        [Fact]
        public void MaxSafeDvScale_LargeRetrograde_GetsTrimmedToSafeRange()
        {
            var (pos, vel) = Circular(5.5e6, 0.0);
            var dv = vel.normalized * -400.0;

            Assert.False(OrbitMath.IsOrbitSafe(pos, vel + dv, SafeRadius, MuEarth));
            double k = RendezvousCore.MaxSafeDvScale(pos, vel, dv, SafeRadius, MuEarth);

            Assert.True(k > 0.0 && k < 1.0, $"应被裁剪到中间值，实际 k={k:F3}");
            Assert.True(OrbitMath.IsOrbitSafe(pos, vel + dv * k, SafeRadius, MuEarth));   // 裁剪后必须安全
            Assert.True(400.0 * k < 200.0, $"裁剪量应在百 m/s 级，实际 {400.0 * k:F0} m/s");
        }

        /// <summary>裁剪是单调二分：取出 k 后必须"安全"，且 2k 必须"不安全"（除非 k=1）。</summary>
        [Fact]
        public void MaxSafeDvScale_IsMonotoneCut()
        {
            // 圆轨上做大幅逆行（低于圆速 ⇒ 近地点掉下去），但小量仍安全
            var (pos, vel) = Circular(5.5e6, 0.0);
            double v = vel.magnitude;
            var dv = vel.normalized * (-(v * 0.25));           // 减 25% 速度：近地点明显下降

            double k = RendezvousCore.MaxSafeDvScale(pos, vel, dv, SafeRadius, MuEarth);
            Assert.True(k > 0.0 && k < 1.0);
            Assert.True(OrbitMath.IsOrbitSafe(pos, vel + dv * k, SafeRadius, MuEarth));
            if (k * 2.0 <= 1.0)
                Assert.False(OrbitMath.IsOrbitSafe(pos, vel + dv * (k * 2.0), SafeRadius, MuEarth));
        }

        // ── S1：重规划 ────────────────────────────────────────────────────────
        /// <summary>
        /// **模型能力边界（实测标定）**：同轨目标相位差 120° 时，"单弧 Lambert 重规划"无便宜解——
        /// 两船同轨 ⇒ 等的再久相位差也不变，而省 Δv 的解法要"进相位轨道滑行 N 圈"，
        /// Gooding 是**单圈**求解器表达不了（实测最小单弧解 ≈3,848 m/s @ tof≈1.05T）。
        /// ⇒ 必须诚实返回 NoPlan，由调用方走相位规划（S3）/滚动接近，而不是硬烧。
        /// </summary>
        [Fact]
        public void PlanRecovery_CoOrbitalPhaseError_ReportsNoPlanRatherThanHardChase()
        {
            double r = 5.5e6, nowTick = 1_000.0;
            var (r1, v1) = Circular(r, 0.0);
            double targetAngle0 = 2.0 * Math.PI / 3.0;
            double omega = Math.Sqrt(MuEarth / (r * r * r));
            (DVector3 pos, DVector3 vel) Predict(double tick)
            {
                return Circular(r, targetAngle0 + omega * (tick - nowTick));
            }

            // 预算给到 500：单弧解要 3.8 km/s，远超预算 ⇒ 必须 NoPlan（不许返回硬追解）
            var plan = RendezvousCore.PlanRecovery(r1, v1, nowTick, Predict, MuEarth, SafeRadius,
                r, prograde: true, maxDepartureDv: 500.0);
            Assert.False(plan.Valid, $"不应给出硬追解：dv1={plan.DepartureDv.magnitude:F0} total={plan.TotalDv:F0}");

            // 而相位规划在同一场景下必须是"便宜"的（Δv 与圈数成反比，量级几十~几百 m/s）
            var ph = RendezvousCore.PlanPhasing(r1, v1, MuEarth, deltaPhaseRad: 2.0 * Math.PI / 3.0, maxRevs: 8);
            Assert.True(ph.Valid);
            Assert.True(ph.TotalDv < 400.0, $"相位规划应远便宜于硬追，实际 {ph.TotalDv:F0} m/s");
        }

        /// <summary>预算不够时不许返回"看起来可行"的计划（出发段 Δv 上限必须被遵守）。</summary>
        [Fact]
        public void PlanRecovery_RespectsDepartureBudget()
        {
            double r1r = 5.5e6, r2r = 2.6322e7;                // LEO → GEO 级
            double nowTick = 0.0;
            var (r1, v1) = Circular(r1r, 0.0);
            double omega2 = Math.Sqrt(MuEarth / (r2r * r2r * r2r));
            (DVector3 pos, DVector3 vel) Predict(double tick)
            {
                double ang = omega2 * tick;
                var pos = new DVector3(Math.Cos(ang) * r2r, 0.0, Math.Sin(ang) * r2r);
                double v = Math.Sqrt(MuEarth / r2r);
                var vel = new DVector3(-Math.Sin(ang) * v, 0.0, Math.Cos(ang) * v);
                return (pos, vel);
            }

            // 预算极小 ⇒ 只能返回 NoPlan（不许超预算）
            var tight = RendezvousCore.PlanRecovery(r1, v1, nowTick, Predict, MuEarth, SafeRadius,
                r1r, prograde: true, maxDepartureDv: 100.0);
            Assert.False(tight.Valid);

            // 预算给足 ⇒ 找到解，且出发段 Δv 落在霍曼量级（1.5–2.5 km/s）
            var ok = RendezvousCore.PlanRecovery(r1, v1, nowTick, Predict, MuEarth, SafeRadius,
                r1r, prograde: true, maxDepartureDv: 3000.0);
            Assert.True(ok.Valid);
            Assert.InRange(ok.DepartureDv.magnitude, 1400.0, 2600.0);
        }
    }
}
