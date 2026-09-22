using System;
using MechJebLib.Functions;
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B2.2 弧补偿（开环侧）**的判据：解出来的执行参数，用**前向模型**验回来必须比原计划更接近目标。
    /// 判据是**可否证的**：补偿后的 (e, Δa) 必须**严格更好**，且在 4 条轨道上一致。
    /// </summary>
    public class BurnArcCompensationTests
    {
        private readonly ITestOutputHelper _out;
        public BurnArcCompensationTests(ITestOutputHelper output) => _out = output;

        private static readonly double Mu = OrbitMath.GM;

        private static OrbitView Orbit(double r0)
        {
            double vc = Math.Sqrt(Mu / r0);
            return OrbitView.FromStateVectors(new V3(r0, 0, 0), new V3(0, Math.Sqrt(1.3) * vc, 0),
                Mu, 0.0, new V3(0, 0, 1), r0 * 0.2);
        }

        [Theory]
        [InlineData(7.0e6)]
        [InlineData(6.6e6)]
        [InlineData(8.5e6)]
        [InlineData(7.8e6)]
        public void Solve_ImprovesOverRawPlan(double r0)
        {
            var o = Orbit(r0);
            const double node = 300.0;
            V3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, node);
            double target = o.Radius(node);
            double burnSeconds = dv.magnitude * 74_995.1 / 560_000.0;   // = Δv / (F/m₀)：恒定加速度时长

            // 原计划（脉冲在节点）：有限燃烧的实际落点
            Assert.True(BurnArc.TryPredict(o, node, dv, burnSeconds, out BurnArc.Result raw, out string why), why);
            double rawCost = raw.achieved.eccentricity + Math.Abs(raw.achieved.semiMajorAxis - target) / target;

            // 补偿解
            Assert.True(BurnArcCompensation.TrySolve(o, node, dv, burnSeconds, target, out var sol, out string why2), why2);
            Assert.True(sol.Cost < rawCost,
                $"r₀={r0 / 1000:F0}km：补偿代价 {sol.Cost:E3} 应小于原计划 {rawCost:E3}｜{sol}");

            // 解出的参数用前向模型复验（自证：解出来的就是算出来的）
            Assert.True(BurnArc.TryPredict(o, node, dv * sol.DvScale, burnSeconds, sol.ShiftSeconds,
                out BurnArc.Result chk, out string why3), why3);
            Assert.True(Math.Abs(chk.achieved.eccentricity - sol.Ecc) < 1e-9,
                "解与复验必须逐位一致（同一前向模型）");

            _out.WriteLine($"r₀={r0 / 1000:F0}km ｜原计划：e={raw.achieved.eccentricity:E3}、Δa={((raw.achieved.semiMajorAxis - target) / 1000.0):+0.00;-0.00}km"
                + $" ⇒ 补偿后：{sol}（代价 {rawCost:E3} → {sol.Cost:E3}）");
        }
    }
}
