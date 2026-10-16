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
            ring.Topology.Check(origin);
            return Enumerable.Range(0, ring.Topology.TileCount)
                .Select(tile => (tile, distance: ring.Topology.Distance(origin, tile)))
                .Where(x => x.distance >= min && x.distance <= max)
                .OrderBy(x => x.distance).ThenBy(x => x.tile).Select(x => x.tile);
        }
        internal static List<PlanetTile> Query(RingSurfaceLayer ring, FastTileFinder.TileQueryParams query,
            List<BiomeDef>? biomes, List<LandmarkDef>? landmarks, int limit)
        {
            var result = new List<PlanetTile>();
            if (!query.Valid || !query.origin.Valid || query.origin.Layer != ring) return result;
            foreach (int id in SelectByDistance(ring, query.origin.tileId, query.minDistTiles, query.maxDistTiles))
            {
                var tileId = new PlanetTile(id, ring); Tile tile = ring[id];
                if (query.minHilliness != Hilliness.Undefined && tile.hilliness < query.minHilliness) continue;
                if (query.maxHilliness != Hilliness.Undefined && tile.hilliness > query.maxHilliness) continue;
                if (query.validSettlement && !TileFinder.IsValidTileForNewSettlement(tileId)) continue;
                if (query.reachable && !Find.WorldReachability.CanReach(query.origin, tileId)) continue;
                if (query.checkBiome && biomes != null && biomes.Count > 0 && !biomes.Contains(tile.PrimaryBiome)) continue;
                if (query.landmarkMode != FastTileFinder.LandmarkMode.Any)
                {
                    Landmark landmark = tile.Landmark;
                    if (query.landmarkMode == FastTileFinder.LandmarkMode.Required && landmark == null) continue;
                    if (query.landmarkMode == FastTileFinder.LandmarkMode.Forbidden && landmark != null) continue;
                    if (!query.comboLandmarks && landmark?.isComboLandmark == true) continue;
                    if (landmarks != null && landmarks.Count > 0 && (landmark == null || !landmarks.Contains(landmark.def))) continue;
                }
                result.Add(tileId);
                if (result.Count >= limit) break;
            }
            return result;
        }
    }
}
