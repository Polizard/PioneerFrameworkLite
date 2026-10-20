using System.Collections.Generic;
using PioneerFramework.Components;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B6：供油计划（FuelPlan）**——优先级/供油组/比例消耗的纯逻辑判据。
    /// 目标：热路径一遍扫描、不排序；组过滤在构建期完成。
    /// </summary>
    public class FuelPlanTests
    {
        private static SpacePart Tank(int id, int group, byte priority, double mass, double capacity)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "PF_ShipPart_Cube",
                boundsSizeShip = new DVector3(2, 2, 2),
                hp = 100f,
                fuelGroupId = group,
                fuelPriority = priority,
            };
            p.AddComponent(new PropellantTankComponentData
            {
                propellantDefName = "P",
                capacityKg = (float)capacity,
                massKg = mass,
            });
            return p;
        }

        [Fact]
        public void Consume_NoPriority_ProportionalByRemainingMass()
        {
            var a = Tank(1, 0, 0, 100, 200);
            var b = Tank(2, 0, 0, 300, 400);
            var plan = new FuelPlan();
            plan.Build(new List<SpacePart> { a, b }, "P", 0);

            double got = plan.Consume(100);

            Assert.Equal(100.0, got, 6);
            Assert.Equal(75.0, a.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(225.0, b.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(300.0, plan.TotalMass, 6);
        }

        [Fact]
        public void Consume_Priority_LowTierFirst()
        {
            var low = Tank(1, 0, 1, 50, 100);    // tier 1 先烧
            var high = Tank(2, 0, 2, 100, 100);  // tier 2 后烧
            var plan = new FuelPlan();
            plan.Build(new List<SpacePart> { low, high }, "P", 0);

            double got = plan.Consume(80);

            Assert.Equal(80.0, got, 6);
            Assert.Equal(0.0, low.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(70.0, high.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(70.0, plan.TotalMass, 6);
        }

        [Fact]
        public void Fill_HighPriorityFirst()
        {
            var low = Tank(1, 0, 1, 0, 100);
            var high = Tank(2, 0, 2, 0, 100);
            var plan = new FuelPlan();
            plan.Build(new List<SpacePart> { low, high }, "P", 0);

            double got = plan.Fill(150);

            Assert.Equal(150.0, got, 6);
            Assert.Equal(50.0, low.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(100.0, high.GetComponent<PropellantTankComponentData>()!.massKg, 6);
        }


        [Fact]
        public void EntityConsume_DoesNotDirtyPartStats_ButBumpsTankMassVersion()
        {
            var def = new PropellantDef { defName = "P" };
            var a = Tank(1, 0, 0, 50, 100);
            var b = Tank(2, 0, 0, 100, 100);
            var e = new SpaceEntity(2, 0);
            e.SetParts(new List<SpacePart> { a, b });
            e.AddPropulsion(new PropulsionData
            {
                propellant = def, ve = 3000f, massFlow = 10f, mass = 150.0, capacity = 200.0
            });
            e.InvalidateFuelPlans();

            // 先把功能 stats 算出来（之后不应因油量变化重算）
            double massBefore = e.AggregatedStats.mass;
            int partVersionBefore = e.partSetVersion;
            int tankVersionBefore = e.tankMassVersion;

            double got = e.TryConsumePropellant(def, 30.0);

            Assert.Equal(30.0, got, 6);
            Assert.Equal(partVersionBefore, e.partSetVersion);       // P0-2：不再全量 InvalidateStats
            Assert.True(e.tankMassVersion > tankVersionBefore);      // COM/惯量专用版本必须动
            Assert.Equal(massBefore, e.AggregatedStats.mass, 6);     // 工质不在 ShipStats.mass
            Assert.Equal(120.0, e.GetPropellantMass(def), 6);        // 池仍正确扣减
        }

        [Fact]
        public void EntityConsume_UsesPlanPriority_AndReducesOwnMass()
        {
            var def = new PropellantDef { defName = "P" };
            var a = Tank(1, 0, 1, 50, 100);
            var b = Tank(2, 0, 2, 100, 100);
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart> { a, b });
            e.AddPropulsion(new PropulsionData
            {
                propellant = def,
                ve = 3000f,
                massFlow = 10f,
                mass = 150.0,
                capacity = 200.0
            });
            // AddPropulsion 按容量均分（75/75）；改回优先级样本值并让计划失效
            a.GetComponent<PropellantTankComponentData>()!.massKg = 50.0;
            b.GetComponent<PropellantTankComponentData>()!.massKg = 100.0;
            e.InvalidateFuelPlans();

            double got = e.TryConsumePropellant(def, 80.0);

            Assert.Equal(80.0, got, 6);
            Assert.Equal(0.0, a.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(70.0, b.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(70.0, e.GetPropellantMass(def), 6);   // 自带池同步扣减
        }


        [Fact]
        public void EntityFill_UsesPlanHighPriorityFirst()
        {
            var def = new PropellantDef { defName = "P" };
            var low = Tank(1, 0, 1, 0, 100);
            var high = Tank(2, 0, 2, 0, 100);
            var e = new SpaceEntity(3, 0);
            e.SetParts(new List<SpacePart> { low, high });
            e.AddPropulsion(new PropulsionData
            {
                propellant = def,
                ve = 3000f,
                massFlow = 10f,
                mass = 0.0,
                capacity = 200.0
            });
            e.InvalidateFuelPlans();

            double added = e.TryFillPropellant(def, 150.0);

            Assert.Equal(150.0, added, 6);
            Assert.Equal(50.0, low.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(100.0, high.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(150.0, e.GetPropellantMass(def), 6);
        }

        [Fact]
        public void EntityConsume_GroupFilter_DoesNotTouchOtherGroup()
        {
            var def = new PropellantDef { defName = "P" };
            var main = Tank(1, 0, 0, 100, 100);
            var cut = Tank(2, 1, 0, 100, 100);
            var e = new SpaceEntity(2, 0);
            e.SetParts(new List<SpacePart> { main, cut });
            e.InvalidateFuelPlans();

            bool handled = e.TryConsumeViaPlan(def, 0, 150.0, out double actual, out _);

            Assert.True(handled);
            Assert.Equal(100.0, actual, 6);   // 只吃 group 0
            Assert.Equal(0.0, main.GetComponent<PropellantTankComponentData>()!.massKg, 6);
            Assert.Equal(100.0, cut.GetComponent<PropellantTankComponentData>()!.massKg, 6);
        }

        [Fact]
        public void Build_FiltersGroupAndDestroyed()
        {
            var g0 = Tank(1, 0, 0, 10, 20);
            var g1 = Tank(2, 1, 0, 30, 40);   // 另一供油组
            var dead = Tank(3, 0, 0, 50, 60);
            dead.isDestroyed = true;

            var plan = new FuelPlan();
            plan.Build(new List<SpacePart> { g0, g1, dead }, "P", 0);

            Assert.Equal(10.0, plan.TotalMass, 6);
            Assert.Equal(20.0, plan.TotalCapacity, 6);
        }
    }
}
