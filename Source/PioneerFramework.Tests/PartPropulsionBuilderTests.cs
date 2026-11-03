using System;
using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **零件 → 推进合成**（2026-10-26）：一个 part 叠引擎+罐就该有 Δv，不区分导弹/卫星/火箭。
    /// 三条约束：只补空（不覆盖显式推进）· 只无图 · 等效 ve 按质量流加权。
    /// </summary>
    public class PartPropulsionBuilderTests
    {
        private static readonly PropellantDef Prop = new() { defName = "P" };
        private static PropellantDef? Lookup(string name) => name == "P" ? Prop : null;

        private static SpacePart EnginePart(int id, float ve, float flow, bool rcs = false)
        {
            var p = new SpacePart { partId = id, defName = "engine", boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new EngineComponentData
            {
                exhaustVelocity = ve,
                massFlowRate = flow,
                propellantDefName = "P",
                isVectoredRcs = rcs,
            });
            return p;
        }

        private static SpacePart TankPart(int id, double capacity, double mass)
        {
            var p = new SpacePart { partId = id, defName = "tank", boundsSizeShip = new DVector3(2, 2, 2) };
            p.AddComponent(new PropellantTankComponentData
            {
                propellantDefName = "P",
                capacityKg = capacity,
                massKg = mass,
            });
            return p;
        }

        [Fact]
        public void SinglePart_EnginePlusTank_ProducesPropulsion()
        {
            var e = new SpaceEntity(1, 0);
            var part = new SpacePart { partId = 1, defName = "missile", boundsSizeShip = new DVector3(2, 2, 2) };
            part.AddComponent(new EngineComponentData { exhaustVelocity = 3000f, massFlowRate = 10f, propellantDefName = "P" });
            part.AddComponent(new PropellantTankComponentData { propellantDefName = "P", capacityKg = 1000.0, massKg = 500.0 });
            e.SetParts(new List<SpacePart> { part });

            Assert.True(PartPropulsionBuilder.BuildForMapless(e, Lookup));

            Assert.True(e.HasPropulsionData);
            Assert.True(e.PropulsionFromParts);
            Assert.Equal(3000f, e.propulsion[0].ve, 3);
            Assert.Equal(10f, e.propulsion[0].massFlow, 3);
            Assert.Equal(1000f, e.propulsion[0].capacity, 3);
            Assert.Equal(500f, e.propulsion[0].mass, 3);
        }

        [Fact]
        public void MixedEngines_EquivalentVeIsFlowWeighted()
        {
            var e = new SpaceEntity(2, 0);
            e.SetParts(new List<SpacePart>
            {
                EnginePart(1, 3000f, 10f),
                EnginePart(2, 1500f, 2f),
                TankPart(3, 2000.0, 800.0),
            });

            Assert.True(PartPropulsionBuilder.BuildForMapless(e, Lookup));

            // (3000×10 + 1500×2) / 12 = 2750
            Assert.Equal(2750f, e.propulsion[0].ve, 3);
            Assert.Equal(12f, e.propulsion[0].massFlow, 3);
        }

        [Fact]
        public void ExplicitPropulsion_IsNotOverwritten()
        {
            var e = new SpaceEntity(3, 0);
            e.SetParts(new List<SpacePart> { EnginePart(1, 3000f, 10f), TankPart(2, 1000.0, 0.0) });
            e.AddPropulsion(new PropulsionData { propellant = Prop, ve = 900f, massFlow = 2f, mass = 0.0, capacity = 0.0 });

            Assert.False(PartPropulsionBuilder.BuildForMapless(e, Lookup));

            Assert.Equal(1, e.propulsion.Count);
            Assert.Equal(900f, e.propulsion[0].ve, 3);
        }

        [Fact]
        public void Rebuild_TracksTankChange()
        {
            var e = new SpaceEntity(4, 0);
            var tank = TankPart(2, 1000.0, 0.0);
            e.SetParts(new List<SpacePart> { EnginePart(1, 3000f, 10f), tank });
            Assert.True(PartPropulsionBuilder.BuildForMapless(e, Lookup));
            Assert.Equal(1000f, e.propulsion[0].capacity, 3);

            tank.GetComponent<PropellantTankComponentData>()!.capacityKg = 2500.0;
            PartPropulsionBuilder.RebuildMapless(e, Lookup);

            Assert.Equal(1, e.propulsion.Count);
            Assert.Equal(2500f, e.propulsion[0].capacity, 3);
        }

        [Fact]
        public void DestroyedEngine_IsExcluded()
        {
            var e = new SpaceEntity(5, 0);
            var engine = EnginePart(1, 3000f, 10f);
            e.SetParts(new List<SpacePart> { engine, TankPart(2, 1000.0, 0.0) });
            Assert.True(PartPropulsionBuilder.BuildForMapless(e, Lookup));

            engine.ApplyDamage(9999f);
            engine.isDestroyed = true;        // ApplyDamage 已置真；这里显式一次，保证不依赖实现细节
            e.MarkPartDestroyed(engine.partId);

            // 重建后没有可用引擎 ⇒ 没有网络（`InvalidateStats` 已在 MarkPartDestroyed 里跑过一次）
            PartPropulsionBuilder.RebuildMapless(e, Lookup);
            Assert.False(e.HasPropulsionData);
        }

        [Fact]
        public void TankWithoutEngine_ProducesNoNetwork()
        {
            var e = new SpaceEntity(6, 0);
            e.SetParts(new List<SpacePart> { TankPart(1, 1000.0, 500.0) });

            Assert.False(PartPropulsionBuilder.BuildForMapless(e, Lookup));
            Assert.False(e.HasPropulsionData);
        }

        [Fact]
        public void MapShip_IsStillNotTouched()
        {
            // 守卫不变：`HasMap` 的实体推进在地图侧。发射出来的飞船现在是 `HasMap = false`
            // （照验收路径 `new SpaceEntity{HasMap=false}`），所以不再需要任何"放行"开关。
            var e = new SpaceEntity(9, 0) { HasMap = true };
            e.SetParts(new List<SpacePart> { EnginePart(1, 3000f, 10f), TankPart(2, 1000.0, 500.0) });

            Assert.False(PartPropulsionBuilder.BuildForMapless(e, Lookup));
            Assert.False(e.HasPropulsionData);
        }
    }
}
