using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>现有半径/GM 的数据接口；运动模式及初始化载荷由外部物理提供者解释。</summary>
    public sealed class CompProperties_ExternalPhysicsBinding : CelestialCompProperties
    {
        public string providerId = "";
        public string modeId = "";
        public int initializationVersion = 1;
        public string initializationPayload = "";
        public CelestialDoubleParameterXml? gravitationalParameter;
        public CelestialDoubleParameterXml? meanDensityKilogramsPerCubicMeter;
        public CelestialMotionXml? motion;
        public override string HandlerId => ExternalPhysicsResolvedProperties.Handler;
        protected override IEnumerable<string> Provides => new[] { "physics.binding" };
        protected override IEnumerable<string> IntrinsicRequires => new[] { "geometry" };
        protected override string ExclusiveSlot => "physics.binding";
        public override CelestialBodyComp CreateComp() => new CompExternalPhysicsBinding(Descriptor);

        public override CelestialResult<ICelestialResolvedProperties> Resolve(CelestialGenerationContext context)
        {
            string[] errors = ConfigErrors().ToArray();
            if (errors.Length > 0) return CelestialResult.Reject<ICelestialResolvedProperties>(
                errors.Select(error => new CelestialError(error, CompKey: key)).ToArray());
            double? gm = gravitationalParameter == null ? null : context.SampleDouble("gravitationalParameter", gravitationalParameter.ToSpec().Value);
            if (meanDensityKilogramsPerCubicMeter != null)
            {
                var geometry = context.GetResolved<ICelestialGeometryProperties>() ?? throw new ArgumentException("ResolvedGeometryRequired");
                double radius = geometry.RadiusMeters;
                var densitySpec = context.GetDoubleSpecification("meanDensityKilogramsPerCubicMeter", meanDensityKilogramsPerCubicMeter.ToSpec().Value);
                if (densitySpec.Minimum <= 0) throw new ArgumentException("InvalidMeanDensity");
                var radiusSpec = context.TryGetSampledDoubleSpecification(geometry.Key, "radiusMeters", out var interval) ? interval : DoubleParameterSpec.Fixed(radius);
                foreach (double r in new[] { radiusSpec.Minimum, radiusSpec.Maximum })
                    foreach (double d in new[] { densitySpec.Minimum, densitySpec.Maximum })
                    {
                        double derived = 6.67430e-11 * (4.0 / 3.0) * Math.PI * r * r * r * d;
                        if (!double.IsFinite(derived) || derived <= 0) throw new ArgumentException("InvalidDerivedGravitationalParameterInterval");
                    }
                double density = context.SampleResolvedDouble("meanDensityKilogramsPerCubicMeter", densitySpec);
                gm = 6.67430e-11 * (4.0 / 3.0) * Math.PI * radius * radius * radius * density;
            }
            var initialization = motion == null ? new PhysicsInitialization(providerId, modeId, initializationVersion, Convert.FromBase64String(initializationPayload)) : motion.Compile(context);
            return CelestialResult.Ok<ICelestialResolvedProperties>(new ExternalPhysicsResolvedProperties(key, gm, initialization));
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (string.IsNullOrWhiteSpace(providerId)) yield return "MissingPhysicsProvider";
            if (motion == null && string.IsNullOrWhiteSpace(modeId)) yield return "MissingInitializationMode";
            if (initializationVersion < 1) yield return "InvalidInitializationVersion";
            bool validPayload;
            try { Convert.FromBase64String(initializationPayload); validPayload = true; }
            catch (Exception exception) when (exception is ArgumentException or FormatException) { validPayload = false; }
            if (!validPayload) yield return "InvalidInitializationPayload";
            if (motion != null)
            {
                if (providerId != ExistingCelestialEphemerisBridge.Id || !string.IsNullOrEmpty(modeId) || !string.IsNullOrEmpty(initializationPayload) || initializationVersion != 1)
                    yield return "ConflictingMotionInitialization";
                foreach (var error in motion.ConfigErrors()) yield return error;
            }
            if (meanDensityKilogramsPerCubicMeter != null)
            {
                if (gravitationalParameter != null) yield return "ConflictingGravitationalParameters";
                var density = meanDensityKilogramsPerCubicMeter.ToSpec();
                if (!density.Success || density.Value.Minimum <= 0) yield return "InvalidMeanDensity";
            }
            if (gravitationalParameter != null)
            {
                var specification = gravitationalParameter.ToSpec();
                if (!specification.Success || specification.Value.Minimum < 0) yield return "InvalidGravitationalParameter";
            }
        }
    }
}
