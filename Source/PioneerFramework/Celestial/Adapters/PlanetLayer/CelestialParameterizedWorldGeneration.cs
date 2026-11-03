using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    public static partial class CelestialWorldGeneration
    {
        private static void GenerateParameterized(CelestialSurfaceLayer layer, CelestialGenerationProfile profile, string seedString)
        {
            var terrain = profile.Terrain!;
            CelestialPlanetMapHooks.Install();
            var biomeIds = terrain.BiomeRules.Select(r => r.BiomeId);
            if (terrain.SeaLevelMeters.HasValue || terrain.OceanCoverage > 0) biomeIds = biomeIds.Append(terrain.OceanBiomeId);
            var biomes = biomeIds.Distinct(StringComparer.Ordinal)
                .ToDictionary(id => id, CelestialPlanetMapHooks.Definition<BiomeDef>, StringComparer.Ordinal);
            // Allocation happens only inside the existing explicit generation operation for this already requested native layer.
            var fields = new CelestialPlanetTerrainSample[layer.TilesCount];
            for (int index = 0; index < fields.Length; index++)
            {
                var coords = layer.LongLatOf(index);
                fields[index] = CelestialPlanetTerrain.Sample(terrain, coords.x, coords.y, terrain.FieldSeed);
            }
            bool[] oceans = CelestialPlanetTerrain.SelectOceanTiles(terrain, fields.Select(f => f.ElevationMeters).ToArray());
            layer.Tiles.Clear();
            for (int index = 0; index < fields.Length; index++)
            {
                var f = fields[index]; var coords = layer.LongLatOf(index);
                var query = new CelestialSurfaceEnvironmentQuery(0, coords.x, coords.y, f.ElevationMeters);
                double temperature = terrain.Climate == null ? profile.Temperature : CelestialClimateModel.SeasonalTemperature(terrain.Climate, query);
                double rainfall = (terrain.Climate?.RainfallMillimeters ?? profile.Rainfall) * (.5 + f.Moisture);
                double humidity = Math.Clamp((terrain.Climate?.Humidity ?? profile.Swampiness) * (.5 + f.Moisture), 0, 1);
                double pick = CelestialParameterSampler.SampleDouble(DoubleParameterSpec.Uniform(0, 1),
                    CelestialSeed.Derive(unchecked((ulong)layer.Header.GenerationSeed), index.ToString(System.Globalization.CultureInfo.InvariantCulture), "terrain", "biome"));
                string biome = oceans[index] ? terrain.OceanBiomeId : CelestialPlanetTerrain.SelectBiome(terrain, temperature, rainfall, f.ElevationMeters, coords.y, f.Roughness, pick).BiomeId;
                Hilliness hills = oceans[index] ? Hilliness.Flat : f.MountainScore > .75 ? Hilliness.Impassable : f.MountainScore > .5 ? Hilliness.Mountainous :
                    f.MountainScore > .3 ? Hilliness.LargeHills : f.Roughness > .5 ? Hilliness.SmallHills : Hilliness.Flat;
                layer.Tiles.Add(new SurfaceTile(new PlanetTile(index, layer)) { PrimaryBiome = biomes[biome], elevation = (float)f.ElevationMeters,
                    hilliness = hills, temperature = (float)temperature, rainfall = (float)rainfall, swampiness = (float)humidity });
            }
            CelestialPlanetHydrologyHooks.Install();
            void Native(WorldGenStep step)
            { Rand.Seed = Gen.HashCombineInt(layer.Header.GenerationSeed, step.SeedPart); step.GenerateFresh(seedString, layer); }
            if ((terrain.AllowRivers || terrain.AllowLakes) && terrain.OceanBiomeId != "Ocean") throw new InvalidOperationException("Native hydrology requires the existing Ocean biome.");
            if (terrain.AllowRivers) Native(new WorldGenStep_Rivers());
            if (terrain.AllowLakes) Native(new WorldGenStep_Lakes());
            if (terrain.Landmarks.Count > 0)
            {
                Rand.Seed = Gen.HashCombineInt(layer.Header.GenerationSeed, 1782513);
                foreach (var tile in layer.Tiles.Where(t => !t.WaterCovered))
                {
                    bool allowCaves = !CelestialMapGeneration.TryGetDiversity(tile.tile, out var diversity) || diversity.AllowCaves;
                    var candidates = terrain.Landmarks.Where(r => {
                        var candidate = CelestialPlanetMapHooks.Definition<TileMutatorDef>(r.Id);
                        return (allowCaves || !candidate.IsCave) && candidate.IsValidTile(tile.tile, layer);
                    }).ToArray();
                    if (candidates.Length == 0) continue;
                    var chosen = CelestialPlanetMapHooks.Definition<TileMutatorDef>(CelestialPlanetMapHooks.Pick(candidates, Rand.Value).Id);
                    tile.mutatorsNullable ??= new(); tile.mutatorsNullable.Add(chosen); chosen.Worker.OnAddedToTile(tile.tile);
                }
            }
        }
    }
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
