using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.RingWorld
{
    internal static class RingWorldMotion
    {
        [ThreadStatic] private static int localPositionDepth;
        private static readonly FieldInfo ColliderCache = AccessTools.Field(typeof(WorldTerrainColliderManager), "layerWorldTerrainColliders");
        private static readonly FieldInfo CaravanField = AccessTools.Field(typeof(Caravan_Tweener), "caravan");
        private static readonly FieldInfo TweenPosition = AccessTools.Field(typeof(Caravan_Tweener), "tweenedPos");
        private static readonly FieldInfo LastTweenPosition = AccessTools.Field(typeof(Caravan_Tweener), "lastTickSpringPos");

        internal static bool LocalPositions => localPositionDepth > 0;
        internal static double Tick => Current.Game == null || Find.TickManager == null ? 0 : TimeScaleManager.GetPhysicsTickSafe();
        internal static Vector3 Point(RingSurfaceLayer ring, DVector3 local) => (Vector3)RingRotation.ToView(local, Tick) + ring.Origin;
        internal static Vector3 Direction(DVector3 local) => (Vector3)RingRotation.ToView(local, Tick);
        internal static Quaternion RotationAt(double tick)
        {
            double half = RingRotation.Phase(tick) / 2;
            return new Quaternion(0, -(float)Math.Sin(half), 0, (float)Math.Cos(half));
        }

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.PropertyGetter(typeof(WorldDrawLayerBase), "Rotation"),
                prefix: new HarmonyMethod(typeof(RingWorldMotion), nameof(DrawRotation)));
            harmony.Patch(AccessTools.Method(typeof(WorldTerrainColliderManager), "Get"),
                postfix: new HarmonyMethod(typeof(RingWorldMotion), nameof(ColliderCreated)));
            harmony.Patch(AccessTools.Method(typeof(WorldTerrainColliderManager), "EnsureRaycastCollidersUpdated"),
                postfix: new HarmonyMethod(typeof(RingWorldMotion), nameof(CollidersUpdated)));
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "JumpTo", new[]{typeof(Vector3)}),
                prefix: new HarmonyMethod(typeof(RingWorldCamera), nameof(RingWorldCamera.WorldTarget)));
            harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "Update"),
                prefix: new HarmonyMethod(typeof(RingWorldCamera), nameof(RingWorldCamera.BeforeUpdate)));
            foreach (string method in new[]{"TweenerTickInterval", "ResetTweenedPosToRoot"})
                harmony.Patch(AccessTools.Method(typeof(Caravan_Tweener), method),
                    prefix: new HarmonyMethod(typeof(RingWorldMotion), nameof(BeginTween)),
                    finalizer: new HarmonyMethod(typeof(RingWorldMotion), nameof(EndTween)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Caravan_Tweener), "TweenedPos"),
                postfix: new HarmonyMethod(typeof(RingWorldMotion), nameof(TweenedPosition)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Caravan_Tweener), "LastTickTweenedVelocity"),
                prefix: new HarmonyMethod(typeof(RingWorldMotion), nameof(TweenedVelocity)));
        }

        private static bool DrawRotation(WorldDrawLayerBase __instance, ref Quaternion __result)
        {
            if (__instance is not WorldDrawLayer layer || layer.planetLayer is not RingSurfaceLayer) return true;
            __result = RotationAt(Tick); return false;
        }
        private static void ColliderCreated(PlanetLayer planetLayer, GameObject __result)
        {
            if (planetLayer is not RingSurfaceLayer) return;
            __result.transform.position = planetLayer.Origin;
            __result.transform.rotation = RotationAt(Tick);
        }
        private static void CollidersUpdated()
        {
            if (Find.WorldSelector?.SelectedLayer is not RingSurfaceLayer ring) return;
            // Get() deactivates colliders; only update roots already owned by the native cache.
            if (ColliderCache.GetValue(null) is not Dictionary<PlanetLayer, Dictionary<int, GameObject>> cache ||
                !cache.TryGetValue(ring, out var roots)) return;
            Quaternion rotation = RotationAt(Tick);
            foreach (GameObject root in roots.Values)
            {
                root.transform.position = ring.Origin;
                root.transform.rotation = rotation;
            }
        }
        private static RingSurfaceLayer? RingOf(Caravan_Tweener tweener) =>
            (CaravanField.GetValue(tweener) as Caravan)?.Tile.Layer as RingSurfaceLayer;
        private static void BeginTween(Caravan_Tweener __instance, out int __state)
        {
            __state = RingOf(__instance) == null ? -1 : localPositionDepth;
            if (__state >= 0) localPositionDepth++;
        }
        private static Exception? EndTween(Exception? __exception, int __state)
        {
            if (__state >= 0) localPositionDepth = __state;
            return __exception;
        }
        private static void TweenedPosition(Caravan_Tweener __instance, ref Vector3 __result)
        {
            if (!LocalPositions && RingOf(__instance) is RingSurfaceLayer ring)
                __result = Point(ring, (DVector3)__result);
        }
        private static bool TweenedVelocity(Caravan_Tweener __instance, ref Vector3 __result)
        {
            if (RingOf(__instance) == null) return true;
            Vector3 delta = (Vector3)TweenPosition.GetValue(__instance) - (Vector3)LastTweenPosition.GetValue(__instance);
            __result = LocalPositions ? delta : Direction((DVector3)delta);
            return false;
        }
    }
}
