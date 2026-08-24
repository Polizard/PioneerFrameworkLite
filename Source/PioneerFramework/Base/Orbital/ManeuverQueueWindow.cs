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
        private SpaceEntityGroup group;
        private Vector2 scrollPos;

        private const float EntryHeight = 104f;
        private const float ButtonHeight = 34f;
        private const float SectionGap = 12f;
        private const float WindowWidth = 640f;
        private const float WindowHeight = 540f;   // 竖长条，避免接近正方形

        // ── 视觉风格 ──
        private static readonly Color PanelBg = new Color(0.055f, 0.075f, 0.10f, 0.95f);
        private static readonly Color SectionBg = new Color(0.09f, 0.12f, 0.16f, 0.92f);
        private static readonly Color SectionBgAlt = new Color(0.115f, 0.145f, 0.185f, 0.92f);
        private static readonly Color AccentCyan = new Color(0.25f, 0.9f, 1f, 0.9f);
        private static readonly Color AccentWarn = new Color(1f, 0.42f, 0.3f, 0.95f);
        private static readonly Color AccentOk = new Color(0.35f, 1f, 0.55f, 0.95f);
        private static readonly Color TextDim = new Color(0.60f, 0.66f, 0.72f, 1f);
        private static readonly Color BarBg = new Color(0.02f, 0.03f, 0.05f, 0.85f);
        private static readonly Color RowBorder = new Color(0.25f, 0.3f, 0.35f, 0.55f);
        /// <summary>普通按钮的统一灰色（未特别着色按钮）。</summary>
        private static readonly Color GrayButton = new Color(0.46f, 0.49f, 0.53f, 1f);

        public ManeuverQueueWindow(SpaceEntityGroup targetGroup)
        {
            group = targetGroup;
            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(WindowWidth, WindowHeight);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            float y = 0f;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();

            DrawHeader(inRect, ref y);
            y += SectionGap;

            if (group.HasActiveBurn)
            {
                DrawBurnSection(inRect, ref y, sg);
                y += SectionGap;
            }

            DrawNetworkSection(inRect, ref y, sg);
            y += SectionGap;

            DrawQueueList(inRect, ref y, sg);
            y += SectionGap;

            DrawFooter(inRect, ref y);
        }

        // ════════════════════════════════════
        //  Header
        // ════════════════════════════════════

        private void DrawHeader(Rect inRect, ref float y)
        {
            Rect header = new Rect(0f, y, inRect.width, 48f);
            Widgets.DrawBoxSolid(header, PanelBg);
            Widgets.DrawBoxSolid(new Rect(header.x, header.yMax - 2f, header.width, 2f), AccentCyan);

            string status = group.HasActiveBurn ? "燃烧中"
                : group.plannedManeuvers.Count > 0 ? "待执行"
                : group.HasRendezvousGuidance
                    ? (group.rendezvousIsApproach
                        ? string.Format("接近中 → G{0}", group.ApproachTargetId)
                        : (group.rendezvousArmed
                            ? string.Format("交会引导中 → G{0}", group.rendezvousTargetId)
                            : "交会引导待命"))
                    : "空闲";
            Color statusColor = group.HasActiveBurn ? AccentWarn
                : group.plannedManeuvers.Count > 0 ? AccentOk
                : group.HasRendezvousGuidance ? AccentCyan
                : group.HasApproachGuidance ? AccentCyan : TextDim;

            Text.Font = GameFont.Medium;
            GUI.color = statusColor;
            Widgets.Label(new Rect(header.x + 16f, header.y + 4f, 380f, 26f),
                string.Format("机动队列 — Group {0}", group.GroupId));
            GUI.color = Color.white;
            Text.Font = GameFont.Tiny;
            GUI.color = TextDim;
            Widgets.Label(new Rect(header.x + 16f, header.yMax - 22f, 400f, 18f),
                string.Format("共 {0} 段机动 · 状态: {1}", group.plannedManeuvers.Count, status));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // 交会/接近引导激活时提供手动取消（玩家接管）
            if (group.HasRendezvousGuidance && Widgets.ButtonText(
                new Rect(header.x + header.width - 116f, header.y + 4f, 100f, 24f),
                group.rendezvousIsApproach ? "取消接近" : "取消交会引导"))
            {
                group.ClearRendezvousGuidance();
                Messages.Message(string.Format("Group {0} {1}已取消", group.GroupId,
                    group.rendezvousIsApproach ? "接近引导" : "交会引导"), MessageTypeDefOf.TaskCompletion);
            }

            y = header.yMax;
        }

        // ════════════════════════════════════
        //  Burn 执行区（dV 进度条 + 紧急终止）
        // ════════════════════════════════════

        private void DrawBurnSection(Rect inRect, ref float y, SpaceGameComp sg)
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
            double applied = group.appliedBurnDV;
            double planned = applied + group.remainingBurnDV;
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
                string.Format("已消耗 dV: {0:F1} / {1:F1} m/s  ({2:P0})", applied, planned, frac));
            GUI.color = TextDim;
            Widgets.Label(new Rect(barRect.x + barRect.width * 0.55f, barRect.yMax + 7f, barRect.width * 0.45f, 16f),
                string.Format("工质已耗: {0:F1} kg", group.burnConsumedPropellant));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // 紧急终止
            Rect stopRect = new Rect(section.x + 12f, section.yMax - 40f, section.width * 0.42f, 30f);
            GUI.color = new Color(0.85f, 0.28f, 0.24f);
            if (Widgets.ButtonText(stopRect, "紧急终止当前机动"))
            {
                if (sg != null)
                    sg.EmergencyStopBurn(group);
                else
                {
                    group.EndBurn();
                    group.burnEnabledNetworks = null;
                    group.GetLinkedMapComp()?.SetThrustersBurning(false);
                }
            }
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

        private void DrawNetworkSection(Rect inRect, ref float y, SpaceGameComp sg)
        {
            IPropulsionSource src = sg != null ? sg.GetGroupPropulsionSource(group) : null;

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

            if (src != null && src.Networks.Count > 0)
            {
                bool allOn = group.currentNetwork == null;
                string allLabel = allOn ? "☑ 全启用" : "☐ 全启用";
                float allW = Text.CalcSize(allLabel).x + 18f;
                Rect allRect = new Rect(section.x + 12f, section.yMax - 30f, allW, 26f);
                GUI.color = GrayButton;
                if (Widgets.ButtonText(allRect, allLabel))
                    group.currentNetwork = null;

                float nx = allRect.xMax + 6f;
                foreach (var n in src.Networks)
                {
                    bool on = group.currentNetwork == n.propellant;
                    string label = (on ? "☑ " : "☐ ") + n.propellant.label;
                    float w = Text.CalcSize(label).x + 18f;
                    Rect btnRect = new Rect(nx, section.yMax - 30f, w, 26f);
                    if (Widgets.ButtonText(btnRect, label))
                        group.currentNetwork = n.propellant;
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

        private void DrawQueueList(Rect inRect, ref float y, SpaceGameComp sg)
        {
            int count = group.plannedManeuvers.Count;

            Rect titleRect = new Rect(4f, y, inRect.width - 8f, 24f);
            Widgets.DrawBoxSolid(titleRect, SectionBg);
            Widgets.DrawBoxSolid(new Rect(titleRect.x, titleRect.y, 3f, titleRect.height), AccentCyan);
            Widgets.Label(new Rect(titleRect.x + 12f, titleRect.y + 3f, titleRect.width - 24f, 18f),
                string.Format("机动规划队列（{0} 段）", count));
            y += 28f;

            if (count == 0)
            {
                Rect emptyRect = new Rect(4f, y, inRect.width - 8f, 72f);
                Widgets.DrawBoxSolid(emptyRect, SectionBg);
                GUI.color = TextDim;
                Text.Font = GameFont.Tiny;
                Widgets.Label(emptyRect.ContractedBy(12f),
                    "当前无机动规划。\n使用「机动规划」添加机动，或用「转移到圆轨 / 交会规划」生成两段式转移。");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                y = emptyRect.yMax;
                return;
            }

            Rect listRect = new Rect(4f, y, inRect.width - 8f, inRect.height - y - ButtonHeight - SectionGap);
            float listContentH = count * EntryHeight;
            Widgets.BeginScrollView(listRect, ref scrollPos,
                new Rect(0f, 0f, listRect.width - 20f, listContentH));

            // 干质量（估算用，每帧一次）
            float dryMass = 0f;
            var sgLocal = Current.Game?.GetComponent<SpaceGameComp>();
            if (sgLocal != null)
            {
                foreach (int eid in group.GetEntityIds())
                {
                    var e = sgLocal.GetEntityById(eid);
                    if (e != null) dryMass += e.TotalMass;
                }
            }

            int currentTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe());

            for (int i = 0; i < count; i++)
            {
                Rect entry = new Rect(0f, i * EntryHeight, listRect.width - 20f, EntryHeight - 2f);
                Widgets.DrawBoxSolid(entry, (i % 2 == 0) ? SectionBg : SectionBgAlt);
                DrawQueueEntry(entry, i, group.plannedManeuvers[i], currentTick, dryMass, sg);
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

        private void DrawQueueEntry(Rect entry, int index, SpaceEntityGroup.PlannedManeuver pm,
            int currentTick, float dryMass, SpaceGameComp sg)
        {
            int remainingTicks = pm.executeTick - currentTick;

            // burn 估算（按本条机动的网络配置）——提前算好用于显示点火时刻（燃烧居中：点火 = 节点 − 半程燃烧）
            float dvMag = Mathf.Sqrt(pm.radialDV * pm.radialDV + pm.tangentialDV * pm.tangentialDV + pm.normalDV * pm.normalDV);
            IPropulsionSource src = sg != null ? sg.GetGroupPropulsionSource(group) : null;
            var est = BurnCalculator.Estimate(dvMag, dryMass, src, src != null ? src.Networks : null, pm.useNetworks);

            string timeStr;
            if (est.Feasible && est.BurnSeconds > 0f)
            {
                int ignitionRemaining = remainingTicks - Mathf.RoundToInt(est.BurnSeconds / 2f);
                timeStr = ignitionRemaining > 0 ? string.Format("点火 T-{0}s", ignitionRemaining) : "点火在即";
            }
            else
            {
                timeStr = remainingTicks > 0 ? string.Format("T-{0}s", remainingTicks) : "已到期";
            }
            string statusStr = pm.active ? "待执行" : "已取消";
            string netLabel = pm.useNetworks == null
                ? "全启用"
                : string.Join("+", pm.useNetworks.Select(d => d != null ? d.label : "?"));
            Color statusColor = pm.active ? AccentOk : TextDim;

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
                string.Format("网络: {0}", netLabel));
            GUI.color = Color.white;

            // 取消此节点及以后
            GUI.color = new Color(0.75f, 0.5f, 0.35f);
            if (Widgets.ButtonText(new Rect(entry.x + entry.width - 140f, entry.y + 5f, 132f, 26f),
                "取消此节点及以后"))
            {
                if (sg != null)
                    sg.CancelGroupManeuvers(group, index);
                else
                    group.CancelManeuversFrom(index);
            }
            GUI.color = Color.white;

            // 行2：dV 分量
            GUI.color = Color.white;
            Widgets.Label(new Rect(line1X, entry.y + 38f, entry.width - line1X - 10f, 18f),
                string.Format("  径向 {0,7:F0}   切向 {1,7:F0}   法向 {2,7:F0}  m/s",
                    pm.radialDV, pm.tangentialDV, pm.normalDV));

            // 行3：burn 估算（est 已在函数开头计算）
            Rect estRect = new Rect(line1X, entry.y + 62f, entry.width - line1X - 10f, 20f);
            if (est.Feasible)
            {
                GUI.color = TextDim;
                Widgets.Label(estRect,
                    string.Format("  需工质 {0:F0} kg · 燃烧 {1:F0} s · 最大可达 ΔV {2:F0} m/s",
                        est.RequiredPropellant, est.BurnSeconds, est.MaxDeltaV));
                GUI.color = Color.white;
            }
            else if (est.Networks == null || est.Networks.Count == 0)
            {
                GUI.color = AccentWarn;
                Widgets.Label(estRect, "  未启用任何工质网络，无法执行");
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = AccentWarn;
                NetworkEstimate badNet = est.Networks.Find(ne => !ne.Feasible);
                if (badNet.Propellant != null)
                    Widgets.Label(estRect,
                        string.Format("  工质不足：网络「{0}」需 {1:F0} kg（当前 {2:F0}）",
                            badNet.Propellant.label, badNet.RequiredPropellant, badNet.Available));
                else
                    Widgets.Label(estRect,
                        string.Format("  工质不足：总需 {0:F0} kg · 最大可达 ΔV {1:F0} m/s",
                            est.RequiredPropellant, est.MaxDeltaV));
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
            if (Widgets.ButtonText(new Rect(4f, y, btnW, ButtonHeight), "清除所有机动"))
            {
                group.ClearManeuvers();
            }
            if (Widgets.ButtonText(new Rect(4f + btnW + 12f, y, btnW, ButtonHeight), "关闭"))
            {
                Close();
            }
            GUI.color = Color.white;
            y += ButtonHeight;
        }
    }
}
