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
    /// **R-7 形态验收**（2026-11-03）：芯级（级 0，标并联点火）+ 4 助推器（级 1，块 0–3）。
    /// 判据：5 个块池、活动集 {1,0}、一次抛级 → 4 个碎片组、母组留芯级；碎片到期按天体记账。
    /// </summary>
    public class R7MultiStageAcceptanceTests
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

        private static PropellantDef Def() => new() { defName = "PF_R7TestProp" };

        private static SpacePart Part(int id, int stage, int block, bool ignite, PropellantDef def,
            DVector3 pos, float hullMass, float tankMass, float capacity)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "PF_Test_Part",
                stageId = stage,
                blockId = block,
                igniteWithUpperStage = ignite,
                positionShip = pos,
                boundsSizeShip = new DVector3(2, 2, 2),
                hp = 100f,
            };
            p.AddComponent(new HullMassComponent { mass = hullMass });
            p.AddComponent(new EngineComponentData
            {
                propellantDefName = def.defName,
                exhaustVelocity = 3000f,
                massFlowRate = 200f,
            });
            if (capacity > 0f)
                p.AddComponent(new PropellantTankComponentData
                {
                    propellantDefName = def.defName,
                    capacityKg = capacity,
                    massKg = tankMass,
                });
            return p;
        }

        [Fact]
        public void R7_CorePlusFourBoosters_ParallelIgnition_AndFourDebrisGroups()
        {
            var def = Def();
            var parts = new List<SpacePart>
            {
                // 芯级：级 0 / 块 0，标并联点火
                Part(1, 0, 0, true, def, new DVector3(0, 0, 0), 500f, 2000f, 2000f),
                Part(2, 0, 0, true, def, new DVector3(0, 0, -3), 300f, 0f, 0f),
                // 4 台助推器：级 1 / 块 0–3（径向对称）
                Part(3, 1, 0, false, def, new DVector3(3, 0, -4), 50f, 400f, 400f),
                Part(4, 1, 1, false, def, new DVector3(-3, 0, -4), 50f, 400f, 400f),
                Part(5, 1, 2, false, def, new DVector3(0, 3, -4), 50f, 400f, 400f),
                Part(6, 1, 3, false, def, new DVector3(0, -3, -4), 50f, 400f, 400f),
            };

            using var w = new World();
            var g = new SpaceEntityGroup(1)
            {
                CurrentOrbit = new OrbitState
                {
                    positionSystem = (SystemVec3)DVector3.zero,
                    velocitySystem = (SystemVec3)new DVector3(0, 0, 0),
                    rotation = DQuaternion.identity,
                },
            };
            var e = new SpaceEntity(1, 0)
            {
                GroupId = 1, HasMap = false, IsMovable = true, IsAffectedByGravity = false, CanInteract = false,
            };
            e.SetParts(parts);
            w.SpaceGame.RegisterGroup(g);
            w.SpaceGame.RegisterEntity(e);

            // 注入测试工质（DefDatabase 里没有 "PF_R7TestProp"）
            Assert.True(PartPropulsionBuilder.BuildForMapless(e, _ => def));

            // C-full：芯级 + 4 助推器 = 5 个块池；活动集 {1,0}
            Assert.Equal(5, e.propulsion.Count);
            Assert.Equal(5, e.Networks.Count);
            var active = StagePartition.ActiveStages(e.PartsList);
            Assert.Contains(0, active);
            Assert.Contains(1, active);

            var created = w.SpaceGame.TrySeparateStageMulti(g, out string reason);
            Assert.Equal("", reason);
            Assert.Equal(4, created.Count);
            Assert.All(created, c => Assert.True(c.IsDebris));
            Assert.All(created, c => Assert.False(c.IsCollidable));
            Assert.Single(g.GetEntityIds());
            Assert.Equal(2, w.SpaceGame.GetEntityById(1)!.Parts.Count);   // 母组只剩芯级两件

            // 碎片到期：4 块 ×（结构 50 + 罐 400）= 1800 kg 记到天体 -1
            w.SpaceGame.RetireDueDebris(5400.0);
            Assert.Equal(1800.0, w.Tracker.GetDebrisMass(-1), 6);
        }
    }
}
