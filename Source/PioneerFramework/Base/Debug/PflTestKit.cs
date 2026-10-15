using System;
using System.Collections.Generic;
using System.Text;
using PioneerFramework.Combat;   // CombatManager 的详细日志开关
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **统一测试判定框架**（S1，2026-09-23）—— 把 9 个各写一份的游戏内跑批器收成**一套判定与记账**。
    ///
    /// ## 它解决什么（2026-09-23 测试体检的读数）
    /// · **四套判定词表并存**（`PASS/FAIL/未覆盖/PARTIAL` ｜ `PASS/FAIL/UNCHECKED` ｜ `PASS/FAIL/SKIP` ｜
    ///   `通过/失败`）⇒ 统一为 <see cref="Kind"/>（判据种类） + <see cref="Case"/>（用例状态）；
    /// · **13+ 处场景把 `DebugDetailed*` 打开却不关** ⇒ <see cref="SwitchesScope"/> 收口（进入前保存、结论时恢复）；
    /// · **7 个跑批器把自造资产留在场上** ⇒ 用 <see cref="SnapshotGroups"/> + <see cref="CleanupSince"/> 收口
    ///   （口径照抄 `PerfAcceptanceRunner.CleanupSinceLastStageEntry`：**只销毁本次新建的组**）；
    /// · **三个跑批器会"永久静默"**（等一个可能永不到来的条件、无超时）⇒ `Case.timeoutTicks` + <see cref="Tick"/> + <see cref="Abort"/>。
    ///
    /// ## 纪律（本库既有，写进类型本身）
    /// · **0 样本 / 没有可断言的形式 ⇒ `UNCHECKED`，绝不折算成 PASS**
    ///   （最佳范式 = `AdapterAcceptanceRunner.cs:27,341`："这不是通过"）；
    /// · **硬指标（<see cref="Kind.Invariant"/>）破坏 ⇒ FAIL**；跑到但没拿到证据 ⇒ `UNCOVERED`；
    /// · 结论块**固定三问**：硬指标全 0？值判据全过？覆盖 x/y（含 UNCHECKED 清单）。
    /// </summary>
    public static class PflTestKit
    {
        /// <summary>判据种类（决定"坏了算什么"）。</summary>
        public enum Kind
        {
            /// <summary>硬指标 / 不变式：**破坏即 FAIL**（"影子比对不一致=0""注册表泄漏=0"这一类）。</summary>
            Invariant = 0,
            /// <summary>值判据：落进登记区间才算过（"弹头 |a| 应 0.2–5× 主导项"这一类）。</summary>
            Value = 1,
            /// <summary>覆盖度证据：只证明"这条路径真跑到了"，不判对错（"证据计数增量 &gt; 0"这一类）。</summary>
            Coverage = 2,
            /// <summary>负向契约：**拒绝也必须带理由**（"无意图 ⇒ 不重解且给理由"这一类）。</summary>
            Contract = 3,
        }

        /// <summary>用例状态。</summary>
        public enum State
        {
            /// <summary>全部判据通过。</summary>
            Pass = 0,
            /// <summary>有判据失败（硬指标或值判据）。</summary>
            Fail = 1,
            /// <summary>跑到了，但**没拿到证据**（超时/未观测到）。</summary>
            Uncovered = 2,
            /// <summary>**无法断言**（0 样本 / 没有可断言的形式）——**这不是通过**。</summary>
            Unchecked = 3,
            /// <summary>本次未运行（用户未勾选 / 前置不满足）。</summary>
            Skipped = 4,
        }

        /// <summary>一条判据。</summary>
        public readonly struct CheckResult
        {
            public readonly Kind kind;
            public readonly bool ok;
            public readonly string label;
            public readonly string? detail;
            public CheckResult(Kind kind, bool ok, string label, string? detail)
            { this.kind = kind; this.ok = ok; this.label = label; this.detail = detail; }
        }

        /// <summary>一个用例（= 一件可判定的事）。</summary>
        public sealed class Case
        {
            public readonly string name;
            public readonly string category;
            /// <summary>0 = 同步即时段（无超时）；&gt;0 = 异步段，超过这么多真实 tick 未结束 ⇒ UNCOVERED。</summary>
            public readonly int timeoutTicks;
            public readonly List<CheckResult> checks = new();
            public int startedTick;
            public bool finished;
            public string? note;
            public State state = State.Unchecked;

            public Case(string name, string category, int timeoutTicks)
            { this.name = name; this.category = category; this.timeoutTicks = timeoutTicks; }

            public bool HasFailure
            {
                get
                {
                    for (int i = 0; i < checks.Count; i++)
                        if (!checks[i].ok && (checks[i].kind == Kind.Invariant || checks[i].kind == Kind.Value))
                            return true;
                    return false;
                }
            }
        }

        // ── 登记表（窗口/结论块读它；不再各 runner 各持一份静态量）──
        public static readonly List<Case> Cases = new();
        private static readonly List<int> snapshotGroups = new();
        private static Case? current;

        /// <summary>本次会话累计：跑了多少 / 通过 / 失败 / 未覆盖 / 无法断言 / 跳过。</summary>
        public static int Total, Passed, Failed, Uncovered, UncheckedCount, Skipped;

        public static void Reset()
        {
            Cases.Clear();
            snapshotGroups.Clear();
            current = null;
            Total = Passed = Failed = Uncovered = UncheckedCount = Skipped = 0;
        }

        /// <summary>开一个用例。`timeoutTicks &gt; 0` ⇒ 异步段（由 <see cref="Tick"/> 判超时）。
        ///
        /// ⚠ **同一时刻只能有一个未结算的用例**（登记表只有 `current` 一个槽位）。异步段开着的时候再
        /// `Begin` 别的用例，异步段落地时会把判据写进**那个别人的用例**并把它提前结算（静默串味）。
        /// 所以：**异步段要最后登记**；异步段的驱动方还要用 <see cref="IsCurrent"/> 确认"现在是它"。
        /// 顶掉未结算用例时会打一行警告（不静默）。</summary>
        public static Case Begin(string name, string? category = null, int timeoutTicks = 0)
        {
            if (current != null && !current.finished)
            {
                Log.Warning($"[测试框架] 用例「{current.name}」还没结算，就被新用例「{name}」顶掉了 ⇒ 它永远不会结算（异步段只能用最后一个登记的；见 Begin 注释）");
            }
            current = new Case(name, category ?? "", timeoutTicks);
            current.startedTick = Find.TickManager?.TicksGame ?? 0;
            lastHeartbeatTicks = 0;   // 心跳节拍跟着用例走（换人即归零）
            Cases.Add(current);
            return current;
        }

        /// <summary>还有未结算的用例吗（异步**排队**用：等它结完再开下一个，避免抢那唯一的槽位）。</summary>
        public static bool AnyPending => current != null && !current.finished;

        /// <summary>现在开着的用例是「<paramref name="name"/>」吗（异步段的驱动方在写判据前必须先问这一句）。</summary>
        public static bool IsCurrent(string name)
            => current != null && !current.finished && string.Equals(current.name, name, StringComparison.Ordinal);

        /// <summary>结一个用例：状态由它的判据决定（**没有任何判据 ⇒ UNCHECKED，不是 PASS**）。</summary>
        public static void End()
        {
            if (current == null) return;
            if (current.finished) { current = null; return; }
            current.finished = true;

            if (current.HasFailure) current.state = State.Fail;
            else if (current.checks.Count == 0) current.state = State.Unchecked;
            else
            {
                // 全是负向/覆盖类判据且都过 ⇒ 也说清"断言到哪一层"
                bool onlyCoverage = true;
                for (int i = 0; i < current.checks.Count; i++)
                {
                    if (current.checks[i].kind == Kind.Invariant || current.checks[i].kind == Kind.Value)
                    { onlyCoverage = false; break; }
                }
                current.state = onlyCoverage ? State.Uncovered : State.Pass;
            }

            switch (current.state)
            {
                case State.Pass: Passed++; break;
                case State.Fail: Failed++; break;
                case State.Uncovered: Uncovered++; break;
                case State.Unchecked: UncheckedCount++; break;
                default: Skipped++; break;
            }
            Total++;
            Announce(current);
            current = null;
        }

        /// <summary>
        /// **每个用例在结算的当下就打一行**（2026-09-23 补）。
        ///
        /// 为什么必须有：<see cref="PrintVerdict"/> 只在跑批结束时打一次，而**异步用例**（带 `timeoutTicks`
        /// 的那些）是在**结论块之后**才落地的 ⇒ 只靠结论块，它们的结局永远没人报（"永久静默"的另一种形态）。
        /// 一行里带：判定词 + 判据条数 + **前两条未过的明细**（异步用例没有第二次机会解释自己）。
        /// </summary>
        private static void Announce(Case c)
        {
            string tag;
            switch (c.state)
            {
                case State.Pass: tag = "PASS"; break;
                case State.Fail: tag = "**FAIL**"; break;
                case State.Uncovered: tag = "UNCOVERED"; break;
                case State.Skipped: tag = "SKIP"; break;
                default: tag = "UNCHECKED"; break;
            }

            var sb = new StringBuilder();
            int bad = 0;
            var reads = new StringBuilder();   // 通过判据的读数——"读数必须无条件"：
                                               // 只打失败明细的后果就是"PASS 了但一个数都没留下"，无从跨版本对照。
                                               // 预算按**字符**不按条数：旧值 240 撞上长标签时会把后半读数整段挤掉
                                               // （2026-10-01 [V7] 指向验收：9 条读数只打出前 4 条）。1200 约容 10–15 条。
            const int readsBudget = 1200;
            for (int i = 0; i < c.checks.Count; i++)
            {
                var ch = c.checks[i];
                if (ch.ok)
                {
                    if (reads.Length < readsBudget && !string.IsNullOrEmpty(ch.detail))
                        reads.Append(reads.Length == 0 ? "" : "｜").Append(ch.label).Append('=').Append(ch.detail);
                    continue;
                }
                bad++;
                if (bad > 2) continue;   // 一行里最多列两条明细
                sb.Append('｜').Append(ch.label);
                if (!string.IsNullOrEmpty(ch.detail)) sb.Append('：').Append(ch.detail);
            }
            Log.Message($"[测试框架] 用例「{c.name}」结算：{tag}（判据 {c.checks.Count} 条，未过 {bad} 条）{(string.IsNullOrEmpty(c.note) ? "" : "｜" + c.note)}{sb.ToString()}{(reads.Length > 0 ? "｜读数：" + reads : "")}");
        }

        /// <summary>记一条判据（用例内）。</summary>
        public static void Check(Kind kind, bool ok, string label, string? detail = null)
        {
            if (current == null)
            {
                Log.Warning($"[测试框架] 判据「{label}」落在没有用例的地方（忘了 Begin？）");
                return;
            }
            current.checks.Add(new CheckResult(kind, ok, label, detail));
        }

        /// <summary>
        /// **登记一个"验不了"的子项**（**不改用例状态**）：用于"这条判据今天无写入者/无样本，
        /// 而用例其余部分是真判据"——与 <see cref="Unchecked"/> 的区别是后者把**整个用例**判为 UNCHECKED。
        /// 两者都会进结论块的 "UNCHECKED 清单"（**不许用沉默冒充没问题**）。
        /// </summary>
        public static void CheckUnchecked(string label, string why)
        {
            if (current == null) return;
            current.checks.Add(new CheckResult(Kind.Coverage, false, "UNCHECKED（登记）" + label, why));
        }

        /// <summary>**无法断言**（0 样本 / 没有可断言的形式）⇒ 记一条、并把用例定为 UNCHECKED。这不是通过。</summary>
        public static void Unchecked(string label, string why)
        {
            if (current == null) return;
            current.checks.Add(new CheckResult(Kind.Contract, false, label, "UNCHECKED：" + why));
            current.note = "UNCHECKED：" + why;
            current.finished = true;
            current.state = State.Unchecked;
            UncheckedCount++; Total++;
            Announce(current);
            current = null;
        }

        /// <summary>未覆盖（跑到但没拿到证据）。</summary>
        public static void UncoveredCase(string label, string why)
        {
            if (current == null) return;
            current.checks.Add(new CheckResult(Kind.Coverage, false, label, why));
            current.finished = true;
            current.state = State.Uncovered;
            Uncovered++; Total++;
            Announce(current);
            current = null;
        }

        /// <summary>本次未运行（未勾选 / 前置不满足）。</summary>
        public static void Skip(string label, string why)
        {
            var c = Begin(label);
            c.note = "SKIP：" + why;
            c.finished = true;
            c.state = State.Skipped;
            Skipped++; Total++;
            Announce(c);
            current = null;
        }

        /// <summary>由驱动方（`SpaceGameComp.GameComponentTick`）每真实 tick 调用：异步段超时判定。</summary>
        public static void Tick()
        {
            if (current == null || current.finished || current.timeoutTicks <= 0) return;
            int now = Find.TickManager?.TicksGame ?? 0;

            // **心跳**（2026-10-08 加固；每 600 真实 tick 一行）：异步用例在跑时定期留痕 ⇒
            //   "框架还在 tick 吗 / 本用例等了多久 / 离超时还有多少" **直接可读**。
            //   为什么必须有：2026-10-08 实测撞到过一次"用例冻结"（既无结算、也无超时），当时日志里
            //   连"框架到底还在不在 tick"都判不出来，只能靠反推。有心跳就不必再反推。
            if (now - current.startedTick - lastHeartbeatTicks >= 600)
            {
                lastHeartbeatTicks = now - current.startedTick;
                Log.Message($"[测试框架·心跳] 异步用例「{current.name}」仍在跑：已 {now - current.startedTick} tick / 超时 {current.timeoutTicks}");
            }

            if (now - current.startedTick > current.timeoutTicks)
            {
                current.checks.Add(new CheckResult(Kind.Coverage, false, current.name,
                    $"超时（{current.timeoutTicks} tick 内没拿到证据）"));
                current.finished = true;
                current.state = State.Uncovered;
                Uncovered++; Total++;
                Announce(current);
                current = null;
                lastHeartbeatTicks = 0;
            }
        }

        /// <summary>心跳节拍（相对 `current.startedTick`；用例换人时由 `Begin` 归零）。</summary>
        private static int lastHeartbeatTicks;

        /// <summary>中止：当前用例记 UNCOVERED，已完成的结论不受影响。</summary>
        public static void Abort()
        {
            if (current != null) UncoveredCase(current.name, "被中止");
        }

        /// <summary>给当前用例附一行备注（进结论块）。</summary>
        public static void Note(string text) { if (current != null) current.note = text; }

        /// <summary>清场快照：记下"开始前就存在的组"，收工后按它销毁新增的（**不动玩家资产**）。</summary>
        public static void SnapshotGroups()
        {
            snapshotGroups.Clear();
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return;
            foreach (var g in sg.GetAllGroups()) snapshotGroups.Add(g.GroupId);
        }

        /// <summary>按 <see cref="SnapshotGroups"/> 的快照清场（口径 = `PerfAcceptanceRunner.CleanupSinceLastStageEntry`）。</summary>
        public static int CleanupSince()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return 0;
            var doomed = new List<int>();
            foreach (var g in sg.GetAllGroups())
                if (!snapshotGroups.Contains(g.GroupId)) doomed.Add(g.GroupId);
            for (int i = 0; i < doomed.Count; i++) sg.DestroyGroup(doomed[i]);
            return doomed.Count;
        }

        /// <summary>总判定词（与用例状态词表一致）。</summary>
        public static string Verdict()
        {
            if (Failed > 0) return "FAIL";
            if (Total == 0) return "UNCHECKED";
            if (Uncovered > 0) return "PARTIAL";
            if (UncheckedCount > 0) return "PASS（有 UNCHECKED 项，见清单）";
            return "PASS";
        }

        public static bool HasFailure => Failed > 0;

        /// <summary>固定结论块：**硬指标全 0？值判据全过？覆盖 x/y？UNCHECKED 清单**。</summary>
        public static void PrintVerdict(string header)
        {
            var sb = new StringBuilder();
            sb.Append("\n[测试框架] ═══════ ").Append(header).Append(" ═══════\n");
            sb.Append("[测试框架] 用例：总 ").Append(Total).Append("｜通过 ").Append(Passed)
              .Append("｜失败 ").Append(Failed).Append("｜未覆盖 ").Append(Uncovered)
              .Append("｜**无法断言（UNCHECKED）** ").Append(UncheckedCount).Append("｜跳过 ").Append(Skipped).Append('\n');

            int invTotal = 0, invBad = 0, valTotal = 0, valBad = 0;
            for (int i = 0; i < Cases.Count; i++)
            {
                var c = Cases[i];
                for (int j = 0; j < c.checks.Count; j++)
                {
                    var ch = c.checks[j];
                    if (ch.kind == Kind.Invariant) { invTotal++; if (!ch.ok) invBad++; }
                    else if (ch.kind == Kind.Value) { valTotal++; if (!ch.ok) valBad++; }
                }
            }
            sb.Append(System.Globalization.CultureInfo.CurrentCulture, $"[测试框架] 硬指标（不变式）：{invTotal} 条，破坏 {invBad} 条{(invBad == 0 ? " ✓" : " ✗")}\n");
            sb.Append(System.Globalization.CultureInfo.CurrentCulture, $"[测试框架] 值判据：{valTotal} 条，出界 {valBad} 条{(valBad == 0 ? " ✓" : " ✗")}\n");
            sb.Append(System.Globalization.CultureInfo.CurrentCulture, $"[测试框架] 覆盖度：通过 {Passed + UncheckedCount}/{Total}（未覆盖 {Uncovered}）\n");
            sb.Append("[测试框架] 覆盖 x/y 明细（未通过者）：\n");

            bool anyBad = false;
            for (int i = 0; i < Cases.Count; i++)
            {
                var c = Cases[i];
                if (c.state == State.Pass) continue;
                anyBad = true;
                // ⚠ **还没结算的异步用例**要单独说清（它的 `state` 还是默认的 UNCHECKED，但那是"在等"，
                //   不是"验不了"）—— 混在一起会让人以为这一条今天做了结论。它结算时会自己打一行「用例…结算」。
                sb.Append("  · [").Append(c.finished ? c.state.ToString() : "WAITING（异步段未结算）")
                  .Append("] ").Append(c.name);
                if (!string.IsNullOrEmpty(c.note)) sb.Append('｜').Append(c.note);
                sb.Append('\n');
                for (int j = 0; j < c.checks.Count; j++)
                {
                    var ch = c.checks[j];
                    if (ch.ok) continue;
                    sb.Append("      - ").Append(ch.kind).Append('：').Append(ch.label);
                    if (!string.IsNullOrEmpty(ch.detail)) sb.Append('（').Append(ch.detail).Append('）');
                    sb.Append('\n');
                }
            }
            if (!anyBad) sb.Append("  （无）\n");

            // ★ 登记的"验不了"子项要**单独列出来**：否则它们只会出现在失败用例的明细里，
            //   而"用例通过 + 某个子项验不了"是最容易被读成"全验过了"的组合（本库的沉默陷阱）。
            sb.Append("[测试框架] **登记的 UNCHECKED 子项**（今天验不了，别当已验）：\n");
            bool anyUncheckedItem = false;
            for (int i = 0; i < Cases.Count; i++)
            {
                var c = Cases[i];
                for (int j = 0; j < c.checks.Count; j++)
                {
                    var ch = c.checks[j];
                    if (ch.label != null && ch.label.StartsWith("UNCHECKED（登记）", System.StringComparison.Ordinal))
                    {
                        anyUncheckedItem = true;
                        sb.Append("  · ").Append(c.name).Append('｜').Append(ch.label)
                          .Append('：').Append(ch.detail).Append('\n');
                    }
                }
            }
            if (!anyUncheckedItem) sb.Append("  （无）\n");

            sb.Append("[测试框架] 总判定：").Append(Verdict()).Append('\n');
            Log.Message(sb.ToString());
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  共用设施①：逐 tick 详细日志开关的**保存/恢复**
        //  背景：13+ 处场景把 `DebugDetailed*` 打开却不关 ⇒ 整轮日志被刷爆
        //  （`PerfAcceptanceRunner` 曾在验收器里手工补过一份，这里收口）。
        //  2026-10-15 反射清理：原为 (Type, fieldName) 反射表——9 个字段全是 `public static` ⇒
        //  改为**强类型访问器表**：字段改名/删除会当场编译错，而不是运行期静默失效。
        // ══════════════════════════════════════════════════════════════════════════
        private static readonly (Func<object?> Get, Action<object?> Set)[] DetailedSwitches =
        [
            (() => CombatManager.DebugDetailedCombat, v => CombatManager.DebugDetailedCombat = (bool)v!),
            (() => CombatManager.DebugDetailedCollision, v => CombatManager.DebugDetailedCollision = (bool)v!),
            (() => CombatManager.DebugProjectileProbeInterval, v => CombatManager.DebugProjectileProbeInterval = (int)v!),
            (() => SpaceGameComp.DebugDetailedSteering, v => SpaceGameComp.DebugDetailedSteering = (bool)v!),
            (() => SpaceGameComp.DebugDetailedThrust, v => SpaceGameComp.DebugDetailedThrust = (bool)v!),
            (() => AutonomousVehicleManager.DebugDetailedGuidance, v => AutonomousVehicleManager.DebugDetailedGuidance = (bool)v!),
            (() => BoardingManager.DebugDetailedBoarding, v => BoardingManager.DebugDetailedBoarding = (bool)v!),
            (() => RocketLaunchController.DebugDetailedLaunch, v => RocketLaunchController.DebugDetailedLaunch = (bool)v!),
            (() => ConstellationGameComp.DebugDetailedConstellation, v => ConstellationGameComp.DebugDetailedConstellation = (bool)v!),
        ];

        /// <summary>
        /// 作用域内可随意开关详细日志；**离开作用域时全部还原**。
        /// 用法：`using (PflTestKit.SwitchesScope()) { ... }`。
        /// </summary>
        public static IDisposable SwitchesScope() => new SwitchesRestorer();

        private sealed class SwitchesRestorer : IDisposable
        {
            private readonly object?[] saved = new object?[DetailedSwitches.Length];

            public SwitchesRestorer()
            {
                for (int i = 0; i < DetailedSwitches.Length; i++)
                    saved[i] = DetailedSwitches[i].Get();
            }

            public void Dispose()
            {
                for (int i = 0; i < DetailedSwitches.Length; i++)
                    DetailedSwitches[i].Set(saved[i]);
            }
        }
    }
}
