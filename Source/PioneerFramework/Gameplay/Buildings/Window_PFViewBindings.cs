using System.Linq;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    internal sealed class Window_PFViewBindings : Window
    {
        private readonly PioneerFrameworkLiteSettings settings;
        private SteeringBindingProfile profile;
        private Vector2 scroll;
        private SteeringAction? editing;
        private SteeringCapture? capture;
        private SteeringChord? pending;
        private string notice = "";

        public override Vector2 InitialSize => new(Mathf.Min(760f, UI.screenWidth - 32f), Mathf.Min(650f, UI.screenHeight - 32f));

        internal Window_PFViewBindings(PioneerFrameworkLiteSettings settings)
        {
            this.settings = settings;
            profile = SteeringBindingProfile.Restore(settings.pfviewBindings.Serialize());
            if (settings.pfviewBindings.Issues.Count > 0)
                notice = "PFV_Pilot_ConfigNormalized".Translate(settings.pfviewBindings.Issues.Count);
            doCloseX = true;
            closeOnCancel = false;
            absorbInputAroundWindow = true;
            preventCameraMotion = true;
            draggable = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Window_Steering.ClearHeldKeys();
            ReadCapture();
            Text.Font = GameFont.Medium;
            Widgets.Label(new(0, 0, inRect.width, 30), "PFV_Pilot_Bindings".Translate());
            Text.Font = GameFont.Small;
            string status = capture != null ? (capture.WaitingRelease ? "PFV_Pilot_ReleaseFirst" : "PFV_Pilot_PressKey").Translate() : notice;
            Widgets.Label(new(0, 32, inRect.width, 50), status);
            var outer = new Rect(0, 86, inRect.width, Mathf.Max(30, inRect.height - 190));
            var nativeKeys = PFViewBindingUtility.NativeTimeKeys().ToArray();
            var content = new Rect(0, 0, outer.width - 18, SteeringBindingProfile.Actions.Length * 36f + nativeKeys.Length * 22f);
            Widgets.BeginScrollView(outer, ref scroll, content);
            float y = 0;
            foreach (var action in SteeringBindingProfile.Actions)
            {
                float labelW = Mathf.Max(100, content.width * .32f);
                float bindW = Mathf.Max(70, content.width * .22f);
                float buttonsX = labelW + bindW;
                float buttonW = Mathf.Max(25, (content.width - buttonsX - 8) / 3);
                Widgets.Label(new(0, y + 4, labelW - 4, 28), PFViewBindingUtility.ActionLabel(action));
                var bindingRect = new Rect(labelW, y + 4, bindW - 4, 28);
                string? nativeConflict = PFViewBindingUtility.NativeConflict(profile[action]);
                Widgets.Label(bindingRect, (nativeConflict != null ? "! " : "") + PFViewBindingUtility.Label(profile[action]));
                if (nativeConflict != null) TooltipHandler.TipRegion(bindingRect, "PFV_Pilot_NativeConflict".Translate(nativeConflict));
                if (Widgets.ButtonText(new(buttonsX, y, buttonW, 28), "PFV_Pilot_Edit".Translate()))
                { editing = action; capture = new(); pending = null; notice = ""; }
                if (Widgets.ButtonText(new(buttonsX + buttonW + 2, y, buttonW, 28), "PFV_Pilot_Clear".Translate())) TryEdit(action, default);
                if (Widgets.ButtonText(new(buttonsX + 2 * (buttonW + 2), y, buttonW, 28), "PFV_Pilot_Reset".Translate())) TryEdit(action, SteeringBindingProfile.Default(action));
                y += 36;
            }
            foreach (var native in nativeKeys)
            {
                Widgets.Label(new(0, y, content.width, 22), native.Source + ": " + PFViewBindingUtility.Label(new(native.Key)));
                y += 22;
            }
            Widgets.EndScrollView();
            float bottom = inRect.height - 96;
            if (pending.HasValue && editing.HasValue)
            {
                if (Widgets.ButtonText(new(0, bottom, inRect.width * .48f, 28), "PFV_Pilot_Swap".Translate()))
                {
                    var staged = SteeringBindingProfile.Restore(profile.Serialize());
                    if (staged.TrySet(editing.Value, pending.Value, out _, true) && ValidateNative(staged))
                    { profile = staged; Commit(); notice = ""; }
                    else notice = "PFV_Pilot_Overlap".Translate();
                    pending = null; editing = null;
                }
                if (Widgets.ButtonText(new(inRect.width * .52f, bottom, inRect.width * .48f, 28), "PFV_Pilot_Cancel".Translate()))
                { pending = null; editing = null; notice = ""; }
            }
            if (Widgets.ButtonText(new(0, inRect.height - 56, inRect.width * .65f, 30), "PFV_Pilot_ResetAll".Translate()))
            {
                var defaults = new SteeringBindingProfile();
                if (ValidateNative(defaults)) { profile = defaults; Commit(); notice = ""; capture = null; pending = null; editing = null; }
            }
            if (Widgets.ButtonText(new(inRect.width * .70f, inRect.height - 56, inRect.width * .30f, 30), "PFV_Close".Translate())) Close();
        }

        private void ReadCapture()
        {
            var e = Event.current;
            if (e == null) return;
            if (capture == null)
            {
                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { Close(); e.Use(); }
                return;
            }
            var chord = capture.Update(PFViewBindingUtility.HeldKeys(), (int)e.keyCode,
                e.type == EventType.KeyDown, e.type == EventType.KeyUp, PFViewBindingUtility.Modifiers);
            if (capture.Cancelled) { capture = null; editing = null; notice = ""; }
            else if (chord.HasValue && editing.HasValue) { var action = editing.Value; capture = null; TryEdit(action, chord.Value); }
            if (e.type == EventType.KeyDown || e.type == EventType.KeyUp) e.Use();
        }

        private void TryEdit(SteeringAction action, SteeringChord chord)
        {
            capture = null; pending = null;
            if (PFViewBindingUtility.NativeConflict(chord) is { } source)
            { notice = "PFV_Pilot_NativeConflict".Translate(source); return; }
            if (profile.TrySet(action, chord, out var conflict)) { Commit(); notice = ""; editing = null; }
            else if (conflict.HasValue)
            { editing = action; pending = chord; notice = "PFV_Pilot_Conflict".Translate(PFViewBindingUtility.ActionLabel(conflict.Value)); }
            else notice = "PFV_Pilot_Overlap".Translate();
        }

        private bool ValidateNative(SteeringBindingProfile candidate)
        {
            foreach (var action in SteeringBindingProfile.Actions)
                if (PFViewBindingUtility.NativeConflict(candidate[action]) is { } source)
                { notice = "PFV_Pilot_NativeConflict".Translate(source); return false; }
            return true;
        }

        private void Commit() { settings.SetPilotBindings(profile); settings.Write(); }
    }
}
