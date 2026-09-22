using System;
using System.Collections.Generic;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **带队列的执行器台架**（B5-3，2026-09-23）。
    ///
    /// ## 它补的是什么
    /// 交接 §B5 第 ④ 项：**"队列整体不存在"** —— 台架此前只有 `LadderVsLawComparison.LadderPilot`
    /// 一个近似物（按节点到点才烧、最小重规划间隔），**缺"原子烧 / 只处理第一条 / 可行性拒绝 /
    /// 队列非空 ⇒ 引导让位 / 反打转删远程节点 / 撤回过期计划"**。
    ///
    /// ## 形状
    /// · **队列状态本体在台架里**（游戏里它是 `SpaceEntityGroup.plannedManeuvers`；本库 J3 禁影子类型，
    ///   故**不**在纯层另立一个队列类型）；
    /// · **判据全部调 `BurnExecutorPolicy`**（让位 / 预点火窗 / 居中点火 / 反打转 / 撤回）⇒ 判据只有一份；
    /// · **每步的烧调 `BurnStep.Compute`**（同样是游戏那一份）；
    /// · 冲量**在下一步首才并入**（一步延迟，与 `ApplyPendingImpulses` 的位置一致）。
    ///
    /// ⇒ 于是"台架 vs 游戏"的差**只剩环境**（轨道、调度、积分器），这正是 `BurnStep.cs` 立下的规矩。
    /// </summary>
    public class QueueBurnBenchTests
    {
        // ── plant（与其它台架同档的过推船，取值口径见 `BurnWindowTerminationTests`）──
        private const double AFull = 160_000.0 / 70_000.0;   // F/m ≈ 2.2857 m/s²（本台架不建模质量账）
        private const double MassFlow = 140.0;
        private const double Dt = 2.0;                        // 物理步（`UPDATE_INTERVAL_TICKS`）
        private const double ExecDelay = 2.0;                 // `BurnArc.ExecutionDelaySeconds`
        private const double Lookahead = 600.0;               // `ManeuverPreBurnLookaheadTicks`
        private const double MatchDistance = 100.0;           // `rendezvousMatchDist`（停下尺度，非完成容差）

        /// <summary>队列条目：只保留与**执行**有关的三个字段（照抄 `PlannedManeuver` 的对应项）。</summary>
        private readonly struct Entry
        {
            public readonly double executeTick, dv, planEpoch;
            public Entry(double executeTick, double dv, double planEpoch = 0.0)
            { this.executeTick = executeTick; this.dv = dv; this.planEpoch = planEpoch; }
        }

        private sealed class Outcome
        {
            public readonly List<(double startTick, double endTick, double planned)> Burns = new List<(double, double, double)>();
            public double AppliedDv;     // 累计**真正并入速度**的 Δv（含一步延迟）
            public int Dropped, Retracted;
        }

        /// <summary>
        /// 带队列的执行器台架（口径见类注释）。`distAt` 给"当前到目标的距离"（反打转判据用）。
        /// </summary>
        private static Outcome RunQueue(List<Entry> queue, double horizon, Func<double, double>? distAt = null,
            double replanEpochAtTick = double.NaN, double plannedDv = 0.0)
        {
            var o = new Outcome();
            double epoch = 0.0;

            double remaining = 0.0;          // 当前燃烧剩余计划 Δv
            double pendingDv = 0.0;          // ③ 一步延迟：上一步算出的冲量，本步首才并入
            bool active = false;
            double burnStart = 0.0, burnPlanned = 0.0;

            for (double t = 0.0; t <= horizon; t += Dt)
            {
                // ── 物理步首：施加挂起的冲量（`ApplyPendingImpulses` 的位置）──
                if (pendingDv > 0.0) { o.AppliedDv += pendingDv; pendingDv = 0.0; }

                // ── 反打转（MJ 纪律）：已进停下尺度 + 下一条还在未来 ⇒ 删光队列 ──
                if (queue.Count > 0 && distAt != null
                    && BurnExecutorPolicy.ShouldDropRemoteNodes(distAt(t), MatchDistance, queue[0].executeTick, t))
                {
                    o.Dropped += queue.Count;
                    queue.Clear();
                }

                // ── 撤回上一条计划未执行的节点（新 epoch 生效）──
                if (!double.IsNaN(replanEpochAtTick) && t >= replanEpochAtTick && epoch == 0.0)
                {
                    epoch = 1.0;
                    int n = queue.RemoveAll(e => BurnExecutorPolicy.ShouldRetractStaleNode(e.planEpoch, epoch));
                    o.Retracted += n;
                    if (plannedDv > 0.0) queue.Add(new Entry(t + 60.0, plannedDv, epoch));   // 新计划
                }

                if (active)
                {
                    // 继续烧（原子：期间不受理新条目）
                    BurnStep.Result bs = BurnStep.Compute(0.0, AFull, MassFlow, Dt, remaining);
                    remaining = bs.RemainingAfter;
                    pendingDv = bs.Dv;
                    if (remaining <= 0.0)
                    {
                        active = false;
                        o.Burns.Add((burnStart, t + Dt, burnPlanned));
                    }
                }
                else if (queue.Count > 0)
                {
                    // ① 原子烧门：没有正在进行的烧才受理**第一条**
                    Entry e = queue[0];
                    double burnSecEst = BurnExecutorPolicy.BurnSeconds(e.dv, AFull);
                    bool windowOk = BurnExecutorPolicy.InPreBurnWindow(t, e.executeTick, Lookahead);
                    double ignitionTick = BurnExecutorPolicy.CenteredIgnitionTick(e.executeTick, burnSecEst, 0.0, ExecDelay);
                    if (windowOk && t >= ignitionTick)
                    {
                        queue.RemoveAt(0);
                        active = true;
                        burnStart = t;
                        burnPlanned = e.dv;
                        remaining = e.dv;
                        BurnStep.Result bs = BurnStep.Compute(0.0, AFull, MassFlow, Dt, remaining);
                        remaining = bs.RemainingAfter;
                        pendingDv = bs.Dv;
                    }
                }
            }
            return o;
        }

        /// <summary>① 顺序 + 原子：两条目按队列顺序执行、**永不重叠**、交付量≈各自计划。</summary>
        [Fact]
        public void Queue_RunsEntriesInOrder_AndNeverOverlaps()
        {
            var q = new List<Entry> { new Entry(100.0, 20.0), new Entry(300.0, 10.0) };
            var o = RunQueue(q, horizon: 800.0);

            Assert.Equal(2, o.Burns.Count);
            Assert.True(o.Burns[0].endTick <= o.Burns[1].startTick,
                $"两发重叠了：第一发到 {o.Burns[0].endTick:F0}s，第二发从 {o.Burns[1].startTick:F0}s 开始");
            Assert.True(Math.Abs(o.Burns[0].planned - 20.0) < 1e-9 && Math.Abs(o.Burns[1].planned - 10.0) < 1e-9);
            Assert.True(Math.Abs(o.AppliedDv - 30.0) < 0.6, $"总交付应≈30 m/s，实得 {o.AppliedDv:F2}");
        }

        /// <summary>
        /// ★ **居中点火**：点火时刻 = `UT − 燃时/2 − 执行延迟` ⇒ 燃烧**跨在节点两侧**（不是"到点才开始"）。
        /// 这条钉住"冲量模型的瞬间落在燃烧中点"这个政策，与 MJ 的 `ignitionUT = node.UT − halfBurnTime` 同构。
        /// </summary>
        [Fact]
        public void Queue_IgnitionIsCenteredOnTheNode()
        {
            const double dv = 20.0;
            var q = new List<Entry> { new Entry(200.0, dv) };
            var o = RunQueue(q, horizon: 400.0);

            Assert.Single(o.Burns);
            double burnSec = BurnExecutorPolicy.BurnSeconds(dv, AFull);
            double expectStart = BurnExecutorPolicy.CenteredIgnitionTick(200.0, burnSec, 0.0, ExecDelay);
            Assert.True(Math.Abs(o.Burns[0].startTick - expectStart) <= Dt,
                $"点火应≈{expectStart:F0}s（UT−半程−延迟），实得 {o.Burns[0].startTick:F0}s");

            // 燃烧窗口应**跨过节点**：起点在节点前、终点在节点后（半程两侧对称到一步之内）
            Assert.True(o.Burns[0].startTick < 200.0 && o.Burns[0].endTick > 200.0,
                $"燃烧应跨在节点两侧：{o.Burns[0].startTick:F0} → {o.Burns[0].endTick:F0}");
        }

        /// <summary>
        /// ★ **过期节点不超时、照点**：节点已在过去（点火时刻早就过了）⇒ 本步就点，**永不放弃**。
        /// 对应上游纪律"没对准就一直等、宁可烧晚也不打偏"（我曾加 25 s 超时 ⇒ 超时即打偏）。
        /// </summary>
        [Fact]
        public void Queue_ExpiredNodeStillFires_NoTimeout()
        {
            var q = new List<Entry> { new Entry(-500.0, 20.0) };   // 节点在过去 500 s
            var o = RunQueue(q, horizon: 200.0);

            Assert.Single(o.Burns);
            Assert.Equal(0.0, o.Burns[0].startTick, 6);            // 第 0 步就点（不等待、不放弃）
        }

        /// <summary>
        /// ★ **反打转**（MJ 纪律）：已进停下尺度（&lt; 100 m）且下一条节点还在遥远的未来 ⇒ **删光队列**。
        /// 反向也要钉：距离还远时**不许删**（否则会把正常的远场节点误杀）。
        /// </summary>
        [Fact]
        public void Queue_AntiSpin_DropsAllNodesOnlyInsideMatchDistance()
        {
            var q1 = new List<Entry> { new Entry(5000.0, 20.0) };
            var far = RunQueue(q1, horizon: 100.0, distAt: _ => 50_000.0);
            Assert.Equal(0, far.Dropped);
            Assert.Empty(far.Burns);

            var q2 = new List<Entry> { new Entry(5000.0, 20.0) };
            var near = RunQueue(q2, horizon: 100.0, distAt: _ => 50.0);
            Assert.Equal(1, near.Dropped);
            Assert.Empty(near.Burns);
        }

        /// <summary>
        /// ★ **撤回过期计划的节点**：新计划（epoch 递增）一到，上一条计划**尚未执行**的节点必须被撤回，
        /// 否则它会"按过期计划的到达点"打出一记错向冲量（实测一步 1.5 km → 11.2 km）。
        /// </summary>
        [Fact]
        public void Queue_Retraction_DropsStaleNodesFromOlderPlanEpoch()
        {
            var q = new List<Entry> { new Entry(5000.0, 20.0, planEpoch: 0.0) };   // 远期、旧计划
            var o = RunQueue(q, horizon: 200.0, replanEpochAtTick: 100.0, plannedDv: 15.0);

            Assert.Equal(1, o.Retracted);              // 旧条目被撤
            Assert.Single(o.Burns);                    // 只有新计划那一发
            Assert.True(Math.Abs(o.Burns[0].planned - 15.0) < 1e-9);
        }

        /// <summary>**队列非空 ⇒ 引导让位**（任何条目都让，不只 Manual —— 这条比纯规则严）。</summary>
        [Fact]
        public void Queue_GuidanceYieldsWhileNonEmpty()
        {
            Assert.False(BurnExecutorPolicy.GuidanceYieldsToQueue(0));
            Assert.True(BurnExecutorPolicy.GuidanceYieldsToQueue(1));
            Assert.True(BurnExecutorPolicy.GuidanceYieldsToQueue(7));
        }

        /// <summary>
        /// **预点火窗**：只在节点前 `lookahead` 秒内才开始处理这一条（可行性校验与点火都在窗内）。
        /// 窗**之前**不处理（否则会在离节点还很远时就点火）。
        /// </summary>
        [Fact]
        public void Queue_NothingStartsBeforePreBurnWindow()
        {
            var q = new List<Entry> { new Entry(4000.0, 20.0) };   // 窗 = 3400 s
            var o = RunQueue(q, horizon: 3400.0);                  // 恰好到窗边（不含）
            Assert.Empty(o.Burns);
            Assert.True(BurnExecutorPolicy.InPreBurnWindow(3400.0, 4000.0, Lookahead));
            Assert.False(BurnExecutorPolicy.InPreBurnWindow(3399.0, 4000.0, Lookahead));
        }
    }
}
