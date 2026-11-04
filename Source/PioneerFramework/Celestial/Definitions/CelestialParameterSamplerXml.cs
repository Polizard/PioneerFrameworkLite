using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>
    /// 采样器作用域。只影响构造/Complete 的拒绝消息与终检项；不改变参数取值、抽样顺序或区间校验的数值比较。
    /// Planet 覆盖 terrain/biomeRules/landmarks/diversity/visual/motion，Environment 覆盖 climate/exposure。
    /// </summary>
    internal enum CelestialParameterScope { Planet, Environment }

    /// <summary>单个参数的闭区间域（含是否要求严格正数）；用于 environment 链路的 key→域表单源。</summary>
    internal readonly struct CelestialParameterDomain
    {
        public double Minimum { get; }
        public double Maximum { get; }
        public bool StrictlyPositive { get; }
        public CelestialParameterDomain(double minimum, double maximum, bool strictlyPositive = false)
        { Minimum = minimum; Maximum = maximum; StrictlyPositive = strictlyPositive; }
    }

    /// <summary>
    /// environment/climate/exposure 参数域表单源。旧的 key→switch 被原样搬到这里：
    /// 未列出的 key 继续使用 Default（下界 0、上界 1e12、非严格正），因此隐藏默认上限行为不漂移。
    /// </summary>
    internal static class CelestialPlanetDomains
    {
        internal static readonly CelestialParameterDomain Default = new(0, 1e12);
        private static readonly Dictionary<string, CelestialParameterDomain> domains = new(StringComparer.Ordinal)
        {
            // CelestialClimateXml
            ["meanTemperatureCelsius"] = new(-273.15, 10000),
            ["lapseCelsiusPerMeter"] = new(-1, 1),
            ["yearLengthTicks"] = new(1e-6, 1e20, strictlyPositive: true),
            ["humidity"] = new(0, 1),
            ["thermalResponseSeconds"] = new(0, 1e12),
            ["windSpeed"] = new(0, 1e6),
            ["rainfallMillimeters"] = new(0, 1000000 / 1.5),
            ["diurnalAmplitudeCelsius"] = new(0, 10000),
            ["seasonalAmplitudeCelsius"] = new(0, 10000),
            ["illuminationTemperatureCelsius"] = new(0, 10000),
            ["lightMultiplier"] = new(0, 1e12),
            // CelestialExposureXml
            ["minimumOxygenPascals"] = new(0, 1e12, strictlyPositive: true),
            ["minimumPressurePascals"] = new(0, 1e12),
            ["maximumPressurePascals"] = new(0, 1e12, strictlyPositive: true),
            ["hypoxiaPerDay"] = new(0, 1e12),
            ["pressurePerDay"] = new(0, 1e12),
            ["toxicityPerDay"] = new(0, 1e12),
            ["radiationPerDay"] = new(0, 1e12),
            ["carbonDioxideThresholdPascals"] = new(0, 1e12, strictlyPositive: true)
        };
        internal static CelestialParameterDomain For(string key) => domains.TryGetValue(key, out var domain) ? domain : Default;
    }

    /// <summary>
    /// 合并后的唯一 XML 数值采样器。planet 链路显式传 lower/upper；environment 链路查 <see cref="CelestialPlanetDomains"/>。
    /// 两条链路的异常消息、参数消费顺序与 Complete 终检各自保持原样，不互相软化。
    /// </summary>
    internal sealed class CelestialParameterSamplerXml
    {
        private readonly Dictionary<string, CelestialDoubleParameterXml> parameters;
        private readonly CelestialGenerationContext? context;
        private readonly string prefix;
        private readonly CelestialParameterScope scope;
        private readonly bool sample;
        private readonly Dictionary<string, DoubleParameterSpec> intervals = new(StringComparer.Ordinal);
        public CelestialParameterSamplerXml(List<CelestialEnvironmentParameterXml> parameters, CelestialGenerationContext? context, string prefix,
            CelestialParameterScope scope = CelestialParameterScope.Planet, bool sample = true)
        {
            if (parameters == null || parameters.Any(p => p == null || string.IsNullOrWhiteSpace(p.key) || p.value == null) ||
                parameters.Select(p => p.key).Distinct(StringComparer.Ordinal).Count() != parameters.Count)
                throw new ArgumentException(scope == CelestialParameterScope.Environment ? "Invalid environment parameters." : "Invalid planet parameter keys.");
            this.parameters = parameters.ToDictionary(p => p.key, p => p.value, StringComparer.Ordinal); this.context = context; this.prefix = prefix;
            this.scope = scope; this.sample = sample;
        }
        private DoubleParameterSpec Parse(string key, double fallback)
        {
            var parsed = parameters.Remove(key, out var parameter) ? parameter.ToSpec() : CelestialResult.Ok(DoubleParameterSpec.Fixed(fallback));
            if (!parsed.Success) throw new ArgumentException("Invalid parameter: " + prefix + key);
            return context?.GetDoubleSpecification(prefix + key, parsed.Value) ?? parsed.Value;
        }
        private DoubleParameterSpec Specification(string key, double fallback, double lower, double upper)
        {
            var spec = Parse(key, fallback);
            CelestialPlanetTerrainConfiguration.Domain(spec.Minimum, lower, upper, prefix + key);
            CelestialPlanetTerrainConfiguration.Domain(spec.Maximum, lower, upper, prefix + key);
            intervals.Add(key, spec);
            return spec;
        }
        private DoubleParameterSpec Specification(string key, double fallback)
        {
            var spec = Parse(key, fallback);
            var domain = CelestialPlanetDomains.For(key);
            if (spec.Minimum < domain.Minimum || spec.Maximum > domain.Maximum || domain.StrictlyPositive && spec.Minimum <= 0)
                throw new ArgumentException("Environment parameter range outside domain: " + prefix + key);
            intervals.Add(key, spec);
            return spec;
        }
        public double Get(string key, double fallback, double lower, double upper)
        {
            var spec = Specification(key, fallback, lower, upper);
            return context == null || !sample ? spec.Minimum : context.SampleResolvedDouble(prefix + key, spec);
        }
        public double Get(string key, double fallback)
        {
            var spec = Specification(key, fallback);
            return context == null ? spec.Minimum : context.SampleResolvedDouble(prefix + key, spec);
        }
        public int GetInt(string key, int fallback)
        {
            var spec = Specification(key, fallback, int.MinValue, int.MaxValue);
            if (Math.Truncate(spec.Minimum) != spec.Minimum || Math.Truncate(spec.Maximum) != spec.Maximum) throw new ArgumentException("Integer planet parameter required: " + prefix + key);
            return context == null ? (int)spec.Minimum : CelestialParameterSampler.SampleInt(spec.IsFixed ? IntParameterSpec.Fixed((int)spec.Minimum) :
                IntParameterSpec.Uniform((int)spec.Minimum, (int)spec.Maximum), context.SeedFor(prefix + key));
        }
        public DoubleParameterSpec Interval(string key) => intervals[key];
        public void Complete()
        {
            if (parameters.Count != 0)
            {
                // 旧 environment 消息不含 prefix，旧 planet 消息含 prefix；逐字保留。
                if (scope == CelestialParameterScope.Environment) throw new ArgumentException("Unknown environment parameter: " + parameters.Keys.First());
                throw new ArgumentException("Unknown planet parameter: " + prefix + parameters.Keys.First());
            }
            if (scope == CelestialParameterScope.Environment)
            {
                // environment 的严格序（>= 即拒绝）与 planet 的成对区间序（> 才拒绝）必须各自保留，不能合并。
                if (intervals.TryGetValue("minimumPressurePascals", out var lower) &&
                    intervals.TryGetValue("maximumPressurePascals", out var upper) && lower.Maximum >= upper.Minimum)
                    throw new ArgumentException("Pressure ranges must remain strictly ordered for all samples.");
                return;
            }
            foreach (var lower in intervals.Where(p => p.Key.StartsWith("minimum", StringComparison.Ordinal)))
                if (intervals.TryGetValue("maximum" + lower.Key[7..], out var upper) && lower.Value.Maximum > upper.Minimum)
                    throw new ArgumentException("Planet rule intervals must remain ordered for every sample: " + prefix + lower.Key);
        }
    }
}
