using System;
using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **§5 电/热闭环的接线判据**（组件 → 部件 → 实体 → 群级状态）。
    ///
    /// 钉四件事：
    /// · 每个组件只写**自己那个通道**，总 `powerDraw` 由四通道求和（组件直写会被覆盖）；
    /// · 电池贡献容量/充放上限，且都乘维护效率；
    /// · 实体聚合把通道/限值逐个加上（新增槽必须同步，否则规则读到 0）；
    /// · 群级 `StepPowerThermal` 真正把系数与存量落回群对象，无输入时回到 1/1（老船零变化）。
    /// </summary>
    public class PowerThermalIntegrationTests
    {
        private static SpacePart MakePart(int id = 1)
            => new() { partId = id, defName = "PF_ShipPart_Cube", boundsSizeShip = new DVector3(2, 2, 2) };

        private static void AssertNear(float expected, float actual, string where)
        {
            float tolerance = Math.Abs(expected) * 1e-6f + 1e-3f;
            Assert.True(Math.Abs(actual - expected) <= tolerance,
                $"{where}: 期望 {expected:R}，实际 {actual:R}（容差 {tolerance:R}）");
        }

        [Fact]
        public void Components_FeedOnlyTheirOwnChannels()
        {
            var p = MakePart();
            p.AddComponent(new ReactorComponentData { powerGenWatts = 5000f, heatProductionWatts = 1000f });
            p.AddComponent(new BatteryComponentData { capacityJoules = 1_000_000f, maxDischargeWatts = 2000f, maxChargeWatts = 3000f });
            p.AddComponent(new EngineComponentData { powerDrawWatts = 4000f });
            p.AddComponent(new SensorArrayComponentData { powerDrawWatts = 500f });
            p.AddComponent(new WeaponMountComponentData { powerDrawWatts = 600f });

            var s = p.Stats;
            Assert.Equal(5000f, s.powerGen);
            Assert.Equal(1000f, s.heatProduction);
            Assert.Equal(1_000_000f, s.powerStorage);
            Assert.Equal(2000f, s.powerDischargeLimit);
            Assert.Equal(3000f, s.powerChargeLimit);

            Assert.Equal(4000f, s.powerDrawPropulsion);
            Assert.Equal(0f, s.powerDrawShield);
            Assert.Equal(600f, s.powerDrawWeapon);
            Assert.Equal(500f, s.powerDrawSensor);

            // 总标称 = 四通道之和（不是组件各写一份）
            Assert.Equal(5100f, s.powerDraw);
        }

        [Fact]
        public void PowerContributions_ScaleWithCondition()
        {
            var p = MakePart();
            p.AddComponent(new ReactorComponentData { powerGenWatts = 5000f, heatProductionWatts = 1000f });
            p.AddComponent(new BatteryComponentData { capacityJoules = 1_000_000f, maxDischargeWatts = 2000f, maxChargeWatts = 3000f });
            p.AddComponent(new EngineComponentData { powerDrawWatts = 4000f });
            p.AddComponent(new SensorArrayComponentData { powerDrawWatts = 500f });
            p.ApplyWear(0.4f);   // condition 0.6 ⇒ eff ≈ 0.5

            var s = p.Stats;
            AssertNear(2500f, s.powerGen, "powerGen");
            AssertNear(500f, s.heatProduction, "heatProduction");
            AssertNear(500_000f, s.powerStorage, "powerStorage");
            AssertNear(1000f, s.powerDischargeLimit, "powerDischargeLimit");
            AssertNear(1500f, s.powerChargeLimit, "powerChargeLimit");
            AssertNear(2000f, s.powerDrawPropulsion, "powerDrawPropulsion");
            AssertNear(250f, s.powerDrawSensor, "powerDrawSensor");
            AssertNear(2250f, s.powerDraw, "powerDraw");
        }

        [Fact]
        public void EntityAggregation_SumsChannelsAndLimits()
        {
            var e = new SpaceEntity(1, 0);

            var a = MakePart(1);
            a.AddComponent(new ReactorComponentData { powerGenWatts = 5000f });
            a.AddComponent(new EngineComponentData { powerDrawWatts = 4000f });

            var b = MakePart(2);
            b.AddComponent(new BatteryComponentData { capacityJoules = 1_000_000f, maxDischargeWatts = 2000f, maxChargeWatts = 3000f });
            b.AddComponent(new WeaponMountComponentData { powerDrawWatts = 600f });

            e.SetParts(new List<SpacePart> { a, b });

            var s = e.AggregatedStats;
            Assert.Equal(5000f, s.powerGen);
            Assert.Equal(4000f, s.powerDrawPropulsion);
            Assert.Equal(600f, s.powerDrawWeapon);
            Assert.Equal(4600f, s.powerDraw);
            Assert.Equal(1_000_000f, s.powerStorage);
            Assert.Equal(2000f, s.powerDischargeLimit);
            Assert.Equal(3000f, s.powerChargeLimit);
        }

        [Fact]
        public void GroupStep_StoresBrownoutAndDrainsBattery()
        {
            // ⚠ 2026-10-26 用户裁定后：**推进通道是"全有或全无"**（欠电即拒绝点火），
            // 不再按比例给一部分 ⇒ 比例分配的读数改用**武器通道**（它仍是按比例的）。
            var g = new SpaceEntityGroup(1, 0);
            g.StoredEnergyJ = 100f;

            var inputs = new PowerThermalCore.Inputs
            {
                genWatts = 0f,
                dischargeLimitWatts = 100f,
                capacityJoules = 1000f,
                storedJoules = g.StoredEnergyJ,
                nominalWeapon = 100f,
                activityWeapon = 1f,
                dtSeconds = 2f,
            };
            g.StepPowerThermal(inputs);

            Assert.Equal(50f, g.PowerSupplyW, 3);          // 存量 100 J / 2 s
            Assert.Equal(0.5f, g.BrownoutWeapon, 6);
            Assert.Equal(0.5f, g.Brownout, 6);
            Assert.True(g.StoredEnergyJ == 0f, $"存量应放空，实际 {g.StoredEnergyJ:R}");
        }

        [Fact]
        public void GroupStep_ChargesBatteryFromSurplus()
        {
            var g = new SpaceEntityGroup(1, 0);
            g.StoredEnergyJ = 0f;

            var inputs = new PowerThermalCore.Inputs
            {
                genWatts = 100f,
                chargeLimitWatts = 10f,
                capacityJoules = 1000f,
                dtSeconds = 2f,
            };
            g.StepPowerThermal(inputs);

            Assert.True(g.StoredEnergyJ == 20f, $"应充入 20 J，实际 {g.StoredEnergyJ:R}");
            Assert.True(g.Brownout == 1f);
        }

        [Fact]
        public void GroupStep_NoInputs_ResetsToFullySupplied()
        {
            var g = new SpaceEntityGroup(1, 0);
            g.Brownout = 0.25f;
            g.BrownoutPropulsion = 0.25f;
            g.Overheat = 0.25f;

            g.StepPowerThermal(default(PowerThermalCore.Inputs));

            Assert.True(g.Brownout == 1f);
            Assert.True(g.BrownoutPropulsion == 1f);
            Assert.True(g.Overheat == 1f);
        }

        [Fact]
        public void GroupStep_OverheatFromProductionAndDissipation()
        {
            var g = new SpaceEntityGroup(1, 0);
            var inputs = new PowerThermalCore.Inputs
            {
                heatProduction = 400f,
                heatDissipation = 200f,
                dtSeconds = 2f,
            };
            g.StepPowerThermal(inputs);

            Assert.Equal(0.5f, g.Overheat, 6);
            Assert.Equal(0f, g.PowerDemandW, 6);
        }
    }
}
