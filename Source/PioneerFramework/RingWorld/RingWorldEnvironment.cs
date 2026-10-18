using RimWorld.Planet;
using Verse;

namespace PioneerFramework.RingWorld
{
    internal static class RingWorldEnvironment
    {
        internal static double DisplayTicks=>Megastructures.SurfaceEnvironmentBridge.DisplayTicks;
        internal static bool Temperature(PlanetTile tile,ref float __result)=>Megastructures.SurfaceEnvironmentBridge.Temperature(tile,ref __result);
    }
}
