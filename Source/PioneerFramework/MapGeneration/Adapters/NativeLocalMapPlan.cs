using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PioneerFramework.MapGeneration
{
    /// <summary>Compiles the actual post-setup native candidates once. No native generation callback is executed here.</summary>
    internal sealed class NativeLocalMapPlan
    {
        internal MapGenerationPlan Plan { get; }
        internal IReadOnlyDictionary<string, IMapGenerationModule> Modules { get; }
        internal IReadOnlyList<NativeMapGenerationStep> NativeSteps { get; }
        internal IReadOnlyList<InitialMapInputs> InitialInputs { get; }
        private NativeLocalMapPlan(MapGenerationPlan plan, Dictionary<string, IMapGenerationModule> modules,
            List<NativeMapGenerationStep> nativeSteps, List<InitialMapInputs> initial)
        { Plan = plan; Modules = modules; NativeSteps = nativeSteps.AsReadOnly(); InitialInputs = initial.AsReadOnly(); }

        internal static NativeLocalMapPlan Build(FrozenLocalMapContext frozen, MapGenerationPurpose purpose,
            IEnumerable<GenStepWithParams> candidates, MapGenerationRegistry registry,
            Func<MapGenerationStepSpec, IDisposable>? enterPreparation = null, bool requiredSiteContent = false)
        {
            LocalMapGenerationModules.ValidateSpecification(frozen.Specification, registry);
            var authored = frozen.Specification.Recipe.Steps.Concat(frozen.Specification.PurposeRecipes.TryGetValue(purpose, out var recipe)
                ? recipe.Steps : Array.Empty<MapGenerationStepSpec>()).ToArray();
            var modules = new Dictionary<string, IMapGenerationModule>(StringComparer.Ordinal);
            var contributions = new Dictionary<string, NativeMapPlanContribution>(StringComparer.Ordinal);
            var initial = new List<InitialMapInputs>();
            foreach (var step in authored)
            {
                using (enterPreparation?.Invoke(step))
                {
                    registry.TryResolve(step.HandlerId, step.ConfigurationVersion, out var registration);
                    var module = registration!.Create(step.Configuration);
                    modules.Add(step.Key, module);
                    if (module is not INativeMapPlanContributor contributor) continue;
                    var contribution = contributor.Contribute(frozen.Source) ?? throw new InvalidOperationException("Null native contribution: " + step.Key);
                    if (step.Domain == MapGenerationDomain.Purpose && (contribution.InitialInputs != null || contribution.ProvidesNativeInputs.Count != 0))
                        throw new InvalidOperationException("PurposeCannotOverrideNaturalInputs: " + step.Key);
                    contributions.Add(step.Key, contribution);
                    if (contribution.InitialInputs != null) initial.Add(contribution.InitialInputs);
                }
            }
            var incoming = candidates.ToArray();
            if (incoming.Any(step => step.def?.genStep == null)) throw new InvalidOperationException("Invalid native candidate.");
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var native = incoming.OrderBy(step => step.def.order).ThenBy(step => step.def.index).Select((step, index) => {
                occurrences.TryGetValue(step.def.defName, out int occurrence);
                occurrences[step.def.defName] = occurrence + 1;
                return new Candidate(step, "native/" + step.def.defName + "/" + occurrence, index, Classify(step.def),
                    step.parms.sitePart != null || step.def.linkWithSite != null); }).ToList();
            foreach (var step in authored)
            {
                if (!contributions.TryGetValue(step.Key, out var contribution)) continue;
                foreach (var selected in native.Where(n => contribution.Remove.Any(selector => NativeMapStepSelection.Matches(selector, frozen.Source.BiomeId, n.Step.def))).ToArray())
                {
                    if (step.Domain == MapGenerationDomain.Purpose && selected.Stage <= MapGenerationStage.NaturalSurface)
                        throw new InvalidOperationException("PurposeCannotRemoveNaturalStep: " + selected.Key);
                    if (selected.Required) throw new InvalidOperationException("RequiredNativeStepConflict: " + selected.Key);
                    native.Remove(selected);
                }
            }
            var nativeSteps = native.Select(n => new NativeMapGenerationStep(n.Key, n.Step)).ToList();
            var authoredTokens = new HashSet<string>(StringComparer.Ordinal);
            // Explicit authored native modules can add a typed native Def even if absent from the generator.
            foreach (var step in authored.Where(step => step.HandlerId == LocalMapBuiltinConfigurations.Native))
            {
                var config = LocalMapBuiltinConfigurations.Decode<NativeGenStepConfiguration>(step.Configuration);
                if (!authoredTokens.Add(config.Token) || native.Any(n => n.Key == config.Token))
                    throw new InvalidOperationException("DuplicateNativeToken: " + config.Token);
                if (config.Token.StartsWith("def/", StringComparison.Ordinal))
                {
                    var definition = DefDatabase<GenStepDef>.GetNamed(config.Token[4..]);
                    if (native.Any(n => ReferenceEquals(n.Step.def, definition)))
                        throw new InvalidOperationException("DuplicateNativeToken: " + config.Token);
                    if (!nativeSteps.Any(n => n.Token == config.Token)) nativeSteps.Add(new(config.Token, new(definition, default)));
                }
                else if (!nativeSteps.Any(n => n.Token == config.Token)) throw new InvalidOperationException("Unknown native token: " + config.Token);
            }
            var explicitSteps = nativeSteps.Where(n => authoredTokens.Contains(n.Token)).ToArray();
            foreach (var authoredStep in explicitSteps)
                if (nativeSteps.Any(other => !ReferenceEquals(other, authoredStep) &&
                    (authoredStep.Step.def.preventsGenSteps?.Contains(other.Step.def) == true || other.Step.def.preventsGenSteps?.Contains(authoredStep.Step.def) == true)))
                    throw new InvalidOperationException("NativePreventionConflict: " + authoredStep.Token);
            // Apply native prevention only after explicit modules have joined the effective candidate set.
            var prevented = native.Where(a => nativeSteps.Any(b => b.Step.def.preventsGenSteps?.Contains(a.Step.def) == true)).ToArray();
            foreach (var candidate in prevented)
            {
                if (candidate.Required) throw new InvalidOperationException("RequiredNativeStepConflict: " + candidate.Key);
                native.Remove(candidate);
                nativeSteps.RemoveAll(n => n.Token == candidate.Key);
            }
            var all = new List<MapGenerationStepSpec>();
            var providers = new Dictionary<string, string>(StringComparer.Ordinal);
            void Provide(string input, string key)
            { if (!providers.TryAdd(input, key)) throw new InvalidOperationException("DuplicateNativeInputProvider: " + input); }
            foreach (var n in native.Where(n => n.Step.def.genStep is GenStep_ElevationFertility))
            { Provide("Elevation", n.Key); Provide("Fertility", n.Key); }
            foreach (var step in authored.Where(step => step.HandlerId == LocalMapBuiltinConfigurations.Native))
            {
                var token = LocalMapBuiltinConfigurations.Decode<NativeGenStepConfiguration>(step.Configuration).Token;
                if (nativeSteps.Single(n => n.Token == token).Step.def.genStep is GenStep_ElevationFertility)
                { Provide("Elevation", step.Key); Provide("Fertility", step.Key); }
            }
            foreach (var pair in contributions) foreach (string input in pair.Value.ProvidesNativeInputs) Provide(input, pair.Key);
            foreach (var step in authored)
            {
                var before = step.Before.ToList(); var after = step.After.ToList();
                if (step.HandlerId == LocalMapBuiltinConfigurations.Native)
                {
                    var token = LocalMapBuiltinConfigurations.Decode<NativeGenStepConfiguration>(step.Configuration).Token;
                    foreach (string input in RequiredInputs(nativeSteps.Single(n => n.Token == token).Step.def.genStep))
                        after.Add(providers.TryGetValue(input, out string? provider) ? provider :
                            throw new InvalidOperationException("MissingNativeInputProvider: " + input + " -> " + step.Key));
                }
                if (contributions.TryGetValue(step.Key, out var contribution))
                {
                    after.AddRange(native.Where(n => contribution.AfterNativeStage == n.Stage || contribution.After.Any(s => NativeMapStepSelection.Matches(s, frozen.Source.BiomeId, n.Step.def))).Select(n => n.Key));
                    before.AddRange(native.Where(n => contribution.BeforeNativeStage == n.Stage).Select(n => n.Key));
                }
                all.Add(Copy(step, before, after));
            }
            foreach (var n in native)
            {
                var after = RequiredInputs(n.Step.def.genStep).Select(input => providers.TryGetValue(input, out string? key) ? key :
                    throw new InvalidOperationException("MissingNativeInputProvider: " + input + " -> " + n.Key)).ToList();
                var previous = native.LastOrDefault(p => p.Index < n.Index && p.Stage == n.Stage);
                if (previous != null) after.Add(previous.Key);
                all.Add(new(n.Key, LocalMapBuiltinConfigurations.Native, 1, 1, n.Stage,
                    n.Stage <= MapGenerationStage.NaturalSurface ? MapGenerationDomain.Natural : MapGenerationDomain.Purpose,
                    LocalMapBuiltinConfigurations.Encode(new NativeGenStepConfiguration(n.Key)), after: after.Distinct(StringComparer.Ordinal), nativeOrder: IsAdapted(n.Step.def) ? null : n.Index,
                    isOpaque: !IsAdapted(n.Step.def)));
            }
            var effective = new LocalMapGenerationSpec(new LocalMapRecipe(frozen.Specification.Recipe.Id + ".effective", all), nativeGeneratorId: frozen.Specification.NativeGeneratorId);
            var result = MapGenerationCompiler.Compile(effective, purpose, registry);
            if (!result.Success) throw new InvalidOperationException("InvalidNativePlan: " + string.Join("; ", result.Diagnostics.Where(d => !d.IsWarning).Select(d => d.Code + ":" + d.StepKey + ":" + d.Detail)));
            return new(result.Plan!, modules, nativeSteps, initial);
        }
        private sealed record Candidate(GenStepWithParams Step, string Key, int Index, MapGenerationStage Stage, bool Required);
        private static readonly IReadOnlyDictionary<string, MapGenerationStage> semanticStages = new Dictionary<string, MapGenerationStage>(StringComparer.Ordinal) {
            ["GenStep_ElevationFertility"] = MapGenerationStage.Fields, ["GenStep_MutatorPostElevationFertility"] = MapGenerationStage.Fields,
            ["GenStep_RocksFromGrid"] = MapGenerationStage.NaturalSurface, ["GenStep_Terrain"] = MapGenerationStage.NaturalSurface,
            ["GenStep_MutatorPostTerrain"] = MapGenerationStage.NaturalSurface, ["GenStep_RemoveTinyIslands"] = MapGenerationStage.NaturalSurface,
            ["GenStep_Roads"] = MapGenerationStage.NaturalSurface, ["GenStep_RockChunks"] = MapGenerationStage.NaturalSurface,
            ["GenStep_ScatterGeysers"] = MapGenerationStage.NaturalSurface,
            ["GenStep_FindPlayerStartSpot"] = MapGenerationStage.Content, ["GenStep_ScenParts"] = MapGenerationStage.Content,
            ["GenStep_MutatorCriticalStructures"] = MapGenerationStage.Content, ["GenStep_MutatorNonCriticalStructures"] = MapGenerationStage.Content,
            ["GenStep_Plants"] = MapGenerationStage.Populate, ["GenStep_Animals"] = MapGenerationStage.Populate,
            ["GenStep_Snow"] = MapGenerationStage.Populate, ["GenStep_Fog"] = MapGenerationStage.Populate,
            ["GenStep_CaveHives"] = MapGenerationStage.Populate, ["GenStep_MutatorFinal"] = MapGenerationStage.Populate };
        internal static bool IsAdapted(GenStepDef step) => step.genStep.GetType().Assembly == typeof(GenStep).Assembly && semanticStages.ContainsKey(step.genStep.GetType().Name);
        // Unadapted nodes retain their native numeric slot and the opaque relative-order chain. Their unknown dependencies remain unverified.
        internal static MapGenerationStage Classify(GenStepDef step) => IsAdapted(step) ? semanticStages[step.genStep.GetType().Name] :
            step.order < 200 ? MapGenerationStage.Fields : step.order < 400 ? MapGenerationStage.NaturalSurface : step.order < 900 ? MapGenerationStage.Content : MapGenerationStage.Populate;
        private static MapGenerationStepSpec Copy(MapGenerationStepSpec s, IEnumerable<string> before, IEnumerable<string> after) =>
            new(s.Key, s.HandlerId, s.ConfigurationVersion, s.AlgorithmVersion, s.Stage, s.Domain, s.Configuration,
                before.Distinct(StringComparer.Ordinal), after.Distinct(StringComparer.Ordinal), s.Inputs, s.Outputs, s.NativeOrder, s.IsOpaque);
        private static IEnumerable<string> RequiredInputs(GenStep step)
        {
            if (step is GenStep_Terrain) return new[] { "Elevation", "Fertility" };
            if (step is GenStep_RocksFromGrid || step.GetType().Name == "GenStep_MutatorPostElevationFertility") return new[] { "Elevation" };
            return Array.Empty<string>();
        }
        internal static void RequireNativeInputs(GenStep step)
        {
            foreach (string input in RequiredInputs(step))
                if (!MapGenerator.TryGetVar<MapGenFloatGrid>(input, out var grid) || grid == null)
                    throw new InvalidOperationException("NativeInputUnavailable: " + input + " -> " + step.GetType().FullName);
        }
    }
}
