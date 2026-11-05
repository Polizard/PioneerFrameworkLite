using UnityEngine;
namespace PioneerFramework.RingWorld
{
    internal static class RingWorldVisualState
    {
        private static readonly Megastructures.SurfaceMaterialState State=new("_RingSunDir","_RingCameraPos","_RingTime");
        internal static Vector3 SunDirection=>State.SunDirection;
        internal static Vector3 LightPropagationDirection=>State.LightPropagationDirection;
        internal static Vector3 CameraPosition=>State.CameraPosition;
        internal static float Time=>State.Time;
        internal static void Register(Material material)=>State.Register(material);
        internal static void UpdatePerFrame()=>State.UpdatePerFrame();
    }
}
