using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>Single input owner for PFView and the console window; physics never polls driving keys.</summary>
    internal static class SteeringInputHost
    {
        private static readonly SteeringReleaseGate gate = new();
        private static SteeringBindingProfile activeProfile = new();
        private static PioneerFrameworkLiteSettings? settingsOwner;
        private static int revision = -1;
        private static int nativeSignature;
        private static SteeringModifiers currentModifiers;
        private static readonly SteeringPointerHold pointer = new();
        private static bool precisionLatched;

        internal static void Rearm() { gate.Rearm(); pointer.Cancel(); }
        internal static void ResetPrecision() => precisionLatched = false;

        internal static void UpdatePointer(bool held, bool enabled) => pointer.Update(held, enabled && SteeringController.IsOpen);
        internal static void BeginPointer(SteeringAction action) => pointer.Press(action);
        internal static void PublishPointer()
        {
            if (pointer.Action is { } action) SteeringController.HoldPointerAction(action);
        }

        internal static bool CanReadKeyboard()
        {
            if (!Application.isFocused) return false;
            var stack = Find.WindowStack;
            var view = stack?.WindowOfType<MainTabWindow_PFView>();
            if (view?.HasTextInputFocus == true) return false;
            var windows = stack?.Windows;
            if (windows == null || windows.Count == 0) return false;
            var foreground = windows[windows.Count - 1];
            if (foreground is Window_Steering) return SteeringController.IsOpen;
            return SteeringController.IsOpen && !SteeringController.FromPFView
                ? foreground is Window_Steering || view != null && ReferenceEquals(foreground, view)
                : view != null && ReferenceEquals(foreground, view);
        }

        private static void RefreshProfile()
        {
            var settings = PioneerFrameworkLiteMod.Settings;
            int signature = 17;
            var natives = PFViewBindingUtility.NativeTimeKeys().ToArray();
            foreach (var native in natives) signature = unchecked(signature * 31 + native.Key);
            int nextRevision = settings?.pfviewBindingRevision ?? 0;
            if (ReferenceEquals(settingsOwner, settings) && nextRevision == revision && signature == nativeSignature) return;
            settingsOwner = settings;
            revision = nextRevision;
            nativeSignature = signature;
            activeProfile = SteeringBindingProfile.Restore((settings?.pfviewBindings ?? new()).Serialize());
            foreach (var action in SteeringBindingProfile.Actions)
            {
                var chord = activeProfile[action];
                if (chord.Modifiers == 0 && natives.Any(n => n.Key == chord.Key))
                    activeProfile.TrySet(action, default, out _);
            }
            gate.Rearm();
        }

        internal static void Poll(bool allowed)
        {
            RefreshProfile();
            ProcessSnapshot(activeProfile, PFViewBindingUtility.HeldKeys(), PFViewBindingUtility.Modifiers,
                allowed && Application.isFocused);
        }

        internal static void ProcessSnapshot(SteeringBindingProfile profile, IEnumerable<int> held,
            SteeringModifiers modifiers, bool enabled)
        {
            activeProfile = profile;
            currentModifiers = modifiers;
            gate.Update(held, enabled);
            if (!activeProfile[SteeringAction.Precision].IsBound) precisionLatched = false;
            if (!enabled || !SteeringController.IsOpen)
            {
                pointer.Cancel();
                SteeringController.ClearHeldInput();
                return;
            }
            bool precisionKeyHeld = IsHeld(SteeringAction.Precision, false);
            if (SteeringController.PrecisionToggle && precisionKeyHeld && gate.TryPress(activeProfile[SteeringAction.Precision].Key))
                precisionLatched = !precisionLatched;
            SteeringController.PrecisionHeld = SteeringController.PrecisionToggle ? precisionLatched : precisionKeyHeld;
            // Ignore the precision chord's modifiers only while that chord is physically held.
            SteeringController.KeyPitchU = IsHeld(SteeringAction.PitchUp, precisionKeyHeld);
            SteeringController.KeyPitchD = IsHeld(SteeringAction.PitchDown, precisionKeyHeld);
            SteeringController.KeyYawL = IsHeld(SteeringAction.YawLeft, precisionKeyHeld);
            SteeringController.KeyYawR = IsHeld(SteeringAction.YawRight, precisionKeyHeld);
            SteeringController.KeyRollL = IsHeld(SteeringAction.RollLeft, precisionKeyHeld);
            SteeringController.KeyRollR = IsHeld(SteeringAction.RollRight, precisionKeyHeld);
            SteeringController.KeyForward = IsHeld(SteeringAction.Forward, precisionKeyHeld);
            SteeringController.KeyBack = IsHeld(SteeringAction.Back, precisionKeyHeld);
            SteeringController.KeyLeft = IsHeld(SteeringAction.Left, precisionKeyHeld);
            SteeringController.KeyRight = IsHeld(SteeringAction.Right, precisionKeyHeld);
            SteeringController.KeyUp = IsHeld(SteeringAction.Up, precisionKeyHeld);
            SteeringController.KeyDown = IsHeld(SteeringAction.Down, precisionKeyHeld);
        }

        private static bool IsHeld(SteeringAction action, bool precision)
        {
            var chord = activeProfile[action];
            return gate.IsHeld(chord.Key) && activeProfile.Matches(action, chord.Key, currentModifiers, precision);
        }

        internal static void EnsureInputSafety()
        {
            if (SteeringController.IsOpen && CanReadKeyboard()) return;
            gate.Update(PFViewBindingUtility.HeldKeys(), false);
            pointer.Update(Input.GetMouseButton(0), false);
            SteeringController.ClearHeldInput();
        }

        internal static bool TryShortcut(Event e, bool groups, out SteeringAction action)
        {
            action = default;
            if (e.type != EventType.KeyDown) return false;
            int key = (int)e.keyCode;
            if (gate.WasConsumed(key)) { e.Use(); return false; }
            foreach (var candidate in SteeringBindingProfile.Actions)
            {
                bool group = candidate >= SteeringAction.Group1;
                if (group != groups || group && !SteeringController.IsOpen
                    || SteeringBindingProfile.IsContinuous(candidate) || candidate == SteeringAction.Precision) continue;
                if (!activeProfile.Matches(candidate, key, currentModifiers, false) || !gate.TryPress(key)) continue;
                action = candidate;
                e.Use();
                return true;
            }
            return false;
        }

        internal static bool HandleGroupShortcut(Event e)
        {
            if (!TryShortcut(e, true, out var action)) return false;
            var group = Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(SteeringController.GroupId);
            var map = group?.GetLinkedMapComp();
            if (map != null) map.armedGroupsMask ^= 1 << (action - SteeringAction.Group1);
            return true;
        }

        internal static bool AcceptNativeTimeKey(Event e)
            => e.type == EventType.KeyDown && (currentModifiers & ~SteeringChord.ModifierFor((int)e.keyCode)) == 0
                && gate.TryPress((int)e.keyCode);

        internal static string Label(SteeringAction action)
            => PFViewBindingUtility.Label((PioneerFrameworkLiteMod.Settings?.pfviewBindings ?? new())[action]);
    }
}
