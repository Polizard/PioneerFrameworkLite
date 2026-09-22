using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **选 tile 主动降落的报价**（`Base/Orbital/LandingQuote.cs`，2026-09-22）。
    ///
    /// 这批用例钉的是**口径**而不是实现细节：
    /// · Δv = |v 船 − v 落点地表|（同系向量差）—— 含**天体平动**；
    /// · 距离门 = min(1000 km, 0.35×R) —— 地球 1000 km / 月球 525 km；
    /// · 参数不全 ⇒ **拒绝**，不冒充可行。
    ///
    /// ⚠ 本库坐标约定：**在 p = +x̂ 处"东向"速度是 +ẑ**（见 `SurfaceFrame` 类注释的推论）——
    ///   下面所有"顺行赤道"的构造都按这条写。
    /// </summary>
    public class LandingQuoteTests
    {
        // ── 地球（XML 真值：R 5000 km、μ 2e14、一天 60000 tick）──
        private const double R_Earth = 5_000_000.0;
        private const double Mu_Earth = 2.0e14;
        private static readonly double OmegaEarth = 2.0 * System.Math.PI / 60000.0;

        // ── 月球（XML 真值：R 1500 km、μ 5e12、自转 160000 tick、绕地 8e7 m / 160000 tick）──
        private const double R_Moon = 1_500_000.0;
        private const double Mu_Moon = 5.0e12;
        private static readonly double OmegaMoon = 2.0 * System.Math.PI / 160000.0;
        private static readonly double MoonOrbitSpeed = 2.0 * System.Math.PI * 8.0e7 / 160000.0;   // ≈3141.6

        private static readonly DVector3 Zero = DVector3.zero;

        /// <summary>
        /// ⭐ 基准读数：近地圆轨（500 km）正下方就是在自转方向上的赤道落点
        /// ⇒ 报价 = **圆轨速度 − 地表共转速度**（两者同向，逐位可算）。
        /// </summary>
        [Fact]
        public void LeoEquatorial_QuoteIsOrbitalMinusSurfaceSpeed()
        {
            double r = R_Earth + 500_000.0;
            double vCirc = System.Math.Sqrt(Mu_Earth / r);                 // 6030.2
            var shipPos = new DVector3(r, 0, 0);
            var shipVel = new DVector3(0, 0, vCirc);                       // 顺行（东向 = +ẑ）

            var q = LandingQuote.Evaluate(shipPos, shipVel, Zero, Zero,
                R_Earth, OmegaEarth, new DVector3(1, 0, 0), 0.0);

            Assert.True(q.ok, q.reason);
            Assert.Equal(vCirc - OmegaEarth * R_Earth, q.deltaV, 3);        // 5506.628
            Assert.Equal(0.0, q.surfaceDistance, 3);                       // 正下方 ⇒ 距离 0
            Assert.Equal(1_000_000.0, q.maxDistance, 3);
        }

        /// <summary>
        /// ⭐⭐ **平动不变性**（本核最容易错的一处）：把"船 + 天体"整体平移/给一个常速度
        /// ⇒ 报价与地表距离**必须逐位不变**。丢了天体平动就不满足这条。
        /// </summary>
        [Fact]
        public void TranslationInvariance_QuoteIsUnchanged()
        {
            double r = R_Earth + 500_000.0;
            var shipPos0 = new DVector3(r, 0, 0);
            var shipVel0 = new DVector3(0, 0, System.Math.Sqrt(Mu_Earth / r));

            var dPos = new DVector3(1.0e7, -2.0e6, 3.0e6);
            var dVel = new DVector3(-40.0, 15.0, 220.0);

            var q0 = LandingQuote.Evaluate(shipPos0, shipVel0, Zero, Zero,
                R_Earth, OmegaEarth, new DVector3(1, 0, 0), 0.0);
            var q1 = LandingQuote.Evaluate(shipPos0 + dPos, shipVel0 + dVel, dPos, dVel,
                R_Earth, OmegaEarth, new DVector3(1, 0, 0), 0.0);

            Assert.Equal(q0.deltaV, q1.deltaV, 6);
            Assert.Equal(q0.surfaceDistance, q1.surfaceDistance, 6);
        }

        /// <summary>
        /// 绕月圆轨（月面上方 1800 km，XML 的"绕月轨道测试"档）落月球赤道
        /// ⇒ 报价 ≈ **1.17 km/s**（对照真实登月下降 1.7–2 km/s，同量级）。
        /// </summary>
        [Fact]
        public void MoonOrbit_QuoteIsAboutOnePointTwoKmPerSecond()
        {
            double r = R_Moon + 1_800_000.0;
            double vCirc = System.Math.Sqrt(Mu_Moon / r);                   // 1230.9
            var q = LandingQuote.Evaluate(new DVector3(r, 0, 0), new DVector3(0, 0, vCirc),
                Zero, Zero, R_Moon, OmegaMoon, new DVector3(1, 0, 0), 0.0);

            Assert.True(q.ok, q.reason);
            Assert.Equal(vCirc - OmegaMoon * R_Moon, q.deltaV, 2);          // 1172.01
            Assert.InRange(q.deltaV, 1100.0, 1250.0);
        }

        /// <summary>
        /// ⭐⭐ **绕地落月**（用户明确要的那条）：落点状态 = 月球的**绕地平动** + 月球固连自转。
        /// 反面钉住：若把平动丢掉，报价会差出整整一个"月球绕地速度"（3141.6 m/s）⇒ 这条测试能区分两者。
        /// </summary>
        [Fact]
        public void EarthOrbit_ToMoonTile_IncludesMoonOrbitalVelocity()
        {
            var moonOffPos = new DVector3(8.0e7, 0, 0);                     // 月球在 +x̂ 方向
            var moonOffVel = new DVector3(0, 0, MoonOrbitSpeed);            // 绕地速度（向东）
            double r = R_Earth + 500_000.0;
            double vCirc = System.Math.Sqrt(Mu_Earth / r);
            var shipPos = new DVector3(r, 0, 0);                            // 地球低轨
            var shipVel = new DVector3(0, 0, vCirc);

            // 落点取月球**朝向地球那一侧**的赤道点 ⇒ 星下点正好在它方向上（距离门内）
            var q = LandingQuote.Evaluate(shipPos, shipVel, moonOffPos, moonOffVel,
                R_Moon, OmegaMoon, new DVector3(-1, 0, 0), 0.0);

            Assert.True(q.ok, q.reason);
            double expect = vCirc - (MoonOrbitSpeed - OmegaMoon * R_Moon);   // ≈2947.5
            Assert.Equal(expect, q.deltaV, 2);

            // 反面：漏掉月球平动的算法会得到 vCirc + ω_m·R_m ≈ 6089 ⇒ 两者相差一整个绕地速度
            double wrong = vCirc + OmegaMoon * R_Moon;
            Assert.True(System.Math.Abs(q.deltaV - wrong) > MoonOrbitSpeed * 0.99,
                $"报价没有把月球绕地平动算进去：{q.deltaV:F1} vs 反例 {wrong:F1}");
        }

        /// <summary>距离门随天体缩：地球吃"绝对上限 1000 km"，月球吃"0.35×R = 525 km"。</summary>
        [Fact]
        public void DistanceGate_ScalesWithBodyRadius()
        {
            Assert.Equal(1_000_000.0, LandingQuote.MaxSurfaceDistance(R_Earth), 3);
            Assert.Equal(525_000.0, LandingQuote.MaxSurfaceDistance(R_Moon), 3);
        }

        /// <summary>距离门两侧：门内可落、门外拒绝（拒绝时必须给出距离与上界两个数）。</summary>
        [Theory]
        [InlineData(0.9, true)]     // 0.9 × 门 ⇒ 收
        [InlineData(1.1, false)]    // 1.1 × 门 ⇒ 拒
        [InlineData(2.0, false)]
        public void DistanceGate_BothSidesOfThreshold(double factor, bool expectOk)
        {
            double r = R_Earth + 500_000.0;
            double vCirc = System.Math.Sqrt(Mu_Earth / r);
            double ang = factor * LandingQuote.MaxSurfaceDistance(R_Earth) / R_Earth;

            // 与星下点（+x̂）成 ang 角的落点方向（xz 面内）
            var tileDir = new DVector3(System.Math.Cos(ang), 0.0, System.Math.Sin(ang));
            var q = LandingQuote.Evaluate(new DVector3(r, 0, 0), new DVector3(0, 0, vCirc),
                Zero, Zero, R_Earth, OmegaEarth, tileDir, 0.0);

            Assert.Equal(expectOk, q.ok);
            Assert.Equal(ang * R_Earth, q.surfaceDistance, 3);
            if (!expectOk)
                Assert.True(q.surfaceDistance > q.maxDistance);
        }

        /// <summary>参数不全 ⇒ **拒绝**（不冒充可行），且理由非空 —— 与本库"缺参不冒充"的既有纪律一致。</summary>
        [Fact]
        public void MissingParams_AreRejectedNotAssumed()
        {
            double r = R_Earth + 500_000.0;
            var pos = new DVector3(r, 0, 0);
            var vel = new DVector3(0, 0, System.Math.Sqrt(Mu_Earth / r));
            var dir = new DVector3(1, 0, 0);

            var noRadius = LandingQuote.Evaluate(pos, vel, Zero, Zero, 0.0, OmegaEarth, dir, 0.0);
            var noOmega = LandingQuote.Evaluate(pos, vel, Zero, Zero, R_Earth, 0.0, dir, 0.0);
            var noDir = LandingQuote.Evaluate(pos, vel, Zero, Zero, R_Earth, OmegaEarth, DVector3.zero, 0.0);

            foreach (var q in new[] { noRadius, noOmega, noDir })
            {
                Assert.False(q.ok);
                Assert.False(string.IsNullOrEmpty(q.reason));
            }
        }
    }
}
