using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialGenerationContext
    {
        public ulong WorldSeed { get; }
        public string GenerationPath { get; }
        public string CompKey { get; }
        private readonly Dictionary<(string Comp, string Parameter), CelestialParameterOverride>? overrides;
        private readonly HashSet<(string Comp, string Parameter)>? used;
        public CelestialGenerationContext(ulong worldSeed, string generationPath, string compKey = "", IReadOnlyList<CelestialParameterOverride>? parameterOverrides = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(generationPath);
            WorldSeed = worldSeed;
            GenerationPath = generationPath;
            CompKey = compKey;
            if (parameterOverrides?.Count > 0)
            {
                overrides = new(); used = new();
                foreach (var item in parameterOverrides)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.CompKey) || string.IsNullOrWhiteSpace(item.ParameterKey) ||
                        item.Value.HasValue && (item.Min.HasValue || item.Max.HasValue) || !item.Value.HasValue && (!item.Min.HasValue || !item.Max.HasValue))
                        throw new ArgumentException("InvalidParameterOverride");
                    double min = item.Value ?? item.Min!.Value, max = item.Value ?? item.Max!.Value;
                    if (!double.IsFinite(min) || !double.IsFinite(max) || min > max || !overrides.TryAdd((item.CompKey, item.ParameterKey), item))
                        throw new ArgumentException("InvalidParameterOverride");
                }
            }
        }
        private CelestialGenerationContext(CelestialGenerationContext parent, string key)
        { WorldSeed = parent.WorldSeed; GenerationPath = parent.GenerationPath; CompKey = key; overrides = parent.overrides; used = parent.used; resolved = parent.resolved; sampledDoubles = parent.sampledDoubles; }
        private readonly List<ICelestialResolvedProperties> resolved = new();
        private readonly Dictionary<(string Comp, string Parameter), DoubleParameterSpec> sampledDoubles = new();
        public IReadOnlyList<ICelestialResolvedProperties> ResolvedProperties => resolved.AsReadOnly();
        public T? GetResolved<T>() where T : class, ICelestialResolvedProperties => resolved.OfType<T>().SingleOrDefault();
        internal CelestialGenerationContext BeginResolution()
            => new(WorldSeed, GenerationPath, CompKey, overrides?.Values.ToArray());
        internal void AddResolved(ICelestialResolvedProperties properties) => resolved.Add(properties);
        public CelestialGenerationContext ForComp(string key) => new(this, key);
        internal CelestialResult<bool> ValidateOverrides()
        {
            var unused = overrides?.Keys.Where(key => !used!.Contains(key)).ToArray() ?? Array.Empty<(string Comp, string Parameter)>();
            return unused.Length == 0 ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(unused.Select(key =>
                new CelestialError("UnknownParameterOverride", CompKey: key.Comp, Detail: key.Parameter)).ToArray());
        }
        private CelestialParameterOverride? Override(string parameterKey)
        { if (overrides?.TryGetValue((CompKey, parameterKey), out var value) != true) return null; used!.Add((CompKey, parameterKey)); return value; }
        public ulong SeedFor(string parameterKey)
        {
            if (string.IsNullOrWhiteSpace(CompKey)) throw new InvalidOperationException("A component scope is required for sampling.");
            return CelestialSeed.Derive(WorldSeed, GenerationPath, CompKey, parameterKey);
        }
        public double SampleDouble(string parameterKey, DoubleParameterSpec specification)
        {
            specification = GetDoubleSpecification(parameterKey, specification);
            return SampleResolvedDouble(parameterKey, specification);
        }
        internal double SampleResolvedDouble(string parameterKey, DoubleParameterSpec specification)
        {
            sampledDoubles[(CompKey, parameterKey)] = specification;
            return CelestialParameterSampler.SampleDouble(specification, SeedFor(parameterKey));
        }
        internal bool TryGetSampledDoubleSpecification(string compKey, string parameterKey, out DoubleParameterSpec specification)
            => sampledDoubles.TryGetValue((compKey, parameterKey), out specification!);
        /// <summary>Returns the effective complete interval, including an explicit override, before a consumer validates and samples it.</summary>
        public DoubleParameterSpec GetDoubleSpecification(string parameterKey, DoubleParameterSpec specification)
        {
            var value = Override(parameterKey);
            return value == null ? specification : value.Value.HasValue ? DoubleParameterSpec.Fixed(value.Value.Value) : DoubleParameterSpec.Uniform(value.Min!.Value, value.Max!.Value);
        }
        public int SampleInt(string parameterKey, IntParameterSpec specification)
        {
            var value = Override(parameterKey);
            if (value != null)
            {
                double min = value.Value ?? value.Min!.Value, max = value.Value ?? value.Max!.Value;
                if (min < int.MinValue || max > int.MaxValue || Math.Truncate(min) != min || Math.Truncate(max) != max) throw new ArgumentException("InvalidIntegerParameterOverride");
                specification = value.Value.HasValue ? IntParameterSpec.Fixed((int)min) : IntParameterSpec.Uniform((int)min, (int)max);
            }
            return CelestialParameterSampler.SampleInt(specification, SeedFor(parameterKey));
        }
    }
}
