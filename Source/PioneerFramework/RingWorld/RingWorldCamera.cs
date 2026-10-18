using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    internal static class RingWorldCamera
    {
        private static readonly System.Reflection.MethodInfo GetCamera = AccessTools.PropertyGetter(typeof(WorldCameraDriver), "MyCamera");
        private static readonly RingCameraFollow Follow = new RingCameraFollow();
        private static Camera? lastCamera;
        private static float previousFarClip;
        private static WorldCameraDriver? trackingDriver;
        private static Game? trackingGame;
        internal static void WorldTarget() => Follow.Reset();
        internal static void BeforeUpdate(WorldCameraDriver __instance)
        {
            // Native input uses world camera axes, so update direction and camera before it runs.
            if(Find.WorldSelector?.SelectedLayer is RingSurfaceLayer && __instance.altitude<300) Apply(__instance);
        }
        internal static bool Apply(WorldCameraDriver __instance)
        {
            if (Find.WorldSelector?.SelectedLayer is not RingSurfaceLayer ring)
            {
                Follow.Reset();
                if(lastCamera!=null){lastCamera.farClipPlane=previousFarClip;lastCamera=null;}
                return true;
            }
            if (GetCamera.Invoke(__instance, null) is not Camera camera) return true;
            if(!ReferenceEquals(trackingDriver,__instance)||!ReferenceEquals(trackingGame,Current.Game))
            {
                Follow.Reset(); trackingDriver=__instance; trackingGame=Current.Game;
            }
            if(lastCamera==null){lastCamera=camera;previousFarClip=camera.farClipPlane;}
            float radius = (float)ring.Geometry.Radius;
            camera.farClipPlane=Mathf.Max(previousFarClip,radius*4+__instance.altitude*2);
            double tick=RingWorldMotion.Tick;
            Vector3 direction = __instance.CurrentlyLookingAtPointOnSphere;
            bool inner=__instance.altitude<300;
            DVector3 followed=Follow.Update(ring.LayerID,inner,(DVector3)direction,tick);
            if((followed-(DVector3)direction).sqrMagnitude>1e-20)
            {
                __instance.JumpTo((Vector3)followed);
                // JumpTo clears tracking; re-establish the baseline after this internal follow step.
                Follow.Update(ring.LayerID,inner,followed,tick);
                direction=__instance.CurrentlyLookingAtPointOnSphere;
            }
            if (__instance.altitude >= 300)
            {
                float halfWidth = (float)ring.Geometry.HalfWidth;
                float bound = Mathf.Sqrt(radius*radius+halfWidth*halfWidth);
                camera.transform.position = ring.Origin+direction*(bound+__instance.altitude);
                camera.transform.rotation = Quaternion.LookRotation(-direction,Vector3.up);
                return false;
            }
            DVector3 desired=ring.Geometry.ProjectDirectionToSurface(RingRotation.ToLocal((DVector3)direction,tick));
            Vector3 center=(Vector3)RingRotation.ToView(desired,tick)+ring.Origin;
            Vector3 normal=(Vector3)RingRotation.ToView(new DVector3(-desired.x/radius,0,-desired.z/radius),tick);
            var body = Current.Game.GetComponent<CelestialBodyManager>()?.GetBodyById(ring.Parameters.ParentBodyId);
            float clearance = radius - (float)((body?.radius ?? 0) * SpaceConstants.UnitsPerMeter);
            // 2026-10-17 电影感重制：相机 near clip = 2，旧版最低高度时距居住面仅约 1 单位会"贴脸穿模"。
            // 最低间距 clamp 到 3 单位，既避开 near clip 又保留贴地观感。
            const float MinSurfaceClearance = 3f;
            float zoomMax = Mathf.Max(MinSurfaceClearance,clearance*.8f);
            float zoom = Mathf.Lerp(MinSurfaceClearance,zoomMax,
                Mathf.InverseLerp(WorldCameraDriver.MinAltitude,300,__instance.altitude));
            camera.transform.position = center + normal*zoom;
            camera.transform.rotation = Quaternion.LookRotation(-normal,Vector3.up);
            return false;
        }
    }
}
