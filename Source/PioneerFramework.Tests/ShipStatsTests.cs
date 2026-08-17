using System;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    public class ShipStatsTests
    {
        [Fact]
        public void ComponentRegistry_RegistersRoomAndCreatesByTypeId()
        {
            // Access the type to force its static constructor to run.
            Assert.NotNull(typeof(RoomComponentData));

            Assert.True(ComponentRegistry.Count >= 1);
            Assert.True(ComponentRegistry.IsRegistered("RoomComponentData"));

            var component = ComponentRegistry.Create("RoomComponentData");
            Assert.NotNull(component);
            Assert.Equal("RoomComponentData", component.TypeId);
        }

        [Fact]
        public void SpacePart_AggregatesRoomMass()
        {
            var part = new SpacePart();
            var room = new RoomComponentData
            {
                roomTypeDefName = "PF_RoomModule",
                dryMass = 400f
            };
            part.components.Add(room);

            var stats = part.Stats;
            Assert.Equal(400f, stats.mass, 3);
        }

        [Fact]
        public void SpacePart_RemovingComponentInvalidatesStats()
        {
            var part = new SpacePart();
            var room = new RoomComponentData { dryMass = 400f };
            part.components.Add(room);

            Assert.Equal(400f, part.Stats.mass, 3);

            Assert.True(part.RemoveComponent(room));

            // With no components, the stats aggregator falls back to 1kg.
            Assert.Equal(1f, part.Stats.mass, 3);
        }

        [Fact]
        public void RoomComponentData_CloneReturnsDeepCopy()
        {
            var room = new RoomComponentData
            {
                roomTypeDefName = "PF_RoomModule",
                dryMass = 400f
            };

            var clone = room.Clone();

            Assert.NotNull(clone);
            Assert.NotSame(room, clone);
            var clonedRoom = Assert.IsType<RoomComponentData>(clone);
            Assert.Equal("PF_RoomModule", clonedRoom.roomTypeDefName);
            Assert.Equal(400f, clonedRoom.dryMass, 3);
        }

        [Fact]
        public void SpacePart_AddComponentInvalidatesAndVersionChanges()
        {
            var part = new SpacePart();
            var room = new RoomComponentData { dryMass = 400f };
            part.components.Add(room);
            var statsBefore = part.Stats;
            int versionBefore = part.StatsVersion;
            Assert.Equal(400f, statsBefore.mass, 3);

            part.AddComponent(new RoomComponentData { dryMass = 100f });

            Assert.Equal(500f, part.Stats.mass, 3);
            Assert.True(part.StatsVersion > versionBefore);
        }
    }
}
