using System;
using System.Collections.Generic;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **M1 第一刀 · 规格 → part 列表**（2026-10-17）：
    /// 舱盒恒第一件（地图载体、原点、质量公式不变）；追加件按 spec 顺序沿 **−Z** 堆叠（2026-10-28 轴改）、不挂图；
    /// 目录命中用 def 几何/质量，缺失走类别兜底；数量有上限；并联站同层（2026-10-29 见
    /// `RocketAssemblerParallelTests`）。
    /// </summary>
    public class DesignPartAssemblerTests
    {
        private static RocketDesignSpec DebugSpec(params DesignPartSpec[] entries)
            => new() { parts = new List<DesignPartSpec>(entries) };

        private static DesignPartSpec Entry(string defName, string category, int qty = 1)
            => new() { defName = defName, category = category, quantity = qty };

        [Fact]
        public void BuildParts_CabinOnly_WhenSpecHasNoParts()
        {
            var spec = DebugSpec();
            var parts = DesignPartAssembler.BuildParts(spec, 50, 50, out SpacePart cabin, _ => null);

            Assert.Single(parts);
            Assert.Same(cabin, parts[0]);
            Assert.Equal(1, cabin.partId);
            Assert.Equal(DVector3.zero, cabin.positionShip);
            Assert.Equal(new DVector3(50, 50, 50), cabin.boundsSizeShip);
            Assert.True(cabin.isIndestructible);
            Assert.Equal(-1, cabin.linkedMapId);
            // B8：part 层不再逐个兜底 1kg；空舱盒 mass=0，整船质量 0 时实体层兜底 1kg。
            Assert.Equal(0.0, cabin.Mass, 3);
        }

        [Fact]
        public void BuildParts_DebugSpec_StacksBelowCabin_AndDoesNotLinkMap()
        {
            var spec = DebugSpec(
                Entry("PF_ShipPart_Thruster_Debug", "thruster", 1),
                Entry("PF_ShipPart_Tank_Debug", "tank", 2));

            var parts = DesignPartAssembler.BuildParts(spec, 50, 50, out SpacePart cabin, _ => null);

            // 舱盒 + 1 推进器 + 2 储罐；舱盒仍第一件（LinkEntityToMap 取首件的语义不变）
            Assert.Equal(4, parts.Count);
            Assert.Same(cabin, parts[0]);

            // 兜底几何：thruster 2×4×2（stack 4）、tank 2×6×2（stack 6）
            // 2026-10-28 轴改（"横着飞的，改吧"）：堆叠方向与长度落在 **Z**（机头 +Z），
            // 舱盒底面 z=-25 ⇒ 首件中心 -27；储罐中心依次 -32、-38。
            Assert.Equal(-27.0, parts[1].positionShip.z, 9);
            Assert.Equal(-32.0, parts[2].positionShip.z, 9);
            Assert.Equal(-38.0, parts[3].positionShip.z, 9);
            Assert.Equal(new DVector3(2, 2, 4), parts[1].boundsSizeShip);
            Assert.Equal(new DVector3(2, 2, 6), parts[2].boundsSizeShip);

            // 追加件不挂图；partId 连续
            for (int i = 1; i < parts.Count; i++)
            {
                Assert.Equal(-1, parts[i].linkedMapId);
                Assert.Equal(i + 1, parts[i].partId);
            }

            // 质量 = Σ零件（封闭舱盒 100kg 占位 + 目录/兜底质量）
            double total = 0.0;
            foreach (var p in parts) total += p.Mass;
            Assert.Equal(SpacePart.SealedPlaceholderMassKg + 500.0 + 2 * 200.0, total, 3);
        }

        [Fact]
        public void BuildParts_CatalogDef_WinsOverFallback()
        {
            var fake = new ShipPartDef
            {
                defName = "PF_ShipPart_Thruster_Debug",
                category = "thruster",
                massKg = 1234f,
                boundsX = 3f, boundsY = 5f, boundsZ = 7f,
                stackLengthMeters = 5f,
            };
            var spec = DebugSpec(Entry("PF_ShipPart_Thruster_Debug", "thruster", 1));

            var parts = DesignPartAssembler.BuildParts(spec, 10, 12, out SpacePart cabin,
                name => name == fake.defName ? fake : null);

            Assert.Equal(2, parts.Count);
            Assert.Equal(1234.0, parts[1].Mass, 3);
            // 2026-10-28 轴改：目录的 boundsY(5) = 堆叠长度 ⇒ 落到 Z；横向 X=3、Y=7
            Assert.Equal(new DVector3(3, 7, 5), parts[1].boundsSizeShip);
            Assert.Equal(-25.0 - 2.5, parts[1].positionShip.z, 9);   // 舱盒厚 50 ⇒ 底面 -25，stack 5 中心再 -2.5
            // B6 习惯默认：多 part 船 ⇒ 舱盒封闭 ⇒ 100kg 占位（不是 1kg 地板）
            Assert.Equal(SpacePart.SealedPlaceholderMassKg, cabin.Mass, 3);
        }

        [Fact]
        public void BuildParts_256Parts_AtCap()
        {
            var spec = DebugSpec(Entry("PF_ShipPart_Stage_Debug", "stage", 255));
            var parts = DesignPartAssembler.BuildParts(spec, 40, 40, out SpacePart cabin, _ => null, includeUnknownDefs: true);

            Assert.Equal(DesignPartAssembler.MaxTotalParts, parts.Count);
            Assert.Same(cabin, parts[0]);
            Assert.True(cabin.isSealed);
            Assert.Equal(DesignPartAssembler.MaxTotalParts, parts[^1].partId);
            Assert.True(parts[^1].positionShip.z < -500.0);   // 2026-10-28 轴改：堆叠在 −Z
        }

        [Fact]
        public void BuildParts_UnknownCategory_UsesGenericFallback()
        {
            var spec = DebugSpec(Entry("PF_ShipPart_Unknown", "mystery", 1));
            var parts = DesignPartAssembler.BuildParts(spec, 4, 4, out _, _ => null);

            Assert.Equal(2, parts.Count);
            Assert.Equal(100.0, parts[1].Mass, 3);
            Assert.Equal(new DVector3(2, 2, 2), parts[1].boundsSizeShip);
        }

        [Fact]
        public void BuildParts_ClampsAbsurdQuantity()
        {
            var spec = DebugSpec(Entry("PF_ShipPart_Tank_Debug", "tank", 1000));
            var parts = DesignPartAssembler.BuildParts(spec, 4, 4, out _, _ => null);

            Assert.Equal(DesignPartAssembler.MaxTotalParts, parts.Count);
        }

        [Fact]
        public void BuildParts_ClampsTotalParts_AcrossEntries()
        {
            // 三条各 100 件 ⇒ 300 + 舱盒，必须在**整船总数** 256 处截断（不是每条目各 256）
            var spec = DebugSpec(
                Entry("PF_ShipPart_Tank_Debug", "tank", 100),
                Entry("PF_ShipPart_Tank_Debug", "tank", 100),
                Entry("PF_ShipPart_Tank_Debug", "tank", 100));
            var parts = DesignPartAssembler.BuildParts(spec, 4, 4, out _, _ => null);

            Assert.Equal(DesignPartAssembler.MaxTotalParts, parts.Count);
        }

        [Fact]
        public void BuildParts_ProductionMode_SkipsUncataloguedDefs()
        {
            // 生产接线（includeUnknownDefs:false）：目录没收录的条目**不追加** ⇒ 既有 DMS/ThingDef 设计零变化
            var spec = DebugSpec(Entry("DMS_SomeThingDefPart", "thruster", 3));
            var parts = DesignPartAssembler.BuildParts(spec, 50, 50, out _, _ => null,
                includeUnknownDefs: false);

            Assert.Single(parts);
        }

        [Fact]
        public void BuildParts_CatalogCylinderShape_IsCopiedToPart()
        {
            // 2026-10-29：目录里 tank/thruster 标 <shape>Cylinder</shape> ⇒ 装配器把形状抄到 part
            // （惯量/渲染/碰撞都按圆柱走）。圆柱轴向 = 体 Z ⇒ 高 = 堆叠长度（boundsY 换轴后的 Z）。
            var def = new ShipPartDef
            {
                defName = "PF_Test_CylTank", category = "tank", massKg = 2000f,
                boundsX = 4f, boundsY = 40f, boundsZ = 4f, stackLengthMeters = 40f,
                tankCapacityKg = 400000f, tankInitialKg = 400000f,
                shape = InertiaTensor.Shape.Cylinder,
            };
            var parts = DesignPartAssembler.BuildParts(DebugSpec(Entry("PF_Test_CylTank", "tank", 1)),
                10, 10, out _, name => name == def.defName ? def : null,
                includeUnknownDefs: false, includeCabin: false);

            var part = Assert.Single(parts);
            Assert.Equal(InertiaTensor.Shape.Cylinder, part.shape);
            Assert.Equal(new DVector3(4, 4, 40), part.boundsSizeShip);   // 直径在 X/Y、高在 Z
            Assert.Equal(-20.0, part.positionShip.z, 6);
        }

        [Fact]
        public void Schematic_CarriesCylinderShape_ForRoundDrawing()
        {
            var def = new ShipPartDef
            {
                defName = "PF_Test_CylEngine", category = "thruster", massKg = 500f,
                boundsX = 2f, boundsY = 3f, boundsZ = 2f, stackLengthMeters = 3f,
                exhaustVelocity = 3000f, massFlowRate = 200f,
                shape = InertiaTensor.Shape.Cylinder,
            };
            var s = RocketSchematicBuilder.Build(DebugSpec(Entry("PF_Test_CylEngine", "thruster", 1)),
                name => name == def.defName ? def : null);

            var p = Assert.Single(s.pieces);
            Assert.Equal(InertiaTensor.Shape.Cylinder, p.shape);         // 侧视图据此画圆头
            Assert.Equal(3f, p.heightM, 6);
        }

        [Fact]
        public void BuildParts_ExplicitPose_Shape_Stage_AndParent()
        {
            DQuaternion rot = DQuaternion.AngleAxis(System.Math.PI / 2.0, DVector3.up);
            var parent = Entry("PF_ShipPart_Thruster_Debug", "thruster", 1);
            var child = new DesignPartSpec
            {
                defName = "PF_ShipPart_Tank_Debug",
                category = "tank",
                quantity = 1,
                useExplicitPose = true,
                localPosition = new DVector3(5, 6, 7),
                localRotation = rot,
                useShapeOverride = true,
                shapeOverride = InertiaTensor.Shape.Cylinder,
                stageId = 2,
                parentIndex = 0,
            };
            var parts = DesignPartAssembler.BuildParts(DebugSpec(parent, child), 10, 10, out _, _ => null);

            // 舱盒 + 推进器 + 显式姿态储罐
            Assert.Equal(3, parts.Count);
            var append = parts[2];
            Assert.Equal(new DVector3(5, 6, 7), append.positionShip);
            Assert.Equal(rot, append.rotationShip);
            Assert.Equal(InertiaTensor.Shape.Cylinder, append.shape);
            Assert.Equal(2, append.stageId);
            Assert.Equal(parts[1].partId, append.parentId);
        }

        // ── 级标签 / 块号 / 父级（2026-11-03）────────────────────
        [Fact]
        public void BuildParts_BakesBlockIdDispositionAndParentStage()
        {
            var parent = Entry("PF_ShipPart_Tank_Debug", "tank");
            parent.stageId = 0;
            var child = new DesignPartSpec
            {
                defName = "PF_ShipPart_Thruster_Debug",
                category = "thruster",
                stageId = 1,
                blockId = 3,
                parentIndex = 0,
            };
            var spec = DebugSpec(parent, child);
            spec.keepStageIds.Add(0);
            spec.recoverableStageIds.Add(1);

            var parts = DesignPartAssembler.BuildParts(spec, 10, 10, out _, _ => null);

            Assert.Equal(StageDisposition.Keep, parts[1].stageDisposition);
            Assert.Equal(StageDisposition.JettisonRecoverable, parts[2].stageDisposition);
            Assert.Equal(0, parts[1].blockId);
            Assert.Equal(3, parts[2].blockId);
            Assert.Equal(-1, parts[1].parentStageId);
            Assert.Equal(0, parts[2].parentStageId);
        }

        [Fact]
        public void BuildParts_LegacyDefaults_BlockZeroJettisonNoParentStage()
        {
            var parts = DesignPartAssembler.BuildParts(
                DebugSpec(Entry("PF_ShipPart_Tank_Debug", "tank")), 10, 10, out _, _ => null);

            Assert.Equal(0, parts[1].blockId);
            Assert.Equal(StageDisposition.Jettison, parts[1].stageDisposition);
            Assert.Equal(-1, parts[1].parentStageId);
        }

        // ── 级间空隙（2026-10-30 用户方案：级边界留空当 ⇒ 抛级瞬间两组不接触）────

        private static DesignPartSpec Staged(string defName, string category, int stage, int qty = 1)
            => new() { defName = defName, category = category, quantity = qty, stageId = stage };

        [Fact]
        public void StageGap_LeavesClearanceAtStageBoundary()
        {
            // 无舱盒、从 z=0 起堆：两件 tank 兜底 2×6×2（stack 6）⇒ 无缝隙时中心 -3 / -9
            var spec = DebugSpec(
                Staged("PF_ShipPart_Tank_Debug", "tank", 0),
                Staged("PF_ShipPart_Tank_Debug", "tank", 1));

            var noGap = DesignPartAssembler.BuildParts(spec, 10, 10, out _,
                _ => null, includeCabin: false);
            var gap = DesignPartAssembler.BuildParts(spec, 10, 10, out _,
                _ => null, includeCabin: false, stageGapMeters: 0.3f);

            Assert.Equal(-3.0, noGap[0].positionShip.z, 9);
            Assert.Equal(-9.0, noGap[1].positionShip.z, 9);
            Assert.Equal(-3.0, gap[0].positionShip.z, 9);
            // zCursor 是 float（历史口径）⇒ 0.3 落成 0.30000019；按 float 精度判，别用 9 位
            Assert.Equal(-9.3, gap[1].positionShip.z, 5);
            Assert.Equal(0.3, noGap[1].positionShip.z - gap[1].positionShip.z, 5);

            // 表面间距（箭尾方向）= 3 + 3 + 0.3
            double surface = (gap[0].positionShip.z - gap[0].boundsSizeShip.z * 0.5)
                           - (gap[1].positionShip.z + gap[1].boundsSizeShip.z * 0.5);
            Assert.Equal(0.3, surface, 5);
        }

        [Fact]
        public void StageGap_SameStage_IsNoOp()
        {
            var spec = DebugSpec(
                Staged("PF_ShipPart_Tank_Debug", "tank", 0),
                Staged("PF_ShipPart_Tank_Debug", "tank", 0));
            var noGap = DesignPartAssembler.BuildParts(spec, 10, 10, out _,
                _ => null, includeCabin: false);
            var gap = DesignPartAssembler.BuildParts(spec, 10, 10, out _,
                _ => null, includeCabin: false, stageGapMeters: 0.3f);

            Assert.Equal(noGap[1].positionShip.z, gap[1].positionShip.z, 9);
        }

        [Fact]
        public void StageGap_ExplicitPose_IsNotShifted()
        {
            var child = new DesignPartSpec
            {
                defName = "PF_ShipPart_Tank_Debug", category = "tank", quantity = 1,
                useExplicitPose = true, localPosition = new DVector3(5, 6, 7), stageId = 2,
            };
            var parts = DesignPartAssembler.BuildParts(
                DebugSpec(Staged("PF_ShipPart_Tank_Debug", "tank", 0), child),
                10, 10, out _, _ => null, includeCabin: false, stageGapMeters: 0.3f);

            Assert.Equal(new DVector3(5, 6, 7), parts[1].positionShip);   // 显式位姿由调用方权威
        }

        [Fact]
        public void Schematic_IncludesStageGap()
        {
            var spec = DebugSpec(
                Staged("PF_ShipPart_Tank_Debug", "tank", 0),
                Staged("PF_ShipPart_Thruster_Debug", "thruster", 1));

            var noGap = RocketSchematicBuilder.Build(spec, _ => null);
            var gap = RocketSchematicBuilder.Build(spec, _ => null, stageGapMeters: 0.3f);

            Assert.Equal(0f, noGap.stageGapM, 6);
            Assert.Equal(0.3f, gap.stageGapM, 6);
            Assert.Equal(noGap.totalHeightM + 0.3f, gap.totalHeightM, 5);

            // 单级设计：空隙不生效
            var one = DebugSpec(Staged("PF_ShipPart_Tank_Debug", "tank", 0, 2));
            Assert.Equal(RocketSchematicBuilder.Build(one, _ => null).totalHeightM,
                         RocketSchematicBuilder.Build(one, _ => null, stageGapMeters: 0.3f).totalHeightM, 5);
        }
    }
}
