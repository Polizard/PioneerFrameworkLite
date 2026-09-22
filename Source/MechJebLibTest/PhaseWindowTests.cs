using System;
using MechJebLib.Functions;
using MechJebLib.Maneuvers;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **相位窗口闭式**（B2.4 第 2 件）的验收。
    ///
    /// 判据分三层：
    /// ① 闭式解 = **独立数值求根**得到的解（不自己信自己）；
    /// ② 按这个 τ 出发，两冲量转移必须**真的交会**（位置对上）；
    /// ③ 计划稿给 B2.4 的**验收线**：LEO→GEO ≤0.5 个目标周期、Δv = 霍曼（≤1.15× 是其上界，
    ///    我们直接钉"等于霍曼"这个更强的断言）。
    /// </summary>
    public class PhaseWindowTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private static readonly V3 Pole = new V3(0, 0, 1);

        /// <summary>赤道圆轨（同向共面 ⇒ 霍曼族成立）。</summary>
        private static OrbitView Circular(double radius, double phaseRad)
        {
            double vc = Math.Sqrt(Mu / radius);
            var r = new V3(radius * Math.Cos(phaseRad), radius * Math.Sin(phaseRad), 0.0);
            var v = new V3(-vc * Math.Sin(phaseRad), vc * Math.Cos(phaseRad), 0.0);
            return OrbitView.FromStateVectors(r, v, Mu, 0.0, Pole, radius * 0.2);
        }

        /// <summary>
        /// ① **与独立数值求根互证**：把"到达时刻两角之差"写成函数 `g(τ)`，扫一遍找符号变化再二分，
        /// 解出来的 τ 必须与闭式一致（模会合周期）。
        /// </summary>
        [Fact]
        public void ClosedForm_MatchesIndependentRootFind()
        {
            OrbitView dep = Circular(7.0e6, 0.0);
            OrbitView tgt = Circular(4.2e7, 1.9); // 相位任意

            Assert.True(PhaseWindow.TrySolveForHohmann(dep, tgt, 0.0, out double tauClosed, out double tof, out string why), why);

            double n1 = dep.meanMotion, n2 = tgt.meanMotion;
            double dTheta0 = dep.PhaseAngle(tgt, 0.0);
            double syn = dep.SynodicPeriod(tgt);

            // g(τ) = 到达时目标角 − 航天器角（0 = 正好交会）
            double G(double tau) => dTheta0 + n2 * (tau + tof) - (n1 * tau + Math.PI);

            double tauScan = 0.0, gPrev = G(0.0);
            const int steps = 20000;
            for (int k = 1; k <= steps; k++)
            {
                double t = syn * k / steps;
                double g = G(t);
                if (gPrev * g <= 0.0) { tauScan = t; break; }
                gPrev = g;
            }
            Assert.True(tauScan > 0.0, "扫一遍应当能找到零点（找不到说明扫参范围不对）");

            double lo = tauScan - syn / steps, hi = tauScan;
            for (int it = 0; it < 200; it++)
            {
                double mid = 0.5 * (lo + hi);
                if (G(lo) * G(mid) <= 0.0) hi = mid; else lo = mid;
            }
            double tauRoot = 0.5 * (lo + hi);

            double wrap(double a, double b) // 两者模 syn 的差
            {
                double d = Math.Abs(a - b) % syn;
                return Math.Min(d, syn - d);
            }

            Assert.True(wrap(tauClosed, tauRoot) < 1e-3,
                $"闭式 {tauClosed:F6}s 与数值根 {tauRoot:F6}s 不一致（syn={syn:F1}s）");
        }

        /// <summary>
        /// ②③ **LEO→GEO 的验收线**：按闭式的 τ 出发，两个冲量必须真的交会，
        /// 且总 Δv 等于解析霍曼；等待时长 ≤ 0.5 个目标周期。
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(1.3)]
        [InlineData(4.7)]
        public void LeoToGeo_MeetsTheAcceptanceLine(double targetPhase)
        {
            const double rLeo = 7.0e6;
            const double rGeo = 4.2e7;           // 6 倍半径差，比计划稿的 4.8 倍更狠
            OrbitView dep = Circular(rLeo, 0.0);
            OrbitView tgt = Circular(rGeo, targetPhase);

            Assert.True(PhaseWindow.TrySolveForHohmann(dep, tgt, 0.0, out double tau, out double tof, out string why), why);

            // ③ 等待 ≤ 0.5 个目标周期（计划稿的验收线）
            Assert.True(tau <= 0.5 * tgt.period,
                $"等待 {tau:F0}s 应 ≤ 半个目标周期 {0.5 * tgt.period:F0}s");

            // 到达时刻的状态
            double ut = tau;
            (V3 r1, V3 v1) = dep.RightHandedStateVectorsAtUT(ut);
            (V3 r2, V3 v2) = tgt.RightHandedStateVectorsAtUT(ut);

            (V3 dv1, double dt1, V3 dv2, double dt2) =
                TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, coplanar: true, rendezvous: true, capture: true);

            // ② 真的交会：转移弧终端必须落在**目标那一刻的位置**上（位置 + 速度）
            (V3 rb, V3 vb) = Shepperd.Solve(Mu, dt1, r1, v1);
            (V3 rt, V3 vt) = Shepperd.Solve(Mu, dt2 - dt1, rb, vb + dv1);
            (V3 rTarget, V3 vTarget) = Shepperd.Solve(Mu, dt2, r2, v2);

            Assert.True(V3.Distance(rt, rTarget) < 1e-6 * rTarget.magnitude,
                $"没交会：位置差 {V3.Distance(rt, rTarget):E3} m");
            Assert.True(V3.Distance(vt + dv2, vTarget) < 1e-6 * vTarget.magnitude,
                $"速度没对上：{V3.Distance(vt + dv2, vTarget):E3} m/s");

            // ③ 代价 = 解析霍曼（比"≤1.15×"更紧的断言）
            (double h1, double h2, double hTof, double _) = Astro.HohmannTransferParameters(Mu, r1, r2);
            double expect = Math.Abs(h1) + Math.Abs(h2);
            double got = dv1.magnitude + dv2.magnitude;
            Assert.True(Math.Abs(got - expect) < 1e-4 * expect,
                $"总 Δv {got:F2} 应等于霍曼 {expect:F2} m/s（转移时长 {dt2 - dt1:F0}s vs t_H {Math.Abs(hTof):F0}s）");
        }

        /// <summary>
        /// ⭐ **复现游戏内的那条路**：`PhaseWindow` → `DeltaVAndTimeForHohmannTransfer`（包装）→ 验交会。
        /// 游戏内 D 段用同一组输入时失败（位置差 3.5e7 m、Δv 2669 比霍曼 3163 还便宜 ⇒ 根本不是霍曼解）
        /// ⇒ 这里把那条路搬进纯环境，看是**包装的锅**还是**我 D 段验证的锅**。
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(0.35)]
        [InlineData(1.9)]
        [InlineData(3.1)]
        [InlineData(4.7)]
        [InlineData(6.0)]
        public void WrapperPath_FromPhaseWindow_DeliversTheTransfer(double targetPhase)
        {
            const double tick = 8830.0;
            OrbitView dep = Circular(7.0e6, 0.0);
            OrbitView tgt = Circular(4.2e7, targetPhase);

            Assert.True(PhaseWindow.TrySolveForHohmann(dep, tgt, tick, out double tau, out double tof, out string why), why);
            double departure = tick + tau;

            (V3 dv1, double ut1, V3 dv2, double ut2) = OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(
                dep, tgt, departure, lagTime: 0.0, fixedTime: false, coplanar: true);

            (V3 rb, V3 vb) = dep.RightHandedStateVectorsAtUT(ut1);
            (V3 rt, V3 vt) = Shepperd.Solve(Mu, ut2 - ut1, rb, vb + dv1);
            (V3 rTgt, V3 vTgt) = tgt.RightHandedStateVectorsAtUT(ut2);

            double posErr = V3.Distance(rt, rTgt);
            double dvTotal = dv1.magnitude + dv2.magnitude;

            Assert.True(posErr < 1e-6 * rTgt.magnitude,
                $"包装这条路没有交会：位置差 {posErr:E3} m｜ut1={ut1:F0} ut2={ut2:F0} " +
                $"（τ={tau:F0} 出发={departure:F0}）｜总 Δv={dvTotal:F0}");
        }

        /// <summary>同周期（同轨）⇒ 没有相位窗口，必须**明说**而不是给个数。</summary>
        [Fact]
        public void SamePeriod_HasNoPhaseWindow_AndSaysSo()
        {
            OrbitView a = Circular(7.0e6, 0.0);
            OrbitView b = Circular(7.0e6, 0.5);

            Assert.False(PhaseWindow.TrySolveForHohmann(a, b, 0.0, out _, out _, out string why));
            Assert.Contains("周期", why);
        }
    }
}
