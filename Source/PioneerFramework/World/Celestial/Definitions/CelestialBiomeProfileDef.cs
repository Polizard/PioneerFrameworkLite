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
        public BiomeDef? polarBiome;
        public BiomeDef? primaryBiome;
        public BiomeDef? secondaryBiome;
        public double polarLatitude = 75;
        public double secondaryChance = 0.5;
        public Hilliness polarHilliness = Hilliness.Flat;
        public Hilliness primaryHilliness = Hilliness.SmallHills;
        public Hilliness secondaryHilliness = Hilliness.Flat;
        public float temperature;
        public float rainfall;
        public float swampiness;
        public List<WorldGenStepDef> worldGenSteps = new();
        public List<WorldGenStepDef> rootOnlySteps = new();
        public CelestialResult<CelestialGenerationProfile> ToProfile()
        {
            try
            {
                if (worldGenSteps == null || rootOnlySteps == null || worldGenSteps.Concat(rootOnlySteps).Any(step => step == null || step.worldGenStep == null) ||
                    !Enum.IsDefined(polarHilliness) || !Enum.IsDefined(primaryHilliness) || !Enum.IsDefined(secondaryHilliness))
                    return CelestialResult.Reject<CelestialGenerationProfile>(new CelestialError("InvalidBiomeProfile"));
                return CelestialResult.Ok(new CelestialGenerationProfile(defName, mode, polarBiome?.defName, primaryBiome?.defName, secondaryBiome?.defName,
                    polarLatitude, secondaryChance, (int)polarHilliness, (int)primaryHilliness, (int)secondaryHilliness, temperature, rainfall, swampiness,
                    worldGenSteps.Select(step => step.defName).ToArray(), rootOnlySteps.Select(step => step.defName).ToArray()));
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
