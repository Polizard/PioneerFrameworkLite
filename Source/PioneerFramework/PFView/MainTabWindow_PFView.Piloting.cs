using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private void DrawPilotPanel()
        {
            if (!SteeringController.IsOpen || rPilotPanel.width <= 0) return;
            var group = Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(SteeringController.GroupId);
            if (group == null) return;
            var rect = rPilotPanel;
            Widgets.DrawBoxSolid(rect, PFViewPalette.PanelBg);
            Text.Font = GameFont.Small;
            string mode = (SteeringController.AssistedStop ? "PFV_Pilot_Assisted" : "PFV_Pilot_Inertial").Translate();
            string heading = "G" + group.GroupId + " · " + Window_Steering.PrecisionStatus() + " · " + mode
                + " · " + Window_Steering.SasLabel(SteeringController.SasKind);
            var headingRect = new Rect(rect.x + 6, rect.y + 2, rect.width - 12, 20);
            Text.Font = rect.width < 450 ? GameFont.Tiny : GameFont.Small;
            Widgets.Label(headingRect, heading);
            TooltipHandler.TipRegion(headingRect, heading + "\n" + Window_Steering.PrecisionHelp());
            Text.Font = GameFont.Small;
            Widgets.Label(new(rect.x + 6, rect.y + 22, rect.width - 12, 20),
                "PFV_Pilot_ActualThrust".Translate((SteeringController.ActualThrust / 1000f).ToString("F1"))
                + " · " + "PFV_Pilot_Spin".Translate((group.angularVelocity.magnitude * Mathf.Rad2Deg).ToString("F1")));
            float width = (rect.width - 16) / 3;
            if (Widgets.ButtonText(new(rect.x + 4, rect.y + 44, width, 26), "PFV_Pilot_Focus".Translate()))
                FocusGroup(group.GroupId);
            if (Widgets.ButtonText(new(rect.x + 8 + width, rect.y + 44, width, 26), "PFV_Pilot_Details".Translate()))
            {
                ResetCameraDrag();
                if (FindSteeringWindow() == null) Find.WindowStack.Add(new Window_Steering(false, false));
            }
            if (Widgets.ButtonText(new(rect.x + 12 + 2 * width, rect.y + 44, width, 26), "PFV_EndSteering".Translate()))
                EndSteering();
            if (rect.height >= 104) DrawPilotDirections(new(rect.x + 6, rect.y + 76, rect.width - 12, 28), group);
            if (SteeringController.ThrustUnavailable)
                TooltipHandler.TipRegion(rect, "PFV_Pilot_NoThrust".Translate());
            if (SteeringController.TakeoverNotice)
            {
                SteeringController.TakeoverNotice = false;
                QueueToast("PFV_Pilot_Takeover".Translate());
            }
        }

        private void DrawPilotDirections(Rect rect, SpaceEntityGroup group)
        {
            if (renderCamera == null) return;
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            if (space == null) return;
            Quaternion toCamera = Quaternion.Inverse(renderCamera.transform.rotation) * viewFrameRot;
            var directions = new[] { group.GetThrustAxisSystem(ThrustAxis.Forward),
                space.GetMainThrustAxisSystem(group), group.CurrentOrbit.velocitySystem.v };
            string[] labels = { "F", "T", "V" };
            float width = rect.width / 3;
            for (int i = 0; i < 3; i++)
            {
                var cell = new Rect(rect.x + i * width, rect.y, width, rect.height);
                Widgets.Label(cell, labels[i]);
                Vector2 center = new(cell.x + width * .55f, cell.center.y);
                Vector3 local = toCamera * (Vector3)directions[i].normalized;
                Vector2 arrow = new(local.x, -local.y);
                if (arrow.magnitude < .12f)
                    Widgets.Label(new(center.x - 8, center.y - 11, 24, 24),
                        directions[i].sqrMagnitude <= 1e-12 ? "—" : local.z >= 0 ? "⊗" : "⊙");
                else
                {
                    Vector2 tip = center + arrow.normalized * 10;
                    Widgets.DrawLine(center, tip, PFViewPalette.Accent, 1.5f);
                    Vector2 back = -arrow.normalized * 4;
                    Vector2 side = new(-back.y, back.x);
                    Widgets.DrawLine(tip, tip + back + side * .6f, PFViewPalette.Accent, 1.5f);
                    Widgets.DrawLine(tip, tip + back - side * .6f, PFViewPalette.Accent, 1.5f);
                }
            }
            TooltipHandler.TipRegion(rect, "PFV_Pilot_Directions".Translate());
        }
    }
}
