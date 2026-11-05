using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CompSurface : CelestialBodyComp, ICelestialSurfaceCapability
    {
        public const string RequestCommand = "PFL.Surface.Request";
        public CompSurface(CelestialCompDescriptor descriptor) : base(descriptor) { }
        public SurfaceResolvedProperties GetProperties() => (SurfaceResolvedProperties)Context.Properties;
        public override void OnReady()
        {
            if (GetProperties().GenerationMode != CelestialSurfaceGenerationMode.Creation) return;
            var result = Context.Submit(new CelestialCommand(RequestCommand, "surface/creation/" + Context.InstanceId.Value,
                Context.InstanceId, Context.WorldSessionId, Context.DescriptorRevision));
            if (!result.Success) throw new InvalidOperationException(string.Join(", ", result.Errors.Select(error => error.Code)));
        }
    }
    public sealed class CompProperties_Surface : CelestialCompProperties
    {
        public CelestialSurfaceGenerationMode generationMode = CelestialSurfaceGenerationMode.OnDemand;
        public CelestialIntParameterXml subdivisions = new() { value = 5 };
        public CelestialDoubleParameterXml metersPerLayerUnit = new() { value = 50000 };
        public override string HandlerId => SurfaceResolvedProperties.Handler;
        protected override IEnumerable<string> Provides => new[] { "surface" };
        protected override IEnumerable<string> IntrinsicRequires => new[] { "geometry" };
        protected override string ExclusiveSlot => "surface";
        public override CelestialBodyComp CreateComp() => new CompSurface(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            var errors = ConfigErrors().ToArray();
            if (errors.Length > 0) return CelestialResult.Reject<ICelestialResolvedProperties>(errors.Select(error => new CelestialError(error, CompKey: key)).ToArray());
            return CelestialResult.Ok<ICelestialResolvedProperties>(new SurfaceResolvedProperties(key, generationMode,
                context.SampleInt("subdivisions", subdivisions.ToSpec().Value), context.SampleDouble("metersPerLayerUnit", metersPerLayerUnit.ToSpec().Value)));
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (!Enum.IsDefined(generationMode)) yield return "InvalidSurfaceGenerationMode";
            var divisions = subdivisions?.ToSpec();
            if (divisions == null || !divisions.Success || divisions.Value.Minimum < 0 || divisions.Value.Maximum > SurfaceResolvedProperties.MaxBuiltinSubdivisions)
                yield return "InvalidSurfaceSubdivisions";
            var scale = metersPerLayerUnit?.ToSpec();
            if (scale == null || !scale.Success || scale.Value.Minimum <= 0) yield return "InvalidSurfaceScale";
        }
    }
}
