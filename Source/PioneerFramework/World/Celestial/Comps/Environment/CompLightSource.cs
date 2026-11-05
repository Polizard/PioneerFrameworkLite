using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CompLightSource(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor), ICelestialLightSourceCapability
    {
        public CelestialLightDescription GetDescription() => ((LightSourceResolvedProperties)Context.Properties).Description;
    }
    public sealed class CompProperties_LightSource : CelestialCompProperties
    {
        public CelestialDoubleParameterXml intensity = new() { value = 1 };
        public CelestialDoubleParameterXml red = new() { value = 1 };
        public CelestialDoubleParameterXml green = new() { value = 1 };
        public CelestialDoubleParameterXml blue = new() { value = 1 };
        public override string HandlerId => LightSourceResolvedProperties.Handler;
        protected override IEnumerable<string> Provides => new[] { "light.source" };
        protected override string ExclusiveSlot => "light.source";
        public override CelestialBodyComp CreateComp() => new CompLightSource(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            var errors = ConfigErrors().ToArray();
            if (errors.Length > 0) return CelestialResult.Reject<ICelestialResolvedProperties>(errors.Select(code => new CelestialError(code, CompKey: key)).ToArray());
            return CelestialResult.Ok<ICelestialResolvedProperties>(new LightSourceResolvedProperties(key, new(context.SampleDouble("intensity", intensity.ToSpec().Value),
                context.SampleDouble("red", red.ToSpec().Value), context.SampleDouble("green", green.ToSpec().Value), context.SampleDouble("blue", blue.ToSpec().Value))));
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            var light = intensity?.ToSpec(); if (light == null || !light.Success || light.Value.Minimum < 0) yield return "InvalidLightIntensity";
            foreach (var channel in new[] { red, green, blue })
            { var spec = channel?.ToSpec(); if (spec == null || !spec.Success || spec.Value.Minimum < 0 || spec.Value.Maximum > 1) yield return "InvalidLightColor"; }
        }
    }
}
