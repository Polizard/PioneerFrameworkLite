using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    /// <summary>Native river coastal-water seam for Parameterized layers; installed lazily, idempotent.</summary>
    public static class CelestialPlanetHydrologyHooks
    {
        private static bool installed;
        public static void Install()
        {
            if (installed) return;
            new Harmony("PioneerFramework.Celestial.PlanetHydrology").Patch(AccessTools.Method(typeof(WorldGenStep_Rivers), "GetCoastalWaterTiles"),
                prefix: new(typeof(CelestialPlanetHydrologyHooks), nameof(CoastalWaterTiles)));
            installed = true;
        }
        public static bool CoastalWaterTiles(PlanetLayer layer, ref List<PlanetTile> __result)
        {
            if (!CelestialMapGeneration.TryGetTerrain(layer, out _)) return true;
            var result = new List<PlanetTile>(); var neighbors = new List<PlanetTile>();
            for (int index = 0; index < layer.TilesCount; index++)
            {
                if (layer[index].PrimaryBiome != BiomeDefOf.Ocean) continue;
                var tile = new PlanetTile(index, layer); layer.GetTileNeighbors(tile, neighbors);
                if (neighbors.Any(neighbor => layer[neighbor].PrimaryBiome != BiomeDefOf.Ocean)) result.Add(tile);
            }
            __result = result; return false;
        }
    }
}
