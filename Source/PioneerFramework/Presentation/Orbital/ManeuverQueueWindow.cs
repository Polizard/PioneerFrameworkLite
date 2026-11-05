using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 机动规划队列窗口（重排版）：
    /// - 顶部 Header（Group + 状态）
    /// - burn 执行区：dV 进度条（已消耗/计划）+ 工质消耗 + 「紧急终止当前机动」
    /// - 当前网络区（新机动继承）
    /// - 机动列表：每行「取消此节点及以后机动规划」，逐行 burn 估算
    /// - 底部：清除所有 / 关闭
    /// </summary>
    public class ManeuverQueueWindow : Window
    {
        private readonly int groupId;
        private readonly List<PlannedManeuverSnapshot> queueBuf = new();
        private readonly List<NetworkOptionSnapshot> networkBuf = new();
        private Vector2 scrollPos;

        private const float EntryHeight = 104f;
        private const float ButtonHeight = 34f;
        private const float SectionGap = 12f;
        private const float WindowWidth = 640f;
        private const float WindowHeight = 540f;   // 竖长条，避免接近正方形

        // ── 视觉风格 ──
        private static readonly Color PanelBg = new(0.055f, 0.075f, 0.10f, 0.95f);
        private static readonly Color SectionBg = new(0.09f, 0.12f, 0.16f, 0.92f);
        private static readonly Color SectionBgAlt = new(0.115f, 0.145f, 0.185f, 0.92f);
        private static readonly Color AccentCyan = new(0.25f, 0.9f, 1f, 0.9f);
        private static readonly Color AccentWarn = new(1f, 0.42f, 0.3f, 0.95f);
        private static readonly Color AccentOk = new(0.35f, 1f, 0.55f, 0.95f);
        private static readonly Color TextDim = new(0.60f, 0.66f, 0.72f, 1f);
        private static readonly Color BarBg = new(0.02f, 0.03f, 0.05f, 0.85f);
        private static readonly Color RowBorder = new(0.25f, 0.3f, 0.35f, 0.55f);
        /// <summary>普通按钮的统一灰色（未特别着色按钮）。</summary>
        private static readonly Color GrayButton = new(0.46f, 0.49f, 0.53f, 1f);

        public ManeuverQueueWindow(int targetGroupId)
        {
            groupId = targetGroupId;
            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
        }

        public override Vector2 InitialSize => new(WindowWidth, WindowHeight);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            float y = 0f;
            PhysicsApi.TryGetSpacecraft(groupId, out var snap);

            DrawHeader(inRect, ref y, snap);
            y += SectionGap;

            if (snap.HasActiveBurn)
            {
                DrawBurnSection(inRect, ref y, snap);
                y += SectionGap;
            }

            DrawNetworkSection(inRect, ref y, snap);
            y += SectionGap;

            DrawQueueList(inRect, ref y);
            y += SectionGap;

            DrawFooter(inRect, ref y);
        }

        // ════════════════════════════════════
        //  Header
        // ════════════════════════════════════

        private void DrawHeader(Rect inRect, ref float y, in SpacecraftSnapshot snap)
        {
            Rect header = new Rect(0f, y, inRect.width, 48f);
            Widgets.DrawBoxSolid(header, PanelBg);
            Widgets.DrawBoxSolid(new Rect(header.x, header.yMax - 2f, header.width, 2f), AccentCyan);

            string status = snap.HasActiveBurn ? "燃烧中"
                : snap.PlannedManeuverTotalCount > 0 ? "待执行"
                : snap.HasRendezvousGuidance
                    ? (snap.RendezvousIsApproach
                        ? $"接近中 → G{snap.ApproachTargetId}"
                        : (snap.RendezvousArmed
                            ? $"交会引导中 → G{snap.RendezvousTargetId}"
                            : "交会引导待命"))
                    : "空闲";
            Color statusColor = snap.HasActiveBurn ? AccentWarn
                : snap.PlannedManeuverTotalCount > 0 ? AccentOk
                : snap.HasRendezvousGuidance ? AccentCyan
                : snap.HasApproachGuidance ? AccentCyan : TextDim;

            Text.Font = GameFont.Medium;
            GUI.color = statusColor;
            Widgets.Label(new Rect(header.x + 16f, header.y + 4f, 380f, 26f),
                $"机动队列 — Group {groupId}");
            GUI.color = Color.white;
            Text.Font = GameFont.Tiny;
            GUI.color = TextDim;
            Widgets.Label(new Rect(header.x + 16f, header.yMax - 22f, 400f, 18f),
                $"共 {snap.PlannedManeuverTotalCount} 段机动 · 状态: {status}");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // 交会/接近引导激活时提供手动取消（玩家接管）
            if (snap.HasRendezvousGuidance && Widgets.ButtonText(
                new Rect(header.x + header.width - 116f, header.y + 4f, 100f, 24f),
                snap.RendezvousIsApproach ? "取消接近" : "取消交会引导"))
            {
                bool wasApproach = snap.RendezvousIsApproach;
                PhysicsCommands.ClearRendezvousGuidance(groupId);
                Messages.Message($"Group {groupId} {(wasApproach ? "接近引导" : "交会引导")}已取消", MessageTypeDefOf.TaskCompletion);
            }

            y = header.yMax;
        }

        // ════════════════════════════════════
        //  Burn 执行区（dV 进度条 + 紧急终止）
        // ════════════════════════════════════

        private void DrawBurnSection(Rect inRect, ref float y, in SpacecraftSnapshot snap)
        {
            Rect section = new Rect(4f, y, inRect.width - 8f, 130f);
            Widgets.DrawBoxSolid(section, SectionBg);
            Widgets.DrawBoxSolid(new Rect(section.x, section.y, 3f, section.height), AccentWarn);

            // 标题
            Text.Font = GameFont.Small;
            GUI.color = AccentWarn;
            Widgets.Label(new Rect(section.x + 12f, section.y + 8f, section.width - 24f, 18f), "当前机动执行中");
            GUI.color = Color.white;

            // dV 进度条（计划 = 已施加 + 剩余）
            double applied = snap.AppliedBurnDv;
            double planned = applied + snap.RemainingBurnDv;
            float frac = planned > 0.001 ? (float)(applied / planned) : 0f;
            Rect barRect = new Rect(section.x + 12f, section.y + 32f, section.width - 24f, 18f);
            Widgets.DrawBoxSolid(barRect, BarBg);
            float fillW = barRect.width * Mathf.Clamp01(frac);
            if (fillW > 2f)
                Widgets.DrawBoxSolid(new Rect(barRect.x, barRect.y, fillW, barRect.height), AccentCyan);
            Widgets.DrawBox(barRect, 1);

            // 信息行：dV 与工质
            Text.Font = GameFont.Tiny;
            GUI.color = Color.white;
            Widgets.Label(new Rect(barRect.x, barRect.yMax + 7f, barRect.width * 0.55f, 16f),
                $"已消耗 dV: {applied:F1} / {planned:F1} m/s  ({frac:P0})");
            GUI.color = TextDim;
            Widgets.Label(new Rect(barRect.x + barRect.width * 0.55f, barRect.yMax + 7f, barRect.width * 0.45f, 16f),
                $"工质已耗: {snap.BurnConsumedPropellant:F1} kg");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // 紧急终止（引擎侧命令仍带"无引擎即 false"的契约）
            Rect stopRect = new Rect(section.x + 12f, section.yMax - 40f, section.width * 0.42f, 30f);
            GUI.color = new Color(0.85f, 0.28f, 0.24f);
            if (Widgets.ButtonText(stopRect, "紧急终止当前机动"))
                PhysicsCommands.EmergencyStopBurn(groupId);
            GUI.color = Color.white;

            // 右侧提示
            GUI.color = TextDim;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(stopRect.xMax + 10f, section.yMax - 36f, section.width - stopRect.width - 34f, 22f),
                "终止后剩余 dV 丢弃，已烧工质不退");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            y = section.yMax;
        }

        // ════════════════════════════════════
        //  当前网络区
        // ════════════════════════════════════

        private void DrawNetworkSection(Rect inRect, ref float y, in SpacecraftSnapshot snap)
        {
            networkBuf.Clear();
            PhysicsQueries.CopyNetworkOptions(groupId, networkBuf);

            Rect section = new Rect(4f, y, inRect.width - 8f, 80f);
            Widgets.DrawBoxSolid(section, SectionBg);
            Widgets.DrawBoxSolid(new Rect(section.x, section.y, 3f, section.height), AccentCyan);

            Widgets.Label(new Rect(section.x + 12f, section.y + 6f, section.width - 24f, 18f),
                "当前网络（新机动继承，单选）");
            Text.Font = GameFont.Tiny;
            GUI.color = TextDim;
            Widgets.Label(new Rect(section.x + 12f, section.y + 28f, section.width - 24f, 16f),
                "机动执行只烧启用网络；null = 全启用");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            if (networkBuf.Count > 0)
            {
                bool allOn = string.IsNullOrEmpty(snap.CurrentNetworkDefName);
                string allLabel = allOn ? "☑ 全启用" : "☐ 全启用";
                float allW = Text.CalcSize(allLabel).x + 18f;
                Rect allRect = new Rect(section.x + 12f, section.yMax - 30f, allW, 26f);
                GUI.color = GrayButton;
                if (Widgets.ButtonText(allRect, allLabel))
                    PhysicsCommands.SetCurrentNetwork(groupId, null);

                float nx = allRect.xMax + 6f;
                for (int i = 0; i < networkBuf.Count; i++)
                {
                    var n = networkBuf[i];
                    bool on = snap.CurrentNetworkDefName == n.DefName;
                    string label = (on ? "☑ " : "☐ ") + n.Label;
                    float w = Text.CalcSize(label).x + 18f;
                    Rect btnRect = new Rect(nx, section.yMax - 30f, w, 26f);
                    if (Widgets.ButtonText(btnRect, label))
                        PhysicsCommands.SetCurrentNetwork(groupId, n.DefName);
                    nx += w + 5f;
                }
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = TextDim;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(section.x + 12f, section.yMax - 28f, section.width - 24f, 20f),
                    "无推进能力（无引擎/工质网络），无法执行机动");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            y = section.yMax;
        }

        // ════════════════════════════════════
        //  机动列表（逐行取消 + burn 估算）
        // ════════════════════════════════════

        private void DrawQueueList(Rect inRect, ref float y)
        {
            PhysicsQueries.CopyPlannedManeuvers(groupId, queueBuf);
            int count = queueBuf.Count;

            Rect titleRect = new Rect(4f, y, inRect.width - 8f, 24f);
            Widgets.DrawBoxSolid(titleRect, SectionBg);
            Widgets.DrawBoxSolid(new Rect(titleRect.x, titleRect.y, 3f, titleRect.height), AccentCyan);
            Widgets.Label(new Rect(titleRect.x + 12f, titleRect.y + 3f, titleRect.width - 24f, 18f),
                $"机动规划队列（{count} 段）");
            y += 28f;

            if (count == 0)
            {
                Rect emptyRect = new Rect(4f, y, inRect.width - 8f, 72f);
                Widgets.DrawBoxSolid(emptyRect, SectionBg);
                GUI.color = TextDim;
                Text.Font = GameFont.Tiny;
                Widgets.Label(emptyRect.ContractedBy(12f),
                    "当前无机动规划。\n使用「机动规划」添加机动，或用「转移到圆轨 / 交会（选目标）」生成两段式转移。");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                y = emptyRect.yMax;
                return;
            }

            Rect listRect = new Rect(4f, y, inRect.width - 8f, inRect.height - y - ButtonHeight - SectionGap);
            float listContentH = count * EntryHeight;
            Widgets.BeginScrollView(listRect, ref scrollPos,
                new Rect(0f, 0f, listRect.width - 20f, listContentH));

            int currentTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe());

            for (int i = 0; i < count; i++)
            {
                Rect entry = new Rect(0f, i * EntryHeight, listRect.width - 20f, EntryHeight - 2f);
                Widgets.DrawBoxSolid(entry, (i % 2 == 0) ? SectionBg : SectionBgAlt);
                DrawQueueEntry(entry, i, queueBuf[i], currentTick);
                if (i < count - 1)
                {
                    GUI.color = RowBorder;
                    Widgets.DrawLineHorizontal(entry.x + 8f, entry.yMax, entry.width - 16f);
                    GUI.color = Color.white;
                }
            }

            Widgets.EndScrollView();
            y = listRect.yMax;
        }

        private void DrawQueueEntry(Rect entry, int index, in PlannedManeuverSnapshot pm, int currentTick)
        {
            int remainingTicks = pm.ExecuteTick - currentTick;

            // burn 估算（按本条机动的网络配置）——提前算好用于显示点火时刻（燃烧居中：点火 = 节点 − 半程燃烧）
            float dvMag = Mathf.Sqrt(pm.RadialDv * pm.RadialDv + pm.TangentialDv * pm.TangentialDv + pm.NormalDv * pm.NormalDv);
            PhysicsQueries.TryBuildBurnQuote(groupId, dvMag, pm.UseNetworkDefNames, out var est);
            bool feasible = est != null && est.Feasible;

            string timeStr;
            if (feasible && est.BurnSeconds > 0f)
            {
                int ignitionRemaining = remainingTicks - Mathf.RoundToInt(est.BurnSeconds / 2f);
                timeStr = ignitionRemaining > 0 ? $"点火 T-{ignitionRemaining}s" : "点火在即";
            }
            else
            {
                timeStr = remainingTicks > 0 ? $"T-{remainingTicks}s" : "已到期";
            }
            string statusStr = pm.Active ? "待执行" : "已取消";
            string netLabel = pm.NetworkLabel;
            Color statusColor = pm.Active ? AccentOk : TextDim;

            // P2（§2.5 / B1.2）：条目上带着"意图（目标条件）"时就挂个 tooltip。
            // 用 tooltip 而不是再加一行：**零布局风险**（这一版没法开游戏看排版）。
            if (pm.IntentDescription != null)
                TooltipHandler.TipRegion(entry,
                    "计划意图（目标条件）：" + pm.IntentDescription +
                    "\nP2：执行时按「活轨道」重解 Δv 与方向，而不是照烧规划那一刻的冻结向量。");

            // 行1：编号徽章 + 状态 + 时间 + 网络 + 取消按钮
            Rect badge = new Rect(entry.x + 6f, entry.y + 8f, 26f, 22f);
            Widgets.DrawBoxSolid(badge, new Color(0.15f, 0.35f, 0.5f, 0.95f));
            Widgets.DrawBox(badge, 1);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(badge, (index + 1).ToString());
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;

            float line1X = entry.x + 40f;
            GUI.color = statusColor;
            Widgets.Label(new Rect(line1X, entry.y + 8f, 90f, 20f), statusStr);
            GUI.color = Color.white;
            Widgets.Label(new Rect(line1X + 100f, entry.y + 8f, 110f, 20f), timeStr);
            GUI.color = TextDim;
            Widgets.Label(new Rect(line1X + 220f, entry.y + 8f, entry.width - 220f - 146f, 20f),
                $"网络: {netLabel}");
            GUI.color = Color.white;

            // 取消此节点及以后
            GUI.color = new Color(0.75f, 0.5f, 0.35f);
            if (Widgets.ButtonText(new Rect(entry.x + entry.width - 140f, entry.y + 5f, 132f, 26f),
                "取消此节点及以后"))
                PhysicsCommands.CancelManeuversFrom(groupId, index);
            GUI.color = Color.white;

            // 行2：dV 分量
            GUI.color = Color.white;
            Widgets.Label(new Rect(line1X, entry.y + 38f, entry.width - line1X - 10f, 18f),
                $"  径向 {pm.RadialDv,7:F0}   切向 {pm.TangentialDv,7:F0}   法向 {pm.NormalDv,7:F0}  m/s");

            // 行3：burn 估算（est 已在函数开头计算）
            Rect estRect = new Rect(line1X, entry.y + 62f, entry.width - line1X - 10f, 20f);
            if (est != null && est.Feasible)
            {
                GUI.color = TextDim;
                // 备用网络（RCS）：本机动不动它，只报存量——与物理的选网语义一致（见 BurnCore）
                string reserve = "";
                var nets = est.Networks;
                for (int i = 0; i < nets.Count; i++)
                {
                    var ne = nets[i];
                    if (!ne.IsReserve || ne.PropellantLabel == null) continue;
                    reserve += $"　备「{ne.PropellantLabel}」{ne.AvailableKg:F0} kg";
                }
                Widgets.Label(estRect,
                    $"  需工质 {est.RequiredPropellant:F0} kg · 燃烧 {est.BurnSeconds:F0} s · 最大可达 ΔV {est.MaxDeltaV:F0} m/s{reserve}");
                GUI.color = Color.white;
            }
            else if (est == null || !est.HasNetworks)
            {
                GUI.color = AccentWarn;
                Widgets.Label(estRect, "  未启用任何工质网络，无法执行");
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = AccentWarn;
                BurnNetworkQuote badNet = default;
                bool hasBad = false;
                var nets = est.Networks;
                for (int i = 0; i < nets.Count; i++)
                {
                    if (nets[i].Feasible) continue;
                    badNet = nets[i];
                    hasBad = true;
                    break;
                }
                if (hasBad && badNet.PropellantLabel != null)
                    Widgets.Label(estRect,
                        $"  工质不足：网络「{badNet.PropellantLabel}」需 {badNet.RequiredKg:F0} kg（当前 {badNet.AvailableKg:F0}）");
                else
                    Widgets.Label(estRect,
                        $"  工质不足：总需 {est.RequiredPropellant:F0} kg · 最大可达 ΔV {est.MaxDeltaV:F0} m/s");
                GUI.color = Color.white;
            }
        }

        // ════════════════════════════════════
        //  Footer
        // ════════════════════════════════════

        private void DrawFooter(Rect inRect, ref float y)
        {
            float btnW = (inRect.width - 20f) * 0.5f;
            GUI.color = GrayButton;
            // P1-8：从这里开规划器（队列与规划器共用同一个 group ✓ 新计划的归属与队列语义一致）
            if (Widgets.ButtonText(new Rect(4f, y, btnW, ButtonHeight), "规划机动…"))
            {
                Find.WindowStack.Add(new Window_PlanEditor(groupId));
            }
            if (Widgets.ButtonText(new Rect(4f + btnW + 12f, y, btnW, ButtonHeight), "清除所有机动"))
            {
                PhysicsCommands.ClearManeuvers(groupId);
            }
            y += ButtonHeight;
            if (Widgets.ButtonText(new Rect(4f, y, btnW, ButtonHeight), "关闭"))
            {
                Close();
            }
            GUI.color = Color.white;
            y += ButtonHeight;
        }
    }
}
