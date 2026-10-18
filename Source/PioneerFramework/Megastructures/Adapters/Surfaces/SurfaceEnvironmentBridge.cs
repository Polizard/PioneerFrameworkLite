using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Megastructures
{
    public static class SurfaceEnvironmentBridge
    {
        public static double DisplayTicks=>Current.Game?.GetComponent<TimeScaleManager>()?.GetDisplayTicksAbs()??Find.TickManager?.TicksAbs??0;
        public static bool TryDayPercent(PlanetLayer layer,out float result)
        {
            result=0;if(!ArtificialSurfaceLayers.TryDescribe(layer,out var s)||s.Environment==null||!s.ModuleEnabled(SurfaceModules.Environment))return false;
            result=(float)ArtificialCycle.DayPercent(DisplayTicks,s.Environment.DayPeriodTicks);return true;
        }
        public static bool TrySunGlow(PlanetLayer layer,out float result)
        {
            result=0;if(!ArtificialSurfaceLayers.TryDescribe(layer,out var s)||s.Environment==null||!s.ModuleEnabled(SurfaceModules.Environment))return false;
            result=(float)ArtificialCycle.SunGlow(DisplayTicks,s.Environment.DayPeriodTicks);return true;
        }
        internal static bool Temperature(PlanetTile tile,ref float __result)
        {
            if(!ArtificialSurfaceLayers.TryDescribe(tile.Layer,out var s)||s.Environment==null||!s.ModuleEnabled(SurfaceModules.Environment))return true;
            if(tile.tileId<0||tile.tileId>=s.Geometry.CellCount)return true;
            __result=tile.Layer[tile.tileId]?.temperature??s.Environment.Temperature;return false;
        }
        public static SurfaceTile CreateInitialTile(PlanetTile tile,BiomeDef biome,ArtificialEnvironmentProfile profile)=>new(tile)
        {PrimaryBiome=biome,elevation=profile.Elevation,hilliness=Hilliness.Flat,temperature=profile.Temperature,rainfall=profile.Rainfall,swampiness=0,pollution=0};
    }
}
