using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>目录显式热源（2026-10-26 用户裁定 A）：`ShipPartDef.heatProductionWatts` →
    /// **同一个** `EngineComponentData` → `ShipStats.heatProduction`（不再恒 0）。</summary>
    public class EngineHeatCatalogTests
    {
        private static ShipPartDef Thruster(float heat, float dissipation = 0f) => new()
        {
            defName = "Test_Thruster",
            label = "测试推进器",
            category = "thruster",
            massKg = 500f,
            exhaustVelocity = 3000f,
            massFlowRate = 10f,
            propellantDefName = "P",
            heatProductionWatts = heat,
            heatDissipationWatts = dissipation,
            presets = new List<PartComponentPreset>(),
        };

        [Fact]
        public void Assembler_WritesCatalogHeat_IntoEngineComponent()
        {
            var def = Thruster(30_000f);
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "Test_Thruster", category = "thruster", quantity = 1 },
                },
            };

            var parts = DesignPartAssembler.BuildParts(spec, 1, 1, out _, _ => def,
                includeUnknownDefs: false, includeCabin: false);

            var engine = parts[0].GetComponent<EngineComponentData>();
            Assert.NotNull(engine);
            Assert.Equal(30_000f, engine!.heatProductionWatts, 3);
        }

        [Fact]
        public void Entity_HeatAggregation_IncludesEngineHeat()
        {
            var def = Thruster(12_500f);
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "Test_Thruster", category = "thruster", quantity = 1 },
                },
            };
            var e = new SpaceEntity(1, 0);
            e.SetParts(DesignPartAssembler.BuildParts(spec, 1, 1, out _, _ => def,
                includeUnknownDefs: false, includeCabin: false));

            Assert.Equal(12_500f, e.AggregatedStats.heatProduction, 3);
        }

        [Fact]
        public void EngineNozzle_Dissipation_IsItsOwnField_AndAggregates()
        {
            var def = Thruster(30_000f, dissipation: 10_000f);
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "Test_Thruster", category = "thruster", quantity = 1 },
                },
            };
            var e = new SpaceEntity(2, 0);
            e.SetParts(DesignPartAssembler.BuildParts(spec, 1, 1, out _, _ => def,
                includeUnknownDefs: false, includeCabin: false));

            var engine = e.Parts[0].GetComponent<EngineComponentData>();
            Assert.NotNull(engine);
            Assert.Equal(10_000f, engine!.heatDissipationWatts, 3);
            Assert.Equal(30_000f, engine.heatProductionWatts, 3);
            Assert.Equal(10_000f, e.AggregatedStats.heatDissipation, 3);
            // 不额外挂散热器组件（喷口散热是引擎自己的属性）
            Assert.Null(e.Parts[0].GetComponent<RadiatorComponentData>());
        }
    }
}
