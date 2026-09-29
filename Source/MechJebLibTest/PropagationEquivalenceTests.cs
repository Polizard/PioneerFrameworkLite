using System;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **传播薄化的逐位等价性**（D7 / P31，2026-09-26）。
    ///
    /// 动机：`OrbitMath.GetPositionAtTimeDelta` / `GetVelocityAtTimeDelta` 原先走
    /// **"建一个 `OrbitView`（含完整要素提取 + 近圆分支），只为了调一次 `Shepperd`"** ——
    /// 那趟构造对结果**零贡献**（`Shepperd.Solve` 不读任何要素）。本轮把它薄化成
    /// `OrbitView.Propagate`，于是"要素提取"整段省掉。
    ///
    /// ⚠ 这类改动**必须逐位钉住**：省掉的如果是"算出来没用"就没事，但若偷偷改了一个
    /// 求值序（例如 `dt - 0.0`、`ResolveGM` 的取用顺序），末位就会变，而本库所有验收都建在
    /// "同场景前后可比"上。⇒ 本测试对**旧路线**（仍在 `OrbitView` 上：建 `OrbitView` 再取）
    /// 与**新路线**（`Propagate` / `OrbitMath` 薄转发）做**逐位**比较，覆盖历史上坏过的 regime
    /// （近圆 `e<1e-6` · 抛物 · 双曲 · 大半径 + 倾角 · 负 Δt）。
    ///
    /// 期望值取自**旧路线的当前实现**，不是我从公式反推的 —— 所以它钉的是"薄化前后同一件事"，
    /// **不引入任何我的模型假设**。
    /// </summary>
    public class PropagationEquivalenceTests
    {
        private static readonly double Mu = OrbitMath.GM;

        /// <summary>构造一个 (r, v)：位置在 xz 面内按 <paramref name="incRad"/> 抬起，速度用系数缩放 ⇒ 得到不同 e。</summary>
        private static (V3 r, V3 v) State(double r0, double vFactor, double incRad)
        {
            var r = new V3(r0 * Math.Cos(incRad), 0.0, -r0 * Math.Sin(incRad));
            double vc = Math.Sqrt(Mu / r0);
            var v = new V3(0.0, vFactor * vc, 0.0);
            return (r, v);
        }

        private static long Bits(double x) => BitConverter.DoubleToInt64Bits(x);

        [Theory]
        [InlineData(7.0e6, 1.0, 0.0, 600.0)]        // 圆（vFactor=1 ⇒ e≈0，走 NearCircularAngles 分支）
        [InlineData(7.0e6, 0.9, 0.0, 600.0)]        // 椭圆
        [InlineData(7.0e6, 1.35, 0.4, 600.0)]       // 椭圆 + 倾角
        [InlineData(7.0e6, 1.4142135, 0.0, 600.0)]  // 近抛物
        [InlineData(7.0e6, 1.6, 0.0, 600.0)]        // 双曲
        [InlineData(4.2e7, 1.0, 0.9, 3600.0)]       // 大半径 + 大倾角
        [InlineData(7.0e6, 1.0, 0.3, -1800.0)]      // 负 Δt
        public void PositionAndVelocity_MatchTheOrbitViewRoute_BitForBit(
            double r0, double vFactor, double incRad, double dt)
        {
            (V3 r, V3 v) = State(r0, vFactor, incRad);

            // 旧路线：与 `OrbitMath` 原来的实现逐字一致（只传 4 个实参 ⇒ 其余取默认）
            OrbitView ov = OrbitView.FromStateVectors(r, v, Mu, 0.0);
            (V3 oldPos, V3 oldVel) = ov.RightHandedStateVectorsAtUT(dt);

            // 新路线 A：`OrbitView.Propagate`（唯一实现点）
            (DVector3 pA, DVector3 vA) = OrbitView.Propagate(r, v, Mu, dt);

            // 新路线 B：`OrbitMath` 的两个薄转发（22 个调用点实际走的就是这条）
            DVector3 pB = OrbitMath.GetPositionAtTimeDelta(r, v, dt, Mu);
            DVector3 vB = OrbitMath.GetVelocityAtTimeDelta(r, v, dt, Mu);

            Assert.Equal(Bits(oldPos.x), Bits(pA.x));
            Assert.Equal(Bits(oldPos.y), Bits(pA.y));
            Assert.Equal(Bits(oldPos.z), Bits(pA.z));
            Assert.Equal(Bits(oldVel.x), Bits(vA.x));
            Assert.Equal(Bits(oldVel.y), Bits(vA.y));
            Assert.Equal(Bits(oldVel.z), Bits(vA.z));

            Assert.Equal(Bits(pA.x), Bits(pB.x));
            Assert.Equal(Bits(pA.y), Bits(pB.y));
            Assert.Equal(Bits(pA.z), Bits(pB.z));
            Assert.Equal(Bits(vA.x), Bits(vB.x));
            Assert.Equal(Bits(vA.y), Bits(vB.y));
            Assert.Equal(Bits(vA.z), Bits(vB.z));
        }

        /// <summary>负向对照：薄化**不该**把 `ResolveGM` 的"NaN = 默认 GM"语义丢掉。</summary>
        [Fact]
        public void ResolveGm_SentinelStillMeansDefaultMu()
        {
            (V3 r, V3 v) = State(7.0e6, 1.2, 0.2);
            DVector3 withNaN = OrbitMath.GetPositionAtTimeDelta(r, v, 600.0, double.NaN);
            DVector3 withMu = OrbitMath.GetPositionAtTimeDelta(r, v, 600.0, Mu);
            Assert.Equal(Bits(withMu.x), Bits(withNaN.x));
            Assert.Equal(Bits(withMu.y), Bits(withNaN.y));
            Assert.Equal(Bits(withMu.z), Bits(withNaN.z));
        }
    }
}
