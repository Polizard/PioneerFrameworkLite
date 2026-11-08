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
            bool narrow = rect.width < 380f;
            DrawPilotReadouts(rect, group, narrow);

            const float pad = 6f, gap = 4f;
            float directionWidth = rect.width >= 460f ? 136f : 0;
            float actionsWidth = rect.width - 2 * pad - directionWidth - (narrow ? 112f : 0f);
            bool showReturn = CanReturnToPilotShip(group.GroupId);
            float buttonWidth = Mathf.Min(156f, (actionsWidth - gap) * .5f);
            float x = rect.x + pad, y = rect.y + 24f;
            Text.Font = narrow ? GameFont.Tiny : GameFont.Small;
            if (showReturn)
            {
                if (CockpitButton(new(x, y, buttonWidth, 26), (narrow ? "PFV_Focus" : "PFV_Pilot_Focus").Translate(),
                    tip: "PFV_Pilot_Focus".Translate()))
                    FocusGroup(group.GroupId);
                x += buttonWidth + gap;
            }
            if (Widgets.ButtonText(new(x, y, buttonWidth, 26), "PFV_Pilot_Details".Translate()))
                OpenPilotDetails();
            if (directionWidth > 0)
                DrawPilotDirections(new(rect.xMax - directionWidth, y, directionWidth - pad, 26), group);
            Text.Font = GameFont.Small;
            if (SteeringController.ThrustUnavailable)
                TooltipHandler.TipRegion(rect, "PFV_Pilot_NoThrust".Translate());
            if (SteeringController.TakeoverNotice)
            {
                SteeringController.TakeoverNotice = false;
                QueueToast("PFV_Pilot_Takeover".Translate());
            }
        }

        private static void DrawPilotReadouts(Rect rect, SpaceEntityGroup group, bool narrow)
        {
            string mode = (SteeringController.AssistedStop ? "PFV_Pilot_Assisted" : "PFV_Pilot_Inertial").Translate();
            string heading = "G" + group.GroupId + " · " + Window_Steering.PrecisionStatus() + " · " + mode
                + " · " + Window_Steering.SasLabel(SteeringController.SasKind);
            string thrust = (SteeringController.ActualThrust / 1000f).ToString("F1");
            string rate = (group.angularVelocity.magnitude * Mathf.Rad2Deg).ToString("F1");
            string fullReadouts = "PFV_Pilot_ActualThrust".Translate(thrust) + " · " + "PFV_Pilot_Spin".Translate(rate);
            string readouts = rect.width >= 520f ? fullReadouts : $"T {thrust} kN · ω {rate}°/s";
            float readoutWidth = narrow ? 108f : Mathf.Clamp(rect.width * .42f, 116f, 270f);
            float headingWidth = rect.width - (narrow ? 12f : readoutWidth + 16f);
            var headingRect = new Rect(rect.x + 6, rect.y + 2, headingWidth, 20);
            var readoutRect = new Rect(rect.xMax - readoutWidth - 6, rect.y + (narrow ? 27f : 2f), readoutWidth, 20);
            bool previousWrap = Text.WordWrap;
            Text.WordWrap = false;
            MiniLabel(headingRect, heading, PFViewPalette.TextPrimary);
            MiniLabel(readoutRect, readouts, PFViewPalette.TextSecondary, TextAnchor.MiddleRight);
            Text.WordWrap = previousWrap;
            TooltipHandler.TipRegion(headingRect, heading + "\n" + Window_Steering.PrecisionHelp());
            TooltipHandler.TipRegion(readoutRect, fullReadouts);
        }

        private void OpenPilotDetails()
        {
            if (!SteeringController.IsOpen) return;
            ResetCameraDrag();
            if (FindSteeringWindow() == null) Find.WindowStack.Add(new Window_Steering(false, false));
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
