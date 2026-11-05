using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    internal static class PFViewBindingUtility
    {
        internal static readonly KeyCode[] KeyboardKeys = Enum.GetValues<KeyCode>()
            .Where(k => IsKeyboardKey((int)k)).Distinct().ToArray();

        internal static bool IsKeyboardKey(int key) => key == 0 || Enum.IsDefined(typeof(KeyCode), key)
            && new SteeringChord(key).IsValid && key != 0;

        internal static string ActionLabel(SteeringAction action) => ("PFV_Pilot_Action_" + action).Translate();

        internal static string Label(SteeringChord chord)
        {
            if (!chord.IsBound) return "PFV_Pilot_Unbound".Translate();
            string prefix = (chord.Modifiers.HasFlag(SteeringModifiers.Control) ? "Ctrl+" : "")
                + (chord.Modifiers.HasFlag(SteeringModifiers.Alt) ? "Alt+" : "")
                + (chord.Modifiers.HasFlag(SteeringModifiers.Shift) ? "Shift+" : "");
            return prefix + ((KeyCode)chord.Key).ToStringReadable();
        }

        internal static SteeringModifiers Modifiers =>
            (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? SteeringModifiers.Shift : 0)
            | (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ? SteeringModifiers.Control : 0)
            | (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ? SteeringModifiers.Alt : 0);

        internal static List<int> HeldKeys() => KeyboardKeys.Where(k => k != KeyCode.None && Input.GetKey(k)).Select(k => (int)k).ToList();

        internal static IEnumerable<(int Key, string Source)> NativeTimeKeys()
        {
            var defs = new[] { KeyBindingDefOf.TogglePause, KeyBindingDefOf.TimeSpeed_Normal, KeyBindingDefOf.TimeSpeed_Fast,
                KeyBindingDefOf.TimeSpeed_Superfast, KeyBindingDefOf.TimeSpeed_Ultrafast };
            for (int i = 0; i < defs.Length; i++)
            {
                var def = defs[i];
                if (def == null)
                {
                    yield return (i == 0 ? 32 : 48 + i, i == 0 ? "Pause" : "Time " + i);
                    continue;
                }
                if (KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(def, out var binding))
                {
                    if (binding.keyBindingA != KeyCode.None) yield return ((int)binding.keyBindingA, def.LabelCap);
                    if (binding.keyBindingB != KeyCode.None) yield return ((int)binding.keyBindingB, def.LabelCap);
                }
                else
                {
                    if (def.defaultKeyCodeA != KeyCode.None) yield return ((int)def.defaultKeyCodeA, def.LabelCap);
                    if (def.defaultKeyCodeB != KeyCode.None) yield return ((int)def.defaultKeyCodeB, def.LabelCap);
                }
            }
        }

        internal static string? NativeConflict(SteeringChord chord)
        {
            if (!chord.IsBound || chord.Modifiers != 0) return null;
            foreach (var native in NativeTimeKeys()) if (chord.Key == native.Key) return native.Source;
            return null;
        }
    }
}
