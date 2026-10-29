using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PioneerFramework.Celestial
{
    public interface ICelestialPhysicsBindingProperties : ICelestialResolvedProperties
    {
        double? GravitationalParameter { get; }
        PhysicsInitialization Initialization { get; }
    }
    public interface ICelestialPhysicsBinding
    {
        CelestialResult<CelestialBodyRegistration> CreateRegistration(CelestialBodyDescription description, CelestialInstanceId? parentId,
            IReadOnlyDictionary<string, CelestialInstanceId> initializationReferences);
    }

    public sealed class ExternalPhysicsResolvedProperties : ICelestialPhysicsBindingProperties
    {
        public const string Handler = "PFL.ExternalPhysicsBinding";
        public string Key { get; }
        public string HandlerId => Handler;
        public int Version => 1;
        public double? GravitationalParameter { get; }
        public PhysicsInitialization Initialization { get; }
        public ExternalPhysicsResolvedProperties(string key, double? gravitationalParameter, PhysicsInitialization initialization)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (gravitationalParameter.HasValue && (!double.IsFinite(gravitationalParameter.Value) || gravitationalParameter.Value < 0))
                throw new ArgumentOutOfRangeException(nameof(gravitationalParameter));
            Key = key;
            GravitationalParameter = gravitationalParameter;
            Initialization = initialization ?? throw new ArgumentNullException(nameof(initialization));
        }
        public ReadOnlyMemory<byte> Serialize()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(GravitationalParameter.HasValue);
            if (GravitationalParameter.HasValue) writer.Write(GravitationalParameter.Value);
            writer.Write(Initialization.ProviderId);
            writer.Write(Initialization.ModeId);
            writer.Write(Initialization.Version);
            writer.Write(Initialization.Payload.Length);
            writer.Write(Initialization.Payload.Span);
            writer.Flush();
            return stream.ToArray();
        }
        public static CelestialResult<ExternalPhysicsResolvedProperties> Deserialize(string key, int version, ReadOnlyMemory<byte> payload)
        {
            if (version != 1) return CelestialResult.Reject<ExternalPhysicsResolvedProperties>(new CelestialError("UnsupportedConfigurationVersion", CompKey: key));
            try
            {
                using var stream = new MemoryStream(payload.ToArray(), false);
                using var reader = new BinaryReader(stream, Encoding.UTF8, true);
                double? gm = reader.ReadBoolean() ? reader.ReadDouble() : null;
                string provider = reader.ReadString();
                string mode = reader.ReadString();
                int initializationVersion = reader.ReadInt32();
                int length = reader.ReadInt32();
                if (length < 0 || stream.Length - stream.Position != length) throw new InvalidDataException("Invalid payload length.");
                var initialization = new PhysicsInitialization(provider, mode, initializationVersion, reader.ReadBytes(length));
                return CelestialResult.Ok(new ExternalPhysicsResolvedProperties(key, gm, initialization));
            }
            catch (Exception exception) when (exception is IOException or ArgumentException)
            { return CelestialResult.Reject<ExternalPhysicsResolvedProperties>(new CelestialError("InvalidConfigurationPayload", CompKey: key, Detail: exception.Message)); }
        }
    }
}
