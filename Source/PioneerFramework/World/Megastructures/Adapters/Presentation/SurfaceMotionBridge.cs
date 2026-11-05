using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Megastructures
{
    internal static class SurfaceMotionBridge
    {
        [ThreadStatic] private static int localPositionDepth;
        private static readonly FieldInfo ColliderCache = AccessTools.Field(typeof(WorldTerrainColliderManager), "layerWorldTerrainColliders");
        private static readonly FieldInfo CaravanField = AccessTools.Field(typeof(Caravan_Tweener), "caravan");
        private static readonly FieldInfo TweenPosition = AccessTools.Field(typeof(Caravan_Tweener), "tweenedPos");
        private static readonly FieldInfo LastTweenPosition = AccessTools.Field(typeof(Caravan_Tweener), "lastTickSpringPos");

        internal static bool LocalPositions => localPositionDepth > 0;
        internal static double Tick => Current.Game == null || Find.TickManager == null ? 0 : TimeScaleManager.GetPhysicsTickSafe();
        internal static Vector3 Point(PlanetLayer layer,DVector3 local)=>Direction(layer,local)+layer.Origin;
        internal static Vector3 Direction(PlanetLayer layer,DVector3 local)=>
            (Vector3)(ArtificialSurfaceLayers.TryDescribe(layer,out var surface)?surface.GetMotionSample(Tick).Rotate(local):local);
        internal static Quaternion RotationAt(PlanetLayer layer,double tick)
        {
            if(!ArtificialSurfaceLayers.TryDescribe(layer,out var surface))return Quaternion.identity;
            var rotation=surface.GetMotionSample(tick).Rotation;
            return new Quaternion((float)rotation.X,(float)rotation.Y,(float)rotation.Z,(float)rotation.W);
        }

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(WorldDrawLayerBase), "Rotation"),
                prefix: new HarmonyMethod(typeof(SurfaceMotionBridge), nameof(DrawRotation)));
            harmony.Patch(AccessTools.Method(typeof(WorldTerrainColliderManager), "Get"),
                postfix: new HarmonyMethod(typeof(SurfaceMotionBridge), nameof(ColliderCreated)));
            harmony.Patch(AccessTools.Method(typeof(WorldTerrainColliderManager), "EnsureRaycastCollidersUpdated"),
                postfix: new HarmonyMethod(typeof(SurfaceMotionBridge), nameof(CollidersUpdated)));
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "JumpTo", new[]{typeof(Vector3)}),
                prefix: new HarmonyMethod(typeof(SurfaceCameraBridge), nameof(SurfaceCameraBridge.WorldTarget)));
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "Update"),
                prefix: new HarmonyMethod(typeof(SurfaceCameraBridge), nameof(SurfaceCameraBridge.BeforeUpdate)));
            foreach (string method in new[]{"TweenerTickInterval", "ResetTweenedPosToRoot"})
                harmony.Patch(AccessTools.Method(typeof(Caravan_Tweener), method),
                    prefix: new HarmonyMethod(typeof(SurfaceMotionBridge), nameof(BeginTween)),
                    finalizer: new HarmonyMethod(typeof(SurfaceMotionBridge), nameof(EndTween)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Caravan_Tweener), "TweenedPos"),
                postfix: new HarmonyMethod(typeof(SurfaceMotionBridge), nameof(TweenedPosition)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Caravan_Tweener), "LastTickTweenedVelocity"),
                prefix: new HarmonyMethod(typeof(SurfaceMotionBridge), nameof(TweenedVelocity)));
        }

        private static bool DrawRotation(WorldDrawLayerBase __instance, ref Quaternion __result)
        {
            if (__instance is not WorldDrawLayer layer || !ArtificialSurfaceLayers.TryDescribe(layer.planetLayer,out _)) return true;
            __result = RotationAt(layer.planetLayer,Tick); return false;
        }
        private static void ColliderCreated(PlanetLayer planetLayer, GameObject __result)
        {
            if (!ArtificialSurfaceLayers.TryDescribe(planetLayer,out _)) return;
            __result.transform.position = planetLayer.Origin;
            __result.transform.rotation = RotationAt(planetLayer,Tick);
        }
        private static void CollidersUpdated()
        {
            var ring=Find.WorldSelector?.SelectedLayer;
            if(!ArtificialSurfaceLayers.TryDescribe(ring,out _))return;
            // Get() deactivates colliders; only update roots already owned by the native cache.
            if (ColliderCache.GetValue(null) is not Dictionary<PlanetLayer, Dictionary<int, GameObject>> cache ||
                !cache.TryGetValue(ring, out var roots)) return;
            Quaternion rotation = RotationAt(ring!,Tick);
            foreach (GameObject root in roots.Values)
            {
                root.transform.position = ring.Origin;
                root.transform.rotation = rotation;
            }
        }
        private static PlanetLayer? SurfaceOf(Caravan_Tweener tweener)
        {
            var layer=(CaravanField.GetValue(tweener) as Caravan)?.Tile.Layer;
            return ArtificialSurfaceLayers.TryDescribe(layer,out _)?layer:null;
        }
        private static void BeginTween(Caravan_Tweener __instance, out int __state)
        {
            __state = SurfaceOf(__instance) == null ? -1 : localPositionDepth;
            if (__state >= 0) localPositionDepth++;
        }
        private static Exception? EndTween(Exception? __exception, int __state)
        {
            if (__state >= 0) localPositionDepth = __state;
            return __exception;
        }
        private static void TweenedPosition(Caravan_Tweener __instance, ref Vector3 __result)
        {
            if (!LocalPositions && SurfaceOf(__instance) is PlanetLayer ring)
                __result = Point(ring, (DVector3)__result);
        }
        private static bool TweenedVelocity(Caravan_Tweener __instance, ref Vector3 __result)
        {
            if (SurfaceOf(__instance) == null) return true;
            Vector3 delta = (Vector3)TweenPosition.GetValue(__instance) - (Vector3)LastTweenPosition.GetValue(__instance);
            __result = LocalPositions ? delta : Direction(SurfaceOf(__instance)!,(DVector3)delta);
            return false;
        }
    }
}
