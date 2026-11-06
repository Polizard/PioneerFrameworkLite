using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.MapGeneration
{
    /// <summary>Public XML protocol: extension subclasses freeze custom typed config into bytes for their registered handler.</summary>
    public abstract class LocalMapModuleProperties
    {
        public abstract ReadOnlyMemory<byte> FreezeConfiguration();
        public virtual ReadOnlyMemory<byte> FreezeConfiguration(PioneerFramework.Celestial.CelestialGenerationContext? context) => FreezeConfiguration();
    }
    public sealed class LocalMapPortXml
    {
        public string name = "";
        public string key = "";
        public string typeId = "";
        public MapGenerationPort Freeze() => new(name, key, typeId);
    }
    public sealed class LocalMapGenerationStepXml
    {
        public string key = "";
        public string handlerId = "";
        public int configVersion = 1;
        public int algorithmVersion = 1;
        public MapGenerationStage stage;
        public MapGenerationDomain domain;
        public List<string> before = new();
        public List<string> after = new();
        public List<LocalMapPortXml> inputs = new();
        public List<LocalMapPortXml> outputs = new();
        public LocalMapModuleProperties properties = null!;
        public MapGenerationStepSpec Freeze(PioneerFramework.Celestial.CelestialGenerationContext? context = null) => new(key, handlerId, configVersion, algorithmVersion, stage, domain,
            properties?.FreezeConfiguration(context) ?? throw new ArgumentException("Missing module properties."), before, after,
            inputs.Select(port => port.Freeze()), outputs.Select(port => port.Freeze()));
    }
    public sealed class LocalMapPurposeRecipeXml
    {
        public MapGenerationPurpose purpose;
        public LocalMapRecipeDef recipe = null!;
    }
    public sealed class LocalMapRecipeDef : Def
    {
        public string? recipeId;
        public List<LocalMapGenerationStepXml> steps = new();
        public LocalMapRecipe ToRecipe(MapGenerationRegistry? registry = null, PioneerFramework.Celestial.CelestialGenerationContext? context = null)
        {
            var recipe = new LocalMapRecipe(recipeId ?? defName, steps.Select(step => step.Freeze(context)));
            LocalMapGenerationModules.ValidateRecipe(recipe, registry ?? LocalMapGenerationModules.Registry);
            return recipe;
        }
        public LocalMapGenerationSpec ToSpecification(MapGenerationRegistry? registry = null, string? nativeGeneratorId = null, PioneerFramework.Celestial.CelestialGenerationContext? context = null)
            => new(ToRecipe(registry, context), nativeGeneratorId: nativeGeneratorId);
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            string? failure = null;
            try { _ = ToRecipe(); } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { failure = exception.Message; }
            if (failure != null) yield return failure;
        }
    }
    public sealed class NativeStepSelectorXml
    {
        public List<GenStepDef> steps = new();
        public List<string> optionalNames = new();
        public List<string> prefixes = new();
        public List<Type> types = new();
        public RimWorld.BiomeDef? biome;
        public NativeMapStepSelector Freeze() => new(steps.Select(step => step?.defName ?? throw new ArgumentException("Missing GenStepDef.")).Concat(optionalNames).ToArray(), prefixes,
            types.Select(type => type != null && typeof(GenStep).IsAssignableFrom(type) && type.FullName != null ? type.FullName : throw new ArgumentException("Expected GenStep type.")).ToArray(), biome?.defName);
    }
    public sealed class NativeGenStepModuleProperties : LocalMapModuleProperties
    {
        public GenStepDef step = null!;
        public NativeStepSelectorXml? replaces;
        public override ReadOnlyMemory<byte> FreezeConfiguration() => LocalMapBuiltinConfigurations.Encode(new NativeGenStepConfiguration(
            "def/" + (step?.defName ?? throw new ArgumentException("Missing native GenStepDef.")), replaces?.Freeze()));
    }
    public sealed class NativeRulesModuleProperties : LocalMapModuleProperties
    {
        public List<NativeStepSelectorXml> skip = new();
        public override ReadOnlyMemory<byte> FreezeConfiguration() => LocalMapBuiltinConfigurations.Encode(new NativeRulesConfiguration(skip.Select(selector => selector.Freeze()).ToArray()));
    }
    public sealed class TerrainReplacementModuleProperties : LocalMapModuleProperties
    {
        public List<TerrainDef> terrains = new();
        public TerrainDef target = null!;
        public List<RimWorld.BiomeDef> rockBiomes = new();
        public NativeStepSelectorXml? replaces;
        public override ReadOnlyMemory<byte> FreezeConfiguration() => LocalMapBuiltinConfigurations.Encode(new TerrainReplacementConfiguration(
            terrains.Select(terrain => terrain?.defName ?? throw new ArgumentException("Missing TerrainDef.")).ToArray(),
            target?.defName ?? throw new ArgumentException("Missing target TerrainDef."), rockBiomes.Select(biome => biome?.defName ?? throw new ArgumentException("Missing BiomeDef.")).ToArray(), replaces?.Freeze()));
    }
    public sealed class InitialInputsModuleProperties : LocalMapModuleProperties
    {
        public PioneerFramework.Celestial.CelestialPlanetMapDiversityXml inputs = new();
        public override ReadOnlyMemory<byte> FreezeConfiguration() => FreezeConfiguration(null);
        public override ReadOnlyMemory<byte> FreezeConfiguration(PioneerFramework.Celestial.CelestialGenerationContext? context)
        {
            if (inputs.weather.Count > 0 || inputs.conditions.Count > 0 || inputs.incidents.Count > 0)
                throw new ArgumentException("Runtime weather/condition/incident policy cannot be supplied as initial map generation inputs.");
            var d = inputs.Resolve(context);
            return LocalMapBuiltinConfigurations.Encode(new InitialMapInputs(d.Rocks, d.Minerals, d.Soils, d.Plants, d.Animals,
                d.MineralAbundance, d.SoilFertility, d.PlantDensity, d.AnimalDensity, d.GeologyVariation, d.AllowCaves));
        }
    }
}
