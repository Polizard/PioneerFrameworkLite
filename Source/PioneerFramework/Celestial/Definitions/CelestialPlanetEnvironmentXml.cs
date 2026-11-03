using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialClimatePointXml
    {
        public double latitudeDegrees;
        public double offsetCelsius;
    }
    public sealed class CelestialClimateXml
    {
        public List<CelestialEnvironmentParameterXml> parameters = new();
        public CelestialClimateMode mode;
        public double meanTemperatureCelsius = 15;
        public List<CelestialClimatePointXml> latitudeCurve = new() { new() { latitudeDegrees = 0 }, new() { latitudeDegrees = 90, offsetCelsius = -40 } };
        public double lapseCelsiusPerMeter = .0065;
        public double diurnalAmplitudeCelsius;
        public double seasonalAmplitudeCelsius;
        public double yearLengthTicks = 3600000;
        public double thermalResponseSeconds;
        public double illuminationTemperatureCelsius = 50;
        public double lightMultiplier = 1;
        public double windSpeed = 1;
        public double rainfallMillimeters = 1000;
        public double humidity = .5;
        public CelestialClimateConfiguration Resolve(CelestialGenerationContext? context = null)
        {
            var sampler = new CelestialEnvironmentParameterSampler(parameters, context, "climate.");
            var result = new CelestialClimateConfiguration(mode, sampler.Get(nameof(meanTemperatureCelsius), meanTemperatureCelsius),
                latitudeCurve.Select(p => new CelestialClimatePoint(p.latitudeDegrees, p.offsetCelsius)).ToArray(), sampler.Get(nameof(lapseCelsiusPerMeter), lapseCelsiusPerMeter),
                sampler.Get(nameof(diurnalAmplitudeCelsius), diurnalAmplitudeCelsius), sampler.Get(nameof(seasonalAmplitudeCelsius), seasonalAmplitudeCelsius),
                sampler.Get(nameof(yearLengthTicks), yearLengthTicks), sampler.Get(nameof(thermalResponseSeconds), thermalResponseSeconds),
                sampler.Get(nameof(illuminationTemperatureCelsius), illuminationTemperatureCelsius), sampler.Get(nameof(lightMultiplier), lightMultiplier),
                sampler.Get(nameof(windSpeed), windSpeed), sampler.Get(nameof(rainfallMillimeters), rainfallMillimeters), sampler.Get(nameof(humidity), humidity));
            sampler.Complete(); return result;
        }
    }
    public sealed class CelestialExposureXml
    {
        public List<CelestialEnvironmentParameterXml> parameters = new();
        public double minimumOxygenPascals = 16000;
        public double minimumPressurePascals = 20000;
        public double maximumPressurePascals = 300000;
        public double hypoxiaPerDay = 2;
        public double pressurePerDay = 2;
        public double toxicityPerDay;
        public double radiationPerDay;
        public double carbonDioxideThresholdPascals = 1000;
        public bool gravityStats;
        public CelestialExposureConfiguration Resolve(CelestialGenerationContext? context = null)
        {
            var sampler = new CelestialEnvironmentParameterSampler(parameters, context, "exposure.");
            var result = new CelestialExposureConfiguration(sampler.Get(nameof(minimumOxygenPascals), minimumOxygenPascals),
                sampler.Get(nameof(minimumPressurePascals), minimumPressurePascals), sampler.Get(nameof(maximumPressurePascals), maximumPressurePascals),
                sampler.Get(nameof(hypoxiaPerDay), hypoxiaPerDay), sampler.Get(nameof(pressurePerDay), pressurePerDay),
                sampler.Get(nameof(toxicityPerDay), toxicityPerDay), sampler.Get(nameof(radiationPerDay), radiationPerDay),
                sampler.Get(nameof(carbonDioxideThresholdPascals), carbonDioxideThresholdPascals), gravityStats);
            sampler.Complete(); return result;
        }
    }
    public sealed class CelestialEnvironmentParameterXml
    {
        public string key = "";
        public CelestialDoubleParameterXml value = new();
    }
    internal sealed class CelestialEnvironmentParameterSampler
    {
        private readonly Dictionary<string, CelestialDoubleParameterXml> parameters;
        private readonly Dictionary<string, DoubleParameterSpec> resolved = new(StringComparer.Ordinal);
        private readonly CelestialGenerationContext? context;
        private readonly string prefix;
        public CelestialEnvironmentParameterSampler(List<CelestialEnvironmentParameterXml> parameters, CelestialGenerationContext? context, string prefix)
        {
            if (parameters == null || parameters.Any(p => p == null || string.IsNullOrWhiteSpace(p.key) || p.value == null) ||
                parameters.Select(p => p.key).Distinct(StringComparer.Ordinal).Count() != parameters.Count) throw new ArgumentException("Invalid environment parameters.");
            this.parameters = parameters.ToDictionary(p => p.key, p => p.value, StringComparer.Ordinal); this.context = context; this.prefix = prefix;
        }
        public double Get(string key, double fallback)
        {
            var spec = parameters.Remove(key, out var parameter) ? parameter.ToSpec() : CelestialResult.Ok(DoubleParameterSpec.Fixed(fallback));
            if (!spec.Success) throw new ArgumentException("Invalid parameter: " + prefix + key);
            var effective = context?.GetDoubleSpecification(prefix + key, spec.Value) ?? spec.Value;
            double minimum = key == "meanTemperatureCelsius" ? -273.15 : key == "lapseCelsiusPerMeter" ? -1 : key == "yearLengthTicks" ? 1e-6 : 0;
            double maximum = key switch { "humidity" => 1, "lapseCelsiusPerMeter" => 1, "yearLengthTicks" => 1e20,
                "thermalResponseSeconds" => 1e12, "windSpeed" => 1e6, "rainfallMillimeters" => 1000000 / 1.5,
                "meanTemperatureCelsius" or "diurnalAmplitudeCelsius" or "seasonalAmplitudeCelsius" or "illuminationTemperatureCelsius" => 10000,
                _ => 1e12 };
            bool strictlyPositive = key is "yearLengthTicks" or "minimumOxygenPascals" or "maximumPressurePascals" or "carbonDioxideThresholdPascals";
            if (effective.Minimum < minimum || effective.Maximum > maximum || strictlyPositive && effective.Minimum <= 0)
                throw new ArgumentException("Environment parameter range outside domain: " + prefix + key);
            resolved.Add(key, effective);
            return context == null ? effective.Minimum : context.SampleResolvedDouble(prefix + key, effective);
        }
        public void Complete()
        {
            if (parameters.Count != 0) throw new ArgumentException("Unknown environment parameter: " + parameters.Keys.First());
            if (resolved.TryGetValue("minimumPressurePascals", out var lower) &&
                resolved.TryGetValue("maximumPressurePascals", out var upper) && lower.Maximum >= upper.Minimum)
                throw new ArgumentException("Pressure ranges must remain strictly ordered for all samples.");
        }
    }
}
