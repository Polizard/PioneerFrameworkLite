using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    public sealed class CompExternalPhysicsBinding : CelestialBodyComp, ICelestialPhysicsBinding, ICelestialDayLengthSource
    {
        private double dayLengthTicks = double.NaN;
        public CompExternalPhysicsBinding(CelestialCompDescriptor descriptor) : base(descriptor) { }
        /// <summary>
        /// 物理初始化解析出的权威日长；外部 provider（非既有星历）/解析失败时为 0，环境采样退回 profile 值。
        /// 只读且不推进模拟；结果在附着后缓存一次。
        /// </summary>
        public double DayLengthTicks
        {
            get
            {
                if (!double.IsNaN(dayLengthTicks)) return dayLengthTicks;
                if (!IsAttached || Context.Properties is not ICelestialPhysicsBindingProperties properties) return 0;
                double value = 0;
                var parsed = ExistingEphemerisParameters.Parse(properties.Initialization);
                if (parsed.Success && double.IsFinite(parsed.Value.DayLengthTicks) && parsed.Value.DayLengthTicks > 0)
                    value = parsed.Value.DayLengthTicks;
                dayLengthTicks = value;
                return value;
            }
        }
        public CelestialResult<CelestialBodyRegistration> CreateRegistration(CelestialBodyDescription description, CelestialInstanceId? parentId,
            IReadOnlyDictionary<string, CelestialInstanceId> initializationReferences)
        {
            if (Context.Properties is not ICelestialPhysicsBindingProperties properties || description.Id != Context.InstanceId ||
                description.GravitationalParameter != properties.GravitationalParameter)
                return CelestialResult.Reject<CelestialBodyRegistration>(new CelestialError("InvalidDescriptor", description.Id, Descriptor.Key));
            return CelestialResult.Ok(new CelestialBodyRegistration(description.DefinitionId,
                new CelestialPhysicsDescriptor(description.Id, description.DescriptorRevision, description.RadiusMeters, properties.GravitationalParameter),
                properties.Initialization, parentId, initializationReferences, description.Handle));
        }
    }
}
