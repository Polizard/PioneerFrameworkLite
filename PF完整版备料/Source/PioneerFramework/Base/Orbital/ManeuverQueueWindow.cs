using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 显示群组的机动规划队列窗口。
    /// </summary>
    public class ManeuverQueueWindow : Window
    {
        private SpaceEntityGroup group;
        private Vector2 scrollPos;

        private const float EntryHeight = 50f;
        private const float ButtonHeight = 30f;
        private const float WindowWidth = 400f;
        private const float WindowHeight = 350f;

        public ManeuverQueueWindow(SpaceEntityGroup targetGroup)
        {
            group = targetGroup;
            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;

            // 确保窗口尺寸
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
                string.Format("共 {0} 个已规划机动", count));
            y += 26f;

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

                int currentTick = Find.TickManager.TicksGame;
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

                    // 编号 + 状态
                    string status = pm.active ? "待执行" : "已取消";
                    Widgets.Label(new Rect(entryRect.x + 4f, entryRect.y + 2f, entryRect.width - 8f, 18f),
                        string.Format("#{0}  {1}  |  {2}", i + 1, status, timeStr));

                    // dV 分量
                    Widgets.Label(new Rect(entryRect.x + 4f, entryRect.y + 20f, entryRect.width - 8f, 16f),
                        string.Format("  径向 {0:F0}  切向 {1:F0}  法向 {2:F0}  m/s",
                            pm.radialDV, pm.tangentialDV, pm.normalDV));

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

        public override void Close(bool doCloseSound = true)
        {
            base.Close(doCloseSound);
        }
    }
}