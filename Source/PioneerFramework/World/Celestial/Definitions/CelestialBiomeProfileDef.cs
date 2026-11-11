using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialBiomeProfileDef : Def
    {
        public CelestialBiomeGenerationMode mode;
        public float temperature;
        public float rainfall;
        public float swampiness;
        public List<WorldGenStepDef> worldGenSteps = new();
        public List<WorldGenStepDef> rootOnlySteps = new();
        public CelestialPlanetTerrainXml? terrain;
        public CelestialResult<CelestialGenerationProfile> ToProfile(CelestialGenerationContext? context = null, bool useEnvironmentClimate = false)
        {
            try
            {
                if (worldGenSteps == null || rootOnlySteps == null || worldGenSteps.Concat(rootOnlySteps).Any(step => step == null || step.worldGenStep == null))
                    return CelestialResult.Reject<CelestialGenerationProfile>(new CelestialError("InvalidBiomeProfile"));
                return CelestialResult.Ok(new CelestialGenerationProfile(defName, mode, temperature, rainfall, swampiness,
                    worldGenSteps.Select(step => step.defName).ToArray(), rootOnlySteps.Select(step => step.defName).ToArray(),
                    terrain?.Resolve(context, useEnvironmentClimate ? context?.GetResolved<ExistingEnvironmentResolvedProperties>()?.Profile.Climate : null)));
            }
            catch (ArgumentException exception) { return CelestialResult.Reject<CelestialGenerationProfile>(new CelestialError("InvalidBiomeProfile", Detail: exception.Message)); }
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            foreach (var error in ToProfile().Errors) yield return error.Code + ": " + error.Detail;
        }
    }
}
