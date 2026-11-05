using System;
using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialCompRegistration
    {
        public string HandlerId { get; }
        public int ConfigurationVersion { get; }
        public int SchemaVersion { get; }
        public ICelestialCompFactory Factory { get; }
        public ICelestialCompStateHandler Codec { get; }
        public CelestialCompRegistration(string handlerId, int configurationVersion, int schemaVersion,
            ICelestialCompFactory factory, ICelestialCompStateHandler codec)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(handlerId);
            ArgumentOutOfRangeException.ThrowIfLessThan(configurationVersion, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
            HandlerId = handlerId;
            ConfigurationVersion = configurationVersion;
            SchemaVersion = schemaVersion;
            Factory = factory ?? throw new ArgumentNullException(nameof(factory));
            Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }
    }

    public sealed class CelestialCompRegistry
    {
        private readonly object gate = new();
        private readonly Dictionary<string, CelestialCompRegistration> handlers = new(StringComparer.Ordinal);
        private bool isSealed;
        public bool IsSealed { get { lock (gate) return isSealed; } }
        public CelestialResult<bool> Register(CelestialCompRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            lock (gate)
            {
                if (isSealed) return CelestialResult.Reject<bool>(new CelestialError("RegistrySealed"));
                return handlers.TryAdd(registration.HandlerId, registration)
                    ? CelestialResult.Ok(true)
                    : CelestialResult.Reject<bool>(new CelestialError("DuplicateHandler", Detail: registration.HandlerId));
            }
        }
        public void Seal() { lock (gate) isSealed = true; }
        public bool TryGet(string handlerId, out CelestialCompRegistration registration)
        {
            lock (gate) return handlers.TryGetValue(handlerId, out registration!);
        }
    }
}
