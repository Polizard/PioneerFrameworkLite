using System;
using System.Collections.Generic;
using System.Text;
using MechJebLib.Functions;
using MechJebLib.Maneuvers;
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **A.3 第一步：窗口政策的度量**（2026-10-01 用户裁定"先立政策 + 度量"）。
    ///
    /// ## 背景（§10.14 结论 2）
    /// `TwoImpulseTransfer.Maneuver` 的搜索用 **12 个起点 = 3 个 `tt` × 4 个 `dt`**；四个 `dt` 种子
    /// 间隔 **¼ 会合周期**（`dtguess + k/4·synodic`，k=0..3）⇒ 它们就是**四个候选窗口**。
    /// 归约是 `ParallelOps.MinIndex`（**并列取小下标**）⇒ 只有成本**严格相等**时才等价于"取最早窗口"；
    /// 而窗口维的 Δv 只差最后几位（浮点噪声）⇒ **赢家实际上由噪声定**（"退化 argmin 陷阱"）。
    ///
    /// ## 本台架量什么（**不改生产、不改行为**）
    /// 对每组几何：把输入状态做 **ULP 级扰动**（相对 1e-15 ~ 1e-12，固定种子 ⇒ 可复现），反复调用
    /// **生产入口** `OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer`（= 阶梯的 Hohmann 那一级；
    /// `RendezvousLadder.cs:328` 的原调用口径 `coplanar: false`），看：
    ///   · 返回的首烧时刻 `UT1`（= 窗口代理）**翻不翻**（按 UT1 聚类 ⇒ 一簇 = 一个窗口）；
    ///   · 各窗口的 **Δv 差多少**（= "显式窗口政策落地后可能改动的量级"）。
    ///
    /// ## 判据（事先登记）
    ///   ① 基线调用必须返回有限解（这几组几何都非退化）；
    ///   ② 全部扰动调用必须返回有限解（不抛、不 NaN）；
    ///   ③ **读数打出来**（簇数 / 各簇 Δv / 翻转率 / 最早窗口的代价）—— 这是本条要的**度量**，
    ///      不是 PASS/FAIL；"政策落不落生产"是另一笔裁定。
    ///
    /// ⚠ **不要**在这条度量没读数之前动种子 / warm start（§10.14 结论 2 的纪律）。
    /// </summary>
    public class TwoImpulseWindowPolicyTests
    {
        private readonly ITestOutputHelper _out;
        public TwoImpulseWindowPolicyTests(ITestOutputHelper o) => _out = o;

        private static readonly double Mu = OrbitMath.GM;
        private static readonly V3 Pole = new V3(0, 0, 1);
        private const int RngSeed = 20261001;              // 固定种子 ⇒ 读数可复现
        private static readonly double[] Epsilons = { 1e-15, 1e-14, 1e-13, 1e-12 };
        private const int RunsPerEpsilon = 12;

        /// <summary>圆轨状态（极轴 +Z、顺行；相位 = 绕 Z 转，倾角 = 整条轨道绕 X 轴转）。</summary>
        private static (V3 r, V3 v) Circular(double radius, double phaseRad, double incRad = 0.0)
        {
            double vc = Math.Sqrt(Mu / radius);
            double c = Math.Cos(phaseRad), s = Math.Sin(phaseRad);
            double ci = Math.Cos(incRad), si = Math.Sin(incRad);
            return (new V3(radius * c, radius * s * ci, radius * s * si),
                    new V3(-vc * s, vc * c * ci, vc * c * si));
        }

        private static OrbitView View((V3 r, V3 v) st, double radius) =>
            OrbitView.FromStateVectors(st.r, st.v, Mu, 0.0, Pole, radius * 0.2);

        private static V3 Perturb(V3 x, double eps, Random rng) =>
            new V3(x.x * (1.0 + eps * (2.0 * rng.NextDouble() - 1.0)),
                   x.y * (1.0 + eps * (2.0 * rng.NextDouble() - 1.0)),
                   x.z * (1.0 + eps * (2.0 * rng.NextDouble() - 1.0)));

        private readonly struct Sample
        {
            public readonly double Ut1, Dv; public readonly bool Finite;
            public Sample(double ut1, double dv, bool finite) { Ut1 = ut1; Dv = dv; Finite = finite; }
        }

        /// <summary>调一次生产入口（阶梯 Hohmann 那一级的原口径）。</summary>
        private static Sample Call((V3 r, V3 v) c, double cR, (V3 r, V3 v) t, double tR)
        {
            var (dv1, ut1, dv2, ut2) =
                OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(View(c, cR), View(t, tR), 0.0, coplanar: false);
            bool finite = !double.IsNaN(ut1) && !double.IsInfinity(ut1) && !double.IsNaN(ut2) && !double.IsInfinity(ut2);
            return new Sample(ut1, dv1.magnitude + dv2.magnitude, finite);
        }

        private sealed class Cluster
        {
            public double Ut1Sum; public int N;
            public double DvMin = double.PositiveInfinity, DvMax = double.NegativeInfinity, DvSum;
            public double MeanUt1 => Ut1Sum / N;
            public double MeanDv => DvSum / N;
        }

        /// <summary>按 UT1 聚类（相对间隔 > 1e-3 ⇒ 新簇）。</summary>
        private static List<Cluster> Clusterize(List<Sample> samples)
        {
            var list = new List<Sample>(samples);
            list.Sort((a, b) => a.Ut1.CompareTo(b.Ut1));
            var clusters = new List<Cluster>();
            foreach (var s in list)
            {
                if (clusters.Count == 0 || Math.Abs(s.Ut1 - clusters[clusters.Count - 1].MeanUt1) > 1e-3 * Math.Max(1.0, Math.Abs(s.Ut1)))
                    clusters.Add(new Cluster());
                var cl = clusters[clusters.Count - 1];
                cl.Ut1Sum += s.Ut1; cl.N += 1; cl.DvSum += s.Dv;
                if (s.Dv < cl.DvMin) cl.DvMin = s.Dv;
                if (s.Dv > cl.DvMax) cl.DvMax = s.Dv;
            }
            return clusters;
        }

        private void Measure(string name, (V3 r, V3 v) chaser, double cR, (V3 r, V3 v) target, double tR)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"── {name} ──");
            sb.AppendLine($"   追击 r={cR / 1000.0:F0} km｜目标 r={tR / 1000.0:F0} km");

            Sample baseline = Call(chaser, cR, target, tR);
            Assert.True(baseline.Finite, $"{name}：基线调用没给出有限解（几何退化？）");
            sb.AppendLine($"   基线：UT1={baseline.Ut1:F1}s｜Δv={baseline.Dv:F3} m/s");

            var samples = new List<Sample> { baseline };
            var rng = new Random(RngSeed);
            foreach (double eps in Epsilons)
            {
                for (int i = 0; i < RunsPerEpsilon; i++)
                {
                    var c = (Perturb(chaser.r, eps, rng), Perturb(chaser.v, eps, rng));
                    var t = (Perturb(target.r, eps, rng), Perturb(target.v, eps, rng));
                    var s = Call(c, cR, t, tR);
                    Assert.True(s.Finite, $"{name}：扰动调用（ε={eps:E0}）没给出有限解");
                    samples.Add(s);
                }
            }

            var clusters = Clusterize(samples);
            int flips = 0;
            double ut1Lo = double.PositiveInfinity, ut1Hi = double.NegativeInfinity;
            double dvSampleLo = double.PositiveInfinity, dvSampleHi = double.NegativeInfinity, dvSampleSum = 0.0;
            foreach (var s in samples)
            {
                if (Math.Abs(s.Ut1 - baseline.Ut1) > 1e-3 * Math.Max(1.0, Math.Abs(baseline.Ut1))) flips++;
                if (s.Ut1 < ut1Lo) ut1Lo = s.Ut1;
                if (s.Ut1 > ut1Hi) ut1Hi = s.Ut1;
                if (s.Dv < dvSampleLo) dvSampleLo = s.Dv;
                if (s.Dv > dvSampleHi) dvSampleHi = s.Dv;
                dvSampleSum += s.Dv;
            }

            double dvLo = double.PositiveInfinity, dvHi = double.NegativeInfinity;
            foreach (var cl in clusters)
            {
                if (cl.MeanDv < dvLo) dvLo = cl.MeanDv;
                if (cl.MeanDv > dvHi) dvHi = cl.MeanDv;
            }

            sb.AppendLine($"   窗口簇：{clusters.Count} 个（共 {samples.Count} 次调用，含基线）");
            foreach (var cl in clusters)
                sb.AppendLine($"     UT1={cl.MeanUt1,10:F1}s  次数={cl.N,3}  Δv 均值={cl.MeanDv:F6}  范围=[{cl.DvMin:F6}, {cl.DvMax:F6}] m/s");
            sb.AppendLine($"   赢家翻转：{flips}/{samples.Count}（vs 基线）｜跨窗口 Δv 差={dvHi - dvLo:F6} m/s" +
                          $"（{dvLo:F6} … {dvHi:F6}）｜最早窗口 UT1={(clusters.Count > 0 ? clusters[0].MeanUt1 : double.NaN):F1}s");
            sb.AppendLine($"   逐次散布（含基线共 {samples.Count} 次）：UT1 极差={ut1Hi - ut1Lo:E3} s｜" +
                          $"Δv 逐次极差={dvSampleHi - dvSampleLo:E3} m/s（相对 {(dvSampleHi - dvSampleLo) / Math.Max(dvSampleSum / samples.Count, 1e-30):E3}）");
            _out.WriteLine(sb.ToString().TrimEnd());
        }

        [Fact]
        public void WindowChoice_IsNoiseDominated_MeasureOnly()
        {
            // ① 现场那局（游戏内验收的原几何：追击 5500 km、目标 6300 km、相位 45°、倾角 3°）
            Measure("① 现场·地球（5500→6300 km，45°，倾角 3°）",
                Circular(5_500_000.0, 0.0), 5_500_000.0,
                Circular(6_300_000.0, 45.0 * Math.PI / 180.0, 3.0 * Math.PI / 180.0), 6_300_000.0);
            SweepSeeds("① 现场·地球（5500→6300 km，45°，倾角 3°）",
                Circular(5_500_000.0, 0.0), 5_500_000.0,
                Circular(6_300_000.0, 45.0 * Math.PI / 180.0, 3.0 * Math.PI / 180.0), 6_300_000.0);

            // ② 同面、Δa 中等（对照：倾角是不是噪声源）
            Measure("② 同面 5500→6300 km，45°，倾角 0°",
                Circular(5_500_000.0, 0.0), 5_500_000.0,
                Circular(6_300_000.0, 45.0 * Math.PI / 180.0), 6_300_000.0);
            SweepSeeds("② 同面 5500→6300 km，45°，倾角 0°",
                Circular(5_500_000.0, 0.0), 5_500_000.0,
                Circular(6_300_000.0, 45.0 * Math.PI / 180.0), 6_300_000.0);

            // ③ Δa 更大（5.5e6 → 1.2e7 同面）
            Measure("③ 同面 5500→12000 km，0°",
                Circular(5_500_000.0, 0.0), 5_500_000.0,
                Circular(12_000_000.0, 0.0), 12_000_000.0);
            SweepSeeds("③ 同面 5500→12000 km，0°",
                Circular(5_500_000.0, 0.0), 5_500_000.0,
                Circular(12_000_000.0, 0.0), 12_000_000.0);

            // ④ LEO→GEO（半径差大；阶梯形状级那档）
            Measure("④ 同面 5500→26322 km（LEO→GEO），30°",
                Circular(5_500_000.0, 0.0), 5_500_000.0,
                Circular(26_322_000.0, 30.0 * Math.PI / 180.0), 26_322_000.0);
            SweepSeeds("④ 同面 5500→26322 km（LEO→GEO），30°",
                Circular(5_500_000.0, 0.0), 5_500_000.0,
                Circular(26_322_000.0, 30.0 * Math.PI / 180.0), 26_322_000.0);
        }

        /// <summary>
        /// **(b) 把首猜按 ¼ 会合周期挪格**（= 把候选窗口整体挪一格；走当年 warm start 那个入口，
        /// ⚠ **此处只做度量，不是要接 warm start** —— 它已判不接，§10.13）：若赢家窗口**跟着挪**
        /// 而 Δv 几乎不变 ⇒ "窗口维代价相同、选谁都可以"（退化 argmin）⇒ 显式政策有用武之地；
        /// 若窗口**不挪** ⇒ 物理最优窗口唯一，窗口政策这个问题不成立。
        /// </summary>
        private void SweepSeeds(string name, (V3 r, V3 v) chaser, double cR, (V3 r, V3 v) target, double tR)
        {
            double syn = Astro.SynodicPeriod(Mu, chaser.r, chaser.v, target.r, target.v);
            double t2 = Astro.PeriodFromStateVectors(Mu, target.r, target.v);
            var sb = new StringBuilder();
            sb.AppendLine($"── {name}（会合周期 {syn:F0} s = {syn / t2:F2} 个目标周期）──");

            double baseDv = double.NaN, baseUt1 = double.NaN, dvLo = double.PositiveInfinity, dvHi = double.NegativeInfinity;
            double ut1Lo = double.PositiveInfinity, ut1Hi = double.NegativeInfinity;
            for (int k = 0; k < 4; k++)
            {
                double guess = k * 0.25 * syn;
                var (dv1, dt1, dv2, dt2) = TwoImpulseTransfer.NextManeuverWarmStart(Mu, chaser.r, chaser.v, target.r, target.v,
                    guess, coplanar: false);
                double dv = dv1.magnitude + dv2.magnitude;
                Assert.True(!double.IsNaN(dt1) && !double.IsInfinity(dt1), $"{name}：首猜 k={k} 没给出有限解");
                if (k == 0) { baseDv = dv; baseUt1 = dt1; }
                if (dv < dvLo) dvLo = dv;
                if (dv > dvHi) dvHi = dv;
                if (dt1 < ut1Lo) ut1Lo = dt1;
                if (dt1 > ut1Hi) ut1Hi = dt1;
                sb.AppendLine($"   首猜 {(k / 4.0):F2}·syn = {guess,9:F0}s ⇒ 窗口 dt1={dt1,10:F1}s（相对 k=0 {dt1 - baseUt1,+9:F1}s）" +
                              $"｜Δv={dv:F6} m/s（相对 {100.0 * (dv / baseDv - 1.0):+0.0000;-0.0000}%）");
            }
            sb.AppendLine($"   窗口跨度={ut1Hi - ut1Lo:F1}s（会合周期的 {(ut1Hi - ut1Lo) / syn:P1}）｜Δv 极差={dvHi - dvLo:F6} m/s" +
                          $"（相对 {(dvHi - dvLo) / Math.Max(dvLo, 1e-30):E3}）");
            _out.WriteLine(sb.ToString().TrimEnd());
        }
    }
}
