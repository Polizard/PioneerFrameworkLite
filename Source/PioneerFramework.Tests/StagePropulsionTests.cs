using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>分级供能（2026-10-27 裁定 A）：只聚合**活动级**（当前最大 stageId）的引擎/罐。</summary>
    public class StagePropulsionTests
    {
        private static readonly PropellantDef Prop = new() { defName = "P" };
        private static PropellantDef? Lookup(string name) => name == "P" ? Prop : null;

        private static SpacePart Engine(int id, int stage, float ve, float flow)
        {
            var p = new SpacePart { partId = id, stageId = stage, defName = "engine", boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new EngineComponentData
            { exhaustVelocity = ve, massFlowRate = flow, propellantDefName = "P" });
            return p;
        }

        private static SpacePart Tank(int id, int stage, double capacity, double mass)
        {
            var p = new SpacePart { partId = id, stageId = stage, defName = "tank", boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new PropellantTankComponentData
            { propellantDefName = "P", capacityKg = capacity, massKg = mass });
            return p;
        }

        [Fact]
        public void OnlyActiveStage_FeedsPropulsion()
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart>
            {
                Engine(1, 0, 3000f, 10f), Tank(2, 0, 1000.0, 700.0),   // 上面级（死重）
                Engine(3, 1, 1500f, 2f), Tank(4, 1, 500.0, 300.0),     // 活动级（地面）
            });

            Assert.True(PartPropulsionBuilder.BuildForMapless(e, Lookup));

            Assert.Single(e.propulsion);
            Assert.Equal(1500f, e.propulsion[0].ve, 3);      // 只认活动级
            Assert.Equal(2f, e.propulsion[0].massFlow, 3);
            Assert.Equal(500f, e.propulsion[0].capacity, 3);
            Assert.Equal(300f, e.propulsion[0].mass, 3);
        }

        [Fact]
        public void AfterJettison_RebuildSwitchesToNextStage()
        {
            var e = new SpaceEntity(2, 0);
            var upperEngine = Engine(1, 0, 3000f, 10f);
            var upperTank = Tank(2, 0, 1000.0, 700.0);
            e.SetParts(new List<SpacePart>
            {
                upperEngine, upperTank,
                Engine(3, 1, 1500f, 2f), Tank(4, 1, 500.0, 300.0),
            });
            Assert.True(PartPropulsionBuilder.BuildForMapless(e, Lookup));
            Assert.Equal(2f, e.propulsion[0].massFlow, 3);

            // 抛掉 stage 1（模拟 TrySeparateStage 后父组只剩 stage 0）
            e.SetParts(new List<SpacePart> { upperEngine, upperTank });
            PartPropulsionBuilder.RebuildMapless(e, Lookup);

            Assert.Single(e.propulsion);
            Assert.Equal(3000f, e.propulsion[0].ve, 3);
            Assert.Equal(10f, e.propulsion[0].massFlow, 3);
            Assert.Equal(1000f, e.propulsion[0].capacity, 3);
        }

        [Fact]
        public void SingleStage_StillAggregatesEverything()
        {
            var e = new SpaceEntity(3, 0);
            e.SetParts(new List<SpacePart>
            {
                Engine(1, 0, 3000f, 10f), Engine(2, 0, 3000f, 10f), Tank(3, 0, 1000.0, 500.0),
            });

            Assert.True(PartPropulsionBuilder.BuildForMapless(e, Lookup));
            Assert.Equal(20f, e.propulsion[0].massFlow, 3);   // 同一级的两台都算
        }
    }
}
