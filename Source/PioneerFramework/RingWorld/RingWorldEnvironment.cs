using RimWorld.Planet;
using Verse;

namespace PioneerFramework.RingWorld
{
    internal static class RingWorldEnvironment
    {
        internal static double DisplayTicks => Current.Game?.GetComponent<TimeScaleManager>()?.GetDisplayTicksAbs() ?? Find.TickManager.TicksAbs;
        internal static bool Temperature(PlanetTile tile, ref float __result)
        {
            if (tile.Layer is not RingSurfaceLayer ring) return true;
            __result = ring[tile.tileId]?.temperature ?? 21; return false;
        }
    }
}
