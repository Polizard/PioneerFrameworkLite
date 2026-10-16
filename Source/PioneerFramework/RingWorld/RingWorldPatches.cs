using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    public static class RingWorldPatches
    {
        private static bool installed;
        public static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("PioneerFrameworkLite.RingWorld");
            try
            {
            RingWorldMotion.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(PlanetLayer), nameof(PlanetLayer.InitializeLayer)),
                prefix: new HarmonyMethod(typeof(RingWorldPatches), nameof(Initialize)));
            harmony.Patch(AccessTools.Method(typeof(PlanetLayer), nameof(PlanetLayer.ExposeData)),
                prefix: new HarmonyMethod(typeof(RingWorldPatches), nameof(Expose)));
            Patch(harmony, typeof(PlanetLayer), "GetTileCenter", nameof(Center), new[]{typeof(int)});
            Patch(harmony, typeof(PlanetLayer), "ApproxDistanceInTiles", nameof(Distance), new[]{typeof(int),typeof(int)});
            Patch(harmony, typeof(PlanetLayer), "GetHeadingFromTo", nameof(Heading), new[]{typeof(int),typeof(int)});
            Patch(harmony, typeof(PlanetLayer), "LongLatOf", nameof(LongLat), new[]{typeof(int)});
            harmony.Patch(AccessTools.Method(typeof(PlanetLayer),"LineIntersects"),prefix:new HarmonyMethod(typeof(RingWorldPatches),nameof(LineIntersects)));
            harmony.Patch(AccessTools.Method(typeof(WorldPath),"DrawPath"),prefix:new HarmonyMethod(typeof(RingWorldObjects),"DrawPath"));
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.Visible)),prefix:new HarmonyMethod(typeof(RingWorldPatches),nameof(Visible)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer), nameof(PlanetLayer.Raycastable)),prefix:new HarmonyMethod(typeof(RingWorldPatches),nameof(Raycastable)));
            harmony.Patch(AccessTools.Method(typeof(WorldPathing),"FindPath"),prefix:new HarmonyMethod(typeof(RingWorldPathing),"FindPath"));
            harmony.Patch(AccessTools.Method(typeof(FastTileFinder),"Query"),prefix:new HarmonyMethod(typeof(RingWorldPathing),"Query"));
            harmony.Patch(AccessTools.Method(typeof(FastTileFinder),"Closest"),prefix:new HarmonyMethod(typeof(RingWorldPathing),"Closest"));
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver),"ApplyPositionToGameObject"),prefix:new HarmonyMethod(typeof(RingWorldCamera),"Apply"));
            harmony.Patch(AccessTools.Method(typeof(WorldObject),"Print"),prefix:new HarmonyMethod(typeof(RingWorldObjects),"Print"));
            harmony.Patch(AccessTools.Method(typeof(WorldObject),"Draw"),prefix:new HarmonyMethod(typeof(RingWorldObjects),"Draw"));
            harmony.Patch(AccessTools.Method(typeof(GenTemperature),"GetTemperatureFromSeasonAtTile"),prefix:new HarmonyMethod(typeof(RingWorldEnvironment),"Temperature"));
            installed = true;
            }
            catch { harmony.UnpatchAll(harmony.Id); throw; }
        }
        private static void Patch(Harmony harmony,Type type,string method,string prefix,Type[] arguments) =>
            harmony.Patch(AccessTools.Method(type,method,arguments)??throw new MissingMethodException(type.FullName,method),
                prefix:new HarmonyMethod(typeof(RingWorldPatches),prefix));
        internal static bool Initialize(PlanetLayer __instance)
        {
            if (__instance is not RingSurfaceLayer ring) return true;
            ring.InitializeRing(); return false;
        }
        internal static void Expose(PlanetLayer __instance)
        {
            if (__instance is RingSurfaceLayer ring) ring.ExposeRingParameters();
        }
        public static bool ShouldHide(PlanetLayer candidate, PlanetLayer? selected) =>
            !ReferenceEquals(candidate, selected) && (candidate is RingSurfaceLayer || selected is RingSurfaceLayer);
        private static bool Center(PlanetLayer __instance,int tileID,ref Vector3 __result)
        {
            if(__instance is not RingSurfaceLayer ring) return true;
            __result=ring.Topology.Contains(tileID)?
                (RingWorldMotion.LocalPositions?(Vector3)ring.Geometry.Center(tileID):RingWorldMotion.Direction(ring.Geometry.Center(tileID))):
                Vector3.zero; return false;
        }
        private static bool Distance(PlanetLayer __instance,int tileA,int tileB,ref float __result)
        {
            if(__instance is not RingSurfaceLayer ring) return true;
            __result=ring.Topology.Distance(tileA,tileB);return false;
        }
        private static bool Heading(PlanetLayer __instance,int fromTileID,int toTileID,ref float __result)
        {
            if(__instance is not RingSurfaceLayer ring) return true;
            __result=(float)ring.Geometry.Heading(fromTileID,toTileID);return false;
        }
        private static bool LongLat(PlanetLayer __instance,int tileID,ref Vector2 __result)
        {
            if(__instance is not RingSurfaceLayer ring) return true;
            DVector3 center=ring.Geometry.Center(tileID);
            __result=new Vector2((float)(Math.Atan2(center.x,-center.z)*180/Math.PI),0);return false;
        }
        private static bool LineIntersects(PlanetLayer __instance,Vector3 A,Vector3 B,float radiusFactor,ref bool __result)
        {
            if(__instance is not RingSurfaceLayer ring) return true;
            __result=ring.Geometry.IntersectsSegment((DVector3)(A-ring.Origin),(DVector3)(B-ring.Origin),radiusFactor);return false;
        }
        private static bool IsParentSurface(PlanetLayer layer,RingSurfaceLayer ring)
        {
            var bodies=Current.Game?.GetComponent<CelestialBodyManager>();
            return bodies!=null&&ReferenceEquals(layer,Launch.GetSurfaceState.GetLayerForBody(bodies,ring.Parameters.ParentBodyId));
        }
        private static bool Visible(PlanetLayer __instance,ref bool __result)
        {
            PlanetLayer? selected=Find.WorldSelector?.SelectedLayer;
            if(selected is RingSurfaceLayer ring&&IsParentSurface(__instance,ring)) {__result=true;return false;}
            if(!ShouldHide(__instance,selected)) return true;
            __result=false;return false;
        }
        private static bool Raycastable(PlanetLayer __instance,ref bool __result)
        {
            if(!ShouldHide(__instance,Find.WorldSelector?.SelectedLayer)) return true;
            __result=false;return false;
        }
    }
    [StaticConstructorOnStartup]
    internal static class RingWorldStartup
    {
        static RingWorldStartup() { RingWorldPatches.Install(); }
    }
}
