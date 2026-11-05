using System;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Megastructures
{
    internal static class SurfacePathingBridge
    {
        internal static bool FindPath(WorldPathing __instance, PlanetTile startTile, PlanetTile destTile,
            Caravan caravan, Func<float, bool>? terminator, ref WorldPath __result)
        {
            if (!ArtificialSurfaceLayers.TryDescribe(__instance.Layer,out var surface)) return true;
            var ring=__instance.Layer;
            __result = WorldPath.NotFound;
            if(!surface.ModuleEnabled(SurfaceModules.Navigation))return false;
            if (!startTile.Valid || !destTile.Valid || startTile.Layer != ring || destTile.Layer != ring ||
                startTile.tileId<0 || startTile.tileId>=surface.Geometry.CellCount || destTile.tileId<0 || destTile.tileId>=surface.Geometry.CellCount) return false;
            if (caravan != null ? !caravan.CanReach(destTile) : !Find.WorldReachability.CanReach(startTile, destTile)) return false;
            if (!Find.WorldPathGrid.layerMovementDifficulty.TryGetValue(ring, out float[]? movement)) return false;
            int ticks = caravan?.TicksPerMove ?? 3300;
            var result = SurfacePathSearch.Find(surface.Geometry, startTile.tileId, destTile.tileId,
                (a, b) => (int)(ticks * movement[b] * Find.WorldGrid.GetRoadMovementDifficultyMultiplier(new PlanetTile(a, ring), new PlanetTile(b, ring))),
                tile => !Find.World.Impassable(new PlanetTile(tile, ring)),
                terminator == null ? null : cost => terminator((float)cost));
            if (!result.Found) return false;
            WorldPath path = Find.WorldPathPool.GetEmptyWorldPath();
            for (int i = result.Nodes.Count - 1; i >= 0; i--) path.AddNodeAtStart(new PlanetTile(result.Nodes[i], ring));
            path.SetupFound((float)result.Cost, ring); __result = path; return false;
        }
        internal static bool Query(FastTileFinder __instance, FastTileFinder.TileQueryParams query,
            System.Collections.Generic.List<RimWorld.BiomeDef>? biomes, System.Collections.Generic.List<RimWorld.LandmarkDef>? landmarks,
            FastTileFinder.TileQueryParams desperate, ref System.Collections.Generic.List<PlanetTile> __result)
        {
            if (!ArtificialSurfaceLayers.TryDescribe(__instance.Layer,out var surface)) return true;
            var ring=__instance.Layer;
            if(!surface.ModuleEnabled(SurfaceModules.Navigation)){__result=new();return false;}
            __result = SurfaceQueries.Query(ring, query, biomes, landmarks, __instance.MaxResults);
            if (__result.Count == 0 && desperate.Valid) __result = SurfaceQueries.Query(ring, desperate, biomes, landmarks, __instance.MaxResults);
            return false;
        }
        internal static bool Closest(FastTileFinder __instance, FastTileFinder.TileQueryParams query, ref PlanetTile tile,
            System.Collections.Generic.List<RimWorld.BiomeDef>? biomes, System.Collections.Generic.List<RimWorld.LandmarkDef>? landmarks,
            FastTileFinder.TileQueryParams desperate, ref bool __result)
        {
            if (!ArtificialSurfaceLayers.TryDescribe(__instance.Layer,out var surface)) return true;
            var ring=__instance.Layer;
            if(!surface.ModuleEnabled(SurfaceModules.Navigation)){__result=false;tile=PlanetTile.Invalid;return false;}
            var results = SurfaceQueries.Query(ring, query, biomes, landmarks, 1);
            if (results.Count == 0 && desperate.Valid) results = SurfaceQueries.Query(ring, desperate, biomes, landmarks, 1);
            __result = results.Count > 0; tile = __result ? results[0] : PlanetTile.Invalid; return false;
        }
    }
}
