using RimWorld.Planet;
using Verse;
namespace PioneerFramework.RingWorld
{
    internal static class RingWorldObjects
    {
        internal static bool DrawPath(WorldPath path,Caravan caravan)=>Megastructures.SurfaceWorldObjectBridge.DrawPath(path,caravan);
        internal static bool Print(WorldObject worldObject,LayerSubMesh mesh)=>Megastructures.SurfaceWorldObjectBridge.Print(worldObject,mesh);
        internal static bool Draw(WorldObject worldObject)=>Megastructures.SurfaceWorldObjectBridge.Draw(worldObject);
    }
}
