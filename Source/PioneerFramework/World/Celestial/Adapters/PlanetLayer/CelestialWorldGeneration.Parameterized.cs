using System;
using System.Collections.Generic;
using System.Linq;
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
                // 降雨/湿度单一公式：与 CompPlanetEnvironment 的运行期采样共用 CelestialPlanetTerrain.SurfaceMoisture。
                var moisture = CelestialPlanetTerrain.SurfaceMoisture(terrain, profile.Rainfall, profile.Swampiness, f.Moisture);
                double rainfall = moisture.RainfallMillimeters;
                double humidity = moisture.Humidity;
                double pick = CelestialParameterSampler.SampleDouble(DoubleParameterSpec.Uniform(0, 1),
                    CelestialSeed.DeriveTile(unchecked((ulong)layer.Header.GenerationSeed), index, "terrain", "biome"));
                var selection = oceans[index] ? null : CelestialPlanetTerrain.SelectBiome(terrain, temperature, rainfall, f.ElevationMeters, coords.y, f.Roughness, pick);
                string biome = selection?.BiomeId ?? terrain.OceanBiomeId;
                // 规则显式 hilliness 覆盖海拔/起伏推导；海洋格保持 Flat。种子与抽样顺序不变。
                Hilliness hills = oceans[index] ? Hilliness.Flat : selection!.Hilliness is int hilliness ? (Hilliness)hilliness :
                    f.MountainScore > .75 ? Hilliness.Impassable : f.MountainScore > .5 ? Hilliness.Mountainous :
                    f.MountainScore > .3 ? Hilliness.LargeHills : f.Roughness > .5 ? Hilliness.SmallHills : Hilliness.Flat;
                layer.Tiles.Add(new SurfaceTile(new PlanetTile(index, layer)) { PrimaryBiome = biomes[biome], elevation = (float)f.ElevationMeters,
                    hilliness = hills, temperature = (float)temperature, rainfall = (float)rainfall, swampiness = (float)humidity });
            }
            CelestialPlanetHydrologyHooks.Install();
            if ((terrain.AllowRivers || terrain.AllowLakes) && terrain.OceanBiomeId != "Ocean") throw new InvalidOperationException("Native hydrology requires the existing Ocean biome.");
            // 单通道：Parameterized 只写参数化 tile 与地标；Rivers/Lakes 等原生步骤统一由 GenerateNativeSteps
            // 按 profile.WorldGenSteps 各执行一次（构造器已禁止 Terrain 并校验 flag 与步骤成对）。
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
}
