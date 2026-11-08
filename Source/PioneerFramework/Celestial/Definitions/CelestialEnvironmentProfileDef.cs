using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialEnvironmentProfileDef : Def
    {
        public CelestialClimateXml? climate;
        public CelestialExposureXml? exposure;
        public double dayLengthTicks = 60000;
        public string primaryLightReference = "primaryLight";
        public List<BiomeDef> vacuumBiomes = new();
        public CelestialResult<CelestialEnvironmentProfile> ToProfile(CelestialGenerationContext? context = null)
        {
            try
            {
                if (vacuumBiomes == null || vacuumBiomes.Any(biome => biome == null || !biome.inVacuum)) throw new ArgumentException("Vacuum references require native vacuum biomes.");
                return CelestialResult.Ok(new CelestialEnvironmentProfile(defName, dayLengthTicks, primaryLightReference, vacuumBiomes.Select(biome => biome.defName).ToArray(), climate?.Resolve(context), exposure?.Resolve(context)));
            }
            catch (ArgumentException exception) { return CelestialResult.Reject<CelestialEnvironmentProfile>(new CelestialError("InvalidEnvironmentProfile", Detail: exception.Message)); }
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            foreach (var error in ToProfile().Errors) yield return error.Code + ": " + error.Detail;
            // 2026-11-07 A-2：mode 专属字段静默失效的加载期诊断（不改运行期行为）。
            if (climate == null) yield break;
            if (climate.mode == CelestialClimateMode.CommittedIllumination && climate.diurnalAmplitudeCelsius != 0)
                yield return "CommittedIlluminationIgnoresDiurnalAmplitude";
            if (climate.mode == CelestialClimateMode.Authored &&
                climate.illuminationTemperatureCelsius != CelestialClimateXml.DefaultIlluminationTemperatureCelsius)
                yield return "AuthoredIgnoresIlluminationTemperature";
        }
    }
}
