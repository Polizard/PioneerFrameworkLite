using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    [StaticConstructorOnStartup]
    public static class CalendarAndSunPatch
    {
        private static readonly FieldInfo SunLatCurveField;
        /// <summary>缓存的 CelestialSunGlowPercent 委托，避免每 tick 反射</summary>
        private static readonly Func<float, int, float, float> CelestialSunGlowPercent;

        static CalendarAndSunPatch()
        {
            Harmony harmony = new Harmony("com.pioneerframework.calendarandsun");

            var prefix = new HarmonyMethod(typeof(CalendarAndSunPatch), nameof(GenDate_TicksAbs_Prefix));

            harmony.Patch(AccessTools.Method(typeof(GenDate), nameof(GenDate.DayOfYear), new[] { typeof(long), typeof(float) }), prefix: prefix);
            harmony.Patch(AccessTools.Method(typeof(GenDate), nameof(GenDate.DayPercent), new[] { typeof(long), typeof(float) }), prefix: prefix);
            harmony.Patch(AccessTools.Method(typeof(GenDate), nameof(GenDate.HourInteger), new[] { typeof(long), typeof(float) }), prefix: prefix);
            harmony.Patch(AccessTools.Method(typeof(GenDate), nameof(GenDate.DayOfTwelfth), new[] { typeof(long), typeof(float) }), prefix: prefix);
            harmony.Patch(AccessTools.Method(typeof(GenDate), nameof(GenDate.Quadrum), new[] { typeof(long), typeof(float) }), prefix: prefix);
            harmony.Patch(AccessTools.Method(typeof(GenDate), nameof(GenDate.Year), new[] { typeof(long), typeof(float) }), prefix: prefix);

            harmony.Patch(AccessTools.Method(typeof(GenDate), nameof(GenDate.Season), new[] { typeof(long), typeof(Vector2) }),
                prefix: new HarmonyMethod(typeof(CalendarAndSunPatch), nameof(GenDate_Season_Prefix)));

            harmony.Patch(AccessTools.Method(typeof(GenDate), nameof(GenDate.DateReadoutStringAt), new[] { typeof(long), typeof(Vector2) }),
                prefix: new HarmonyMethod(typeof(CalendarAndSunPatch), nameof(GenDate_DateReadoutStringAt_Prefix)));

            // 殖民地 Map 光照
            harmony.Patch(AccessTools.Method(typeof(GenCelestial), nameof(GenCelestial.CurCelestialSunGlow), new[] { typeof(Map) }),
                prefix: new HarmonyMethod(typeof(CalendarAndSunPatch), nameof(CurCelestialSunGlow_Prefix)));

            // 殖民地 Map 太阳方向（GetLightSourceInfo → GenLocalDate.DayPercent(Map)）
            // 必须也使用天体自转周期，否则太阳位置和亮度不同步
            harmony.Patch(AccessTools.Method(typeof(GenLocalDate), nameof(GenLocalDate.DayPercent), new[] { typeof(Map) }),
                prefix: new HarmonyMethod(typeof(CalendarAndSunPatch), nameof(GenLocalDate_DayPercent_Map_Prefix)));

            // 世界地图太阳位置
            SunLatCurveField = AccessTools.Field(typeof(GenCelestial), "SunOffsetFractionFromLatitudeCurve");

            harmony.Patch(AccessTools.Method(typeof(GenCelestial), nameof(GenCelestial.CurSunPositionInWorldSpace)),
                prefix: new HarmonyMethod(typeof(CalendarAndSunPatch), nameof(CurSunPositionInWorldSpace_Prefix)));

            // 缓存 CelestialSunGlowPercent 为委托，避免每 tick 反射
            var glowPercentMethod = AccessTools.Method(typeof(GenCelestial), "CelestialSunGlowPercent",
                new[] { typeof(float), typeof(int), typeof(float) });
            if (glowPercentMethod != null)
            {
                CelestialSunGlowPercent = (Func<float, int, float, float>)
                    Delegate.CreateDelegate(typeof(Func<float, int, float, float>), glowPercentMethod);
            }

            Log.Message("[PioneerFramework] Patched GenDate and GenCelestial for time scaling");
        }

        /// <summary>
        /// 日历 tick 缩放：始终使用 TimeScaleManager.GetDisplayTicksAbs()，
        /// 无论 Normal/Fast 模式，保证切换时间流速时数值连续。
        /// 始终以地球时间为准，不叠加天体自转缩放。
        /// </summary>
        private static long GetTimeScaledTicksAbs(long originalTicksAbs)
        {
            var timeScaleManager = Current.Game?.GetComponent<TimeScaleManager>();
            return (timeScaleManager != null)
                ? (long)timeScaleManager.GetDisplayTicksAbs()
                : originalTicksAbs;
        }

        /// <summary>
        /// 时间倍速 + 天体自转周期缩放（太阳光用）。
        /// dayLengthSource: 天体自转周期来源。传 null 则使用当前选中天体 layer。
        /// </summary>
        private static long GetCelestialScaledTicksAbs(float? dayLengthSource = null)
        {
            var timeScaleManager = Current.Game?.GetComponent<TimeScaleManager>();
            long ticksAbs = (timeScaleManager != null)
                ? (long)timeScaleManager.GetDisplayTicksAbs()
                : Find.TickManager.TicksAbs;

            float dayLength = dayLengthSource ?? CelestialBodyLayerManager.GetCurrentDayLength();
            if (Math.Abs(dayLength - 60000f) > 10f)
            {
                ticksAbs = (long)(ticksAbs * (60000.0 / dayLength));
            }

            return ticksAbs;
        }

        public static bool GenDate_TicksAbs_Prefix(ref long absTicks)
        {
            absTicks = GetTimeScaledTicksAbs(absTicks);
            return true;
        }

        public static bool GenDate_Season_Prefix(ref long absTicks, ref Vector2 longLat)
        {
            absTicks = GetTimeScaledTicksAbs(absTicks);
            return true;
        }

        public static bool GenDate_DateReadoutStringAt_Prefix(ref long absTicks, ref Vector2 location)
        {
            absTicks = GetTimeScaledTicksAbs(absTicks);
            return true;
        }

        /// <summary>
        /// 殖民地 Map 光照：绕过 GenDate 链路，直接用天体缩放 ticks 调用 CelestialSunGlowPercent。
        /// 避免 GenDate_TicksAbs_Prefix 覆盖掉天体自转缩放后的 tick 值。
        /// </summary>
        public static bool CurCelestialSunGlow_Prefix(Map map, ref float __result)
        {
            if (map == null || map.Tile < 0) return true;
            if (map.Tile.Layer is RingWorld.RingSurfaceLayer)
            {
                __result = (float)RingWorld.RingClimate.SunGlow(RingWorld.RingWorldEnvironment.DisplayTicks);
                return false;
            }

            // 用 Map 所在 tile 的天体自转周期，而非当前查看的星球
            float dayLength = CelestialBodyLayerManager.GetDayLengthForMap(map);
            long celestialTicks = GetCelestialScaledTicksAbs(dayLength);
            Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);

            // 绕过 GenDate：等价于 GenDate.DayPercent / GenDate.DayOfYear
            // DayPercent = PositiveMod(adjustedTicks, 60000) / 60000f (fractional part of days)
            // DayOfYear  = PositiveModRemap(adjustedTicks, 60000, 60)    (0-based, 0~59)
            double adjustedTicks = (double)celestialTicks + (double)(longLat.x / 360f * 60000f);
            double days = adjustedTicks / 60000.0;

            // fractional part (safe for negative)
            float dayPercent = (float)(((days % 1.0) + 1.0) % 1.0);
            // 0-based day of year (0~59), matching GenDate.DayOfYear convention
            int dayOfYear = (int)(((Math.Floor(days) % 60.0) + 60.0) % 60.0);

            if (CelestialSunGlowPercent != null)
            {
                __result = CelestialSunGlowPercent(longLat.y, dayOfYear, dayPercent);
            }
            else
            {
                __result = 1f; // fallback：如果反射失败，返回满光照
            }
            return false;
        }

        /// <summary>
        /// GenLocalDate.DayPercent(Map) 的 Prefix。
        /// GetLightSourceInfo 通过此方法获取太阳方向，必须也使用天体自转周期，
        /// 否则太阳位置（方向）和亮度（glow）不同步，导致光源在太阳/月亮模式间跳变。
        /// </summary>
        public static bool GenLocalDate_DayPercent_Map_Prefix(Map map, ref float __result)
        {
            if (map == null || map.Tile < 0) return true;
            if (map.Tile.Layer is RingWorld.RingSurfaceLayer)
            {
                __result = (float)RingWorld.RingClimate.DayPercent(RingWorld.RingWorldEnvironment.DisplayTicks);
                return false;
            }

            float dayLength = CelestialBodyLayerManager.GetDayLengthForMap(map);
            if (Math.Abs(dayLength - 60000f) <= 10f) return true; // Earth，走原版

            // 获取 physicsTick 并缩放到天体自转周期
            var timeScaleManager = Current.Game?.GetComponent<TimeScaleManager>();
            long displayTicksAbs = (timeScaleManager != null)
                ? (long)timeScaleManager.GetDisplayTicksAbs()
                : Find.TickManager.TicksAbs;

            long celestialTicks = (long)(displayTicksAbs * (60000.0 / dayLength));
            Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
            double adjustedTicks = (double)celestialTicks + (double)(longLat.x / 360f * 60000f);

            __result = (float)(((adjustedTicks / 60000.0 % 1.0) + 1.0) % 1.0);
            return false;
        }

        /// <summary>
        /// 世界地图太阳位置：统一使用 physicsTick + 天体自转周期计算，
        /// 不经过 GenDate（避免 GenDate 补丁再次缩放 TimeScaleManager）。
        /// </summary>
        public static bool CurSunPositionInWorldSpace_Prefix(ref Vector3 __result)
        {
            long scaledTicks = GetCelestialScaledTicksAbs();
            double days = (double)scaledTicks / 60000.0;

            // 绕过 GenDate 直接计算（避免被 GenDate_TicksAbs_Prefix 二次缩放）
            // dayPercent = fractional_part(days), matching GenDate.DayPercent convention
            float dayPercent = (float)(((days % 1.0) + 1.0) % 1.0);
            // 使用等价于 GenDate.DayOfYear 的连续值（0-60），与 SunPositionUnmodified 兼容
            float dayOfYearF = (float)(((days % 60.0) + 60.0) % 60.0);

            // 复制 SunPositionUnmodified(dayOfYear, dayPercent, (0,0,-1), 0)
            Vector3 vector = new Vector3(0f, 0f, -1f) * 100f;
            float cosVal = -Mathf.Cos(dayOfYearF / 60f * Mathf.PI * 2f);
            var curve = SunLatCurveField?.GetValue(null) as SimpleCurve;
            vector.y += cosVal * 100f * (curve != null ? curve.Evaluate(0f) : 0f);
            vector = Quaternion.AngleAxis((dayPercent - 0.5f) * 360f, Vector3.up) * vector;
            __result = vector.normalized;
            return false;
        }
    }
}
