using System;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public static class CelestialBuiltinCompRegistry
    {
        public static CelestialCompRegistry Global { get; } = Create();
        public static CelestialCompRegistry Create()
        {
            var registry = new CelestialCompRegistry();
            // handler 集与最高配置版本只从 CelestialBuiltinComps 表派生；decode/factory 仍由 HandlerFor 的 switch 提供。
            foreach (var row in CelestialBuiltinComps.All)
            {
                var (factory, codec) = HandlerFor(row.Handler);
                var result = registry.Register(new(row.Handler, row.ConfigurationVersion, 1, factory, codec));
                if (!result.Success) throw new InvalidOperationException(result.Errors[0].Code);
            }
            return registry;
        }

        /// <summary>表行对应的 decode/factory；缺覆盖必须显式抛错，不允许静默变成读档 MissingHandler。</summary>
        internal static (ICelestialCompFactory Factory, ICelestialCompStateHandler Codec) HandlerFor(string handler)
        {
            switch (handler)
            {
                case SphericalBodyResolvedProperties.Handler:
                    return Stateless<SphericalBodyResolvedProperties>(SphericalBodyResolvedProperties.Deserialize, p => new CompProperties_SphericalBody { key = p.Key }.CreateComp());
                case SurfaceResolvedProperties.Handler:
                    return Stateless<SurfaceResolvedProperties>(SurfaceResolvedProperties.Deserialize, p => new CompProperties_Surface { key = p.Key }.CreateComp());
                case BiomeGenerationResolvedProperties.Handler:
                    return Stateless<BiomeGenerationResolvedProperties>(BiomeGenerationResolvedProperties.Deserialize, p => new CompProperties_BiomeGeneration { key = p.Key }.CreateComp());
                case MapGenerationResolvedProperties.Handler:
                    return Stateless<MapGenerationResolvedProperties>(MapGenerationResolvedProperties.Deserialize, p => new CompProperties_MapGeneration { key = p.Key }.CreateComp());
                case ExternalPhysicsResolvedProperties.Handler:
                    return Stateless<ExternalPhysicsResolvedProperties>(ExternalPhysicsResolvedProperties.Deserialize, p => new CompProperties_ExternalPhysicsBinding { key = p.Key }.CreateComp());
                case AtmosphereResolvedProperties.Handler:
                    return Stateless<AtmosphereResolvedProperties>(AtmosphereResolvedProperties.Deserialize,
                        p => new CompProperties_Atmosphere { key = p.Key, pressurePascals = p.Version == 2 ? new() { value = 0 } : null }.CreateComp());
                case LightSourceResolvedProperties.Handler:
                    return Stateless<LightSourceResolvedProperties>(LightSourceResolvedProperties.Deserialize, p => new CompProperties_LightSource { key = p.Key }.CreateComp());
                case ExistingEnvironmentResolvedProperties.Handler:
                    return Stateless<ExistingEnvironmentResolvedProperties>(ExistingEnvironmentResolvedProperties.Deserialize, p => p.Version == 2 ? new CompPlanetEnvironment(new CompProperties_ExistingEnvironment { key = p.Key }.Descriptor) :
                        new CompProperties_ExistingEnvironment { key = p.Key }.CreateComp());
                default:
                    foreach (var kind in Enum.GetValues<CelestialVisualKind>())
                        if (handler == VisualResolvedProperties.HandlerFor(kind))
                            return Stateless<VisualResolvedProperties>((key, version, payload) => VisualResolvedProperties.Deserialize(key, version, payload, kind),
                                properties => kind switch {
                                    CelestialVisualKind.Surface => new CompProperties_CelestialVisual { key = properties.Key }.CreateComp(),
                                    CelestialVisualKind.Atmosphere => new CompProperties_AtmosphereVisual { key = properties.Key }.CreateComp(),
                                    CelestialVisualKind.Clouds => new CompProperties_CloudsVisual { key = properties.Key }.CreateComp(),
                                    CelestialVisualKind.Stellar => new CompProperties_StellarVisual { key = properties.Key }.CreateComp(),
                                    _ => throw new ArgumentOutOfRangeException(nameof(kind)) });
                    throw new InvalidOperationException("MissingBuiltinHandlerCoverage: " + handler);
            }
        }

        private static (ICelestialCompFactory Factory, ICelestialCompStateHandler Codec) Stateless<T>(
            Func<string, int, ReadOnlyMemory<byte>, CelestialResult<T>> decode, Func<T, CelestialBodyComp> create) where T : ICelestialResolvedProperties
        {
            var adapter = new StatelessHandler<T>(decode, create);
            return (adapter, adapter);
        }

        private sealed class StatelessHandler<T>(Func<string, int, ReadOnlyMemory<byte>, CelestialResult<T>> decode, Func<T, CelestialBodyComp> create) :
            ICelestialCompFactory, ICelestialCompStateHandler where T : ICelestialResolvedProperties
        {
            public CelestialBodyComp Create(ICelestialResolvedProperties properties) => create((T)properties);
            public CelestialResult<ICelestialResolvedProperties> DecodeProperties(string key, int version, ReadOnlyMemory<byte> payload)
            {
                var result = decode(key, version, payload);
                return result.Success ? CelestialResult.Ok<ICelestialResolvedProperties>(result.Value) : CelestialResult.Reject<ICelestialResolvedProperties>(result.Errors.ToArray());
            }
            public CelestialResult<bool> RestoreState(CelestialBodyComp component, int version, ReadOnlyMemory<byte> payload) => version == 1 && payload.IsEmpty ?
                CelestialResult.Ok(true) : CelestialResult.Reject<bool>(new CelestialError("UnsupportedStatePayload", CompKey: component.Descriptor.Key));
        }
    }
}
