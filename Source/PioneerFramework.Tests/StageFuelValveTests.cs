using System.Collections.Generic;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **分级供油阀门**（2026-10-28 事故回归）：一级燃烧时**上面级的罐一滴油都不能少**。
    ///
    /// 症状（游戏内实测）：两级火箭——上面级 100 t 罐、一级 400 t 罐。一级烧了 499.3 t 之后抛级，
    /// 抛级后的母舰只剩 0.7 t 燃料（群质量 6.5 t），上面级 272 kg 就熄火、被惯性甩到 15,000 km。
    ///
    /// 两条闸：`SyncStageFeedValves`（只有活动级罐 `feedEnabled=true`）与 `FuelPlan`（跳过关闭的罐）。
    /// 本测试直接验它们，不依赖游戏。
    /// </summary>
    public class StageFuelValveTests
    {
        private static readonly PropellantDef Prop = new() { defName = "PF_Test_Prop" };

        private static SpacePart TankPart(int id, int stage, double cap, double fuel)
        {
            var p = new SpacePart { partId = id, defName = "tank", stageId = stage, boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new PropellantTankComponentData
            {
                propellantDefName = Prop.defName,   // 与 def.defName 同名即可
                capacityKg = cap,
                massKg = fuel,
            });
            return p;
        }

        private static double Fuel(SpacePart p) => p.GetComponent<PropellantTankComponentData>()!.massKg;

        [Fact]
        public void OnlyActiveStageTank_Feeds()
        {
            var booster = TankPart(1, stage: 1, cap: 400_000, fuel: 400_000);
            var upper = TankPart(2, stage: 0, cap: 100_000, fuel: 100_000);
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart> { booster, upper });

            Assert.True(booster.GetComponent<PropellantTankComponentData>()!.feedEnabled);
            Assert.False(upper.GetComponent<PropellantTankComponentData>()!.feedEnabled);
        }

        [Fact]
        public void Consumption_DoesNotDrainUpperStage()
        {
            var booster = TankPart(1, stage: 1, cap: 400_000, fuel: 400_000);
            var upper = TankPart(2, stage: 0, cap: 100_000, fuel: 100_000);
            var e = new SpaceEntity(2, 0);
            e.SetParts(new List<SpacePart> { booster, upper });

            // 一次扣 50 t：只能从一级罐出（上面级阀门关）
            Assert.True(e.TryConsumeViaPlan(Prop, 0, 50_000, out double actual, out bool handled));
            Assert.True(handled);
            Assert.Equal(50_000, actual, 3);
            Assert.Equal(350_000, Fuel(booster), 3);
            Assert.Equal(100_000, Fuel(upper), 3);      // ★ 上面级一滴不少

            // 抽干一级（再扣 400 t，只能拿到剩下的 350 t）
            Assert.True(e.TryConsumeViaPlan(Prop, 0, 400_000, out double actual2, out _));
            Assert.Equal(350_000, actual2, 3);
            Assert.Equal(0, Fuel(booster), 3);
            Assert.Equal(100_000, Fuel(upper), 3);      // ★ 仍然一滴不少
        }

        [Fact]
        public void AfterStaging_UpperStageValveOpens()
        {
            var booster = TankPart(1, stage: 1, cap: 400_000, fuel: 400_000);
            var upper = TankPart(2, stage: 0, cap: 100_000, fuel: 100_000);
            var e = new SpaceEntity(3, 0);
            e.SetParts(new List<SpacePart> { booster, upper });

            // 模拟抛级：把一级罐移除 ⇒ 活动级变成 0 ⇒ 上面级阀门打开
            e.RemovePart(1);

            Assert.True(upper.GetComponent<PropellantTankComponentData>()!.feedEnabled);
            Assert.True(e.TryConsumeViaPlan(Prop, 0, 10_000, out double actual, out _));
            Assert.Equal(10_000, actual, 3);
            Assert.Equal(90_000, Fuel(upper), 3);
        }
    }
}
