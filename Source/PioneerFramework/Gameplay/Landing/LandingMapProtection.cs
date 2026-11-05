using System.Linq;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Landing
{
    [HarmonyPatch(typeof(MapParent), "CheckRemoveMapNow")]
    internal static class LandingMapProtection
    {
        internal static bool Prefix(MapParent __instance)
        {
            if (!__instance.HasMap || Current.Game == null) return true;
            int id = __instance.Map.uniqueID;
            return !Current.Game.GetComponent<LandingTransitManager>().Tasks.Any(t => t.Status != LandingTransitStatus.Completed &&
                (t.DestinationMapId == id || t.SourceMaps.Any(s => s.MapId == id && s.Status != LandingMapStatus.Cleaned)));
        }
    }
}
