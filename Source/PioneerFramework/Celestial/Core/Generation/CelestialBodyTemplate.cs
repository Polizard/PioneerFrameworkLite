using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialResolvedComp
    {
        public CelestialCompDescriptor Descriptor { get; }
        public ICelestialResolvedProperties Properties { get; }
        public ICelestialCompFactory Factory { get; }
        public CelestialResolvedComp(CelestialCompDescriptor descriptor, ICelestialResolvedProperties properties, ICelestialCompFactory factory)
        {
            Descriptor = descriptor;
            Properties = properties;
            Factory = factory;
            CelestialFrozenConfiguration.Get(properties);
        }
    }

    public sealed class CelestialCompDefinition
    {
        private readonly Func<CelestialGenerationContext, CelestialResult<ICelestialResolvedProperties>> resolve;
        public CelestialCompDescriptor Descriptor { get; }
        public ICelestialCompFactory Factory { get; }
        public int ConfigurationVersion { get; }
        public CelestialCompDefinition(CelestialCompDescriptor descriptor, ICelestialCompFactory factory,
            Func<CelestialGenerationContext, CelestialResult<ICelestialResolvedProperties>> resolve, int configurationVersion = 1)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            ArgumentOutOfRangeException.ThrowIfLessThan(configurationVersion, 1);
            ConfigurationVersion = configurationVersion;
        }

        public CelestialResult<CelestialResolvedComp> Resolve(CelestialGenerationContext context)
        {
            try
            {
                var result = resolve(context.ForComp(Descriptor.Key));
                if (!result.Success) return CelestialResult.Reject<CelestialResolvedComp>(result.Errors.ToArray());
                var properties = result.Value;
                if (properties == null || properties.Key != Descriptor.Key || properties.HandlerId != Descriptor.HandlerId ||
                    properties.Version != ConfigurationVersion)
                    return Reject("HandlerContractViolation");
                if (Descriptor.Provides.Contains("geometry", StringComparer.Ordinal) &&
                    (properties is not ICelestialGeometryProperties geometry || !double.IsFinite(geometry.RadiusMeters) || geometry.RadiusMeters <= 0))
                    return Reject("InvalidGeometry");
                return CelestialResult.Ok(new CelestialResolvedComp(Descriptor, properties, Factory));
            }
            catch (Exception exception) { return Reject("ComponentResolutionFailed", exception.Message); }
        }

        private CelestialResult<CelestialResolvedComp> Reject(string code, string detail = "")
            => CelestialResult.Reject<CelestialResolvedComp>(new CelestialError(code, CompKey: Descriptor.Key, Detail: detail));
    }

    public sealed class CelestialBodyTemplate
    {
        public string DefinitionId { get; }
        public string Label { get; }
        public string Category { get; }
        public IReadOnlyList<string> Tags { get; }
        public IReadOnlyList<CelestialCompDefinition> Components { get; }

        private CelestialBodyTemplate(string definitionId, string label, string category, IEnumerable<string> tags,
            IReadOnlyList<CelestialCompDefinition> components)
        {
            DefinitionId = definitionId;
            Label = label;
            Category = category;
            Tags = Array.AsReadOnly(tags.ToArray());
            Components = components;
        }

        public static CelestialResult<CelestialBodyTemplate> Create(string definitionId, string label, string category,
            IEnumerable<string> tags, IEnumerable<CelestialCompDefinition> components)
        {
            if (string.IsNullOrWhiteSpace(definitionId)) return Reject("MissingDefinitionId");
            ArgumentNullException.ThrowIfNull(tags);
            ArgumentNullException.ThrowIfNull(components);
            var definitions = components.ToArray();
            if (definitions.Any(definition => definition == null)) return Reject("InvalidComponent");
            var graph = CelestialCompGraph.Resolve(definitions.Select(definition => definition.Descriptor).ToArray());
            if (!graph.Success) return CelestialResult.Reject<CelestialBodyTemplate>(graph.Errors.ToArray());
            int geometryCount = definitions.Count(definition => definition.Descriptor.Provides.Contains("geometry", StringComparer.Ordinal));
            if (geometryCount != 1) return Reject(geometryCount == 0 ? "MissingGeometry" : "MultipleGeometryProviders");
            if (definitions.Count(definition => definition.Descriptor.Provides.Contains("surface", StringComparer.Ordinal)) > 1)
                return Reject("MultipleSurfaceProviders");
            var byKey = definitions.ToDictionary(definition => definition.Descriptor.Key, StringComparer.Ordinal);
            var copiedTags = tags.ToArray();
            if (copiedTags.Any(string.IsNullOrWhiteSpace)) return Reject("InvalidTags");
            return CelestialResult.Ok(new CelestialBodyTemplate(definitionId, label, category, copiedTags,
                Array.AsReadOnly(graph.Value.Select(descriptor => byKey[descriptor.Key]).ToArray())));
        }

        public bool HasCapability(string capability)
            => Components.Any(component => component.Descriptor.Provides.Contains(capability, StringComparer.Ordinal));

        public CelestialResult<CelestialBodyTemplate> WithOverrides(IEnumerable<CelestialCompDefinition> overrides)
        {
            var replacements = new Dictionary<string, CelestialCompDefinition>(StringComparer.Ordinal);
            foreach (var replacement in overrides)
            {
                if (replacement == null) return Reject("InvalidCompOverride");
                if (!Components.Any(component => component.Descriptor.Key == replacement.Descriptor.Key)) return Reject("UnknownCompOverride");
                if (!replacements.TryAdd(replacement.Descriptor.Key, replacement)) return Reject("DuplicateCompOverride");
            }
            return Create(DefinitionId, Label, Category, Tags, Components.Select(component =>
                replacements.TryGetValue(component.Descriptor.Key, out var replacement) ? replacement : component));
        }

        public CelestialResult<IReadOnlyList<CelestialResolvedComp>> Resolve(CelestialGenerationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var result = new List<CelestialResolvedComp>();
            foreach (var definition in Components)
            {
                var resolved = definition.Resolve(context);
                if (!resolved.Success) return CelestialResult.Reject<IReadOnlyList<CelestialResolvedComp>>(resolved.Errors.ToArray());
                result.Add(resolved.Value);
            }
            var overrides = context.ValidateOverrides();
            if (!overrides.Success) return CelestialResult.Reject<IReadOnlyList<CelestialResolvedComp>>(overrides.Errors.ToArray());
            var properties = Array.AsReadOnly(result.Select(component => component.Properties).ToArray());
            foreach (var component in result)
            {
                if (component.Properties is not ICelestialResolvedConfigurationValidator validator) continue;
                try
                {
                    var validation = validator.Validate(properties);
                    if (!validation.Success) return CelestialResult.Reject<IReadOnlyList<CelestialResolvedComp>>(validation.Errors.ToArray());
                    if (!validation.Value) return CelestialResult.Reject<IReadOnlyList<CelestialResolvedComp>>(
                        new CelestialError("InconsistentParameters", CompKey: component.Descriptor.Key));
                }
                catch (Exception exception)
                {
                    return CelestialResult.Reject<IReadOnlyList<CelestialResolvedComp>>(
                        new CelestialError("ComponentValidationFailed", CompKey: component.Descriptor.Key, Detail: exception.Message));
                }
            }
            return CelestialResult.Ok<IReadOnlyList<CelestialResolvedComp>>(result.AsReadOnly());
        }

        private static CelestialResult<CelestialBodyTemplate> Reject(string code)
            => CelestialResult.Reject<CelestialBodyTemplate>(new CelestialError(code));
    }
}
