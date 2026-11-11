using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using PioneerFramework.MapGeneration;

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
        public CelestialPlanetMapDiversityXml? diversity;
        public LocalMapRecipeDef? localRecipe;
        public bool useDiversityAsInitialInputs;
        public List<LocalMapPurposeRecipeXml> purposeRecipes = new();
        public CelestialResult<CelestialMapProfile> ToProfile(CelestialGenerationContext? context = null)
        {
            try
            {
                string[] Types(IEnumerable<Type> types) => types.Select(type => type != null && typeof(GenStep).IsAssignableFrom(type) && type.FullName != null ?
                    type.FullName : throw new ArgumentException("Map step type must derive from GenStep.")).ToArray();
                var resolvedDiversity = diversity?.Resolve(context);
                LocalMapGenerationSpec? specification = null;
                if (localRecipe != null || purposeRecipes.Count > 0 || useDiversityAsInitialInputs)
                {
                    if (localRecipe == null) throw new ArgumentException("A natural local recipe is required.");
                    var natural = localRecipe.ToRecipe(context: context);
                    if (useDiversityAsInitialInputs)
                    {
                        if (resolvedDiversity == null) throw new ArgumentException("Initial input projection requires a diversity configuration.");
                        if (natural.Steps.Any(step => step.HandlerId == LocalMapBuiltinConfigurations.InitialInputs))
                            throw new ArgumentException("Diversity projection and explicit initial inputs cannot be combined.");
                        natural = new LocalMapRecipe(natural.Id, natural.Steps.Append(
                            CelestialLocalMapCompatibility.FreezeInitialInputs("profile.initial-inputs", resolvedDiversity)));
                    }
                    specification = new LocalMapGenerationSpec(natural, purposeRecipes.ToDictionary(row => row.purpose,
                        row => row.recipe?.ToRecipe(context: context) ?? throw new ArgumentException("Missing purpose recipe.")), mapGenerator?.defName);
                    LocalMapGenerationModules.ValidateSpecification(specification, LocalMapGenerationModules.Registry);
                }
                return CelestialResult.Ok(new CelestialMapProfile(defName, mapGenerator?.defName, skipStepNames, skipStepPrefixes, Types(skipStepTypes),
                    replaceTerrains.Select(terrain => terrain?.defName ?? throw new ArgumentException("Missing terrain.")).ToArray(), replacementTerrain?.defName,
                    biomeRules.Select(rule => new CelestialMapBiomeProfile(rule.biome?.defName ?? "", rule.skipStepNames, Types(rule.skipStepTypes), rule.replaceRockTerrains)).ToArray(), resolvedDiversity, specification));
            }
            catch (Exception exception) when (exception is ArgumentException or NullReferenceException or InvalidOperationException or System.Text.Json.JsonException)
            { return CelestialResult.Reject<CelestialMapProfile>(new CelestialError("InvalidMapProfile", Detail: exception.Message)); }
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            foreach (var error in ToProfile().Errors) yield return error.Code + ": " + error.Detail;
        }
    }
}
