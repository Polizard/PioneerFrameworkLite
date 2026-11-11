using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.MapGeneration
{
    public static class MapGenerationCompiler
    {
        public static MapGenerationCompilationResult Compile(LocalMapGenerationSpec spec, MapGenerationPurpose purpose,
            MapGenerationRegistry registry, IEnumerable<MapGenerationStepSpec>? additionalNativeSteps = null,
            IEnumerable<MapGenerationPortContract>? externalInputs = null)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(registry);
            if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
            var diagnostics = new List<MapGenerationDiagnostic>();
            void Error(string code, string? key = null, string? detail = null) => diagnostics.Add(new(code, key, detail));
            var steps = spec.Recipe.Steps.Concat(spec.PurposeRecipes.TryGetValue(purpose, out var recipe)
                ? recipe.Steps : Array.Empty<MapGenerationStepSpec>()).Concat(additionalNativeSteps ?? Array.Empty<MapGenerationStepSpec>()).ToArray();
            var external = (externalInputs ?? Array.Empty<MapGenerationPortContract>()).ToArray();
            var nodes = new Dictionary<string, MapGenerationStepSpec>(StringComparer.Ordinal);
            foreach (var step in steps)
            {
                if (step == null) { Error("NullStep"); continue; }
                if (!nodes.TryAdd(step.Key, step)) Error("DuplicateStepKey", step.Key);
                if (step.IsOpaque) diagnostics.Add(new("OpaqueNativeStep", step.Key, "Only native order and random scope isolation are guaranteed.", true));
            }
            var registrations = new Dictionary<string, GenerationModuleRegistration>(StringComparer.Ordinal);
            var runtimeTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
            void CheckTypes(IEnumerable<MapGenerationPortContract> ports, string? key)
            {
                foreach (var port in ports)
                {
                    if (runtimeTypes.TryGetValue(port.TypeId, out var type) && type != port.RuntimeType)
                        Error("PortRuntimeTypeMismatch", key, port.TypeId);
                    else runtimeTypes.TryAdd(port.TypeId, port.RuntimeType);
                }
            }
            bool Matches(IReadOnlyList<MapGenerationPort> ports, IReadOnlyList<MapGenerationPortContract> contracts)
                => ports.Count == contracts.Count && ports.All(port => contracts.Any(contract => contract.Name == port.Name && contract.TypeId == port.TypeId));
            foreach (var step in nodes.Values)
            {
                if (!registry.TryResolve(step.HandlerId, step.ConfigurationVersion, out var registration) || registration == null)
                { Error("UnknownHandlerVersion", step.Key, step.HandlerId); continue; }
                registrations.Add(step.Key, registration);
                if (!registration.AlgorithmVersions.Contains(step.AlgorithmVersion)) Error("UnsupportedAlgorithmVersion", step.Key);
                if (!Matches(step.Inputs, registration.Inputs) || !Matches(step.Outputs, registration.Outputs)) Error("PortContractMismatch", step.Key);
                CheckTypes(registration.Inputs.Concat(registration.Outputs), step.Key);
                try { registration.ValidateConfiguration(step.Configuration); }
                catch (Exception exception) { Error("InvalidConfiguration", step.Key, exception.ToString()); }
            }
            CheckTypes(external.Where(port => port != null), null);
            var edges = nodes.Keys.ToDictionary(key => key, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            void AddEdge(string from, string to, bool native = false)
            {
                if (nodes[from].Stage > nodes[to].Stage)
                    Error(native ? "NativeOrderConflict" : "ReverseStageDependency", to, from);
                edges[from].Add(to);
            }
            foreach (var step in nodes.Values)
            {
                foreach (var key in step.Before)
                    if (!nodes.ContainsKey(key)) Error("UnknownOrderingKey", step.Key, key); else AddEdge(step.Key, key);
                foreach (var key in step.After)
                    if (!nodes.ContainsKey(key)) Error("UnknownOrderingKey", step.Key, key); else AddEdge(key, step.Key);
            }
            // Phases are unconditional barriers. Explicit edges may strengthen their ordering only.
            foreach (var from in nodes.Values)
                foreach (var to in nodes.Values)
                    if (from.Stage < to.Stage) edges[from.Key].Add(to.Key);
            var producers = new Dictionary<string, (string TypeId, string? StepKey)>(StringComparer.Ordinal);
            foreach (var port in external)
            {
                if (port == null) { Error("InvalidExternalInput"); continue; }
                if (!producers.TryAdd(port.Name, (port.TypeId, null))) Error("DuplicateOutputProducer", detail: port.Name);
            }
            foreach (var step in nodes.Values)
                foreach (var port in step.Outputs)
                    if (!producers.TryAdd(port.Key, (port.TypeId, step.Key))) Error("DuplicateOutputProducer", step.Key, port.Key);
            foreach (var step in nodes.Values)
                foreach (var port in step.Inputs)
                {
                    if (!producers.TryGetValue(port.Key, out var producer)) { Error("MissingInput", step.Key, port.Key); continue; }
                    if (producer.TypeId != port.TypeId) Error("InputTypeMismatch", step.Key, port.Key);
                    if (producer.StepKey != null) AddEdge(producer.StepKey, step.Key);
                }
            bool HasPath(string from, string to)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Stack<string>(); pending.Push(from);
                while (pending.Count > 0)
                {
                    string current = pending.Pop();
                    if (current == to) return true;
                    if (!visited.Add(current)) continue;
                    foreach (string next in edges[current]) pending.Push(next);
                }
                return false;
            }
            var nativeSteps = nodes.Values.Where(step => step.NativeOrder.HasValue).OrderBy(step => step.NativeOrder).ThenBy(step => step.Key, StringComparer.Ordinal).ToArray();
            for (int i = 1; i < nativeSteps.Length; i++)
            {
                var from = nativeSteps[i - 1]; var to = nativeSteps[i];
                if (from.NativeOrder == to.NativeOrder) Error("DuplicateNativeOrder", to.Key);
                if (HasPath(to.Key, from.Key)) Error("NativeOrderConflict", to.Key, from.Key);
                AddEdge(from.Key, to.Key, true);
            }
            var degrees = nodes.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
            foreach (var targets in edges.Values) foreach (string key in targets) degrees[key]++;
            var ready = new SortedSet<string>(degrees.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
            var order = new List<string>();
            while (ready.Count > 0)
            {
                string key = ready.Min!; ready.Remove(key); order.Add(key);
                foreach (string next in edges[key]) if (--degrees[next] == 0) ready.Add(next);
            }
            if (order.Count != nodes.Count) Error("DependencyCycle");
            if (diagnostics.Any(diagnostic => !diagnostic.IsWarning)) return new(null, diagnostics);
            return new(new MapGenerationPlan(spec, purpose, order.Select(key => new CompiledMapGenerationStep(nodes[key], registrations[key])), external, diagnostics), diagnostics);
        }
    }
}
