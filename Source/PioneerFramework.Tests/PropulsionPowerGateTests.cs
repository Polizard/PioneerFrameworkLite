using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **"欠电 ⇒ 拒绝点火"的判据**（用户 2026-10-26 裁定；设计稿 §5 推进消费端）。
    ///
    /// 语义（写成判据，防止将来被"顺手改成按比例降推力"）：
    /// · 推进通道**要么全额、要么不点**（`PowerThermalCore.CanSupplyChannel`）；
    /// · 点火入口 `StartBurn` 在欠电时**直接拒绝**（`HasActiveBurn` 保持 false，置读数位）；
    /// · 燃烧中掉电 ⇒ 本步**不出力不耗料**（`PropulsionPowered=false`）并由主线程熄火；
    /// · **没填推进功耗的旧船恒通过**（零行为变化）——这是本条不砸既有发射链的唯一保证。
    /// </summary>
    public class PropulsionPowerGateTests
    {
        private static PowerThermalCore.Inputs Inputs(
            float gen = 0f, float discharge = 0f, float capacity = 0f, float stored = 0f,
            float prop = 0f, float actProp = 1f, float dt = 2f)
            => new()
            {
                genWatts = gen,
                dischargeLimitWatts = discharge,
                capacityJoules = capacity,
                storedJoules = stored,
                nominalPropulsion = prop,
                activityPropulsion = actProp,
                dtSeconds = dt,
            };

        [Fact]
        public void CanSupply_NoDemand_IsAlwaysTrue()
        {
            Assert.True(PowerThermalCore.CanSupplyChannel(Inputs(prop: 0f), 0f));
            Assert.True(PowerThermalCore.CanSupplyChannel(Inputs(gen: 0f), 0f));
        }

        [Fact]
        public void CanSupply_GeneratorAlone()
        {
            Assert.True(PowerThermalCore.CanSupplyChannel(Inputs(gen: 100_000f), 100_000f));
            Assert.False(PowerThermalCore.CanSupplyChannel(Inputs(gen: 99_999f), 100_000f));
        }

        [Fact]
        public void CanSupply_BatteryLimitedByDischargeRateAndStoredEnergy()
        {
            // 放电上限 2000 W、存量 100 J、dt 2 s ⇒ 只能放 50 W
            var enough = Inputs(discharge: 2000f, capacity: 10_000f, stored: 100f);
            Assert.True(PowerThermalCore.CanSupplyChannel(enough, 50f));
            Assert.False(PowerThermalCore.CanSupplyChannel(enough, 51f));

            // 存量充足时受放电上限约束
            var rateLimited = Inputs(discharge: 40f, capacity: 10_000f, stored: 10_000f);
            Assert.True(PowerThermalCore.CanSupplyChannel(rateLimited, 40f));
            Assert.False(PowerThermalCore.CanSupplyChannel(rateLimited, 41f));
        }

        [Fact]
        public void CanSupply_NoStepYet_UsesDischargeLimitOnly()
        {
            Assert.True(PowerThermalCore.CanSupplyChannel(Inputs(discharge: 500f, dt: 0f), 500f));
            Assert.False(PowerThermalCore.CanSupplyChannel(Inputs(discharge: 500f, dt: 0f), 501f));
        }

        [Fact]
        public void Group_NoPropulsionDemand_CanIgnite()
        {
            var g = new SpaceEntityGroup(1, 0);
            Assert.True(g.CanSupplyPropulsionNow());

            g.StepPowerThermal(Inputs(gen: 0f, prop: 0f));
            Assert.True(g.CanSupplyPropulsionNow());
            Assert.True(g.powerThermal.PropulsionPowered);
        }

        [Fact]
        public void Group_InsufficientPower_RefusesIgnition()
        {
            var g = new SpaceEntityGroup(1, 0);
            g.StepPowerThermal(Inputs(gen: 50_000f, prop: 100_000f, actProp: 0f));   // 没点火时只备着数据

            Assert.False(g.CanSupplyPropulsionNow());

            g.StartBurn(new DVector3(100, 0, 0));

            Assert.False(g.HasActiveBurn);                  // 拒绝：不点火
            Assert.True(g.powerThermal.BurnIgnitionBlockedByPower);      // 读数位
            Assert.True(g.remainingBurnDV == 0.0);
        }

        [Fact]
        public void Group_SufficientPower_AllowsIgnition()
        {
            var g = new SpaceEntityGroup(1, 0);
            g.StepPowerThermal(Inputs(gen: 120_000f, prop: 100_000f, actProp: 0f));

            Assert.True(g.CanSupplyPropulsionNow());

            g.StartBurn(new DVector3(100, 0, 0));

            Assert.True(g.HasActiveBurn);
            Assert.False(g.powerThermal.BurnIgnitionBlockedByPower);
            Assert.Equal(100.0, g.remainingBurnDV, 6);
        }

        [Fact]
        public void Group_BatteryMakesUpTheDifference()
        {
            var g = new SpaceEntityGroup(1, 0);
            g.powerThermal.StoredEnergyJ = 1000f;   // dt=2 ⇒ 可放 500 W（受存量约束）
            g.StepPowerThermal(Inputs(gen: 0f, discharge: 1200f, capacity: 10_000f, stored: 0f, prop: 1000f, actProp: 0f));
            // 输入里的 stored 快照是 0，但 `CanSupplyPropulsionNow` 用群级实时存量 ⇒ 500 W 仍不够 1000 W
            Assert.False(g.CanSupplyPropulsionNow());

            g.powerThermal.StoredEnergyJ = 4000f;   // dt=2 ⇒ 可放 1200 W（受放电上限）⇒ 够 1000 W
            Assert.True(g.CanSupplyPropulsionNow());
        }

        [Fact]
        public void Step_WhenBurningButUnderpowered_ZeroesPropulsionDemand()
        {
            var g = new SpaceEntityGroup(1, 0);
            g.StepPowerThermal(Inputs(gen: 50_000f, prop: 100_000f, actProp: 1f));

            Assert.False(g.powerThermal.PropulsionPowered);        // 推力门关闭
            Assert.True(g.powerThermal.BrownoutPropulsion == 1f);  // 本步推进不需求（不是"给一半"）
            Assert.Equal(0f, g.powerThermal.PowerDemandW, 6);      // 需求被收掉 ⇒ 不白烧电池
        }

        [Fact]
        public void Step_WhenBurningAndPowered_KeepsDemand()
        {
            var g = new SpaceEntityGroup(1, 0);
            g.StepPowerThermal(Inputs(gen: 120_000f, prop: 100_000f, actProp: 1f));

            Assert.True(g.powerThermal.PropulsionPowered);
            Assert.True(g.powerThermal.BrownoutPropulsion == 1f);
            Assert.Equal(100_000f, g.powerThermal.PowerDemandW, 3);
        }

        [Fact]
        public void Assembler_CopiesEnginePowerDrawFromCatalogDef()
        {
            var def = new ShipPartDef
            {
                defName = "Test_ElectricThruster",
                label = "测试电推",
                category = "thruster",
                massKg = 600f,
                exhaustVelocity = 3000f,
                massFlowRate = 10f,
                propellantDefName = "PF_Propellant_Chemical",
                powerDrawWatts = 100_000f,
                presets = new List<PartComponentPreset>(),
            };
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "Test_ElectricThruster", category = "thruster", quantity = 1 },
                },
            };

            var parts = DesignPartAssembler.BuildParts(spec, 10, 10, out _, _ => def);
            var engine = parts[1].GetComponent<EngineComponentData>();

            Assert.NotNull(engine);
            Assert.Equal(100_000f, engine!.powerDrawWatts);
            Assert.Equal(100_000f, parts[1].Stats.powerDrawPropulsion);
        }
    }
}
