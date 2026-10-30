using System;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **烧执行器政策**（`Base/Orbital/BurnExecutorPolicy.cs`，B5 第一份交付物，2026-09-23）。
    ///
    /// 这批用例钉两件事：
    /// ① **收敛不改行为**：`RendezvousGuidanceCore.SelectActuator` 转发到政策件后，三态判决与政策件**逐位一致**
    ///    （网格等价性）。⚠ 2026-10-27：语义收敛为"只按 Δv 窗口"——大 Δv 一律主网（等对准），
    ///    删掉了"未对准 ⇒ RCS 兜底"那一支（500 m/s 交 RCS 要 ~19 分钟，拖死节点）。
    /// ② ★ **把"计划所用执行器 ≠ 实际所用执行器"这条量化**（游戏实测：计划 0.8 s、实烧 14 s）——
    ///    这正是交接里"纯核复现失败"那条读数的**来路之一**，也是 B5 存在的理由。
    /// </summary>
    public class BurnExecutorPolicyTests
    {
        // ── 游戏实测样本（`交接-20260921…md` 更新 4 的日志）──
        //   [燃烧·拟] 计划 Δv=6.09｜命令加速度 a_cmd=8.0849（按主网 560 kN）
        //   [燃烧·头步] aCmd=0.4356｜可用a=0.4356｜fTotal=30000（= RCS 30 kN）
        //   [Burn] 实际时长=14s
        private const double SampleDv = 6.09;
        private const double SampleAMain = 8.0849;
        private const double SampleARcs = 0.4356;

        /// <summary>
        /// ① **收敛等价性**：政策件的布尔判决 ↔ 原有三态枚举，在整个参数网格上必须一致
        /// （否则"把规则收成一份"就悄悄改了行为）。
        /// </summary>
        [Fact]
        public void RcsRule_MatchesCoreActuator_TriStateAcrossGrid()
        {
            double[] dvs = { 0.0, 0.5, 6.09, 30.0, 200.0, 1500.0 };
            double[] mains = { 0.0, 0.4, 8.0849 };
            double[] rcss = { 0.0, 0.05, 0.4356, 1.5 };
            foreach (double dv in dvs)
            foreach (double aMain in mains)
            foreach (double aRcs in rcss)
            foreach (bool aligned in new[] { false, true })
            {
                var core = RendezvousGuidanceCore.SelectActuator(dv, aMain, aRcs, aligned);
                bool policyRcs = BurnExecutorPolicy.UseRcs(dv, aMain, aRcs, aligned);
                bool any = BurnExecutorPolicy.HasAnyActuator(aMain, aRcs);

                var expect = !any ? RendezvousGuidanceCore.Actuator.None
                    : policyRcs ? RendezvousGuidanceCore.Actuator.Rcs
                    : RendezvousGuidanceCore.Actuator.Main;
                Assert.True(core == expect,
                    $"收敛后行为漂移：dv={dv} aMain={aMain} aRcs={aRcs} aligned={aligned} ⇒ 转发版 {expect}、原版 {core}");
            }
        }

        /// <summary>四条语义各自点名（小 Δv 优先 RCS / 大 Δv 走主网 / 未对准兜底 / 无推进）。</summary>
        [Fact]
        public void RcsRule_ThresholdAndFallbacks()
        {
            // 小 Δv（≤ aRcs × 15 s）⇒ RCS，哪怕主推已对准
            Assert.True(BurnExecutorPolicy.UseRcs(0.4356 * 15.0, 8.0, 0.4356, mainAligned: true));
            // 大 Δv 且主推已对准 ⇒ 主网
            Assert.False(BurnExecutorPolicy.UseRcs(0.4356 * 15.0 + 0.01, 8.0, 0.4356, mainAligned: true));
            // 大 Δv 但主推未对准 ⇒ **主网（等对准）**：2026-10-27 裁决删掉"RCS 兜底"
            //（500 m/s 交 RCS 要烧 ~19 分钟，拖死节点，不是兜底）
            Assert.False(BurnExecutorPolicy.UseRcs(500.0, 8.0, 0.4356, mainAligned: false));
            // 无 RCS ⇒ 走主网（哪怕未对准：调用方继续转姿态）
            Assert.False(BurnExecutorPolicy.UseRcs(1.0, 8.0, 0.0, mainAligned: false));
            // 两者皆无 ⇒ 没有可用推进
            Assert.False(BurnExecutorPolicy.HasAnyActuator(0.0, 0.0));
        }

        /// <summary>① **原子烧门**：正在烧 ⇒ 拒发新烧（只能排队）。</summary>
        [Fact]
        public void AtomicGate_RejectsNewBurnWhileActive()
        {
            Assert.True(BurnExecutorPolicy.CanStartNewBurn(hasActiveBurn: false));
            Assert.False(BurnExecutorPolicy.CanStartNewBurn(hasActiveBurn: true));
        }

        /// <summary>
        /// ★★ **B5 第一份交付物**：把"规划器按主网估燃时、执行器按 RCS 烧"这条量化，并与游戏实测对表。
        /// 规划 0.8 s / 实烧 14 s（**17.5 倍**）是"每一发都晚、下一发节点落进过去"的机器。
        /// </summary>
        [Fact]
        public void PlannerMainVsExecutorRcs_ReproducesTheSeventeenfoldMismatch()
        {
            double planned = BurnExecutorPolicy.BurnSeconds(SampleDv, SampleAMain);
            double actual = BurnExecutorPolicy.BurnSeconds(SampleDv, SampleARcs);
            double ratio = BurnExecutorPolicy.PlannedOverActualBurnSeconds(SampleDv, SampleAMain, SampleARcs);

            Assert.InRange(planned, 0.70, 0.80);          // 游戏读 0.8 s
            Assert.InRange(actual, 13.5, 14.5);           // 游戏读 14 s
            Assert.InRange(ratio, 17.0, 19.0);            // 实测 17.5 倍（= aMain/aRcs）

            // ★ 级联的量级（⚠ 2026-09-23 更正：**整个 ΔT 堆到收尾**，不是对半分）：
            //   点火按**估**的燃时居中、燃烧按**真**的燃时进行 ⇒ 收尾 = UT + T_real − T_est/2 − delay
            double endOffset = BurnExecutorPolicy.BurnEndOffsetFromNode(actual, planned,
                executionDelaySeconds: 2.0);
            Assert.InRange(endOffset, 11.5, 11.7);
        }

        /// <summary>
        /// 燃时估错的形状：**收尾**偏离节点 `ΔT/2`（点火居中）。
        /// 若下一发节点排在这个偏移之内，它的点火时刻就已经在**过去**了 —— 这就是级联，
        /// 而不是"某处算错了一个数"。反向也要钉：估准（T_real = T_est）⇒ 收尾正好落在节点上。
        /// </summary>
        [Fact]
        public void BurnEndOffset_IsTheWholeDurationError_AccumulatedAtTheTail()
        {
            // 估准（T_real = T_est）⇒ 收尾落在 UT + T_est/2 − delay（居中，扣掉执行延迟）
            Assert.Equal(60.0 - 2.0, BurnExecutorPolicy.BurnEndOffsetFromNode(120.0, 120.0, 2.0), 9);
            // 实际比估算**久** ΔT ⇒ 收尾晚**整个 ΔT**（不是一半）：180 vs 120 ⇒ 60 s
            Assert.Equal(60.0 + 60.0 - 2.0, BurnExecutorPolicy.BurnEndOffsetFromNode(180.0, 120.0, 2.0), 9);
            // 实际比估算**短** ΔT ⇒ 收尾早**整个 ΔT**：60 vs 120 ⇒ −60 s
            Assert.Equal(60.0 - 60.0 - 2.0, BurnExecutorPolicy.BurnEndOffsetFromNode(60.0, 120.0, 2.0), 9);

            // 级联判据：偏移 11.6 s 远大于下一发节点的提前量（取 5 s）⇒ 下一发必定迟到
            double offset = BurnExecutorPolicy.BurnEndOffsetFromNode(13.98, 0.753, executionDelaySeconds: 2.0);
            Assert.True(offset > 5.0, $"偏移 {offset:F1}s 应当已经吃掉下一发节点的提前量");
        }

        /// <summary>
        /// `实交/指令` 的**读数口径**（游戏 `SpaceGameComp.cs:1372-1377`）：分子是窗口内**实测**冲量，
        /// 分母是同窗口**记账**扣减。本用例钉住"比值 &lt; 1 **可以**来自非物理来路"：
        /// · 窗口内 5 步里最后一步的冲量落在**下一步首**（记账已扣、实测未到）⇒ 读 4/5 = 0.8；
        /// · 同一个物理事实，换成一个"内部窗口"（冲量全部落进来）⇒ 读 1.0。
        /// ⇒ **读这个比值必须同时报窗口里有没有燃烧、以及它是不是收尾那一窗**（本库"读数必须能自证语境"）。
        /// </summary>
        [Fact]
        public void DeliveredOverCommanded_HasNonPhysicalSources_BelowOne()
        {
            const double dvPerStep = 2.0;
            const int steps = 5;

            // 收尾窗：5 步记账、只有 4 步的冲量落在窗口内
            double appliedTail = dvPerStep * (steps - 1);
            double commanded = dvPerStep * steps;
            Assert.Equal(0.8, BurnExecutorPolicy.DeliveredOverCommanded(appliedTail, commanded), 9);

            // 内部窗：5 步记账、5 步冲量都在
            Assert.Equal(1.0, BurnExecutorPolicy.DeliveredOverCommanded(dvPerStep * steps, commanded), 9);

            // 没有燃烧的窗口 ⇒ 分母为 0 ⇒ NaN（不是 0，也不是 1：不许冒充"正常"）
            Assert.True(double.IsNaN(BurnExecutorPolicy.DeliveredOverCommanded(0.0, 0.0)));
        }

        /// <summary>没有可用推进 ⇒ 燃时**无穷**（"烧不完"），不是 0（那会被读成"瞬间烧完"）。</summary>
        [Fact]
        public void BurnSeconds_IsInfiniteWithoutActuator_NotZero()
        {
            Assert.True(double.IsInfinity(BurnExecutorPolicy.BurnSeconds(10.0, 0.0)));
            Assert.True(double.IsInfinity(BurnExecutorPolicy.PlannedOverActualBurnSeconds(10.0, 5.0, 0.0)));
            Assert.Equal(2.0, BurnExecutorPolicy.BurnSeconds(10.0, 5.0), 9);
        }

        /// <summary>
        /// **MJ 反打转纪律**（`ShouldDropRemoteNodes`，游戏 `SpaceGameComp:3775` 调它）：
        /// 已进停下尺度**且**下一条节点还在未来 ⇒ 删光队列。两侧都要钉：
        /// 距离还远 ⇒ **不许删**（否则误杀正常远场节点）；下一条已在眼前/当前时刻 ⇒ 也不删（删了是丢活儿）。
        /// </summary>
        [Fact]
        public void RemoteNodesAreDroppedOnlyInsideMatchDistanceAndAheadOfTime()
        {
            const double match = 100.0;   // `rendezvousMatchDist`
            Assert.True(BurnExecutorPolicy.ShouldDropRemoteNodes(50.0, match, nextUt: 5000.0, now: 100.0));
            Assert.False(BurnExecutorPolicy.ShouldDropRemoteNodes(50_000.0, match, 5000.0, 100.0));
            Assert.False(BurnExecutorPolicy.ShouldDropRemoteNodes(50.0, match, 100.5, 100.0));   // 守卫 1 s
            Assert.False(BurnExecutorPolicy.ShouldDropRemoteNodes(50.0, match, 100.0, 100.0));   // 就是当前时刻
        }
    }
}
