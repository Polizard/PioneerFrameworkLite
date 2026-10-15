using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly.Patches
{
    /// <summary>
    /// PlaceWorker_NeedsFuelingPort 补丁：PFL 火箭/返回舱必须放在总装发射台可建造区域内。
    /// </summary>
    public static class PlaceWorker_NeedsFuelingPortPatches
    {
        [HarmonyPatch(typeof(PlaceWorker_NeedsFuelingPort), "AllowsPlacing")]
        public static class AllowsPlacing_Patch
        {
            public static bool Prefix(BuildableDef def, IntVec3 center, Rot4 rot, Map map,
                Thing thingToIgnore, Thing thing, ref AcceptanceReport __result)
            {
                if (!(def is ThingDef thingDef))
                    return true;

                Building fuelingPortGiver = FuelingPortUtility.FuelingPortGiverAtFuelingPortCell(center, map);
                if (fuelingPortGiver == null)
                    return true;

                if (LaunchPadUtility.IsVanillaPodLauncher(fuelingPortGiver.def))
                {
                    if (LaunchPadUtility.IsRocket(thingDef) || LaunchPadUtility.IsReturnedBooster(thingDef))
                    {
                        __result = new AcceptanceReport("PFL 火箭/回收第一级不能放在原版运输舱发射器上。");
                        return false;
                    }

                    return true;
                }

                if (LaunchPadUtility.IsAssemblyPad(fuelingPortGiver.def))
                {
                    if (LaunchPadUtility.IsReturnedBooster(thingDef))
                    {
                        if (!LaunchPadUtility.IsWithinBuildableArea(fuelingPortGiver, center, thingDef, rot))
                        {
                            __result = new AcceptanceReport("回收第一级必须放在总装发射台可建造区域内。");
                            return false;
                        }

                        if (!LaunchPadUtility.CanRebuildOnPad(thingDef, fuelingPortGiver.def))
                        {
                            __result = new AcceptanceReport("该总装发射台不支持重建此返回舱。");
                            return false;
                        }

                        if (IsOccupied(map, fuelingPortGiver, thingDef))
                        {
                            __result = new AcceptanceReport("总装发射台已被占用。");
                            return false;
                        }

                        __result = true;
                        return false;
                    }

                    if (LaunchPadUtility.IsRocket(thingDef))
                    {
                        if (!LaunchPadUtility.IsWithinBuildableArea(fuelingPortGiver, center, thingDef, rot))
                        {
                            __result = new AcceptanceReport("火箭必须放在总装发射台可建造区域内。");
                            return false;
                        }

                        if (!LaunchPadUtility.CanLaunchFromPad(thingDef, fuelingPortGiver.def))
                        {
                            __result = new AcceptanceReport("该总装发射台不支持此火箭。");
                            return false;
                        }

                        if (IsOccupied(map, fuelingPortGiver, thingDef))
                        {
                            __result = new AcceptanceReport("总装发射台已被占用。");
                            return false;
                        }

                        __result = true;
                        return false;
                    }

                    return true;
                }

                return true;
            }

            private static bool IsOccupied(Map map, Building pad, ThingDef placingDef)
            {
                CellRect area = LaunchPadUtility.GetBuildableArea(pad);
                foreach (IntVec3 cell in area.Cells)
                {
                    List<Thing> thingsAtCell = map.thingGrid.ThingsListAt(cell);
                    for (int i = 0; i < thingsAtCell.Count; i++)
                    {
                        Thing t = thingsAtCell[i];
                        if (t is Building && t.def != placingDef &&
                            (LaunchPadUtility.IsRocket(t.def) || LaunchPadUtility.IsReturnedBooster(t.def)))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        [HarmonyPatch(typeof(PlaceWorker_NeedsFuelingPort), "DrawGhost")]
        [StaticConstructorOnStartup]
        public static class DrawGhost_Patch
        {
            private static readonly Color ValidColor = new(0.2f, 1f, 0.2f, 0.5f);
            private static readonly Color InvalidColor = new(1f, 0.2f, 0.2f, 0.5f);
            private static readonly Material FuelingPortCellMaterial =
                MaterialPool.MatFrom("UI/Overlays/FuelingPort", ShaderDatabase.Transparent);

            public static bool Prefix(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null!)
            {
                if (!LaunchPadUtility.IsRocket(def) && !LaunchPadUtility.IsReturnedBooster(def))
                    return true;

                Map map = Find.CurrentMap;
                if (map == null)
                    return true;

                // 在所有 PFL 总装发射台中心绘制真实燃料口图标，固定不偏移。
                if (map.listerBuildings?.allBuildingsColonist != null)
                {
                    for (int i = 0; i < map.listerBuildings.allBuildingsColonist.Count; i++)
                    {
                        Building pad = map.listerBuildings.allBuildingsColonist[i];
                        if (pad == null || pad.Destroyed || !LaunchPadUtility.IsAssemblyPad(pad.def))
                            continue;

                        Vector3 iconPos = pad.OccupiedRect().CenterCell.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
                        if (FuelingPortCellMaterial != null)
                            Graphics.DrawMesh(MeshPool.plane10, iconPos, Quaternion.identity, FuelingPortCellMaterial, 0);
                    }
                }

                Building? launchpad = null;
                List<Thing> things = map.thingGrid.ThingsListAt(center);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] is Building b && LaunchPadUtility.IsAssemblyPad(b.def))
                    {
                        launchpad = b;
                        break;
                    }
                }

                if (launchpad == null)
                    return false;

                bool canPlace = LaunchPadUtility.IsWithinBuildableArea(launchpad, center, def, rot)
                    && (LaunchPadUtility.IsReturnedBooster(def)
                        ? LaunchPadUtility.CanRebuildOnPad(def, launchpad.def)
                        : LaunchPadUtility.CanLaunchFromPad(def, launchpad.def));

                Color color = canPlace ? ValidColor : InvalidColor;
                Vector3 drawPos = center.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
                Material mat = SolidColorMaterials.SimpleSolidColorMaterial(color);
                Graphics.DrawMesh(MeshPool.plane10, drawPos, Quaternion.identity, mat, 0);

                return false;
            }
        }
    }
}
