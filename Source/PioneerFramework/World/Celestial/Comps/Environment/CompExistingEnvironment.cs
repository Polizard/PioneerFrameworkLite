using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public class CompExistingEnvironment(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor), ICelestialEnvironmentCapability
    {
        private CelestialInstanceId? primaryLight;
        private double? resolvedDayLength;
        /// <summary>
        /// 日长唯一优先级（2026-11-07 B-2）：物理 motion 的权威自转周期 → 本 profile 的兼容 fallback。
        /// 与 <see cref="CelestialDayLength"/> 同口径；外部 provider/解析失败时退回 profile，绝不抛异常。
        /// </summary>
        protected double DayLength(CelestialEnvironmentProfile profile)
        {
            if (resolvedDayLength.HasValue) return resolvedDayLength.Value;
            var sources = Context.GetCapabilities<ICelestialDayLengthSource>();
            double value = sources.Count == 1 && double.IsFinite(sources[0].DayLengthTicks) && sources[0].DayLengthTicks > 0
                ? sources[0].DayLengthTicks : profile.DayLengthTicks;
            resolvedDayLength = value;
            return value;
        }
        protected CelestialInstanceId PrimaryLight()
        {
            if (primaryLight.HasValue) return primaryLight.Value;
            var profile = ((ExistingEnvironmentResolvedProperties)Context.Properties).Profile;
            if (!Context.TryGetService<ICelestialCapabilityResolver>(out var resolver) ||
                !resolver.TryGetInitializationReference(Context.InstanceId, profile.PrimaryLightReference, out var light) || resolver.GetCapabilities<ICelestialLightSourceCapability>(light).Count != 1)
                throw new InvalidOperationException("PrimaryLightUnavailable: " + profile.PrimaryLightReference);
            primaryLight = light;
            return light;
        }
        public override void OnReady() { resolvedDayLength = null; _ = PrimaryLight(); }
        public override void OnRestored() { resolvedDayLength = null; _ = PrimaryLight(); }
        public virtual CelestialEnvironmentSample Evaluate(CelestialEnvironmentQuery query)
        {
            if (!double.IsFinite(query.DisplayTicksAbs) || !float.IsFinite(query.LongitudeDegrees) || !float.IsFinite(query.LatitudeDegrees) || Math.Abs(query.LatitudeDegrees) > 90)
                throw new ArgumentOutOfRangeException(nameof(query));
            var profile = ((ExistingEnvironmentResolvedProperties)Context.Properties).Profile;
            var cycle = PF_SunMath.EvaluateCycle(query.DisplayTicksAbs, (float)DayLength(profile), query.LongitudeDegrees);
            return new(cycle.DayPercent, PF_SunMath.SunGlowPercent(query.LatitudeDegrees, cycle.DayOfYear, cycle.DayPercent), PrimaryLight(),
                query.BiomeId != null && profile.VacuumBiomeIds.Contains(query.BiomeId, StringComparer.Ordinal) ? query.BiomeId : null, cycle.DayOfYearFraction);
        }
    }
    public sealed class CompProperties_ExistingEnvironment : CelestialCompProperties, ICelestialCompFactory
    {
        public CelestialEnvironmentProfileDef profile = null!;
        public override int ConfigurationVersion => profile?.climate != null || profile?.exposure != null ? 2 : 1;
        public override string HandlerId => ExistingEnvironmentResolvedProperties.Handler;
        protected override IEnumerable<string> Provides => new[] { "environment.existing" };
        protected override IEnumerable<string> IntrinsicRequires => new[] { "geometry" };
        protected override string ExclusiveSlot => "environment.existing";
        public override CelestialBodyComp CreateComp() => profile?.climate != null || profile?.exposure != null ?
            new CompPlanetEnvironment(Descriptor) : new CompExistingEnvironment(Descriptor);
        CelestialBodyComp ICelestialCompFactory.Create(ICelestialResolvedProperties properties)
        {
            if (properties is not ExistingEnvironmentResolvedProperties resolved || resolved.Key != key)
                throw new ArgumentException("Environment properties do not match factory.");
            CelestialBodyComp component = resolved.Version == 2 ? new CompPlanetEnvironment(Descriptor) : new CompExistingEnvironment(Descriptor);
            component.Initialize(resolved); return component;
        }
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            var value = profile?.ToProfile(context);
            return value == null ? CelestialResult.Reject<ICelestialResolvedProperties>(new CelestialError("EnvironmentProfileMissing", CompKey: key)) :
                value.Success ? CelestialResult.Ok<ICelestialResolvedProperties>(new ExistingEnvironmentResolvedProperties(key, value.Value)) : CelestialResult.Reject<ICelestialResolvedProperties>(value.Errors.ToArray());
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (profile == null) yield return "EnvironmentProfileMissing";
            else foreach (var error in profile.ToProfile().Errors) yield return error.Code;
        }
    }
}
