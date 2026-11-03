using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>Versioned instance data. Elevation is meters above the body's reference sphere, never native display radius.</summary>
    public sealed class CelestialPlanetTerrainConfiguration
    {
        public double ContinentScale { get; }
        public double ElevationAmplitudeMeters { get; }
        public double ElevationOffsetMeters { get; }
        public double Roughness { get; }
        public double MountainWeight { get; }
        public double? SeaLevelMeters { get; }
        public double? OceanCoverage { get; }
        public bool AllowRivers { get; }
        public bool AllowLakes { get; }
        public string OceanBiomeId { get; }
        public CelestialClimateConfiguration? Climate { get; }
        public IReadOnlyList<CelestialPlanetBiomeRule> BiomeRules { get; }
        public IReadOnlyList<CelestialWeightedReference> Landmarks { get; }
        public int FieldSeed { get; }
        public CelestialPlanetTerrainConfiguration(double continentScale = 2, double elevationAmplitudeMeters = 3000,
            double elevationOffsetMeters = 0, double roughness = .35, double mountainWeight = .5,
            double? seaLevelMeters = null, double? oceanCoverage = null, bool allowRivers = false, bool allowLakes = false,
            string oceanBiomeId = "Ocean", CelestialClimateConfiguration? climate = null,
            IReadOnlyList<CelestialPlanetBiomeRule>? biomeRules = null, IReadOnlyList<CelestialWeightedReference>? landmarks = null, int fieldSeed = 0)
        {
            Domain(continentScale, .01, 64, nameof(continentScale)); Domain(elevationAmplitudeMeters, 0, 100000, nameof(elevationAmplitudeMeters));
            Domain(elevationOffsetMeters, -100000, 100000, nameof(elevationOffsetMeters)); Domain(roughness, 0, 1, nameof(roughness));
            Domain(mountainWeight, 0, 1, nameof(mountainWeight));
            if (seaLevelMeters.HasValue && oceanCoverage.HasValue) throw new ArgumentException("Ambiguous water modes.");
            if (seaLevelMeters.HasValue) Domain(seaLevelMeters.Value, -200000, 200000, nameof(seaLevelMeters));
            if (oceanCoverage.HasValue) Domain(oceanCoverage.Value, 0, 1, nameof(oceanCoverage));
            ArgumentException.ThrowIfNullOrWhiteSpace(oceanBiomeId);
            if ((allowRivers || allowLakes) && oceanBiomeId != "Ocean") throw new ArgumentException("Native water steps require the existing Ocean biome.");
            if (climate != null)
            {
                Domain(climate.MeanTemperatureCelsius, -273.15, 10000, "terrain climate mean");
                Domain(climate.LapseCelsiusPerMeter, -1, 1, "terrain climate lapse");
                Domain(climate.SeasonalAmplitudeCelsius, 0, 10000, "terrain climate seasonal amplitude");
                Domain(climate.YearLengthTicks, .000001, 1e20, "terrain climate year");
                Domain(climate.ThermalResponseSeconds, 0, 1e12, "terrain climate response");
                Domain(climate.RainfallMillimeters, 0, 1000000 / 1.5, "terrain climate rainfall");
                foreach (var point in climate.LatitudeCurve) Domain(point.OffsetCelsius, -10000, 10000, "terrain climate latitude");
                if (allowRivers)
                {
                    double lo = elevationOffsetMeters - elevationAmplitudeMeters, hi = elevationOffsetMeters + elevationAmplitudeMeters;
                    double minimum = climate.MeanTemperatureCelsius + climate.LatitudeCurve.Min(p => p.OffsetCelsius) -
                        Math.Max(lo * climate.LapseCelsiusPerMeter, hi * climate.LapseCelsiusPerMeter) - climate.SeasonalAmplitudeCelsius;
                    double maximum = climate.MeanTemperatureCelsius + climate.LatitudeCurve.Max(p => p.OffsetCelsius) -
                        Math.Min(lo * climate.LapseCelsiusPerMeter, hi * climate.LapseCelsiusPerMeter) + climate.SeasonalAmplitudeCelsius;
                    if (minimum < -100 || maximum > 100) throw new ArgumentException("Native river evaporation requires the entire temperature field in [-100,100] Celsius.");
                }
            }
            var rules = biomeRules?.ToArray() ?? Array.Empty<CelestialPlanetBiomeRule>();
            if (rules.Any(r => r == null) || rules.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != rules.Length ||
                rules.Length > 0 && rules.Count(r => r.Fallback) != 1) throw new ArgumentException("Biome rules require unique keys and one fallback.");
            if (rules.Any(r => r.BiomeId == oceanBiomeId || r.BiomeId == "Lake")) throw new ArgumentException("Water biomes must use the water field rather than land biome rules.");
            ContinentScale = continentScale; ElevationAmplitudeMeters = elevationAmplitudeMeters; ElevationOffsetMeters = elevationOffsetMeters;
            Roughness = roughness; MountainWeight = mountainWeight; SeaLevelMeters = seaLevelMeters; OceanCoverage = oceanCoverage;
            AllowRivers = allowRivers; AllowLakes = allowLakes; OceanBiomeId = oceanBiomeId; Climate = climate;
            BiomeRules = Array.AsReadOnly(rules); Landmarks = CelestialWeightedReference.Copy(landmarks);
            FieldSeed = fieldSeed;
        }
        internal static void Domain(double value, double min, double max, string name)
        { if (!double.IsFinite(value) || value < min || value > max) throw new ArgumentOutOfRangeException(name); }
    }
    public sealed class CelestialPlanetBiomeRule
    {
        public string Key { get; }
        public string BiomeId { get; }
        public int Priority { get; }
        public double Weight { get; }
        public bool Fallback { get; }
        public double MinimumTemperatureCelsius { get; }
        public double MaximumTemperatureCelsius { get; }
        public double MinimumRainfallMillimeters { get; }
        public double MaximumRainfallMillimeters { get; }
        public double MinimumElevationMeters { get; }
        public double MaximumElevationMeters { get; }
        public double MinimumAbsoluteLatitude { get; }
        public double MaximumAbsoluteLatitude { get; }
        public double MinimumRoughness { get; }
        public double MaximumRoughness { get; }
        public CelestialPlanetBiomeRule(string key, string biomeId, int priority = 0, double weight = 1, bool fallback = false,
            double minimumTemperatureCelsius = -273.15, double maximumTemperatureCelsius = 10000,
            double minimumRainfallMillimeters = 0, double maximumRainfallMillimeters = 1000000,
            double minimumElevationMeters = -200000, double maximumElevationMeters = 200000,
            double minimumAbsoluteLatitude = 0, double maximumAbsoluteLatitude = 90,
            double minimumRoughness = 0, double maximumRoughness = 1)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key); ArgumentException.ThrowIfNullOrWhiteSpace(biomeId);
            CelestialPlanetTerrainConfiguration.Domain(weight, double.Epsilon, 1000000, nameof(weight));
            void Interval(double min, double max, double lower, double upper)
            { CelestialPlanetTerrainConfiguration.Domain(min, lower, upper, nameof(min)); CelestialPlanetTerrainConfiguration.Domain(max, min, upper, nameof(max)); }
            Interval(minimumTemperatureCelsius, maximumTemperatureCelsius, -273.15, 10000);
            Interval(minimumRainfallMillimeters, maximumRainfallMillimeters, 0, 1000000);
            Interval(minimumElevationMeters, maximumElevationMeters, -200000, 200000);
            Interval(minimumAbsoluteLatitude, maximumAbsoluteLatitude, 0, 90); Interval(minimumRoughness, maximumRoughness, 0, 1);
            Key = key; BiomeId = biomeId; Priority = priority; Weight = weight; Fallback = fallback;
            MinimumTemperatureCelsius = minimumTemperatureCelsius; MaximumTemperatureCelsius = maximumTemperatureCelsius;
            MinimumRainfallMillimeters = minimumRainfallMillimeters; MaximumRainfallMillimeters = maximumRainfallMillimeters;
            MinimumElevationMeters = minimumElevationMeters; MaximumElevationMeters = maximumElevationMeters;
            MinimumAbsoluteLatitude = minimumAbsoluteLatitude; MaximumAbsoluteLatitude = maximumAbsoluteLatitude;
            MinimumRoughness = minimumRoughness; MaximumRoughness = maximumRoughness;
            if (fallback && (minimumTemperatureCelsius != -273.15 || maximumTemperatureCelsius != 10000 || minimumRainfallMillimeters != 0 ||
                maximumRainfallMillimeters != 1000000 || minimumElevationMeters != -200000 || maximumElevationMeters != 200000 ||
                minimumAbsoluteLatitude != 0 || maximumAbsoluteLatitude != 90 || minimumRoughness != 0 || maximumRoughness != 1))
                throw new ArgumentException("Fallback cannot have conditions.");
        }
        public bool Matches(double temperature, double rainfall, double elevation, double latitude, double roughness) =>
            temperature >= MinimumTemperatureCelsius && temperature <= MaximumTemperatureCelsius && rainfall >= MinimumRainfallMillimeters &&
            rainfall <= MaximumRainfallMillimeters && elevation >= MinimumElevationMeters && elevation <= MaximumElevationMeters &&
            Math.Abs(latitude) >= MinimumAbsoluteLatitude && Math.Abs(latitude) <= MaximumAbsoluteLatitude && roughness >= MinimumRoughness && roughness <= MaximumRoughness;
    }
    public sealed class CelestialWeightedReference
    {
        public string Id { get; }
        public double Weight { get; }
        public CelestialWeightedReference(string id, double weight = 1)
        { ArgumentException.ThrowIfNullOrWhiteSpace(id); CelestialPlanetTerrainConfiguration.Domain(weight, double.Epsilon, 1000000, nameof(weight)); Id = id; Weight = weight; }
        internal static IReadOnlyList<CelestialWeightedReference> Copy(IReadOnlyList<CelestialWeightedReference>? source)
        {
            var items = source?.ToArray() ?? Array.Empty<CelestialWeightedReference>();
            if (items.Any(i => i == null) || items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != items.Length) throw new ArgumentException("Duplicate or missing weighted references.");
            return Array.AsReadOnly(items);
        }
    }
    public sealed record CelestialPlanetTerrainSample(double ElevationMeters, double Roughness, double MountainScore, double Geology, double Moisture);
    public static class CelestialPlanetTerrain
    {
        // Smooth three-dimensional waves sample directions on the physical sphere. No latitude/longitude raster seam or polar coordinate noise.
        private static double Field(double x, double y, double z, double scale, int seed, int channel)
        {
            double phase = (uint)unchecked(seed * 1664525 + channel * 1013904223) / (double)uint.MaxValue * Math.PI * 2;
            return (Math.Sin((x * 1.17 + y * .73 + z * .91) * scale + phase) +
                .5 * Math.Sin((x * -.63 + y * 1.31 + z * 1.13) * scale * 2 + phase * 1.7) +
                .25 * Math.Cos((x * 1.71 - y * .53 + z * .39) * scale * 4 + phase * .7)) / 1.75;
        }
        public static CelestialPlanetTerrainSample Sample(CelestialPlanetTerrainConfiguration configuration, double longitudeDegrees, double latitudeDegrees, int seed)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            if (!double.IsFinite(longitudeDegrees) || !double.IsFinite(latitudeDegrees) || Math.Abs(latitudeDegrees) > 90) throw new ArgumentOutOfRangeException(nameof(latitudeDegrees));
            double lon = Math.IEEERemainder(longitudeDegrees, 360) * Math.PI / 180, lat = latitudeDegrees * Math.PI / 180;
            double x = Math.Sin(lon) * Math.Cos(lat), y = Math.Sin(lat), z = -Math.Cos(lon) * Math.Cos(lat);
            double land = Field(x, y, z, configuration.ContinentScale, seed, 1);
            double rough = configuration.Roughness * (Field(x, y, z, configuration.ContinentScale * 5, seed, 2) + 1) / 2;
            double elevation = configuration.ElevationOffsetMeters + configuration.ElevationAmplitudeMeters *
                ((1 - configuration.Roughness) * land + configuration.Roughness * Field(x, y, z, configuration.ContinentScale * 4, seed, 3));
            return new(elevation, rough, configuration.MountainWeight * (Field(x, y, z, configuration.ContinentScale * 2, seed, 4) + 1) / 2,
                (Field(x, y, z, configuration.ContinentScale * 3, seed, 5) + 1) / 2, (Field(x, y, z, configuration.ContinentScale, seed, 6) + 1) / 2);
        }
        public static bool[] SelectOceanTiles(CelestialPlanetTerrainConfiguration configuration, double[] elevations)
        {
            if (elevations == null || elevations.Any(v => !double.IsFinite(v))) throw new ArgumentException("Invalid elevations.");
            var result = new bool[elevations.Length];
            if (configuration.OceanCoverage.HasValue)
            {
                int count = (int)Math.Round(configuration.OceanCoverage.Value * elevations.Length, MidpointRounding.AwayFromZero);
                foreach (int i in Enumerable.Range(0, elevations.Length).OrderBy(i => elevations[i]).ThenBy(i => i).Take(count)) result[i] = true;
            }
            else if (configuration.SeaLevelMeters.HasValue)
                for (int i = 0; i < elevations.Length; i++) result[i] = elevations[i] < configuration.SeaLevelMeters.Value;
            return result;
        }
        public static CelestialPlanetBiomeRule SelectBiome(CelestialPlanetTerrainConfiguration configuration, double temperature, double rainfall,
            double elevation, double latitude, double roughness, double randomUnit)
        {
            CelestialPlanetTerrainConfiguration.Domain(randomUnit, 0, 1, nameof(randomUnit));
            var candidates = configuration.BiomeRules.Where(r => !r.Fallback && r.Matches(temperature, rainfall, elevation, latitude, roughness))
                .OrderByDescending(r => r.Priority).ThenBy(r => r.Key, StringComparer.Ordinal).ToArray();
            if (candidates.Length == 0) return configuration.BiomeRules.Single(r => r.Fallback);
            candidates = candidates.Where(r => r.Priority == candidates[0].Priority).ToArray();
            double remaining = randomUnit * candidates.Sum(r => r.Weight);
            foreach (var candidate in candidates) { remaining -= candidate.Weight; if (remaining < 0) return candidate; }
            return candidates[^1];
        }
    }
}
