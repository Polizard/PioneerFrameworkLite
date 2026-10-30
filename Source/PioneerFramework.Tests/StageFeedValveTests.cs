using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>罐阀门（2026-10-27）：活动级 = 最大 stageId，只有它的罐开着；抛级后自动切。</summary>
    public class StageFeedValveTests
    {
        private static SpacePart TankPart(int id, int stage)
        {
            var p = new SpacePart { partId = id, stageId = stage, defName = "tank", boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new PropellantTankComponentData
            { propellantDefName = "PF_Propellant_Chemical", capacityKg = 1000.0, massKg = 1000.0 });
            return p;
        }

        private static PropellantTankComponentData TankOf(SpacePart p)
            => p.GetComponent<PropellantTankComponentData>()!;

        [Fact]
        public void Default_IsEnabled_AndPresetCanClose()
        {
            var t = new PropellantTankComponentData();
            Assert.True(t.feedEnabled);
            Assert.True(t.ApplyPreset("feedEnabled", "false"));
            Assert.False(t.feedEnabled);
            Assert.False(t.ApplyPreset("feedEnabled", "nope"));
        }

        [Fact]
        public void OnlyActiveStageTank_IsOpen()
        {
            var upper = TankPart(1, 0);
            var ground = TankPart(2, 1);
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart> { upper, ground });

            Assert.False(TankOf(upper).feedEnabled);
            Assert.True(TankOf(ground).feedEnabled);
        }

        [Fact]
        public void AfterSeparation_NextStageOpens()
        {
            var upper = TankPart(1, 0);
            var ground = TankPart(2, 1);
            var e = new SpaceEntity(2, 0);
            e.SetParts(new List<SpacePart> { upper, ground });
            Assert.False(TankOf(upper).feedEnabled);

            e.SetParts(new List<SpacePart> { upper });   // 模拟抛掉地面级

            Assert.True(TankOf(upper).feedEnabled);
        }
    }
}
