using System;
using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **设计器：级标签 + 块号 + 显式位姿预览**（2026-11-03，§1.3a/§1.6）。
    /// · 两道设计门对 `useExplicitPose` 行豁免（不是新增第三道门）；
    /// · 级三态/并联开关与块号可编辑；
    /// · 侧视图按**绝对船体 z** 排布，显式行不再被顺序堆叠。
    /// </summary>
    public class StageLabelDesignerTests
    {
        private static ShipPartDef Tank(string name, int stage = 0, float w = 3f, float h = 10f)
            => new()
            {
                defName = name, label = name, category = "tank", massKg = 2000f,
                tankCapacityKg = 20000f, tankInitialKg = 20000f,
                boundsX = w, boundsZ = w, boundsY = h, stackLengthMeters = h,
            };

        private static ShipPartDef Engine(string name, int stage = 0, float w = 2f, float h = 3f)
            => new()
            {
                defName = name, label = name, category = "thruster", massKg = 500f,
                boundsX = w, boundsZ = w, boundsY = h, stackLengthMeters = h,
                exhaustVelocity = 3000f, massFlowRate = 200f,
            };

        [Fact]
        public void ExplicitPoseRows_AreExemptFromBothStackingGates()
        {
            // 引擎在级首（违规），但后面的结构行改成显式位姿 ⇒ 引擎门豁免
            var comp = new CompRocketAssembler();
            comp.AddPart(Engine("E"), 1, stageId: 1);
            comp.AddPart(Tank("T"), 1, stageId: 1);
            Assert.NotEqual("", comp.EnginePlacementError());

            comp.Design.parts[1].useExplicitPose = true;
            Assert.Equal("", comp.EnginePlacementError());

            // 级行被别的级隔开（违规）；显式行豁免后恢复合法
            var comp2 = new CompRocketAssembler();
            comp2.AddPart(Tank("A"), 1, stageId: 1);
            comp2.AddPart(Tank("B"), 1, stageId: 0);
            comp2.AddPart(Tank("C"), 1, stageId: 1);
            Assert.NotEqual("", comp2.StageContiguityError());

            comp2.Design.parts[2].useExplicitPose = true;
            Assert.Equal("", comp2.StageContiguityError());
        }

        [Fact]
        public void StageToggles_KeepRecoverableParallel_AndBlockId()
        {
            var comp = new CompRocketAssembler();
            comp.AddPart(Tank("A"), 1, stageId: 0);
            comp.AddPart(Engine("E"), 1, stageId: 0);

            Assert.True(comp.SetStageKeep(0, true));
            Assert.True(comp.IsStageKeep(0));
            Assert.False(comp.SetStageRecoverable(0, true));   // 不抛弃与可回收互斥
            Assert.False(comp.IsStageRecoverable(0));

            Assert.True(comp.SetStageKeep(0, false));
            Assert.True(comp.SetStageRecoverable(0, true));
            Assert.True(comp.IsStageRecoverable(0));

            Assert.True(comp.SetStageParallel(0, true));
            Assert.True(comp.IsStageParallel(0));

            Assert.True(comp.SetPartBlock(0, 7));
            Assert.Equal(7, comp.Design.parts[0].blockId);
        }

        [Fact]
        public void Schematic_ExplicitPose_UsesAbsoluteZAndLateralOffset()
        {
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "T", category = "tank", stageId = 0 },
                    new() { defName = "E", category = "thruster", stageId = 0 },
                    new()
                    {
                        defName = "B", category = "thruster", stageId = 1,
                        useExplicitPose = true, blockId = 2,
                        localPosition = new DVector3(3, 0, -5),
                    },
                },
            };

            var s = RocketSchematicBuilder.Build(spec, _ => null);   // 占位 2×2×2

            Assert.Equal(3, s.pieces.Count);
            Assert.Equal(2.0, s.pieces[0].heightM, 5);
            Assert.Equal(-1.0, s.pieces[0].centerZM, 5);   // 堆叠：-h/2
            Assert.Equal(-3.0, s.pieces[1].centerZM, 5);
            Assert.Equal(3.0, s.pieces[2].centerXM, 5);    // 显式横向偏移
            Assert.Equal(-5.0, s.pieces[2].centerZM, 5);   // 显式绝对 z
            Assert.True(s.pieces[2].explicitPose);
            Assert.Equal(6.0, s.totalHeightM, 5);          // 0 … -6
        }

        [Fact]
        public void ActiveSet_DrivesReadinessAndThrust()
        {
            var defs = new Dictionary<string, ShipPartDef>();
            ShipPartDef Add(ShipPartDef d) { defs[d.defName] = d; return d; }
            ShipPartDef? Lookup(string n) => defs.TryGetValue(n, out var d) ? d : null;

            var comp = new CompRocketAssembler();
            comp.AddPart(Add(Tank("C")), 1, stageId: 0);
            comp.AddPart(Add(Engine("CE")), 1, stageId: 0);
            comp.AddPart(Add(Tank("B")), 1, stageId: 1);
            comp.AddPart(Add(Engine("BE")), 1, stageId: 1);

            Assert.Single(comp.ActiveStages);
            Assert.Contains(1, comp.ActiveStages);
            float solo = RocketSchematicBuilder.ThrustN(comp.Design, Lookup, comp.ActiveStages);
            Assert.True(Math.Abs(solo - 600_000f) < 1f, $"solo={solo}");

            Assert.True(comp.SetStageParallel(0, true));
            Assert.Equal(2, comp.ActiveStages.Count);
            Assert.Contains(0, comp.ActiveStages);
            float both = RocketSchematicBuilder.ThrustN(comp.Design, Lookup, comp.ActiveStages);
            Assert.True(Math.Abs(both - 1_200_000f) < 1f, $"both={both}");
            Assert.True(comp.ActiveStageReady);
        }

        [Fact]
        public void RadialCopy_ReplacesRowWithFourExplicitBlocks()
        {
            var defs = new Dictionary<string, ShipPartDef>();
            ShipPartDef Add(ShipPartDef d) { defs[d.defName] = d; return d; }

            var comp = new CompRocketAssembler();
            comp.AddPart(Add(Tank("C", w: 4f, h: 10f)), 1, stageId: 0);
            comp.AddPart(Add(Engine("CE")), 1, stageId: 0);
            comp.AddPart(Add(Tank("B", w: 2f, h: 10f)), 1, stageId: 1);
            comp.AddPart(Add(Engine("BE")), 1, stageId: 1);

            int idx = 2;   // 助推器罐行
            ShipPartDef? Lookup(string n) => defs.TryGetValue(n, out var d) ? d : null;
            Assert.True(comp.RadialCopy(idx, 4, lookup: Lookup));

            Assert.Equal(7, comp.Design.parts.Count);   // 4 - 1 + 4
            var blocks = new HashSet<int>();
            foreach (var p in comp.Design.parts)
            {
                if (p.stageId != 1 || p.category != "tank") continue;
                Assert.True(p.useExplicitPose);
                blocks.Add(p.blockId);
                // 自动半径 ≥ 芯半宽 2 + 本体半宽 1 + 缝 0.3
                Assert.True(Math.Abs(p.localPosition.x) + Math.Abs(p.localPosition.y) >= 3.3 - 1e-3,
                    $"({p.localPosition.x},{p.localPosition.y}) 太靠近轴");
            }
            Assert.Equal(4, blocks.Count);
        }

        [Fact]
        public void R7DemoDesign_HasCorePlusFourBoosters_AndPassesBothGates()
        {
            var defs = new Dictionary<string, ShipPartDef>();
            ShipPartDef Add(ShipPartDef d) { defs[d.defName] = d; return d; }
            Add(Tank("PF_ShipPart_Tank_Kerolox_Medium", w: 3f, h: 8f));
            Add(Tank("PF_ShipPart_Tank_Kerolox_Heavy", w: 4f, h: 12f));
            Add(Engine("PF_ShipPart_Engine_Lifter_Small"));
            Add(new ShipPartDef
            {
                defName = "PF_ShipPart_ControlCore_Debug", category = "control", massKg = 100f,
                boundsX = 2f, boundsY = 2f, boundsZ = 2f,
            });

            var spec = CompRocketAssembler.BuildR7TestDesign(
                n => defs.TryGetValue(n, out var d) ? d : null, out string reason);

            Assert.NotNull(spec);
            Assert.Equal("", reason);
            Assert.Equal(11, spec.parts.Count);              // 3 芯级 + 4 罐 + 4 引擎
            Assert.Contains(0, spec.parallelIgnitionStageIds);
            Assert.Equal("", RocketDesignRules.EnginePlacementError(spec));
            Assert.Equal("", RocketDesignRules.StageContiguityError(spec));

            int tanks = 0, engines = 0;
            var blocks = new HashSet<int>();
            foreach (var p in spec.parts)
            {
                if (p.stageId != 1) continue;
                Assert.True(p.useExplicitPose);
                blocks.Add(p.blockId);
                if (p.category == "tank") tanks++;
                else if (p.category == "thruster") engines++;
            }
            Assert.Equal(4, tanks);
            Assert.Equal(4, engines);
            Assert.Equal(4, blocks.Count);

            var active = RocketDesignRules.ActiveStages(spec);
            Assert.Contains(0, active);
            Assert.Contains(1, active);
        }

        [Fact]
        public void AddPartRadial_PlacesAroundSelectedParent_WithBlocks()
        {
            var defs = new Dictionary<string, ShipPartDef>();
            ShipPartDef Add(ShipPartDef d) { defs[d.defName] = d; return d; }
            ShipPartDef? Lookup(string n) => defs.TryGetValue(n, out var d) ? d : null;

            var comp = new CompRocketAssembler();
            comp.AddPart(Add(Tank("C", w: 4f, h: 10f)), 1, stageId: 0);
            comp.AddPart(Add(Engine("CE")), 1, stageId: 0);

            Assert.True(comp.AddPartRadial(Add(Tank("B", w: 2f, h: 8f)), 4, parentIndex: 0,
                stageId: 1, lookup: Lookup));

            Assert.Equal(6, comp.Design.parts.Count);   // 芯级 2 行 + 径向 4 份
            int n = 0;
            var blocks = new HashSet<int>();
            foreach (var p in comp.Design.parts)
            {
                if (p.stageId != 1) continue;
                n++;
                Assert.True(p.useExplicitPose);
                Assert.Equal(0, p.parentIndex);        // 父件 = 芯级罐行
                blocks.Add(p.blockId);
                // 自动半径 = 芯半宽 2 + 本体半宽 1 + 缝 0.3
                Assert.True(Math.Abs(p.localPosition.x) + Math.Abs(p.localPosition.y) >= 3.3 - 1e-3,
                    $"({p.localPosition.x},{p.localPosition.y}) 太靠近轴");
            }
            Assert.Equal(4, n);
            Assert.Equal(4, blocks.Count);
            Assert.Equal("", RocketDesignRules.EnginePlacementError(comp.Design));
            Assert.Equal("", RocketDesignRules.StageContiguityError(comp.Design));
        }

        [Fact]
        public void AddPartAxialTo_RadialTank_KeepsBlockAndXY()
        {
            var defs = new Dictionary<string, ShipPartDef>();
            ShipPartDef Add(ShipPartDef d) { defs[d.defName] = d; return d; }
            ShipPartDef? Lookup(string n) => defs.TryGetValue(n, out var d) ? d : null;

            var comp = new CompRocketAssembler();
            comp.AddPart(Add(Tank("C", w: 4f, h: 10f)), 1, stageId: 0);
            comp.AddPart(Add(Engine("CE")), 1, stageId: 0);

            Assert.True(comp.AddPartRadial(Add(Tank("B", w: 2f, h: 8f)), 2, parentIndex: 0,
                stageId: 1, lookup: Lookup));

            int tankRow = comp.Design.parts.FindIndex(p => p.stageId == 1 && p.category == "tank");
            Assert.True(comp.AddPartAxialTo(Add(Engine("BE", h: 3f)), tankRow, below: true, lookup: Lookup));

            var tank = comp.Design.parts[tankRow];
            var eng = comp.Design.parts[comp.Design.parts.Count - 1];
            Assert.True(eng.useExplicitPose);
            Assert.Equal(tank.blockId, eng.blockId);       // 同一块：抛级时罐+机一起走
            Assert.Equal(tank.stageId, eng.stageId);
            Assert.Equal(tank.localPosition.x, eng.localPosition.x, 5);
            Assert.Equal(tank.localPosition.y, eng.localPosition.y, 5);
            Assert.True(eng.localPosition.z < tank.localPosition.z);
        }
    }
}
