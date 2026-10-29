using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CompExistingEnvironment(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor), ICelestialEnvironmentCapability
    {
        private CelestialInstanceId? primaryLight;
        private CelestialInstanceId PrimaryLight()
        {
            if (primaryLight.HasValue) return primaryLight.Value;
            var profile = ((ExistingEnvironmentResolvedProperties)Context.Properties).Profile;
            if (!Context.TryGetService<ICelestialCapabilityResolver>(out var resolver) ||
                !resolver.TryGetInitializationReference(Context.InstanceId, profile.PrimaryLightReference, out var light) || resolver.GetCapabilities<ICelestialLightSourceCapability>(light).Count != 1)
                throw new InvalidOperationException("PrimaryLightUnavailable: " + profile.PrimaryLightReference);
            primaryLight = light;
            return light;
        }
        public override void OnReady() => _ = PrimaryLight();
        public override void OnRestored() => _ = PrimaryLight();
        public CelestialEnvironmentSample Evaluate(CelestialEnvironmentQuery query)
        {
            if (!double.IsFinite(query.DisplayTicksAbs) || !float.IsFinite(query.LongitudeDegrees) || !float.IsFinite(query.LatitudeDegrees) || Math.Abs(query.LatitudeDegrees) > 90)
                throw new ArgumentOutOfRangeException(nameof(query));
            var profile = ((ExistingEnvironmentResolvedProperties)Context.Properties).Profile;
            var cycle = PF_SunMath.EvaluateCycle(query.DisplayTicksAbs, (float)profile.DayLengthTicks, query.LongitudeDegrees);
            return new(cycle.DayPercent, PF_SunMath.SunGlowPercent(query.LatitudeDegrees, cycle.DayOfYear, cycle.DayPercent), PrimaryLight(),
                query.BiomeId != null && profile.VacuumBiomeIds.Contains(query.BiomeId, StringComparer.Ordinal) ? query.BiomeId : null, cycle.DayOfYearFraction);
        }
    }
    public sealed class CompProperties_ExistingEnvironment : CelestialCompProperties
    {
        public CelestialEnvironmentProfileDef profile = null!;
        public override string HandlerId => ExistingEnvironmentResolvedProperties.Handler;
        protected override IEnumerable<string> Provides => new[] { "environment.existing" };
        protected override IEnumerable<string> IntrinsicRequires => new[] { "geometry" };
        protected override string ExclusiveSlot => "environment.existing";
        public override CelestialBodyComp CreateComp() => new CompExistingEnvironment(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            var value = profile?.ToProfile();
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
