using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public enum CelestialClimateMode { Authored, CommittedIllumination }
    public sealed record CelestialClimatePoint(double LatitudeDegrees, double OffsetCelsius);

    /// <summary>Frozen, bounded surface climate. Latitude knots clamp at endpoints; no interior thermal state.</summary>
    public sealed class CelestialClimateConfiguration
    {
        public CelestialClimateMode Mode { get; }
        public double MeanTemperatureCelsius { get; }
        public IReadOnlyList<CelestialClimatePoint> LatitudeCurve { get; }
        public double LapseCelsiusPerMeter { get; }
        public double DiurnalAmplitudeCelsius { get; }
        public double SeasonalAmplitudeCelsius { get; }
        public double YearLengthTicks { get; }
        public double ThermalResponseSeconds { get; }
        public double IlluminationTemperatureCelsius { get; }
        public double LightMultiplier { get; }
        public double WindSpeed { get; }
        public double RainfallMillimeters { get; }
        public double Humidity { get; }
        public CelestialClimateConfiguration(CelestialClimateMode mode, double meanTemperatureCelsius,
            IReadOnlyList<CelestialClimatePoint> latitudeCurve, double lapseCelsiusPerMeter,
            double diurnalAmplitudeCelsius, double seasonalAmplitudeCelsius, double yearLengthTicks,
            double thermalResponseSeconds, double illuminationTemperatureCelsius, double lightMultiplier,
            double windSpeed, double rainfallMillimeters, double humidity)
        {
            if (!Enum.IsDefined(mode) || latitudeCurve == null || latitudeCurve.Count < 2 ||
                new[] { meanTemperatureCelsius, lapseCelsiusPerMeter, diurnalAmplitudeCelsius, seasonalAmplitudeCelsius,
                    yearLengthTicks, thermalResponseSeconds, illuminationTemperatureCelsius, lightMultiplier, windSpeed,
                    rainfallMillimeters, humidity }.Any(x => !double.IsFinite(x)) ||
                meanTemperatureCelsius < -273.15 || meanTemperatureCelsius > 10000 || yearLengthTicks < 1e-6 || yearLengthTicks > 1e20 || thermalResponseSeconds < 0 || thermalResponseSeconds > 1e12 ||
                Math.Abs(lapseCelsiusPerMeter) > 1 || diurnalAmplitudeCelsius > 10000 || seasonalAmplitudeCelsius > 10000 ||
                illuminationTemperatureCelsius > 10000 || lightMultiplier > 1e12 || windSpeed > 1e6 || rainfallMillimeters > 1000000 / 1.5 ||
                diurnalAmplitudeCelsius < 0 || seasonalAmplitudeCelsius < 0 || illuminationTemperatureCelsius < 0 ||
                lightMultiplier < 0 || windSpeed < 0 || rainfallMillimeters < 0 || humidity < 0 || humidity > 1)
                throw new ArgumentException("Invalid climate configuration.");
            double previous = -1;
            foreach (var point in latitudeCurve)
            {
                if (point == null || !double.IsFinite(point.LatitudeDegrees) || !double.IsFinite(point.OffsetCelsius) ||
                    point.LatitudeDegrees <= previous || point.LatitudeDegrees < 0 || point.LatitudeDegrees > 90 || Math.Abs(point.OffsetCelsius) > 10000)
                    throw new ArgumentException("Latitude curve requires strictly increasing finite knots in [0,90].");
                previous = point.LatitudeDegrees;
            }
            Mode = mode; MeanTemperatureCelsius = meanTemperatureCelsius;
            LatitudeCurve = Array.AsReadOnly(latitudeCurve.ToArray()); LapseCelsiusPerMeter = lapseCelsiusPerMeter;
            DiurnalAmplitudeCelsius = diurnalAmplitudeCelsius; SeasonalAmplitudeCelsius = seasonalAmplitudeCelsius;
            YearLengthTicks = yearLengthTicks; ThermalResponseSeconds = thermalResponseSeconds;
            IlluminationTemperatureCelsius = illuminationTemperatureCelsius; LightMultiplier = lightMultiplier;
            WindSpeed = windSpeed; RainfallMillimeters = rainfallMillimeters; Humidity = humidity;
        }
        public double LatitudeOffset(double latitude)
        {
            latitude = Math.Abs(latitude);
            if (latitude <= LatitudeCurve[0].LatitudeDegrees) return LatitudeCurve[0].OffsetCelsius;
            for (int i = 1; i < LatitudeCurve.Count; i++)
                if (latitude <= LatitudeCurve[i].LatitudeDegrees)
                {
                    var a = LatitudeCurve[i - 1]; var b = LatitudeCurve[i];
                    return a.OffsetCelsius + (b.OffsetCelsius - a.OffsetCelsius) * (latitude - a.LatitudeDegrees) / (b.LatitudeDegrees - a.LatitudeDegrees);
                }
            return LatitudeCurve[^1].OffsetCelsius;
        }
    }

    public sealed record CelestialSurfaceEnvironmentQuery(double DisplayTicksAbs, double LongitudeDegrees,
        double LatitudeDegrees, double AltitudeMeters = 0, string? BiomeId = null);
    public sealed record CelestialSurfaceEnvironmentSample(double? TemperatureCelsius, double SunGlow,
        double WindSpeed, double RainfallMillimeters, double Humidity, double? PressurePascals,
        double? OxygenPartialPressurePascals, double ToxicityPerDay, double RadiationPerDay,
        double? GravityMetersPerSecondSquared, double SunEast, double SunNorth, double SunUp,
        double PhysicalSeconds, bool NativeVacuum, CelestialExposureConfiguration? Exposure, bool UsesCommittedLight = false,
        bool UsesClimateWind = false, bool HasAtmosphere = false, double? SeasonalTemperatureCelsius = null)
    {
        /// <summary>
        /// 由**物理太阳**在本地 ENU 的分量反算本地太阳时（0..1，0.5 = 本地正午）。
        /// 只在 <see cref="UsesCommittedLight"/> 的物理样本上有意义；日历回退请继续用 <see cref="PF_SunMath.EvaluateCycle"/>。
        /// 2026-11-23：v2 的 DayPercent 不能再拿日历相位（否则天空 shader 与太阳方向/亮度是两套时钟）。
        /// </summary>
        public double DayPercentAt(double longitudeDegrees, double latitudeDegrees)
        {
            if (!double.IsFinite(longitudeDegrees) || !double.IsFinite(latitudeDegrees) || Math.Abs(latitudeDegrees) > 90)
                throw new ArgumentOutOfRangeException(nameof(longitudeDegrees));
            if (!double.IsFinite(SunEast) || !double.IsFinite(SunNorth) || !double.IsFinite(SunUp))
                throw new ArgumentOutOfRangeException(nameof(longitudeDegrees));
            double lon = longitudeDegrees * Math.PI / 180, lat = latitudeDegrees * Math.PI / 180;
            double sinLon = Math.Sin(lon), cosLon = Math.Cos(lon), sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
            // 本地 ENU 基（与 CompPlanetEnvironment.SampleSurface 同一套）还原体固系太阳方向。
            double x = SunEast * cosLon - SunNorth * sinLon * sinLat + SunUp * sinLon * cosLat;
            double z = SunEast * sinLon + SunNorth * cosLon * sinLat - SunUp * cosLon * cosLat;
            // RW 经度约定：0 = 世界 -z，东 = +x ⇒ λ = atan2(x, -z)。
            double subsolar = Math.Atan2(x, -z);
            double value = .5 + (lon - subsolar) / (2 * Math.PI);
            return value - Math.Floor(value);
        }
    }
    public interface ICelestialSurfaceEnvironmentCapability
    {
        CelestialSurfaceEnvironmentSample SampleSurface(CelestialSurfaceEnvironmentQuery query);
    }

    public static class CelestialClimateModel
    {
        /// <summary>Periodic first-order response, evaluated analytically. Reads never advance state or accumulate time.</summary>
        public static double Harmonic(double phase, double angularFrequency, double responseSeconds)
        {
            if (responseSeconds == 0) return Math.Cos(phase);
            double lag = angularFrequency * responseSeconds;
            if (Math.Abs(lag) < 1e150)
                return (Math.Cos(phase) + lag * Math.Sin(phase)) / (1 + lag * lag);
            double inverse = 1 / lag;
            return (Math.Cos(phase) * inverse + Math.Sin(phase)) * inverse / (1 + inverse * inverse);
        }

        /// <summary>Long-term field for native seasonal consumers; excludes instantaneous sun/day and map conditions.</summary>
        public static double SeasonalTemperature(CelestialClimateConfiguration climate, CelestialSurfaceEnvironmentQuery query)
        {
            if (!double.IsFinite(query.DisplayTicksAbs) || !double.IsFinite(query.LongitudeDegrees) ||
                !double.IsFinite(query.LatitudeDegrees) || Math.Abs(query.LatitudeDegrees) > 90 || !double.IsFinite(query.AltitudeMeters))
                throw new ArgumentOutOfRangeException(nameof(query));
            double temperature = climate.MeanTemperatureCelsius + climate.LatitudeOffset(query.LatitudeDegrees) -
                query.AltitudeMeters * climate.LapseCelsiusPerMeter;
            temperature += climate.SeasonalAmplitudeCelsius * Math.Sin(query.LatitudeDegrees * Math.PI / 180) *
                Harmonic(2 * Math.PI * (query.DisplayTicksAbs % climate.YearLengthTicks) / climate.YearLengthTicks,
                    2 * Math.PI / climate.YearLengthTicks, climate.ThermalResponseSeconds);
            return Math.Clamp(temperature, -273.15, 10000);
        }

        /// <summary>
        /// 地表温度。<paramref name="authoredPhaseRadians"/> 允许适配层用**已提交物理太阳的实际小时角**（0 = 本地正午）
        /// 代替日历 tick 驱动的 Authored 日相位；传 null 保留旧日历相位。只有 Authored 分支读它。
        /// 2026-11-07 P1-1：存在物理绑定时光照走物理快照，若日相位仍用日历 tick，温度峰值会落在物理日出/日落。
        /// </summary>
        public static double Temperature(CelestialClimateConfiguration climate, CelestialSurfaceEnvironmentQuery query,
            double dayLengthTicks, double signedSun, double signedSunQuadrature = 0, double angularSpeed = 0,
            double? authoredPhaseRadians = null)
        {
            if (!double.IsFinite(query.DisplayTicksAbs) || !double.IsFinite(query.LongitudeDegrees) ||
                !double.IsFinite(query.LatitudeDegrees) || Math.Abs(query.LatitudeDegrees) > 90 || !double.IsFinite(query.AltitudeMeters))
                throw new ArgumentOutOfRangeException(nameof(query));
            if (!double.IsFinite(dayLengthTicks) || dayLengthTicks <= 0) throw new ArgumentOutOfRangeException(nameof(dayLengthTicks));
            if (authoredPhaseRadians is { } supplied && !double.IsFinite(supplied)) throw new ArgumentOutOfRangeException(nameof(authoredPhaseRadians));
            double phase = authoredPhaseRadians ??
                2 * Math.PI * ((query.DisplayTicksAbs % dayLengthTicks) / dayLengthTicks + (query.LongitudeDegrees % 360) / 360 - .5);
            double season = 2 * Math.PI * (query.DisplayTicksAbs % climate.YearLengthTicks) / climate.YearLengthTicks;
            double temperature = climate.MeanTemperatureCelsius + climate.LatitudeOffset(query.LatitudeDegrees) -
                query.AltitudeMeters * climate.LapseCelsiusPerMeter;
            if (climate.Mode == CelestialClimateMode.Authored)
                temperature += climate.DiurnalAmplitudeCelsius * Math.Cos(query.LatitudeDegrees * Math.PI / 180) *
                    Harmonic(phase, 2 * Math.PI / dayLengthTicks, climate.ThermalResponseSeconds);
            else
            {
                double lag = climate.ThermalResponseSeconds == 0 ? 0 : angularSpeed * climate.ThermalResponseSeconds;
                double inverse = 1 / lag;
                double response = Math.Abs(lag) < 1e150 ? (signedSun - lag * signedSunQuadrature) / (1 + lag * lag) :
                    (signedSun * inverse - signedSunQuadrature) * inverse / (1 + inverse * inverse);
                temperature += climate.IlluminationTemperatureCelsius * Math.Max(0, response) * climate.LightMultiplier;
            }
            temperature += climate.SeasonalAmplitudeCelsius * Math.Sin(query.LatitudeDegrees * Math.PI / 180) *
                Harmonic(season, 2 * Math.PI / climate.YearLengthTicks, climate.ThermalResponseSeconds);
            return Math.Clamp(temperature, -273.15, 10000);
        }
    }
}
