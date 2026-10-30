using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class PioneerFrameworkLiteSettings
    {
        internal bool pfviewAssistStop = true;
        internal bool pfviewInvertPitch;
        internal float pfviewPrecisionScale = .25f;
        internal bool pfviewPrecisionToggle;
        internal bool pfviewFollowRoll;
        internal bool pfviewInvertMouseY;
        internal bool pfviewInvertWheel;
        internal bool pfviewRecenter;
        internal float pfviewSensitivity = 1f;
        internal float pfviewFollowSmoothing = .10f;
        internal SteeringBindingProfile pfviewBindings = new();
        internal int pfviewBindingRevision;

        private void ExposePilotSettings()
        {
            Scribe_Values.Look(ref pfviewAssistStop, "pfviewAssistStop", true);
            Scribe_Values.Look(ref pfviewInvertPitch, "pfviewInvertPitch");
            Scribe_Values.Look(ref pfviewPrecisionScale, "pfviewPrecisionScale", .25f);
            Scribe_Values.Look(ref pfviewPrecisionToggle, "pfviewPrecisionToggle");
            Scribe_Values.Look(ref pfviewFollowRoll, "pfviewFollowRoll");
            Scribe_Values.Look(ref pfviewInvertMouseY, "pfviewInvertMouseY");
            Scribe_Values.Look(ref pfviewInvertWheel, "pfviewInvertWheel");
            Scribe_Values.Look(ref pfviewRecenter, "pfviewRecenter");
            Scribe_Values.Look(ref pfviewSensitivity, "pfviewSensitivity", 1f);
            Scribe_Values.Look(ref pfviewFollowSmoothing, "pfviewFollowSmoothing", .10f);
            List<string>? records = Scribe.mode == LoadSaveMode.Saving ? pfviewBindings.Serialize() : null;
            Scribe_Collections.Look(ref records, "pfviewPilotBindings", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                pfviewBindings = SteeringBindingProfile.Restore(records, PFViewBindingUtility.IsKeyboardKey);
                pfviewBindingRevision++;
            }
            NormalizePilotSettings();
        }

        internal void NormalizePilotSettings()
        {
            pfviewPrecisionScale = float.IsFinite(pfviewPrecisionScale) ? Math.Clamp(pfviewPrecisionScale, .05f, .50f) : .25f;
            pfviewSensitivity = float.IsFinite(pfviewSensitivity) ? Math.Clamp(pfviewSensitivity, .25f, 3f) : 1f;
            pfviewFollowSmoothing = float.IsFinite(pfviewFollowSmoothing) ? Math.Clamp(pfviewFollowSmoothing, 0f, .30f) : .10f;
        }

        internal void SetPilotBindings(SteeringBindingProfile profile)
        {
            pfviewBindings = SteeringBindingProfile.Restore(profile.Serialize(), PFViewBindingUtility.IsKeyboardKey);
            pfviewBindingRevision++;
        }
    }

    public partial class PioneerFrameworkLiteMod
    {
        private Vector2 pilotSettingsScroll;
        private float pilotSettingsHeight = 1100f;

        private static void DrawPilotSettings(Listing_Standard listing)
        {
            var settings = Settings!;
            listing.Gap(12f);
            listing.Label("PFV_Pilot_Settings".Translate());
            listing.CheckboxLabeled("PFV_Pilot_Assist".Translate(), ref settings.pfviewAssistStop);
            listing.CheckboxLabeled("PFV_Pilot_InvertPitch".Translate(), ref settings.pfviewInvertPitch);
            string precisionMode = (settings.pfviewPrecisionToggle ? "PFV_Pilot_PrecisionToggle" : "PFV_Pilot_PrecisionHold").Translate();
            if (Widgets.ButtonText(listing.GetRect(30f), "PFV_Pilot_PrecisionMode".Translate(precisionMode)))
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new("PFV_Pilot_PrecisionHold".Translate(), () => settings.pfviewPrecisionToggle = false),
                    new("PFV_Pilot_PrecisionToggle".Translate(), () => settings.pfviewPrecisionToggle = true)
                }));
            listing.Label("PFV_Pilot_Precision".Translate((settings.pfviewPrecisionScale * 100).ToString("F0")));
            settings.pfviewPrecisionScale = listing.Slider(settings.pfviewPrecisionScale, .05f, .50f);
            listing.CheckboxLabeled("PFV_Pilot_FollowRoll".Translate(), ref settings.pfviewFollowRoll);
            listing.CheckboxLabeled("PFV_Pilot_InvertMouse".Translate(), ref settings.pfviewInvertMouseY);
            listing.CheckboxLabeled("PFV_Pilot_InvertWheel".Translate(), ref settings.pfviewInvertWheel);
            listing.CheckboxLabeled("PFV_Pilot_Recenter".Translate(), ref settings.pfviewRecenter);
            listing.Label("PFV_Pilot_Sensitivity".Translate(settings.pfviewSensitivity.ToString("F2")));
            settings.pfviewSensitivity = listing.Slider(settings.pfviewSensitivity, .25f, 3f);
            listing.Label("PFV_Pilot_Smoothing".Translate(settings.pfviewFollowSmoothing.ToString("F2")));
            settings.pfviewFollowSmoothing = listing.Slider(settings.pfviewFollowSmoothing, 0f, .30f);
            listing.Label("PFV_Pilot_Activation".Translate());
            if (Widgets.ButtonText(listing.GetRect(32f), "PFV_Pilot_Bindings".Translate()))
                Find.WindowStack.Add(new Window_PFViewBindings(settings));
        }
    }
}
