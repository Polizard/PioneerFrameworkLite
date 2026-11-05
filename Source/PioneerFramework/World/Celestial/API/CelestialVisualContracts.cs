using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PioneerFramework.Celestial
{
    public enum CelestialVisualKind { Surface, Atmosphere, Clouds, Stellar }
    public interface ICelestialVisualCapability
    {
        CelestialVisualDescription GetVisualDescription();
        long VisualRevision => 0;
    }
    public sealed record CelestialVisualDescription(string CompKey, CelestialVisualKind Kind, CelestialVisualProfile Profile);
    public sealed record CelestialViewCacheKey(Guid WorldSessionId, CelestialInstanceId InstanceId, long DescriptorRevision, int? LayerId, string ConfigurationHash);
    public sealed class CelestialVisualProfile
    {
        [System.Text.Json.Serialization.JsonPropertyName("Id"), System.Text.Json.Serialization.JsonPropertyOrder(1)]
        public string Id { get; }
        [System.Text.Json.Serialization.JsonPropertyName("Red"), System.Text.Json.Serialization.JsonPropertyOrder(2)]
        public double Red { get; }
        [System.Text.Json.Serialization.JsonPropertyName("Green"), System.Text.Json.Serialization.JsonPropertyOrder(3)]
        public double Green { get; }
        [System.Text.Json.Serialization.JsonPropertyName("Blue"), System.Text.Json.Serialization.JsonPropertyOrder(4)]
        public double Blue { get; }
        [System.Text.Json.Serialization.JsonPropertyName("Alpha"), System.Text.Json.Serialization.JsonPropertyOrder(5)]
        public double Alpha { get; }
        [System.Text.Json.Serialization.JsonPropertyName("TexturePath"), System.Text.Json.Serialization.JsonPropertyOrder(6)]
        public string? TexturePath { get; }
        [System.Text.Json.Serialization.JsonPropertyName("BackgroundBiomeId"), System.Text.Json.Serialization.JsonPropertyOrder(7)]
        public string? BackgroundBiomeId { get; }
        [System.Text.Json.Serialization.JsonPropertyName("ShellHeightMeters"), System.Text.Json.Serialization.JsonPropertyOrder(8)]
        public double ShellHeightMeters { get; }
        [System.Text.Json.Serialization.JsonPropertyName("FadeRangeMeters"), System.Text.Json.Serialization.JsonPropertyOrder(9)]
        public double FadeRangeMeters { get; }
        [System.Text.Json.Serialization.JsonPropertyName("DriftPeriodTicks"), System.Text.Json.Serialization.JsonPropertyOrder(10)]
        public double DriftPeriodTicks { get; }
        [System.Text.Json.Serialization.JsonPropertyName("DistortionPeriodTicks"), System.Text.Json.Serialization.JsonPropertyOrder(11)]
        public double DistortionPeriodTicks { get; }
        public CelestialVisualProfile(string id, double red, double green, double blue, double alpha, string? texturePath, string? backgroundBiomeId,
            double shellHeightMeters, double fadeRangeMeters, double driftPeriodTicks, double distortionPeriodTicks)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            foreach (double channel in new[] { red, green, blue, alpha })
                if (!double.IsFinite(channel) || channel < 0 || channel > 1) throw new ArgumentOutOfRangeException(nameof(red));
            if (!double.IsFinite(shellHeightMeters) || shellHeightMeters < 0 || !double.IsFinite(fadeRangeMeters) || fadeRangeMeters <= 0 ||
                !double.IsFinite(driftPeriodTicks) || driftPeriodTicks <= 0 || !double.IsFinite(distortionPeriodTicks) || distortionPeriodTicks <= 0)
                throw new ArgumentOutOfRangeException(nameof(shellHeightMeters));
            if (texturePath != null) ArgumentException.ThrowIfNullOrWhiteSpace(texturePath);
            if (backgroundBiomeId != null) ArgumentException.ThrowIfNullOrWhiteSpace(backgroundBiomeId);
            Id = id; Red = red; Green = green; Blue = blue; Alpha = alpha; TexturePath = texturePath; BackgroundBiomeId = backgroundBiomeId;
            ShellHeightMeters = shellHeightMeters; FadeRangeMeters = fadeRangeMeters; DriftPeriodTicks = driftPeriodTicks; DistortionPeriodTicks = distortionPeriodTicks;
        }
    }
    public sealed class VisualResolvedProperties : ICelestialResolvedProperties
    {
        public string Key { get; }
        public CelestialVisualKind Kind { get; }
        public string HandlerId => HandlerFor(Kind);
        public int Version => 1;
        public CelestialVisualProfile Profile { get; }
        public static string HandlerFor(CelestialVisualKind kind) => "PFL.Visual." + kind;
        public static string CapabilityFor(CelestialVisualKind kind) => "visual." + kind.ToString().ToLowerInvariant();
        public VisualResolvedProperties(string key, CelestialVisualKind kind, CelestialVisualProfile profile)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            Key = key; Kind = kind; Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }
        public ReadOnlyMemory<byte> Serialize() => JsonSerializer.SerializeToUtf8Bytes(Profile);
        public static CelestialResult<VisualResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload, CelestialVisualKind kind) =>
            CelestialResolvedJson.Decode<VisualResolvedProperties, CelestialVisualProfile>(key, version, payload, data => new(key, kind, data));
    }
    /// <summary>姿态和速度均来自同一权威采样；固连点使用物理米，显示尺度独立保存。</summary>
    public sealed class CelestialViewSnapshot
    {
        public CelestialBodyDescription Description { get; }
        public PhysicsSampleStamp Stamp { get; }
        public CelestialMotionState Motion { get; }
        public double? NativeDisplayRadius { get; }
        public double UnitsPerMeter => NativeDisplayRadius.HasValue ? NativeDisplayRadius.Value / Description.RadiusMeters : SpaceConstants.UnitsPerMeter;
        public CelestialViewCacheKey CacheKey { get; }
        public IReadOnlyList<CelestialVisualDescription> Visuals { get; }
        internal CelestialViewSnapshot(CelestialBodyDescription description, PhysicsSampleStamp stamp, CelestialMotionState motion, double? nativeDisplayRadius,
            CelestialViewCacheKey cacheKey, IReadOnlyList<CelestialVisualDescription> visuals)
        { Description = description; Stamp = stamp; Motion = motion; NativeDisplayRadius = nativeDisplayRadius; CacheKey = cacheKey; Visuals = visuals; }
        public DVector3 FixedPointToSystem(DVector3 point) => Motion.Position + DVector3.Rotate(point, Motion.Orientation);
        public DVector3 SystemPointToFixed(DVector3 point) => DVector3.Rotate(point - Motion.Position, DQuaternion.Inverse(Motion.Orientation));
        public DVector3 FixedPointVelocity(DVector3 point) => Motion.LinearVelocity + DVector3.Cross(Motion.AngularVelocity, DVector3.Rotate(point, Motion.Orientation));
    }
}
