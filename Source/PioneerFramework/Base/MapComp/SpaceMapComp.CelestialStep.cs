using System;

namespace PioneerFramework
{
    public partial class SpaceMapComp
    {
        private bool isPreparedPhysicsCopy;
        private bool? preparedThrustersBurning;
        private CelestialPrivateContainers? celestialBuffers;
        private SpaceMapComp? celestialSource;
        internal SpaceMapComp CopyForCelestialStep(SpaceGameComp host, SpaceMapComp? recycled = null)
        {
            var copy = recycled?.celestialSource == this ? recycled : (SpaceMapComp)MemberwiseClone();
            var containers = copy.celestialBuffers ??= new(); var oldMass = copy.propellantMass;
            CopyCelestialInputFieldsTo(copy); copy.celestialSource = this;
            copy.isPreparedPhysicsCopy = true;
            copy.preparedThrustersBurning = null;
            copy.propellantMass = containers.Map("mass", propellantMass, oldMass);
            var owner = ResolveOwner();
            copy.ownerEntity = owner == null ? null : host.GetEntityById(owner.EntityId)
                ?? throw new InvalidOperationException("ShipOwnerMissing");
            return copy;
        }
        internal Action PrepareCelestialPublication(SpaceMapComp prepared)
            => () => { propellantMass = prepared.propellantMass; totalPropellantMass = prepared.totalPropellantMass; };
        internal void PublishCelestialVisuals(SpaceMapComp prepared)
        {
            if (prepared.preparedThrustersBurning.HasValue) SetThrustersBurning(prepared.preparedThrustersBurning.Value);
        }
    }
}
