using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>Verse XML 配置入口。工厂按实际类型创建，每个天体单独持有解析后的配置与实例。</summary>
    public abstract class CelestialCompProperties : ICelestialCompFactory
    {
        public string key = "";
        public List<string> requires = new();
        public List<string> before = new();
        public List<string> after = new();
        public double updateIntervalSeconds;
        public CelestialCompFailurePolicy failurePolicy;

        public abstract string HandlerId { get; }
        public virtual int ConfigurationVersion => 1;
        public virtual int SchemaVersion => 1;
        protected virtual IEnumerable<string> Provides => Array.Empty<string>();
        protected virtual IEnumerable<string> IntrinsicRequires => Array.Empty<string>();
        protected virtual string? ExclusiveSlot => null;
        protected virtual CelestialUpdatePolicy DefaultUpdatePolicy => CelestialUpdatePolicy.None;

        public CelestialCompDescriptor Descriptor => new(key, HandlerId, SchemaVersion, Provides,
            IntrinsicRequires.Concat(requires), ExclusiveSlot, before, after,
            updateIntervalSeconds > 0 ? CelestialUpdatePolicy.Every(updateIntervalSeconds) : DefaultUpdatePolicy, failurePolicy);

        public abstract CelestialBodyComp CreateComp();
        public abstract CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context);

        public CelestialBodyComp Create(ICelestialResolvedProperties properties)
        {
            ArgumentNullException.ThrowIfNull(properties);
            if (properties.Key != key || properties.HandlerId != HandlerId)
                throw new ArgumentException("Resolved properties do not match this definition.", nameof(properties));
            Type? expected = HandlerId switch {
                SphericalBodyResolvedProperties.Handler => typeof(SphericalBodyResolvedProperties),
                SurfaceResolvedProperties.Handler => typeof(SurfaceResolvedProperties),
                ExternalPhysicsResolvedProperties.Handler => typeof(ExternalPhysicsResolvedProperties),
                AtmosphereResolvedProperties.Handler => typeof(AtmosphereResolvedProperties),
                LightSourceResolvedProperties.Handler => typeof(LightSourceResolvedProperties),
                ExistingEnvironmentResolvedProperties.Handler => typeof(ExistingEnvironmentResolvedProperties),
                MapGenerationResolvedProperties.Handler => typeof(MapGenerationResolvedProperties),
                BiomeGenerationResolvedProperties.Handler => typeof(BiomeGenerationResolvedProperties),
                _ when this is CelestialVisualCompProperties => typeof(VisualResolvedProperties),
                _ => null };
            if (expected != null && !expected.IsInstanceOfType(properties))
                throw new ArgumentException("Resolved properties have the wrong type.", nameof(properties));
            var component = CreateComp();
            component.Initialize(properties);
            return component;
        }

        public CelestialCompDefinition ToDefinition() => new(Descriptor, this, Resolve, ConfigurationVersion);

        public virtual IEnumerable<string> ConfigErrors()
        {
            if (string.IsNullOrWhiteSpace(key)) yield return "MissingCompKey";
            if (string.IsNullOrWhiteSpace(HandlerId)) yield return "MissingHandlerId";
            if (ConfigurationVersion < 1 || SchemaVersion < 1) yield return "InvalidComponentVersion";
            if (requires == null || before == null || after == null) yield return "NullComponentList";
            else if (requires.Concat(before).Concat(after).Any(string.IsNullOrWhiteSpace)) yield return "InvalidComponentReference";
            if (!double.IsFinite(updateIntervalSeconds) || updateIntervalSeconds < 0) yield return "InvalidUpdateInterval";
            if (!Enum.IsDefined(failurePolicy)) yield return "InvalidFailurePolicy";
        }
    }
}
