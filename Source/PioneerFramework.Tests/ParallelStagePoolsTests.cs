using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **C-full：并联点火 + 每块一池**（2026-11-03，§1.7）的接线判据。
    /// 纯逻辑在 `PartPropulsionBuilder` / `StagePartition.ActiveStages` / `FuelPlan`；
    /// 这里钉：活动集（含传递）、每块一网、按块扣料只动目标块。
    /// </summary>
    public class ParallelStagePoolsTests
    {
        private static PropellantDef Def() => new() { defName = "PF_TestParallel" };

        private static SpacePart Part(int id, int stage, int block, bool ignite, PropellantDef def,
            double tankMass, double capacity, bool withEngine = true)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "PF_Test_Part",
                stageId = stage,
                blockId = block,
                igniteWithUpperStage = ignite,
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(2, 2, 2),
                hp = 100f,
            };
            p.AddComponent(new HullMassComponent { mass = 100f });
            if (withEngine)
                p.AddComponent(new EngineComponentData
                {
                    propellantDefName = def.defName,
                    exhaustVelocity = 3000f,
                    massFlowRate = 10f,
                });
            if (capacity > 0.0)
                p.AddComponent(new PropellantTankComponentData
                {
                    propellantDefName = def.defName,
                    capacityKg = (float)capacity,
                    massKg = tankMass,
                });
            return p;
        }

        private static SpaceEntity Entity(params SpacePart[] parts)
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart>(parts));
            return e;
        }

        private static (double block1, double block2) PoolMass(SpaceEntity e, int stage)
        {
            double b1 = 0.0, b2 = 0.0;
            foreach (var d in e.propulsion)
            {
                if (d == null || d.stageId != stage) continue;
                if (d.blockId == 1) b1 += d.mass;
                if (d.blockId == 2) b2 += d.mass;
            }
            return (b1, b2);
        }

        [Fact]
        public void ParallelIgnition_CoreAndBooster_BothActive_TwoPools()
        {
            var def = Def();
            var e = Entity(
                Part(1, 0, 0, ignite: true, def, tankMass: 500, capacity: 500),
                Part(2, 1, 0, ignite: false, def, tankMass: 1000, capacity: 1000));

            Assert.True(PartPropulsionBuilder.BuildForMapless(e, _ => def));

            Assert.Equal(2, e.propulsion.Count);
            Assert.Contains(e.propulsion, d => d.stageId == 0 && d.blockId == 0);
            Assert.Contains(e.propulsion, d => d.stageId == 1 && d.blockId == 0);
            Assert.Equal(2, e.Networks.Count);   // 每块一张网

            e.SyncStageFeedValves();
            Assert.All(e.Parts, p =>
            {
                var t = p.GetComponent<PropellantTankComponentData>();
                Assert.NotNull(t);
                Assert.True(t!.feedEnabled, $"stage {p.stageId} 应开阀");
            });
        }

        [Fact]
        public void WithoutParallelFlag_CoreStaysDeadWeight()
        {
            var def = Def();
            var e = Entity(
                Part(1, 0, 0, ignite: false, def, 500, 500),
                Part(2, 1, 0, ignite: false, def, 1000, 1000));

            Assert.True(PartPropulsionBuilder.BuildForMapless(e, _ => def));
            var only = Assert.Single(e.propulsion);
            Assert.Equal(1, only.stageId);

            e.SyncStageFeedValves();
            var coreTank = e.GetPartById(1)!.GetComponent<PropellantTankComponentData>()!;
            Assert.False(coreTank.feedEnabled);
        }

        [Fact]
        public void ParallelFlag_IsTransitive()
        {
            var def = Def();

            // 级 1 没标 ⇒ 级 0 也不能跨过它点火
            var e1 = Entity(
                Part(1, 0, 0, ignite: true, def, 100, 100),
                Part(2, 1, 0, ignite: false, def, 100, 100),
                Part(3, 2, 0, ignite: false, def, 100, 100));
            Assert.True(PartPropulsionBuilder.BuildForMapless(e1, _ => def));
            Assert.Single(e1.propulsion);
            Assert.Equal(2, e1.propulsion[0].stageId);

            // 级 1 也标 ⇒ 0/1/2 全开
            var e2 = Entity(
                Part(1, 0, 0, ignite: true, def, 100, 100),
                Part(2, 1, 0, ignite: true, def, 100, 100),
                Part(3, 2, 0, ignite: false, def, 100, 100));
            Assert.True(PartPropulsionBuilder.BuildForMapless(e2, _ => def));
            Assert.Equal(3, e2.propulsion.Count);
            Assert.Contains(e2.propulsion, d => d.stageId == 0);
            Assert.Contains(e2.propulsion, d => d.stageId == 1);
            Assert.Contains(e2.propulsion, d => d.stageId == 2);
        }

        [Fact]
        public void PerBlockConsumption_DrainsOnlyTargetBlock()
        {
            var def = Def();
            var e = Entity(
                Part(1, 1, 1, false, def, tankMass: 100, capacity: 200),
                Part(2, 1, 2, false, def, tankMass: 100, capacity: 200));

            Assert.True(PartPropulsionBuilder.BuildForMapless(e, _ => def));
            Assert.Equal(2, e.propulsion.Count);
            e.SyncStageFeedValves();
            e.InvalidateFuelPlans();

            double got = e.TryConsumePropellantBlock(def, 1, 2, 40.0);

            Assert.Equal(40.0, got, 6);
            var (b1, b2) = PoolMass(e, 1);
            Assert.Equal(100.0, b1, 6);   // 块 1 未被扣
            Assert.Equal(60.0, b2, 6);    // 块 2 扣了 40

            Assert.Equal(100.0, e.GetPartById(1)!.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(60.0, e.GetPartById(2)!.GetComponent<PropellantTankComponentData>()!.massKg, 6);
        }
    }
}
