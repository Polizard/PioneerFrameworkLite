using System;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **适配层要素提取的自证**（2026-09-19 思维实验的产出，**纯测试、不进游戏**）。
    ///
    /// 动机：追了一整天的"24 倍残差"里，`e` 是唯一一个隔了**一层转换**的量
    /// （`CurrentOrbit → OrbitView → Astro.KeplerianFromStateVectors`），而 `ε`/`h` 是直接算的 ✓。
    /// 开普勒轨道有**代数恒等式**：
    ///     `e = √(1 + 2·ε·h²/μ²)`，`a = −μ/(2ε)`，其中 `ε = v²/2 − μ/r`、`h = |r×v|`
    /// ⇒ **适配层给的 `e`/`a` 必须与它逐位一致**。不一致 ⇒ "幻影"就在要素提取层
    /// （今天 11 次仪器事故的同居地），而不是物理。
    ///
    /// ⚠ 这条测试**只用直接测量**：状态矢量 → 两个不变量 → 闭式要素。**没有任何我的模型假设**。
    /// </summary>
    public class OrbitViewElementIdentityTests
    {
        private static readonly double Mu = OrbitMath.GM;
        private static readonly V3 Pole = new V3(0, 0, 1);

        /// <summary>`e`/`a` 的两个算法必须一致：适配层 vs 不变量恒等式。</summary>
        [Theory]
        [InlineData(7.0e6, 0.0, 7.0e6, 0.0)]        // 圆
        [InlineData(7.0e6, 1.3, 7.0e6, 0.0)]        // 椭圆 e=0.3
        [InlineData(7.0e6, 1.9, 7.0e6, 0.4)]        // 倾角
        [InlineData(7.0e6, 1.3, 2.0e7, 0.9)]        // 异形椭圆 + 倾角
        [InlineData(4.2e7, 1.0, 1.0e7, 0.2)]        // 大半径
        public void EccentricityAndSma_MatchTheInvariantIdentity(double r0, double vFactor, double r1, double incRad)
        {
            // 构造一个状态（位置在 xy 面内按 incRad 抬起，速度用系数缩放 ⇒ 得到不同 e）
            var r = new V3(r0 * Math.Cos(incRad), 0.0, -r0 * Math.Sin(incRad));
            double vc = Math.Sqrt(Mu / r0);
            var v = new V3(0.0, vFactor * vc, 0.0);

            var ov = OrbitView.FromStateVectors(r, v, Mu, 0.0, Pole, r0 * 0.2);
            _ = r1;

            // 直接测量出的两个不变量
            double rM = r.magnitude;
            double eps = v.sqrMagnitude / 2.0 - Mu / rM;
            double hM = V3.Cross(r, v).magnitude;

            double eIdentity = Math.Sqrt(Math.Max(0.0, 1.0 + 2.0 * eps * hM * hM / (Mu * Mu)));
            double aIdentity = eps < 0.0 ? -Mu / (2.0 * eps) : double.NaN;

            // ① e：两个算法必须一致（相对容差，双曲/近抛物端不适用 ⇒ 只在 eps<0 时比 a）
            Assert.True(Math.Abs(ov.eccentricity - eIdentity) < 1e-9 * Math.Max(1.0, eIdentity),
                $"e 不一致：适配层 {ov.eccentricity:E9} vs 恒等式 {eIdentity:E9}（差 {Math.Abs(ov.eccentricity - eIdentity):E3}）");

            // ② a：同上
            if (!double.IsNaN(aIdentity))
                Assert.True(Math.Abs(ov.semiMajorAxis - aIdentity) < 1e-9 * aIdentity,
                    $"a 不一致：适配层 {ov.semiMajorAxis:E9} vs 恒等式 {aIdentity:E9}");
        }

        /// <summary>随机扫一遍（覆盖各种形状），确认恒等式在**整个**范围内成立 —— 不是几个特例碰巧对上。</summary>
        [Fact]
        public void Identity_HoldsAcrossRandomStates()
        {
            var rnd = new Random(20260919);
            for (int i = 0; i < 2000; i++)
            {
                double r0 = 7.0e6 * (0.5 + 2.0 * rnd.NextDouble());
                var r = new V3((2.0 * rnd.NextDouble() - 1.0) * r0,
                               (2.0 * rnd.NextDouble() - 1.0) * r0,
                               (2.0 * rnd.NextDouble() - 1.0) * r0);
                double vc = Math.Sqrt(Mu / r.magnitude);
                var v = new V3((2.0 * rnd.NextDouble() - 1.0) * 1.4 * vc,
                               (2.0 * rnd.NextDouble() - 1.0) * 1.4 * vc,
                               (2.0 * rnd.NextDouble() - 1.0) * 1.4 * vc);

                double rM = r.magnitude;
                double eps = v.sqrMagnitude / 2.0 - Mu / rM;
                if (eps >= 0.0) continue; // 只看椭圆（双曲时 a 无意义）

                var ov = OrbitView.FromStateVectors(r, v, Mu, 0.0, Pole, rM * 0.2);
                double hM = V3.Cross(r, v).magnitude;
                double eIdentity = Math.Sqrt(Math.Max(0.0, 1.0 + 2.0 * eps * hM * hM / (Mu * Mu)));
                double aIdentity = -Mu / (2.0 * eps);

                Assert.True(Math.Abs(ov.eccentricity - eIdentity) < 1e-8 * Math.Max(1.0, eIdentity),
                    $"第 {i} 个状态：e 不一致 {ov.eccentricity:E9} vs {eIdentity:E9}");
                Assert.True(Math.Abs(ov.semiMajorAxis - aIdentity) < 1e-8 * aIdentity,
                    $"第 {i} 个状态：a 不一致 {ov.semiMajorAxis:E9} vs {aIdentity:E9}");
            }
        }
    }
}
