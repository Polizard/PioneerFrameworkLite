using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Celestial
{
    [StaticConstructorOnStartup]
    public static class CelestialPlanetEnvironmentHooks
    {
        private static bool installed;
        private static readonly Func<GameConditionManager, float> temperatureOffset =
            AccessTools.Method(typeof(GameConditionManager), "AggregateTemperatureOffset").CreateDelegate<Func<GameConditionManager, float>>();
        // 2026-11-07 C-2：热路径不再逐次 Game.GetComponent（组件表线性扫描）。
        private static readonly GameComponentRef<TimeScaleManager> timeScaleRef = new();
        // 2026-11-07 C-2：同 (world, layer, tile, 显示时刻) 的采样结果缓存一次；失败不缓存。
        private static bool sampleCacheValid;
        private static int sampleCacheWorld = -1, sampleCacheLayer = -1, sampleCacheTile = -1;
        private static long sampleCacheTime;
        private static CelestialSurfaceEnvironmentSample sampleCache = null!;
        static CelestialPlanetEnvironmentHooks() => Install();
        public static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("PioneerFramework.Celestial.PlanetEnvironment");
            void Prefix(Type owner, string method, string hook) => harmony.Patch(AccessTools.Method(owner, method), prefix: new HarmonyMethod(typeof(CelestialPlanetEnvironmentHooks), hook));
            Prefix(typeof(TileTemperaturesComp), nameof(TileTemperaturesComp.GetOutdoorTemp), nameof(OutdoorTemperature));
            Prefix(typeof(TileTemperaturesComp), nameof(TileTemperaturesComp.GetSeasonalTemp), nameof(SeasonalTemperature));
            Prefix(typeof(TileTemperaturesComp), nameof(TileTemperaturesComp.OutdoorTemperatureAt), nameof(OutdoorTemperatureAt));
            Prefix(typeof(WindManager), "BaseWindSpeedAt", nameof(BaseWindSpeed));
            harmony.Patch(AccessTools.Method(typeof(WindManager), nameof(WindManager.WindManagerTick)),
                postfix: new HarmonyMethod(typeof(CelestialPlanetEnvironmentHooks), nameof(FinishWindTick)));
            harmony.Patch(AccessTools.Method(typeof(GenCelestial), nameof(GenCelestial.GetLightSourceInfo)),
                postfix: new HarmonyMethod(typeof(CelestialPlanetEnvironmentHooks), nameof(LightSource)));
            harmony.Patch(AccessTools.Method(typeof(JobGiver_SeekSafeTemperature), nameof(JobGiver_SeekSafeTemperature.TryGetAllowedCellInRegion)),
                postfix: new HarmonyMethod(typeof(CelestialPlanetEnvironmentHooks), nameof(SafeTemperatureDestination)));
            installed = true;
        }
        internal static double DisplayTime => timeScaleRef.Get()?.GetDisplayTicksAbs() ?? Find.TickManager?.TicksAbs ?? 0;
        internal static bool TryGetLayer(PlanetTile tile, out PlanetLayer layer)
        {
            layer = null!;
            if (!tile.Valid || Find.WorldGrid == null) return false;
            try { layer = tile.Layer; return layer != null; }
            catch (KeyNotFoundException) { return false; }
        }
        public static bool TrySample(PlanetTile tile, out CelestialSurfaceEnvironmentSample sample, double? displayTime = null)
        {
            sample = null!;
            if (!TryGetLayer(tile, out var layer) || layer is not CelestialSurfaceLayer) return false;
            if (displayTime is null)
            {
                double now = DisplayTime;
                int world = Find.WorldGrid == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Find.WorldGrid);
                long bits = BitConverter.DoubleToInt64Bits(now);
                if (sampleCacheValid && sampleCacheWorld == world && sampleCacheLayer == layer.LayerID &&
                    sampleCacheTile == tile.tileId && sampleCacheTime == bits)
                { sample = sampleCache; return true; }
                if (!SampleNow(tile, layer, now, out sample)) return false;
                sampleCacheWorld = world; sampleCacheLayer = layer.LayerID; sampleCacheTile = tile.tileId;
                sampleCacheTime = bits; sampleCache = sample; sampleCacheValid = true;
                return true;
            }
            return SampleNow(tile, layer, displayTime.Value, out sample);
        }
        private static bool SampleNow(PlanetTile tile, PlanetLayer layer, double displayTime, out CelestialSurfaceEnvironmentSample sample)
        {
            sample = null!;
            var coords = Find.WorldGrid.LongLatOf(tile);
            return CelestialEnvironmentBridge.TrySample(layer, new(displayTime, coords.x, coords.y,
                Find.WorldGrid[tile].elevation, Find.WorldGrid[tile].PrimaryBiome?.defName), out sample);
        }
        public static bool OutdoorTemperature(PlanetTile tile, ref float __result)
        {
            if (!TrySample(tile, out var sample) || !sample.TemperatureCelsius.HasValue) return true;
            __result = (float)sample.TemperatureCelsius.Value;
            var map = Current.Game?.FindMap(tile);
            if (map?.gameConditionManager != null) __result += temperatureOffset(map.gameConditionManager);
            return false;
        }
        public static bool OutdoorTemperatureAt(PlanetTile tile, int absTick, ref float __result)
        {
            // 2026-11-07 C-4：原生 absTick 先映射到显示时钟，避免 Fast/Combat 档与 GetOutdoorTemp 不同源。
            if (!TrySample(tile, out var sample, DisplayTickForNative(absTick)) || !sample.TemperatureCelsius.HasValue) return true;
            __result = (float)sample.TemperatureCelsius.Value; return false;
        }
        private static double DisplayTickForNative(int absTick) =>
            timeScaleRef.Get()?.GetDisplayTicksForNativeTick(absTick) ?? absTick;
        public static bool SeasonalTemperature(PlanetTile tile, ref float __result)
        {
            if (!TrySample(tile, out var sample) || !sample.SeasonalTemperatureCelsius.HasValue) return true;
            __result = (float)sample.SeasonalTemperatureCelsius.Value; return false;
        }
        public static bool BaseWindSpeed(Map ___map, ref float __result)
        {
            if (___map == null || !TrySample(___map.Tile, out var sample) || !sample.UsesClimateWind) return true;
            __result = (float)sample.WindSpeed; return false;
        }
        public static void FinishWindTick(Map ___map, ref float ___cachedWindSpeed)
        {
            // Weather, conditions and wind sources remain native. No air means none of them can produce wind.
            if (___map != null && TrySample(___map.Tile, out var sample) && sample.UsesClimateWind && !sample.HasAtmosphere)
                ___cachedWindSpeed = 0;
        }
        public static void LightSource(Map map, GenCelestial.LightType type, ref GenCelestial.LightInfo __result)
        {
            if (map == null || !TrySample(map.Tile, out var sample) || !sample.UsesCommittedLight) return;
            if (type == GenCelestial.LightType.LightingMoon) { __result.intensity = 0; return; }
            // RimWorld consumes a two-dimensional projected light vector, not a spherical astronomical angle.
            // Use local east/north components with bounded grazing elevation; no second rotation clock.
            double height = Math.Max(.1, Math.Abs(sample.SunUp));
            __result.vector = new Vector2((float)(-sample.SunEast / height), (float)(-sample.SunNorth / height - 1.5));
            __result.intensity = (float)Math.Clamp(sample.SunGlow, 0, 1);
        }
        public static void SafeTemperatureDestination(Pawn pawn, ref IntVec3 cell, ref bool __result)
        {
            if (__result && pawn?.MapHeld?.GetComponent<MapComponent_CelestialEnvironment>() is { } environment &&
                environment.IsDangerous(pawn, cell)) { cell = IntVec3.Invalid; __result = false; }
        }
    }
}
