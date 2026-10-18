using System.Linq;
using UnityEngine;
using Verse;
namespace PioneerFramework.Landing
{
    public sealed class Window_LandingPlacement : Window
    {
        private readonly LandingTaskData task;
        public string Reason = "";
        public Window_LandingPlacement(LandingTaskData task)
        {
            this.task = task; forcePause = true; absorbInputAroundWindow = false; preventCameraMotion = false;
            closeOnClickedOutside = false; closeOnCancel = true; doCloseX = true; draggable = true;
        }
        public override Vector2 InitialSize => new Vector2(System.Math.Min(340, UI.screenWidth - 24), 180);
        protected override void SetInitialSizeAndPosition() { base.SetInitialSizeAndPosition(); windowRect.x = UI.screenWidth - windowRect.width - 14; windowRect.y = 80; }
        public override void DoWindowContents(Rect inRect)
        {
            Widgets.Label(new Rect(0, 0, inRect.width - 25, 30), "PFL_LandingPlacementTitle".Translate(task.SourceMaps.Count(s => s.Status != LandingMapStatus.Pending) + 1, task.SourceMaps.Count));
            Widgets.Label(new Rect(0, 34, inRect.width, 100), Reason);
        }
        public override void PreClose() { Find.Targeter.StopTargeting(); base.PreClose(); }
    }
}
