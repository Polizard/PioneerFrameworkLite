using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PioneerFramework.MapGeneration
{
    /// <summary>One handler configuration schema with explicit supported algorithm versions and typed logical ports.</summary>
    public sealed class GenerationModuleRegistration
    {
        public string HandlerId { get; }
        public int ConfigurationVersion { get; }
        public IReadOnlyList<int> AlgorithmVersions { get; }
        public IReadOnlyList<MapGenerationPortContract> Inputs { get; }
        public IReadOnlyList<MapGenerationPortContract> Outputs { get; }
        private readonly Action<ReadOnlyMemory<byte>> validateConfiguration;
        private readonly Func<ReadOnlyMemory<byte>, IMapGenerationModule> factory;

        public GenerationModuleRegistration(string handlerId, int configurationVersion, IEnumerable<int> algorithmVersions,
            Action<ReadOnlyMemory<byte>> validateConfiguration, Func<ReadOnlyMemory<byte>, IMapGenerationModule> factory,
            IEnumerable<MapGenerationPortContract>? inputs = null, IEnumerable<MapGenerationPortContract>? outputs = null)
        {
            HandlerId = MapGenerationGuards.Identity(handlerId);
            ArgumentOutOfRangeException.ThrowIfLessThan(configurationVersion, 1);
            ArgumentNullException.ThrowIfNull(algorithmVersions);
            var algorithms = algorithmVersions.ToArray();
            if (algorithms.Length == 0 || algorithms.Any(version => version < 1) || algorithms.Distinct().Count() != algorithms.Length)
                throw new ArgumentException("Algorithm versions must be distinct positive integers.", nameof(algorithmVersions));
            ConfigurationVersion = configurationVersion;
            AlgorithmVersions = Array.AsReadOnly(algorithms);
            this.validateConfiguration = validateConfiguration ?? throw new ArgumentNullException(nameof(validateConfiguration));
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            Inputs = Freeze(inputs);
            Outputs = Freeze(outputs);
        }
        private static ReadOnlyCollection<MapGenerationPortContract> Freeze(IEnumerable<MapGenerationPortContract>? contracts)
        {
            var copy = (contracts ?? Array.Empty<MapGenerationPortContract>()).ToArray();
            if (copy.Any(port => port == null) || copy.Select(port => port.Name).Distinct(StringComparer.Ordinal).Count() != copy.Length)
                throw new ArgumentException("Null or duplicate logical port contract.");
            return Array.AsReadOnly(copy);
        }
        public void ValidateConfiguration(ReadOnlyMemory<byte> configuration) => validateConfiguration(configuration.ToArray());
        public IMapGenerationModule Create(ReadOnlyMemory<byte> configuration)
            => factory(configuration.ToArray()) ?? throw new InvalidOperationException("Module factory returned null.");
    }

    public sealed class MapGenerationRegistry
    {
        private readonly Dictionary<(string, int), GenerationModuleRegistration> registrations = new();
        public bool IsFrozen { get; private set; }
        public void Register(GenerationModuleRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (IsFrozen) throw new InvalidOperationException("The generation registry is frozen.");
            if (!registrations.TryAdd((registration.HandlerId, registration.ConfigurationVersion), registration))
                throw new ArgumentException("Duplicate handler/configuration version.", nameof(registration));
        }
        public void Freeze() => IsFrozen = true;
        public bool TryResolve(string handlerId, int configurationVersion, out GenerationModuleRegistration? registration)
            => registrations.TryGetValue((handlerId, configurationVersion), out registration);
    }
}
