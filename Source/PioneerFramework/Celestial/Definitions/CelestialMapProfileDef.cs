using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialMapBiomeRule
    {
        public BiomeDef biome = null!;
        public List<string> skipStepNames = new();
        public List<Type> skipStepTypes = new();
        public bool replaceRockTerrains;
    }
    public sealed class CelestialMapProfileDef : Def
    {
        public MapGeneratorDef? mapGenerator;
        public List<string> skipStepNames = new();
        public List<string> skipStepPrefixes = new();
        public List<Type> skipStepTypes = new();
        public List<TerrainDef> replaceTerrains = new();
        public TerrainDef? replacementTerrain;
        public List<CelestialMapBiomeRule> biomeRules = new();
        public CelestialResult<CelestialMapProfile> ToProfile()
        {
            try
            {
                string[] Types(IEnumerable<Type> types) => types.Select(type => type != null && typeof(GenStep).IsAssignableFrom(type) && type.FullName != null ?
                    type.FullName : throw new ArgumentException("Map step type must derive from GenStep.")).ToArray();
                return CelestialResult.Ok(new CelestialMapProfile(defName, mapGenerator?.defName, skipStepNames, skipStepPrefixes, Types(skipStepTypes),
                    replaceTerrains.Select(terrain => terrain?.defName ?? throw new ArgumentException("Missing terrain.")).ToArray(), replacementTerrain?.defName,
                    biomeRules.Select(rule => new CelestialMapBiomeProfile(rule.biome?.defName ?? "", rule.skipStepNames, Types(rule.skipStepTypes), rule.replaceRockTerrains)).ToArray()));
            }
            catch (Exception exception) when (exception is ArgumentException or NullReferenceException)
            { return CelestialResult.Reject<CelestialMapProfile>(new CelestialError("InvalidMapProfile", Detail: exception.Message)); }
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            foreach (var error in ToProfile().Errors) yield return error.Code + ": " + error.Detail;
        }
    }
}
