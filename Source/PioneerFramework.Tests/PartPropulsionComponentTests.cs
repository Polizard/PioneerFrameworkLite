using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B4a：tank/engine part 归属**：目录 → 组件、油量按容量分布到 tank、Σtank = 池。
    /// </summary>
    public class PartPropulsionComponentTests
    {
        private static DesignPartSpec Entry(string defName, string category, int qty = 1)
            => new() { defName = defName, category = category, quantity = qty };

        private static SpacePart Tank(int id, double x, double capacity)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "PF_ShipPart_Cube",
                positionShip = new DVector3(x, 0, 0),
                boundsSizeShip = new DVector3(2, 2, 2),
                hp = 100f,
            };
            p.AddComponent(new HullMassComponent { mass = 100f });
            p.AddComponent(new PropellantTankComponentData { propellantDefName = "P", capacityKg = capacity });
            return p;
        }

        [Fact]
        public void SyncTankFuel_DistributesByCapacity_AndSumsToPool()
        {
            var e = new SpaceEntity(9200, 0);
            var a = Tank(1, -2, 1000);
            var b = Tank(2, 2, 3000);
            e.SetParts(new List<SpacePart> { a, b });

            e.SyncTankFuelDistribution("P", 2000);

            var ta = a.GetComponent<PropellantTankComponentData>()!;
            var tb = b.GetComponent<PropellantTankComponentData>()!;
            Assert.Equal(500.0, ta.massKg, 9);
            Assert.Equal(1500.0, tb.massKg, 9);
            Assert.Equal(2000.0, ta.massKg + tb.massKg, 9);

            e.SyncTankFuelDistribution("P", 0);
            Assert.Equal(0.0, ta.massKg, 9);
            Assert.Equal(0.0, tb.massKg, 9);
        }

        [Fact]
        public void Assembler_AttachesTankAndEngineComponents_FromCatalog()
        {
            var tankDef = new ShipPartDef
            {
                defName = "TankX",
                category = "tank",
                massKg = 200f,
                boundsX = 2, boundsY = 6, boundsZ = 2,
                stackLengthMeters = 6,
                tankCapacityKg = 1234f,
                propellantDefName = "P",
            };
            var engineDef = new ShipPartDef
            {
                defName = "EngineX",
                category = "thruster",
                massKg = 500f,
                boundsX = 2, boundsY = 4, boundsZ = 2,
                stackLengthMeters = 4,
                exhaustVelocity = 3200f,
                massFlowRate = 12f,
                isVectoredRcs = true,
                propellantDefName = "P",
            };
            var parts = DesignPartAssembler.BuildParts(
                new RocketDesignSpec { parts = new List<DesignPartSpec> { Entry("TankX", "tank"), Entry("EngineX", "thruster") } },
                10, 10, out _, name => name == tankDef.defName ? tankDef : name == engineDef.defName ? engineDef : null);

            var tank = parts[1].GetComponent<PropellantTankComponentData>();
            var engine = parts[2].GetComponent<EngineComponentData>();
            Assert.NotNull(tank);
            Assert.Equal(1234f, tank!.capacityKg, 3);
            Assert.Equal("P", tank.propellantDefName);
            Assert.NotNull(engine);
            Assert.Equal(3200f, engine!.exhaustVelocity, 3);
            Assert.Equal(12f, engine.massFlowRate, 3);
            Assert.True(engine.isVectoredRcs);
        }

        [Fact]
        public void Components_Clone_PreservesFields()
        {
            var tank = new PropellantTankComponentData
            {
                propellantDefName = "P",
                capacityKg = 1234.5,
                massKg = 678.9,
                leaked = true,
            };
            var tankCopy = (PropellantTankComponentData)tank.Clone()!;
            Assert.Equal("P", tankCopy.propellantDefName);
            Assert.Equal(1234.5, tankCopy.capacityKg, 9);
            Assert.Equal(678.9, tankCopy.massKg, 9);
            Assert.True(tankCopy.leaked);

            var engine = new EngineComponentData
            {
                exhaustVelocity = 3200f,
                massFlowRate = 12f,
                isVectoredRcs = true,
                propellantDefName = "P",
            };
            var engineCopy = (EngineComponentData)engine.Clone()!;
            Assert.Equal(3200f, engineCopy.exhaustVelocity, 3);
            Assert.Equal(12f, engineCopy.massFlowRate, 3);
            Assert.True(engineCopy.isVectoredRcs);
            Assert.Equal("P", engineCopy.propellantDefName);
        }
    }
}
