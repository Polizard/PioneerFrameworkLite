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
                // 2026-10-15 修：原只用 `Find.CurrentMap` ⇒ 在**非当前地图**上拆台时找不到台
                //（vanilla 唯一调用者 = `Building.DeSpawn`，它传 Position/Rotation、不带 Map）。
                // 现在先查当前图（常见路径），找不到再扫其余已载入地图——该重载没有 Map 参数，
                // 而"格子上有总装台"足以唯一确定；此路径稀有，代价可忽略。
                Building? pad = FindAssemblyPadAt(Find.CurrentMap, center);
                if (pad == null)
                {
                    foreach (Map m in Find.Maps)
                    {
                        if (ReferenceEquals(m, Find.CurrentMap))
                            continue;
                        pad = FindAssemblyPadAt(m, center);
                        if (pad != null)
                            break;
                    }
                }

                if (pad != null)
                {
                    __result = pad.OccupiedRect().CenterCell;
                }
            }

            private static Building? FindAssemblyPadAt(Map? map, IntVec3 center)
                => map == null
                    ? null
                    : map.thingGrid.ThingsListAt(center)
                        .OfType<Building>()
                        .FirstOrDefault(b => LaunchPadUtility.IsAssemblyPad(b.def));
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
