using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Megastructures
{
    internal static class SurfaceQueries
    {
        internal static List<PlanetTile> Query(PlanetLayer ring, FastTileFinder.TileQueryParams query,
            List<BiomeDef>? biomes, List<LandmarkDef>? landmarks, int limit)
        {
            var result = new List<PlanetTile>();
            if(!ArtificialSurfaceLayers.TryDescribe(ring,out var surface))return result;
            if (!query.Valid || !query.origin.Valid || query.origin.Layer != ring) return result;
            foreach (int id in SurfaceQueryService.SelectByDistance(surface.Geometry, query.origin.tileId, query.minDistTiles, query.maxDistTiles))
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
