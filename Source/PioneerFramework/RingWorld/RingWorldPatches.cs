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
        public static void Install()=>Megastructures.SurfaceNativeBridge.Install(InstallShapeHooks);
        private static void InstallShapeHooks(Harmony harmony)
        {
            Patch(harmony,typeof(PlanetLayer),"GetHeadingFromTo",nameof(Heading),new[]{typeof(int),typeof(int)});
            Patch(harmony,typeof(PlanetLayer),"LongLatOf",nameof(LongLat),new[]{typeof(int)});
            harmony.Patch(AccessTools.Method(typeof(PlanetLayer),"LineIntersects"),prefix:new HarmonyMethod(typeof(RingWorldPatches),nameof(LineIntersects)));
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
            Megastructures.SurfaceNativeBridge.ShouldHide(candidate,selected);
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
