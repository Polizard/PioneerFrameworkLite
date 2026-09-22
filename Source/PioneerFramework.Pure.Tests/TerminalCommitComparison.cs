using System;
using System.IO;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **B1.3 先验：终端该"滚动重解"还是"提交一份计划"？**（2026-09-20）
    ///
    /// ## 为什么要先验
    /// 计划稿 B1.3 要把终端从"每 10 s 重解重发"改成"提交两个条目"，但**上一轮"提交 dv2"台架实测失败**
    /// （15° → 9.40 圈不收敛）⇒ 留下一条假设：**失败根因是"冻结向量"，而不是"提交"本身**。
    /// 本测 = 三档对照：A 滚动重解（现状）／B 提交+冻结／C 提交+到达时重解（B1.2 的条件语义）。
    ///
    /// ## ⚠ 接线纪律（上一版就是在这里翻车的）
    /// **整套接线照抄 `RendezvousGuidanceCoreTests.T5_Terminal_ReachesTolerance`**（它是**已知通过**的终端交接测试：
    /// `MidThrustShip(R2)`、`CircularTarget.Create(R2, Mu, dphi0, OrbitNormal)`、`TerminalHandoff=true`、
    /// `ReArmLegs=true`、`TimeBudget0=budget`、`TerminalEngageFraction=0.05`、dt=5）。
    /// 上一版我自己另起炉灶（高推船 / 没设那几个开关 / 相位角多加了 180°）⇒ 三档全逃逸 ✗，
    /// **连"已知会通过"的 A 档都逃逸** ⇒ 那是仪器坏，不是被测物的结论。
    /// ⇒ **第一个断言必须是 A 档收敛**（先证仪器 ✓）。
    /// </summary>
    public class TerminalCommitComparison
    {
        private readonly ITestOutputHelper _out;
        public TerminalCommitComparison(ITestOutputHelper output) => _out = output;

        private const double Mu = 2.0e14;
        private const double R2 = 2.6322e7;          // GEO（与 T5 同）
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);
        private const double Dt = 5.0;
        private const double Budget = 192_000.0;     // T5 的第二个档
        private const double Dist = 300.0, RelVel = 2.0;   // 与 HasRendezvoused 默认同口径
        private static readonly double Period = 2.0 * Math.PI * Math.Sqrt(R2 * R2 * R2 / Mu);

        private static void Flush(string s)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "pfl_term_commit.txt"), s + "\n"); }
            catch { }
        }

        private sealed class Run
        {
            public bool Reached; public double Seconds, TotalDv, TerminalDv, FinalDist, FinalRel;
            public bool Crashed;
        }

        private static Run Fly(bool commit, bool resolveAtBurn)
        {
            double dphi0 = 2.0 * Math.PI / 6.0;
            var ship = T5Ship();
            var target = CircularTarget.Create(R2, Mu, dphi0, OrbitNormal);
            var pilot = new InPlaneRendezvousPilot
            {
                TimeBudget = Budget, ClosePhase = true, TerminalHandoff = true, ReArmLegs = true,
                TimeBudget0 = Budget, TerminalEngageFraction = 0.05,
                CommitTwoImpulse = commit, CommitResolveAtBurn = resolveAtBurn,
            };

            var r = new Run();
            double t = 0.0, dist = double.NaN, rel = double.NaN;
            while (t < Budget + 400_000.0)
            {
                pilot.Step(ship, target, Mu, t, Dt, out _, out dist, out rel, out _);
                t += Dt;
                if (RendezvousGuidanceCore.HasRendezvoused(dist, rel)) break;
            }
            r.Reached = RendezvousGuidanceCore.HasRendezvoused(dist, rel);
            r.Seconds = t; r.FinalDist = dist; r.FinalRel = rel; r.Crashed = ship.Crashed;
            r.TotalDv = ship.MainDv + ship.RcsDv;
            r.TerminalDv = pilot.TerminalDv;
            return r;
        }

        /// <summary>与 T5 同参数的中推船（**逐字照抄 `RendezvousGuidanceCoreTests.MidThrustShip`**：
        /// 8000 N / ṁ2 / 料 12000 + RCS 1500 / ṁ1 / 500）。
        /// ⚠⚠ **逃逸的真因就在这两行**：现有测试的约定是 `OrbitNormal=(0,-1,0)` ⇒ 顺行切向 `ĥ×r̂ = (0,0,+1)`
        /// ⇒ 初速与**主推轴**都用 `(0,0,+1)`。我上一版写成 `(0,0,-1)` ⇒ **船在逆行** ⇒ 与目标永远交不上、
        /// 一直烧到 9.5 km/s 逃逸 ✗（而三档全逃逸正是"仪器坏"的签名——**连已知会通过的 A 档也逃逸**）。
        /// </summary>
        private static RendezvousShip T5Ship()
        {
            var s = RendezvousShip.Spawn(new DVector3(R2, 0, 0),
                new DVector3(0, 0, Math.Sqrt(Mu / R2)), new DVector3(0, 0, 1));
            s.DryMass = 5000.0;
            s.MainThrust = 8000.0; s.MainVe = 4000.0; s.MainMassFlow = 2.0; s.MainFuel = 12000.0;
            s.RcsThrust = 1500.0; s.RcsVe = 1500.0; s.RcsMassFlow = 1.0; s.RcsFuel = 500.0;
            return s;
        }

        [Fact]
        public void ThreeTerminalPolicies()
        {
            Flush("── 终端政策三档（T5 底座：GEO 60°、中推船、预算 192000 s；判据 300 m / 2 m/s）──");
            var a = Fly(commit: false, resolveAtBurn: false);
            Flush("  " + Row("A 滚动重解（现状）", a));
            var b = Fly(commit: true, resolveAtBurn: false);
            Flush("  " + Row("B 提交 + 冻结", b));
            var c = Fly(commit: true, resolveAtBurn: true);
            Flush("  " + Row("C 提交 + 到达时重解", c));

            var sb = new StringBuilder();
            sb.Append("\n══ 终端政策三档（T5 底座）══");
            sb.Append("\n  ").Append(Row("A 滚动重解（现状）", a));
            sb.Append("\n  ").Append(Row("B 提交 + 冻结", b));
            sb.Append("\n  ").Append(Row("C 提交 + 到达时重解", c));
            _out.WriteLine(sb.ToString());

            // ★ 第一断言 = 仪器自证：A 档（现状）**必须**收敛（T5 已证）⇒ 它不收敛就说明接线又错了 ✗
            Assert.True(a.Reached, "仪器自证失败：A 档（现状政策，T5 已证会收敛）未收敛 ⇒ 先修接线，别读政策结论");
        }

        private static string Row(string name, Run r)
        {
            string head = name.PadRight(22);
            string tail = string.Format("｜{0:F1} 圈｜Δv {1:F0}（终端 {2:F0}，占 {3:P0}）｜末 {4:F0} m / {5:F2} m/s{6}",
                r.Seconds / Period, r.TotalDv, r.TerminalDv,
                r.TotalDv > 1e-9 ? r.TerminalDv / r.TotalDv : 0.0, r.FinalDist, r.FinalRel,
                r.Crashed ? "｜**侵地**" : "");
            return head + (r.Reached ? "达标" : "**未达判据**") + tail;
        }
    }
}
