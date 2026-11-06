using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PioneerFramework.Celestial;

namespace PioneerFramework.MapGeneration
{
    /// <summary>Stable adapter schemas. These DTOs contain no native objects or runtime policy overrides.</summary>
    public static class LocalMapBuiltinConfigurations
    {
        public const string Native = "PFL.LocalMap.NativeGenStep";
        public const string NativeRules = "PFL.LocalMap.NativeRules";
        public const string TerrainReplacement = "PFL.LocalMap.TerrainReplacement";
        public const string InitialInputs = "PFL.LocalMap.InitialInputs";
        private static readonly JsonSerializerOptions options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
        public static ReadOnlyMemory<byte> Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
        public static T Decode<T>(ReadOnlyMemory<byte> payload) where T : class
        {
            CelestialJsonFields.RejectDuplicates(payload.Span);
            return JsonSerializer.Deserialize<T>(payload.Span, options) ?? throw new ArgumentException("Missing local module configuration.");
        }
    }

    public sealed class NativeMapStepSelector
    {
        public IReadOnlyList<string> Names { get; }
        public IReadOnlyList<string> Prefixes { get; }
        public IReadOnlyList<string> Types { get; }
        public string? BiomeId { get; }
        public NativeMapStepSelector(IReadOnlyList<string>? names = null, IReadOnlyList<string>? prefixes = null,
            IReadOnlyList<string>? types = null, string? biomeId = null)
        {
            Names = CelestialMapProfile.CopyNames(names ?? Array.Empty<string>());
            Prefixes = CelestialMapProfile.CopyNames(prefixes ?? Array.Empty<string>());
            Types = CelestialMapProfile.CopyNames(types ?? Array.Empty<string>());
            if (biomeId != null) ArgumentException.ThrowIfNullOrWhiteSpace(biomeId);
            BiomeId = biomeId;
        }
    }
    public sealed class NativeGenStepConfiguration
    {
        public string Token { get; }
        public NativeMapStepSelector? Replaces { get; }
        public NativeGenStepConfiguration(string token, NativeMapStepSelector? replaces = null)
        { ArgumentException.ThrowIfNullOrWhiteSpace(token); Token = token; Replaces = replaces; }
    }
    public sealed class NativeRulesConfiguration
    {
        public IReadOnlyList<NativeMapStepSelector> Skip { get; }
        public NativeRulesConfiguration(IReadOnlyList<NativeMapStepSelector> skip)
        { ArgumentNullException.ThrowIfNull(skip); if (skip.Any(x => x == null)) throw new ArgumentException("Null native selector."); Skip = Array.AsReadOnly(skip.ToArray()); }
    }
    public sealed class TerrainReplacementConfiguration
    {
        public IReadOnlyList<string> TerrainIds { get; }
        public string TargetTerrainId { get; }
        public IReadOnlyList<string> RockBiomeIds { get; }
        public NativeMapStepSelector? Replaces { get; }
        public TerrainReplacementConfiguration(IReadOnlyList<string> terrainIds, string targetTerrainId,
            IReadOnlyList<string>? rockBiomeIds = null, NativeMapStepSelector? replaces = null)
        {
            TerrainIds = CelestialMapProfile.CopyNames(terrainIds); ArgumentException.ThrowIfNullOrWhiteSpace(targetTerrainId);
            TargetTerrainId = targetTerrainId; RockBiomeIds = CelestialMapProfile.CopyNames(rockBiomeIds ?? Array.Empty<string>()); Replaces = replaces;
        }
        public bool ShouldReplace(string biomeId, string terrainId, bool isRock) => TerrainIds.Contains(terrainId, StringComparer.Ordinal) || (isRock && RockBiomeIds.Contains(biomeId, StringComparer.Ordinal));
    }
    public sealed class InitialMapInputs
    {
        public IReadOnlyList<CelestialWeightedReference> Rocks { get; }
        public IReadOnlyList<CelestialWeightedReference> Minerals { get; }
        public IReadOnlyList<CelestialWeightedReference> Soils { get; }
        public IReadOnlyList<CelestialWeightedReference> Plants { get; }
        public IReadOnlyList<CelestialWeightedReference> Animals { get; }
        public double MineralAbundance { get; }
        public double SoilFertility { get; }
        public double PlantDensity { get; }
        public double AnimalDensity { get; }
        public double GeologyVariation { get; }
        public bool AllowCaves { get; }
        public InitialMapInputs(IReadOnlyList<CelestialWeightedReference>? rocks = null, IReadOnlyList<CelestialWeightedReference>? minerals = null,
            IReadOnlyList<CelestialWeightedReference>? soils = null, IReadOnlyList<CelestialWeightedReference>? plants = null,
            IReadOnlyList<CelestialWeightedReference>? animals = null, double mineralAbundance = 1, double soilFertility = 1,
            double plantDensity = 1, double animalDensity = 1, double geologyVariation = 0, bool allowCaves = true)
        {
            var validated = new CelestialPlanetMapDiversity(rocks, minerals, soils, plants, animals, mineralAbundance: mineralAbundance,
                soilFertility: soilFertility, plantDensity: plantDensity, animalDensity: animalDensity, geologyVariation: geologyVariation, allowCaves: allowCaves);
            Rocks = validated.Rocks; Minerals = validated.Minerals; Soils = validated.Soils; Plants = validated.Plants; Animals = validated.Animals;
            MineralAbundance = mineralAbundance; SoilFertility = soilFertility; PlantDensity = plantDensity; AnimalDensity = animalDensity;
            GeologyVariation = geologyVariation; AllowCaves = allowCaves;
        }
    }

    public static class CelestialLocalMapCompatibility
    {
        internal static MapGenerationStepSpec FreezeInitialInputs(string key, CelestialPlanetMapDiversity diversity) =>
            new(key, LocalMapBuiltinConfigurations.InitialInputs, 1, 1, MapGenerationStage.Fields, MapGenerationDomain.Natural,
                LocalMapBuiltinConfigurations.Encode(new InitialMapInputs(diversity.Rocks, diversity.Minerals, diversity.Soils,
                    diversity.Plants, diversity.Animals, diversity.MineralAbundance, diversity.SoilFertility, diversity.PlantDensity,
                    diversity.AnimalDensity, diversity.GeologyVariation, diversity.AllowCaves)));

        /// <summary>Translate once at configuration freeze, retaining the original world/runtime projection unchanged.</summary>
        public static LocalMapGenerationSpec ToSpecification(CelestialMapProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (profile.LocalSpecification != null) return profile.LocalSpecification;
            var selectors = new List<NativeMapStepSelector> { new(profile.SkipStepNames, profile.SkipStepPrefixes, profile.SkipStepTypes) };
            selectors.AddRange(profile.BiomeRules.Select(rule => new NativeMapStepSelector(rule.SkipStepNames, types: rule.SkipStepTypes, biomeId: rule.BiomeId)));
            var steps = new List<MapGenerationStepSpec> {
                new("legacy.native-rules", LocalMapBuiltinConfigurations.NativeRules, 1, 1, MapGenerationStage.Fields, MapGenerationDomain.Natural,
                    LocalMapBuiltinConfigurations.Encode(new NativeRulesConfiguration(selectors))) };
            if (profile.Diversity is { } d) steps.Add(FreezeInitialInputs("legacy.initial-inputs", d));
            if (profile.ReplacementTerrainId != null) steps.Add(new("legacy.terrain-replacement", LocalMapBuiltinConfigurations.TerrainReplacement, 1, 1,
                MapGenerationStage.NaturalSurface, MapGenerationDomain.Natural, LocalMapBuiltinConfigurations.Encode(new TerrainReplacementConfiguration(
                    profile.ReplaceTerrainIds, profile.ReplacementTerrainId, profile.BiomeRules.Where(rule => rule.ReplaceRockTerrains).Select(rule => rule.BiomeId).ToArray()))));
            return new(new LocalMapRecipe(profile.Id + ".local", steps), nativeGeneratorId: profile.MapGeneratorId);
        }
    }
}
