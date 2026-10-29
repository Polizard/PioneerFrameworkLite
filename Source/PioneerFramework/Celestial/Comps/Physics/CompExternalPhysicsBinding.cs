using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    public sealed class CompExternalPhysicsBinding : CelestialBodyComp, ICelestialPhysicsBinding
    {
        public CompExternalPhysicsBinding(CelestialCompDescriptor descriptor) : base(descriptor) { }
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
