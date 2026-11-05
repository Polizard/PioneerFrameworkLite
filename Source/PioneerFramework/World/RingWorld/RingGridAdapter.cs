using System.Reflection;
namespace PioneerFramework.RingWorld
{
    internal static class RingGridAdapter
    {
        internal static FieldInfo Field(string name)=>Megastructures.PlanetLayerGridBridge.Field(name);
        internal static void Initialize(RingSurfaceLayer layer)=>Megastructures.PlanetLayerGridBridge.Initialize(layer,layer.Surface.Geometry);
    }
}
