using System;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **小 Δv 的结构性失配 + `实交/指令 = 0.481` 的边界**（B5-5，2026-09-23）。
    ///
    /// ## 这条链（游戏实测：计划 0.8 s / 实烧 14 s / `实交/指令 = 0.481`）
    /// 1. **规划器**按**主网**估燃时（`BurnCalculator.Estimate` → `BurnCore.Estimate`：主网＋RCS 接力，
    ///    小 Δv 时主网一口吃下）⇒ `T_est = Δv·m₀/F_main`；
    /// 2. **执行器**按政策选网：`dv ≤ aRcs × 15 s` ⇒ **RCS** ⇒ `T_real = Δv·m₀/F_rcs`；
    /// 3. 两者比值 = `F_main/F_rcs`（本是**结构性**的：同一个 Δv，两条网差一个推力档）；
    /// 4. 点火居中 ⇒ 收尾偏离节点 `(T_real − T_est)/2` ⇒ 下一发节点若排得更近，就落进过去 ⇒ 级联。
    ///
    /// ## 本文件的两条判据
    /// · **结构性失配**：用游戏那组真值（560 kN 主网 / 30 kN RCS / Δv 6.09）钉住"估的是主网、烧的是 RCS"，
    ///   以及 18.6 倍的比值 —— 这条**不是 bug，是两条政策的交界面**（要改就得改其中一条的口径）；
    /// · ★ **`实交/指令` 的物理可达范围**：把已建模的政策全摆上，该比值只可能落在
    ///   **收尾窗 `(n−1)/n`** 与 **内部窗 1.0** 之间（dt=2 s、控制 10 s ⇒ 下界 **0.8**）。
    ///   ⇒ **0.481 落在这个范围之外** ⇒ 它**不可能**由这条链单独产生 ⇒ 必然还有一条未建模的来路
    ///   （候选：活差额靶路径的逐拍重发与 `burnThrottleCap` 的"差额即计数"、或诊断口径与重发的交互）。
    ///   把这条件为**护栏**留着：将来谁改政策把下界压到 0.8 以下，这条会红。
    /// </summary>
    public class SmallDvMismatchBenchTests
    {
        // ── 游戏实测那组真值（`交接-20260921…md` 更新 4 的日志）──
        private const double SampleDv = 6.09;
        private const double MainThrust = 560_000.0;   // 主网（560 kN）
        private const double RcsThrust = 30_000.0;     // RCS（30 kN）
        // ⚠ 质量由日志反推：`[燃烧·头步] aCmd=0.4356 / fTotal=30000` ⇒ m ≈ 30 t/0.4356 ≈ **68.9 t**；
        //   规划端 `a_cmd=8.0849 / 560 kN` ⇒ m ≈ 69.3 t ✓ 两处自洽（**不要用另一个场景的 75 t**，
        //   那会让 RCS 阈值变成 6.0 < Δv 6.09 ⇒ 判成主网，整条链就不成立了）。
        private const double Mass0 = 69_000.0;
        private const double Dt = 2.0;                 // 物理步（Normal）
        private const double ControlPeriod = 10.0;     // 控制周期（`NewLawControlDt`）

        /// <summary>
        /// ① **结构性失配**：同一个 Δv，估算走主网、执行走 RCS ⇒ 燃时差 `F_main/F_rcs`。
        /// 并钉住"这条链的触发边界"：`dv ≤ aRcs × 15 s` 才选 RCS（本样本 6.09 ≤ 6.53 ⇒ 恰好在内）。
        /// </summary>
        [Fact]
        public void SmallDv_EstimateUsesMain_ExecutorUsesRcs_ByConstruction()
        {
            double aMain = MainThrust / Mass0;             // 7.4667 m/s²
            double aRcs = RcsThrust / Mass0;               // 0.4 m/s²

            // 执行器选择：小 Δv ⇒ RCS（政策件）
            Assert.True(BurnExecutorPolicy.UseRcs(SampleDv, aMain, aRcs, mainAligned: true),
                "小 Δv 应当被政策判给 RCS（这正是「估算按主网、执行按 RCS」的开关）");
            // 边界：再大一点点就回主网 ⇒ 失配只在"小 Δv 带"里出现
            Assert.False(BurnExecutorPolicy.UseRcs(aRcs * BurnExecutorPolicy.RcsFineDvWindowSeconds + 1.0,
                aMain, aRcs, mainAligned: true));

            // 燃时：估 0.815 s（主网）、实 15.2 s（RCS）—— 与游戏实测 0.8 s / 14 s 同档
            double tEst = BurnExecutorPolicy.BurnSeconds(SampleDv, aMain);
            double tReal = BurnExecutorPolicy.BurnSeconds(SampleDv, aRcs);
            Assert.InRange(tEst, 0.7, 0.9);
            Assert.InRange(tReal, 14.0, 16.0);

            // 比值 = 推力比（结构性，与 Δv 无关）
            double ratio = BurnExecutorPolicy.PlannedOverActualBurnSeconds(SampleDv, aMain, aRcs);
            Assert.Equal(MainThrust / RcsThrust, ratio, 6);
        }

        /// <summary>
        /// ② **级联**：燃时估短 18.6 倍 ⇒ 收尾晚 `ΔT/2`；若下一发节点排得更近，它的点火时刻已经在过去，
        /// 而此时本发**还在烧**（原子烧门 ⇒ 新烧被拒）⇒ 只能等 ⇒ 每一发都晚。
        /// </summary>
        [Fact]
        public void SmallDv_LateEnd_BlocksTheNextNode_AndCascades()
        {
            double aMain = MainThrust / Mass0;
            double aRcs = RcsThrust / Mass0;
            double tEst = BurnExecutorPolicy.BurnSeconds(SampleDv, aMain);
            double tReal = BurnExecutorPolicy.BurnSeconds(SampleDv, aRcs);

            double firstNodeUt = 0.0;
            double ignition = BurnExecutorPolicy.CenteredIgnitionTick(
                firstNodeUt, tEst, phaseShiftSeconds: 0.0, executionDelaySeconds: Dt);
            double endTick = ignition + tReal;
            double lateBy = BurnExecutorPolicy.BurnEndOffsetFromNode(tReal, tEst, Dt);
            Assert.InRange(lateBy, 11.0, 12.5);                     // ≈ +11.6 s（**整个 ΔT** 堆在收尾）
            Assert.Equal(firstNodeUt + lateBy, endTick, 6);         // 恒等式：收尾 = 节点 + 偏移

            // 下一发排在首发的 +5 s（比"晚收尾"更近）⇒ 下一发的点火时刻已在过去
            double nextNodeUt = firstNodeUt + 5.0;
            double nextIgnition = BurnExecutorPolicy.CenteredIgnitionTick(
                nextNodeUt, tEst, phaseShiftSeconds: 0.0, executionDelaySeconds: Dt);
            Assert.True(nextIgnition < endTick,
                $"下一发的点火时刻 {nextIgnition:F1}s 应当落在首发收尾 {endTick:F1}s 之前 ⇒ 到点时还在烧");

            // 原子烧门：此时"开新烧"被拒 ⇒ 只能等首发烧完（这就是"每一发都晚"的机制）
            Assert.False(BurnExecutorPolicy.CanStartNewBurn(hasActiveBurn: true));
        }

        /// <summary>
        /// ③ ★ **`实交/指令` 的物理可达范围**（护栏）：在已建模的政策下，该比值只可能落在
        /// **收尾窗 `(n−1)/n`** 与 **内部窗 1.0** 之间 —— dt=2 s、控制 10 s ⇒ 窗内 5 步 ⇒ 下界 **0.8**。
        /// 而游戏读到 **0.481 < 0.8** ⇒ **这条链单独产生不出来那个数** ⇒ 还有一条未建模的来路。
        /// （这条不是"复现成功"，是**把复现边界量化**：B5 要的正是"分得清模型错还是实现错"。）
        /// </summary>
        [Fact]
        public void DiagnosticRatio_LowerBoundIsTailWindow_And0481LiesOutsideIt()
        {
            int stepsPerWindow = BurnExecutorPolicy.PhysicsStepsPerControlPeriod(Dt, ControlPeriod);
            Assert.Equal(5, stepsPerWindow);

            const double dvPerStep = 4.0;
            double interior = BurnExecutorPolicy.DeliveredOverCommanded(
                dvPerStep * stepsPerWindow, dvPerStep * stepsPerWindow);
            double tail = BurnExecutorPolicy.DeliveredOverCommanded(
                dvPerStep * (stepsPerWindow - 1), dvPerStep * stepsPerWindow);

            Assert.Equal(1.0, interior, 9);
            Assert.Equal(0.8, tail, 9);

            // 游戏读数 0.481 落在 [0.8, 1.0] 之外 ⇒ 不是这条链（也不是"收尾窗"）能给的
            const double gameReading = 0.481;
            Assert.True(gameReading < tail,
                $"0.481 应当落在下界 {tail:F2} 之外 ⇒ 说明还有未建模的来路（若这条不再成立，说明政策改了）");
        }

        /// <summary>
        /// ★★ **失配是一条"政策域泄漏"，不是物理**（2026-09-23 查明）：无 Map 执行器的注释声明
        /// "RCS 一个物理步烧得完的活才交给 RCS"（`aRcs·dt`），而实现里**没有这条比较**，只有燃料/对准分支
        /// ⇒ 那组 `Δv=6.09` 本该走**主推**（一步阈值只有 0.87 m/s）⇒ 燃时与估算同为 0.75 s
        /// ⇒ **18.6 倍失配与随后的级联根本不会发生**。
        /// 本用例把两条政策的**差域**量出来：`[aRcs·dt, aRcs·15 s]` 这一段两条结论相反。
        /// </summary>
        [Fact]
        public void OneStepRuleVsFifteenSecondRule_HaveAnOppositeBand_AndTheSampleFallsInsideIt()
        {
            double aRcs = RcsThrust / Mass0;                       // 0.43478 m/s²
            double oneStepThreshold = aRcs * Dt;                    // 0.87 m/s
            double fifteenSecThreshold = aRcs * BurnExecutorPolicy.RcsFineDvWindowSeconds;   // 6.52 m/s

            Assert.True(oneStepThreshold < fifteenSecThreshold);
            Assert.InRange(oneStepThreshold, 0.8, 0.9);
            Assert.InRange(fifteenSecThreshold, 6.4, 6.6);

            // 样本落在差域里：15 s 规则说 RCS、一步规则说主推 —— 结论相反
            Assert.True(SampleDv > oneStepThreshold && SampleDv < fifteenSecThreshold,
                $"样本 {SampleDv} 应落在差域 ({oneStepThreshold:F2}, {fifteenSecThreshold:F2}) 内");
            Assert.True(BurnExecutorPolicy.UseRcs(SampleDv, MainThrust / Mass0, aRcs, mainAligned: true));
            Assert.False(BurnExecutorPolicy.UseRcsWithinOneStep(SampleDv, aRcs, Dt));

            // 一步判据下走主推 ⇒ 燃时 = 估算（失配消失）
            Assert.InRange(BurnExecutorPolicy.BurnSeconds(SampleDv, MainThrust / Mass0), 0.7, 0.9);
        }
    }
}
