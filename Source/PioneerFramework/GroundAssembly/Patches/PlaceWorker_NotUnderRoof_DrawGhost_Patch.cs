using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly.Patches
{
    /// <summary>
    /// 为回收第一级（PlaceWorker_NotUnderRoof）在总装发射台中心绘制放置引导。
    /// </summary>
    public static class PlaceWorker_NotUnderRoof_DrawGhost_Patch
    {
        private static readonly Color ValidCenterColor = new(0f, 1f, 0.2f, 0.6f);
        private static readonly Color InvalidCenterColor = new(1f, 0.2f, 0.2f, 0.6f);

        [HarmonyPatch(typeof(PlaceWorker), "DrawGhost")]
        public static class PlaceWorker_DrawGhost_Patch
        {
            public static void Postfix(PlaceWorker __instance, ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null!)
            {
                if (!(__instance is PlaceWorker_NotUnderRoof))
                    return;

                if (!LaunchPadUtility.IsReturnedBooster(def))
                    return;

                if (Find.CurrentMap == null)
                    return;

                Building? launchpad = null;
                foreach (Thing t in Find.CurrentMap.thingGrid.ThingsListAt(center))
                {
                    if (t is Building b && LaunchPadUtility.IsAssemblyPad(b.def))
                    {
                        launchpad = b;
                        break;
                    }
                }

                if (launchpad == null)
                    return;

                bool canPlace = LaunchPadUtility.IsWithinBuildableArea(launchpad, center, def, rot)
                    && LaunchPadUtility.CanRebuildOnPad(def, launchpad.def);
                Color markerColor = canPlace ? ValidCenterColor : InvalidCenterColor;
                Vector3 drawPos = center.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
                Material mat = SolidColorMaterials.SimpleSolidColorMaterial(markerColor);
                Vector3 scale = new Vector3(1f, 1f, 1f);
                Matrix4x4 matrix = Matrix4x4.TRS(drawPos, Quaternion.identity, scale);
                Graphics.DrawMesh(MeshPool.plane10, matrix, mat, 0);
            }
        }
    }
}
