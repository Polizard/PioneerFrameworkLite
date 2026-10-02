using System;
using System.Collections.Generic;
using System.Text;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **电子战系统 · 一键全测**（2026-10-02 用户要求"一键测试整个电子战系统"）。
    ///
    /// ## 它做什么
    /// 把已有的五段验收**按依赖顺序自动跑完**，最后给一条**合并结论**（判据总数 / 未过 / UNCHECKED）：
    ///
    /// | # | 段 | 覆盖 |
    /// |---|---|---|
    /// | ① | [V7] 建筑级投影链 | 能力面：建筑 → 群级侦测技术/雷达、W1 开机升幅、波束三模式、打伤减半、摧毁归零 |
    /// | ② | [V3] 认知层 | 点亮 / 影子（σ 涨）/ 失联 / 恢复、点查询不刷新、公开投影 |
    /// | ③ | [V3] 视野对账 | 消费面：绘制/点击准入 = 认知层可见集（不漏画、不多画） |
    /// | ④ | [E3]/[N6] EW 浅层 | 干扰抬阈值 → 烧穿（内外两侧钉）、干扰机自暴露、开火揭示 |
    /// | ⑤ | [E5] 假目标 | 依赖对方开机（DRFM）、识破 = 深层提示、**标记单调**（去伪存真）、生命期 |
    ///
    /// ## 三段纪律
    /// · **顺序**：前一段跑完（`Running == false`）才起下一段；每段结束**自动清场**（总控自己的快照口径，
    ///   `PflTestKit.SnapshotGroups/CleanupSince` ⇒ 只销毁本段铺的场，不动玩家资产）；
    /// · **读数**：每段收割它自己的结算行（从 `PflTestKit.Cases` 读最后一个用例的判据），合并成一行一行；
    /// · **手动项**：①的存档往返用**单件按钮**跑（本总控会把站清掉，手动项在单跑时做）。
    /// </summary>
    public static class EwSystemAcceptanceRunner
    {
        private sealed class Seg
        {
            // 全部用对象初始化器赋值 ⇒ 给默认值消掉可空告警（本类型只在本文件的静态表里构造）
            public string label = "";
            public Action start = () => { };
            public Func<bool> busy = () => false;      // true = 异步段还在跑
        }

        private static readonly Seg[] Segments =
        {
            new Seg { label = "① [V7] 能力面：建筑级投影 / W1 开机 / 波束三模式 / 打伤 / 摧毁",
                      start = SensorProjectionAcceptanceRunner.Run,
                      busy = () => SensorProjectionAcceptanceRunner.Running },
            new Seg { label = "② [V3] 认知层：点亮 / 影子 / 失联 / 恢复 + 两个读入口",
                      start = SensorCognitionAcceptanceRunner.Run,
                      busy = () => SensorCognitionAcceptanceRunner.Running },
            new Seg { label = "③ [V3] 消费面：视野对账（绘制/点击准入 = 认知层可见集）",
                      start = SensorCognitionAcceptanceRunner.CheckViewReconciliation,
                      busy = () => false },
            new Seg { label = "④ [E3]/[N6] EW 浅层：干扰抬阈值 / 烧穿 / 干扰机自暴露 / 开火揭示",
                      start = SensorCognitionAcceptanceRunner.CheckJammingAndReveal,
                      busy = () => false },
            new Seg { label = "⑤ [E5] 假目标：依赖对方开机 / 识破 = 深层提示 / 标记单调 / 生命期",
                      start = SensorCognitionAcceptanceRunner.CheckDecoys,
                      busy = () => false },
        };

        private const int MaxWaitSteps = 3000;    // 保险丝：单段最多等这么多物理步（≈100 分钟游戏时间）
        private static bool running;
        private static int index, waitSteps;

        public static bool Running => running;

        public static void Run()
        {
            if (running) { Log.Message("[EW 总控] 已在跑 ⇒ 忽略本次"); return; }
            if (SensorProjectionAcceptanceRunner.Running || SensorCognitionAcceptanceRunner.Running
                || SwarmAttackPerfRunner.Running || SwarmScaleAcceptanceRunner.Running)
            {
                Log.Warning("[EW 总控] 有其它验收场景在跑 ⇒ 等它跑完再点（会互相污染读数）");
                return;
            }
            PflTestKit.SnapshotGroups();     // 总控自己的清场口径：每段结束只销毁"本总控开始之后新建的"
            running = true;
            index = 0;
            waitSteps = 0;
            harvest.Clear();
            Log.Message(string.Format("[EW 总控] 开始：依次跑 {0} 段（①投影/能力面 → ②认知层 → ③视野对账 → ④干扰与揭示 → ⑤假目标），每段跑完自动清场，最后给合并结论。",
                Segments.Length));
            Segments[0].start();
        }

        /// <summary>物理步驱动（空闲时零开销）。</summary>
        public static void Tick()
        {
            if (!running) return;
            var cur = Segments[index];
            if (cur.busy())
            {
                if (++waitSteps > MaxWaitSteps)
                {
                    harvest.Add(string.Format("✗ {0}：等超时（{1} 步）⇒ 中止本段", cur.label, MaxWaitSteps));
                    Advance();
                }
                return;
            }
            Harvest();
            Advance();
        }

        private static void Advance()
        {
            PflTestKit.CleanupSince();       // 清掉本段铺的场（只销毁总控开始之后新建的）
            index++;
            waitSteps = 0;
            if (index >= Segments.Length) { Finish(); return; }
            Segments[index].start();
        }

        /// <summary>收割刚跑完那段的结算（从 `PflTestKit.Cases` 读最后一个用例）。</summary>
        private static void Harvest()
        {
            if (PflTestKit.Cases.Count == 0) { harvest.Add("✗ 没有任何用例结算行"); totalBad++; return; }
            var c = PflTestKit.Cases[PflTestKit.Cases.Count - 1];
            int n = 0, bad = 0, unchk = 0;
            foreach (var ch in c.checks)
            {
                n++;
                if (ch.ok) continue;
                bool hard = ch.kind == PflTestKit.Kind.Invariant || ch.kind == PflTestKit.Kind.Value;
                if (hard) bad++; else unchk++;
            }
            totalChecks += n; totalBad += bad; totalUnchecked += unchk;
            harvest.Add(string.Format("{0} ⇒ {1}｜判据 {2} 条｜未过 {3}｜UNCHECKED {4}",
                Segments[index].label, c.state, n, bad, unchk));
        }

        private static readonly List<string> harvest = new List<string>();
        private static long totalChecks, totalBad, totalUnchecked;

        private static void Finish()
        {
            running = false;
            var sb = new StringBuilder("[EW 总控·结论] 五段跑完（每段已自动清场）：");
            for (int i = 0; i < harvest.Count; i++) sb.Append("\n  ").Append(harvest[i]);
            sb.Append(string.Format("\n  ⇒ **合计判据 {0} 条｜未过 {1} 条｜UNCHECKED {2} 条**（未过里的 UNCHECKED = 登记的手动项，不是失败）",
                totalChecks, totalBad, totalUnchecked));
            sb.Append("\n  注：①的「存档往返」是手动项 ⇒ 请用单件按钮「传感器/雷达投影验收」跑一遍并做存档往返。");
            Log.Message(sb.ToString());
        }
    }
}
