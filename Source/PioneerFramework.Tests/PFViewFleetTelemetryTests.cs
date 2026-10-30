using System;
using System.Runtime.CompilerServices;
using PioneerFramework.Components;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    public class PFViewFleetTelemetryTests
    {
        private static bool Collect(SpaceGameComp space, int id, int faction, out PFViewFleetSnapshot snapshot)
            => PFViewFleetTelemetry.TryCollect(space, null, id, faction, out snapshot);

        [Fact]
        public void OnlyActivePlayerOwnedGroupsExposeDetailedTelemetry()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            Assert.False(Collect(host.Space, group.GroupId, 3, out _));
            Assert.False(Collect(host.Space, 999, 2, out _));
            Assert.True(Collect(host.Space, group.GroupId, 2, out _));
            group.IsActive = false;
            Assert.False(Collect(host.Space, group.GroupId, 2, out _));
        }

        [Fact]
        public void SummaryCountsMembersOnceAndIgnoresStaleMembershipAndInactiveNodes()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            var entity = new SpaceEntity(31, 2) { GroupId = group.GroupId };
            entity.AddPart(new SpacePart { partId = 1, hp = 500 }, false);
            entity.AddPart(new SpacePart { partId = 2, hp = 0, isDestroyed = true }, false);
            host.Space.RegisterEntity(entity);
            var stale = new SpaceEntity(32, 2) { GroupId = 99 };
            stale.AddPart(new SpacePart { partId = 1, hp = 0, isDestroyed = true }, false);
            host.Space.RegisterEntity(stale);
            group.AddEntity(stale.EntityId);
            group.plannedManeuvers.Add(new() { active = false, executeTick = 40 });
            group.plannedManeuvers.Add(new() { active = true, executeTick = 300 });
            group.plannedManeuvers.Add(new() { active = true, executeTick = 200 });
            Assert.True(Collect(host.Space, group.GroupId, 2, out var snapshot));
            Assert.Equal(1, snapshot.DestroyedParts);
            Assert.Equal(1, snapshot.MemberCount);
            Assert.Equal(500f, snapshot.MinimumPartHp);
            Assert.Equal(200d, snapshot.NextNodeTick);
            Assert.Equal(2, snapshot.ManeuverCount);
        }

        [Fact]
        public void MainMapWithoutPartLinksRetainsItsShortcutAndArchivedMapsRemainUnavailable()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.uniqueID = 41;
            host.Space.RegisterMapComponent(41, new SpaceMapComp(map));
            var entity = new SpaceEntity(31, 2) { GroupId = group.GroupId, HasMap = true, MapId = 41 };
            host.Space.RegisterEntity(entity);
            Assert.True(Collect(host.Space, group.GroupId, 2, out var loaded));
            Assert.True(loaded.HasMap);
            Assert.False(loaded.UnloadedMaps);
            entity.IsMapArchived = true;
            Assert.True(Collect(host.Space, group.GroupId, 2, out var archived));
            Assert.False(archived.HasMap);
            Assert.True(archived.UnloadedMaps);
        }

        [Fact]
        public void SnapshotUsesGroupPropulsionAndDoesNotConsumeFuel()
        {
            using var host = new SpaceAiRuntimeTestHost();
            var group = host.AddShip();
            var propellant = new PropellantDef { defName = "FleetTestFuel", label = "Fuel" };
            var entity = new SpaceEntity(31, 2) { GroupId = group.GroupId };
            var part = new SpacePart { partId = 1, hp = 100 };
            part.AddComponent(new HullMassComponent { mass = 100 });
            entity.AddPart(part, false);
            entity.AddPropulsion(new PropulsionData { propellant = propellant, ve = 1000, massFlow = 1, mass = 10, capacity = 100 });
            host.Space.RegisterEntity(entity);
            Assert.True(Collect(host.Space, group.GroupId, 2, out var snapshot));
            Assert.True(snapshot.HasPropulsion);
            Assert.Equal(10d, snapshot.PropellantMass);
            Assert.Equal(100d, snapshot.PropellantCapacity);
            Assert.InRange(snapshot.ReferenceDeltaV, 95.30f, 95.32f);
            Assert.Equal(PFViewFleetAlert.LowPropellant, snapshot.Alerts);
            Assert.Single(snapshot.Networks);
            Assert.Equal(10d, entity.GetPropellantMass(propellant));
        }
    }
}
