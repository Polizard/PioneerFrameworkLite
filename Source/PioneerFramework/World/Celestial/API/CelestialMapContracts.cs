using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PioneerFramework.MapGeneration;

namespace PioneerFramework.Celestial
{
    public enum CelestialMapAction { Inspect, Settle, Land, Generate }
    public sealed record CelestialMapAddress(CelestialInstanceId InstanceId, int LayerId, int TileId, int? MapParentId = null);
    public sealed record CelestialMapRequest(CelestialOperationContext Context, CelestialMapAddress Address, CelestialMapAction Action, int SizeX, int SizeZ);
    public sealed record CelestialMapHandle(CelestialMapAddress Address, int MapParentId, int MapId);
    public sealed record CelestialMapOperation(CelestialOperation Operation, CelestialMapHandle? Handle = null);
    public sealed record CelestialMapSiteRecord(CelestialMapAddress Address, int MapParentId, int? MapId, string? Failure = null)
    {
        public LocalMapGenerationRecord? Generation { get; init; }
        public bool ParentRetired { get; init; }
        public IReadOnlyList<string> OperationIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> RetiredOperationIds { get; init; } = Array.Empty<string>();
    }
    public interface ICelestialMapGenerationCapability
    {
        LocalMapGenerationSpec GetSpecification();
        CelestialMapProfile GetProfile() => throw new NotSupportedException("This capability has no map profile.");
    }
    public interface ICelestialLocalMapGenerationProperties : ICelestialResolvedProperties
    {
        LocalMapGenerationSpec Specification { get; }
    }

    /// <summary>Frozen per-instance map configuration: identity, long-lived ecology diversity, and the natural/purpose recipe.</summary>
    public sealed class CelestialMapProfile
    {
        [System.Text.Json.Serialization.JsonPropertyName("Id"), System.Text.Json.Serialization.JsonPropertyOrder(1)]
        public string Id { get; }
        [System.Text.Json.Serialization.JsonPropertyName("Diversity"), System.Text.Json.Serialization.JsonPropertyOrder(2),
            System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public CelestialPlanetMapDiversity? Diversity { get; }
        [System.Text.Json.Serialization.JsonPropertyOrder(3),
            System.Text.Json.Serialization.JsonConverter(typeof(LocalMapGenerationJsonConverter))]
        public LocalMapGenerationSpec LocalSpecification { get; }
        public CelestialMapProfile(string id, CelestialPlanetMapDiversity? diversity = null, LocalMapGenerationSpec? localSpecification = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            Id = id;
            Diversity = diversity;
            LocalSpecification = localSpecification ?? throw new ArgumentNullException(nameof(localSpecification));
        }
        internal static IReadOnlyList<string> CopyNames(IReadOnlyList<string> names)
        {
            if (names == null || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
                throw new ArgumentException("Invalid map rule references.");
            return Array.AsReadOnly(names.ToArray());
        }
    }
    public sealed class MapGenerationResolvedProperties : ICelestialLocalMapGenerationProperties
    {
        public const string Handler = "PFL.MapGeneration";
        /// <summary>Current frozen map configuration schema. Legacy XML/configuration versions are not decoded.</summary>
        public const int CurrentVersion = 4;
        public string Key { get; }
        public string HandlerId => Handler;
        public int Version => CurrentVersion;
        public CelestialMapProfile Profile { get; }
        public LocalMapGenerationSpec Specification => Profile.LocalSpecification;
        public MapGenerationResolvedProperties(string key, CelestialMapProfile profile)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            Key = key;
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }
        public ReadOnlyMemory<byte> Serialize() => JsonSerializer.SerializeToUtf8Bytes(Profile);
        public static CelestialResult<MapGenerationResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload)
        {
            if (version != CurrentVersion) return CelestialResult.Reject<MapGenerationResolvedProperties>(new CelestialError("UnsupportedConfigurationVersion", CompKey: key));
            try
            {
                var profile = CelestialResolvedJson.Read<CelestialMapProfile>(payload);
                return CelestialResult.Ok(new MapGenerationResolvedProperties(key, profile));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            { return CelestialResult.Reject<MapGenerationResolvedProperties>(new CelestialError("InvalidConfigurationPayload", CompKey: key, Detail: exception.Message)); }
        }
    }
}
