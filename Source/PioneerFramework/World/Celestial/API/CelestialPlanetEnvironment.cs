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
        bool UsesClimateWind = false, bool HasAtmosphere = false, double? SeasonalTemperatureCelsius = null);
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

        public static double Temperature(CelestialClimateConfiguration climate, CelestialSurfaceEnvironmentQuery query,
            double dayLengthTicks, double signedSun, double signedSunQuadrature = 0, double angularSpeed = 0)
        {
            if (!double.IsFinite(query.DisplayTicksAbs) || !double.IsFinite(query.LongitudeDegrees) ||
                !double.IsFinite(query.LatitudeDegrees) || Math.Abs(query.LatitudeDegrees) > 90 || !double.IsFinite(query.AltitudeMeters))
                throw new ArgumentOutOfRangeException(nameof(query));
            if (!double.IsFinite(dayLengthTicks) || dayLengthTicks <= 0) throw new ArgumentOutOfRangeException(nameof(dayLengthTicks));
            double phase = 2 * Math.PI * ((query.DisplayTicksAbs % dayLengthTicks) / dayLengthTicks + (query.LongitudeDegrees % 360) / 360 - .5);
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
