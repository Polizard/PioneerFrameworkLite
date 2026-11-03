using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PioneerFramework.Celestial
{
    public enum CelestialMapAction { Inspect, Settle, Land, Generate }
    public sealed record CelestialMapAddress(CelestialInstanceId InstanceId, int LayerId, int TileId, int? MapParentId = null);
    public sealed record CelestialMapRequest(CelestialOperationContext Context, CelestialMapAddress Address, CelestialMapAction Action, int SizeX, int SizeZ);
    public sealed record CelestialMapHandle(CelestialMapAddress Address, int MapParentId, int MapId);
    public sealed record CelestialMapOperation(CelestialOperation Operation, CelestialMapHandle? Handle = null);
    public sealed record CelestialMapSiteRecord(CelestialMapAddress Address, int MapParentId, int? MapId, string? Failure = null)
    {
        public bool ParentRetired { get; init; }
        public IReadOnlyList<string> OperationIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> RetiredOperationIds { get; init; } = Array.Empty<string>();
    }
    public interface ICelestialMapGenerationCapability { CelestialMapProfile GetProfile(); }

    public sealed class CelestialMapBiomeProfile
    {
        [System.Text.Json.Serialization.JsonPropertyName("BiomeId"), System.Text.Json.Serialization.JsonPropertyOrder(1)]
        public string BiomeId { get; }
        [System.Text.Json.Serialization.JsonPropertyName("SkipStepNames"), System.Text.Json.Serialization.JsonPropertyOrder(2)]
        public IReadOnlyList<string> SkipStepNames { get; }
        [System.Text.Json.Serialization.JsonPropertyName("SkipStepTypes"), System.Text.Json.Serialization.JsonPropertyOrder(3)]
        public IReadOnlyList<string> SkipStepTypes { get; }
        [System.Text.Json.Serialization.JsonPropertyName("ReplaceRockTerrains"), System.Text.Json.Serialization.JsonPropertyOrder(4)]
        public bool ReplaceRockTerrains { get; }
        public CelestialMapBiomeProfile(string biomeId, IReadOnlyList<string> skipStepNames, IReadOnlyList<string> skipStepTypes, bool replaceRockTerrains)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(biomeId);
            BiomeId = biomeId;
            SkipStepNames = CelestialMapProfile.CopyNames(skipStepNames);
            SkipStepTypes = CelestialMapProfile.CopyNames(skipStepTypes);
            ReplaceRockTerrains = replaceRockTerrains;
        }
    }
    public sealed class CelestialMapProfile
    {
        [System.Text.Json.Serialization.JsonPropertyName("Id"), System.Text.Json.Serialization.JsonPropertyOrder(5)]
        public string Id { get; }
        [System.Text.Json.Serialization.JsonPropertyName("MapGeneratorId"), System.Text.Json.Serialization.JsonPropertyOrder(6)]
        public string? MapGeneratorId { get; }
        [System.Text.Json.Serialization.JsonPropertyName("SkipStepNames"), System.Text.Json.Serialization.JsonPropertyOrder(7)]
        public IReadOnlyList<string> SkipStepNames { get; }
        [System.Text.Json.Serialization.JsonPropertyName("SkipStepPrefixes"), System.Text.Json.Serialization.JsonPropertyOrder(8)]
        public IReadOnlyList<string> SkipStepPrefixes { get; }
        [System.Text.Json.Serialization.JsonPropertyName("SkipStepTypes"), System.Text.Json.Serialization.JsonPropertyOrder(9)]
        public IReadOnlyList<string> SkipStepTypes { get; }
        [System.Text.Json.Serialization.JsonPropertyName("ReplaceTerrainIds"), System.Text.Json.Serialization.JsonPropertyOrder(10)]
        public IReadOnlyList<string> ReplaceTerrainIds { get; }
        [System.Text.Json.Serialization.JsonPropertyName("ReplacementTerrainId"), System.Text.Json.Serialization.JsonPropertyOrder(11)]
        public string? ReplacementTerrainId { get; }
        [System.Text.Json.Serialization.JsonPropertyName("BiomeRules"), System.Text.Json.Serialization.JsonPropertyOrder(12)]
        public IReadOnlyList<CelestialMapBiomeProfile> BiomeRules { get; }
        [System.Text.Json.Serialization.JsonPropertyName("Diversity"), System.Text.Json.Serialization.JsonPropertyOrder(13),
            System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public CelestialPlanetMapDiversity? Diversity { get; }
        public CelestialMapProfile(string id, string? mapGeneratorId, IReadOnlyList<string> skipStepNames, IReadOnlyList<string> skipStepPrefixes,
            IReadOnlyList<string> skipStepTypes, IReadOnlyList<string> replaceTerrainIds, string? replacementTerrainId, IReadOnlyList<CelestialMapBiomeProfile> biomeRules,
            CelestialPlanetMapDiversity? diversity = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            Id = id; MapGeneratorId = mapGeneratorId;
            SkipStepNames = CopyNames(skipStepNames); SkipStepPrefixes = CopyNames(skipStepPrefixes); SkipStepTypes = CopyNames(skipStepTypes);
            ReplaceTerrainIds = CopyNames(replaceTerrainIds);
            if (biomeRules == null || biomeRules.Any(rule => rule == null) || biomeRules.Select(rule => rule.BiomeId).Distinct(StringComparer.Ordinal).Count() != biomeRules.Count)
                throw new ArgumentException("Invalid biome map rules.");
            BiomeRules = Array.AsReadOnly(biomeRules.ToArray());
            if ((ReplaceTerrainIds.Count > 0 || BiomeRules.Any(rule => rule.ReplaceRockTerrains)) && string.IsNullOrWhiteSpace(replacementTerrainId))
                throw new ArgumentException("Terrain replacement requires a target.");
            ReplacementTerrainId = replacementTerrainId;
            Diversity = diversity;
        }
        internal static IReadOnlyList<string> CopyNames(IReadOnlyList<string> names)
        {
            if (names == null || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
                throw new ArgumentException("Invalid map rule references.");
            return Array.AsReadOnly(names.ToArray());
        }
        public bool ShouldReplace(string biomeId, string terrainId, bool isRock) => ReplaceTerrainIds.Contains(terrainId, StringComparer.Ordinal) ||
            (isRock && BiomeRules.Any(rule => rule.BiomeId == biomeId && rule.ReplaceRockTerrains));
    }
    public sealed class MapGenerationResolvedProperties : ICelestialResolvedProperties
    {
        public const string Handler = "PFL.MapGeneration";
        public string Key { get; }
        public string HandlerId => Handler;
        public int Version => Profile.Diversity == null ? 1 : 2;
        public CelestialMapProfile Profile { get; }
        public MapGenerationResolvedProperties(string key, CelestialMapProfile profile)
        { ArgumentException.ThrowIfNullOrWhiteSpace(key); Key = key; Profile = profile ?? throw new ArgumentNullException(nameof(profile)); }
        public ReadOnlyMemory<byte> Serialize() => JsonSerializer.SerializeToUtf8Bytes(Profile);
        public static CelestialResult<MapGenerationResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload)
        {
            if (version is not (1 or 2)) return CelestialResult.Reject<MapGenerationResolvedProperties>(new CelestialError("UnsupportedConfigurationVersion", CompKey: key));
            try
            {
                var profile = CelestialResolvedJson.Read<CelestialMapProfile>(payload);
                if ((version == 2) != (profile.Diversity != null)) throw new ArgumentException("Map configuration version mismatch.");
                return CelestialResult.Ok(new MapGenerationResolvedProperties(key, profile));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            { return CelestialResult.Reject<MapGenerationResolvedProperties>(new CelestialError("InvalidConfigurationPayload", CompKey: key, Detail: exception.Message)); }
        }
    }
}
