using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CompAtmosphere(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor), ICelestialAtmosphereCapability
    {
        private AtmosphereProfile profile;
        protected override void OnAttached() => profile = ((AtmosphereResolvedProperties)Context.Properties).BuildProfile();
        public AtmosphereProfile GetProfile() => profile;
    }
    public sealed class CompProperties_Atmosphere : CelestialCompProperties
    {
        public CelestialDoubleParameterXml rho0 = new() { value = 0 };
        public CelestialDoubleParameterXml scaleHeight = new() { value = 0 };
        public CelestialDoubleParameterXml topAltitude = new() { value = 0 };
        public override string HandlerId => AtmosphereResolvedProperties.Handler;
        protected override IEnumerable<string> Provides => new[] { "atmosphere" };
        protected override IEnumerable<string> IntrinsicRequires => new[] { "geometry" };
        protected override string ExclusiveSlot => "atmosphere";
        public override CelestialBodyComp CreateComp() => new CompAtmosphere(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            var errors = ConfigErrors().ToArray();
            if (errors.Length > 0) return CelestialResult.Reject<ICelestialResolvedProperties>(errors.Select(code => new CelestialError(code, CompKey: key)).ToArray());
            return CelestialResult.Ok<ICelestialResolvedProperties>(new AtmosphereResolvedProperties(key, context.SampleDouble("rho0", rho0.ToSpec().Value),
                context.SampleDouble("scaleHeight", scaleHeight.ToSpec().Value), context.SampleDouble("topAltitude", topAltitude.ToSpec().Value)));
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            foreach (var parameter in new[] { rho0, scaleHeight, topAltitude })
            { var spec = parameter?.ToSpec(); if (spec == null || !spec.Success || spec.Value.Minimum < 0) yield return "InvalidAtmosphereParameter"; }
        }
    }
}
