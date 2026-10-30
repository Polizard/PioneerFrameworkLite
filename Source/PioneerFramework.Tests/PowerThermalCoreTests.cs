using System;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **§5 电/热闭环的判据**（2026-10-22 部件功能化步 4/5 · 纯函数内核）。
    ///
    /// 设计稿给的三条判据逐条钉死：
    /// · **功率守恒**：分配 ≤ 供给（发电 + 电池放电）；
    /// · **优先级顺序**：推进 &gt; 护盾 &gt; 武器 &gt; 传感器（低优先级只吃剩下的）；
    /// · **单调性**：供给 ↑ ⇒ brownout 不降；需求 ↑ ⇒ brownout 不升；超温系数对超温量单调不增。
    /// 另加：电池充放的上限/存量夹取、无输入时"全供/不超温"恰为 1.0、同输入逐位同输出。
    /// </summary>
    public class PowerThermalCoreTests
    {
        private static PowerThermalCore.Inputs Inputs(
            float gen = 0f, float discharge = 0f, float charge = 0f, float capacity = 0f, float stored = 0f,
            float prop = 0f, float shield = 0f, float weapon = 0f, float sensor = 0f,
            float actProp = 1f, float actShield = 1f, float actWeapon = 1f, float actSensor = 1f,
            float heatProd = 0f, float heatDiss = 0f, float dt = 2f)
            => new()
            {
                genWatts = gen,
                dischargeLimitWatts = discharge,
                chargeLimitWatts = charge,
                capacityJoules = capacity,
                storedJoules = stored,
                nominalPropulsion = prop,
                nominalShield = shield,
                nominalWeapon = weapon,
                nominalSensor = sensor,
                activityPropulsion = actProp,
                activityShield = actShield,
                activityWeapon = actWeapon,
                activitySensor = actSensor,
                heatProduction = heatProd,
                heatDissipation = heatDiss,
                dtSeconds = dt,
            };

        [Fact]
        public void NoInputs_IsFullySupplied_AndNotOverheated()
        {
            var o = PowerThermalCore.Step(Inputs());

            Assert.True(o.brownout == 1f);
            Assert.True(o.brownoutPropulsion == 1f);
            Assert.True(o.overheat == 1f);
            Assert.Equal(0f, o.usedWatts);
            Assert.Equal(0f, o.storedJoules);
        }

        [Fact]
        public void Conservation_UsedNeverExceedsSupply()
        {
            for (int gen = 0; gen <= 1000; gen += 137)
            {
                var o = PowerThermalCore.Step(Inputs(gen: gen, prop: 400, shield: 400, weapon: 400, sensor: 400));
                Assert.True(o.usedWatts <= o.supplyWatts + 1e-3f, $"used={o.usedWatts} supply={o.supplyWatts} gen={gen}");
                Assert.True(o.usedWatts <= o.demandWatts + 1e-3f);
            }
        }

        [Fact]
        public void Priority_PropulsionThenShieldThenWeaponThenSensor()
        {
            // 供给 150，四通道各要 100 ⇒ 推进 100、护盾 50、武器 0、传感器 0
            var o = PowerThermalCore.Step(Inputs(gen: 150f, prop: 100f, shield: 100f, weapon: 100f, sensor: 100f));

            Assert.Equal(100f, 100f * o.brownoutPropulsion, 3);
            Assert.Equal(50f, 100f * o.brownoutShield, 3);
            Assert.Equal(0f, 100f * o.brownoutWeapon, 3);
            Assert.Equal(0f, 100f * o.brownoutSensor, 3);
            Assert.Equal(150f, o.usedWatts, 3);
            Assert.Equal(0.375f, o.brownout, 6);   // 150 / 400
        }

        [Fact]
        public void ActivityZero_RemovesDemandEntirely()
        {
            // 未点火 ⇒ 推进通道不需求（§5：按活动状态折算，不是静态标称）；
            // 护盾仍常开 ⇒ 需求只剩 10 W，而发电 0 ⇒ 护盾饿死、推进"无需求"视为全供（系数 1）。
            var o = PowerThermalCore.Step(Inputs(gen: 0f, prop: 1000f, actProp: 0f, shield: 10f));

            Assert.Equal(10f, o.demandWatts, 6);
            Assert.Equal(0f, o.usedWatts, 6);
            Assert.True(o.brownoutPropulsion == 1f, "无需求通道的系数必须是 1（不是 0）");
            Assert.Equal(0f, o.brownoutShield, 6);
            Assert.Equal(0f, o.brownout, 6);
        }

        [Fact]
        public void Battery_DischargeLimitedByBothLimitAndStoredEnergy()
        {
            // 存量 100 J，dt=2 s ⇒ 最多 50 W；放电上限 100 W ⇒ 供给 50 W
            var o = PowerThermalCore.Step(Inputs(discharge: 100f, capacity: 1000f, stored: 100f, prop: 100f));

            Assert.Equal(50f, o.supplyWatts, 3);
            Assert.Equal(0.5f, o.brownoutPropulsion, 6);
            Assert.Equal(50f, o.batteryDischargeWatts, 3);
            Assert.Equal(0f, o.storedJoules, 3);

            // 存量充足时受放电上限约束：1000 J / 2 s = 500 W，但上限 100 W
            var o2 = PowerThermalCore.Step(Inputs(discharge: 100f, capacity: 2000f, stored: 1000f, prop: 100f));
            Assert.Equal(100f, o2.supplyWatts, 3);
            Assert.Equal(1f, o2.brownoutPropulsion, 6);
            Assert.Equal(800f, o2.storedJoules, 3);
        }

        [Fact]
        public void Battery_ChargesFromSurplus_ClampedByChargeLimitAndCapacity()
        {
            // 发电 100 W，无需求，充电上限 10 W，dt=2 s ⇒ 充 20 J
            var o = PowerThermalCore.Step(Inputs(gen: 100f, charge: 10f, capacity: 1000f, stored: 0f));
            Assert.Equal(10f, o.batteryChargeWatts, 3);
            Assert.Equal(20f, o.storedJoules, 3);

            // 容量只剩 5 J ⇒ 只充 5 J（2.5 W × 2 s）
            var o2 = PowerThermalCore.Step(Inputs(gen: 100f, charge: 100f, capacity: 5f, stored: 0f));
            Assert.Equal(5f, o2.storedJoules, 3);
            Assert.Equal(2.5f, o2.batteryChargeWatts, 3);
        }

        [Fact]
        public void Battery_NotDrainedWhenNoDemand()
        {
            var o = PowerThermalCore.Step(Inputs(capacity: 1000f, stored: 500f));
            Assert.Equal(0f, o.batteryDischargeWatts);
            Assert.Equal(500f, o.storedJoules, 3);
        }

        [Fact]
        public void Brownout_MonotoneInSupplyAndDemand()
        {
            float last = -1f;
            for (int gen = 0; gen <= 400; gen += 20)
            {
                float b = PowerThermalCore.Step(Inputs(gen: gen, prop: 400f)).brownoutPropulsion;
                Assert.True(b >= last - 1e-6f, $"供给增加时 brownout 降了：gen={gen} b={b} last={last}");
                last = b;
            }

            last = 2f;
            for (int demand = 0; demand <= 800; demand += 40)
            {
                float b = PowerThermalCore.Step(Inputs(gen: 400f, prop: demand)).brownoutPropulsion;
                Assert.True(b <= last + 1e-6f, $"需求增加时 brownout 升了：demand={demand} b={b} last={last}");
                last = b;
            }
        }

        [Fact]
        public void Overheat_IsOneWhenBalanced_AndDecreasesWithExcess()
        {
            Assert.True(PowerThermalCore.OverheatFactor(0f, 0f) == 1f);
            Assert.True(PowerThermalCore.OverheatFactor(300f, 300f) == 1f);
            Assert.True(PowerThermalCore.OverheatFactor(200f, 300f) == 1f);   // 产热 < 散热：不超温

            // 产 400 / 散 200 ⇒ 超一倍 ⇒ 0.5
            Assert.Equal(0.5f, PowerThermalCore.OverheatFactor(400f, 200f), 6);

            float last = -1f;
            for (int diss = 0; diss <= 800; diss += 50)
            {
                float f = PowerThermalCore.OverheatFactor(400f, diss);
                // 散热 ↑ ⇒ 超温系数**↑**（更接近 1）；这里断言的是"不降"
                Assert.True(f >= last - 1e-6f, $"散热增加时超温系数反而降了：diss={diss} f={f} last={last}");
                last = f;
            }
        }

        [Fact]
        public void SameInputs_ProduceBitwiseSameOutputs()
        {
            var i = Inputs(gen: 123.45f, discharge: 67f, capacity: 999f, stored: 333f,
                prop: 111f, shield: 22f, weapon: 3f, sensor: 0.5f, heatProd: 400f, heatDiss: 150f);
            var a = PowerThermalCore.Step(i);
            var b = PowerThermalCore.Step(i);

            Assert.True(a.brownout == b.brownout);
            Assert.True(a.storedJoules == b.storedJoules);
            Assert.True(a.overheat == b.overheat);
            Assert.True(a.usedWatts == b.usedWatts);
        }
    }
}
