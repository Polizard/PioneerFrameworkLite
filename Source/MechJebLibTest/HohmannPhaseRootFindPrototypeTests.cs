using System;
using PioneerFramework;
using PioneerFramework.Pure.Tests;   // CircularTarget
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **原型①：窗口搜索 → 相位标量求根**（2026-10-21，只量不接）。
    ///
    /// 思路：霍曼族的物理条件是"到达时目标正好在烧点的对面"——
    ///   `g(t) = angle(r_self(t), r_target(t+τ)) − π = 0`，其中 `τ = π√((r1+r2)³/(8μ))`。
    /// 这个 g 只需要**两次轨道传播 + 一个点积**（不用 Lambert），对 t 光滑、按会合周期重复。
    /// 于是：粗扫找变号 → 二分细化 → 只在该根上算一次 Δv。
    /// 对照：现有 `TwoImpulseTransfer` 的 3 维多起点搜索（12 种子 × 60 sweeps）。
    ///
    /// 本文件**不碰生产**；只回答"1 维路子能不能复现搜索的窗口与 Δv"。
    /// </summary>
    public class HohmannPhaseRootFindPrototypeTests
    {
        private readonly ITestOutputHelper _out;
        public HohmannPhaseRootFindPrototypeTests(ITestOutputHelper o) => _out = o;

        private const double Mu = 2.0e14;
        private const double BodyRadius = 5.0e6;
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);

        private static OrbitView Circular(double radius, double phaseDeg, double k = 0.0)
        {
            var t = CircularTarget.Create(radius, Mu, phaseDeg * Math.PI / 180.0, OrbitNormal);
            return OrbitView.FromStateVectors(t.PosAt(0.0), t.VelAt(0.0) * (1.0 + k), Mu, 0.0,
                t.PlaneNormalAt(0.0), BodyRadius);
        }

        /// <summary>
        /// 相位残差 `g(t)`：把自船推到 t、目标推到 t+τ(t)，量两半径夹角与 π 的差（弧度，−π..π）。
        /// 只用传播，不用 Lambert ⇒ 单次评估 ~2 次 Kepler 解。
        /// </summary>
        private static double PhaseResidual(OrbitView self, OrbitView target, double t, out double tau,
            out double rSelf, out double rTarget)
        {
            (V3 r1, V3 _) = self.RightHandedStateVectorsAtUT(t);
            double a1 = r1.magnitude;
            // τ 依赖 r2 ⇒ 先粗估一轮再回代一次（两步足够，圆轨下一轮就收敛）
            double tau0 = Math.PI * Math.Sqrt(Math.Pow(2.0 * a1, 3.0) / (8.0 * self.mu));
            (V3 r2a, V3 _) = target.RightHandedStateVectorsAtUT(t + tau0);
            double a2 = r2a.magnitude;
            tau = Math.PI * Math.Sqrt(Math.Pow(a1 + a2, 3.0) / (8.0 * self.mu));
            (V3 r2, V3 _) = target.RightHandedStateVectorsAtUT(t + tau);
            rSelf = a1;
            rTarget = r2.magnitude;
            V3 h = V3.Cross(r1, self.RightHandedStateVectorsAtUT(t).vel);
            double ang = Math.Atan2(V3.Dot(h.normalized, V3.Cross(r1, r2)), V3.Dot(r1, r2));
            // ⚠ 用 `sin(ang)` 当残差、再要求 `cos(ang) < 0`：这样只在"对面"（ang=±π）处变号，
            //   不会把 atan2 的 ±π 分支切口当成根（第一版就是被这个切口骗了，窗口差出 4 圈）。
            return Math.Sin(ang);
        }

        /// <summary>两半径在轨道面内的有符号夹角（弧度，−π..π）。</summary>
        private static double PhaseAngle(OrbitView self, OrbitView target, double t)
        {
            (V3 r1, V3 v1) = self.RightHandedStateVectorsAtUT(t);
            double a1 = r1.magnitude;
            double tau0 = Math.PI * Math.Sqrt(Math.Pow(2.0 * a1, 3.0) / (8.0 * self.mu));
            (V3 r2a, V3 _) = target.RightHandedStateVectorsAtUT(t + tau0);
            double tau = Math.PI * Math.Sqrt(Math.Pow(a1 + r2a.magnitude, 3.0) / (8.0 * self.mu));
            (V3 r2, V3 _) = target.RightHandedStateVectorsAtUT(t + tau);
            V3 h = V3.Cross(r1, v1);
            return Math.Atan2(V3.Dot(h.normalized, V3.Cross(r1, r2)), V3.Dot(r1, r2));
        }

        /// <summary>闭式两发（同 `RendezvousLadder` 那一刀的口径）：沿当地速度方向。</summary>
        private static double ClosedFormDv(OrbitView self, OrbitView target, double burnT, double tau)
        {
            (V3 rb, V3 vb) = self.RightHandedStateVectorsAtUT(burnT);
            (V3 ra, V3 va) = target.RightHandedStateVectorsAtUT(burnT + tau);
            double r1 = rb.magnitude, r2 = ra.magnitude;
            if (!(r1 > 0.0) || !(r2 > 0.0)) return double.NaN;
            double vNeed = Math.Sqrt(self.mu * (2.0 / r1 - 2.0 / (r1 + r2)));
            double vArr = Math.Sqrt(self.mu * (2.0 / r2 - 2.0 / (r1 + r2)));
            return Math.Abs(vNeed - vb.magnitude) + Math.Abs(va.magnitude - vArr);
        }

        /// <summary>1 维求根找"最早的相位窗口"，返回 (t*, τ, Δv)。</summary>
        private static bool FindFirstPhaseWindow(OrbitView self, OrbitView target, double synodicSeconds,
            out double burnT, out double tauOut, out double dv)
        {
            burnT = double.NaN; tauOut = double.NaN; dv = double.NaN;
            const int N = 48;
            double prevT = 0.0;
            double prevG = PhaseResidual(self, target, prevT, out _, out _, out _);
            double bestT = double.NaN, bestG = double.MaxValue;
            for (int i = 1; i <= N; i++)
            {
                double t = synodicSeconds * i / N;
                double g = PhaseResidual(self, target, t, out _, out _, out _);
                bool opposite = g * prevG < 0.0 && Math.Cos(PhaseAngle(self, target, t)) < 0.0;
                if (opposite)
                {
                    // 二分细化（20 次 ⇒ 区间 1/1e6）
                    double lo = prevT, hi = t, glo = prevG;
                    for (int k = 0; k < 20; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        double gm = PhaseResidual(self, target, mid, out _, out _, out _);
                        if (Math.Sign(gm) == Math.Sign(glo)) { lo = mid; glo = gm; }
                        else hi = mid;
                    }
                    double root = 0.5 * (lo + hi);
                    if (double.IsNaN(bestT) || root < bestT) { bestT = root; bestG = 0.0; }
                    break;
                }
                prevT = t; prevG = g;
            }
            if (double.IsNaN(bestT)) return false;
            PhaseResidual(self, target, bestT, out double tau, out _, out _);
            burnT = bestT; tauOut = tau; dv = ClosedFormDv(self, target, bestT, tau);
            return !double.IsNaN(dv);
        }

        [Fact]
        public void PhaseRootFind_Vs_Search_OnCircularCorpus()
        {
            double[] radii = { 5.5e6, 6.0e6, 7.0e6, 7.5e6 };
            double[] ratios = { 1.01, 1.02, 1.05, 1.10, 1.20, 1.35 };
            double[] phases = { 0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330 };

            int cases = 0, ok = 0, dvWithin1pct = 0, burnWithin60s = 0;
            double sumDvRel = 0.0, maxDvRel = 0.0, sumBurnDt = 0.0, maxBurnDt = 0.0;

            foreach (double r in radii)
                foreach (double ratio in ratios)
                    foreach (double ph in phases)
                    {
                        var self = Circular(r, 0.0);
                        var tgt = Circular(r * ratio, ph);
                        cases++;

                        double dt1, dvA;
                        try
                        {
                            (V3 a1, double t1, V3 a2, double _) =
                                OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, tgt, 0.0, coplanar: false);
                            if (double.IsNaN(t1) || double.IsInfinity(t1)) continue;
                            dt1 = t1; dvA = a1.magnitude + a2.magnitude;
                        }
                        catch (Exception) { continue; }

                        // 会合周期的解析上界：2π/|n2−n1|
                        double n1 = Math.Sqrt(Mu / Math.Pow(r, 3.0));
                        double n2 = Math.Sqrt(Mu / Math.Pow(r * ratio, 3.0));
                        double syn = 2.0 * Math.PI / Math.Abs(n2 - n1);
                        if (!(syn > 0.0) || double.IsInfinity(syn)) continue;

                        if (!FindFirstPhaseWindow(self, tgt, syn, out double burnT, out double tau, out double dvB))
                            continue;
                        ok++;

                        double rel = dvA > 1e-9 ? Math.Abs(dvB - dvA) / dvA : 0.0;
                        double dt = Math.Abs(burnT - dt1);
                        sumDvRel += rel; if (rel > maxDvRel) maxDvRel = rel;
                        sumBurnDt += dt; if (dt > maxBurnDt) maxBurnDt = dt;
                        if (rel <= 0.01) dvWithin1pct++;
                        if (dt <= 60.0) burnWithin60s++;
                    }

            _out.WriteLine($"[相位求根] 用例={cases} 求根成功={ok}");
            if (ok > 0)
            {
                _out.WriteLine($"[相位求根] Δv 相对差：中位={sumDvRel / ok:P2} 最大={maxDvRel:P2}｜≤1% 的 {dvWithin1pct * 100.0 / ok:F0}%");
                _out.WriteLine($"[相位求根] 窗口时刻差：中位={sumBurnDt / ok:F0}s 最大={maxBurnDt:F0}s｜≤60s 的 {burnWithin60s * 100.0 / ok:F0}%");
            }
            Assert.True(ok > 100, $"覆盖不足：{ok}");
        }
        /// <summary>残差（只返回 sin(θ)），供牛顿用。</summary>
        private static double Residual(OrbitView self, OrbitView target, double t)
            => PhaseResidual(self, target, t, out _, out _, out _);

        /// <summary>
        /// **解析种子 + 牛顿**：从 `EstimateHohmannWaitSeconds` 的圆模型窗口出发，用中心差分数值导数走 3–6 步。
        /// 返回评估次数（每次评估 = 3 次轨道传播），用于估成本。
        /// </summary>
        private static bool NewtonRefine(OrbitView self, OrbitView target, double seed,
            out double root, out int evals)
        {
            root = double.NaN; evals = 0;
            if (double.IsNaN(seed) || seed < 0.0) return false;
            double t = seed;
            const double h = 20.0;                       // 差分步长（秒）
            double maxStep = 0.25 * Math.Abs(self.period);   // 限幅：一步不超过 1/4 周期
            for (int it = 0; it < 6; it++)
            {
                double f0 = Residual(self, target, t); evals++;
                double fp = Residual(self, target, t + h); evals++;
                double fm = Residual(self, target, t - h); evals++;
                double d = (fp - fm) / (2.0 * h);
                if (Math.Abs(d) < 1e-14) return false;
                double step = -f0 / d;
                if (step > maxStep) step = maxStep;
                if (step < -maxStep) step = -maxStep;
                t += step;
                if (t < 0.0) t = 0.0;
                if (Math.Abs(step) < 1e-4) break;
            }
            double th = PhaseAngle(self, target, t);
            if (Math.Cos(th) >= 0.0) return false;       // 不是"对面"那个根
            if (Math.Abs(Math.Sin(th)) > 1e-3) return false;
            root = t;
            return true;
        }

        /// <summary>
        /// **偏心语料（e≈0.02 / 0.05）**：解析种子 + 牛顿 vs 现有搜索。
        /// 这一档超出闭式门的 `e<0.005`，正是"1 维求根"要接管的区间。
        /// </summary>
        [Fact]
        public void NewtonFromAnalyticSeed_Vs_Search_OnEccentricCorpus()
        {
            double[] radii = { 6.0e6, 7.5e6 };
            double[] ratios = { 1.05, 1.10, 1.20 };
            double[] phases = { 0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330 };
            double[] ks = { -0.05, -0.02, 0.02, 0.05 };

            int cases = 0, ok = 0, dv1pct = 0, win60 = 0, newtonFail = 0;
            long evalSum = 0;
            double sumDvRel = 0.0, maxDvRel = 0.0, sumWin = 0.0, maxWin = 0.0;

            foreach (double r in radii)
                foreach (double ratio in ratios)
                    foreach (double ph in phases)
                        foreach (double k in ks)
                        {
                            var self = Circular(r, 0.0, k);
                            var tgt = Circular(r * ratio, ph, -k);
                            if (self.eccentricity > 0.06 || tgt.eccentricity > 0.06) continue;
                            cases++;

                            double dt1, dvA;
                            try
                            {
                                (V3 a1, double t1, V3 a2, double _) =
                                    OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, tgt, 0.0, coplanar: false);
                                if (double.IsNaN(t1) || double.IsInfinity(t1)) continue;
                                dt1 = t1; dvA = a1.magnitude + a2.magnitude;
                            }
                            catch (Exception) { continue; }

                            double seed = RendezvousLadder.EstimateHohmannWaitSeconds(self, tgt, 0.0);
                            if (!NewtonRefine(self, tgt, seed, out double root, out int evals))
                            { newtonFail++; continue; }
                            evalSum += evals;
                            PhaseResidual(self, tgt, root, out double tau, out _, out _);
                            double dvB = ClosedFormDv(self, tgt, root, tau);
                            if (double.IsNaN(dvB)) continue;
                            ok++;

                            double rel = dvA > 1e-9 ? Math.Abs(dvB - dvA) / dvA : 0.0;
                            double dt = Math.Abs(root - dt1);
                            sumDvRel += rel; if (rel > maxDvRel) maxDvRel = rel;
                            sumWin += dt; if (dt > maxWin) maxWin = dt;
                            if (rel <= 0.01) dv1pct++;
                            if (dt <= 60.0) win60++;
                        }

            _out.WriteLine($"[解析种子+牛顿] 用例={cases} 成功={ok} 牛顿拒收={newtonFail}｜平均评估={evalSum / (double)Math.Max(ok, 1):F1} 次（每次≈3 传播）");
            if (ok > 0)
            {
                _out.WriteLine($"[解析种子+牛顿] Δv 相对差：中位={sumDvRel / ok:P2} 最大={maxDvRel:P2}｜≤1% 的 {dv1pct * 100.0 / ok:F0}%");
                _out.WriteLine($"[解析种子+牛顿] 窗口时刻差：中位={sumWin / ok:F0}s 最大={maxWin:F0}s｜≤60s 的 {win60 * 100.0 / ok:F0}%");
            }
            Assert.True(cases > 100, $"覆盖不足：{cases}");
        }
        /// <summary>
        /// 扫**整个会合周期**找所有"对面"窗口，每个根用闭式两发算 Δv，取 Δv 最小的那个。
        /// （偏心下 Δv 随烧点半径变化 ⇒ 只取第一个根会挑到差的窗口，实测 Δv 差 17%。）
        /// </summary>
        private static bool FindBestPhaseWindow(OrbitView self, OrbitView target, double synodicSeconds,
            out double burnT, out double tauOut, out double dv, out int evals)
        {
            burnT = double.NaN; tauOut = double.NaN; dv = double.MaxValue; evals = 0;
            const int N = 64;
            double bestDv = double.MaxValue, bestT = double.NaN, bestTau = double.NaN;
            double prevT = 0.0, prevF = Residual(self, target, prevT); evals++;
            for (int i = 1; i <= N; i++)
            {
                double t = synodicSeconds * i / N;
                double f = Residual(self, target, t); evals++;
                double th = PhaseAngle(self, target, t);
                if (f * prevF < 0.0 && Math.Cos(th) < 0.0)
                {
                    double lo = prevT, hi = t, flo = prevF;
                    for (int k = 0; k < 16; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        double fm = Residual(self, target, mid); evals++;
                        if (Math.Sign(fm) == Math.Sign(flo)) { lo = mid; flo = fm; }
                        else hi = mid;
                    }
                    double root = 0.5 * (lo + hi);
                    PhaseResidual(self, target, root, out double tau, out _, out _); evals++;
                    double d = ClosedFormDv(self, target, root, tau);
                    if (!double.IsNaN(d) && d < bestDv) { bestDv = d; bestT = root; bestTau = tau; }
                }
                prevT = t; prevF = f;
            }
            if (double.IsNaN(bestT)) return false;
            burnT = bestT; tauOut = bestTau; dv = bestDv;
            return true;
        }

        [Fact]
        public void BestRoot_Vs_Search_OnEccentricCorpus()
        {
            double[] radii = { 6.0e6, 7.5e6 };
            double[] ratios = { 1.05, 1.10, 1.20 };
            double[] phases = { 0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330 };
            double[] ks = { -0.05, -0.02, 0.02, 0.05 };

            int cases = 0, ok = 0, dv1pct = 0, win60 = 0;
            long evalSum = 0;
            double sumDvRel = 0.0, maxDvRel = 0.0, sumWin = 0.0, maxWin = 0.0;

            foreach (double r in radii)
                foreach (double ratio in ratios)
                    foreach (double ph in phases)
                        foreach (double k in ks)
                        {
                            var self = Circular(r, 0.0, k);
                            var tgt = Circular(r * ratio, ph, -k);
                            if (self.eccentricity > 0.06 || tgt.eccentricity > 0.06) continue;
                            cases++;

                            double dt1, dvA;
                            try
                            {
                                (V3 a1, double t1, V3 a2, double _) =
                                    OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, tgt, 0.0, coplanar: false);
                                if (double.IsNaN(t1) || double.IsInfinity(t1)) continue;
                                dt1 = t1; dvA = a1.magnitude + a2.magnitude;
                            }
                            catch (Exception) { continue; }

                            double n1 = Math.Sqrt(Mu / Math.Pow(self.semiMajorAxis, 3.0));
                            double n2 = Math.Sqrt(Mu / Math.Pow(tgt.semiMajorAxis, 3.0));
                            double syn = 2.0 * Math.PI / Math.Abs(n2 - n1);
                            if (!(syn > 0.0) || double.IsInfinity(syn)) continue;

                            if (!FindBestPhaseWindow(self, tgt, syn, out double burnT, out double tau, out double dvB, out int ev))
                                continue;
                            evalSum += ev;
                            ok++;

                            double rel = dvA > 1e-9 ? Math.Abs(dvB - dvA) / dvA : 0.0;
                            double dt = Math.Abs(burnT - dt1);
                            sumDvRel += rel; if (rel > maxDvRel) maxDvRel = rel;
                            sumWin += dt; if (dt > maxWin) maxWin = dt;
                            if (rel <= 0.01) dv1pct++;
                            if (dt <= 60.0) win60++;
                        }

            _out.WriteLine($"[全根取最小Δv] 用例={cases} 成功={ok}｜平均评估={evalSum / (double)Math.Max(ok, 1):F0} 次");
            if (ok > 0)
            {
                _out.WriteLine($"[全根取最小Δv] Δv 相对差：中位={sumDvRel / ok:P2} 最大={maxDvRel:P2}｜≤1% 的 {dv1pct * 100.0 / ok:F0}%");
                _out.WriteLine($"[全根取最小Δv] 窗口时刻差：中位={sumWin / ok:F0}s 最大={maxWin:F0}s｜≤60s 的 {win60 * 100.0 / ok:F0}%");
            }
            Assert.True(cases > 100, $"覆盖不足：{cases}");
        }
    }
}
