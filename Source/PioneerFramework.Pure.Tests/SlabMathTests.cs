using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 线段-AABB slab 相交纯数学单测（2026-09-08 从 CombatGrid 抽出）。
    /// 重点：**退化零长线段**必须按点-盒包含判定——旧实现三轴 slab 全跳过 → tNear/tFar 保持 ±∞
    /// → 恒判命中，导致弹群场景"组刚创建首拍 8 枚全转化"（日志实证：接触点 = 各弹自身前一位置）。
    /// </summary>
    public class SlabMathTests
    {
        private static readonly DVector3 Min = new DVector3(-30, -25, -30);
        private static readonly DVector3 Max = new DVector3(30, 25, 30);

        [Fact]
        public void DegenerateSegment_InsideBox_Hits()
        {
            DVector3 p = new DVector3(10, 0, -5);
            Assert.True(SlabMath.RaySlabIntersection(p, p, Min, Max, out double tNear, out _, out _));
            Assert.Equal(0.0, tNear, 9);
        }

        [Fact]
        public void DegenerateSegment_OutsideBox_Misses()
        {
            // 弹群首拍回归：实体在目标盒外 5km，零长段必须判"不接触"（旧版恒真 → 8 枚全转化）
            DVector3 p = new DVector3(5000, 0, 0);
            Assert.False(SlabMath.RaySlabIntersection(p, p, Min, Max, out _, out _, out _));
            DVector3 p2 = new DVector3(0, 26, 0); // 仅 y 轴超出
            Assert.False(SlabMath.RaySlabIntersection(p2, p2, Min, Max, out _, out _, out _));
        }

        [Fact]
        public void CrossingSegment_HitsWithEntryParameter()
        {
            // 从 -1000 到 +1000（沿 x）：应命中，tEntry ≈ (1000-30)/2000 = 0.485
            Assert.True(SlabMath.RaySlabIntersection(
                new DVector3(-1000, 0, 0), new DVector3(1000, 0, 0), Min, Max,
                out double tNear, out double tFar, out _));
            Assert.Equal(0.485, tNear, 6);
            Assert.Equal(0.515, tFar, 6);
        }

        [Fact]
        public void ParallelSegment_OutsideSlab_Misses()
        {
            // 平行于 x 轴但 z 偏 100m：不命中
            Assert.False(SlabMath.RaySlabIntersection(
                new DVector3(-1000, 0, 100), new DVector3(1000, 0, 100), Min, Max, out _, out _, out _));
        }

        [Fact]
        public void SegmentPassingBeyondBox_Misses()
        {
            // 段整体在盒前方（未到达）或整体在盒后方（已越过）：都不命中
            Assert.False(SlabMath.RaySlabIntersection(
                new DVector3(-1000, 0, 0), new DVector3(-500, 0, 0), Min, Max, out _, out _, out _));
            Assert.False(SlabMath.RaySlabIntersection(
                new DVector3(500, 0, 0), new DVector3(1000, 0, 0), Min, Max, out _, out _, out _));
        }

        [Fact]
        public void HighSpeedPass_StillHits()
        {
            // 5.2km/步 高速穿越（弹群首拍场景）：单段必须命中（扫掠语义）
            Assert.True(SlabMath.RaySlabIntersection(
                new DVector3(-2600, 0, -300), new DVector3(2600, 0, 300), Min, Max, out _, out _, out _));
        }
    }
}
