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
            return CelestialResult.Ok<ICelestialResolvedProperties>(new ExternalPhysicsResolvedProperties(key, gm,
                new PhysicsInitialization(providerId, modeId, initializationVersion, Convert.FromBase64String(initializationPayload))));
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (string.IsNullOrWhiteSpace(providerId)) yield return "MissingPhysicsProvider";
            if (string.IsNullOrWhiteSpace(modeId)) yield return "MissingInitializationMode";
            if (initializationVersion < 1) yield return "InvalidInitializationVersion";
            bool validPayload;
            try { Convert.FromBase64String(initializationPayload); validPayload = true; }
            catch (Exception exception) when (exception is ArgumentException or FormatException) { validPayload = false; }
            if (!validPayload) yield return "InvalidInitializationPayload";
            if (gravitationalParameter != null)
            {
                var specification = gravitationalParameter.ToSpec();
                if (!specification.Success || specification.Value.Minimum < 0) yield return "InvalidGravitationalParameter";
            }
        }
    }
}
