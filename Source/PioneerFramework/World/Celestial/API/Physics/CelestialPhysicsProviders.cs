using System;
using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    public sealed record CelestialPhysicsWorldRequest(Guid WorldSessionId, ulong WorldSeed, CelestialSpawnManifest Manifest, double InitialSeconds = 0);
    public sealed record CelestialPhysicsConnection(ICelestialPhysicsBridge Bridge, PhysicsSampleStamp InitialStamp);
    /// <summary>Mod 初始化时登记工厂；每个世界获得独立提供者及会话。框架不提供运动算法。</summary>
    public sealed class CelestialPhysicsProviderRegistry
    {
        private readonly object gate = new();
        private readonly Dictionary<string, Func<CelestialPhysicsWorldRequest, CelestialResult<CelestialPhysicsConnection>>> factories = new(StringComparer.Ordinal);
        private bool sealedRegistry;
        public static CelestialPhysicsProviderRegistry Global { get; } = new();
        public CelestialResult<bool> Register(string providerId, Func<CelestialPhysicsWorldRequest, CelestialResult<CelestialPhysicsConnection>> factory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
            ArgumentNullException.ThrowIfNull(factory);
            lock (gate)
            {
                if (sealedRegistry) return CelestialResult.Reject<bool>(new CelestialError("RegistrySealed"));
                return factories.TryAdd(providerId, factory) ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(new CelestialError("DuplicateProvider"));
            }
        }
        public CelestialResult<CelestialPhysicsConnection> Connect(string providerId, CelestialPhysicsWorldRequest request)
        {
            Func<CelestialPhysicsWorldRequest, CelestialResult<CelestialPhysicsConnection>> factory;
            lock (gate)
            {
                if (!factories.TryGetValue(providerId, out factory!)) return CelestialResult.Reject<CelestialPhysicsConnection>(new CelestialError("ProviderUnavailable", Detail: providerId));
                sealedRegistry = true;
            }
            try
            {
                var result = factory(request);
                if (!result.Success) return result;
                if (result.Value.Bridge.Status != PhysicsBridgeStatus.Ready || result.Value.Bridge.ContractVersion != PhysicsContractVersion.Current ||
                    result.Value.Bridge.ProviderId != providerId || result.Value.InitialStamp.WorldSessionId != request.WorldSessionId ||
                    result.Value.InitialStamp.PhysicalSeconds != request.InitialSeconds || result.Value.InitialStamp.TopologyRevision != 0)
                    return CelestialResult.Reject<CelestialPhysicsConnection>(new CelestialError("ProviderContractViolation"));
                return result;
            }
            catch (Exception exception) { return CelestialResult.Reject<CelestialPhysicsConnection>(new CelestialError("ProviderInitializationFailed", Detail: exception.Message)); }
        }
    }
}
