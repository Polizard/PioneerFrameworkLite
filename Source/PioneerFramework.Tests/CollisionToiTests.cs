using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **碰 6（步内 TOI 响应）**：扫掠命中点必须给出接触面法线，供冲击/爆炸响应用。
    /// </summary>
    public class CollisionToiTests
    {
        [Fact]
        public void TunneledStop_HeadOnFromPositiveX_NormalPointsPositiveX()
        {
            // A 盒 [-5,5]^3；B 半宽 3 ⇒ 膨胀盒 [-8,8]；B 从 +X 向原点冲
            bool ok = CombatManager.TryComputeTunneledStopWithNormal(
                new DVector3(100, 0, 0), DVector3.zero,
                new DVector3(-3, -3, -3), new DVector3(3, 3, 3),
                new DVector3(-5, -5, -5), new DVector3(5, 5, 5),
                out DVector3 stop, out DVector3 normal);

            Assert.True(ok);
            Assert.Equal(1.0, normal.x, 9);
            Assert.Equal(0.0, normal.y, 9);
            Assert.Equal(0.0, normal.z, 9);
            Assert.InRange(stop.x, 8.0, 8.2);   // 留 0.1m 防嵌入
        }

        [Fact]
        public void TunneledStop_HeadOnFromNegativeX_NormalPointsNegativeX()
        {
            bool ok = CombatManager.TryComputeTunneledStopWithNormal(
                new DVector3(-100, 0, 0), DVector3.zero,
                new DVector3(-3, -3, -3), new DVector3(3, 3, 3),
                new DVector3(-5, -5, -5), new DVector3(5, 5, 5),
                out _, out DVector3 normal);

            Assert.True(ok);
            Assert.Equal(-1.0, normal.x, 9);
        }

        [Fact]
        public void TunneledStop_StartAlreadyInsideExpandedBox_NoNewCapture()
        {
            // 上一步停在接触点会留下 0.1m 余量（膨胀盒 [-8,8] 内）；此时起点 7.9 已在内
            // ⇒ 没有"进入面"，必须返回 false，否则每步把 B 钉回旧相对位置（黏着回环）。
            bool ok = CombatManager.TryComputeTunneledStopWithNormal(
                new DVector3(7.9, 0, 0), DVector3.zero,
                new DVector3(-3, -3, -3), new DVector3(3, 3, 3),
                new DVector3(-5, -5, -5), new DVector3(5, 5, 5),
                out _, out _);

            Assert.False(ok);
        }

        [Fact]
        public void TunneledStop_StartOutsideExpandedBox_StillCaptures()
        {
            // 边界另一侧：8.1 在膨胀盒外，正常捕获（防把真 tunneling 一并关掉）
            bool ok = CombatManager.TryComputeTunneledStopWithNormal(
                new DVector3(8.1, 0, 0), DVector3.zero,
                new DVector3(-3, -3, -3), new DVector3(3, 3, 3),
                new DVector3(-5, -5, -5), new DVector3(5, 5, 5),
                out _, out _);

            Assert.True(ok);
        }
    }
}
