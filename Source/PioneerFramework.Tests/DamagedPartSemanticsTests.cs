using System.Collections.Generic;
using PioneerFramework.Combat;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B5 / D2 乙：损伤件语义**——损毁件仍在/有质量/参与命中；功能与容量出局；
    /// 储罐漏光；关键件判定。
    /// </summary>
    public class DamagedPartSemanticsTests
    {
        private static SpacePart Part(int id, DVector3 pos, double size, float mass)
        {
            var p = new SpacePart
            {
                partId = id,
                defName = "PF_ShipPart_Cube",
                positionShip = pos,
                boundsSizeShip = new DVector3(size, size, size),
                hp = 100f,
            };
            p.AddComponent(new HullMassComponent { mass = mass });
            return p;
        }

        [Fact]
        public void DestroyedPart_StillCountsMassAndBounds_KeySemantics()
        {
            var e = new SpaceEntity(1, 0);
            var hull = Part(1, DVector3.zero, 4, 1000f);
            var side = Part(2, new DVector3(10, 0, 0), 2, 500f);
            e.SetParts(new List<SpacePart> { hull, side });

            double massBefore = e.TotalMass;
            double boundsBefore = e.BoundsSize.x;
            Assert.Equal(1500.0, massBefore, 3);
            Assert.Equal(13.0, boundsBefore, 9);
            Assert.True(e.HasIntactKeyPart);

            e.MarkPartDestroyed(side.partId);
            Assert.Equal(massBefore, e.TotalMass, 3);       // D2 乙：残骸质量仍在
            Assert.Equal(boundsBefore, e.BoundsSize.x, 9);  // 残骸仍参与包围盒/碰撞
            Assert.True(e.HasIntactKeyPart);                // 非关键件损毁不判失能

            e.MarkPartDestroyed(hull.partId);
            Assert.False(e.HasIntactKeyPart);               // 首件（无挂图件兜底）是关键件
        }

        [Fact]
        public void DestroyedPart_StillHitByRay()
        {
            var e = new SpaceEntity(2, 0);
            var p = Part(1, DVector3.zero, 4, 100f);
            e.SetParts(new List<SpacePart> { p });
            e.MarkPartDestroyed(p.partId);

            bool hit = CombatManager.TryPickHitPart(e, new DVector3(0, -10, 0), new DVector3(0, 1, 0), 20.0,
                out int idx, out var hitPart, out _);
            Assert.True(hit);
            Assert.Equal(0, idx);
            Assert.Same(p, hitPart);
        }

        [Fact]
        public void DestroyedPart_FunctionalStatsOut_MassStays()
        {
            var e = new SpaceEntity(3, 0);
            var hull = Part(1, DVector3.zero, 4, 1000f);
            var sensor = Part(2, new DVector3(0, 0, 6), 2, 100f);
            sensor.AddComponent(new SensorArrayComponentData { passiveRangeMeters = 1_000_000f });
            e.SetParts(new List<SpacePart> { hull, sensor });

            Assert.Equal(1_000_000.0, e.AggregatedStats.sensorRange, 3);
            double massBefore = e.TotalMass;

            e.MarkPartDestroyed(sensor.partId);
            Assert.Equal(0.0, e.AggregatedStats.sensorRange, 6);
            Assert.Equal(massBefore, e.TotalMass, 3);
        }

        [Fact]
        public void DestroyedTank_LeaksPoolAndCapacity()
        {
            var def = new PropellantDef { defName = "P" };
            var e = new SpaceEntity(4, 0);
            var hull = Part(1, DVector3.zero, 4, 1000f);
            var tankPart = Part(2, new DVector3(0, 0, 6), 2, 200f);
            var tank = new PropellantTankComponentData
            {
                propellantDefName = "P",
                capacityKg = 500f,
                massKg = 0f
            };
            tankPart.AddComponent(tank);
            e.SetParts(new List<SpacePart> { hull, tankPart });
            e.AddPropulsion(new PropulsionData
            {
                propellant = def,
                ve = 3000f,
                massFlow = 10f,
                mass = 300f,
                capacity = 500f
            });

            Assert.Equal(300.0, e.GetPropellantMass(def), 6);
            Assert.Equal(300.0, tank.massKg, 6);
            Assert.Equal(500.0, e.TotalPropellantCapacity, 6);
            Assert.Equal(500.0, e.GetTankCapacitySum("P"), 6);

            // 真实伤害路径：ApplyDamage 先置 isDestroyed/通知组件，战斗侧随后调 MarkPartDestroyed 做善后。
            tankPart.ApplyDamage(9999f);
            e.MarkPartDestroyed(tankPart.partId);

            Assert.Equal(0.0, e.GetPropellantMass(def), 6);      // 漏光：权威池同步扣减
            Assert.Equal(0.0, tank.massKg, 6);                   // 镜像清零
            Assert.Equal(0.0, e.TotalPropellantCapacity, 6);     // 容量出局
            Assert.Equal(0.0, e.GetTankCapacitySum("P"), 6);
        }

        [Fact]
        public void DestroyedEngine_RemovesFlowAndRecomputesVe()
        {
            var def = new PropellantDef { defName = "Q" };
            var e = new SpaceEntity(7, 0);
            var hull = Part(1, DVector3.zero, 4, 1000f);
            var enginePart = Part(2, new DVector3(0, 0, 6), 2, 100f);
            enginePart.AddComponent(new EngineComponentData
            {
                exhaustVelocity = 3200f,
                massFlowRate = 10f,
                propellantDefName = "Q"
            });
            e.SetParts(new List<SpacePart> { hull, enginePart });
            e.AddPropulsion(new PropulsionData
            {
                propellant = def,
                ve = 3000f,
                massFlow = 20f,
                mass = 0.0,
                capacity = 0.0
            });

            Assert.Equal(20f, e.Networks[0].MassFlow, 3);
            Assert.Equal(3000f, e.Networks[0].Ve, 3);

            enginePart.ApplyDamage(9999f);
            e.MarkPartDestroyed(enginePart.partId);

            Assert.Equal(10f, e.Networks[0].MassFlow, 3);
            // 60000 − 32000 = 28000 N ÷ 10 kg/s = 2800 m/s（混装时按 ve·ṁ 线性扣减）
            Assert.Equal(2800f, e.Networks[0].Ve, 3);
        }

        [Fact]
        public void KeyPart_MapLinkedWins_NoMapFallsBackToFirst()
        {
            var e = new SpaceEntity(5, 0);
            var cabin = Part(1, DVector3.zero, 4, 100f);
            cabin.linkedMapId = 7;
            var engine = Part(2, new DVector3(0, 0, 6), 2, 100f);
            e.SetParts(new List<SpacePart> { cabin, engine });

            Assert.True(e.IsKeyPart(cabin));
            Assert.False(e.IsKeyPart(engine));

            var mapless = new SpaceEntity(6, 0);
            var first = Part(1, DVector3.zero, 4, 100f);
            var second = Part(2, new DVector3(0, 0, 6), 2, 100f);
            mapless.SetParts(new List<SpacePart> { first, second });
            Assert.True(mapless.IsKeyPart(first));
            Assert.False(mapless.IsKeyPart(second));
        }

        [Fact]
        public void CrippledGroup_RejectsNewBurn()
        {
            var g = new SpaceEntityGroup(1, 0);
            g.StartBurn(new DVector3(10, 0, 0));
            Assert.True(g.HasActiveBurn);
            g.EndBurn();

            g.IsCrippled = true;
            g.StartBurn(new DVector3(10, 0, 0));
            Assert.False(g.HasActiveBurn);
        }

        [Fact]
        public void BoardingAim_IncludesTargetPartOffset()
        {
            DVector3 aim = BoardingMath.AimPointSystem(
                new DVector3(100, 0, 0), new DVector3(0, 10, 0),
                Quaternion.identity, new DVector3(5, 0, 0), new DVector3(1, 0, 2));

            Assert.Equal(106.0, aim.x, 9);
            Assert.Equal(10.0, aim.y, 9);
            Assert.Equal(2.0, aim.z, 9);

            // 旋转 90° 绕 Y：part 偏移与落点偏移一起转（+X → -Z）。
            DVector3 rotated = BoardingMath.AimPointSystem(
                DVector3.zero, DVector3.zero,
                Quaternion.Euler(0, 90, 0), new DVector3(5, 0, 0), new DVector3(1, 0, 0));
            Assert.Equal(0.0, rotated.x, 5);
            Assert.Equal(-6.0, rotated.z, 5);
        }
    }
}
