using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    /// <summary>Single native dispatch installer. Shape packages install save/shape-only hooks in the same transaction.</summary>
    public static class SurfaceNativeBridge
    {
        private static bool installed;
        public static void Install(Action<Harmony> installShapeHooks)
        {
            if(installed)return;
            var harmony=new Harmony("PioneerFrameworkLite.RingWorld");
            try
            {
                SurfaceMotionBridge.Install(harmony);
                Patch(typeof(PlanetLayer),"InitializeLayer",typeof(SurfaceNativeBridge),nameof(Initialize));
                Patch(typeof(PlanetLayer),"ExposeData",typeof(SurfaceNativeBridge),nameof(Expose));
                Patch(typeof(PlanetLayer),"GetTileCenter",typeof(SurfaceNativeBridge),nameof(Center),new[]{typeof(int)});
                Patch(typeof(PlanetLayer),"ApproxDistanceInTiles",typeof(SurfaceNativeBridge),nameof(Distance),new[]{typeof(int),typeof(int)});
                harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer),nameof(PlanetLayer.Visible)),prefix:new HarmonyMethod(typeof(SurfaceNativeBridge),nameof(Visible)));
                harmony.Patch(AccessTools.PropertyGetter(typeof(PlanetLayer),nameof(PlanetLayer.Raycastable)),prefix:new HarmonyMethod(typeof(SurfaceNativeBridge),nameof(Raycastable)));
                Patch(typeof(WorldPathing),"FindPath",typeof(SurfacePathingBridge),"FindPath");
                Patch(typeof(FastTileFinder),"Query",typeof(SurfacePathingBridge),"Query");
                Patch(typeof(FastTileFinder),"Closest",typeof(SurfacePathingBridge),"Closest");
                Patch(typeof(GenTemperature),"GetTemperatureFromSeasonAtTile",typeof(SurfaceEnvironmentBridge),"Temperature");
                Patch(typeof(WorldCameraDriver),"ApplyPositionToGameObject",typeof(SurfaceCameraBridge),"Apply");
                Patch(typeof(WorldPath),"DrawPath",typeof(SurfaceWorldObjectBridge),"DrawPath");
                Patch(typeof(WorldObject),"Print",typeof(SurfaceWorldObjectBridge),"Print");
                Patch(typeof(WorldObject),"Draw",typeof(SurfaceWorldObjectBridge),"Draw");
                installShapeHooks(harmony);installed=true;
            }
            catch{harmony.UnpatchAll(harmony.Id);throw;}
            void Patch(Type target,string method,Type provider,string prefix,Type[]? args=null)=>harmony.Patch((args==null?AccessTools.Method(target,method):AccessTools.Method(target,method,args))??throw new MissingMethodException(target.FullName,method),prefix:new HarmonyMethod(provider,prefix));
        }
        private static bool Initialize(PlanetLayer __instance)
        {
            if(!ArtificialSurfaceLayers.TryDescribe(__instance,out var surface))return true;
            PlanetLayerGridBridge.Initialize(__instance,surface.Geometry);return false;
        }
        private static void Expose(PlanetLayer __instance)=>(__instance as IArtificialSurfaceSaveHeader)?.ExposeSurfaceHeader();
        private static bool Center(PlanetLayer __instance,int tileID,ref Vector3 __result)
        {
            if(!ArtificialSurfaceLayers.TryDescribe(__instance,out var surface))return true;
            if(tileID<0||tileID>=surface.Geometry.CellCount){__result=Vector3.zero;return false;}
            var local=surface.Geometry.EvaluateLocal(tileID).PositionMeters*SpaceConstants.UnitsPerMeter;
            __result=SurfaceMotionBridge.LocalPositions?(Vector3)local:SurfaceMotionBridge.Direction(__instance,local);return false;
        }
        private static bool Distance(PlanetLayer __instance,int tileA,int tileB,ref float __result)
        {
            if(!ArtificialSurfaceLayers.TryDescribe(__instance,out var surface))return true;
            __result=(float)surface.Geometry.Distance(tileA,tileB);return false;
        }
        public static bool ShouldHide(PlanetLayer candidate,PlanetLayer? selected)=>!ReferenceEquals(candidate,selected)&&(candidate is IArtificialSurfaceLayer||selected is IArtificialSurfaceLayer);
        private static bool Visible(PlanetLayer __instance,ref bool __result)
        {
            var selected=Find.WorldSelector?.SelectedLayer;
            if(ArtificialSurfaceLayers.TryDescribe(selected,out var surface))
            {
                var bodies=Current.Game?.GetComponent<CelestialBodyManager>();
                if(bodies!=null&&ReferenceEquals(__instance,Launch.GetSurfaceState.GetLayerForBody(bodies,surface.ParentBodyId))){__result=true;return false;}
            }
            if(!ShouldHide(__instance,selected))return true;__result=false;return false;
        }
        private static bool Raycastable(PlanetLayer __instance,ref bool __result)
        {if(!ShouldHide(__instance,Find.WorldSelector?.SelectedLayer))return true;__result=false;return false;}
    }
}
