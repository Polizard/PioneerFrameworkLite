using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **机动规划器（玩家向）**：选一个 Operation、改它的参数、算出计划、送进机动队列。
    ///
    /// 它补的是移植时**故意留下的缺口**：上游每个 Operation 的 `DoParametersGUI` 直接用 KSP 的 IMGUI，
    /// 我们把它摘成空操作（那一层不许进纯层）⇒ "玩家怎么改参数"由本窗口 + `Operation.GetParams()`
    /// 那句纯描述承担。
    ///
    /// **参数一律按"显示单位"编辑**（km / 度 / 比值）：换算写在每个 Operation 的 `PlanParam` 闭包里
    /// ⇒ 这里不需要知道 `EditableDoubleMult` 的 scale，也没有 `* 1000` 这类魔法数。
    ///
    /// ## ⚠ 状态：**布局与手感尚未经游戏内确认**（2026-09-19）
    /// 本批把它写到"能编译 + 参数描述被 `PlanParamTests` 机械覆盖"为止；下面这些**只有开游戏才能定**：
    /// 控件间距、滚动是否够用、长标签会不会挤掉单位、以及各操作默认值是否合理。
    /// 打开方式：**机动队列窗口 → 「规划机动…」**。
    ///
    /// ## 登记（本批不做）
    /// · 需要「目标」的操作（`RequiresTarget`，目前只有 `OperationKillRelVel`）**暂不可用**——
    ///   目标选择还没接进这个界面，窗口会明说原因，而不是给一个算错的计划。
    /// </summary>
    public class Window_PlanEditor : Window
    {
        private readonly SpaceEntityGroup group;

        private readonly Operation[] ops = Operation.GetAvailableOperations();
        private int sel;

        private Vector2 opScroll = Vector2.zero;
        private Vector2 paramScroll = Vector2.zero;

        /// <summary>各参数的编辑缓冲（按 `sel` 变化重建，避免每帧格式化把玩家正在敲的字冲掉）。</summary>
        private readonly List<string> buffers = new();
        private int buffersFor = -1;

        private List<PlanNode>? nodes;

        /// <summary>最近一次计划算出来的燃烧区制（B2.1）——null = 还没算或拿不到推力。</summary>
        private BurnRegime? regime;
        private OrbitView plannedFrom;
        private string status = "";

        private const float Pad = 12f;
        private const float ListWidth = 200f;
        private const float RowH = 28f;

        private static readonly Color PanelBg = new(0.09f, 0.12f, 0.16f, 0.92f);
        private static readonly Color PanelBgAlt = new(0.115f, 0.145f, 0.185f, 0.92f);
        private static readonly Color SelectedBg = new(0.18f, 0.42f, 0.55f, 0.85f);

        public Window_PlanEditor(SpaceEntityGroup targetGroup)
        {
            group = targetGroup;
            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
        }

        public override Vector2 InitialSize => new(780f, 560f);

        private Operation Sel => ops[sel];

        private void SyncBuffers()
        {
            if (buffersFor == sel) return;
            buffersFor = sel;
            buffers.Clear();
            if (ops.Length == 0) return;
            foreach (PlanParam p in Sel.GetParams())
                buffers.Add($"{p.Get():0.###}");
            nodes = null;
            status = "";
        }

        /// <summary>
        /// **燃烧区制判据（B2.1）**：这条计划是"准脉冲"还是"连续推力段"。
        /// 输入 = 烧点的半径与本次燃烧的推力加速度（由 `BurnCalculator` 的燃烧时长反推 `a = Δv/t_b`）。
        /// 拿不到推力（没引擎/没工质网络）就返回 null——**不许猜**（猜错会把连续段当脉冲烧）。
        /// </summary>
        private BurnRegime? ComputeRegime(List<PlanNode>? plan, OrbitView ov, double tick)
        {
            if (plan == null || plan.Count == 0) return null;

            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return null;

            float dvMag = (float)plan[0].dV.magnitude;
            if (dvMag <= 0f) return null;

            var src = sg.GetGroupPropulsionSource(group);

            // 干质量：按实体求和（`GetGroupMass` 不可见 ⇒ 与机动队列窗口用同一个公开口径）
            float dryMass = 0f;
            foreach (int eid in group.GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e != null) dryMass += e.TotalMass;
            }

            if (src == null) return null; // 没引擎就没有推力加速度 ⇒ 不报区制（不许猜）
            var est = BurnCalculator.Estimate(dvMag, dryMass, src, src.Networks); // 第 5 参走默认（别显式传 null）
            if (!est.Feasible || est.BurnSeconds <= 0f) return null;

            (DVector3 r, DVector3 _) = ov.RightHandedStateVectorsAtUT(plan[0].ut);
            return BurnRegime.Classify(dvMag, r.magnitude, ov.mu, dvMag / est.BurnSeconds);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;

            if (ops.Length == 0)
            {
                Widgets.Label(new Rect(Pad, Pad, inRect.width - 2 * Pad, 40f),
                    "没有发现任何可用的机动操作（反射没扫到 Operation 子类）。");
                return;
            }

            int tick = (int)Math.Round(TimeScaleManager.GetPhysicsTickSafe());
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            bool hasOrbit = OrbitAdapter.TryToOrbitView(group, mgr, tick, out OrbitView ov);

            float y = Pad;
            Widgets.Label(new Rect(Pad, y, inRect.width - 2 * Pad, 24f), $"规划机动 · G{group.GroupId}");
            y += 24f;

            if (!hasOrbit)
            {
                Widgets.Label(new Rect(Pad, y, inRect.width - 2 * Pad, 40f),
                    "这条船现在拿不到轨道状态（需要处在天体参考系下的自由飞行）⇒ 无法规划。");
                return;
            }

            Widgets.Label(new Rect(Pad, y, inRect.width - 2 * Pad, 20f),
                $"当前：e={ov.eccentricity:F4}   a={ov.semiMajorAxis / 1000.0:F0} km   " +
                $"i={ov.inclination * 180.0 / Math.PI:F3}°   T={ov.period:F0} s");
            y += 26f;

            float bodyH = inRect.height - Pad - y;

            // ── 左列：操作列表 ──
            var listRect = new Rect(Pad, y, ListWidth, bodyH);
            Widgets.DrawBoxSolid(listRect, PanelBg);
            Rect listInner = listRect.ContractedBy(4f);
            var listView = new Rect(0f, 0f, listInner.width - 16f, ops.Length * RowH + 4f);
            Widgets.BeginScrollView(listInner, ref opScroll, listView);
            for (int i = 0; i < ops.Length; i++)
            {
                var r = new Rect(0f, i * RowH, listView.width, RowH - 2f);
                if (i == sel) Widgets.DrawBoxSolid(r, SelectedBg);
                if (Widgets.ButtonText(r, ops[i].GetName()) && i != sel)
                {
                    sel = i;
                    SyncBuffers();
                }
            }
            Widgets.EndScrollView();

            // ── 右列：参数与计划 ──
            float rx = Pad + ListWidth + Pad;
            var rightRect = new Rect(rx, y, inRect.width - rx - Pad, bodyH);
            Widgets.DrawBoxSolid(rightRect, PanelBgAlt);

            SyncBuffers();

            Rect inner = rightRect.ContractedBy(8f);
            var content = new Rect(0f, 0f, inner.width - 16f, 640f);
            Widgets.BeginScrollView(inner, ref paramScroll, content);
            float cy = 0f;

            TimeSelector? ts = Sel.GetTimeSelector();
            if (ts != null)
            {
                Widgets.Label(new Rect(0f, cy, content.width, 20f), "点火时刻");
                cy += 22f;
                int n = ts.TimeRefNames.Length;
                float bw = Mathf.Min(120f, (content.width - 4f * (n - 1)) / Mathf.Max(1, n));
                for (int i = 0; i < n; i++)
                {
                    var br = new Rect(i * (bw + 4f), cy, bw, 26f);
                    if (i == ts._currentTimeRef) Widgets.DrawBoxSolid(br, SelectedBg);
                    if (Widgets.ButtonText(br, ts.TimeRefNames[i]) && i != ts._currentTimeRef)
                    {
                        ts._currentTimeRef = i;
                        nodes = null;
                    }
                }
                cy += 30f;

                Widgets.Label(new Rect(0f, cy, 120f, 22f), "提前（秒）");
                string lead = $"{ts.LeadTime.Val:0.###}";
                string leadNew = Widgets.TextField(new Rect(124f, cy, 110f, 22f), lead);
                if (leadNew != lead && double.TryParse(leadNew, out double lv))
                {
                    ts.LeadTime.Val = lv;
                    nodes = null;
                }
                cy += 28f;
            }

            IReadOnlyList<PlanParam> ps = Sel.GetParams();
            for (int i = 0; i < ps.Count && i < buffers.Count; i++)
            {
                PlanParam p = ps[i];
                Widgets.Label(new Rect(0f, cy, 150f, 22f), p.Label);
                string cur = buffers[i];
                string nw = Widgets.TextField(new Rect(154f, cy, 110f, 22f), cur);
                if (nw != cur)
                {
                    buffers[i] = nw;
                    if (double.TryParse(nw, out double v))
                    {
                        p.Set(v);
                        nodes = null;
                    }
                }
                if (!string.IsNullOrEmpty(p.Unit)) Widgets.Label(new Rect(268f, cy, 60f, 22f), p.Unit);
                cy += 26f;
            }

            cy += 8f;

            if (Sel.RequiresTarget)
            {
                Widgets.Label(new Rect(0f, cy, content.width, 44f),
                    "这个操作需要一个「目标」才能规划，而目标选择还没接进这个界面 ⇒ 暂时不可用。");
                cy += 48f;
            }
            else
            {
                if (Widgets.ButtonText(new Rect(0f, cy, 150f, 30f), "计算计划"))
                {
                    nodes = Sel.MakeNodes(ov, tick, new FixedTargetView(ov));
                    plannedFrom = ov;
                    regime = ComputeRegime(nodes, ov, tick);
                    status = nodes == null ? "规划失败。" : "";
                }

                if (Widgets.ButtonText(new Rect(158f, cy, 150f, 30f), "送入机动队列"))
                {
                    if (nodes == null)
                        status = "先点「计算计划」。";
                    else
                    {
                        // 带上"意图"：P2 里它是活重解的输入（参数走的是描述层 ⇒ 界面改什么就存什么）
                        // §2.4：玩家在这个窗口里排的 = **手动**条目（会夺走引导）
                        // B2.2：把预计燃烧时长写进计划（执行层照它居中；对准可行性也靠它）
                        double burnSec = regime?.burnSeconds ?? 0.0;
                        for (int k = 0; k < nodes.Count; k++)
                            nodes[k] = new PlanNode(nodes[k].dV, nodes[k].ut, PlanOwner.Manual, burnSec);

                        // B2.2 的对准可行性：**排队时**就告警（而不是等到点火时才发现转不过来）
                        var sgForAlign = Current.Game?.GetComponent<SpaceGameComp>();
                        if (burnSec > 0.0 && sgForAlign != null)
                        {
                            DVector3 mainAxis = sgForAlign.GetMainThrustAxisSystem(group);
                            if (mainAxis.sqrMagnitude > 1e-12)
                            {
                                double angDeg = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                                    DVector3.Dot(mainAxis.normalized, nodes[0].dV.normalized))))   // 两系同轴（P4）⇒ 直传
                                    * 180.0 / Math.PI;
                                double leadSeconds = nodes[0].ut - tick;
                                if (!AlignFeasibility.IsFeasible(angDeg, group.MaxAngularRate * 180.0 / Math.PI,
                                        leadSeconds, burnSec, out string alignWhy))
                                    status = "⚠ " + alignWhy;
                            }
                        }

                        PlanIntent intent = PlanIntent.Capture(Sel, absoluteTick: nodes[0].ut);
                        if (OrbitAdapter.TryQueuePlan(group, plannedFrom, nodes, out string reason, out string summary, intent))
                            status = "已入队：" + summary + "｜意图：" + intent.DescribeSelf();
                        else
                            status = "入队失败：" + reason;
                    }
                }
                cy += 36f;

                if (nodes != null)
                {
                    var sb = new StringBuilder();
                    sb.Append("计划 ").Append(nodes.Count).Append(" 条");
                    for (int i = 0; i < nodes.Count && i < 4; i++)
                        sb.Append("\n  #").Append(i + 1)
                          .Append("  t=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{nodes[i].ut:F0}").Append('s')
                          .Append("  |Δv|=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{nodes[i].dV.magnitude:F2}").Append(" m/s");
                    if (regime.HasValue) sb.Append("\n  区制：").Append(regime.Value.Describe());
                    Widgets.Label(new Rect(0f, cy, content.width, 100f), sb.ToString());
                    cy += 104f;
                }
            }

            string err = Sel.GetErrorMessage() ?? "";
            if (!string.IsNullOrEmpty(err))
            {
                Widgets.Label(new Rect(0f, cy, content.width, 44f), "提示：" + err);
                cy += 48f;
            }

            if (!string.IsNullOrEmpty(status)) Widgets.Label(new Rect(0f, cy, content.width, 44f), status);

            Widgets.EndScrollView();
        }
    }
}
