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
            var sampler = new CelestialParameterSamplerXml(parameters, context, "climate.", CelestialParameterScope.Environment);
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
            var sampler = new CelestialParameterSamplerXml(parameters, context, "exposure.", CelestialParameterScope.Environment);
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
}
