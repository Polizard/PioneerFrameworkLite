using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CompSphericalBody : CelestialBodyComp, ICelestialGeometryCapability
    {
        public CompSphericalBody(CelestialCompDescriptor descriptor) : base(descriptor) { }
        public CelestialGeometryDescription GetDescription() => new("sphere", ((SphericalBodyResolvedProperties)Context.Properties).RadiusMeters, "PFL.BodyFixed.v1");
        public DVector3 FixedPosition(double latitudeRadians, double longitudeRadians)
            => ((SphericalBodyResolvedProperties)Context.Properties).FixedPosition(latitudeRadians, longitudeRadians);
    }
    public sealed class CompProperties_SphericalBody : CelestialCompProperties
    {
        public CelestialDoubleParameterXml radiusMeters = new() { value = 5000000 };
        public override string HandlerId => SphericalBodyResolvedProperties.Handler;
        protected override IEnumerable<string> Provides => new[] { "geometry" };
        protected override string ExclusiveSlot => "geometry";
        public override CelestialBodyComp CreateComp() => new CompSphericalBody(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            var specification = radiusMeters?.ToSpec();
            if (specification == null || !specification.Success || specification.Value.Minimum <= 0)
                return CelestialResult.Reject<ICelestialResolvedProperties>(new CelestialError("InvalidGeometry", CompKey: key));
            return CelestialResult.Ok<ICelestialResolvedProperties>(new SphericalBodyResolvedProperties(key, context.SampleDouble("radiusMeters", specification.Value)));
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            var specification = radiusMeters?.ToSpec();
            if (specification == null || !specification.Success || specification.Value.Minimum <= 0) yield return "InvalidGeometry";
        }
    }
}
