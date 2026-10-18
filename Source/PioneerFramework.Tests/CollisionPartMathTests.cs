using System.Collections.Generic;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **批 3 真碰撞的纯几何部分**（2026-10-17）：最小穿透轴（碰1）与单 part 世界 AABB（窄相基础）。
    /// </summary>
    public class CollisionPartMathTests
    {
        [Fact]
        public void MinPenetrationAxis_PicksSmallestOverlap_WithSign()
        {
            var n = CombatManager.MinPenetrationAxis(1.0, 2.0, 3.0,
                new DVector3(0, 0, 0), new DVector3(0.5, 0, 0), out double depth);
            Assert.Equal(1.0, depth, 12);
            Assert.Equal(1.0, n.x, 12);
            Assert.Equal(0.0, n.y, 12);
            Assert.Equal(0.0, n.z, 12);

            var n2 = CombatManager.MinPenetrationAxis(1.0, 2.0, 3.0,
                new DVector3(0, 0, 0), new DVector3(-0.5, 0, 0), out _);
            Assert.Equal(-1.0, n2.x, 12);
        }

        [Fact]
        public void GetPartAabbSystem_UsesPartPositionAndBounds()
        {
            var e = new SpaceEntity(1, 0);
            e.SetParts(new List<SpacePart>
            {
                new()
                {
                    partId = 1,
                    defName = "PF_ShipPart_Cube",
                    positionShip = new DVector3(0, 10, 0),
                    boundsSizeShip = new DVector3(4, 6, 8),
                    hp = 100f,
                },
            });

            Assert.True(e.GetPartAabbSystem(0, out DVector3 min, out DVector3 max));
            Assert.Equal(-2.0, min.x, 9);
            Assert.Equal(7.0, min.y, 9);
            Assert.Equal(-4.0, min.z, 9);
            Assert.Equal(2.0, max.x, 9);
            Assert.Equal(13.0, max.y, 9);
            Assert.Equal(4.0, max.z, 9);
        }

        [Fact]
        public void GetPartAabbSystem_RejectsOutOfRange_IncludesDestroyed()
        {
            var e = new SpaceEntity(1, 0);
            var p = new SpacePart { partId = 1, positionShip = DVector3.zero, boundsSizeShip = new DVector3(2, 2, 2) };
            e.SetParts(new List<SpacePart> { p });
            Assert.False(e.GetPartAabbSystem(1, out _, out _));

            // B5 / D-P7：损毁残骸仍参与碰撞 ⇒ AABB 仍然有效（不再“穿过空气”）。
            p.isDestroyed = true;
            Assert.True(e.GetPartAabbSystem(0, out _, out _));
        }

        // ── 碰 3 修订（2026-10-17 用户裁 A）：并集盒空角不算接触 ──────────────

        [Fact]
        public void CollisionMass_IncludesPropellant()
        {
            // 2026-10-17 用户裁定：燃料参与碰撞动量交换（干质量 + 工质），下限 1 kg。
            Assert.Equal(1500.0, CombatManager.CollisionMassKg(1000.0, 500.0), 12);
            Assert.Equal(1.0, CombatManager.CollisionMassKg(0.0, 0.0), 12);
        }

        [Fact]
        public void GroupAabbFallback_OnlyWhenEitherSideHasNoUsableParts()
        {
            Assert.False(CombatManager.ShouldFallbackToGroupAabb(1, 1));
            Assert.False(CombatManager.ShouldFallbackToGroupAabb(7, 7));
            Assert.True(CombatManager.ShouldFallbackToGroupAabb(0, 7));
            Assert.True(CombatManager.ShouldFallbackToGroupAabb(7, 0));
            Assert.True(CombatManager.ShouldFallbackToGroupAabb(0, 0));
        }

        [Fact]
        public void AlignImpactNormal_FlipsDegeneratePartNormalAgainstCenterLine()
        {
            // S1 退化场景：最深 part 对中心完全重合 ⇒ SAT 的 delta=0，轴符号按轴序取 +X；
            // 但枚举顺序让 A 是右船（centerDelta = -X）⇒ 组级法线必须翻成 -X。
            var flipped = CombatManager.AlignImpactNormal(new DVector3(1, 0, 0), new DVector3(-12, 0, 0));
            Assert.Equal(-1.0, flipped.x, 12);

            var kept = CombatManager.AlignImpactNormal(new DVector3(1, 0, 0), new DVector3(12, 0, 0));
            Assert.Equal(1.0, kept.x, 12);

            // 组心重合：无方向可依 ⇒ 保留原法线（交给一步跨过/相对速度判据）。
            var zero = CombatManager.AlignImpactNormal(new DVector3(0, 1, 0), DVector3.zero);
            Assert.Equal(1.0, zero.y, 12);
        }

        [Fact]
        public void S1DegenerateContact_CoincidentSideParts_NormalAlignedToGroupCenters()
        {
            // 对称船正撞的首次接触：两船 ±6 侧件在世界系**完全重合**（S1 实测）。
            DQuaternion id = DQuaternion.identity;
            var half = new DVector3(1.5, 5, 6);
            Assert.True(PartGeometry.ObbObbSat(new DVector3(6, 0, 0), half, id,
                new DVector3(6, 0, 0), half, id, out DVector3 n, out _, out _));

            // A 是右船、B 是左船 ⇒ 组心连线 -X。对齐后法线必须指向 B（-X），
            // 否则 vRel<0 被当分离，位置分离还会把两船推进彼此怀里（S1 记录 3 条的直接成因）。
            DVector3 aligned = CombatManager.AlignImpactNormal(n, new DVector3(-12, 0, 0));
            Assert.True(aligned.x < 0.0);
        }

        [Fact]
        public void DeepestPartPair_UnionBoxesOverlapButPartsDoNot_ReturnsFalse()
        {
            // A = 两个小盒（x≈0 与 x≈10）⇒ 并集 x∈[-1,11]；
            // B = 两个小盒（x≈4.5/5.5）⇒ 并集 x∈[3.5,6.5]。
            // 两个**并集**盒重叠（x 3.5~6.5），但没有任何 A-B part 对重叠（A 的两块都在外侧）。
            var a = new List<CombatManager.PartAabb>
            {
                Box(-1, -1, -1, 1, 1, 1),
                Box(9, -1, -1, 11, 1, 1),
            };
            var b = new List<CombatManager.PartAabb>
            {
                Box(3.5, -1, -1, 5.5, 1, 1),
                Box(4.5, -1, -1, 6.5, 1, 1),
            };

            bool hit = CombatManager.TryFindDeepestPartPair(a, b, out _, out double depth, out _);
            Assert.False(hit);
            Assert.Equal(0.0, depth, 12);
        }

        private static CombatManager.PartAabb Box(double minX, double minY, double minZ,
            double maxX, double maxY, double maxZ)
            => new(new DVector3(minX, minY, minZ), new DVector3(maxX, maxY, maxZ));
    }
}
