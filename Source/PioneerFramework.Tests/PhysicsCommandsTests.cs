using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PioneerFramework;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **批 5e 命令面的解析语义**（2026-11-01）：`PhysicsCommands` 新增的四条命令
    /// 只做“组 id / 部件 id → 宿主对象”的解析，再把语义交给既有宿主方法。
    ///
    /// 判据（不重复测宿主行为，宿主行为由 `AutoStageSwitchTests` / `StageSeparationAcceptanceRunner` 覆盖）：
    /// - 组不存在 ⇒ false + 明确 reason，`newGroupId = -1`；
    /// - `partId` 解析：找得到 ⇒ 宿主的原因透传（证明解析成功），找不到 ⇒ `bay-part-not-found`；
    /// - `SetAutoStage` 落组态 + 建/撤计划；
    /// - `EnterRoomMap` 对非舱室件返回 false（不触碰地图/相机）。
    /// </summary>
    public class PhysicsCommandsTests
    {
        // ── 世界壳：与 PhysicsApiTests / AutoStageSwitchTests 同款 ──
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

        /// <summary>注册组 + 一个带指定 partId 的实体（非舱室件、非载荷舱）。</summary>
        private static void RegisterShipWithPart(World world, int groupId, int partId)
        {
            world.SpaceGame.RegisterGroup(Group(groupId));
            var entity = new SpaceEntity(700)
            {
                GroupId = groupId,
                HasMap = false,
            };
            entity.SetParts(new List<SpacePart>
            {
                new SpacePart
                {
                    partId = partId,
                    positionShip = DVector3.zero,
                    boundsSizeShip = new DVector3(1, 1, 1),
                    hp = 10f,
                },
            });
            world.SpaceGame.RegisterEntity(entity);
        }

        [Fact]
        public void SeparateStage_UnknownGroup_ReportsGroupNotFound()
        {
            using var world = new World();

            Assert.False(PhysicsCommands.SeparateStage(999, out int newGroupId, out string reason));

            Assert.Equal(-1, newGroupId);
            Assert.Equal("group-not-found", reason);
        }

        [Fact]
        public void SeparateStage_EmptyGroup_PassesHostReasonThrough()
        {
            using var world = new World();
            world.SpaceGame.RegisterGroup(Group(7));   // 无实体 ⇒ 无零件 ⇒ 宿主判 single-stage

            Assert.False(PhysicsCommands.SeparateStage(7, out int newGroupId, out string reason));

            Assert.Equal(-1, newGroupId);
            Assert.Equal("single-stage", reason);
        }

        [Fact]
        public void DeployPayload_UnknownGroupOrPart_ReportsWhy()
        {
            using var world = new World();
            RegisterShipWithPart(world, 7, partId: 5);

            Assert.False(PhysicsCommands.DeployPayload(999, 5, out int unknownGroupId, out string groupReason));
            Assert.Equal(-1, unknownGroupId);
            Assert.Equal("group-not-found", groupReason);

            Assert.False(PhysicsCommands.DeployPayload(7, 999, out int unknownPartId, out string partReason));
            Assert.Equal(-1, unknownPartId);
            Assert.Equal("bay-part-not-found", partReason);
        }

        [Fact]
        public void DeployPayload_KnownPart_ResolvesToHostReason()
        {
            using var world = new World();
            RegisterShipWithPart(world, 7, partId: 5);

            // partId 解析成功 ⇒ 宿主报"这部件不是载荷舱"，而不是 "bay-part-not-found"。
            Assert.False(PhysicsCommands.DeployPayload(7, 5, out _, out string reason));
            Assert.Equal("no-payload-design-id", reason);
        }

        [Fact]
        public void SetAutoStage_UnknownGroup_ReturnsFalse()
        {
            using var world = new World();
            Assert.False(PhysicsCommands.SetAutoStage(999, true));
        }

        [Fact]
        public void SetAutoStage_KnownGroup_FlipsFlagAndPlans()
        {
            using var world = new World();
            world.SpaceGame.RegisterGroup(Group(7));

            Assert.True(PhysicsCommands.SetAutoStage(7, true));
            var group = world.SpaceGame.GetGroupById(7)!;
            Assert.True(group.autoStageEnabled);
            var plan = Assert.Single(world.SpaceGame.autoStagePlans);
            Assert.Equal(7, plan.GroupId);

            Assert.True(PhysicsCommands.SetAutoStage(7, false));
            Assert.False(group.autoStageEnabled);
            Assert.Empty(world.SpaceGame.autoStagePlans);
        }

        [Fact]
        public void EnterRoomMap_UnknownGroupOrPart_ReturnsFalse()
        {
            using var world = new World();
            RegisterShipWithPart(world, 7, partId: 5);

            Assert.False(PhysicsCommands.EnterRoomMap(999, 5));
            Assert.False(PhysicsCommands.EnterRoomMap(7, 999));

            // 已知件但不是舱室件：GenerateRoomMap 自身拒绝，命令不碰地图/相机。
            Assert.False(PhysicsCommands.EnterRoomMap(7, 5));
        }
    }
}
