using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>超小逻辑视口的仪表入口；复用现有仪表绘制，不复制输入或宿主控制状态。</summary>
    internal sealed class Window_PFViewInstrument : Window
    {
        private readonly MainTabWindow_PFView owner;
        private readonly bool radar;
        private Vector2 scroll;
        public override Vector2 InitialSize => new(Mathf.Min(radar ? 300f : 560f, UI.screenWidth - 32f), 320f);

        public Window_PFViewInstrument(MainTabWindow_PFView owner, bool radar)
        {
            this.owner = owner;
            this.radar = radar;
            doCloseX = true;
            draggable = true;
            closeOnCancel = true;
            preventCameraMotion = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (!ReferenceEquals(Find.WindowStack?.WindowOfType<MainTabWindow_PFView>(), owner)) { Close(); return; }
            // 保留球体与读数逻辑尺寸；更小窗口提供滚动，不截断两侧信息。
            var content = new Rect(0, 0, Mathf.Max(inRect.width - 18f, radar ? 260f : 522f), 280f);
            Widgets.BeginScrollView(inRect, ref scroll, content);
            var rect = radar ? new Rect(0, 22, 260, 260)
                : new Rect(0, 28, content.width, PFViewUiLayoutCore.BallSize);
            owner.DrawDetachedInstrument(radar, rect);
            Widgets.EndScrollView();
        }
    }
}
