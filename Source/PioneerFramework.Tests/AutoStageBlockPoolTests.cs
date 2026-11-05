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
    /// **自动抛级（C-full 块罐占比 + 整级事件）**（2026-11-03，§1.7）：
    /// 候选 = 最大级；该级**所有块**都低于阈值才抛整级；单块级保持旧行为。
    /// </summary>
    public class AutoStageBlockPoolTests
    {
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

        private static SpacePart Part(int id, int stage, int block, float tankMass, float capacity)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "PF_Test_Part",
                stageId = stage,
                blockId = block,
                positionShip = new DVector3(0, 0, -stage * 2 - block * 2),
                boundsSizeShip = new DVector3(2, 2, 2),
                hp = 100f,
            };
            p.AddComponent(new HullMassComponent { mass = 100f });
            p.AddComponent(new EngineComponentData
            {
                propellantDefName = "P",
                exhaustVelocity = 3000f,
                massFlowRate = 10f,
            });
            if (capacity > 0f)
                p.AddComponent(new PropellantTankComponentData
                {
                    propellantDefName = "P",
                    capacityKg = capacity,
                    massKg = tankMass,
                });
            return p;
        }

        private static SpaceEntityGroup Register(World w, int groupId, params SpacePart[] parts)
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
            var e = new SpaceEntity(1, 0)
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

        private static int CountDebris(World w)
        {
            int n = 0;
            foreach (var g in w.SpaceGame.GetAllGroups())
                if (g.IsDebris) n++;
            return n;
        }

        [Fact]
        public void AutoStage_DropsWholeStageOnlyWhenAllBlocksLow()
        {
            using var w = new World();
            var g = Register(w, 1,
                Part(1, 0, 0, tankMass: 500, capacity: 500),   // 芯级满油（不是候选级）
                Part(2, 1, 1, tankMass: 10, capacity: 100),    // 块 1 低
                Part(3, 1, 2, tankMass: 100, capacity: 100));  // 块 2 满
            w.SpaceGame.AddAutoStageFuelPlan(1, fuelFraction: 0.5f, stages: 2, minSeconds: 0.0);

            w.SpaceGame.StepAutoStagePlans(1.0);
            Assert.Equal(0, CountDebris(w));   // 只有一块低 ⇒ 整级不抛

            w.SpaceGame.GetEntityById(1)!.GetPartById(3)!
                .GetComponent<PropellantTankComponentData>()!.massKg = 10f;
            w.SpaceGame.StepAutoStagePlans(1.0);

            Assert.Equal(2, CountDebris(w));   // 两块都低 ⇒ 整级 2 块一起抛
            Assert.Single(g.GetEntityIds());   // 母组仍在
        }

        [Fact]
        public void AutoStage_SingleBlockStage_StillDrops()
        {
            using var w = new World();
            Register(w, 1,
                Part(1, 0, 0, tankMass: 500, capacity: 500),
                Part(2, 1, 0, tankMass: 10, capacity: 100));
            w.SpaceGame.AddAutoStageFuelPlan(1, fuelFraction: 0.5f, stages: 1, minSeconds: 0.0);

            w.SpaceGame.StepAutoStagePlans(1.0);

            Assert.Equal(1, CountDebris(w));
        }
    }
}
