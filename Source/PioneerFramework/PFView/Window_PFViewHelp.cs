using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 操作说明（2026-10-18 视图系统 v2）：键位 / 状态语义 / 取景限制 / 记号图例。
    /// 由工具条「?」打开；absorbInputAroundWindow=true ⇒ 打开时相机输入自然让位。
    /// </summary>
    public class Window_PFViewHelp : Window
    {
        public override Vector2 InitialSize => new Vector2(Mathf.Min(680f, UI.screenWidth - 48f), Mathf.Min(560f, UI.screenHeight - 48f));

        public Window_PFViewHelp()
        {
            doCloseX = true;
            closeOnCancel = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = true;
            resizeable = false;
            preventCameraMotion = true;
        }

        private Vector2 scroll;
        private float contentHeight = 720f;

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 32), "PFV_HelpTitle".Translate());
            Text.Font = GameFont.Small;
            var outer = new Rect(0, 40, inRect.width, Mathf.Max(1, inRect.height - 82));
            var content = new Rect(0, 0, outer.width - 18, contentHeight);
            Widgets.BeginScrollView(outer, ref scroll, content);
            float y = 0;
            foreach (string name in new[] { "Camera", "CameraModes", "Tasks", "Subjects", "Time", "Known", "Limits" })
            {
                Widgets.Label(new Rect(0, y, content.width, 26), ("PFV_Help" + name + "Title").Translate());
                y += 28;
                string body = ("PFV_Help" + name).Translate();
                float height = Text.CalcHeight(body, content.width);
                Widgets.Label(new Rect(0, y, content.width, height), body);
                y += height + 16;
            }
            contentHeight = Mathf.Max(outer.height, y);
            Widgets.EndScrollView();
            if (Widgets.ButtonText(new Rect(inRect.center.x - 60, inRect.yMax - 32, 120, 28), "PFV_Close".Translate())) Close();
        }
    }
}
