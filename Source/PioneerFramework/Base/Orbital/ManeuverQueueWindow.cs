using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 显示群组的机动规划队列窗口。顶部可切换空间站「当前网络」——
    /// 此后加入的机动都继承该网络配置，并实时显示 MechJeb 式 burn 估算。
    /// </summary>
    public class ManeuverQueueWindow : Window
    {
        private SpaceEntityGroup group;
        private Vector2 scrollPos;

        private const float EntryHeight = 66f;
        private const float ButtonHeight = 30f;
        private const float WindowWidth = 480f;
        private const float WindowHeight = 480f;

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
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f),
                string.Format("机动队列 - Group {0}", group.GroupId));
            Text.Font = GameFont.Small;

            float y = 35f;

            int count = group.plannedManeuvers.Count;
            Widgets.Label(new Rect(0f, y, inRect.width, 22f),
                string.Format("共 {0} 个已规划机动（新机动继承下方「当前网络」）", count));
            y += 22f;

            // 船的推进参数（MechJeb 式 burn 估算用，每帧一次）
            IPropulsionSource src = null;
            float dryMass = 0f;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg != null)
            {
                src = sg.GetGroupPropulsionSource(group);
                foreach (int eid in group.GetEntityIds())
                {
                    var e = sg.GetEntityById(eid);
                    if (e != null) dryMass += e.TotalMass;
                }
            }

            // ── 当前网络区（单选：切换后加入的机动都按此网络判定）──
            if (src != null && src.Networks.Count > 0)
            {
                Widgets.Label(new Rect(0f, y, inRect.width, 18f), "当前网络（新机动继承，单选）：");
                y += 18f;

                bool allOn = group.currentNetwork == null;
                string allLabel = allOn ? "☑ 全启用" : "☐ 全启用";
                float allW = Text.CalcSize(allLabel).x + 14f;
                if (Widgets.ButtonText(new Rect(0f, y, allW, 17f), allLabel, true, false, true))
                    group.currentNetwork = null;

                float nx = allW + 6f;
                foreach (var n in src.Networks)
                {
                    bool on = group.currentNetwork == n.propellant;
                    string label = (on ? "☑ " : "☐ ") + n.propellant.label;
                    float w = Text.CalcSize(label).x + 14f;
                    Color prev = GUI.color;
                    if (!on)
                        GUI.color = new Color(0.6f, 0.6f, 0.6f);
                    if (Widgets.ButtonText(new Rect(nx, y, w, 17f), label, true, false, true))
                        group.currentNetwork = n.propellant; // 单选：直接设为该网络
                    GUI.color = prev;
                    nx += w + 4f;
                }
                y += 24f;
            }

            if (count == 0)
            {
                Widgets.Label(new Rect(0f, y, inRect.width, 22f), "当前无机动规划。");
                y += 30f;
            }
            else
            {
                float listHeight = count * EntryHeight;
                Rect listRect = new Rect(0f, y, inRect.width, inRect.height - y - ButtonHeight - 10f);
                Widgets.BeginScrollView(listRect, ref scrollPos, new Rect(0f, 0f, listRect.width - 16f, listHeight));

                // executeTick 为 physicsTick 基准，倒计时须用同一基准（Fast 档 TicksGame 会失真）
                int currentTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe());

                for (int i = 0; i < count; i++)
                {
                    var pm = group.plannedManeuvers[i];
                    int remainingTicks = pm.executeTick - currentTick;
                    string timeStr = remainingTicks > 0
                        ? string.Format("T-{0} 秒", remainingTicks)
                        : "已到期";

                    Rect entryRect = new Rect(0f, i * EntryHeight, listRect.width - 16f, EntryHeight - 2f);
                    // 背景
                    if (i % 2 == 0)
                        Widgets.DrawBoxSolid(entryRect, new Color(0.1f, 0.1f, 0.15f, 0.4f));

                    // 行1：编号 + 状态 + 时间 + 使用的网络
                    string status = pm.active ? "待执行" : "已取消";
                    string netLabel = pm.useNetworks == null ? "全启用"
                        : string.Join("+", System.Linq.Enumerable.Select(pm.useNetworks, d => d != null ? d.label : "?"));
                    Widgets.Label(new Rect(entryRect.x + 4f, entryRect.y + 2f, entryRect.width - 8f, 18f),
                        string.Format("#{0}  {1}  |  {2}  |  网络:{3}", i + 1, status, timeStr, netLabel));

                    // 行2：dV 分量
                    Widgets.Label(new Rect(entryRect.x + 4f, entryRect.y + 20f, entryRect.width - 8f, 16f),
                        string.Format("  径向 {0:F0}  切向 {1:F0}  法向 {2:F0}  m/s",
                            pm.radialDV, pm.tangentialDV, pm.normalDV));

                    // 行3：burn 估算（按本条机动的网络配置）
                    float dvMag = Mathf.Sqrt(pm.radialDV * pm.radialDV + pm.tangentialDV * pm.tangentialDV + pm.normalDV * pm.normalDV);
                    var est = BurnCalculator.Estimate(dvMag, dryMass, src,
                        src != null ? src.Networks : null, pm.useNetworks);
                    if (est.Feasible)
                    {
                        Widgets.Label(new Rect(entryRect.x + 4f, entryRect.y + 38f, entryRect.width - 8f, 16f),
                            string.Format("  需工质 {0:F0} kg · 燃烧 {1:F0} s · 最大可达 ΔV {2:F0} m/s",
                                est.RequiredPropellant, est.BurnSeconds, est.MaxDeltaV));
                    }
                    else if (est.Networks == null || est.Networks.Count == 0)
                    {
                        GUI.color = Color.red;
                        Widgets.Label(new Rect(entryRect.x + 4f, entryRect.y + 38f, entryRect.width - 8f, 16f),
                            "  未启用任何工质网络，无法执行");
                        GUI.color = Color.white;
                    }
                    else
                    {
                        GUI.color = Color.red;
                        NetworkEstimate badNet = est.Networks.Find(ne => !ne.Feasible);
                        if (badNet.Propellant != null)
                            Widgets.Label(new Rect(entryRect.x + 4f, entryRect.y + 38f, entryRect.width - 8f, 16f),
                                string.Format("  工质不足：网络「{0}」需 {1:F0} kg（当前 {2:F0}）",
                                    badNet.Propellant.label, badNet.RequiredPropellant, badNet.Available));
                        else
                            Widgets.Label(new Rect(entryRect.x + 4f, entryRect.y + 38f, entryRect.width - 8f, 16f),
                                string.Format("  工质不足：总需 {0:F0} kg · 最大可达 ΔV {1:F0} m/s",
                                    est.RequiredPropellant, est.MaxDeltaV));
                        GUI.color = Color.white;
                    }

                    if (i < count - 1)
                    {
                        // 分隔线
                        GUI.color = new Color(0.3f, 0.3f, 0.4f, 0.5f);
                        Widgets.DrawLineHorizontal(entryRect.x + 4f, entryRect.yMax, entryRect.width - 8f);
                        GUI.color = Color.white;
                    }
                }

                Widgets.EndScrollView();
                y += listRect.height + 6f;
            }

            // 清除所有
            if (Widgets.ButtonText(new Rect(0f, y, inRect.width * 0.48f, ButtonHeight), "清除所有"))
            {
                group.ClearManeuvers();
            }

            // 关闭
            if (Widgets.ButtonText(new Rect(inRect.width * 0.52f, y, inRect.width * 0.48f, ButtonHeight), "关闭"))
            {
                Close();
            }
        }
    }
}
