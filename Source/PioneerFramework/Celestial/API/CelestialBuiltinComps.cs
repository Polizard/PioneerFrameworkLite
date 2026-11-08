using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>
    /// 内置 comp 的唯一登记表：handler、解析配置类型、最高配置版本与能力契约只在这里声明。
    /// `CelestialCapabilityRegistry`、`CelestialCompProperties.Create` 的类型校验与 `CelestialBuiltinCompRegistry`
    /// 的版本/handler 集都从本表派生；漏登一行会让存档恢复静默缺 handler，因此由表行的一致性测试锁死。
    /// 本文件属于 API 纯核（会被链接进纯测试工程），不得引用 Verse/RimWorld/Unity。
    /// </summary>
    internal static class CelestialBuiltinComps
    {
        /// <summary>一行 = 一个内置 handler；Capabilities 是该 handler 可能提供的全部能力名与契约接口。</summary>
        internal sealed class Row
        {
            internal Row(string handler, Type propertiesType, int configurationVersion,
                IReadOnlyList<(string Name, Type Contract)> capabilities)
            {
                if (string.IsNullOrWhiteSpace(handler)) throw new ArgumentException("Missing builtin handler id.", nameof(handler));
                if (propertiesType == null || !typeof(ICelestialResolvedProperties).IsAssignableFrom(propertiesType))
                    throw new ArgumentException("Builtin properties type must implement ICelestialResolvedProperties.", nameof(propertiesType));
                if (configurationVersion < 1) throw new ArgumentOutOfRangeException(nameof(configurationVersion));
                if (capabilities == null || capabilities.Count == 0) throw new ArgumentException("Builtin comps must declare capabilities.", nameof(capabilities));
                foreach (var (name, contract) in capabilities)
                {
                    if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Missing capability name.", nameof(capabilities));
                    if (contract == null || !contract.IsInterface) throw new ArgumentException("Builtin capability contracts must be interfaces.", nameof(capabilities));
                }
                Handler = handler;
                PropertiesType = propertiesType;
                ConfigurationVersion = configurationVersion;
                Capabilities = Array.AsReadOnly(capabilities.ToArray());
            }

            internal string Handler { get; }
            internal Type PropertiesType { get; }
            /// <summary>存档注册表声明的最高配置版本；与每实例的动态 ConfigurationVersion 区分。</summary>
            internal int ConfigurationVersion { get; }
            internal IReadOnlyList<(string Name, Type Contract)> Capabilities { get; }
        }

        private static Row Comp(string handler, Type propertiesType, int configurationVersion,
            params (string Name, Type Contract)[] capabilities) => new(handler, propertiesType, configurationVersion, capabilities);

        private static Row Visual(CelestialVisualKind kind) => Comp(VisualResolvedProperties.HandlerFor(kind), typeof(VisualResolvedProperties), 2,
            (VisualResolvedProperties.CapabilityFor(kind), typeof(ICelestialVisualCapability)));

        /// <summary>声明顺序同时决定能力注册顺序与存档注册顺序，改动等于改契约，必须显式评审。</summary>
        internal static IReadOnlyList<Row> All { get; } = Array.AsReadOnly(new[]
        {
            Comp(SphericalBodyResolvedProperties.Handler, typeof(SphericalBodyResolvedProperties), 1,
                ("geometry", typeof(ICelestialGeometryCapability))),
            Comp(SurfaceResolvedProperties.Handler, typeof(SurfaceResolvedProperties), 1,
                ("surface", typeof(ICelestialSurfaceCapability))),
            Comp(ExternalPhysicsResolvedProperties.Handler, typeof(ExternalPhysicsResolvedProperties), 1,
                ("physics.binding", typeof(ICelestialPhysicsBinding))),
            // 大气在压力模式下同时提供 composition；表记录该 handler 的提供全集，实例 Descriptor.Provides 仍是动态的。
            Comp(AtmosphereResolvedProperties.Handler, typeof(AtmosphereResolvedProperties), 2,
                ("atmosphere", typeof(ICelestialAtmosphereCapability)),
                ("atmosphere.composition", typeof(ICelestialAtmosphereCompositionCapability))),
            Comp(LightSourceResolvedProperties.Handler, typeof(LightSourceResolvedProperties), 1,
                ("light.source", typeof(ICelestialLightSourceCapability))),
            Comp(ExistingEnvironmentResolvedProperties.Handler, typeof(ExistingEnvironmentResolvedProperties), 2,
                ("environment.existing", typeof(ICelestialEnvironmentCapability))),
            Comp(MapGenerationResolvedProperties.Handler, typeof(MapGenerationResolvedProperties), 4,
                ("map.generation", typeof(ICelestialMapGenerationCapability))),
            Comp(BiomeGenerationResolvedProperties.Handler, typeof(BiomeGenerationResolvedProperties), 2,
                ("biomes", typeof(ICelestialBiomeGenerationCapability))),
            Visual(CelestialVisualKind.Surface),
            Visual(CelestialVisualKind.Atmosphere),
            Visual(CelestialVisualKind.Clouds),
            Visual(CelestialVisualKind.Stellar)
        });

        private static readonly IReadOnlyDictionary<string, Row> byHandler =
            All.ToDictionary(row => row.Handler, StringComparer.Ordinal);

        internal static bool TryGet(string handlerId, out Row row)
        {
            if (!string.IsNullOrWhiteSpace(handlerId)) return byHandler.TryGetValue(handlerId, out row!);
            row = null!;
            return false;
        }
    }
}
