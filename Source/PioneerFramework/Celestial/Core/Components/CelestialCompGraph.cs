using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public static class CelestialCompGraph
    {
        internal static CelestialResult<IReadOnlyList<CelestialCompDescriptor>> ResolveActive(IReadOnlyList<CelestialCompDescriptor> components, IReadOnlySet<string> disabled)
            => disabled.Count == 0 ? Resolve(components) : Resolve(components.Where(d => !disabled.Contains(d.Key)).Select(d => new CelestialCompDescriptor(d.Key, d.HandlerId, d.SchemaVersion,
                d.Provides, d.Requires, d.ExclusiveSlot, d.Before.Where(key => !disabled.Contains(key)), d.After.Where(key => !disabled.Contains(key)),
                d.UpdatePolicy, d.FailurePolicy)).ToArray());
        public static CelestialResult<IReadOnlyList<CelestialCompDescriptor>> Resolve(IReadOnlyList<CelestialCompDescriptor> components)
        {
            ArgumentNullException.ThrowIfNull(components);
            var nodes = new Dictionary<string, CelestialCompDescriptor>(StringComparer.Ordinal);
            var slots = new HashSet<string>(StringComparer.Ordinal);
            var providers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var component in components)
            {
                if (component == null) return Reject("InvalidComponent");
                if (!nodes.TryAdd(component.Key, component)) return Reject("DuplicateCompKey", component.Key);
                if (component.ExclusiveSlot != null && !slots.Add(component.ExclusiveSlot))
                    return Reject("ExclusiveCapabilityConflict", component.Key);
                foreach (string capability in component.Provides)
                {
                    if (!providers.TryGetValue(capability, out var keys)) providers.Add(capability, keys = new List<string>());
                    keys.Add(component.Key);
                }
            }
            var outgoing = nodes.Keys.ToDictionary(key => key, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            var degrees = nodes.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
            void Edge(string from, string to)
            {
                if (outgoing[from].Add(to)) degrees[to]++;
            }
            foreach (var component in nodes.Values)
            {
                foreach (string required in component.Requires)
                {
                    if (!providers.TryGetValue(required, out var keys)) return Reject("MissingCapability", component.Key, required);
                    foreach (string provider in keys) Edge(provider, component.Key);
                }
                foreach (string target in component.Before)
                {
                    if (!nodes.ContainsKey(target)) return Reject("MissingCompOrderTarget", component.Key, target);
                    Edge(component.Key, target);
                }
                foreach (string target in component.After)
                {
                    if (!nodes.ContainsKey(target)) return Reject("MissingCompOrderTarget", component.Key, target);
                    Edge(target, component.Key);
                }
            }
            var ready = new SortedSet<string>(degrees.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
            var ordered = new List<CelestialCompDescriptor>();
            while (ready.Count > 0)
            {
                string key = ready.Min!;
                ready.Remove(key);
                ordered.Add(nodes[key]);
                foreach (string target in outgoing[key])
                    if (--degrees[target] == 0) ready.Add(target);
            }
            return ordered.Count == nodes.Count
                ? CelestialResult.Ok<IReadOnlyList<CelestialCompDescriptor>>(ordered.AsReadOnly())
                : Reject("CompDependencyCycle");
        }

        private static CelestialResult<IReadOnlyList<CelestialCompDescriptor>> Reject(string code, string? key = null, string detail = "")
            => CelestialResult.Reject<IReadOnlyList<CelestialCompDescriptor>>(new CelestialError(code, CompKey: key, Detail: detail));
    }
}
