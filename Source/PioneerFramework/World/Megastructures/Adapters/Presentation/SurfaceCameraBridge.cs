using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    internal static class SurfaceCameraBridge
    {
        private static readonly System.Reflection.MethodInfo GetCamera = AccessTools.PropertyGetter(typeof(WorldCameraDriver), "MyCamera");
        private static readonly SurfaceCameraFollow Follow = new SurfaceCameraFollow();
        private static Camera? lastCamera;
        private static float previousFarClip;
        private static WorldCameraDriver? trackingDriver;
        private static Game? trackingGame;
        internal static void WorldTarget() => Follow.Reset();
        internal static void BeforeUpdate(WorldCameraDriver __instance)
        {
            // Native input uses world camera axes, so update direction and camera before it runs.
            if(ArtificialSurfaceLayers.TryDescribe(Find.WorldSelector?.SelectedLayer,out var surface)&&surface.View!=null&&__instance.altitude<surface.View.InnerAltitude)Apply(__instance);
        }
        internal static bool Apply(WorldCameraDriver __instance)
        {
            if (!ArtificialSurfaceLayers.TryDescribe(Find.WorldSelector?.SelectedLayer,out var surface)||surface.View==null||!surface.ModuleEnabled(SurfaceModules.View))
            {
                Follow.Reset();
                if(lastCamera!=null){lastCamera.farClipPlane=previousFarClip;lastCamera=null;}
                trackingGame=null;trackingDriver=null;
                return true;
            }
            var ring=Find.WorldSelector.SelectedLayer;
            var view=surface.View;
            var motion=surface.ModuleEnabled(SurfaceModules.Motion)?surface.Motion:null;
            if (GetCamera.Invoke(__instance, null) is not Camera camera) return true;
            if(!ReferenceEquals(trackingDriver,__instance)||!ReferenceEquals(trackingGame,Current.Game))
            {
                if(lastCamera!=null){lastCamera.farClipPlane=previousFarClip;lastCamera=null;}
                Follow.Reset(); trackingDriver=__instance; trackingGame=Current.Game;
            }
            if(lastCamera!=camera)
            {
                if(lastCamera!=null)lastCamera.farClipPlane=previousFarClip;
                lastCamera=camera;previousFarClip=camera.farClipPlane;
            }
            float radius = view.Radius;
            camera.farClipPlane=Mathf.Max(previousFarClip,radius*4+__instance.altitude*2);
            double tick=SurfaceMotionBridge.Tick;
            var sample=surface.GetMotionSample(tick);
            Vector3 direction = __instance.CurrentlyLookingAtPointOnSphere;
            bool inner=__instance.altitude<view.InnerAltitude;
            DVector3 followed=Follow.Update(ring.LayerID,inner,(DVector3)direction,tick,motion);
            if((followed-(DVector3)direction).sqrMagnitude>1e-20)
            {
                __instance.JumpTo((Vector3)followed);
                // JumpTo clears tracking; re-establish the baseline after this internal follow step.
                Follow.Update(ring.LayerID,inner,followed,tick,motion);
                direction=__instance.CurrentlyLookingAtPointOnSphere;
            }
            if (__instance.altitude >= view.InnerAltitude)
            {
                float bound=view.Bound;
                camera.transform.position = ring.Origin+direction*(bound+__instance.altitude);
                camera.transform.rotation = Quaternion.LookRotation(-direction,Vector3.up);
                return false;
            }
            DVector3 desired=view.Project(sample.InverseRotate((DVector3)direction));
            Vector3 center=(Vector3)sample.Rotate(desired)+ring.Origin;
            Vector3 normal=(Vector3)sample.Rotate(view.NormalAt(desired));
            var body = Current.Game.GetComponent<CelestialBodyManager>()?.GetBodyById(surface.ParentBodyId);
            float clearance = radius - (float)((body?.radius ?? 0) * SpaceConstants.UnitsPerMeter);
            // 2026-10-17 电影感重制：相机 near clip = 2，旧版最低高度时距居住面仅约 1 单位会"贴脸穿模"。
            // 最低间距 clamp 到 3 单位，既避开 near clip 又保留贴地观感。
            const float MinSurfaceClearance = 3f;
            float zoomMax = Mathf.Max(MinSurfaceClearance,clearance*.8f);
            float zoom = Mathf.Lerp(MinSurfaceClearance,zoomMax,
                Mathf.InverseLerp(WorldCameraDriver.MinAltitude,view.InnerAltitude,__instance.altitude));
            camera.transform.position = center + normal*zoom;
            camera.transform.rotation = Quaternion.LookRotation(-normal,Vector3.up);
            return false;
        }
    }
}
