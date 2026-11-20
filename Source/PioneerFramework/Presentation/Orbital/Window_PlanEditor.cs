using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **机动规划窗（独立窗口形态）**：内容在 <see cref="PlanEditorView" />。
    ///
    /// 2026-11-20：视图抽出后本类只剩窗口壳（尺寸/暂停/拖动）。
    /// 玩家主入口已改为「机动规划窗」（`ManeuverPlanner`）的第一个页签；
    /// 本窗口仍保留，因为验收台（`PlanEditorAcceptanceRunner`）按"独立规划窗"验收它。
    /// </summary>
    public class Window_PlanEditor : Window
    {
        private readonly PlanEditorView view;

        public Window_PlanEditor(int targetGroupId)
        {
            view = new PlanEditorView(targetGroupId);
            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
        }

        public override Vector2 InitialSize => new(780f, 560f);

        public override void DoWindowContents(Rect inRect) => view.Draw(inRect);
    }
}
