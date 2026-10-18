using System;
using HarmonyLib;
using UnityEngine;
namespace PioneerFramework.RingWorld
{
    internal static class RingWorldMotion
    {
        internal static bool LocalPositions=>Megastructures.SurfaceMotionBridge.LocalPositions;
        internal static double Tick=>Megastructures.SurfaceMotionBridge.Tick;
        internal static Vector3 Point(RingSurfaceLayer ring,DVector3 local)=>Megastructures.SurfaceMotionBridge.Point(ring,local);
        internal static Vector3 Direction(DVector3 local)=>(Vector3)RingRotation.ToView(local,Tick);
        internal static Quaternion RotationAt(double tick)
        {double half=RingRotation.Phase(tick)/2;return new Quaternion(0,-(float)Math.Sin(half),0,(float)Math.Cos(half));}
        internal static void Install(Harmony harmony)=>Megastructures.SurfaceMotionBridge.Install(harmony);
    }
}
