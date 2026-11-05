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
    /// **多块抛级 + 碎片退役的接线判据**（2026-11-03，§1.3a/§1.6）：
    /// 4 块 → 4 个碎片组 + 母组；Keep 拒绝；子级在场拒绝；90 游戏秒（5,400 物理 tick）退役并按 kg 记垃圾。
    /// 纯核守恒/单块等价见 `PioneerFramework.Pure.Tests/StageSeparationCoreTests.cs`。
    /// </summary>
    public class StageSeparationMultiTests
    {
        private sealed class World : IDisposable
        {
            private readonly Game previous;
            public readonly Game Game;
            public readonly SpaceGameComp SpaceGame;
            public readonly SpaceDebrisTracker Tracker;

            public World()
            {
                previous = Current.Game;
                Game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
                SpaceGame = new SpaceGameComp(Game);
                Tracker = new SpaceDebrisTracker(Game);
                Game.components = new List<GameComponent> { SpaceGame, Tracker };
                Current.Game = Game;
            }

            public void Dispose() => Current.Game = previous;
        }

        private static SpacePart Part(int id, int stage, int block, DVector3 pos, float mass,
            StageDisposition disposition = StageDisposition.Jettison, int parentStageId = -1)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "PF_Test_Part",
                stageId = stage,
                blockId = block,
                stageDisposition = disposition,
                parentStageId = parentStageId,
                positionShip = pos,
                boundsSizeShip = new DVector3(2, 2, 2),
                hp = 100f,
            };
            p.AddComponent(new HullMassComponent { mass = mass });
            return p;
        }

        private static SpaceEntityGroup Register(World w, int groupId, int entityId, params SpacePart[] parts)
        {
            var g = new SpaceEntityGroup(groupId)
            {
                CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)DVector3.zero,
                    velocitySystem = (SystemVec3)new DVector3(0, 0, 0),
                    rotation = DQuaternion.identity,
                },
            };
            var e = new SpaceEntity(entityId, 0)
            {
                GroupId = groupId,
                HasMap = false,
                IsMovable = true,
                IsAffectedByGravity = false,
                CanInteract = false,
            };
            e.SetParts(new List<SpacePart>(parts));
            w.SpaceGame.RegisterGroup(g);
            w.SpaceGame.RegisterEntity(e);
            return g;
        }

        /// <summary>芯级（级 0，1000+500kg）+ 4 块（级 1，各 100kg，径向对称）。</summary>
        private static SpacePart[] RocketParts(StageDisposition blockDisposition = StageDisposition.Jettison)
        {
            return new[]
            {
                Part(1, 0, 0, DVector3.zero, 1000f),
                Part(2, 0, 0, new DVector3(0, 0, -2), 500f),
                Part(3, 1, 1, new DVector3(2, 0, -4), 100f, blockDisposition),
                Part(4, 1, 2, new DVector3(-2, 0, -4), 100f, blockDisposition),
                Part(5, 1, 3, new DVector3(0, 2, -4), 100f, blockDisposition),
                Part(6, 1, 4, new DVector3(0, -2, -4), 100f, blockDisposition),
            };
        }

        private static int CountDebris(World w)
        {
            int n = 0;
            foreach (var g in w.SpaceGame.GetAllGroups())
                if (g.IsDebris) n++;
            return n;
        }

        [Fact]
        public void FourBlocks_EmitsFourDebrisGroups_AndKeepsCore()
        {
            using var w = new World();
            var g = Register(w, 1, 1, RocketParts());

            var created = w.SpaceGame.TrySeparateStageMulti(g, out string reason);

            Assert.Equal("", reason);
            Assert.Equal(4, created.Count);
            Assert.All(created, c =>
            {
                Assert.True(c.IsDebris);
                Assert.Equal(DebrisPolicy.Scrap, c.debrisPolicy);
                Assert.False(c.IsCollidable);
                Assert.Equal(5400.0, c.debrisExpireTick, 6);
                Assert.Single(c.GetEntityIds());
            });
            // 4 个新组 id 互异、且都不是母组
            var ids = new HashSet<int>();
            foreach (var c in created) Assert.True(ids.Add(c.GroupId) && c.GroupId != 1);

            // 母组仍在、只剩芯级 2 件
            Assert.Single(g.GetEntityIds());
            Assert.Equal(2, w.SpaceGame.GetEntityById(1)!.Parts.Count);
            Assert.False(g.IsDebris);
            Assert.True(g.IsCollidable);
        }

        [Fact]
        public void KeptStage_IsRejected_AndNothingChanges()
        {
            using var w = new World();
            var g = Register(w, 1, 1, RocketParts(StageDisposition.Keep));

            var created = w.SpaceGame.TrySeparateStageMulti(g, out string reason);

            Assert.Empty(created);
            Assert.Equal("stage-kept", reason);
            Assert.Equal(6, w.SpaceGame.GetEntityById(1)!.Parts.Count);
            Assert.Equal(0, CountDebris(w));
        }

        [Fact]
        public void AttachedChildStage_BlocksParentJettison()
        {
            using var w = new World();
            var parts = RocketParts();
            parts[0].parentStageId = 1;   // 级 0 挂在级 1 上 ⇒ 级 0 是子级，级 1（父）不能抛
            var g = Register(w, 1, 1, parts);

            var created = w.SpaceGame.TrySeparateStageMulti(g, out string reason);

            Assert.Empty(created);
            Assert.Equal("child-stage-attached", reason);
            Assert.Equal(6, w.SpaceGame.GetEntityById(1)!.Parts.Count);
        }

        [Fact]
        public void Debris_RetiresAt5400_AndCountsMassOnce()
        {
            using var w = new World();
            var g = Register(w, 1, 1, RocketParts());
            var created = w.SpaceGame.TrySeparateStageMulti(g, out _);
            Assert.Equal(4, created.Count);

            w.SpaceGame.RetireDueDebris(5399.0);
            Assert.Equal(4, CountDebris(w));                  // 未到期
            Assert.Equal(0.0, w.Tracker.GetDebrisMass(-1), 6);

            w.SpaceGame.RetireDueDebris(5400.0);
            Assert.Equal(0, CountDebris(w));                  // 到期全退役
            Assert.Equal(400.0, w.Tracker.GetDebrisMass(-1), 6);   // 4 × 100kg，恰好一次

            w.SpaceGame.RetireDueDebris(9999.0);
            Assert.Equal(400.0, w.Tracker.GetDebrisMass(-1), 6);   // 不重复记账
        }

        [Fact]
        public void SingleBlock_StillEmitsOneGroup_WithDebrisFlags()
        {
            using var w = new World();
            var g = Register(w, 1, 1,
                Part(1, 0, 0, DVector3.zero, 1000f),
                Part(2, 1, 0, new DVector3(0, 0, -4), 100f));

            var created = w.SpaceGame.TrySeparateStageMulti(g, out string reason);

            Assert.Equal("", reason);
            var single = Assert.Single(created);
            Assert.True(single.IsDebris);
            Assert.Equal(DebrisPolicy.Scrap, single.debrisPolicy);
            Assert.False(single.IsCollidable);
            Assert.Equal(5400.0, single.debrisExpireTick, 6);
            Assert.Single(w.SpaceGame.GetEntityById(1)!.Parts);   // 母组留级 0 一件
        }
    }
}
