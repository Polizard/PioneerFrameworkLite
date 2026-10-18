using RimWorld.Planet;
namespace PioneerFramework.RingWorld
{
    internal static class RingWorldCamera
    {
        internal static void WorldTarget()=>Megastructures.SurfaceCameraBridge.WorldTarget();
        internal static void BeforeUpdate(WorldCameraDriver driver)=>Megastructures.SurfaceCameraBridge.BeforeUpdate(driver);
        internal static bool Apply(WorldCameraDriver driver)=>Megastructures.SurfaceCameraBridge.Apply(driver);
    }
}
