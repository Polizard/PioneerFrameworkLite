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
        [System.Text.Json.Serialization.JsonPropertyName("Appearance"), System.Text.Json.Serialization.JsonPropertyOrder(12),
            System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public CelestialVisualAppearance? Appearance { get; }
        public CelestialVisualProfile(string id, double red, double green, double blue, double alpha, string? texturePath, string? backgroundBiomeId,
            double shellHeightMeters, double fadeRangeMeters, double driftPeriodTicks, double distortionPeriodTicks)
            : this(id, red, green, blue, alpha, texturePath, backgroundBiomeId, shellHeightMeters, fadeRangeMeters, driftPeriodTicks, distortionPeriodTicks, null) { }
        [System.Text.Json.Serialization.JsonConstructor]
        public CelestialVisualProfile(string id, double red, double green, double blue, double alpha, string? texturePath, string? backgroundBiomeId,
            double shellHeightMeters, double fadeRangeMeters, double driftPeriodTicks, double distortionPeriodTicks, CelestialVisualAppearance? appearance = null)
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
            Appearance = appearance;
            if (appearance != null)
            {
                CelestialVisualAppearance.Domain(shellHeightMeters, 0, 1e9, nameof(shellHeightMeters));
                CelestialVisualAppearance.Domain(fadeRangeMeters, 1e-6, 1e9, nameof(fadeRangeMeters));
                CelestialVisualAppearance.Domain(driftPeriodTicks, 1, 1e15, nameof(driftPeriodTicks));
                CelestialVisualAppearance.Domain(distortionPeriodTicks, 1, 1e15, nameof(distortionPeriodTicks));
                if (appearance.Style == CelestialSurfaceStyle.GasBands && (texturePath != null || backgroundBiomeId != null))
                    throw new ArgumentException("Gas bands cannot select a surface texture or biome.");
            }
        }
        internal void ValidateVisualKind(CelestialVisualKind kind)
        {
            if (Appearance == null) return;
            if (kind == CelestialVisualKind.Stellar) throw new ArgumentException("Stellar visuals retain the v1 configuration.");
            if (Appearance.Style == CelestialSurfaceStyle.GasBands && kind != CelestialVisualKind.Surface)
                throw new ArgumentException("Gas bands require the surface visual slot.");
            if (kind != CelestialVisualKind.Clouds && Appearance.CloudCoverage != 1)
                throw new ArgumentException("Cloud coverage requires the clouds visual slot.");
            if (Appearance.Style != CelestialSurfaceStyle.GasBands && (Appearance.BandFrequency != 12 || Appearance.BandContrast != 0.25))
                throw new ArgumentException("Band parameters require the gas bands style.");
            if (kind != CelestialVisualKind.Clouds && (FadeRangeMeters != 5000 || DriftPeriodTicks != 600000 || DistortionPeriodTicks != 6000))
                throw new ArgumentException("Fade and animation parameters require the clouds visual slot.");
            if (kind == CelestialVisualKind.Surface && ShellHeightMeters != 0)
                throw new ArgumentException("Shell height requires a cloud or atmosphere visual slot.");
            if (kind == CelestialVisualKind.Surface && Alpha != 1)
                throw new ArgumentException("Version two surface visuals use the opaque base sphere.");
            if (BackgroundBiomeId != null || kind != CelestialVisualKind.Clouds && TexturePath != null)
                throw new ArgumentException("Version two textures require the clouds visual slot; background biomes retain v1.");
        }
    }
    public sealed class VisualResolvedProperties : ICelestialResolvedProperties
    {
        public string Key { get; }
        public CelestialVisualKind Kind { get; }
        public string HandlerId => HandlerFor(Kind);
        public int Version => Profile.Appearance == null ? 1 : 2;
        public CelestialVisualProfile Profile { get; }
        public static string HandlerFor(CelestialVisualKind kind) => "PFL.Visual." + kind;
        public static string CapabilityFor(CelestialVisualKind kind) => "visual." + kind.ToString().ToLowerInvariant();
        public VisualResolvedProperties(string key, CelestialVisualKind kind, CelestialVisualProfile profile)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            Key = key; Kind = kind; Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            profile.ValidateVisualKind(kind);
        }
        public ReadOnlyMemory<byte> Serialize() => JsonSerializer.SerializeToUtf8Bytes(Profile);
        public static CelestialResult<VisualResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload, CelestialVisualKind kind)
        {
            if (version != 1 && version != 2) return CelestialResult.Reject<VisualResolvedProperties>(new CelestialError("UnsupportedConfigurationVersion", CompKey: key));
            try
            {
                var profile = CelestialResolvedJson.Read<CelestialVisualProfile>(payload);
                if ((profile.Appearance == null ? 1 : 2) != version) throw new ArgumentException("Visual configuration version does not match appearance.");
                return CelestialResult.Ok(new VisualResolvedProperties(key, kind, profile));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            { return CelestialResult.Reject<VisualResolvedProperties>(new CelestialError("InvalidConfigurationPayload", CompKey: key, Detail: exception.Message)); }
        }
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
