using PioneerFramework;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 落点格映射纯逻辑测试（MapCoordMapper 无 Map 重载）。
    /// 约定：1 格 = 1 米，地图中心 = 船体局部原点，局部米 x/z 叠加中心格取整，越界收敛到边界。
    /// </summary>
    public class MapCoordMapperTests
    {
        private static readonly IntVec2 Map60 = new IntVec2(60, 60);
        private static readonly IntVec3 Center = new IntVec3(30, 0, 30);

        [Fact]
        public void LocalToMapCell_AddsOffsetToCenter()
        {
            var cell = MapCoordMapper.LocalToMapCell(Center, new Vector3(10, 0, 20), Map60);
            Assert.Equal(new IntVec3(40, 0, 50), cell);
        }

        [Fact]
        public void LocalToMapCell_NegativeOffset()
        {
            var cell = MapCoordMapper.LocalToMapCell(Center, new Vector3(-10, 0, -20), Map60);
            Assert.Equal(new IntVec3(20, 0, 10), cell);
        }

        [Fact]
        public void LocalToMapCell_ClampsOutOfBounds()
        {
            Assert.Equal(new IntVec3(59, 0, 59),
                MapCoordMapper.LocalToMapCell(Center, new Vector3(100, 0, 100), Map60));
            Assert.Equal(new IntVec3(0, 0, 0),
                MapCoordMapper.LocalToMapCell(Center, new Vector3(-100, 0, -100), Map60));
        }

        [Fact]
        public void LocalToMapCell_RoundsToNearestCell()
        {
            // Math.Round(10.4)=10 → 40；Math.Round(20.6)=21 → 51
            var cell = MapCoordMapper.LocalToMapCell(Center, new Vector3(10.4f, 0, 20.6f), Map60);
            Assert.Equal(new IntVec3(40, 0, 51), cell);
        }

        [Fact]
        public void CellLocalRoundTrip_ReturnsSameCell()
        {
            // 格 → 局部米（纯重载）→ 格（纯重载）应回到原格
            var cell = new IntVec3(42, 0, 17);
            var local = MapCoordMapper.MapCellToLocal3D(Center, cell);
            var back = MapCoordMapper.LocalToMapCell(Center, local, Map60);
            Assert.Equal(cell, back);
        }

        [Fact]
        public void CenterCell_MapsToZeroOffset_AndBack()
        {
            var local = MapCoordMapper.MapCellToLocal3D(Center, Center);
            Assert.Equal(0f, local.x);
            Assert.Equal(0f, local.z);
            Assert.Equal(Center, MapCoordMapper.LocalToMapCell(Center, local, Map60));
        }

        // ── RayExitAabb：F2 炮塔出膛点（船体局部系六面求交）──
        // 盒 = 60×60 站（1 格 = 1 米）半长 (30, 25, 30)，炮口默认在盒内原点。

        private static void AssertNear(Vector3 actual, Vector3 expected, int precision = 2)
        {
            Assert.Equal(expected.x, actual.x, precision);
            Assert.Equal(expected.y, actual.y, precision);
            Assert.Equal(expected.z, actual.z, precision);
        }

        [Theory]
        [InlineData(1f, 0f, 0f, 30f, 0f, 0f, 1)]   // +x → x+ 面
        [InlineData(-1f, 0f, 0f, -30f, 0f, 0f, 0)]  // -x → x- 面
        [InlineData(0f, 1f, 0f, 0f, 25f, 0f, 3)]   // +y → y+ 面（上表面）
        [InlineData(0f, -1f, 0f, 0f, -25f, 0f, 2)]  // -y → y- 面（下表面）
        [InlineData(0f, 0f, 1f, 0f, 0f, 30f, 5)]   // +z → z+ 面
        [InlineData(0f, 0f, -1f, 0f, 0f, -30f, 4)]  // -z → z- 面
        public void RayExitAabb_AxisAligned_ExitsCorrectFace(
            float dx, float dy, float dz,
            float ex, float ey, float ez, int expectFace)
        {
            var origin = Vector3.zero;
            var exit = MapCoordMapper.RayExitAabb(origin, new Vector3(dx, dy, dz),
                new Vector3(-30, -25, -30), new Vector3(30, 25, 30), out int face);
            AssertNear(exit, new Vector3(ex, ey, ez));
            Assert.Equal(expectFace, face);
        }

        [Fact]
        public void RayExitAabb_Diagonal_ExitsClosestFace()
        {
            // 对角线 (1,1,0)/√2：到 y+ 面 t=25/0.707≈35.4 < 到 x+ 面 t=30/0.707≈42.4 → 出上表面
            var origin = Vector3.zero;
            var dir = new Vector3(1f, 1f, 0f).normalized;
            var exit = MapCoordMapper.RayExitAabb(origin, dir,
                new Vector3(-30, -25, -30), new Vector3(30, 25, 30), out int face);
            AssertNear(exit, new Vector3(25f, 25f, 0f));
            Assert.Equal(3, face);
        }

        [Fact]
        public void RayExitAabb_OffsetOrigin_ExitsCorrectFace()
        {
            // 炮口偏离中心（靠 z- 侧）：沿 +x 仍从 x+ 面出，y/z 保持炮口高度
            var origin = new Vector3(5f, 0f, -10f);
            var exit = MapCoordMapper.RayExitAabb(origin, new Vector3(1f, 0f, 0f),
                new Vector3(-30, -25, -30), new Vector3(30, 25, 30), out int face);
            AssertNear(exit, new Vector3(30f, 0f, -10f));
            Assert.Equal(1, face);
        }

        [Fact]
        public void RayExitAabb_ZeroDirection_ReturnsOrigin()
        {
            var origin = new Vector3(3f, 4f, 5f);
            var exit = MapCoordMapper.RayExitAabb(origin, Vector3.zero,
                new Vector3(-30, -25, -30), new Vector3(30, 25, 30), out int face);
            Assert.Equal(origin, exit);
            Assert.Equal(-1, face);
        }
    }
}