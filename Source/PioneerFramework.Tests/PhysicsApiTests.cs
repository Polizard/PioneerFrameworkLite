using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PioneerFramework;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **物理引擎对外快照面**（2026-10-31，类重组批 2）。
    ///
    /// 判据：`PhysicsApi` 取到的快照必须与组上的**真实字段逐项相等**；未知组返回 false；
    /// 批量路径写 buffer 且清空旧内容；命令/查询对未知组不抛异常。
    /// 壳外只该看到快照/命令/查询——本测试同时是“快照字段没漏”的清单。
    /// </summary>
    public class PhysicsApiTests
    {
        // ── 世界壳：与 AutoStageSwitchTests / CollisionPlanValidationBench 同款 ──
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
            FactionId = 2,
            CelestialBodyId = 3,
            IsActive = true,
            IsMovable = true,
            IsStealth = true,
            IsCrippled = true,
            IsCircularOrbit = true,
            OrbitDirty = true,
            HasActiveBurn = true,
            ActivePingEnabled = true,
            transferActive = true,
            HasRendezvousGuidance = true,
            rendezvousArrivalTick = 123.5,
            LandingTaskId = "land-42",
            propulsionSelection = new PropulsionSelection { enabledNetworkDefName = "PF_Propellant_Chemical" },
            angularVelocity = new DVector3(0.1, 0.2, 0.3),
            circularOrbitNormal = new DVector3(0, 1, 0),
            pendingImpulse = new DVector3(5, 0, 0),
            CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)new DVector3(1000, 2000, 3000),
                velocitySystem = (SystemVec3)new DVector3(4, 5, 6),
                rotation = DQuaternion.identity,
            },
        };

        [Fact]
        public void TryGetSpacecraft_MapsEverySnapshotField()
        {
            using var world = new World();
            var group = Group(7);
            group.plannedManeuvers.Add(new SpaceEntityGroup.PlannedManeuver { active = true, executeTick = 50 });
            group.plannedManeuvers.Add(new SpaceEntityGroup.PlannedManeuver { active = false, executeTick = 10 });
            world.SpaceGame.RegisterGroup(group);

            Assert.True(PhysicsApi.TryGetSpacecraft(7, out var s));

            Assert.Equal(7, s.GroupId);
            Assert.Equal(2, s.FactionId);
            Assert.Equal(3, s.CelestialBodyId);
            Assert.Equal(0, s.EntityCount);
            Assert.Equal(1, s.PlannedManeuverCount);
            Assert.Equal(50, s.NextManeuverTick, 12);
            Assert.Equal(123.5, s.RendezvousArrivalTick, 12);

            Assert.True(s.IsActive);
            Assert.True(s.IsMovable);
            Assert.True(s.IsStealth);
            Assert.True(s.IsCrippled);
            Assert.True(s.IsCircularOrbit);
            Assert.True(s.OrbitDirty);
            Assert.True(s.HasActiveBurn);
            Assert.True(s.HasRendezvousGuidance);
            Assert.True(s.TransferActive);
            Assert.True(s.ActivePingEnabled);
            Assert.True(s.IsControllable);
            Assert.False(s.HasApproachGuidance);

            Assert.Equal(1000.0, s.PositionSystem.x, 12);
            Assert.Equal(2000.0, s.PositionSystem.y, 12);
            Assert.Equal(3000.0, s.PositionSystem.z, 12);
            Assert.Equal(4.0, s.VelocitySystem.x, 12);
            Assert.Equal(5.0, s.VelocitySystem.y, 12);
            Assert.Equal(6.0, s.VelocitySystem.z, 12);
            Assert.True(s.Rotation.Equals(DQuaternion.identity));
            Assert.Equal(0.3, s.AngularVelocity.z, 12);
            Assert.Equal(1.0, s.CircularOrbitNormal.y, 12);
            Assert.Equal(5.0, s.PendingImpulse.x, 12);

            Assert.Equal("land-42", s.LandingTaskId);
            Assert.Equal("PF_Propellant_Chemical", s.EnabledNetworkDefName);
        }

        [Fact]
        public void TryGetSpacecraft_UnknownGroup_ReturnsFalse()
        {
            using var world = new World();
            Assert.False(PhysicsApi.TryGetSpacecraft(999, out var s));
            Assert.Equal(0, s.GroupId);
        }

        [Fact]
        public void CopySpacecrafts_ClearsBufferAndWritesAllGroups()
        {
            using var world = new World();
            world.SpaceGame.RegisterGroup(Group(1));
            world.SpaceGame.RegisterGroup(Group(2));

            var buffer = new List<SpacecraftSnapshot> { default, default, default };
            int count = PhysicsApi.CopySpacecrafts(buffer);

            Assert.Equal(2, count);
            Assert.Equal(2, buffer.Count);
            Assert.Contains(buffer, s => s.GroupId == 1);
            Assert.Contains(buffer, s => s.GroupId == 2);
        }

        [Fact]
        public void TryCopyParts_FlattensRegisteredEntityParts()
        {
            using var world = new World();
            var group = Group(7);
            world.SpaceGame.RegisterGroup(group);

            var entity = new SpaceEntity(70) { GroupId = 7, MapId = 41 };
            entity.SetParts(new List<SpacePart>
            {
                new SpacePart
                {
                    partId = 1,
                    positionShip = new DVector3(1, 2, 3),
                    boundsSizeShip = new DVector3(4, 5, 6),
                    stageId = 2,
                    hp = 55,
                },
                new SpacePart
                {
                    partId = 2,
                    positionShip = new DVector3(-1, 0, 0),
                    boundsSizeShip = new DVector3(2, 2, 2),
                    isDestroyed = true,
                },
            });
            world.SpaceGame.RegisterEntity(entity);

            var buffer = new List<PartSnapshot> { default, default, default };
            Assert.True(PhysicsApi.TryCopyParts(7, buffer));

            Assert.Equal(2, buffer.Count);
            Assert.Equal(1, buffer[0].PartId);
            Assert.Equal(70, buffer[0].EntityId);
            Assert.Equal(41, buffer[0].MapId);
            Assert.Equal(2, buffer[0].StageId);
            Assert.Equal(1.0, buffer[0].PositionShip.x, 12);
            Assert.Equal(6.0, buffer[0].BoundsSizeShip.z, 12);
            Assert.Equal(55f, buffer[0].Hp);
            Assert.False(buffer[0].IsDestroyed);
            Assert.True(buffer[1].IsDestroyed);
        }

        [Fact]
        public void TryCopyParts_UnknownGroup_ReturnsFalseAndClearsBuffer()
        {
            using var world = new World();
            var buffer = new List<PartSnapshot> { default };
            Assert.False(PhysicsApi.TryCopyParts(999, buffer));
            Assert.Empty(buffer);
        }

        [Fact]
        public void Commands_UnknownGroup_ReturnFalseWithoutThrowing()
        {
            using var world = new World();
            Assert.False(PhysicsCommands.SetBurnNetwork(999, "PF_Propellant_Chemical", false));
            Assert.False(PhysicsCommands.EnterCircularOrbit(999, 100, 3));
            Assert.False(PhysicsCommands.ExitCircularOrbit(999));
            Assert.False(PhysicsCommands.AddPendingImpulse(999, new DVector3(1, 0, 0)));
        }

        [Fact]
        public void Queries_ThrustAxis_UsesShipAxisRotatedByAttitude()
        {
            using var world = new World();
            world.SpaceGame.RegisterGroup(Group(7));

            Assert.True(PhysicsQueries.TryGetThrustAxis(7, ThrustAxis.Forward, out var forward));
            Assert.Equal(0.0, forward.x, 12);
            Assert.Equal(0.0, forward.y, 12);
            Assert.Equal(1.0, forward.z, 12);

            Assert.False(PhysicsQueries.TryGetThrustAxis(999, ThrustAxis.Forward, out _));
            Assert.False(PhysicsQueries.TryGetCircularOrbitPosition(999, 0, out _));
            Assert.False(PhysicsQueries.TryGetRadarBoresight(999, 0, out _));
        }
    }
}
