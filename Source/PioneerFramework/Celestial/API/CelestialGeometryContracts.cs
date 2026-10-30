using System;
using System.Buffers.Binary;

namespace PioneerFramework.Celestial
{
    public sealed record CelestialGeometryDescription(string Kind, double ReferenceRadiusMeters, string FixedFrameId);
    public interface ICelestialGeometryCapability
    {
        CelestialGeometryDescription GetDescription();
        DVector3 FixedPosition(double latitudeRadians, double longitudeRadians);
    }
    public sealed class SphericalBodyResolvedProperties : ICelestialGeometryProperties
    {
        public const string Handler = "PFL.Sphere";
        public string Key { get; }
        public string HandlerId => Handler;
        public int Version => 1;
        public double RadiusMeters { get; }
        public SphericalBodyResolvedProperties(string key, double radiusMeters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (!double.IsFinite(radiusMeters) || radiusMeters <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMeters));
            Key = key;
            RadiusMeters = radiusMeters;
        }
        public DVector3 FixedPosition(double latitudeRadians, double longitudeRadians)
        {
            if (!double.IsFinite(latitudeRadians) || Math.Abs(latitudeRadians) > Math.PI / 2) throw new ArgumentOutOfRangeException(nameof(latitudeRadians));
            if (!double.IsFinite(longitudeRadians)) throw new ArgumentOutOfRangeException(nameof(longitudeRadians));
            return SurfaceFrame.SurfaceState(RadiusMeters, latitudeRadians, longitudeRadians, 0, 0).pos;
        }
        public ReadOnlyMemory<byte> Serialize()
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(bytes, RadiusMeters);
            return bytes;
        }
        public static CelestialResult<SphericalBodyResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload)
        {
            if (version != 1) return CelestialResult.Reject<SphericalBodyResolvedProperties>(new CelestialError("UnsupportedConfigurationVersion", CompKey: key));
            if (payload.Length != 8) return CelestialResult.Reject<SphericalBodyResolvedProperties>(new CelestialError("InvalidConfigurationPayload", CompKey: key));
            try { return CelestialResult.Ok(new SphericalBodyResolvedProperties(key, BinaryPrimitives.ReadDoubleLittleEndian(payload.Span))); }
            catch (ArgumentException exception) { return CelestialResult.Reject<SphericalBodyResolvedProperties>(new CelestialError("InvalidGeometry", CompKey: key, Detail: exception.Message)); }
        }
    }
}
