using System;
using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B3a/B3b 零件姿态几何**：OBB→AABB 投影、identity 退化、射线-OBB、OBB-SAT 窄相。
    /// </summary>
    public class PartGeometryTests
    {
        private static DQuaternion Yaw90 => DQuaternion.AngleAxis(Math.PI / 2.0, DVector3.up);

        [Fact]
        public void Identity_WorldAabb_MatchesAxisAligned()
        {
            PartGeometry.WorldAabb(new DVector3(1, 2, 3), new DVector3(4, 2, 6),
                DQuaternion.identity, DQuaternion.identity, out var min, out var max);

            Assert.Equal(-1.0, min.x, 12);
            Assert.Equal(1.0, min.y, 12);
            Assert.Equal(0.0, min.z, 12);
            Assert.Equal(3.0, max.x, 12);
            Assert.Equal(3.0, max.y, 12);
            Assert.Equal(6.0, max.z, 12);
        }

        [Fact]
        public void RotatedBox_WorldAabb_SwapsExtents()
        {
            // 4(长轴 x) × 2 × 2，绕 Y 转 90° ⇒ 长轴到 z；世界半尺寸 (1,1,2)
            PartGeometry.WorldAabb(DVector3.zero, new DVector3(4, 2, 2),
                DQuaternion.identity, Yaw90, out var min, out var max);

            Assert.Equal(-1.0, min.x, 12);
            Assert.Equal(-1.0, min.y, 12);
            Assert.Equal(-2.0, min.z, 12);
            Assert.Equal(1.0, max.x, 12);
            Assert.Equal(1.0, max.y, 12);
            Assert.Equal(2.0, max.z, 12);
        }

        [Fact]
        public void ZeroEntityRotation_TreatedAsIdentity()
        {
            // B2 遗留：DQuaternion 默认零四元数与零件姿态相乘会退化；几何层必须把零当 identity。
            PartGeometry.WorldAabb(DVector3.zero, new DVector3(4, 2, 2),
                default, Yaw90, out var min, out var max);

            Assert.Equal(-1.0, min.x, 12);
            Assert.Equal(-2.0, min.z, 12);
            Assert.Equal(1.0, max.x, 12);
            Assert.Equal(2.0, max.z, 12);
        }

        [Fact]
        public void RayObb_HitsRotatedBox_WithSegmentT()
        {
            // 线段 z: -10 → +10，旋转后盒的长轴沿 z（半长 2）⇒ 入口 z=-2 ⇒ t=8/20=0.4
            bool hit = PartGeometry.RayObb(
                new DVector3(0, 0, -10), new DVector3(0, 0, 10),
                DVector3.zero, Yaw90, new DVector3(4, 2, 2), out double t);

            Assert.True(hit);
            Assert.Equal(0.4, t, 12);
        }

        [Fact]
        public void RayObb_MissesOutsideRotatedBox()
        {
            // x=1.5 在旋转后盒的 x 半宽 1 之外 ⇒ 不命中（旧 AABB 会误判命中）
            bool hit = PartGeometry.RayObb(
                new DVector3(1.5, 0, -10), new DVector3(1.5, 0, 10),
                DVector3.zero, Yaw90, new DVector3(4, 2, 2), out _);

            Assert.False(hit);
        }

        [Fact]
        public void ObbSat_TwoAxisAlignedBoxes_MatchesAabb()
        {
            bool hit = PartGeometry.ObbObbSat(
                DVector3.zero, new DVector3(1, 1, 1), DQuaternion.identity,
                new DVector3(1.5, 0, 0), new DVector3(1, 1, 1), DQuaternion.identity,
                out DVector3 n, out double depth, out DVector3 contact);

            Assert.True(hit);
            Assert.Equal(0.5, depth, 9);
            Assert.True(n.x > 0.99 && Math.Abs(n.y) < 1e-9 && Math.Abs(n.z) < 1e-9);
            Assert.Equal(0.75, contact.x, 9);
        }

        [Fact]
        public void ObbSat_RotatedLongBox_OverlapsSmallBox()
        {
            // 4×2×2 绕 Y 90° ⇒ 世界 x 半宽 1；小盒中心 x=1.2 半宽 0.5 ⇒ 重叠 0.3
            bool hit = PartGeometry.ObbObbSat(
                DVector3.zero, new DVector3(2, 1, 1), Yaw90,
                new DVector3(1.2, 0, 0), new DVector3(0.5, 0.5, 0.5), DQuaternion.identity,
                out DVector3 n, out double depth, out _);

            Assert.True(hit);
            Assert.Equal(0.3, depth, 9);
            Assert.True(n.x > 0.99);
        }

        [Fact]
        public void Narrowphase_RotatedPart_UsesObbSat()
        {
            // 45° 长盒 vs 小盒：AABB 重叠 0.821，而 SAT 最小轴（盒的局部 z）只有 ~0.434
            // ⇒ 只有走 OBB-SAT 才能得到 <0.8 的深度（AABB 回退会给出 0.821）。
            var a = new List<CombatManager.PartAabb>
            {
                new(DVector3.zero, new DVector3(2, 1, 1),
                    DQuaternion.AngleAxis(Math.PI / 4.0, DVector3.up), 0),
            };
            var b = new List<CombatManager.PartAabb>
            {
                new(new DVector3(1.8, 0, 0), new DVector3(0.5, 0.5, 0.5), DQuaternion.identity, 0),
            };

            bool hit = CombatManager.TryFindDeepestPartPair(a, b, out _, out double depth, out _);
            Assert.True(hit);
            Assert.True(depth > 0.0 && depth < 0.8, $"OBB-SAT depth 应 <0.8（实际 {depth:F6}）");
        }
    }
}
