using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    /// <summary>Native consumer inputs scoped by the actual tile's layer object and frozen owner header. Shared Defs remain immutable.</summary>
    [StaticConstructorOnStartup]
    public static partial class CelestialPlanetMapHooks
    {
        private static bool installed;
        static CelestialPlanetMapHooks() => Install();
        public static void Install()
        {
            if (installed) return;
            var h = new Harmony("PioneerFramework.Celestial.PlanetMapDiversity");
            h.Patch(AccessTools.Method(typeof(World), nameof(World.NaturalRockTypesIn)), prefix: new(typeof(CelestialPlanetMapHooks), nameof(NaturalRocks)));
            h.Patch(AccessTools.PropertyGetter(typeof(Tile), nameof(Tile.PlantDensityFactor)), postfix: new(typeof(CelestialPlanetMapHooks), nameof(PlantDensity)));
            h.Patch(AccessTools.PropertyGetter(typeof(Tile), nameof(Tile.AnimalDensity)), postfix: new(typeof(CelestialPlanetMapHooks), nameof(AnimalDensity)));
            h.Patch(AccessTools.PropertyGetter(typeof(SurfaceTile), nameof(SurfaceTile.WaterCovered)), postfix: new(typeof(CelestialPlanetMapHooks), nameof(WaterCovered)));
            h.Patch(AccessTools.Method(typeof(World), nameof(World.HasCaves)), postfix: new(typeof(CelestialPlanetMapHooks), nameof(HasCaves)));
            h.Patch(AccessTools.Method(typeof(TileMutatorWorker_Caves), nameof(TileMutatorWorker_Caves.GeneratePostElevationFertility)), prefix: new(typeof(CelestialPlanetMapHooks), nameof(Caves)));
            h.Patch(AccessTools.Method(typeof(GenStep_ScatterLumpsMineable), nameof(GenStep_ScatterLumpsMineable.Generate)),
                prefix: new(typeof(CelestialPlanetMapHooks), nameof(BeginMinerals)), finalizer: new(typeof(CelestialPlanetMapHooks), nameof(EndMinerals)));
            h.Patch(AccessTools.Method(typeof(GenStep_ScatterLumpsMineable), "ChooseThingDef"), prefix: new(typeof(CelestialPlanetMapHooks), nameof(ChooseMineral)));
            h.Patch(AccessTools.Method(typeof(GenStep_ScatterLumpsMineable), "CalculateFinalCount"), postfix: new(typeof(CelestialPlanetMapHooks), nameof(MineralCount)));
            h.Patch(AccessTools.PropertyGetter(typeof(WeatherDecider), nameof(WeatherDecider.WeatherCommonalities)), prefix: new(typeof(CelestialPlanetMapHooks), nameof(WeatherCandidates)));
            h.Patch(AccessTools.Method(typeof(WeatherDecider), "CurrentWeatherCommonality"), transpiler: new(typeof(CelestialPlanetMapHooks), nameof(WeatherTableInput)));
            InstallEcology(h);
            h.Patch(AccessTools.Method(typeof(MapGenUtility), nameof(MapGenUtility.TerrainFrom)),
                prefix: new(typeof(CelestialPlanetMapHooks), nameof(SoilFertility)), postfix: new(typeof(CelestialPlanetMapHooks), nameof(SoilTerrain)));
            h.Patch(AccessTools.Method(typeof(StorytellerComp), "IncidentChanceFinal"), postfix: new(typeof(CelestialPlanetMapHooks), nameof(IncidentChance)));
            installed = true;
        }
        internal static T Definition<T>(string id) where T : Def => DefDatabase<T>.GetNamedSilentFail(id) ?? throw new InvalidOperationException("CelestialReferenceMissing: " + id);
        internal static CelestialWeightedReference Pick(IReadOnlyList<CelestialWeightedReference> candidates, double unit)
        {
            double remaining = unit * candidates.Sum(c => c.Weight);
            foreach (var candidate in candidates) { remaining -= candidate.Weight; if (remaining < 0) return candidate; }
            return candidates[^1];
        }
        public static bool NaturalRocks(PlanetTile tile, ref IEnumerable<ThingDef> __result)
        {
            if (!CelestialMapGeneration.TryGetDiversity(tile, out var diversity) || diversity.Rocks.Count == 0) return true;
            var layer = (CelestialSurfaceLayer)tile.Layer;
            var candidates = diversity.Rocks.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
            var result = new List<ThingDef>();
            int count = Math.Min(candidates.Count, 1 + (int)(CelestialMapGeneration.GeologyFactor(tile, diversity) * 1.5));
            ulong seed = CelestialSeed.DeriveTile(unchecked((ulong)layer.Header.GenerationSeed), tile.tileId, "map", "rocks");
            for (int i = 0; i < count; i++)
            {
                var selected = Pick(candidates, CelestialParameterSampler.SampleDouble(DoubleParameterSpec.Uniform(0, 1), seed + (ulong)i));
                result.Add(Definition<ThingDef>(selected.Id)); candidates.Remove(selected);
            }
            __result = result; return false;
        }
        public static void PlantDensity(Tile __instance, ref float __result)
        {
            if (CelestialMapGeneration.TryGetDiversity(__instance.tile, out var d)) __result *= (float)(d.PlantDensity * CelestialMapGeneration.GeologyFactor(__instance.tile, d));
        }
        public static void AnimalDensity(Tile __instance, ref float __result)
        {
            if (CelestialMapGeneration.TryGetDiversity(__instance.tile, out var d)) __result *= (float)(d.AnimalDensity * CelestialMapGeneration.GeologyFactor(__instance.tile, d));
        }
        public static void HasCaves(PlanetTile tile, ref bool __result)
        { if (CelestialMapGeneration.TryGetDiversity(tile, out var d) && !d.AllowCaves) __result = false; }
        public static void WaterCovered(SurfaceTile __instance, ref bool __result)
        {
            if (CelestialPlanetEnvironmentHooks.TryGetLayer(__instance.tile, out var layer) && CelestialMapGeneration.TryGetTerrain(layer, out var t))
                __result = __instance.PrimaryBiome.defName == t.OceanBiomeId || __instance.PrimaryBiome == BiomeDefOf.Lake;
        }
        public static bool Caves(Map map) => !CelestialMapGeneration.TryGetDiversity(map.Tile, out var d) || d.AllowCaves;
        [ThreadStatic] private static Map? mineralMap;
        public static void BeginMinerals(Map map, out Map? __state) { __state = mineralMap; mineralMap = map; }
        public static Exception? EndMinerals(Exception? __exception, Map? __state) { mineralMap = __state; return __exception; }
        public static bool ChooseMineral(GenStep_ScatterLumpsMineable __instance, ref ThingDef __result)
        {
            if (__instance.forcedDefToScatter != null || mineralMap == null || !CelestialMapGeneration.TryGetDiversity(mineralMap.Tile, out var d) || d.Minerals.Count == 0) return true;
            var candidates = d.Minerals.Where(r => __instance.maxValue == float.MaxValue || Definition<ThingDef>(r.Id).building.mineableThing.BaseMarketValue <= __instance.maxValue).ToArray();
            __result = candidates.Length == 0 ? null! : Definition<ThingDef>(Pick(candidates, Rand.Value).Id); return false;
        }
        public static void MineralCount(GenStep_ScatterLumpsMineable __instance, Map map, ref int __result)
        {
            if (__instance.forcedDefToScatter == null && CelestialMapGeneration.TryGetDiversity(map.Tile, out var d))
                __result = (int)Math.Min(int.MaxValue, Math.Round(__result * d.MineralAbundance * CelestialMapGeneration.GeologyFactor(map.Tile, d), MidpointRounding.ToEven));
        }
        private static readonly ConditionalWeakTable<CelestialPlanetMapDiversity, List<WeatherCommonalityRecord>> weather = new();
        private static List<WeatherCommonalityRecord> WeatherRecords(BiomeDef native, Map map)
        {
            if (!CelestialMapGeneration.TryGetDiversity(map.Tile, out var d) || d.Weather.Count == 0) return native.baseWeatherCommonalities;
            return weather.GetValue(d, static profile => profile.Weather.Select(r => new WeatherCommonalityRecord { weather = Definition<WeatherDef>(r.Id), commonality = (float)r.Weight }).ToList());
        }
        public static bool WeatherCandidates(Map ___map, ref IEnumerable<WeatherCommonalityRecord> __result)
        {
            if (!CelestialMapGeneration.TryGetDiversity(___map.Tile, out var d) || d.Weather.Count == 0) return true;
            __result = WeatherRecords(___map.Biome, ___map); return false;
        }
        public static IEnumerable<CodeInstruction> WeatherTableInput(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            var field = AccessTools.Field(typeof(BiomeDef), nameof(BiomeDef.baseWeatherCommonalities));
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, field))
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(WeatherDecider), "map"));
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CelestialPlanetMapHooks), nameof(WeatherRecords)));
                }
                else yield return instruction;
            }
            if (replaced == 0) throw new InvalidOperationException("NativeWeatherTableContractMissing");
        }
        public static void SoilFertility(Map map, ref float fertility)
        {
            if (CelestialMapGeneration.TryGetDiversity(map.Tile, out var d))
                fertility = (float)Math.Clamp(fertility * d.SoilFertility * CelestialMapGeneration.GeologyFactor(map.Tile, d), 0, 10);
        }
        public static void IncidentChance(IncidentDef def, IIncidentTarget target, ref float __result)
        {
            if (target is not Map map || !CelestialMapGeneration.TryGetDiversity(map.Tile, out var d)) return;
            double factor = CelestialPlanetMapDiversity.Weight(d.Incidents, def.defName);
            if (d.Conditions.Count > 0 && def.Worker is IncidentWorker_MakeGameCondition conditionWorker)
            {
                var condition = conditionWorker.GetGameConditionDef(new IncidentParms { target = target });
                factor *= condition == null ? 0 : CelestialPlanetMapDiversity.Weight(d.Conditions, condition.defName);
            }
            __result = (float)Math.Clamp(__result * factor, 0, 1e12);
        }
        private static readonly ConditionalWeakTable<CelestialPlanetMapDiversity, (TerrainDef Terrain, double Weight)[]> soils = new();
        public static void SoilTerrain(IntVec3 c, Map map, float fertility, bool preferRock, ref TerrainDef __result)
        {
            if (preferRock || __result == null || __result.IsWater || __result.IsRock || __result.IsIce ||
                !CelestialMapGeneration.TryGetDiversity(map.Tile, out var d) || d.Soils.Count == 0) return;
            var candidates = soils.GetValue(d, static p => p.Soils.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => (Definition<TerrainDef>(r.Id), r.Weight)).ToArray());
            double total = 0;
            foreach (var candidate in candidates) total += candidate.Weight / (.05 + Math.Abs(candidate.Terrain.fertility - fertility));
            // 2026-11-03 P4: the hand-written 32-bit hash was retired in favour of CelestialSeed's standard
            // derivation. Soil distribution intentionally changes versus older builds and is frozen by
            // CelestialMapDeterminismTests (deterministic, not a regression).
            ulong soilWord = CelestialSeed.DeriveCell(unchecked((ulong)((CelestialSurfaceLayer)map.Tile.Layer).Header.GenerationSeed),
                map.Tile.tileId, c.x, c.z, "map", "soil");
            double remaining = (soilWord >> 11) / 9007199254740991d * total;
            foreach (var candidate in candidates)
            {
                remaining -= candidate.Weight / (.05 + Math.Abs(candidate.Terrain.fertility - fertility));
                if (remaining < 0) { __result = candidate.Terrain; return; }
            }
            __result = candidates[^1].Terrain;
        }
    }
}
