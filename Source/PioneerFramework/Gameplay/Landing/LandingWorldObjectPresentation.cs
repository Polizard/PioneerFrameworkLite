using System;
using System.Linq;
using HarmonyLib;
using RimWorld.Planet;

namespace PioneerFramework.Landing
{
    [HarmonyPatch(typeof(ExpandableWorldObjectsUtility), nameof(ExpandableWorldObjectsUtility.TransitionPct))]
    internal static class LandingWorldObjectPresentation
    {
        internal static string JoinInspectLines(params string?[] sections) => string.Join("\n", sections
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .SelectMany(s => s!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim()).Where(line => line.Length > 0));

        private static bool IsLandingMarker(Type type) => type == typeof(LandingTransitWorldObject) ||
            type == typeof(LandedGroupWorldObject) || type == typeof(LandedShipMapParent);

        internal static float ExpandedTransition(Type type, bool enabled, bool hiddenByRules, float original) =>
            enabled && !hiddenByRules && IsLandingMarker(type) ? 1f : original;

        // Native expanded icons use DrawPos for both rendering and mouse picking, including moving transits.
        private static void Postfix(WorldObject wo, ref float __result)
        {
            if (!IsLandingMarker(wo.GetType())) return;
            __result = ExpandedTransition(wo.GetType(), wo.def.expandingIcon,
                ExpandableWorldObjectsUtility.HiddenByRules(wo), __result);
        }
    }
}
