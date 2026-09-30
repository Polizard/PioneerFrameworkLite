using System;
using System.Text;
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **燃烧弧预测的"相位选择"行为判据（2026-09-30 建）** —— 为"改 `BurnArc.Steps`"这类**决策改动**立的门。
    ///
    /// ## 为什么立这条（而不是逐位基线）
    /// 相位扫描只用预测结果里的一个量：**末态偏心率 e**，再从 7 档里挑最小的那档当点火相位。
    /// 所以"改积分步数会不会改计划"这个问题，**精确**的表述是两件事：
    ///   ① **argmin 那一档不能变**（变了就是换了点火相位 = 计划被改）；
    ///   ② 7 个 e 值的误差必须**远小于"第一名与第二名之差"（判别余量）** —— 否则近并列时选择会被噪声翻掉。
    /// 逐位基线（像 `NextManeuverBitwiseBaselineTests` 那样）只回答"变没变**位**"，对**本轮这种"已知会变位但远在判别尺度之下"**
    /// 的改动给不出可接受的结论（它必然红，且红了也不说明危害有多大）。⇒ 本文件用**有尺度的判据**取代它。
    ///
    /// ## 判据（三条，都对**高分辨率参考解**而言）
    /// · 参考解 = 同一套算式（本文件内的复刻，见 `Predict`）跑 **6400 步**；复刻先在**生产步数**下与
    ///   `BurnArc.TryPredict` 的 `e`/`sma` **逐位相等**才算可信（"仪器必须先被验证"）。
    /// · **① argmin 一致**：7 档里最小的那一档，生产步数与参考解相同。
    /// · **② 余量相对精度**：`|Δe| ≤ 判别余量 / 100`（逐档，7 档都算）。
    /// · **③ 覆盖面**：几何 × **燃烧时长**（×0.25 / ×1 / ×4 / ×16）扫过 —— 决定精度的是 `dt/T` 而不是步数，
    ///   所以"改步数"的判据必须带上燃烧时长这一维（实测：16× 燃烧时长仍留 5 个数量级余量）。
    ///
    /// ⚠ 本文件**不**断言任何耗时（耗时读数在 `BurnArcPhaseScanCostTests`，且带口径横幅）。
    /// </summary>
    public class BurnArcPhaseChoiceTests
    {
        private readonly ITestOutputHelper _out;
        public BurnArcPhaseChoiceTests(ITestOutputHelper output) => _out = output;

        private const int ReferenceSteps = 6400;
        private static readonly double MuEarth = OrbitMath.GM;   // `OrbitMath.GM` 不是编译期常量

        /// <summary>地球档：e=0.3 的椭圆（与 `BurnArcPhaseScanCostTests` 同构造，便于读数对照）。</summary>
        private static OrbitView Orbit(double r0, double mu)
        {
            double vc = Math.Sqrt(mu / r0);
            return OrbitView.FromStateVectors(new DVector3(r0, 0, 0), new DVector3(0, Math.Sqrt(1.3) * vc, 0),
                mu, 0.0, new DVector3(0, 0, 1), r0 * 0.2);
        }

        // ── 以下四个函数与 `BurnArc` 的 `Deriv` / `Rk4` / `CurrentDir` / `Predict` **逐句同式**（复刻只为能改步数）──

        private static (DVector3 dr, DVector3 dv) Deriv(double mu, DVector3 r, DVector3 v, double a, double rad, double tan, double nor)
        {
            double rmag = r.magnitude;
            DVector3 rhat = r / rmag;
            DVector3 hhat = DVector3.Cross(r, v).normalized;
            DVector3 that = DVector3.Cross(hhat, rhat);

            DVector3 dir = rad * rhat + tan * that + nor * hhat;
            if (dir.sqrMagnitude > 0.0) dir = dir.normalized;

            return (v, -(mu / (rmag * rmag)) * rhat + a * dir);
        }

        private static DVector3 CurrentDir(DVector3 r, DVector3 v, double rad, double tan, double nor)
        {
            DVector3 rhat = r.normalized;
            DVector3 hhat = DVector3.Cross(r, v).normalized;
            DVector3 that = DVector3.Cross(hhat, rhat);
            DVector3 dir = rad * rhat + tan * that + nor * hhat;
            return dir.sqrMagnitude > 0.0 ? dir.normalized : dir;
        }

        private static (DVector3 r, DVector3 v) Rk4(double mu, DVector3 r, DVector3 v, double dt, double a, double rad, double tan, double nor)
        {
            (DVector3 dr1, DVector3 dv1) = Deriv(mu, r, v, a, rad, tan, nor);
            (DVector3 dr2, DVector3 dv2) = Deriv(mu, r + 0.5 * dt * dr1, v + 0.5 * dt * dv1, a, rad, tan, nor);
            (DVector3 dr3, DVector3 dv3) = Deriv(mu, r + 0.5 * dt * dr2, v + 0.5 * dt * dv2, a, rad, tan, nor);
            (DVector3 dr4, DVector3 dv4) = Deriv(mu, r + dt * dr3, v + dt * dv3, a, rad, tan, nor);
            return (r + (dt / 6.0) * (dr1 + 2.0 * dr2 + 2.0 * dr3 + dr4),
                    v + (dt / 6.0) * (dv1 + 2.0 * dv2 + 2.0 * dv3 + dv4));
        }

        /// <summary>复刻 `BurnArc.Predict`（步数可给）⇒ 末态 e 与 sma。</summary>
        private static (double e, double sma) Predict(OrbitView o, double nodeUt, DVector3 dv, double burnSeconds,
            double ignitionShiftSeconds, int steps)
        {
            double aMean = dv.magnitude / burnSeconds;
            double startUt = nodeUt - 0.5 * burnSeconds + ignitionShiftSeconds;
            (DVector3 r, DVector3 v) = o.RightHandedStateVectorsAtUT(startUt);

            (DVector3 rNode, DVector3 vNode) = o.RightHandedStateVectorsAtUT(nodeUt);
            DVector3 rhat0 = rNode.normalized;
            DVector3 hhat0 = DVector3.Cross(rNode, vNode).normalized;
            DVector3 that0 = DVector3.Cross(hhat0, rhat0);
            double rad = DVector3.Dot(dv, rhat0);
            double tan = DVector3.Dot(dv, that0);
            double nor = DVector3.Dot(dv, hhat0);

            double dt = burnSeconds / steps;
            for (int i = 0; i < steps; i++) (r, v) = Rk4(o.mu, r, v, dt, aMean, rad, tan, nor);

            var ov = OrbitView.FromStateVectors(r, v, o.mu, startUt + burnSeconds, o.equatorialAxis, o.bodyRadius,
                o.soiRadiusApprox, o.angularVelocity);
            return (ov.eccentricity, ov.semiMajorAxis);
        }

        private const int ShiftCount = 7;
        private static double ShiftOf(int k) => -6.0 + 2.0 * k;

        /// <summary>7 档 e 值（用生产 `BurnArc.TryPredict`）。</summary>
        private static double[] ScanProduction(OrbitView o, double nodeUt, DVector3 dv, double burnSeconds, double delay)
        {
            var es = new double[ShiftCount];
            for (int k = 0; k < ShiftCount; k++)
                es[k] = BurnArc.TryPredict(o, nodeUt, dv, burnSeconds, ShiftOf(k) + delay, out BurnArc.Result r, out _)
                    ? r.eccentricity : double.NaN;
            return es;
        }

        /// <summary>7 档 e 值（用复刻，步数可给）⇒ 参考解那一臂。</summary>
        private static double[] ScanReplica(OrbitView o, double nodeUt, DVector3 dv, double burnSeconds, double delay, int steps)
        {
            var es = new double[ShiftCount];
            for (int k = 0; k < ShiftCount; k++)
                es[k] = Predict(o, nodeUt, dv, burnSeconds, ShiftOf(k) + delay, steps).e;
            return es;
        }

        private static int Argmin(double[] es)
        {
            int idx = 0;
            for (int k = 1; k < ShiftCount; k++) if (es[k] < es[idx]) idx = k;
            return idx;
        }

        /// <summary>第一名与第二名之差（判别余量）。</summary>
        private static double RunnerUpGap(double[] es)
        {
            var sorted = (double[])es.Clone();
            Array.Sort(sorted);
            return sorted[1] - sorted[0];
        }

        [Fact]
        public void PhaseChoice_MatchesHighResolutionReference_OnGeometryAndBurnSweep()
        {
            var geoms = new (string name, double r0, double mu, double node)[]
            {
                ("地球 7.0e6 @300", 7.0e6, MuEarth, 300.0),
                ("地球 7.0e6 @1500", 7.0e6, MuEarth, 1500.0),
                ("地球 8.0e6 @900", 8.0e6, MuEarth, 900.0),
                ("地球 11.0e6 @2500", 1.1e7, MuEarth, 2500.0),
                ("月球 2.9e6 @300", 2.9e6, 5.0e12, 300.0),
            };
            double[] burnScales = { 0.25, 1.0, 4.0, 16.0 };
            double delay = BurnArc.ExecutionDelaySeconds;

            int worstArgmin = -1;
            double worstRatio = 0.0, minGap = double.PositiveInfinity;
            var sb = new StringBuilder();

            foreach (var g in geoms)
            {
                var o = Orbit(g.r0, g.mu);
                DVector3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, g.node);
                double baseBurn = dv.magnitude * 74_995.1 / 560_000.0;

                foreach (double bs in burnScales)
                {
                    double burnSeconds = baseBurn * bs;

                    // 仪器自校验：复刻在**生产步数**下必须与生产逐位相等（e 与 sma 都查）
                    var prod = ScanProduction(o, g.node, dv, burnSeconds, delay);
                    BurnArc.TryPredict(o, g.node, dv, burnSeconds, delay, out BurnArc.Result r1, out _);
                    (double eRep, double smaRep) = Predict(o, g.node, dv, burnSeconds, delay, BurnArc.Steps);
                    Assert.True(eRep == r1.eccentricity && smaRep == r1.sma,
                        $"复刻与生产在 {BurnArc.Steps} 步下不逐位相等（e {eRep:R} vs {r1.eccentricity:R}）⇒ 复刻不可信");

                    var reference = ScanReplica(o, g.node, dv, burnSeconds, delay, ReferenceSteps);
                    int iProd = Argmin(prod), iRef = Argmin(reference);
                    double gap = RunnerUpGap(reference);
                    minGap = Math.Min(minGap, gap);

                    double maxDe = 0.0;
                    for (int k = 0; k < ShiftCount; k++)
                        maxDe = Math.Max(maxDe, Math.Abs(prod[k] - reference[k]));
                    double ratio = maxDe / gap;
                    worstRatio = Math.Max(worstRatio, ratio);
                    if (iProd != iRef) worstArgmin = iProd;

                    sb.AppendLine($"    {g.name,-18} burn×{bs,5:F2}（{burnSeconds,7:F1}s）" +
                                  $"dt/T≈{burnSeconds / BurnArc.Steps / (2 * Math.PI * Math.Sqrt(Math.Pow(g.r0, 3.0) / g.mu)):E2} " +
                                  $"｜argmin {iProd}/{iRef} {(iProd == iRef ? "✓" : "✗")}" +
                                  $"｜max|Δe| {maxDe:E3}｜余量 {gap:E3}｜比 {ratio:E2}");
                }
            }

            _out.WriteLine($"相位选择判据（生产 {BurnArc.Steps} 步 vs 参考 {ReferenceSteps} 步；几何 × 燃烧时长 共 {geoms.Length * burnScales.Length} 格）：");
            _out.WriteLine(sb.ToString().TrimEnd());
            _out.WriteLine($"    汇总：argmin 不一致 {(worstArgmin < 0 ? "0 格 ✓" : $"{worstArgmin}（**要重核**）")}｜" +
                           $"最差 |Δe|/余量 = {worstRatio:E2}（判据 ≤ 1e-2）｜最小余量 {minGap:E3}");

            Assert.True(worstArgmin < 0, "有几何的 7 档 argmin 与高分辨率参考解**不同** ⇒ 点火相位被改了，这不是纯性能改动");
            Assert.True(worstRatio <= 1e-2,
                $"7 档 e 的最大误差达到判别余量的 {worstRatio:E2}（判据 ≤ 1e-2）⇒ 近并列时选择可能被积分误差翻掉，要加步数");
        }
    }
}
