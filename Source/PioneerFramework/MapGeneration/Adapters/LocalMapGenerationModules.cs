using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace PioneerFramework.MapGeneration
{
    public static class LocalMapGenerationModules
    {
        public static MapGenerationRegistry Registry { get; } = CreateRegistry(true);
        public static void Register(GenerationModuleRegistration registration) => Registry.Register(registration);
        public static MapGenerationRegistry Freeze() { Registry.Freeze(); return Registry; }
        public static MapGenerationRegistry CreateRegistry(bool validateNativeReferences = false)
        {
            var registry = new MapGenerationRegistry();
            void Add<T>(string id, Action<T> validate, Func<T, IMapGenerationModule> create) where T : class
                => registry.Register(new(id, 1, new[] { 1 }, bytes => validate(LocalMapBuiltinConfigurations.Decode<T>(bytes)),
                    bytes => create(LocalMapBuiltinConfigurations.Decode<T>(bytes))));
            void Definition<T>(string id) where T : Def
            { if (validateNativeReferences && DefDatabase<T>.GetNamedSilentFail(id) == null) throw new ArgumentException("Missing " + typeof(T).Name + ": " + id); }
            void Selector(NativeMapStepSelector selector, bool optionalNames = false)
            {
                if (!optionalNames) foreach (string id in selector.Names) Definition<GenStepDef>(id);
                if (selector.BiomeId != null) Definition<BiomeDef>(selector.BiomeId);
                foreach (string name in selector.Types)
                {
                    var type = GenTypes.GetTypeInAnyAssembly(name);
                    if (type == null || !typeof(GenStep).IsAssignableFrom(type)) throw new ArgumentException("Invalid native step type: " + name);
                }
            }
            Add<NativeGenStepConfiguration>(LocalMapBuiltinConfigurations.Native, config => {
                if (config.Token.StartsWith("def/", StringComparison.Ordinal)) Definition<GenStepDef>(config.Token[4..]);
                if (config.Replaces != null) Selector(config.Replaces);
            }, config => new NativeGenStepModule(config));
            Add<NativeRulesConfiguration>(LocalMapBuiltinConfigurations.NativeRules, config => { foreach (var selector in config.Skip) Selector(selector, true); }, config => new NativeRulesModule(config));
            Add<TerrainReplacementConfiguration>(LocalMapBuiltinConfigurations.TerrainReplacement, config => {
                foreach (string id in config.TerrainIds.Append(config.TargetTerrainId)) Definition<TerrainDef>(id);
                foreach (string id in config.RockBiomeIds) Definition<BiomeDef>(id);
                if (config.Replaces != null) Selector(config.Replaces);
            }, config => new TerrainReplacementModule(config));
            Add<InitialMapInputs>(LocalMapBuiltinConfigurations.InitialInputs, config => {
                if (!validateNativeReferences) return;
                foreach (var rock in config.Rocks) if (DefDatabase<ThingDef>.GetNamedSilentFail(rock.Id)?.IsNonResourceNaturalRock != true) throw new ArgumentException("Invalid natural rock: " + rock.Id);
                foreach (var mineral in config.Minerals)
                { var def = DefDatabase<ThingDef>.GetNamedSilentFail(mineral.Id); if (def?.category != ThingCategory.Building || def.building?.mineableThing == null) throw new ArgumentException("Invalid mineral: " + mineral.Id); }
                foreach (var soil in config.Soils)
                { var def = DefDatabase<TerrainDef>.GetNamedSilentFail(soil.Id); if (def == null || def.IsWater || def.IsRock || !float.IsFinite(def.fertility) || def.fertility < 0) throw new ArgumentException("Invalid soil: " + soil.Id); }
                foreach (var plant in config.Plants)
                { var def = DefDatabase<ThingDef>.GetNamedSilentFail(plant.Id); if (def?.category != ThingCategory.Plant || def.plant == null) throw new ArgumentException("Invalid plant: " + plant.Id); }
                foreach (var animal in config.Animals) if (DefDatabase<PawnKindDef>.GetNamedSilentFail(animal.Id)?.race?.race?.Animal != true) throw new ArgumentException("Invalid animal: " + animal.Id);
            }, config => new InitialInputsModule(config));
            return registry;
        }
        /// <summary>Static validation deliberately defers candidate-dependent ordering and resource graph checks to final compilation.</summary>
        public static void ValidateRecipe(LocalMapRecipe recipe, MapGenerationRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(recipe); ArgumentNullException.ThrowIfNull(registry);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            bool Matches(IReadOnlyList<MapGenerationPort> ports, IReadOnlyList<MapGenerationPortContract> contracts)
                => ports.Count == contracts.Count && ports.All(port => contracts.Any(contract => contract.Name == port.Name && contract.TypeId == port.TypeId));
            foreach (var step in recipe.Steps)
            {
                if (step == null || !keys.Add(step.Key)) throw new ArgumentException("Null or duplicate recipe step key.");
                if (!registry.TryResolve(step.HandlerId, step.ConfigurationVersion, out var registration) || registration == null) throw new ArgumentException("Unknown module handler/configuration version: " + step.HandlerId);
                if (!registration.AlgorithmVersions.Contains(step.AlgorithmVersion)) throw new ArgumentException("Unsupported module algorithm version.");
                if (!Matches(step.Inputs, registration.Inputs) || !Matches(step.Outputs, registration.Outputs)) throw new ArgumentException("Module port contract mismatch.");
                if ((step.HandlerId == LocalMapBuiltinConfigurations.InitialInputs || step.HandlerId == LocalMapBuiltinConfigurations.NativeRules) && step.Stage != MapGenerationStage.Fields)
                    throw new ArgumentException("Initial inputs/native rules must be Fields modules.");
                if (step.HandlerId == LocalMapBuiltinConfigurations.TerrainReplacement && step.Stage != MapGenerationStage.NaturalSurface)
                    throw new ArgumentException("Terrain replacement must be a NaturalSurface module.");
                registration.ValidateConfiguration(step.Configuration);
            }
            var produced = recipe.Steps.SelectMany(step => step.Outputs).Select(port => port.Key).ToHashSet(StringComparer.Ordinal);
            var external = new Dictionary<string, MapGenerationPortContract>(StringComparer.Ordinal);
            foreach (var step in recipe.Steps)
            {
                registry.TryResolve(step.HandlerId, step.ConfigurationVersion, out var registration);
                foreach (var input in step.Inputs.Where(input => !produced.Contains(input.Key)))
                {
                    var contract = registration!.Inputs.Single(port => port.Name == input.Name);
                    if (external.TryGetValue(input.Key, out var existing) && (existing.TypeId != contract.TypeId || existing.RuntimeType != contract.RuntimeType))
                        throw new ArgumentException("Conflicting external resource types: " + input.Key);
                    external[input.Key] = new(input.Key, contract.TypeId, contract.RuntimeType);
                }
            }
            var local = recipe.Steps.Select(step => new MapGenerationStepSpec(step.Key, step.HandlerId, step.ConfigurationVersion, step.AlgorithmVersion,
                step.Stage, step.Domain, step.Configuration, step.Before.Where(keys.Contains), step.After.Where(keys.Contains), step.Inputs, step.Outputs,
                step.NativeOrder, step.IsOpaque));
            var validation = MapGenerationCompiler.Compile(new(new LocalMapRecipe(recipe.Id, local)), MapGenerationPurpose.Colony, registry, externalInputs: external.Values);
            if (!validation.Success) throw new ArgumentException("Invalid static local recipe: " + string.Join("; ", validation.Diagnostics.Where(d => !d.IsWarning).Select(d => d.Code + ":" + d.StepKey + ":" + d.Detail)));
        }
        public static void ValidateSpecification(LocalMapGenerationSpec specification, MapGenerationRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(specification);
            void ValidatePurposeStep(MapGenerationStepSpec step)
            {
                if (step.Domain != MapGenerationDomain.Purpose || step.Stage < MapGenerationStage.Content ||
                    step.HandlerId is LocalMapBuiltinConfigurations.InitialInputs or LocalMapBuiltinConfigurations.NativeRules or LocalMapBuiltinConfigurations.TerrainReplacement or LocalMapBuiltinConfigurations.Native)
                    throw new ArgumentException("Purpose modules must use Content/Populate and cannot override natural generation.");
            }
            foreach (var step in specification.Recipe.Steps.Where(step => step.Domain == MapGenerationDomain.Purpose)) ValidatePurposeStep(step);
            ValidateRecipe(specification.Recipe, registry);
            foreach (var purpose in specification.PurposeRecipes.Values)
            {
                foreach (var step in purpose.Steps) ValidatePurposeStep(step);
                ValidateRecipe(new LocalMapRecipe(specification.Recipe.Id + "+" + purpose.Id, specification.Recipe.Steps.Concat(purpose.Steps)), registry);
            }
        }
    }
    public sealed class NativeGenStepModule(NativeGenStepConfiguration configuration) : IMapGenerationModule, IMapGenerationPostInitializer, INativeMapPlanContributor
    {
        public void Execute(MapGenerationContext context) => context.RequireService<INativeMapGenerationServices>().GenerateNative(configuration.Token, context.Seed);
        public void PostInitialize(MapGenerationContext context) => context.RequireService<INativeMapGenerationServices>().PostInitializeNative(configuration.Token, context.Seed);
        public NativeMapPlanContribution Contribute(MapSourceContext source) => new(configuration.Replaces == null ? null : new[] { configuration.Replaces });
    }
    public sealed class NativeRulesModule(NativeRulesConfiguration configuration) : IMapGenerationModule, INativeMapPlanContributor
    {
        public void Execute(MapGenerationContext context) { }
        public NativeMapPlanContribution Contribute(MapSourceContext source) => new(configuration.Skip);
    }
    public sealed class TerrainReplacementModule(TerrainReplacementConfiguration configuration) : IMapGenerationModule, INativeMapPlanContributor
    {
        public void Execute(MapGenerationContext context) => context.RequireService<INativeMapGenerationServices>().ApplyTerrainReplacement(configuration);
        public NativeMapPlanContribution Contribute(MapSourceContext source) => new(configuration.Replaces == null ? null : new[] { configuration.Replaces },
            new[] { new NativeMapStepSelector(types: new[] { typeof(GenStep_Terrain).FullName! }) }, MapGenerationStage.NaturalSurface);
    }
    public sealed class InitialInputsModule(InitialMapInputs configuration) : IMapGenerationModule, INativeMapPlanContributor
    {
        public void Execute(MapGenerationContext context) => context.RequireService<INativeMapGenerationServices>().SetInitialOverrides(configuration);
        public NativeMapPlanContribution Contribute(MapSourceContext source) => new(beforeNativeStage: MapGenerationStage.Fields, initialInputs: configuration);
    }
}
