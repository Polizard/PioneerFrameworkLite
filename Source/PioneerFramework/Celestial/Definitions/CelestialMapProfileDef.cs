using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using PioneerFramework.MapGeneration;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialMapProfileDef : Def
    {
        public MapGeneratorDef? mapGenerator;
        public CelestialPlanetMapDiversityXml? diversity;
        public LocalMapRecipeDef? localRecipe;
        public bool useDiversityAsInitialInputs;
        public List<LocalMapPurposeRecipeXml> purposeRecipes = new();
        public CelestialResult<CelestialMapProfile> ToProfile(CelestialGenerationContext? context = null)
        {
            try
            {
                if (localRecipe == null) throw new ArgumentException("A natural local recipe is required.");
                var resolvedDiversity = diversity?.Resolve(context);
                var natural = localRecipe.ToRecipe(context: context);
                if (useDiversityAsInitialInputs)
                {
                    if (resolvedDiversity == null) throw new ArgumentException("Initial input projection requires a diversity configuration.");
                    if (natural.Steps.Any(step => step.HandlerId == LocalMapBuiltinConfigurations.InitialInputs))
                        throw new ArgumentException("Diversity projection and explicit initial inputs cannot be combined.");
                    natural = new LocalMapRecipe(natural.Id, natural.Steps.Append(
                        CelestialLocalMapCompatibility.FreezeInitialInputs("profile.initial-inputs", resolvedDiversity)));
                }
                CelestialLocalMapCompatibility.RequireInitialInputsMatchDiversity(resolvedDiversity, natural);
                var specification = new LocalMapGenerationSpec(natural, purposeRecipes.ToDictionary(row => row.purpose,
                    row => row.recipe?.ToRecipe(context: context) ?? throw new ArgumentException("Missing purpose recipe.")), mapGenerator?.defName);
                LocalMapGenerationModules.ValidateSpecification(specification, LocalMapGenerationModules.Registry);
                return CelestialResult.Ok(new CelestialMapProfile(defName, resolvedDiversity, specification));
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
