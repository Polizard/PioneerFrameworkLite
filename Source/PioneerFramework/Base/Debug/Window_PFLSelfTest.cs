using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 「PFL 自检进度」窗口（2026-09-14）：一键验收的实时进度与读数面板。
    ///
    /// 为什么需要它：验收是"电池 + 7 个阶段"的长流程（1x 下每阶段上限 90 游戏秒），
    /// 没有它只能翻日志猜"跑到哪了、为什么卡住"。窗口直接读 `PerfAcceptanceRunner` 的状态与
    /// `PerfProbe` 的读数（不从日志字符串解析），并给每个阶段标注**判据**与**当前读数**，
    /// 这样"卡在哪个阶段、缺哪个证据、值对不对"一眼可见。
    ///
    /// 布局：总进度条 → 电池行 → 7 个阶段行（含阶段内进度条）→ 实时读数 → 值判据失败清单 → 按钮。
    /// 全窗口只读（除按钮），不暂停游戏、不吃输入。
    /// </summary>
    public class Window_PFLSelfTest : Window
    {
        private Vector2 detailScroll;
        private const float WindowWidth = 720f;
        /// <summary>高度要扣掉 Window.Margin(18) 的上下各一份：布局约需 508 → 取 580 留余量。</summary>
        private const float WindowHeight = 640f;
        private const float RowHeight = 24f;

        public Window_PFLSelfTest()
        {
            forcePause = false;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
            absorbInputAroundWindow = false;
            resizeable = false;
        }

        public override Vector2 InitialSize => new(WindowWidth, WindowHeight);

        /// <summary>已开着就不重复开。</summary>
        public static void OpenIfClosed()
        {
            if (Find.WindowStack == null) return;
            var windows = Find.WindowStack.Windows;
            for (int i = 0; i < windows.Count; i++)
                if (windows[i] is Window_PFLSelfTest) return;
            Find.WindowStack.Add(new Window_PFLSelfTest());
        }

        private static string Glyph(PerfAcceptanceRunner.StageOutcome o)
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    => o switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    {
            PerfAcceptanceRunner.StageOutcome.Passed => "✓",
            PerfAcceptanceRunner.StageOutcome.Failed => "✗",
            PerfAcceptanceRunner.StageOutcome.Uncovered => "○",
            PerfAcceptanceRunner.StageOutcome.Running => "▶",
            PerfAcceptanceRunner.StageOutcome.Skipped => "–",
            _ => "·",
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    };

        private static Color GlyphColor(PerfAcceptanceRunner.StageOutcome o)
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          => o switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          {
            PerfAcceptanceRunner.StageOutcome.Passed => new Color(0.45f, 0.95f, 0.45f),
            PerfAcceptanceRunner.StageOutcome.Failed => new Color(1f, 0.4f, 0.4f),
            PerfAcceptanceRunner.StageOutcome.Uncovered => new Color(1f, 0.85f, 0.35f),
            PerfAcceptanceRunner.StageOutcome.Running => new Color(0.55f, 0.8f, 1f),
            PerfAcceptanceRunner.StageOutcome.Skipped => new Color(0.5f, 0.5f, 0.5f),
            _ => Color.gray,
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          };

        /// <summary>
        /// 本 DLL 的构建时间（取程序集文件最后写入时刻），缓存一次。
        /// 用途：**自证游戏加载的是哪一份 DLL**——"找不到新加的按钮"最常见的根因是加载了旧 DLL。
        /// </summary>
        private static string? cachedDllTime;
        private static string DllWriteTime()
        {
            if (cachedDllTime != null) return cachedDllTime;
            try
            {
                string loc = typeof(Window_PFLSelfTest).Assembly.Location;
                cachedDllTime = string.IsNullOrEmpty(loc)
                    ? "未知（内存加载）"
                    : $"{System.IO.File.GetLastWriteTime(loc):yyyy-MM-dd HH:mm}";
            }
            catch
            {
                cachedDllTime = "未知";
            }
            return cachedDllTime;
        }

        /// <summary>该阶段"拿什么判过"——写在这里，省得再翻计划文档。</summary>
        private static string CriterionAt(int index)
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                => index switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                {
            0 => "⑮弹头 AoE 走细网格（导弹命中必然触发）",
            1 => "①聚合器缓存命中（多实体组群级燃烧）",
            2 => "⑤组件缓存命中 + ⑯逆四元数命中（炮塔真开火）",
            3 => "接触解算 > 0（碰撞链跑到 SeparatePositions）",
            4 => "③组级燃烧切片 scratch 复用检查",
            5 => "㉑子天体域差分采样；比值应落潮汐带 0.1%–0.6%",
            6 => "㉒弹头建段；|a| 应 ≈ 主导项 μ/r²（0.2–5×）",
            7 => "㉒段重建 > 0（段内插值飞行）+ ㉔弯曲量 实际/½|a|t² ≈ 1×",
            _ => "",
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                };

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            bool running = PerfAcceptanceRunner.Running;
            float y = 0f;

            // ── 标题 + 总进度条 ──
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, 200f, 28f), "PFL 自检进度");
            // 用法提示：放在标题右侧（Tiny），**零垂直成本**——可发现性是本面板的历史痛点
            // （"找不到按钮"两次的同一个根因：面板不自解释，且按钮同质、语义混杂）。
            Text.Font = GameFont.Tiny;
            Color hintPrev = GUI.color;
            GUI.color = new Color(0.68f, 0.68f, 0.68f);
            Widgets.Label(new Rect(206f, y + 9f, inRect.width - 206f, 18f),
                "勾选 → 点「运行一键验收」→ 看下方明细与日志 [验收] 段；勾选改动下次运行生效");
            GUI.color = hintPrev;
            Text.Font = GameFont.Small;
            y += 30f;

            string state = running
                ? $"运行中 · 第 {PerfAcceptanceRunner.CurrentStageNumber}/{PerfAcceptanceRunner.StageTotal} 阶段：{PerfAcceptanceRunner.StageLabelAt(PerfAcceptanceRunner.CurrentStageNumber - 1)}"
                : PerfAcceptanceRunner.Verdict != null
                    ? "已出结论（见下方「值判据」与日志 [验收] 段）"
                    : "未运行";
            // 状态着色：运行中=蓝 / 通过=绿 / 失败=红 / 未运行=灰 —— 让"现在什么状态"一眼可读
            Color stateColor = running
                ? new Color(0.55f, 0.8f, 1f)
                : PerfAcceptanceRunner.Verdict == null
                    ? Color.gray
                    : PerfAcceptanceRunner.VerdictIsFail
                        ? new Color(1f, 0.5f, 0.5f)
                        : new Color(0.5f, 1f, 0.5f);
            Color statePrev = GUI.color;
            GUI.color = stateColor;
            Widgets.Label(new Rect(0, y, inRect.width - 150f, 22f), state);
            GUI.color = statePrev;
            Widgets.Label(new Rect(inRect.width - 150f, y, 150f, 22f),
                $"勾选：电池{(PerfAcceptanceRunner.IsBatteryEnabled ? "开" : "关")} 阶段 {PerfAcceptanceRunner.SelectedStageCount}/{PerfAcceptanceRunner.StageTotal}");
            y += 24f;

            float overall = PerfAcceptanceRunner.OverallProgress01;
            Widgets.FillableBarLabeled(new Rect(0, y, inRect.width, 22f), overall, 60,
                $"{overall * 100f:F0}%");
            y += 30f;

            // ── 电池行 ──
            bool batteryRun = PerfAcceptanceRunner.BatterySummary != "未运行";
            int bPass = PerfAcceptanceRunner.BatteryPassedCount;
            int bFail = PerfAcceptanceRunner.BatteryFailedCount;
            var batteryOutcome = !batteryRun
                ? PerfAcceptanceRunner.StageOutcome.Pending
                : (bFail == 0 ? PerfAcceptanceRunner.StageOutcome.Passed : PerfAcceptanceRunner.StageOutcome.Failed);
            DrawRow(new Rect(0, y, inRect.width, RowHeight), batteryOutcome,
                "即时电池（引力 34 项 + 机制在册）",
                batteryRun ? $"通过 {bPass} / 失败 {bFail}" : "未运行",
                true, PerfAcceptanceRunner.IsBatteryEnabled, running,
                on => PerfAcceptanceRunner.SetBatteryEnabled(on));
            y += RowHeight + 2f;

            // ── 7 个阶段行 ──
            for (int i = 0; i < PerfAcceptanceRunner.StageTotal; i++)
            {
                var outcome = PerfAcceptanceRunner.OutcomeAt(i);
                string right;
                if (outcome == PerfAcceptanceRunner.StageOutcome.Running)
                {
                    right = $"{PerfAcceptanceRunner.CurrentStageProgress01 * 100f:F0}%  t+{PerfAcceptanceRunner.CurrentStageElapsed}/{PerfAcceptanceRunner.StageTimeout}";
                }
                else if (outcome == PerfAcceptanceRunner.StageOutcome.Pending
                    || outcome == PerfAcceptanceRunner.StageOutcome.Skipped)
                {
                    right = CriterionAt(i);
                }
                else
                {
                    right = PerfAcceptanceRunner.NoteAt(i) ?? CriterionAt(i);
                }

                var rowRect = new Rect(0, y, inRect.width, RowHeight);
                bool enabled = PerfAcceptanceRunner.IsStageEnabled(i);
                DrawRow(rowRect, outcome, PerfAcceptanceRunner.StageLabelAt(i), right,
                    true, enabled, running, on => PerfAcceptanceRunner.SetStageEnabled(i, on));

                // 进行中的阶段在行内叠加一条细进度条（视觉上"还在走"）
                if (outcome == PerfAcceptanceRunner.StageOutcome.Running)
                {
                    var bar = new Rect(rowRect.x, rowRect.yMax - 4f, rowRect.width * 0.6f, 3f);
                    Widgets.FillableBar(bar, PerfAcceptanceRunner.CurrentStageProgress01, Widgets.BarFullTexHor, null, false);
                }
                y += RowHeight + 2f;
            }

            // ── 底部区从窗口底反推：即使上面的行高算错，按钮也不会被切掉 ──
            float buttonsY = inRect.height - 30f;
            float presetRowY = buttonsY - 34f;              // 选择预设行（非运行时显示）
            float verdictY = presetRowY - 28f;

            // ── 实时读数 + 失败清单（滚动，高度自适应）──
            float detailTop = y + 4f;
            float detailH = Mathf.Max(70f, verdictY - 6f - detailTop);
            var detailRect = new Rect(0, detailTop, inRect.width, detailH);
            Widgets.DrawMenuSection(detailRect);
            var inner = detailRect.ContractedBy(6f);
            var sb = new StringBuilder();

            // DLL 构建时间：**自证"游戏加载的是哪一份 DLL"**。此前两次"找不到按钮"的根因都是
            // 游戏加载的是旧 DLL（`1.6/Assemblies/` 只在 Release 构建时更新，Debug 出到 bin/Debug）。
            // 若这里显示的不是你刚构建的时刻 ⇒ 先跑 tools/rebuild-dll.ps1 + tools/Deploy-LiteToGame.ps1。
            sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"── DLL 构建时间 ── {DllWriteTime()}（不符即说明游戏加载的是旧 DLL）");
            sb.AppendLine("");
            sb.AppendLine("── 引力场读数（㉑㉒）──");
            sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"根域影子：检查 {PerfProbe.GravityRootChecked} 次 / 不一致 {PerfProbe.GravityRootMismatch}{(PerfProbe.GravityRootMismatch == 0 ? " ✓" : " ✗（天体枚举或位置累加错）")}");
            if (PerfProbe.GravitySubBodyProbes > 0)
                sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"月球域差分：{PerfProbe.GravitySubBodyProbes} 次 · 最近 {PerfProbe.GravitySubBodyLastRatio:P4}（范围 {PerfProbe.GravitySubBodyMinRatio:P4}–{PerfProbe.GravitySubBodyMaxRatio:P4}）· 差分 {PerfProbe.GravitySubBodyLastDiff:E3} / 局部 {PerfProbe.GravitySubBodyLastLocal:F3} m/s²");
            else
                sb.AppendLine("月球域差分：未采样（0=旧行为错 / 0.1%–0.6%=潮汐对 / ~7%=全量和错）");
            if (PerfProbe.WarheadSegments > 0)
                sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"弹头引力：建段 {PerfProbe.WarheadSegments} 次 · |a|={PerfProbe.WarheadAccel:F3} m/s²（主导项 {PerfProbe.WarheadAccelRatio:F2}×，应 0.2–5×）· 指向行星中心余弦 {PerfProbe.WarheadTowardCenterCos:F3}");
            else
                sb.AppendLine("弹头引力：未采样（旧实现为 ~100× 主导项的伪值）");
            sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"射弹段：重建 {PerfProbe.ProjectileSegments} / 步 {PerfProbe.ProjectileSteps}（按弹按时间判定，全局比只作参考）· 探针不一致总数 {PerfProbe.TotalMismatches}");
            sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"id 复用观察：{PerfProbe.GroupIdReuseObserved} 次（id 分配「扫 max+1」的后果——计划 A5a 的根因，非缺陷）");

            var batteryFails = GravityFieldSelfTest.LastFailures;
            if (batteryFails.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"── 电池失败 {batteryFails.Count} 项 ──");
                for (int i = 0; i < batteryFails.Count; i++) sb.AppendLine("✗ " + batteryFails[i]);
            }

            var fails = PerfAcceptanceRunner.Failures;
            if (fails.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"── 值判据失败 {fails.Count} 项（FAIL）──");
                for (int i = 0; i < fails.Count; i++) sb.AppendLine("✗ " + fails[i]);
            }
            else if (PerfAcceptanceRunner.Verdict != null)
            {
                sb.AppendLine();
                sb.AppendLine("── 值判据：全部通过 ✓ ──");
            }
            if (PerfAcceptanceRunner.Uncovered.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"── 未覆盖 {PerfAcceptanceRunner.Uncovered.Count} 项（场景没跑到该路径，非缺陷）──");
                for (int i = 0; i < PerfAcceptanceRunner.Uncovered.Count; i++)
                    sb.AppendLine("○ " + PerfAcceptanceRunner.Uncovered[i]);
            }

            var text = sb.ToString();
            float textHeight = Text.CalcHeight(text, inner.width);
            var viewRect = new Rect(0, 0, inner.width - 16f, Mathf.Max(textHeight, inner.height));
            Widgets.BeginScrollView(inner, ref detailScroll, viewRect);
            Widgets.Label(new Rect(0, 0, viewRect.width, viewRect.height), text);
            Widgets.EndScrollView();
            y = detailRect.yMax + 6f;

            // ── 结论行 ──
            if (PerfAcceptanceRunner.Verdict != null)
            {
                Color prev = GUI.color;
                GUI.color = PerfAcceptanceRunner.VerdictIsFail
                    ? new Color(1f, 0.5f, 0.5f)
                    : new Color(0.5f, 1f, 0.5f);
                Widgets.Label(new Rect(0, verdictY, inRect.width, 24f), "总判定：" + PerfAcceptanceRunner.Verdict);
                GUI.color = prev;
            }

            // ── 选择预设行（仅在非运行时显示）；y 用上面已算好的 presetRowY（此前在这里又算了一遍 presetY）──
            if (!running)
            {
                float pw = (inRect.width - 8f) / 3f;
                if (Widgets.ButtonText(new Rect(0, presetRowY, pw, 28f), "全选所有测试项"))
                {
                    PerfAcceptanceRunner.SetBatteryEnabled(true);
                    PerfAcceptanceRunner.SetAllStages(true);
                }
                if (Widgets.ButtonText(new Rect(pw + 4f, presetRowY, pw, 28f), "只跑引力相关（电池 + 6/7 + 7/7）"))
                    PerfAcceptanceRunner.SelectGravityOnly();
                if (Widgets.ButtonText(new Rect(2f * (pw + 4f), presetRowY, pw, 28f), "跳过慢阶段（4/7 等）"))
                {
                    PerfAcceptanceRunner.SetStageEnabled(3, false); // 4/7 无 Map 碰撞：等船漂到接触，最慢
                    PerfAcceptanceRunner.SetStageEnabled(0, false); // 1/7 弹群齐射：弹群多、耗时长
                }
            }
            // 勾选项改动在**下一次运行**生效（运行中复选框是灰的）
            // 底部按钮行**按语义分段 + 关闭靠右独立**：
            //   第 1 位 = 运行控制（运行 / 中止，互斥）；第 2–4 位 = 一次性动作（点一下跑一次或打一份日志）；
            //   最右 = 关闭（与前四个拉开空隙，避免与"运行"误点相邻）。
            float y2 = buttonsY;
            const float closeW = 76f;
            const float closeGap = 16f;
            float bw = (inRect.width - closeW - closeGap - 3f * 4f) / 4f;
            if (!running)
            {
                if (Widgets.ButtonText(new Rect(0, y2, bw, 30f), "运行一键验收"))
                {
                    PerfAcceptanceRunner.Run();
                    OpenIfClosed();
                }
            }
            else
            {
                if (Widgets.ButtonText(new Rect(0, y2, bw, 30f), "中止并出结论"))
                    PerfAcceptanceRunner.Abort();
            }
            if (Widgets.ButtonText(new Rect(bw + 4f, y2, bw, 30f), "只跑自检电池"))
                GravityFieldSelfTest.Run();
            if (Widgets.ButtonText(new Rect(2f * (bw + 4f), y2, bw, 30f), "打完整探针汇总"))
                PerfProbe.LogSummaryNow();
            // 可见性快照（[V1]）：与"探针汇总"同类（点一下打一份日志）⇒ 并排。
            // 跨存档检查用它，而不是靠点选群组的面板读数。
            if (Widgets.ButtonText(new Rect(3f * (bw + 4f), y2, bw, 30f), "打印可见性快照"))
                VisibilityProbe.PrintSnapshot();
            if (Widgets.ButtonText(new Rect(inRect.width - closeW, y2, closeW, 30f), "关闭"))
                Close();
        }

        /// <summary>
        /// 一行：勾选框 + 状态字形 + 标签 + 右侧读数。
        /// 勾选框只在非运行时可改（运行中改勾选语义混乱），并**不参与本轮的阶段推进**——
        /// 改完要再点「运行一键验收」才生效。
        /// </summary>
        private static void DrawRow(Rect rect, PerfAcceptanceRunner.StageOutcome outcome, string label, string right,
            bool showCheckbox, bool checkedOn, bool running, System.Action<bool> onToggle)
        {
            if (Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);

            float x = rect.x;
            if (showCheckbox)
            {
                bool box = checkedOn;
                Widgets.Checkbox(x, rect.y + 2f, ref box, 20f, disabled: running);
                if (!running && box != checkedOn) onToggle?.Invoke(box);
                x += 24f;
            }

            Color prev = GUI.color;
            GUI.color = GlyphColor(outcome);
            Widgets.Label(new Rect(x, rect.y, 20f, rect.height), Glyph(outcome));
            GUI.color = prev;
            x += 22f;

            float labelW = rect.width * 0.42f;
            Widgets.Label(new Rect(x, rect.y, labelW, rect.height), label);
            x += labelW;

            prev = GUI.color;
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            Widgets.Label(new Rect(x + 2f, rect.y, rect.xMax - x - 6f, rect.height), right ?? "");
            GUI.color = prev;
        }
    }
}
