using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Components;

namespace PioneerFramework
{
    public partial class SpacePart
    {
        private CelestialPrivateContainers? celestialBuffers;
        private SpacePart? celestialSource;
        internal SpacePart CopyForCelestialStep(SpacePart? recycled = null)
        {
            var copy = recycled?.celestialSource == this ? recycled : (SpacePart)MemberwiseClone();
            var containers = copy.celestialBuffers ??= new(); var previous = copy.components;
            CopyCelestialInputFieldsTo(copy); copy.celestialSource = this;
            // Only fuel distribution is mutable during a physical interval. Other component data is read-only.
            copy.components = containers.Project("components", components, previous, (component, old) => {
                if (component is not PropellantTankComponentData tank) return component;
                if (tank.GetType() != typeof(PropellantTankComponentData) || old is not PropellantTankComponentData target || target.GetType() != tank.GetType() || ReferenceEquals(target, tank))
                    return tank.Clone() ?? throw new InvalidOperationException("FuelComponentCloneFailed");
                target.propellantDefName = tank.propellantDefName; target.capacityKg = tank.capacityKg; target.massKg = tank.massKg; target.leaked = tank.leaked;
                return target;
            });
            return copy;
        }
    }

    public partial class SpaceEntity
    {
        private SpaceGameComp? preparedPhysicsHost;
        private CelestialPrivateContainers? celestialBuffers;
        private SpaceEntity? celestialSource;
        internal SpaceEntity CopyForCelestialStep(SpaceGameComp host, SpaceEntity? recycled = null)
        {
            var copy = recycled?.celestialSource == this ? recycled : (SpaceEntity)MemberwiseClone();
            var containers = copy.celestialBuffers ??= new();
            var oldParts = copy.parts; var oldPropulsion = copy.propulsion; var oldMaps = copy.cachedPartLinkedMapIds;
            var oldPlans = copy.fuelPlans; var oldNetworks = copy.cachedPropulsionNetworks;
            CopyCelestialInputFieldsTo(copy); copy.celestialSource = this;
            copy.preparedPhysicsHost = host;
            copy.parts = containers.Project("parts", parts, oldParts, (part, old) => part.CopyForCelestialStep(old));
            copy.propulsion = containers.Project("propulsion", propulsion, oldPropulsion, (data, old) => data.CopyForCelestialStep(old));
            copy.cachedPartLinkedMapIds = containers.List("maps", cachedPartLinkedMapIds, oldMaps);
            copy.fuelPlans = containers.Choose("plans", fuelPlans, oldPlans, () => new()); copy.fuelPlans.Clear();
            copy.cachedPropulsionNetworks = containers.Choose("networks", cachedPropulsionNetworks, oldNetworks, () => new()); copy.cachedPropulsionNetworks.Clear();
            copy.propulsionDirty = true;
            return copy;
        }

        // Preparation has already checked shape and allocated the assignments. Preserve live part/component identities.
        internal Action PrepareCelestialPublication(SpaceEntity prepared)
        {
            if (parts.Count != prepared.parts.Count || propulsion.Count != prepared.propulsion.Count)
                throw new InvalidOperationException("ShipCompositionChanged");
            var tanks = new List<(PropellantTankComponentData Target, double Mass)>();
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].partId != prepared.parts[i].partId || parts[i].components.Count != prepared.parts[i].components.Count)
                    throw new InvalidOperationException("ShipCompositionChanged");
                for (int j = 0; j < parts[i].components.Count; j++)
                    if (parts[i].components[j] is PropellantTankComponentData tank)
                        tanks.Add((tank, ((PropellantTankComponentData)prepared.parts[i].components[j]).massKg));
            }
            var propellants = propulsion.Select((data, index) => (Target: data, Mass: prepared.propulsion[index].mass)).ToArray();
            var tankAssignments = tanks.ToArray();
            return () => {
                foreach (var assignment in tankAssignments) assignment.Target.massKg = assignment.Mass;
                foreach (var assignment in propellants) assignment.Target.mass = assignment.Mass;
                totalPropulsionMass = prepared.totalPropulsionMass;
                tankMassVersion = prepared.tankMassVersion;
                fuelPlanVersion = prepared.fuelPlanVersion;
                Rotation = prepared.rotation;
            };
        }
    }

    public partial class PropulsionData
    {
        internal PropulsionData CopyForCelestialStep(PropulsionData? recycled = null)
        { var copy = recycled != null && !ReferenceEquals(recycled, this) ? recycled : (PropulsionData)MemberwiseClone(); CopyCelestialInputFieldsTo(copy); return copy; }
    }
}
