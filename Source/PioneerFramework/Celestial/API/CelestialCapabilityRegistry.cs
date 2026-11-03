using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>Explicit typed contracts; unregistered names remain graph-only markers.</summary>
    public sealed class CelestialCapabilityRegistry
    {
        private readonly Dictionary<string, Type> contracts = new(StringComparer.Ordinal);
        private readonly object gate = new();
        private bool sealedRegistry;
        public static CelestialCapabilityRegistry Global { get; } = CreateBuiltins();
        public CelestialResult<bool> Register<T>(string name) where T : class
        {
            if (!typeof(T).IsInterface || string.IsNullOrWhiteSpace(name)) return CelestialResult.Reject<bool>(new CelestialError("InvalidCapabilityContract"));
            lock (gate)
            {
                if (sealedRegistry) return CelestialResult.Reject<bool>(new CelestialError("RegistrySealed"));
                return contracts.TryAdd(name, typeof(T)) ? CelestialResult.Ok(true) :
                    CelestialResult.Reject<bool>(new CelestialError("DuplicateCapabilityContract", Detail: name));
            }
        }
        public void Seal() { lock (gate) sealedRegistry = true; }
        public CelestialResult<bool> Validate(CelestialBodyComp component)
        {
            lock (gate)
            {
                var errors = component.Descriptor.Provides.Where(name => contracts.TryGetValue(name, out var type) && !type.IsInstanceOfType(component))
                    .Select(name => new CelestialError("CapabilityContractViolation", CompKey: component.Descriptor.Key, Detail: name)).ToArray();
                return errors.Length == 0 ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(errors);
            }
        }
        private static CelestialCapabilityRegistry CreateBuiltins()
        {
            var result = new CelestialCapabilityRegistry();
            result.Register<ICelestialGeometryCapability>("geometry");
            result.Register<ICelestialSurfaceCapability>("surface");
            result.Register<ICelestialPhysicsBinding>("physics.binding");
            result.Register<ICelestialAtmosphereCapability>("atmosphere");
            result.Register<ICelestialAtmosphereCompositionCapability>("atmosphere.composition");
            result.Register<ICelestialLightSourceCapability>("light.source");
            result.Register<ICelestialEnvironmentCapability>("environment.existing");
            result.Register<ICelestialMapGenerationCapability>("map.generation");
            result.Register<ICelestialBiomeGenerationCapability>("biomes");
            foreach (string name in new[] { "visual.surface", "visual.atmosphere", "visual.clouds", "visual.stellar" })
                result.Register<ICelestialVisualCapability>(name);
            return result;
        }
    }
}
