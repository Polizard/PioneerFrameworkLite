using PioneerFramework;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 船-船碰撞扫掠膨胀盒（tunneling 修复）核心逻辑测试。
    /// 验证：离散采样在"每步位移 &gt; 重叠窗口"时会漏检，而「当前 AABB ∪ 上一步 AABB」并集能捕获。
    /// </summary>
    public class CollisionSweptTests
    {
        [Fact]
        public void AabbOverlap_BasicOverlap()
        {
            // A 盒 x∈[0,10], B 盒 x∈[5,15] → x 重叠量 5
            bool hit = CombatManager.AabbOverlap(
                new DVector3(0, 0, 0), new DVector3(10, 10, 10),
                new DVector3(5, 0, 0), new DVector3(15, 10, 10),
                out double ox, out _, out _);
            Assert.True(hit);
            Assert.Equal(5.0, ox, 3);
        }

        [Fact]
        public void AabbOverlap_NonOverlap()
        {
            bool hit = CombatManager.AabbOverlap(
                new DVector3(0, 0, 0), new DVector3(10, 10, 10),
                new DVector3(20, 0, 0), new DVector3(30, 10, 10),
                out _, out _, out _);
            Assert.False(hit);
        }

        [Fact]
        public void AabbOverlap_TouchingEdgesNotOverlap()
        {
            // 恰好相贴（重叠量 = 0）不算重叠
            bool hit = CombatManager.AabbOverlap(
                new DVector3(0, 0, 0), new DVector3(10, 10, 10),
                new DVector3(10, 0, 0), new DVector3(20, 10, 10),
                out double ox, out _, out _);
            Assert.False(hit);
            Assert.Equal(0.0, ox, 3);
        }

        [Fact]
        public void SweptUnion_CatchesTunneledStep()
        {
            // 模拟碰撞测试场景：A 半宽 25（盒 [-25,25]），B 半宽 20，以 30 m/s 靠近、物理步 dt=2s。
            // 单步位移 60m：上一步 B 中心 x=0（盒 [-20,20]），当前 B 中心 x=60（盒 [40,80]）。
            // 离散采样只测"当前盒"→ [40,80] 与 [-25,25] 不重叠（40 > 25）→ 漏检（tunneling）。

            // A 盒（当前，静止）
            var aMin = new DVector3(-25, -25, -25);
            var aMax = new DVector3(25, 25, 25);

            // B 当前盒（离散采样视角）
            var bCurMin = new DVector3(40, -20, -20);
            var bCurMax = new DVector3(80, 20, 20);

            bool discreteHit = CombatManager.AabbOverlap(aMin, aMax, bCurMin, bCurMax, out _, out _, out _);
            Assert.False(discreteHit); // 离散采样漏检 = 旧实现 tunneling

            // 扫掠并集：当前盒 ∪ 上一步盒（上一步中心 x=0）
            var bPrevMin = new DVector3(-20, -20, -20);
            var bPrevMax = new DVector3(20, 20, 20);
            var sweptMin = new DVector3(
                System.Math.Min(bCurMin.x, bPrevMin.x),
                System.Math.Min(bCurMin.y, bPrevMin.y),
                System.Math.Min(bCurMin.z, bPrevMin.z));
            var sweptMax = new DVector3(
                System.Math.Max(bCurMax.x, bPrevMax.x),
                System.Math.Max(bCurMax.y, bPrevMax.y),
                System.Math.Max(bCurMax.z, bPrevMax.z));

            bool sweptHit = CombatManager.AabbOverlap(aMin, aMax, sweptMin, sweptMax,
                out double ox, out _, out _);
            Assert.True(sweptHit); // 扫掠并集捕获重叠 = 新实现消除 tunneling
            Assert.Equal(45.0, ox, 3); // 重叠量 = min(25, 80) - max(-25, -20) = 25 - (-20) = 45
        }

        [Fact]
        public void SweptUnion_DoesNotFalsePositiveWhenNeverNear()
        {
            // 两船运动路径从未接近：并集不应误报
            var aMin = new DVector3(0, 0, 0);
            var aMax = new DVector3(10, 10, 10);
            // B 从 x=100 移到 x=200，远离 A
            var bMin = new DVector3(100, 0, 0);
            var bMax = new DVector3(210, 10, 10);
            bool hit = CombatManager.AabbOverlap(aMin, aMax, bMin, bMax, out _, out _, out _);
            Assert.False(hit);
        }

        [Fact]
        public void RayAabb_HitsBoxAlongPath()
        {
            // B 中心从 (-100,0,0) 一步移动 200m（dir=(200,0,0)），A 盒 x∈[-25,25]
            bool hit = CombatManager.RayAabb(
                new DVector3(-100, 0, 0), new DVector3(200, 0, 0),
                new DVector3(-25, -25, -25), new DVector3(25, 25, 25),
                out double tHit);
            Assert.True(hit);
            Assert.Equal(0.375, tHit, 3); // (-25+100)/200 = 0.375 → 停点 x=-25（盒面）
        }

        [Fact]
        public void RayAabb_MissesWhenParallelOutside()
        {
            // 射线沿 +x（y=50 超出盒 y∈[-25,25]）→ miss
            bool hit = CombatManager.RayAabb(
                new DVector3(-100, 50, 0), new DVector3(200, 0, 0),
                new DVector3(-25, -25, -25), new DVector3(25, 25, 25),
                out _);
            Assert.False(hit);
        }

        [Fact]
        public void RayAabb_HitBeyondStepRangeRejected()
        {
            // 盒在射线前方超出本步（t>1）→ 不应命中
            bool hit = CombatManager.RayAabb(
                new DVector3(0, 0, 0), new DVector3(200, 0, 0),
                new DVector3(250, -10, -10), new DVector3(260, 10, 10),
                out _);
            Assert.False(hit);
        }

        [Fact]
        public void RayAabb_OriginInsideBoxHitsAtZero()
        {
            // 起点已在盒内 → t=0 命中
            bool hit = CombatManager.RayAabb(
                new DVector3(0, 0, 0), new DVector3(100, 0, 0),
                new DVector3(-25, -25, -25), new DVector3(25, 25, 25),
                out double tHit);
            Assert.True(hit);
            Assert.Equal(0.0, tHit, 3);
        }

        [Fact]
        public void RayAabb_NegativeDirectionHits()
        {
            // 从 +x 侧向 -x 移动穿过盒
            bool hit = CombatManager.RayAabb(
                new DVector3(100, 0, 0), new DVector3(-200, 0, 0),
                new DVector3(-25, -25, -25), new DVector3(25, 25, 25),
                out double tHit);
            Assert.True(hit);
            Assert.Equal(0.375, tHit, 3); // (100-25)/200 = 0.375
        }

        [Fact]
        public void RayAabb_ObliqueDirectionHits()
        {
            // 斜向（含 x 与 y 分量）穿过盒角
            bool hit = CombatManager.RayAabb(
                new DVector3(-100, -100, 0), new DVector3(200, 200, 0),
                new DVector3(-25, -25, -25), new DVector3(25, 25, 25),
                out double tHit);
            Assert.True(hit);
            Assert.Equal(0.375, tHit, 3); // (-25+100)/200 = 0.375
        }

        [Fact]
        public void SweptUnion_YZAxesAlsoCatchTunnel()
        {
            // y 方向快速移动同样应被扫掠并集捕获
            var aMin = new DVector3(-25, -25, -25);
            var aMax = new DVector3(25, 25, 25);
            // B 上一步 y=0（盒 [-20,20]），当前 y=60（盒 [40,80]）
            var bCurMin = new DVector3(-20, 40, -20);
            var bCurMax = new DVector3(20, 80, 20);
            bool discrete = CombatManager.AabbOverlap(aMin, aMax, bCurMin, bCurMax, out _, out _, out _);
            Assert.False(discrete); // 离散采样漏检

            var sweptMin = new DVector3(-20, -20, -20); // ∪ 上一步
            var sweptMax = new DVector3(20, 80, 20);
            bool swept = CombatManager.AabbOverlap(aMin, aMax, sweptMin, sweptMax, out _, out double oy, out _);
            Assert.True(swept);
            Assert.Equal(45.0, oy, 3); // min(25,80)-max(-25,-20) = 45
        }

        [Fact]
        public void AabbOverlap_RotatedShipOverlapWindow()
        {
            // 模拟碰撞测试：A 无旋转 half (30,25,30)；B 绕 Z 转 45° → half≈(25,25,20)（投影放大）
            var aMin = new DVector3(-30, -25, -30);
            var aMax = new DVector3(30, 25, 30);
            var bMin = new DVector3(-25, -25, -20);
            var bMax = new DVector3(25, 25, 20);
            // 中心 x 差 55m（刚好相贴）→ 不重叠
            bool touch = CombatManager.AabbOverlap(
                aMin, aMax,
                new DVector3(55 - 25, -25, -20), new DVector3(55 + 25, 25, 20),
                out _, out _, out _);
            Assert.False(touch);
            // 中心 x 差 50m → 重叠 5m
            bool hit = CombatManager.AabbOverlap(
                aMin, aMax,
                new DVector3(50 - 25, -25, -20), new DVector3(50 + 25, 25, 20),
                out double ox, out _, out _);
            Assert.True(hit);
            Assert.Equal(5.0, ox, 3); // min(30,75)-max(-30,25) = 30-25 = 5
        }

        [Fact]
        public void TunneledStop_StopsAtContactSurface()
        {
            // B 一步 200m 从 (-100,0,0) 移到 (100,0,0)，A 盒 [-25,25]³，B half (10,10,10)。
            // B 中心不可进入区 = A 膨胀 halfB = [-35,35]。B 从外进入 → 命中进入面 -35。
            // t = (-35+100)/200 = 0.325；停点 x = -100 + 200×0.325 - 0.1 = -35.1（进入面内侧 0.1m）
            bool ok = CombatManager.TryComputeTunneledStop(
                new DVector3(-100, 0, 0), new DVector3(100, 0, 0),
                new DVector3(-10, -10, -10), new DVector3(10, 10, 10),
                new DVector3(-25, -25, -25), new DVector3(25, 25, 25),
                out DVector3 stop);
            Assert.True(ok);
            Assert.Equal(-35.1, stop.x, 3);
            Assert.Equal(0.0, stop.y, 3);
            Assert.Equal(0.0, stop.z, 3);
        }

        [Fact]
        public void TunneledStop_NoStopWhenPathMisses()
        {
            // B 路径与 A 无交（y 偏出）→ 不产生停点
            bool ok = CombatManager.TryComputeTunneledStop(
                new DVector3(-100, 100, 0), new DVector3(100, 100, 0),
                new DVector3(-10, 90, -10), new DVector3(10, 110, 10),
                new DVector3(-25, -25, -25), new DVector3(25, 25, 25),
                out _);
            Assert.False(ok);
        }

        [Fact]
        public void TunneledStop_NoStopWhenStationary()
        {
            // B 静止（无位移）→ 无法求交
            bool ok = CombatManager.TryComputeTunneledStop(
                new DVector3(0, 0, 0), new DVector3(0, 0, 0),
                new DVector3(-10, -10, -10), new DVector3(10, 10, 10),
                new DVector3(-25, -25, -25), new DVector3(25, 25, 25),
                out _);
            Assert.False(ok);
        }
    }
}
