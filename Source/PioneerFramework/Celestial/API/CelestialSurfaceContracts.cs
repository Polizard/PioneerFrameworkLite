using System;
using System.IO;
using System.Text;

namespace PioneerFramework.Celestial
{
    public enum CelestialSurfaceGenerationMode { OnDemand, Creation }
    public enum CelestialSurfaceState { Generating, Ready, Failed }
    public sealed record CelestialSurfaceRequest(CelestialOperationContext Context, CelestialInstanceId InstanceId);
    public sealed record CelestialSurfaceHandle(CelestialInstanceId OwnerId, int LayerId);
    public sealed record CelestialSurfaceOperation(CelestialOperation Operation, CelestialSurfaceHandle? Handle = null);
    public interface ICelestialSurfaceCapability
    {
        SurfaceResolvedProperties GetProperties();
    }
    public sealed class SurfaceResolvedProperties : ICelestialResolvedProperties
    {
        public const string Handler = "PFL.Surface";
        // 内置球面适配覆盖原版 WorldGrid.SubdivisionsCount 的 0..10 档，先校验再分配原生数组。
        public const int MaxBuiltinSubdivisions = 10;
        public string Key { get; }
        public string HandlerId => Handler;
        public int Version => 1;
        public CelestialSurfaceGenerationMode GenerationMode { get; }
        public int Subdivisions { get; }
        public double MetersPerLayerUnit { get; }
        public SurfaceResolvedProperties(string key, CelestialSurfaceGenerationMode generationMode, int subdivisions, double metersPerLayerUnit)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentOutOfRangeException.ThrowIfNegative(subdivisions);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(subdivisions, MaxBuiltinSubdivisions);
            if (!Enum.IsDefined(generationMode)) throw new ArgumentOutOfRangeException(nameof(generationMode));
            if (!double.IsFinite(metersPerLayerUnit) || metersPerLayerUnit <= 0) throw new ArgumentOutOfRangeException(nameof(metersPerLayerUnit));
            Key = key;
            GenerationMode = generationMode;
            Subdivisions = subdivisions;
            MetersPerLayerUnit = metersPerLayerUnit;
        }
        public ReadOnlyMemory<byte> Serialize()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write((int)GenerationMode);
            writer.Write(Subdivisions);
            writer.Write(MetersPerLayerUnit);
            writer.Flush();
            return stream.ToArray();
        }
        public static CelestialResult<SurfaceResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload)
        {
            if (version != 1) return CelestialResult.Reject<SurfaceResolvedProperties>(new CelestialError("UnsupportedConfigurationVersion", CompKey: key));
            try
            {
                using var stream = new MemoryStream(payload.ToArray(), false);
                using var reader = new BinaryReader(stream, Encoding.UTF8);
                var properties = new SurfaceResolvedProperties(key, (CelestialSurfaceGenerationMode)reader.ReadInt32(), reader.ReadInt32(), reader.ReadDouble());
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing surface configuration bytes.");
                return CelestialResult.Ok(properties);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException)
            { return CelestialResult.Reject<SurfaceResolvedProperties>(new CelestialError("InvalidConfigurationPayload", CompKey: key, Detail: exception.Message)); }
        }
    }
}
