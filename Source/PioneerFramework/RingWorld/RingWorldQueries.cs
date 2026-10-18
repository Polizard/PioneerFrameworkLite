using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.RingWorld
{
    public static class RingWorldQueries
    {
        public static IEnumerable<int> SelectByDistance(RingSurfaceLayer ring, int origin, float min, float max)
        {
            return Megastructures.SurfaceQueryService.SelectByDistance(ring.Topology,origin,min,max);
        }
        internal static List<PlanetTile> Query(RingSurfaceLayer ring,FastTileFinder.TileQueryParams query,List<BiomeDef>? biomes,List<LandmarkDef>? landmarks,int limit)=>
            Megastructures.SurfaceQueries.Query(ring,query,biomes,landmarks,limit);
    }
}
