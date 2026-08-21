using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly.Patches
{
    /// <summary>
    /// PlaceWorker_FuelingPort 补丁：PFL 总装发射台燃料口幽灵绘制在中心点。
    /// </summary>
    public static class PlaceWorker_FuelingPortPatches
    {
        [HarmonyPatch(typeof(PlaceWorker_FuelingPort), "DrawGhost")]
        [StaticConstructorOnStartup]
        public static class DrawGhost_Patch
        {
            private static readonly Material FuelingPortCellMaterial =
                MaterialPool.MatFrom("UI/Overlays/FuelingPort", ShaderDatabase.Transparent);

            public static bool Prefix(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null!)
            {
                if (!LaunchPadUtility.IsAssemblyPad(def))
                    return true;

                Vector3 drawPos = center.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
                if (FuelingPortCellMaterial != null)
                {
                    Graphics.DrawMesh(MeshPool.plane10, drawPos, Quaternion.identity, FuelingPortCellMaterial, 0);
                }

                return false;
            }
        }
    }
}
