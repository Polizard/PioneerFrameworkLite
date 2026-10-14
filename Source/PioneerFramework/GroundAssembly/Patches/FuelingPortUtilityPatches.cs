using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly.Patches
{
    /// <summary>
    /// FuelingPortUtility 补丁：让 PFL 总装发射台返回中心格作为燃料口。
    /// </summary>
    public static class FuelingPortUtilityPatches
    {
        [HarmonyPatch(typeof(FuelingPortUtility), "GetFuelingPortCell", [typeof(Building)])]
        public static class GetFuelingPortCell_Building_Patch
        {
            public static bool Prefix(Building podLauncher, ref IntVec3 __result)
            {
                if (LaunchPadUtility.IsAssemblyPad(podLauncher.def))
                {
                    __result = podLauncher.OccupiedRect().CenterCell;
                    return false;
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(FuelingPortUtility), "GetFuelingPortCell", [typeof(IntVec3), typeof(Rot4)])]
        public static class GetFuelingPortCell_IntVec3Rot4_Patch
        {
            public static void Postfix(IntVec3 center, Rot4 rot, ref IntVec3 __result)
            {
                Map map = Find.CurrentMap;
                if (map == null)
                    return;

                Building? pad = map.thingGrid.ThingsListAt(center)
                    .OfType<Building>()
                    .FirstOrDefault(b => LaunchPadUtility.IsAssemblyPad(b.def));
                if (pad != null)
                {
                    __result = pad.OccupiedRect().CenterCell;
                }
            }
        }

        [HarmonyPatch(typeof(FuelingPortUtility), "FuelingPortGiverAtFuelingPortCell")]
        public static class FuelingPortGiverAtFuelingPortCell_Patch
        {
            public static void Postfix(IntVec3 c, Map map, ref Building __result)
            {
                if (map == null || __result != null)
                {
                    return;
                }

                foreach (Thing thing in map.thingGrid.ThingsListAt(c))
                {
                    if (thing is Building building && LaunchPadUtility.IsAssemblyPad(building.def))
                    {
                        __result = building;
                        return;
                    }
                }
            }
        }
    }
}
