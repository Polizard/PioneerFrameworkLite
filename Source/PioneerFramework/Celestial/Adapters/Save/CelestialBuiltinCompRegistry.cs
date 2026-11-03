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
            Register<SphericalBodyResolvedProperties>(registry, SphericalBodyResolvedProperties.Handler, SphericalBodyResolvedProperties.Deserialize,
                p => new CompProperties_SphericalBody { key = p.Key }.CreateComp());
            Register<SurfaceResolvedProperties>(registry, SurfaceResolvedProperties.Handler, SurfaceResolvedProperties.Deserialize,
                p => new CompProperties_Surface { key = p.Key }.CreateComp());
            Register<BiomeGenerationResolvedProperties>(registry, BiomeGenerationResolvedProperties.Handler, BiomeGenerationResolvedProperties.Deserialize,
                p => new CompProperties_BiomeGeneration { key = p.Key }.CreateComp(), 2);
            Register<MapGenerationResolvedProperties>(registry, MapGenerationResolvedProperties.Handler, MapGenerationResolvedProperties.Deserialize,
                p => new CompProperties_MapGeneration { key = p.Key }.CreateComp(), 2);
            Register<ExternalPhysicsResolvedProperties>(registry, ExternalPhysicsResolvedProperties.Handler, ExternalPhysicsResolvedProperties.Deserialize,
                p => new CompProperties_ExternalPhysicsBinding { key = p.Key }.CreateComp());
            Register<AtmosphereResolvedProperties>(registry, AtmosphereResolvedProperties.Handler, AtmosphereResolvedProperties.Deserialize,
                p => new CompProperties_Atmosphere { key = p.Key, pressurePascals = p.Version == 2 ? new() { value = 0 } : null }.CreateComp(), 2);
            Register<LightSourceResolvedProperties>(registry, LightSourceResolvedProperties.Handler, LightSourceResolvedProperties.Deserialize,
                p => new CompProperties_LightSource { key = p.Key }.CreateComp());
            Register<ExistingEnvironmentResolvedProperties>(registry, ExistingEnvironmentResolvedProperties.Handler, ExistingEnvironmentResolvedProperties.Deserialize,
                p => p.Version == 2 ? new CompPlanetEnvironment(new CompProperties_ExistingEnvironment { key = p.Key }.Descriptor) :
                    new CompProperties_ExistingEnvironment { key = p.Key }.CreateComp(), 2);
            foreach (var kind in Enum.GetValues<CelestialVisualKind>())
                Register<VisualResolvedProperties>(registry, VisualResolvedProperties.HandlerFor(kind),
                    (key, version, payload) => VisualResolvedProperties.Deserialize(key, version, payload, kind),
                    properties => kind switch {
                        CelestialVisualKind.Surface => new CompProperties_CelestialVisual { key = properties.Key }.CreateComp(),
                        CelestialVisualKind.Atmosphere => new CompProperties_AtmosphereVisual { key = properties.Key }.CreateComp(),
                        CelestialVisualKind.Clouds => new CompProperties_CloudsVisual { key = properties.Key }.CreateComp(),
                        CelestialVisualKind.Stellar => new CompProperties_StellarVisual { key = properties.Key }.CreateComp(),
                        _ => throw new ArgumentOutOfRangeException(nameof(kind)) }, 2);
            return registry;
        }
        private static void Register<T>(CelestialCompRegistry registry, string handler, Func<string, int, ReadOnlyMemory<byte>, CelestialResult<T>> decode,
            Func<T, CelestialBodyComp> create, int configurationVersion = 1) where T : ICelestialResolvedProperties
        {
            var adapter = new StatelessHandler<T>(decode, create);
            var result = registry.Register(new(handler, configurationVersion, 1, adapter, adapter));
            if (!result.Success) throw new InvalidOperationException(result.Errors[0].Code);
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
