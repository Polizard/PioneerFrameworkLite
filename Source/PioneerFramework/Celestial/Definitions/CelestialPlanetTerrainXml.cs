using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;

namespace PioneerFramework.Celestial
{
    internal sealed class CelestialPlanetParameterSampler
    {
        private readonly Dictionary<string, CelestialDoubleParameterXml> parameters;
        private readonly CelestialGenerationContext? context;
        private readonly string prefix;
        private readonly bool sample;
        private readonly Dictionary<string, DoubleParameterSpec> intervals = new(StringComparer.Ordinal);
        public CelestialPlanetParameterSampler(List<CelestialEnvironmentParameterXml> parameters, CelestialGenerationContext? context, string prefix, bool sample = true)
        {
            if (parameters == null || parameters.Any(p => p == null || string.IsNullOrWhiteSpace(p.key) || p.value == null) ||
                parameters.Select(p => p.key).Distinct(StringComparer.Ordinal).Count() != parameters.Count) throw new ArgumentException("Invalid planet parameter keys.");
            this.parameters = parameters.ToDictionary(p => p.key, p => p.value, StringComparer.Ordinal); this.context = context; this.prefix = prefix; this.sample = sample;
        }
        private DoubleParameterSpec Specification(string key, double fallback, double lower, double upper)
        {
            var parsed = parameters.Remove(key, out var parameter) ? parameter.ToSpec() : CelestialResult.Ok(DoubleParameterSpec.Fixed(fallback));
            if (!parsed.Success) throw new ArgumentException("Invalid parameter: " + prefix + key);
            var spec = context?.GetDoubleSpecification(prefix + key, parsed.Value) ?? parsed.Value;
            CelestialPlanetTerrainConfiguration.Domain(spec.Minimum, lower, upper, prefix + key);
            CelestialPlanetTerrainConfiguration.Domain(spec.Maximum, lower, upper, prefix + key);
            intervals.Add(key, spec);
            return spec;
        }
        public double Get(string key, double fallback, double lower, double upper)
        {
            var spec = Specification(key, fallback, lower, upper);
            return context == null || !sample ? spec.Minimum : context.SampleResolvedDouble(prefix + key, spec);
        }
        public int GetInt(string key, int fallback)
        {
            var spec = Specification(key, fallback, int.MinValue, int.MaxValue);
            if (Math.Truncate(spec.Minimum) != spec.Minimum || Math.Truncate(spec.Maximum) != spec.Maximum) throw new ArgumentException("Integer planet parameter required: " + prefix + key);
            return context == null ? (int)spec.Minimum : CelestialParameterSampler.SampleInt(spec.IsFixed ? IntParameterSpec.Fixed((int)spec.Minimum) :
                IntParameterSpec.Uniform((int)spec.Minimum, (int)spec.Maximum), context.SeedFor(prefix + key));
        }
        public DoubleParameterSpec Interval(string key) => intervals[key];
        public void Complete()
        {
            if (parameters.Count != 0) throw new ArgumentException("Unknown planet parameter: " + prefix + parameters.Keys.First());
            foreach (var lower in intervals.Where(p => p.Key.StartsWith("minimum", StringComparison.Ordinal)))
                if (intervals.TryGetValue("maximum" + lower.Key[7..], out var upper) && lower.Value.Maximum > upper.Minimum)
                    throw new ArgumentException("Planet rule intervals must remain ordered for every sample: " + prefix + lower.Key);
        }
    }
    public sealed class CelestialPlanetBiomeRuleXml
    {
        public string key = "";
        public BiomeDef biome = null!;
        public int priority;
        public bool fallback;
        public double weight = 1;
        public double minimumTemperatureCelsius = -273.15;
        public double maximumTemperatureCelsius = 10000;
        public double minimumRainfallMillimeters;
        public double maximumRainfallMillimeters = 1000000;
        public double minimumElevationMeters = -200000;
        public double maximumElevationMeters = 200000;
        public double minimumAbsoluteLatitude;
        public double maximumAbsoluteLatitude = 90;
        public double minimumRoughness;
        public double maximumRoughness = 1;
        public List<CelestialEnvironmentParameterXml> parameters = new();
        internal CelestialPlanetBiomeRule Resolve(CelestialGenerationContext? context)
        {
            var s = new CelestialPlanetParameterSampler(parameters, context, "terrain.biomeRules." + key + ".");
            double Get(string name, double value, double low, double high) => s.Get(name, value, low, high);
            var result = new CelestialPlanetBiomeRule(key, biome?.defName ?? "", s.GetInt(nameof(priority), priority), Get(nameof(weight), weight, double.Epsilon, 1000000), fallback,
                Get(nameof(minimumTemperatureCelsius), minimumTemperatureCelsius, -273.15, 10000), Get(nameof(maximumTemperatureCelsius), maximumTemperatureCelsius, -273.15, 10000),
                Get(nameof(minimumRainfallMillimeters), minimumRainfallMillimeters, 0, 1000000), Get(nameof(maximumRainfallMillimeters), maximumRainfallMillimeters, 0, 1000000),
                Get(nameof(minimumElevationMeters), minimumElevationMeters, -200000, 200000), Get(nameof(maximumElevationMeters), maximumElevationMeters, -200000, 200000),
                Get(nameof(minimumAbsoluteLatitude), minimumAbsoluteLatitude, 0, 90), Get(nameof(maximumAbsoluteLatitude), maximumAbsoluteLatitude, 0, 90),
                Get(nameof(minimumRoughness), minimumRoughness, 0, 1), Get(nameof(maximumRoughness), maximumRoughness, 0, 1));
            if (fallback)
                foreach (string condition in new[] { nameof(minimumTemperatureCelsius), nameof(maximumTemperatureCelsius), nameof(minimumRainfallMillimeters),
                    nameof(maximumRainfallMillimeters), nameof(minimumElevationMeters), nameof(maximumElevationMeters), nameof(minimumAbsoluteLatitude),
                    nameof(maximumAbsoluteLatitude), nameof(minimumRoughness), nameof(maximumRoughness) })
                {
                    var interval = s.Interval(condition);
                    if (interval.Minimum != interval.Maximum) throw new ArgumentException("Fallback condition intervals must remain unconditional.");
                }
            s.Complete(); return result;
        }
    }
    public sealed class CelestialPlanetLandmarkXml
    {
        public TileMutatorDef mutator = null!;
        public double weight = 1;
        public List<CelestialEnvironmentParameterXml> parameters = new();
        internal CelestialWeightedReference Resolve(CelestialGenerationContext? context)
        {
            var s = new CelestialPlanetParameterSampler(parameters, context, "terrain.landmarks." + mutator?.defName + ".");
            var result = new CelestialWeightedReference(mutator?.defName ?? "", s.Get(nameof(weight), weight, double.Epsilon, 1000000)); s.Complete(); return result;
        }
    }
    public sealed class CelestialPlanetTerrainXml
    {
        public List<CelestialEnvironmentParameterXml> parameters = new();
        public double continentScale = 2;
        public double elevationAmplitudeMeters = 3000;
        public double elevationOffsetMeters;
        public double roughness = .35;
        public double mountainWeight = .5;
        public CelestialDoubleParameterXml? seaLevelMeters;
        public CelestialDoubleParameterXml? oceanCoverage;
        public bool allowRivers;
        public bool allowLakes;
        public BiomeDef? oceanBiome;
        public List<CelestialPlanetBiomeRuleXml> biomeRules = new();
        public List<CelestialPlanetLandmarkXml> landmarks = new();
        public CelestialPlanetTerrainConfiguration Resolve(CelestialGenerationContext? context = null, CelestialClimateConfiguration? climate = null)
        {
            if (parameters == null || biomeRules == null || landmarks == null || biomeRules.Any(r => r == null) || landmarks.Any(r => r?.mutator?.Worker == null))
                throw new ArgumentException("Missing terrain lists, rules or native landmark worker.");
            var values = new List<CelestialEnvironmentParameterXml>(parameters);
            if (seaLevelMeters != null) values.Add(new() { key = nameof(seaLevelMeters), value = seaLevelMeters });
            if (oceanCoverage != null) values.Add(new() { key = nameof(oceanCoverage), value = oceanCoverage });
            var s = new CelestialPlanetParameterSampler(values, context, "terrain.");
            var surface = context?.GetResolved<SurfaceResolvedProperties>();
            ulong seed = context != null && surface != null ? CelestialSeed.Derive(context.WorldSeed, context.GenerationPath, surface.Key, "surface.seed") : 0;
            var result = new CelestialPlanetTerrainConfiguration(s.Get(nameof(continentScale), continentScale, .01, 64),
                s.Get(nameof(elevationAmplitudeMeters), elevationAmplitudeMeters, 0, 100000), s.Get(nameof(elevationOffsetMeters), elevationOffsetMeters, -100000, 100000),
                s.Get(nameof(roughness), roughness, 0, 1), s.Get(nameof(mountainWeight), mountainWeight, 0, 1),
                seaLevelMeters == null ? null : s.Get(nameof(seaLevelMeters), 0, -200000, 200000), oceanCoverage == null ? null : s.Get(nameof(oceanCoverage), 0, 0, 1),
                allowRivers, allowLakes, oceanBiome?.defName ?? "Ocean", climate,
                biomeRules.Select(r => r.Resolve(context)).ToArray(), landmarks.Select(r => r.Resolve(context)).ToArray(), unchecked((int)(seed ^ (seed >> 32))));
            ValidateClimateIntervals(s, climate, context);
            s.Complete(); return result;
        }
        private void ValidateClimateIntervals(CelestialPlanetParameterSampler sampler, CelestialClimateConfiguration? climate, CelestialGenerationContext? context)
        {
            if (climate == null) return;
            string? owner = context?.GetResolved<ExistingEnvironmentResolvedProperties>()?.Key;
            DoubleParameterSpec Spec(string key, double value) => owner != null && context!.TryGetSampledDoubleSpecification(owner, "climate." + key, out var interval) ? interval : DoubleParameterSpec.Fixed(value);
            void Domain(string key, double value, double min, double max)
            {
                var interval = Spec(key, value);
                CelestialPlanetTerrainConfiguration.Domain(interval.Minimum, min, max, "terrain climate interval " + key);
                CelestialPlanetTerrainConfiguration.Domain(interval.Maximum, min, max, "terrain climate interval " + key);
            }
            Domain("meanTemperatureCelsius", climate.MeanTemperatureCelsius, -273.15, 10000);
            Domain("lapseCelsiusPerMeter", climate.LapseCelsiusPerMeter, -1, 1);
            Domain("seasonalAmplitudeCelsius", climate.SeasonalAmplitudeCelsius, 0, 10000);
            Domain("yearLengthTicks", climate.YearLengthTicks, .000001, 1e20);
            Domain("thermalResponseSeconds", climate.ThermalResponseSeconds, 0, 1e12);
            Domain("rainfallMillimeters", climate.RainfallMillimeters, 0, 1000000 / 1.5);
            if (!allowRivers) return;
            var offset = sampler.Interval(nameof(elevationOffsetMeters)); var amplitude = sampler.Interval(nameof(elevationAmplitudeMeters));
            double lo = offset.Minimum - amplitude.Maximum, hi = offset.Maximum + amplitude.Maximum;
            var lapse = Spec("lapseCelsiusPerMeter", climate.LapseCelsiusPerMeter);
            var products = new[] { lo * lapse.Minimum, lo * lapse.Maximum, hi * lapse.Minimum, hi * lapse.Maximum };
            var mean = Spec("meanTemperatureCelsius", climate.MeanTemperatureCelsius);
            var season = Spec("seasonalAmplitudeCelsius", climate.SeasonalAmplitudeCelsius);
            double minimum = mean.Minimum + climate.LatitudeCurve.Min(p => p.OffsetCelsius) - products.Max() - season.Maximum;
            double maximum = mean.Maximum + climate.LatitudeCurve.Max(p => p.OffsetCelsius) - products.Min() + season.Maximum;
            if (minimum < -100 || maximum > 100) throw new ArgumentException("Native river evaporation requires every climate and altitude interval combination in [-100,100] Celsius.");
        }
    }
}
