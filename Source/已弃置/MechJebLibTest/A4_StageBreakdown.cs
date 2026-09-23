using System;
using System.Diagnostics;
using System.IO;
using MechJebLib.Primitives;
using PioneerFramework.Pure.Tests;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **阶梯耗时的分期归因**（2026-09-20，游戏内量到 6.4~8.3 ms/次之后）：
    /// 把 `MakePlan` 里几段**可能贵**的逐段计时（同一批几何 × 若干次取均值），
    /// 这样"降哪里"是**数据决定**的。进度写文件（进程被杀也留读数）。
    /// </summary>
    public class A4_StageBreakdown
    {
        private static readonly string Log = Path.Combine(Path.GetTempPath(), "pfl_a4_stage.txt");
        private static void W(string s) { File.AppendAllText(Log, s + "\n"); }

        private const double Mu = 2.0e14, R0 = 7_000_000.0, BodyR = 5.0e6;

        [Fact]
        public void Probe_StageCosts()
        {
            File.WriteAllText(Log, "== 分期归因 " + DateTime.Now.ToString("HH:mm:ss") + " ==\n");
            double vc = Math.Sqrt(Mu / R0);
            var ship = RendezvousShip.HighThrustShip(new DVector3(R0, 0, 0), new DVector3(0, 0, -vc), new DVector3(0, 0, -1));
            var self = OrbitView.FromStateVectors(OrbitView.ToV3(ship.Pos), OrbitView.ToV3(ship.Vel), Mu, 0.0,
                OrbitView.ToV3(DVector3.up), BodyR);

            foreach (double daKm in new[] { 0.0, 1100.0 })
            {
                var tgtOnly = CircularTarget.Create(R0 + daKm * 1000.0, Mu, (90.0 + 180.0) * Math.PI / 180.0, DVector3.up);
                var tgt = OrbitView.FromStateVectors(OrbitView.ToV3(tgtOnly.PosAt(0.0)), OrbitView.ToV3(tgtOnly.VelAt(0.0)),
                    Mu, 0.0, OrbitView.ToV3(DVector3.up), BodyR);

                // 逐段（各 50 次取均值）
                double tPlan = Time(50, () => RendezvousLadder.MakePlan(self, tgt, 0.0,
                    new RendezvousLadder.Params { DesiredDistance = 300.0, MinRadius = 5.1e6 }, out _, out _));
                double tScan = Time(50, () => { double u = self.NextClosestApproachTime(tgt, 0.0); self.Separation(tgt, u); });
                double tPhase = Time(50, () => RendezvousLadder.PhaseDifferenceDeg(self, tgt, 0.0));
                double tCost = Time(50, () => RendezvousGuidanceCore.CostEstimate(self.semiMajorAxis, tgt.semiMajorAxis, 1.571, Mu, true));
                double tHohmann = Time(50, () => OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, tgt, 0.0));

                // 「两段式」：先闭式解相位窗口 τ，再在窗口处解转移（游戏侧 TryPlanHohmannTransfer 走的正是这条）
                double tWin = Time(50, () => PhaseWindow.TrySolveForHohmann(self, tgt, 0.0, out _, out _, out _));
                double tTwoStage = Time(50, () =>
                {
                    if (PhaseWindow.TrySolveForHohmann(self, tgt, 0.0, out double tau2, out _, out _)
                        && tau2 >= 0.0)
                        OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, tgt, tau2,
                            lagTime: 0.0, coplanar: true);
                });
                // 调相支的子步骤（游戏里 10.1 ms 那条 = PhasingOrbit ⇒ 必须逐段量）
                double aTgtR = tgt.semiMajorAxis;
                double phR = aTgtR * 1.16;
                double tApR = Time(50, () => OrbitalManeuverCalculator.DeltaVToChangeApoapsis(self, 15.0, phR));
                double tPeR = Time(50, () => OrbitalManeuverCalculator.DeltaVToChangePeriapsis(self, 15.0, phR * 0.98));
                double tCirc = Time(50, () => OrbitalManeuverCalculator.DeltaVToCircularize(self, 15.0));
                W(string.Format("Δa={0:F0} km：MakePlan {1:F2} ms｜最近点扫描 {2:F2} ms｜相位角 {3:F3} ms｜价目 {4:F3} ms｜霍曼求解 {5:F2} ms｜窗口+两段式 {6:F2}+{7:F2} ms",
                    daKm, tPlan, tScan, tPhase, tCost, tHohmann, tWin, tTwoStage));
                W(string.Format("        调相子步骤：改远点 {0:F2} ms｜改近点 {1:F2} ms｜圆化 {2:F2} ms", tApR, tPeR, tCirc));
            }
            W("== 完 ==");
            Assert.True(true);
        }

        private static double Time(int n, Action body)
        {
            body();  // 预热
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) body();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds / n;
        }
    }
}
