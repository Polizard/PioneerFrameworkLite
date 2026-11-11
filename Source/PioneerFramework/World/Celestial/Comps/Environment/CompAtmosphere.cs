using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public class CompAtmosphere(CelestialCompDescriptor descriptor) : CelestialBodyComp(descriptor), ICelestialAtmosphereCapability
    {
        private AtmosphereProfile profile;
        protected AtmosphereResolvedProperties Properties { get; private set; } = null!;
        protected override void OnAttached()
        { Properties = (AtmosphereResolvedProperties)Context.Properties; profile = Properties.BuildProfile(); }
        public AtmosphereProfile GetProfile() => profile;
    }
    public sealed class CompAtmosphereComposition(CelestialCompDescriptor descriptor) : CompAtmosphere(descriptor), ICelestialAtmosphereCompositionCapability
    {
        public CelestialAtmosphereSample Sample(double altitudeMeters) => Properties.Sample(altitudeMeters);
    }
    public sealed class CelestialGasFractionXml
    {
        public string gas = "";
        public double moleFraction;
        private readonly List<string> xmlErrors = new();
        internal bool HasXmlErrors => xmlErrors.Count > 0;
        public void LoadDataFromXmlCustom(System.Xml.XmlNode node) => CelestialMotionXmlFields.Read(this, node, xmlErrors);
    }
    public sealed class CompProperties_Atmosphere : CelestialCompProperties
    {
        public CelestialDoubleParameterXml? rho0;
        public CelestialDoubleParameterXml? scaleHeight;
        public CelestialDoubleParameterXml topAltitude = new() { value = 0 };
        public CelestialDoubleParameterXml? pressurePascals;
        public CelestialDoubleParameterXml? temperatureKelvin;
        public List<CelestialGasFractionXml> composition = new();
        private bool PressureMode => pressurePascals != null || temperatureKelvin != null || composition?.Count > 0;
        public override int ConfigurationVersion => PressureMode ? 2 : 1;
        public override string HandlerId => AtmosphereResolvedProperties.Handler;
        protected override IEnumerable<string> Provides => PressureMode ? new[] { "atmosphere", "atmosphere.composition" } : new[] { "atmosphere" };
        protected override IEnumerable<string> IntrinsicRequires => PressureMode ? new[] { "geometry", "physics.binding" } : new[] { "geometry" };
        protected override string ExclusiveSlot => "atmosphere";
        public override CelestialBodyComp CreateComp() => PressureMode ? new CompAtmosphereComposition(Descriptor) : new CompAtmosphere(Descriptor);
        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            var errors = ConfigErrors().ToArray();
            if (errors.Length > 0) return CelestialResult.Reject<ICelestialResolvedProperties>(errors.Select(code => new CelestialError(code, CompKey: key)).ToArray());
            if (!PressureMode)
            {
                double legacyTop = context.SampleDouble("topAltitude", topAltitude.ToSpec().Value);
                return CelestialResult.Ok<ICelestialResolvedProperties>(new AtmosphereResolvedProperties(key,
                    context.SampleDouble("rho0", rho0?.ToSpec().Value ?? DoubleParameterSpec.Fixed(0)),
                    context.SampleDouble("scaleHeight", scaleHeight?.ToSpec().Value ?? DoubleParameterSpec.Fixed(0)), legacyTop));
            }
            var geometry = context.GetResolved<ICelestialGeometryProperties>() ?? throw new ArgumentException("ResolvedGeometryRequired");
            var binding = context.GetResolved<ICelestialPhysicsBindingProperties>() ?? throw new ArgumentException("ResolvedPhysicsRequired");
            double gravity = (binding.GravitationalParameter ?? 0) / (geometry.RadiusMeters * geometry.RadiusMeters);
            var pressure = context.GetDoubleSpecification("pressurePascals", pressurePascals!.ToSpec().Value);
            var temperature = context.GetDoubleSpecification("temperatureKelvin", temperatureKelvin!.ToSpec().Value);
            var altitude = context.GetDoubleSpecification("topAltitude", topAltitude.ToSpec().Value);
            if (pressure.Minimum < 0 || temperature.Minimum <= 0 || altitude.Minimum <= 0) throw new ArgumentException("InvalidPressureAtmosphereInterval");
            var fractions = composition.Select(item => new CelestialGasFraction(item.gas, item.moleFraction)).ToArray();
            var radius = context.TryGetSampledDoubleSpecification(geometry.Key, "radiusMeters", out var radii) ? radii : DoubleParameterSpec.Fixed(geometry.RadiusMeters);
            var gm = context.TryGetSampledDoubleSpecification(binding.Key, "gravitationalParameter", out var gms) ? gms : DoubleParameterSpec.Fixed(binding.GravitationalParameter ?? 0);
            if (context.TryGetSampledDoubleSpecification(binding.Key, "meanDensityKilogramsPerCubicMeter", out var densities))
                gm = DoubleParameterSpec.Uniform(6.67430e-11 * (4.0 / 3.0) * Math.PI * radius.Minimum * radius.Minimum * radius.Minimum * densities.Minimum,
                    6.67430e-11 * (4.0 / 3.0) * Math.PI * radius.Maximum * radius.Maximum * radius.Maximum * densities.Maximum);
            foreach (double p in new[] { pressure.Minimum, pressure.Maximum })
                foreach (double t in new[] { temperature.Minimum, temperature.Maximum })
                    foreach (double g in new[] { gm.Minimum / (radius.Maximum * radius.Maximum), gm.Maximum / (radius.Minimum * radius.Minimum) })
                        _ = new AtmosphereResolvedProperties(key, p, t, altitude.Maximum, g, fractions);
            double top = context.SampleResolvedDouble("topAltitude", altitude);
            return CelestialResult.Ok<ICelestialResolvedProperties>(new AtmosphereResolvedProperties(key,
                context.SampleResolvedDouble("pressurePascals", pressure),
                context.SampleResolvedDouble("temperatureKelvin", temperature), top, gravity, fractions));
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (PressureMode)
            {
                if (composition == null) { yield return "InvalidAtmosphereComposition"; yield break; }
                if (composition.Any(item => item == null || item.HasXmlErrors)) yield return "InvalidAtmosphereComposition";
                if (rho0 != null || scaleHeight != null) yield return "ConflictingAtmosphereForms";
                foreach (var parameter in new[] { pressurePascals, temperatureKelvin, topAltitude })
                {
                    var spec = parameter?.ToSpec();
                    if (spec == null || !spec.Success || spec.Value.Minimum < 0 || parameter != pressurePascals && spec.Value.Minimum == 0)
                        yield return "InvalidAtmosphereParameter";
                }
                bool valid;
                try { AtmosphereResolvedProperties.ValidateComposition(composition.Select(item => new CelestialGasFraction(item.gas, item.moleFraction)).ToArray()); valid = true; }
                catch (Exception exception) when (exception is ArgumentException or NullReferenceException) { valid = false; }
                if (!valid) yield return "InvalidAtmosphereComposition";
            }
            else
            {
                if (topAltitude == null) yield return "InvalidAtmosphereParameter";
                foreach (var parameter in new[] { rho0, scaleHeight, topAltitude })
                { var spec = parameter?.ToSpec(); if (parameter != null && (!spec!.Success || spec.Value.Minimum < 0)) yield return "InvalidAtmosphereParameter"; }
            }
        }
    }
}
