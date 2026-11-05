using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PioneerFramework;
using PioneerFramework.Components;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **批 5e 查询面**（2026-11-01）：`PhysicsQueries` 新增聚合读数的解析与口径。
    ///
    /// 判据：未知组/buffer 清空/无推进源返回空表；舱室件只收未毁的 `IsRoomModulePart`；
    /// 遥测的已毁计数、最小 HP 口径（未毁 + 非无敌 + HP 有限）、成员排序、无推进时 Δv 全零；
    /// 无图组 `TryGetLinkedMap` 为 false。地图/建筑/推进网明细需要真 Def 与地图，留给游戏集成与冒烟。
    /// </summary>
    public class PhysicsQueriesTests
    {
        // ── 世界壳：与 PhysicsApiTests / PhysicsCommandsTests 同款 ──
        private sealed class World : IDisposable
        {
            private readonly Game previous;
            public readonly Game Game;
            public readonly SpaceGameComp SpaceGame;

            public World()
            {
                previous = Current.Game;
                Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
                SpaceGame = new SpaceGameComp(Game);
                Game.components = new List<GameComponent> { SpaceGame };
                Current.Game = Game;
            }

            public void Dispose() => Current.Game = previous;
        }

        private static SpaceEntityGroup Group(int id) => new(id)
        {
            CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)DVector3.zero,
                velocitySystem = (SystemVec3)DVector3.zero,
                rotation = DQuaternion.identity,
            },
        };

        private static SpacePart Part(int partId, float hp = 100f, bool destroyed = false, bool indestructible = false)
            => new()
            {
                partId = partId,
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(1, 1, 1),
                hp = hp,
                isDestroyed = destroyed,
                isIndestructible = indestructible,
            };

        private static SpaceEntity Entity(int entityId, int groupId, params SpacePart[] parts)
        {
            var entity = new SpaceEntity(entityId) { GroupId = groupId };
            entity.SetParts(new List<SpacePart>(parts));
            return entity;
        }

        [Fact]
        public void CopyNetworkOptions_UnknownGroup_ReturnsFalseAndClearsBuffer()
        {
            using var world = new World();
            var buffer = new List<NetworkOptionSnapshot> { default };
            Assert.False(PhysicsQueries.CopyNetworkOptions(999, buffer));
            Assert.Empty(buffer);
        }

        [Fact]
        public void CopyNetworkOptions_GroupWithoutPropulsion_ReturnsTrueAndEmpty()
        {
            using var world = new World();
            world.SpaceGame.RegisterGroup(Group(7));

            var buffer = new List<NetworkOptionSnapshot>();
            Assert.True(PhysicsQueries.CopyNetworkOptions(7, buffer));
            Assert.Empty(buffer);
        }

        [Fact]
        public void CopyRoomParts_UnknownGroup_ReturnsFalseAndClearsBuffer()
        {
            using var world = new World();
            var buffer = new List<RoomPartSnapshot> { default };
            Assert.False(PhysicsQueries.CopyRoomParts(999, buffer));
            Assert.Empty(buffer);
        }

        [Fact]
        public void CopyRoomParts_CollectsOnlyLiveRoomParts()
        {
            using var world = new World();
            world.SpaceGame.RegisterGroup(Group(7));
            var plain = Part(1);
            var room = Part(2);
            room.AddComponent(new RoomComponentData());
            var destroyedRoom = Part(3, destroyed: true);
            destroyedRoom.AddComponent(new RoomComponentData());
            world.SpaceGame.RegisterEntity(Entity(70, 7, plain, room, destroyedRoom));

            var buffer = new List<RoomPartSnapshot>();
            Assert.True(PhysicsQueries.CopyRoomParts(7, buffer));

            var row = Assert.Single(buffer);
            Assert.Equal(2, row.PartId);
            Assert.Equal(70, row.EntityId);
            Assert.Null(row.RoomLabel);   // 未配 roomTypeDefName ⇒ Def 缺失，UI 自己兜底文案
        }

        [Fact]
        public void TryGetLinkedMap_UnknownOrMaplessGroup_ReturnsFalse()
        {
            using var world = new World();
            world.SpaceGame.RegisterGroup(Group(7));
            world.SpaceGame.RegisterEntity(Entity(70, 7, Part(1)));

            Assert.False(PhysicsQueries.TryGetLinkedMap(999, out _));
            Assert.False(PhysicsQueries.TryGetLinkedMap(7, out _));
        }

        [Fact]
        public void TryCollectTelemetry_UnknownGroup_ReturnsFalseAndNull()
        {
            using var world = new World();
            Assert.False(PhysicsQueries.TryCollectTelemetry(999, out var snapshot));
            Assert.Null(snapshot);
        }

        [Fact]
        public void TryCollectTelemetry_PartHealthCaliber()
        {
            using var world = new World();
            world.SpaceGame.RegisterGroup(Group(7));
            world.SpaceGame.RegisterEntity(Entity(70, 7,
                Part(1, hp: 55f),
                Part(2, hp: 100f, destroyed: true),
                Part(3, hp: 9f, indestructible: true)));

            Assert.True(PhysicsQueries.TryCollectTelemetry(7, out var snapshot));
            Assert.NotNull(snapshot);
            Assert.Equal(7, snapshot!.GroupId);
            Assert.Equal(1, snapshot.MemberCount);
            Assert.Equal(3, snapshot.Members[0].PartCount);
            Assert.Equal(1, snapshot.DestroyedParts);
            Assert.Equal(1, snapshot.Members[0].DestroyedPartCount);
            Assert.Equal(55f, snapshot.MinimumPartHp);
            Assert.False(snapshot.HasPropulsion);
            Assert.Equal(0f, snapshot.ReferenceDeltaV);
            Assert.Equal(0.0, snapshot.PropellantMass, 12);
            Assert.Empty(snapshot.Networks);
            Assert.False(snapshot.HasMap);
            Assert.False(snapshot.UnloadedMaps);
        }

        [Fact]
        public void TryCollectTelemetry_SortsMembersByEntityId()
        {
            using var world = new World();
            world.SpaceGame.RegisterGroup(Group(7));
            world.SpaceGame.RegisterEntity(Entity(900, 7, Part(1)));
            world.SpaceGame.RegisterEntity(Entity(700, 7, Part(1)));

            Assert.True(PhysicsQueries.TryCollectTelemetry(7, out var snapshot));
            Assert.Equal(2, snapshot!.MemberCount);
            Assert.Equal(700, snapshot.Members[0].EntityId);
            Assert.Equal(900, snapshot.Members[1].EntityId);
        }

        [Fact]
        public void TryCollectTelemetry_SharedMap_MaplessFirstEntityDoesNotHideLaterMap()
        {
            using var world = new World();
            world.SpaceGame.RegisterGroup(Group(7));

            // 真 Map 的构造依赖游戏世界；这里只把它当"已装载"标记（`TryCollectTelemetry` 只判 null）。
            // `listerThings` 给一个真实的空 Lister（Global 档），让建筑计数分支走真实集合而不是 null 短路。
            var map = (Map)RuntimeHelpers.GetUninitializedObject(typeof(Map));
            map.listerThings = new ListerThings(ListerThingsUse.Global);
            world.SpaceGame.RegisterMapComponent(123, new SpaceMapComp(map));

            // 首个实体：图挂在 part 上（GetMapIds 含 123），但主图/HasMap 不在它身上
            var firstPart = Part(1);
            firstPart.linkedMapId = 123;
            var first = Entity(700, 7, firstPart);
            first.HasMap = false;
            first.MapId = -1;

            // 后续实体：同一张图，且它是主图
            var secondPart = Part(1);
            secondPart.linkedMapId = 123;
            var second = Entity(900, 7, secondPart);
            second.HasMap = true;
            second.MapId = 123;

            // 注：`GetEntityIds()` 当前是 HashSet（按注册顺序遍历）；"撤掉修复必红"已自证本用例能抓到原缺陷。
            world.SpaceGame.RegisterEntity(first);
            world.SpaceGame.RegisterEntity(second);

            Assert.True(PhysicsQueries.TryCollectTelemetry(7, out var snapshot));
            Assert.True(snapshot!.HasMap);
        }
    }
}
