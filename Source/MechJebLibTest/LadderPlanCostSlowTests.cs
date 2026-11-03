using System;
using System.Diagnostics;
using MechJebLib.Primitives;
using PioneerFramework.Pure.Tests;      // 台架（CircularTarget）
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **阶梯规划的慢几何成本分解**（2026-10-21）。目的：回答"9–21 ms 到底花在哪一段"。
    ///
    /// 已知（交接 §4.6）：同轨 90° = 0.5 ms；**大半径差（走霍曼搜索）= 9–21 ms**（游戏内
    /// `[阶梯·耗时] G2 MakePlan 9.2 ms（PhasingOrbit）` / `6.9 ms（Hohmann）`）。
    /// 现有 `LadderPlanCostTests` 只量同轨几何，量不到这条。
    ///
    /// 本台架把一次 `MakePlan` 拆成三块分别计时（Release 口径；时间只进日志，不进 PASS/FAIL）：
    /// ① `NextClosestApproachTime` = 8 轮 × 20 分格 = 160 次双轨传播；
    /// ② `DeltaVAndTimeForHohmannTransfer` = `TwoImpulseTransfer` 的模式搜索（≤50 次 Lambert 求解）；
    /// ③ 整条 `MakePlan`。
    /// </summary>
    public class LadderPlanCostSlowTests
    {
        private readonly ITestOutputHelper _out;
        public LadderPlanCostSlowTests(ITestOutputHelper output) => _out = output;

        private const double Mu = 2.0e14;
        private const double BodyRadius = 5.0e6;
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);

        private static OrbitView Circular(double radius, double phaseDeg)
        {
            // 与 `LadderPlanCostTests` 同一套造靶：`CircularTarget` 给相位，再转 `OrbitView`。
            var t = CircularTarget.Create(radius, Mu, phaseDeg * Math.PI / 180.0, OrbitNormal);
            return OrbitView.FromStateVectors(t.PosAt(0.0), t.VelAt(0.0), Mu, 0.0,
                t.PlaneNormalAt(0.0), BodyRadius);
        }

        private static double MedianMs(int reps, Action body)
        {
            var samples = new double[reps];
            for (int i = 0; i < reps; i++)
            {
                var sw = Stopwatch.StartNew();
                body();
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(samples);
            return samples[reps / 2];
        }

        [Fact]
        public void PlanCost_CoOrbital_Vs_LargeRadiusGap_Breakdown()
        {
            var p = new RendezvousLadder.Params { DesiredDistance = 283.0, MinRadius = 5.1e6 };

            var self = Circular(7_000_000.0, 0.0);
            var coOrb = Circular(7_000_000.0, 90.0);
            var farOrb = Circular(8_100_000.0, 90.0);

            // 预热（JIT + 首调用）
            for (int i = 0; i < 3; i++)
            {
                RendezvousLadder.MakePlan(self, coOrb, 0.0, p, out _, out _);
                RendezvousLadder.MakePlan(self, farOrb, 0.0, p, out _, out _);
                self.NextClosestApproachTime(farOrb, 0.0);
                OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, farOrb, 0.0, coplanar: false);
            }

            double coMs = MedianMs(15, () =>
                RendezvousLadder.MakePlan(self, coOrb, 0.0, p, out _, out _));

            RendezvousLadder.Rung farRung = RendezvousLadder.Rung.Done;
            double farMs = MedianMs(15, () =>
                farRung = RendezvousLadder.MakePlan(self, farOrb, 0.0, p, out _, out _));

            double ncaMs = MedianMs(15, () => self.NextClosestApproachTime(farOrb, 0.0));
            double hohMs = MedianMs(15, () =>
                OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, farOrb, 0.0, coplanar: false));

            _out.WriteLine($"[阶梯成本] 同轨 90°      MakePlan = {coMs,8:F3} ms");
            _out.WriteLine($"[阶梯成本] 7000→8100 km  MakePlan = {farMs,8:F3} ms（选中 {farRung}）");
            _out.WriteLine($"[阶梯成本]   ├ NextClosestApproachTime(160 次传播) = {ncaMs,8:F3} ms");
            _out.WriteLine($"[阶梯成本]   └ 霍曼搜索(TwoImpulseTransfer ≤50 次 Lambert) = {hohMs,8:F3} ms");
            _out.WriteLine($"[阶梯成本] 参考：慢几何下 NCA+霍曼 = {ncaMs + hohMs:F3} ms（占 MakePlan 的 " +
                           $"{(farMs > 0 ? (ncaMs + hohMs) / farMs * 100.0 : 0):F0}%）");

            Assert.True(farMs > 0.0);
        }

        /// <summary>
        /// 扫几何，找出**哪一族**会走到 9–21 ms（游戏内实测值）。判据只打读数，不断言。
        /// 两个中心天体：地球档（mu=2e14 / R=5000 km）与月球档（mu=4.9048e12 / R=1737 km，
        /// 对应调试场景「绕月交会·异轨」2900→3300 km）。
        /// </summary>
        [Fact]
        public void PlanCost_SweepGeometries_ShowsWhichFamilyIsSlow()
        {
            var p = new RendezvousLadder.Params { DesiredDistance = 283.0, MinRadius = 5.1e6 };
            double[] earthSelf = { 7_000_000.0, 7_000_000.0, 7_500_000.0 };
            double[] earthTgt = { 8_100_000.0, 12_000_000.0, 7_700_000.0 };

            for (int i = 0; i < earthSelf.Length; i++)
            {
                var self = Circular(earthSelf[i], 0.0);
                var tgt = Circular(earthTgt[i], 90.0);
                var par = new RendezvousLadder.Params
                {
                    DesiredDistance = 283.0,
                    MinRadius = 5.1e6,
                    MaxPhasingOrbits = 5.0,
                };
                RendezvousLadder.MakePlan(self, tgt, 0.0, par, out _, out _);   // 预热
                RendezvousLadder.Rung rung = RendezvousLadder.Rung.Done;
                double ms = MedianMs(5, () =>
                    rung = RendezvousLadder.MakePlan(self, tgt, 0.0, par, out _, out _));
                double hoh = MedianMs(5, () =>
                    OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, tgt, 0.0, coplanar: false));
                _out.WriteLine($"[阶梯成本·扫] 地球 {earthSelf[i] / 1000.0:F0}→{earthTgt[i] / 1000.0:F0} km：MakePlan={ms,8:F3} ms（{rung}）霍曼={hoh,8:F3} ms");
            }

            // 月球档：自 2900 km、目标 3300 km（游戏内「绕月交会·异轨」的几何）
            var moonSelf = MoonCircular(2_900_000.0, 0.0);
            var moonTgt = MoonCircular(3_300_000.0, 90.0);
            var mp = new RendezvousLadder.Params
            {
                DesiredDistance = 283.0,
                MinRadius = 1.8e6,
                MaxPhasingOrbits = 5.0,
            };
            RendezvousLadder.MakePlan(moonSelf, moonTgt, 0.0, mp, out _, out _);
            RendezvousLadder.Rung mr = RendezvousLadder.Rung.Done;
            double moonMs = MedianMs(5, () =>
                mr = RendezvousLadder.MakePlan(moonSelf, moonTgt, 0.0, mp, out _, out _));
            double moonHoh = MedianMs(5, () =>
                OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(moonSelf, moonTgt, 0.0, coplanar: false));
            _out.WriteLine($"[阶梯成本·扫] 月球 2900→3300 km：MakePlan={moonMs,8:F3} ms（{mr}）霍曼={moonHoh,8:F3} ms");
        }


        /// <summary>诊断：PhasingOrbit 那档几何里，霍曼搜索到底是"找到太远的窗口"还是"根本搜不到"。</summary>
        [Fact]
        public void HohmannSearch_OnPhasingGeometry_ReturnsWhat()
        {
            var self = Circular(7_500_000.0, 0.0);
            var tgt = Circular(7_700_000.0, 90.0);
            var par = new RendezvousLadder.Params { DesiredDistance = 283.0, MinRadius = 5.1e6, MaxPhasingOrbits = 5.0 };
            var rung = RendezvousLadder.MakePlan(self, tgt, 0.0, par, out var nodes, out var status);
            double dt1 = double.NaN;
            try
            {
                (_, dt1, _, _) = OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, tgt, 0.0, coplanar: false);
            }
            catch (Exception ex)
            {
                _out.WriteLine($"[霍曼诊断] 抛异常：{ex.GetType().Name}: {ex.Message}");
            }
            _out.WriteLine($"[霍曼诊断] rung={rung} dt1={dt1:F0}s 周期={self.period:F0}s 圈数={(dt1 / self.period):F2} " +
                           $"(上限 5)｜节点={nodes.Count}｜status={status}");
        }
        /// <summary>月球档的等价造靶（mu=4.9048e12、R=1737 km、同一套法向约定）。</summary>
        private static OrbitView MoonCircular(double radius, double phaseDeg)
        {
            const double muMoon = 4.9048e12;
            const double rMoon = 1.737e6;
            var t = CircularTarget.Create(radius, muMoon, phaseDeg * Math.PI / 180.0, OrbitNormal);
            return OrbitView.FromStateVectors(t.PosAt(0.0), t.VelAt(0.0), muMoon, 0.0,
                t.PlaneNormalAt(0.0), rMoon);
        }
    }
}
