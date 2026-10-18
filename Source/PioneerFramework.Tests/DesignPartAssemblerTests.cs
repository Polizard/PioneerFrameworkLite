using System;
using System.Collections.Generic;
using PioneerFramework.GroundAssembly;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **M1 第一刀 · 规格 → part 列表**（2026-10-17）：
    /// 舱盒恒第一件（地图载体、原点、质量公式不变）；追加件按 spec 顺序沿 −Y 堆叠、不挂图；
    /// 目录命中用 def 几何/质量，缺失走类别兜底；数量有上限。
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
            // 舱盒底面 y=-25 ⇒ 首件中心 -27；储罐中心依次 -32、-38
            Assert.Equal(-27.0, parts[1].positionShip.y, 9);
            Assert.Equal(-32.0, parts[2].positionShip.y, 9);
            Assert.Equal(-38.0, parts[3].positionShip.y, 9);
            Assert.Equal(new DVector3(2, 4, 2), parts[1].boundsSizeShip);
            Assert.Equal(new DVector3(2, 6, 2), parts[2].boundsSizeShip);

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
            Assert.Equal(new DVector3(3, 5, 7), parts[1].boundsSizeShip);
            Assert.Equal(-25.0 - 2.5, parts[1].positionShip.y, 9);   // 舱盒厚 50 ⇒ 底面 -25，stack 5 中心再 -2.5
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
            Assert.True(parts[^1].positionShip.y < -500.0);
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
    }
}
