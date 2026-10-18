using System;
using System.Collections.Generic;
using PioneerFramework.GroundAssembly;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **批 5 余下 · 目录是推进的唯一出处**（2026-10-17）：
    /// `ShipPartDef` 收录的件，其 ve/ṁ/罐容不再去查建筑 `ThingDef`；
    /// 目录未收录才走 ThingDef Comp 兼容链；两者都没有保持旧调试兜底。
    /// 多型号混装按质量流加权等效 ve（ΣF/Σṁ），不是"最后一台赢"。
    /// </summary>
    public class PartPropulsionResolverTests
    {
        private static RocketDesignSpec Spec(params DesignPartSpec[] entries)
            => new() { parts = new List<DesignPartSpec>(entries) };

        private static DesignPartSpec Entry(string defName, string category, int qty = 1)
            => new() { defName = defName, category = category, quantity = qty };

        private static ShipPartDef PartDef(string defName, string category,
            float massFlow = 0f, float ve = 0f, float capacity = 0f, string propellant = "")
            => new()
            {
                defName = defName,
                category = category,
                massFlowRate = massFlow,
                exhaustVelocity = ve,
                tankCapacityKg = capacity,
                propellantDefName = propellant,
            };

        private static Func<string, ShipPartDef> Catalog(params ShipPartDef[] defs)
        {
            var map = new Dictionary<string, ShipPartDef>();
            foreach (var d in defs) map[d.defName] = d;
            return name => map.TryGetValue(name, out var d) ? d : null;
        }

        private static ThingDef ThrusterThing(string defName, float ve, float massFlow, string propellant = "")
            => new()
            {
                defName = defName,
                comps = new List<CompProperties>
                {
                    new CompProperties_Thruster
                    {
                        exhaustVelocity = ve,
                        massFlowRate = massFlow,
                        propellantDefName = propellant,
                    }
                }
            };

        private static ThingDef TankThing(string defName, float capacity, string propellant = "")
            => new()
            {
                defName = defName,
                comps = new List<CompProperties>
                {
                    new CompProperties_PropellantTank
                    {
                        capacity = capacity,
                        propellantDefName = propellant,
                    }
                }
            };

        [Fact]
        public void Resolve_CatalogThrusterAndTank_UsesCatalogValues()
        {
            var catalog = Catalog(
                PartDef("Part_Engine", "thruster", massFlow: 7f, ve: 3200f, propellant: "P_Custom"),
                PartDef("Part_Tank", "tank", capacity: 2500f, propellant: "P_Custom"));

            var summary = PartPropulsionResolver.Resolve(
                Spec(Entry("Part_Engine", "thruster", 2), Entry("Part_Tank", "tank", 3)),
                catalog, _ => null);

            Assert.Equal(2, summary.thrusterCount);
            Assert.Equal(3, summary.tankCount);
            Assert.Equal(2, summary.catalogThrusters);
            Assert.Equal(3, summary.catalogTanks);
            Assert.Equal(14f, summary.massFlow, 5);
            Assert.Equal(3200f, summary.exhaustVelocity, 5);
            Assert.Equal(7500f, summary.capacity, 5);
            Assert.Equal("P_Custom", summary.propellantDefName);
        }

        [Fact]
        public void Resolve_CatalogWinsOverThingDef()
        {
            // 同名 Def 同时在目录与建筑表里：目录是唯一出处，建筑 Comp 不参与
            var catalog = Catalog(PartDef("Both", "thruster", massFlow: 3f, ve: 1111f));
            var thing = ThrusterThing("Both", ve: 9999f, massFlow: 99f);

            var summary = PartPropulsionResolver.Resolve(
                Spec(Entry("Both", "thruster")), catalog, _ => thing);

            Assert.Equal(3f, summary.massFlow, 5);
            Assert.Equal(1111f, summary.exhaustVelocity, 5);
            Assert.Equal(1, summary.catalogThrusters);
        }

        [Fact]
        public void Resolve_CatalogEntryWithoutPropulsionData_ContributesNothing()
        {
            // 目录收录了这件、但没给流率 ⇒ 它不是工作引擎；不回退建筑表、也不吃调试默认值
            var catalog = Catalog(PartDef("HullOnly", "thruster"));
            var thing = ThrusterThing("HullOnly", ve: 9000f, massFlow: 50f);

            var summary = PartPropulsionResolver.Resolve(
                Spec(Entry("HullOnly", "thruster", 2)), catalog, _ => thing);

            Assert.Equal(0f, summary.massFlow, 5);
            Assert.Equal(2, summary.thrusterCount);
            Assert.Equal(0, summary.catalogThrusters);
        }

        [Fact]
        public void Resolve_UncataloguedEntry_FallsBackToThingDefComp()
        {
            var thing = ThrusterThing("DMS_Engine", ve: 2500f, massFlow: 12f, propellant: "P_Dms");
            var tank = TankThing("DMS_Tank", capacity: 400f, propellant: "P_Dms");

            var summary = PartPropulsionResolver.Resolve(
                Spec(Entry("DMS_Engine", "thruster", 2), Entry("DMS_Tank", "tank", 3)),
                _ => null,
                name => name == thing.defName ? thing : name == tank.defName ? tank : null);

            Assert.Equal(24f, summary.massFlow, 5);
            Assert.Equal(2500f, summary.exhaustVelocity, 5);
            Assert.Equal(1200f, summary.capacity, 5);
            Assert.Equal("P_Dms", summary.propellantDefName);
            Assert.Equal(0, summary.catalogThrusters);
            Assert.Equal(0, summary.catalogTanks);
        }

        [Fact]
        public void Resolve_UnknownEverywhere_KeepsLegacyDebugDefaults()
        {
            // 旧行为逐位保留：ve=3000、每台 ṁ=10、每罐 1000
            var summary = PartPropulsionResolver.Resolve(
                Spec(Entry("PF_ShipPart_Thruster_Debug", "thruster", 2),
                     Entry("PF_ShipPart_Tank_Debug", "tank", 3)),
                _ => null, _ => null);

            Assert.Equal(20f, summary.massFlow, 5);
            Assert.Equal(3000f, summary.exhaustVelocity, 5);
            Assert.Equal(3000f, summary.capacity, 5);
            Assert.Equal(2, summary.thrusterCount);
            Assert.Equal(3, summary.tankCount);
        }

        [Fact]
        public void Resolve_MixedThrusters_WeightsExhaustVelocityByMassFlow()
        {
            var catalog = Catalog(
                PartDef("A", "thruster", massFlow: 10f, ve: 3000f),
                PartDef("B", "thruster", massFlow: 30f, ve: 6000f));

            var summary = PartPropulsionResolver.Resolve(
                Spec(Entry("A", "thruster", 2), Entry("B", "thruster", 1)),
                catalog, _ => null);

            // ΣF = 3000·10·2 + 6000·30 = 240,000；Σṁ = 50 ⇒ ve_eff = 4800
            Assert.Equal(50f, summary.massFlow, 5);
            Assert.Equal(4800f, summary.exhaustVelocity, 5);
        }

        [Fact]
        public void Resolve_NullSpec_ReturnsZeroCountsAndDefaultVe()
        {
            var summary = PartPropulsionResolver.Resolve(null, _ => null, _ => null);

            Assert.Equal(0, summary.thrusterCount);
            Assert.Equal(0, summary.tankCount);
            Assert.Equal(0f, summary.massFlow, 5);
            Assert.Equal(0f, summary.capacity, 5);
            Assert.Equal(PartPropulsionResolver.DefaultExhaustVelocity, summary.exhaustVelocity, 5);
            Assert.Equal(string.Empty, summary.propellantDefName);
        }
    }
}
