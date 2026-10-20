using System;
using System.Diagnostics;
using MechJebLib.Primitives;
using PioneerFramework.Pure.Tests;      // 台架（RendezvousShip / ITarget / CircularTarget）
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **阶梯的规划成本与重规划率**（墙钟判据）。
    ///
    /// 这条判据回答的是"能不能进游戏"：物理步 2 s、帧预算 16 ms ⇒ **一次 `MakePlan` 要不要挤进帧**。
    ///
    /// ## 批5.B（2026-09-28）：本文件是原「A4 对照实验」的**幸存件**
    /// 原文件（`LadderVsLawComparison`）是"同一跑道只换规划器"的对照实验。用户已裁定旧律作废
    /// （"那个东西不能用了"），对照组随之消失 ⇒ 删掉：与律对照的整侧（含**零调用者**的
    /// `RunLaw`/`RunLadder` 与它自带的私有 `LadderPilot`）、以及 `A4_PlanLevel_CostTable`
    /// （`Assert.True(true)` 探针，对照目的随律作废）。
    /// **端到端**判据搬去了 `LadderEndToEndTests`（四条几何）；**本文件只留墙钟这条**。
    /// </summary>
    public class LadderPlanCostTests
    {
        private readonly ITestOutputHelper _out;
        public LadderPlanCostTests(ITestOutputHelper output) => _out = output;

        private const double Mu = 2.0e14;
        private const double R0 = 7_000_000.0;
        private const double BodyRadius = 5.0e6;
        private const double Dt = 5.0;
        /// <summary>顺行约定：法向 (0,−1,0) ⇒ 切向 ĥ×r̂ = (0,0,+1)（与两条台架同一套）。</summary>
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);

        [Fact]
        public void PlanCost_And_ReplanRate()
        {
            double vc = Math.Sqrt(Mu / R0);
            var self = OrbitView.FromStateVectors(new DVector3(R0, 0, 0), new DVector3(0, 0, vc), Mu, 0.0,
                OrbitNormal, BodyRadius);
            var targetOnly = CircularTarget.Create(R0, Mu, 90.0 * Math.PI / 180.0, OrbitNormal);
            var goal = OrbitView.FromStateVectors(targetOnly.PosAt(0.0), targetOnly.VelAt(0.0), Mu, 0.0,
                targetOnly.PlaneNormalAt(0.0), BodyRadius);

            var p = new RendezvousLadder.Params { DesiredDistance = 283.0, MinRadius = 5.1e6 };
            for (int i = 0; i < 3; i++) RendezvousLadder.MakePlan(self, goal, 0.0, p, out _, out _);   // 预热 JIT
            var sw = Stopwatch.StartNew();
            const int N = 200;
            for (int i = 0; i < N; i++) RendezvousLadder.MakePlan(self, goal, i * 10.0, p, out _, out _);
            sw.Stop();
            double perCallMs = sw.Elapsed.TotalMilliseconds / N;

            var ship = RendezvousShip.HighThrustShip(new DVector3(R0, 0, 0), new DVector3(0, 0, vc),
                new DVector3(0, 0, 1));
            var pilot = new LadderPilot();
            var sw2 = Stopwatch.StartNew();
            int steps = 0;
            bool aborted = false;
            // ⚠ **硬墙钟护栏**：仿真超过 `wallGuard` 秒就主动停并报出来。
            //   这条是为了兑现"再不许出现'跑了 5 分钟没动静'"—— 任何一次运行都必须**有界**。
            const double wallGuard = 20.0;
            for (double t = 0.0; t < 3_000.0; t += Dt)
            {
                if (sw2.Elapsed.TotalSeconds > wallGuard) { aborted = true; break; }
                ship.Propagate(pilot.Step(ship, targetOnly, Mu, t, Dt), Mu, Dt);
                steps++;
            }
            sw2.Stop();
            _out.WriteLine(string.Format(
                "\n[阶梯·成本] 单次 MakePlan = {0:F3} ms（{1} 次平均）｜短仿真 {2} 步（{3:F0} s{4}）：重规划 {5} 次、"
                + "墙钟 {6:F2} s ⇒ 平均每步 {7:F3} ms｜每步都在重规划？{8}"
                + "\n（游戏口径：物理步 2 s、帧预算 16 ms ⇒ **每次规划**要不要进帧才是关键）",
                perCallMs, N, steps, steps * Dt, aborted ? "，被护栏截断" : "", pilot.PlanCount,
                sw2.Elapsed.TotalSeconds, sw2.Elapsed.TotalMilliseconds / Math.Max(steps, 1),
                pilot.PlanCount >= steps));
            Assert.True(perCallMs < 50.0, $"单次规划 {perCallMs:F1} ms 太慢（游戏里一次规划要挤进帧预算）");
            Assert.False(aborted, "短仿真被墙钟护栏截断 ⇒ 说明每步成本太高（先看上面的'每步 ms'）");
        }

        /// <summary>
        /// 拦截级（很近、同轨 100km）的 `MakePlan` 成本：`IncludeDiagnostics` 开/关对照。
        /// 诊断段 = 独立 Shepperd 复算（closingTime/5 步），只写 status、不参与决策。
        /// </summary>
        [Fact]
        public void PlanCost_Intercept_DiagnosticsOnOff()
        {
            double vc = Math.Sqrt(Mu / R0);
            var self = OrbitView.FromStateVectors(new DVector3(R0, 0, 0), new DVector3(0, 0, vc), Mu, 0.0,
                OrbitNormal, BodyRadius);
            // 100 km 相位差（仍 < r/25 = 280 km ⇒ 走 veryClose 拦截级）
            double phase = 100_000.0 / R0;
            var targetOnly = CircularTarget.Create(R0, Mu, phase, OrbitNormal);
            var goal = OrbitView.FromStateVectors(targetOnly.PosAt(0.0), targetOnly.VelAt(0.0), Mu, 0.0,
                targetOnly.PlaneNormalAt(0.0), BodyRadius);

            var pOn = new RendezvousLadder.Params { DesiredDistance = 283.0, MinRadius = 5.1e6, IncludeDiagnostics = true };
            var pOff = new RendezvousLadder.Params { DesiredDistance = 283.0, MinRadius = 5.1e6, IncludeDiagnostics = false };

            var rungOn = RendezvousLadder.MakePlan(self, goal, 0.0, pOn, out _, out string stOn);
            for (int i = 0; i < 20; i++)
            {
                RendezvousLadder.MakePlan(self, goal, 0.0, pOn, out _, out _);
                RendezvousLadder.MakePlan(self, goal, 0.0, pOff, out _, out _);
            }

            const int N = 300;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) RendezvousLadder.MakePlan(self, goal, 0.0, pOn, out _, out _);
            sw.Stop();
            double onMs = sw.Elapsed.TotalMilliseconds / N;

            sw.Restart();
            for (int i = 0; i < N; i++) RendezvousLadder.MakePlan(self, goal, 0.0, pOff, out _, out _);
            sw.Stop();
            double offMs = sw.Elapsed.TotalMilliseconds / N;

            _out.WriteLine(string.Format(
                "\n[阶梯·成本·拦截] rung={0}｜诊断开={1:F3} ms｜诊断关={2:F3} ms｜省={3:F3} ms（{4:F1}%）"
                + "\n  诊断开 status（前 160 字）：{5}",
                rungOn, onMs, offMs, onMs - offMs, (onMs - offMs) / Math.Max(onMs, 1e-9) * 100.0,
                stOn.Length > 160 ? stOn.Substring(0, 160) : stOn));

            Assert.True(offMs <= onMs + 0.05, $"关闭诊断不应更慢（开={onMs:F3} 关={offMs:F3}）");
        }
    }
}
