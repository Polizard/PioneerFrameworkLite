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
        public CelestialResult<bool> Register<T>(string name) where T : class => Register(name, typeof(T));
        internal CelestialResult<bool> Register(string name, Type contract)
        {
            if (contract == null || !contract.IsInterface || string.IsNullOrWhiteSpace(name))
                return CelestialResult.Reject<bool>(new CelestialError("InvalidCapabilityContract"));
            lock (gate)
            {
                if (sealedRegistry) return CelestialResult.Reject<bool>(new CelestialError("RegistrySealed"));
                return contracts.TryAdd(name, contract) ? CelestialResult.Ok(true) :
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
        internal bool TryGetContract(string name, out Type contract)
        {
            lock (gate) return contracts.TryGetValue(name, out contract!);
        }
        internal int ContractCount { get { lock (gate) return contracts.Count; } }
        // 内置能力名与契约类型只从 CelestialBuiltinComps 表派生，注册顺序与旧手写列表逐项一致。
        private static CelestialCapabilityRegistry CreateBuiltins()
        {
            var result = new CelestialCapabilityRegistry();
            foreach (var row in CelestialBuiltinComps.All)
                foreach (var (name, contract) in row.Capabilities)
                    result.Register(name, contract);
            return result;
        }
    }
}
