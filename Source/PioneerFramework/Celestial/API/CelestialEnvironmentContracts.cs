using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PioneerFramework.Celestial
{
    public interface ICelestialAtmosphereCapability { AtmosphereProfile GetProfile(); }
    public interface ICelestialLightSourceCapability { CelestialLightDescription GetDescription(); }
    public interface ICelestialEnvironmentCapability { CelestialEnvironmentSample Evaluate(CelestialEnvironmentQuery query); }
    public interface ICelestialCapabilityResolver
    {
        IReadOnlyList<T> GetCapabilities<T>(CelestialInstanceId id) where T : class;
        bool TryGetInitializationReference(CelestialInstanceId owner, string key, out CelestialInstanceId target);
    }
    public sealed record CelestialLightDescription([property: System.Text.Json.Serialization.JsonPropertyName("Intensity"), System.Text.Json.Serialization.JsonPropertyOrder(0)] double Intensity, [property: System.Text.Json.Serialization.JsonPropertyName("Red"), System.Text.Json.Serialization.JsonPropertyOrder(1)] double Red, [property: System.Text.Json.Serialization.JsonPropertyName("Green"), System.Text.Json.Serialization.JsonPropertyOrder(2)] double Green, [property: System.Text.Json.Serialization.JsonPropertyName("Blue"), System.Text.Json.Serialization.JsonPropertyOrder(3)] double Blue);
    public sealed record CelestialEnvironmentQuery(double DisplayTicksAbs, float LongitudeDegrees, float LatitudeDegrees, string? BiomeId = null);
    public sealed record CelestialEnvironmentSample(float DayPercent, float SunGlow, CelestialInstanceId PrimaryLightId, string? VacuumBiomeId, float DayOfYearFraction);
    public sealed class CelestialEnvironmentProfile
    {
        [System.Text.Json.Serialization.JsonPropertyName("Id"), System.Text.Json.Serialization.JsonPropertyOrder(1)]
        public string Id { get; }
        [System.Text.Json.Serialization.JsonPropertyName("DayLengthTicks"), System.Text.Json.Serialization.JsonPropertyOrder(2)]
        public double DayLengthTicks { get; }
        [System.Text.Json.Serialization.JsonPropertyName("PrimaryLightReference"), System.Text.Json.Serialization.JsonPropertyOrder(3)]
        public string PrimaryLightReference { get; }
        [System.Text.Json.Serialization.JsonPropertyName("VacuumBiomeIds"), System.Text.Json.Serialization.JsonPropertyOrder(4)]
        public IReadOnlyList<string> VacuumBiomeIds { get; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public CelestialClimateConfiguration? Climate { get; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public CelestialExposureConfiguration? Exposure { get; }
        public CelestialEnvironmentProfile(string id, double dayLengthTicks, string primaryLightReference, IReadOnlyList<string> vacuumBiomeIds,
            CelestialClimateConfiguration? climate = null, CelestialExposureConfiguration? exposure = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentException.ThrowIfNullOrWhiteSpace(primaryLightReference);
            if (!double.IsFinite(dayLengthTicks) || !float.IsFinite((float)dayLengthTicks) || (float)dayLengthTicks <= 0) throw new ArgumentOutOfRangeException(nameof(dayLengthTicks));
            if (vacuumBiomeIds == null || vacuumBiomeIds.Any(string.IsNullOrWhiteSpace) || vacuumBiomeIds.Distinct(StringComparer.Ordinal).Count() != vacuumBiomeIds.Count)
                throw new ArgumentException("Invalid vacuum biome references.");
            Climate = climate; Exposure = exposure;
            Id = id; DayLengthTicks = dayLengthTicks; PrimaryLightReference = primaryLightReference; VacuumBiomeIds = Array.AsReadOnly(vacuumBiomeIds.ToArray());
        }
    }
    internal static class CelestialResolvedJson
    {
        private static readonly JsonSerializerOptions Options = new() {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true };
        internal static TData Read<TData>(ReadOnlyMemory<byte> payload) where TData : class
        { CelestialJsonFields.RejectDuplicates(payload.Span); return JsonSerializer.Deserialize<TData>(payload.Span, Options) ?? throw new JsonException("Missing configuration."); }
        internal static CelestialResult<T> Decode<T, TData>(string key, int version, ReadOnlyMemory<byte> payload, Func<TData, T> create) where TData : class
        {
            if (version != 1) return CelestialResult.Reject<T>(new CelestialError("UnsupportedConfigurationVersion", CompKey: key));
            try { return CelestialResult.Ok(create(Read<TData>(payload))); }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                // 2026-11-07 A-4：未知字段通常是「存档来自更新版本」，诊断里显式给出字段路径而不是只有解析器原文。
                string detail = exception is JsonException json && !string.IsNullOrEmpty(json.Path)
                    ? exception.Message + " (path: " + json.Path + "; unknown/mismatched fields usually mean the payload came from a newer build)"
                    : exception.Message;
                return CelestialResult.Reject<T>(new CelestialError("InvalidConfigurationPayload", CompKey: key, Detail: detail));
            }
        }
    }
    public sealed partial class AtmosphereResolvedProperties : ICelestialResolvedProperties
    {
        public const string Handler = "PFL.Atmosphere";
        public string Key { get; }
        public string HandlerId => Handler;
        public int Version => PressurePascals.HasValue ? 2 : 1;
        public double Rho0 { get; }
        public double ScaleHeight { get; }
        public double TopAltitude { get; }
        public AtmosphereResolvedProperties(string key, double rho0, double scaleHeight, double topAltitude)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (new[] { rho0, scaleHeight, topAltitude }.Any(value => !double.IsFinite(value) || value < 0)) throw new ArgumentOutOfRangeException(nameof(rho0));
            Key = key; Rho0 = rho0; ScaleHeight = scaleHeight; TopAltitude = topAltitude;
        }
        public AtmosphereProfile BuildProfile() => AtmosphereModel.Build(Rho0, ScaleHeight, TopAltitude);
        public ReadOnlyMemory<byte> Serialize() => Version == 2 ? SerializePressure() : JsonSerializer.SerializeToUtf8Bytes(new Configuration(Rho0, ScaleHeight, TopAltitude));
        public static CelestialResult<AtmosphereResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload) =>
            version == 2 ? DeserializePressure(key, payload) : CelestialResolvedJson.Decode<AtmosphereResolvedProperties, Configuration>(key, version, payload, data => new(key, data.Rho0, data.ScaleHeight, data.TopAltitude));
        private sealed record Configuration([property: System.Text.Json.Serialization.JsonPropertyName("Rho0")] double Rho0, [property: System.Text.Json.Serialization.JsonPropertyName("ScaleHeight")] double ScaleHeight, [property: System.Text.Json.Serialization.JsonPropertyName("TopAltitude")] double TopAltitude);
    }
    public sealed class LightSourceResolvedProperties : ICelestialResolvedProperties
    {
        public const string Handler = "PFL.LightSource";
        public string Key { get; }
        public string HandlerId => Handler;
        public int Version => 1;
        public CelestialLightDescription Description { get; }
        public LightSourceResolvedProperties(string key, CelestialLightDescription description)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(description);
            if (!double.IsFinite(description.Intensity) || description.Intensity < 0 || new[] { description.Red, description.Green, description.Blue }.Any(value => !double.IsFinite(value) || value < 0 || value > 1))
                throw new ArgumentException("Invalid light description.");
            Key = key; Description = description;
        }
        public ReadOnlyMemory<byte> Serialize() => JsonSerializer.SerializeToUtf8Bytes(Description);
        public static CelestialResult<LightSourceResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload) =>
            CelestialResolvedJson.Decode<LightSourceResolvedProperties, CelestialLightDescription>(key, version, payload, data => new(key, data));
    }
    public sealed class ExistingEnvironmentResolvedProperties : ICelestialResolvedProperties, ICelestialResolvedConfigurationValidator
    {
        public const string Handler = "PFL.ExistingEnvironment";
        public string Key { get; }
        public string HandlerId => Handler;
        public int Version => Profile.Climate != null || Profile.Exposure != null ? 2 : 1;
        public CelestialEnvironmentProfile Profile { get; }
        public ExistingEnvironmentResolvedProperties(string key, CelestialEnvironmentProfile profile)
        { ArgumentException.ThrowIfNullOrWhiteSpace(key); Key = key; Profile = profile ?? throw new ArgumentNullException(nameof(profile)); }
        public CelestialResult<bool> Validate(IReadOnlyList<ICelestialResolvedProperties> components) =>
            (Profile.Climate?.Mode == CelestialClimateMode.CommittedIllumination || Profile.Exposure?.GravityStats == true) &&
                components.OfType<ICelestialPhysicsBindingProperties>().Count() != 1 ?
                CelestialResult.Reject<bool>(new CelestialError("EnvironmentPhysicsRequired", CompKey: Key)) : CelestialResult.Ok(true);
        public ReadOnlyMemory<byte> Serialize() => Version == 1 ? JsonSerializer.SerializeToUtf8Bytes(
            new LegacyProfile(Profile.Id, Profile.DayLengthTicks, Profile.PrimaryLightReference, Profile.VacuumBiomeIds)) : JsonSerializer.SerializeToUtf8Bytes(Profile);
        private sealed record LegacyProfile(string Id, double DayLengthTicks, string PrimaryLightReference, IReadOnlyList<string> VacuumBiomeIds);
        public static CelestialResult<ExistingEnvironmentResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload) =>
            version == 1 ? CelestialResolvedJson.Decode<ExistingEnvironmentResolvedProperties, LegacyProfile>(key, 1, payload,
                data => new(key, new(data.Id, data.DayLengthTicks, data.PrimaryLightReference, data.VacuumBiomeIds))) :
            version == 2 ? CelestialResolvedJson.Decode<ExistingEnvironmentResolvedProperties, CelestialEnvironmentProfile>(key, 1, payload,
                data => data.Climate != null || data.Exposure != null ? new(key, data) : throw new ArgumentException("Empty v2 environment.")) :
            CelestialResult.Reject<ExistingEnvironmentResolvedProperties>(new CelestialError("UnsupportedConfigurationVersion", CompKey: key));
    }
}
