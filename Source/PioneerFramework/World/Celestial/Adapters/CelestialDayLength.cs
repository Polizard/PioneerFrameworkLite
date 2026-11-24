using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>
    /// 自转周期（dayLengthTicks）的**唯一解析优先级**（2026-11-03 P2 数据源单一化）：
    /// **物理 motion**（既有星历初始化，权威）→ **environment profile**（无物理绑定的兼容 fallback）→ 0。
    ///
    /// 为什么收敛到一处：旧实现里渲染/视觉只读 `environment`、物理只读 `motion`，
    /// 两侧各读一侧；当前 XML 恰好一致，但没有任何机制保证，也没有校验。
    /// 现在投影、视觉描述缓存与物理实例表都只问这一个 helper。
    ///
    /// 解析是**只读**的：外部 provider（非既有星历）、损坏/不支持的载荷、缺字段都按“无物理”处理，
    /// 绝不用异常打断渲染或建表；只有成功且 DayLengthTicks&gt;0 才采信物理值。
    /// </summary>
    internal static class CelestialDayLength
    {
        internal static double ResolveFrom(IEnumerable<CelestialBodyComp> components)
            => ResolveFrom(components.Select(component => component.ResolvedProperties).OfType<ICelestialResolvedProperties>());

        internal static double ResolveFrom(IEnumerable<ICelestialResolvedProperties> properties)
        {
            foreach (var physics in properties.OfType<ICelestialPhysicsBindingProperties>())
            {
                var parsed = ExistingEphemerisParameters.Parse(physics.Initialization);
                if (parsed.Success && parsed.Value.DayLengthTicks > 0.0) return parsed.Value.DayLengthTicks;
            }
            foreach (var environment in properties.OfType<ExistingEnvironmentResolvedProperties>())
                if (environment.Profile.DayLengthTicks > 0.0) return environment.Profile.DayLengthTicks;
            return 0.0;
        }

        internal static double ResolveFrom(IEnumerable<CelestialCompSaveRecord> records)
        {
            if (records == null) return 0.0;
            foreach (var record in records)
            {
                if (record.HandlerId != ExternalPhysicsResolvedProperties.Handler) continue;
                var binding = ExternalPhysicsResolvedProperties.Deserialize(record.Key, record.ConfigurationVersion, record.ConfigurationPayload);
                if (!binding.Success) continue;
                var parsed = ExistingEphemerisParameters.Parse(binding.Value.Initialization);
                if (parsed.Success && parsed.Value.DayLengthTicks > 0.0) return parsed.Value.DayLengthTicks;
            }
            foreach (var record in records)
            {
                if (record.HandlerId != ExistingEnvironmentResolvedProperties.Handler) continue;
                var environment = ExistingEnvironmentResolvedProperties.Deserialize(record.Key, record.ConfigurationVersion, record.ConfigurationPayload);
                if (environment.Success && environment.Value.Profile.DayLengthTicks > 0.0) return environment.Value.Profile.DayLengthTicks;
            }
            return 0.0;
        }
    }
}
