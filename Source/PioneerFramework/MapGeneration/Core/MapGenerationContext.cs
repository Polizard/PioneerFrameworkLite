using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.MapGeneration
{
    public sealed class MapGenerationContext
    {
        private readonly CompiledMapGenerationStep compiledStep;
        private readonly Dictionary<string, object> data;
        private readonly IServiceProvider? services;
        public MapSourceContext Source { get; }
        public MapGenerationPurpose Purpose { get; }
        public MapGenerationStepSpec Step { get; }
        public ulong Seed { get; }
        public ReadOnlyMemory<byte> Parameters => Step.Configuration;
        internal MapGenerationContext(MapSourceContext source, MapGenerationPurpose purpose, CompiledMapGenerationStep step,
            ulong seed, Dictionary<string, object> data, IServiceProvider? services)
        {
            Source = source; Purpose = purpose; Step = step.Spec; Seed = seed;
            compiledStep = step; this.data = data; this.services = services;
        }
        public void Publish<T>(string name, T value)
        {
            var binding = Step.Outputs.SingleOrDefault(port => port.Name == name)
                ?? throw new InvalidOperationException("Undeclared output port: " + name);
            var contract = compiledStep.Registration.Outputs.Single(port => port.Name == name);
            if (typeof(T) != contract.RuntimeType || value == null || !contract.RuntimeType.IsInstanceOfType(value))
                throw new InvalidOperationException("Output runtime type mismatch: " + name);
            if (!data.TryAdd(binding.Key, value)) throw new InvalidOperationException("Resource already published: " + binding.Key);
        }
        public T Read<T>(string name)
        {
            var binding = Step.Inputs.SingleOrDefault(port => port.Name == name)
                ?? throw new InvalidOperationException("Undeclared input port: " + name);
            var contract = compiledStep.Registration.Inputs.Single(port => port.Name == name);
            if (typeof(T) != contract.RuntimeType) throw new InvalidOperationException("Input runtime type mismatch: " + name);
            if (!data.TryGetValue(binding.Key, out var value) || value is not T typed)
                throw new InvalidOperationException("Input resource is unavailable: " + binding.Key);
            return typed;
        }
        public T? GetService<T>() where T : class
        {
            object? value = services?.GetService(typeof(T));
            if (value == null) return null;
            return value as T ?? throw new InvalidOperationException("Service provider returned an incompatible type.");
        }
        public T RequireService<T>() where T : class
            => GetService<T>() ?? throw new InvalidOperationException("Required generation service is unavailable: " + typeof(T).FullName);
        internal void ValidateOutputs()
        {
            foreach (var port in Step.Outputs)
                if (!data.ContainsKey(port.Key)) throw new InvalidOperationException("Declared output was not published: " + port.Name);
        }
    }
}
