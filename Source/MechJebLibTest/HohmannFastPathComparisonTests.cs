using System;
using PioneerFramework;
using PioneerFramework.Pure.Tests;   // CircularTarget
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **"解析窗口 + 一次固定时刻 Lambert"能不能替掉霍曼搜索**（2026-10-21，先判后修）。
    ///
    /// 现状：`DeltaVAndTimeForHohmannTransfer` 走 `TwoImpulseTransfer` 的多起点搜索（≤50 轮，
    /// 每轮 12 种子 × ~60 sweeps）—— 游戏内实测 **1.7–2.7 ms，最坏 7.1 ms**。
    /// 候选快路（只对近圆共面）：
    ///   ① 解析窗口时刻 `t_b`（`RendezvousLadder.EstimateHohmannWaitSeconds`）；
    ///   ② 转移半时长 `tof = π√((a1+a2)³/(8μ))`；
    ///   ③ 一次固定时刻 Lambert `DeltaVToInterceptAtTime(self, now+t_b, target, tof)` ⇒ 两发 Δv。
    ///
    /// 本文件**只产读数、不改生产**：把两条路的 Δv 总量、点火时刻、到达时刻并排量出来，
    /// 再决定值不值得换（换 = 改选中级/Δv ⇒ 必须重新立判据）。
    /// </summary>
    public class HohmannFastPathComparisonTests
    {
        private readonly ITestOutputHelper _out;
        public HohmannFastPathComparisonTests(ITestOutputHelper o) => _out = o;

        private const double Mu = 2.0e14;
        private const double BodyRadius = 5.0e6;
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);

        private static OrbitView Circular(double radius, double phaseDeg)
        {
            var t = CircularTarget.Create(radius, Mu, phaseDeg * Math.PI / 180.0, OrbitNormal);
            return OrbitView.FromStateVectors(t.PosAt(0.0), t.VelAt(0.0), Mu, 0.0,
                t.PlaneNormalAt(0.0), BodyRadius);
        }

        [Fact]
        public void Search_And_AnalyticPlusFixedTimeLambert_Compared()
        {
            double[] radii = { 5.5e6, 6.0e6, 7.0e6, 7.5e6 };
            double[] ratios = { 1.02, 1.05, 1.10, 1.20, 1.35 };
            double[] phases = { 0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330 };

            int cases = 0, okA = 0, okB = 0, bCheaper = 0, bPricier = 0;
            double maxDvRel = 0.0, sumDvRel = 0.0, maxBurnDt = 0.0, sumBurnDt = 0.0;
            int within1pct = 0, within5s = 0;

            foreach (double r in radii)
                foreach (double ratio in ratios)
                    foreach (double ph in phases)
                    {
                        var self = Circular(r, 0.0);
                        var tgt = Circular(r * ratio, ph);
                        cases++;

                        // ── A：现行搜索 ──
                        double dvA;
                        double utA1, utA2;
                        try
                        {
                            (V3 a1, double t1, V3 a2, double t2) =
                                OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, tgt, 0.0, coplanar: false);
                            if (double.IsNaN(t1) || double.IsInfinity(t1)) continue;
                            dvA = a1.magnitude + a2.magnitude;
                            utA1 = t1; utA2 = t2;
                            okA++;
                        }
                        catch (Exception) { continue; }

                        // ── B：解析窗口 + **闭式霍曼 Δv**（不用 Lambert：霍曼是 180° 转移，
                        //        Lambert 在该处近乎奇异 —— 实测有些相位会解出 17 km/s 的另一支）──
                        double tb = RendezvousLadder.EstimateHohmannWaitSeconds(self, tgt, 0.0);
                        if (double.IsNaN(tb)) continue;
                        double a1r = self.semiMajorAxis, a2r = tgt.semiMajorAxis;
                        double tof = Math.PI * Math.Sqrt(Math.Pow(a1r + a2r, 3.0) / (8.0 * Mu));
                        double arrUt = tb + tof;
                        (V3 rb, V3 vb) = self.RightHandedStateVectorsAtUT(tb);
                        (V3 ra, V3 va) = tgt.RightHandedStateVectorsAtUT(arrUt);
                        double r1m = rb.magnitude, r2m = ra.magnitude;
                        if (!(r1m > 0.0) || !(r2m > 0.0)) continue;
                        double vNeed = Math.Sqrt(Mu * (2.0 / r1m - 2.0 / (r1m + r2m)));   // 转移轨道在 r1 的速度
                        double vArr = Math.Sqrt(Mu * (2.0 / r2m - 2.0 / (r1m + r2m)));    // 转移轨道在 r2 的速度
                        V3 b1 = vb.normalized * (vNeed - vb.magnitude);
                        V3 b2 = va.normalized * (va.magnitude - vArr);
                        double dvB = b1.magnitude + b2.magnitude;
                        double utB1 = tb, utB2 = tb + tof;
                        okB++;

                        double rel = dvA > 1e-9 ? Math.Abs(dvB - dvA) / dvA : 0.0;
                        double dtBurn = Math.Abs(utB1 - utA1);
                        sumDvRel += rel; sumBurnDt += dtBurn;
                        if (rel > maxDvRel) maxDvRel = rel;
                        if (dtBurn > maxBurnDt) maxBurnDt = dtBurn;
                        if (rel <= 0.01) within1pct++;
                        if (dtBurn <= 5.0) within5s++;
                        if (dvB < dvA) bCheaper++; else if (dvB > dvA) bPricier++;
                        if (okB <= 6)
                            _out.WriteLine($"[样本] r={r / 1000:F0} ratio={ratio:F2} φ={ph}°｜A: dv={dvA:F2} m/s burn=t+{utA1:F0}s arr=t+{utA2:F0}s" +
                                           $"｜B: dv={dvB:F2} burn=t+{tb:F0}s arr=t+{tb + tof:F0}s (tof={tof:F0}s)");
                    }

            _out.WriteLine($"[霍曼快路对照] 用例={cases} 搜索有解={okA} 快路有解={okB}");
            _out.WriteLine($"[霍曼快路对照] Δv 相对差：中位={sumDvRel / Math.Max(okB, 1):P2} 最大={maxDvRel:P2}｜≤1% 的占 {within1pct * 100.0 / Math.Max(okB, 1):F0}%");
            _out.WriteLine($"[霍曼快路对照] 点火时刻差：中位={sumBurnDt / Math.Max(okB, 1):F0}s 最大={maxBurnDt:F0}s｜≤5s 的占 {within5s * 100.0 / Math.Max(okB, 1):F0}%");
            _out.WriteLine($"[霍曼快路对照] 快路更省 Δv 的 {bCheaper} 例 / 更贵 {bPricier} 例");

            // ── 判据（换路线之前必须先满足）──
            Assert.True(okA > 100, $"覆盖不足：搜索有解 {okA}");
            Assert.True(okB > 100, $"覆盖不足：快路有解 {okB}");
            Assert.True(maxDvRel < 0.001, $"Δv 相对差最大 {maxDvRel:P2} 超过 0.1%");
            Assert.True(maxBurnDt < 15.0, $"点火时刻差最大 {maxBurnDt:F0}s 超过 15s");
            Assert.True(bPricier <= okB / 20, $"快路更贵的比例过高：{bPricier}/{okB}");
        }

        /// <summary>
        /// **闭式霍曼接进阶梯后**：近圆共面、窗口在上限内的几何应当走 `霍曼拦截（闭式）`，
        /// 且两发节点齐全、成本落到"解析 + 两次传播"的量级（原搜索 1.7–2.7 ms）。
        /// </summary>
        [Fact]
        public void MakePlan_UsesClosedFormHohmann_AndIsCheap()
        {
            var self = Circular(5.5e6, 0.0);
            var tgt = Circular(5.5e6 * 1.02, 30.0);
            var p = new RendezvousLadder.Params
            {
                DesiredDistance = 283.0,
                MatchDistance = 2000.0,
                MaxClosingSpeed = 100.0,
                MaxPhasingOrbits = 5.0,
                MinRadius = 5.1e6,
            };

            var rung = RendezvousLadder.MakePlan(self, tgt, 0.0, p, out var nodes, out var status);
            _out.WriteLine($"[闭式霍曼·端到端] rung={rung} nodes={nodes.Count}｜status={status}");
            Assert.Equal(RendezvousLadder.Rung.Hohmann, rung);
            Assert.Equal(2, nodes.Count);
            Assert.Contains("闭式", status);

            var sw = new System.Diagnostics.Stopwatch();
            var samples = new double[21];
            for (int i = 0; i < samples.Length; i++)
            {
                sw.Restart();
                RendezvousLadder.MakePlan(self, tgt, 0.0, p, out _, out _);
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(samples);
            _out.WriteLine($"[闭式霍曼·端到端] MakePlan 中位={samples[10]:F3} ms（原搜索 ~1.7–2.7 ms）");
        }
    }
}
