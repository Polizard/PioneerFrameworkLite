using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **机动规划视图（玩家向，可嵌入）**：选一个 Operation、改它的参数、算出计划、送进机动队列。
    ///
    /// 2026-11-20 从 `Window_PlanEditor` 里抽出来，为的是让「机动规划窗」能把
    /// **规划页签**和**滑条页签**放在同一个窗口里（用户令：规划器从队列窗口拿出来、
    /// 新控制律为主、滑条与预览都不能没有）。
    ///
    /// 它补的是移植时**故意留下的缺口**：上游每个 Operation 的 `DoParametersGUI` 直接用 KSP 的 IMGUI，
    /// 我们把它摘成空操作（那一层不许进纯层）⇒ "玩家怎么改参数"由本视图 + `Operation.GetParams()`
    /// 那句纯描述承担。
    ///
    /// **参数一律按"显示单位"编辑**（km / 度 / 比值）：换算写在每个 Operation 的 `PlanParam` 闭包里
    /// ⇒ 这里不需要知道 `EditableDoubleMult` 的 scale，也没有 `* 1000` 这类魔法数。
    /// </summary>
    public sealed class PlanEditorView
    {
        private readonly int groupId;

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

        /// <summary>规划预览回调（宿主窗口实现：把计划节点变成 `orbitSegments`）。2026-11-20。</summary>
        public Action<OrbitView, IReadOnlyList<PlanNode>>? OnPlanPreview;

        // ── 霍曼（转移到目标编队）：2026-11-20 挂进规划页签 ──
        private bool hohmannMode;
        private readonly List<SpaceEntityGroup> hohmannTargets = new();
        private int hohmannSel = -1;
        private List<PlanNode>? hohmannNodes;
        private string hohmannStatus = "";

        private const float Pad = 12f;
        private const float ListWidth = 200f;
        private const float RowH = 28f;

        private static readonly Color PanelBg = new(0.09f, 0.12f, 0.16f, 0.92f);
        private static readonly Color PanelBgAlt = new(0.115f, 0.145f, 0.185f, 0.92f);
        private static readonly Color SelectedBg = new(0.18f, 0.42f, 0.55f, 0.85f);

        public PlanEditorView(int targetGroupId)
        {
            groupId = targetGroupId;
        }

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

            float dvMag = (float)plan[0].dV.magnitude;
            if (dvMag <= 0f) return null;

            // 干质量 + 推进源口径收在查询里（批 5e §4.3b：不再从窗口拼实体质量）。
            if (!PhysicsQueries.TryBuildBurnQuote(groupId, dvMag, null, out var quote) || !quote.HasNetworks)
                return null; // 没引擎就没有推力加速度 ⇒ 不报区制（不许猜）
            if (!quote.Feasible || quote.BurnSeconds <= 0f) return null;

            (DVector3 r, DVector3 _) = ov.RightHandedStateVectorsAtUT(plan[0].ut);
            return BurnRegime.Classify(dvMag, r.magnitude, ov.mu, dvMag / quote.BurnSeconds);
        }

        public void Draw(Rect inRect)
        {
            Text.Font = GameFont.Small;

            if (ops.Length == 0)
            {
                Widgets.Label(new Rect(Pad, Pad, inRect.width - 2 * Pad, 40f),
                    "没有发现任何可用的机动操作（反射没扫到 Operation 子类）。");
                return;
            }

            int tick = (int)Math.Round(TimeScaleManager.GetPhysicsTickSafe());
            bool hasOrbit = PhysicsQueries.TryToOrbitView(groupId, tick, out OrbitView ov);

            float y = Pad;
            Widgets.Label(new Rect(Pad, y, inRect.width - 2 * Pad, 24f), $"规划机动 · G{groupId}");
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
            var listView = new Rect(0f, 0f, listInner.width - 16f, (ops.Length + 1) * RowH + 4f);
            Widgets.BeginScrollView(listInner, ref opScroll, listView);

            // 第 0 行：转移到目标编队（霍曼）——用户 2026-11-20 要求把它挂进规划页签
            var hoRect = new Rect(0f, 0f, listView.width, RowH - 2f);
            if (hohmannMode) Widgets.DrawBoxSolid(hoRect, SelectedBg);
            if (Widgets.ButtonText(hoRect, "转移到目标编队"))
            {
                if (!hohmannMode)
                {
                    hohmannMode = true;
                    RefreshHohmannTargets();
                }
            }

            for (int i = 0; i < ops.Length; i++)
            {
                var r = new Rect(0f, (i + 1) * RowH, listView.width, RowH - 2f);
                if (!hohmannMode && i == sel) Widgets.DrawBoxSolid(r, SelectedBg);
                if (Widgets.ButtonText(r, ops[i].GetName()) && (i != sel || hohmannMode))
                {
                    hohmannMode = false;
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
            if (hohmannMode)
            {
                DrawHohmann(content, ref cy, ov, tick);
            }
            else
            {
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
                    if (nodes != null) OnPlanPreview?.Invoke(ov, nodes);
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
                        if (burnSec > 0.0 && PhysicsApi.TryGetSpacecraft(groupId, out var alignSnap)
                            && PhysicsQueries.TryGetMainThrustAxis(groupId, out DVector3 mainAxis))
                        {
                            if (mainAxis.sqrMagnitude > 1e-12)
                            {
                                double angDeg = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                                    DVector3.Dot(mainAxis.normalized, nodes[0].dV.normalized))))   // 两系同轴（P4）⇒ 直传
                                    * 180.0 / Math.PI;
                                double leadSeconds = nodes[0].ut - tick;
                                if (!AlignFeasibility.IsFeasible(angDeg, alignSnap.MaxAngularRateRad * 180.0 / Math.PI,
                                        leadSeconds, burnSec, out string alignWhy))
                                    status = "⚠ " + alignWhy;
                            }
                        }

                        PlanIntent intent = PlanIntent.Capture(Sel, absoluteTick: nodes[0].ut);
                        if (PhysicsCommands.QueuePlan(groupId, plannedFrom, nodes, intent, out string reason, out string summary))
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

        /// <summary>同中心天体下的其它编队（霍曼目标候选）。</summary>
        private void RefreshHohmannTargets()
        {
            hohmannTargets.Clear();
            hohmannSel = -1;
            hohmannNodes = null;
            hohmannStatus = "";
            var sg = SpaceGameComp.GetCached();
            var self = sg?.GetGroupById(groupId);
            if (sg == null || self == null) return;
            foreach (var g in sg.GetAllGroups())
            {
                if (g == null || g.GroupId == groupId) continue;
                if (g.CelestialBodyId != self.CelestialBodyId) continue;
                hohmannTargets.Add(g);
            }
        }

        /// <summary>
        /// **霍曼（转移到目标编队）**：选目标 → 算相位窗口与两发 → 预览 → 入队。
        /// 2026-11-20 从"没有玩家入口"补上（`OrbitAdapter.TryPlanHohmannTransfer` 之前只有验收台在调）。
        /// </summary>
        private void DrawHohmann(Rect content, ref float cy, OrbitView ov, int tick)
        {
            Widgets.Label(new Rect(0f, cy, content.width, 22f), "转移到目标编队（霍曼：两发 + 相位窗口）");
            cy += 26f;

            if (hohmannTargets.Count == 0)
            {
                Widgets.Label(new Rect(0f, cy, content.width, 22f), "同一中心天体下没有其它编队可选。");
                cy += 26f;
                if (Widgets.ButtonText(new Rect(0f, cy, 120f, 26f), "刷新列表")) RefreshHohmannTargets();
                cy += 32f;
                return;
            }

            for (int i = 0; i < hohmannTargets.Count; i++)
            {
                var g = hohmannTargets[i];
                var r = new Rect(0f, cy, content.width, 24f);
                if (i == hohmannSel) Widgets.DrawBoxSolid(r, SelectedBg);
                if (Widgets.ButtonText(r, $"G{g.GroupId}（中心天体 {g.CelestialBodyId}）"))
                {
                    hohmannSel = i;
                    hohmannNodes = null;
                    hohmannStatus = "";
                }
                cy += 26f;
            }

            cy += 6f;
            if (Widgets.ButtonText(new Rect(0f, cy, 120f, 28f), "刷新列表")) RefreshHohmannTargets();
            if (Widgets.ButtonText(new Rect(128f, cy, 150f, 28f), "计算两发"))
            {
                hohmannNodes = null;
                hohmannStatus = "";
                if (hohmannSel < 0) hohmannStatus = "先选目标编队。";
                else
                {
                    var self = SpaceGameComp.GetCached()?.GetGroupById(groupId);
                    var tgt = hohmannTargets[hohmannSel];
                    var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                    if (self == null || tgt == null) hohmannStatus = "编队不存在。";
                    else if (OrbitAdapter.TryPlanHohmannTransfer(self, tgt, mgr, tick, out var nodes, out string why))
                    {
                        hohmannNodes = nodes;
                        hohmannStatus = "相位窗口：" + why;
                        if (nodes != null) OnPlanPreview?.Invoke(ov, nodes);
                    }
                    else hohmannStatus = "无解：" + why;
                }
            }
            cy += 34f;

            if (hohmannNodes != null)
            {
                var sb = new StringBuilder();
                sb.Append("计划 ").Append(hohmannNodes.Count).Append(" 条");
                for (int i = 0; i < hohmannNodes.Count; i++)
                    sb.Append("\n  #").Append(i + 1)
                      .Append("  t=").Append(hohmannNodes[i].ut.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append('s')
                      .Append("  |Δv|=").Append(hohmannNodes[i].dV.magnitude.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(" m/s");
                Widgets.Label(new Rect(0f, cy, content.width, 80f), sb.ToString());
                cy += 84f;

                if (Widgets.ButtonText(new Rect(0f, cy, 150f, 28f), "送入机动队列"))
                {
                    if (PhysicsCommands.QueuePlan(groupId, ov, hohmannNodes, null, out string reason, out string summary))
                        hohmannStatus = "已入队：" + summary;
                    else hohmannStatus = "入队失败：" + reason;
                }
                cy += 34f;
            }

            if (!string.IsNullOrEmpty(hohmannStatus))
            {
                Widgets.Label(new Rect(0f, cy, content.width, 44f), hohmannStatus);
                cy += 48f;
            }
        }
    }
}
