using System;
using MechJebLib.Functions;
using MechJebLib.Primitives;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **KSA-② 步长按局部轨道周期归一**（2026-10-17）：`steps = clamp(ceil(970·t_b/T_loc), 50, 400)`。
    /// 判据：典型短燃仍是 50 步（既有行为不变）、长燃加密、上限封顶、部分弧与整弧在端点逐位一致。
    /// </summary>
    public class BurnArcStepNormalizationTests
    {
        private static readonly double Mu = OrbitMath.GM;

        private static OrbitView Orbit(double r0)
        {
            double vc = Math.Sqrt(Mu / r0);
            return OrbitView.FromStateVectors(new DVector3(r0, 0, 0), new DVector3(0, Math.Sqrt(1.3) * vc, 0),
                Mu, 0.0, new DVector3(0, 0, 1), r0 * 0.2);
        }

        [Fact]
        public void StepCount_TypicalShortBurn_StaysAtFloor()
        {
            var o = Orbit(7.0e6);
            // 典型 300 s LEO 燃：T_loc≈5828 s ⇒ 970·300/5828≈49.9 ⇒ 取下限 50（与旧固定值逐位相同）
            Assert.Equal(BurnArc.Steps, BurnArc.StepCountFor(o, 300.0, 300.0, 0.0));
            // 更短的燃也不少于 50 步
            Assert.Equal(BurnArc.Steps, BurnArc.StepCountFor(o, 300.0, 10.0, 0.0));
        }

        [Fact]
        public void StepCount_LongBurn_GrowsWithBurnOverPeriod()
        {
            var o = Orbit(7.0e6);
            // 从 t=0 的 r=7e6 起烧 1800 s：游戏 GM=2e14（SpaceConstants.PrimaryGravitationalParameter=8R²）
            // ⇒ T_loc=2π√(r³/μ)≈8228 s ⇒ 970·1800/8228≈212.2 ⇒ ceil=213 步（约 4.3× 旧值）。
            // ⚠ 不是地球 GM（3.986e14）——那会得 300 步；本项目的"地球"是游戏行星。
            Assert.Equal(213, BurnArc.StepCountFor(o, 0.0, 1800.0));
        }

        [Fact]
        public void StepCount_HighOrbit_StaysAtFloor()
        {
            var o = Orbit(4.2e7);   // GEO 半径：T_loc≈85,600 s ⇒ 1800 s 燃只需 ≈20 步 ⇒ 下限 50
            Assert.Equal(BurnArc.Steps, BurnArc.StepCountFor(o, 300.0, 1800.0, 0.0));
        }

        [Fact]
        public void StepCount_VeryLongBurn_IsCapped()
        {
            var o = Orbit(7.0e6);
            Assert.Equal(BurnArc.MaxSteps, BurnArc.StepCountFor(o, 300.0, 1.0e6, 0.0));
        }

        [Fact]
        public void PartialAtFullElapsed_EqualsFullPrediction_Bitwise()
        {
            var o = Orbit(7.0e6);
            const double node = 300.0;
            const double burn = 1800.0;
            DVector3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, node);

            Assert.True(BurnArc.TryPredict(o, node, dv, burn, out BurnArc.Result full, out string why1), why1);
            Assert.True(BurnArc.TryPredictPartial(o, node, dv, burn, burn, out BurnArc.Result part, out string why2), why2);
            Assert.Equal(full.eccentricity, part.eccentricity);
            Assert.Equal(full.sma, part.sma);
        }
    }
}
