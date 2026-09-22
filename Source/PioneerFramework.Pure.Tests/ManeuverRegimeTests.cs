using System;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **区制判据**（B2.1）判据：把两档船的真实参数喂进去，看它是否把"默认船"判成准脉冲、
    /// 把"低推力船"判成连续 —— 以及**阈值不在敏感带上**（设计稿实测的两个工况在阈值两侧一个数量级外）。
    /// 船的参数取**台架里那两档的逐位同参**（`RendezvousShip.HighThrustShip` / `MidThrustShip` 的那组数）。
    /// </summary>
    public class ManeuverRegimeTests
    {
        private const double Mu = 2.0e14, R0 = 7_000_000.0;
        private static readonly double VC = Math.Sqrt(Mu / R0);
        private static readonly double G = Mu / (R0 * R0);

        /// <summary>默认（过推）船：560 kN / 干重 5 t + 燃料 ⇒ 满载 a ≈ 7.87 m/s²（台架 HighThrustShip）。</summary>
        private const double A_HIGH = 7.87;
        /// <summary>中推船（旧架构失效区）：台架 MidThrustShip 那一档 a ≈ 0.46 m/s²。</summary>
        private const double A_MID = 0.46;

        [Fact]
        public void DefaultShip_IsQuasiImpulsive()
        {
            var kind = ManeuverRegime.Classify(808.0, VC, G, A_HIGH, out double theta, out double alpha);
            Assert.Equal(ManeuverRegime.Kind.QuasiImpulsive, kind);
            // Θ = (808/5345)·(4.07/7.87) ≈ 0.078 rad ≈ 4.5°（设计稿说"默认船 Θ≈10°"，同量级 ✓）
            Assert.InRange(theta, 0.02, 0.15);
            Assert.InRange(alpha, 1.5, 2.5);
        }

        [Fact]
        public void LowThrustShip_IsContinuous()
        {
            var kind = ManeuverRegime.Classify(808.0, VC, G, A_MID, out double theta, out _);
            Assert.Equal(ManeuverRegime.Kind.Continuous, kind);
            Assert.True(theta > 1.0, $"低推力船 Θ 应远大于 1，实测 {theta:F2}");
        }

        /// <summary>阈值不在敏感带：设计稿实测的"失败工况 Θ≈1.82"与"现配置 Θ≈0.18"分列两侧。</summary>
        [Fact]
        public void Threshold_IsNotInTheSensitiveBand()
        {
            Assert.Equal(ManeuverRegime.Kind.QuasiImpulsive,
                ManeuverRegime.Classify(0.18 * VC * A_HIGH / G, VC, G, A_HIGH, out _, out _));
            Assert.Equal(ManeuverRegime.Kind.Continuous,
                ManeuverRegime.Classify(1.82 * VC * A_HIGH / G, VC, G, A_HIGH, out _, out _));
        }

        /// <summary>退化输入 ⇒ 保守判"连续"（那条总能执行），且不抛。</summary>
        [Fact]
        public void DegenerateInputs_GoContinuous()
        {
            Assert.Equal(ManeuverRegime.Kind.Continuous, ManeuverRegime.Classify(0.0, VC, G, A_HIGH, out _, out _));
            Assert.Equal(ManeuverRegime.Kind.Continuous, ManeuverRegime.Classify(100.0, 0.0, G, A_HIGH, out _, out _));
            Assert.Equal(ManeuverRegime.Kind.Continuous, ManeuverRegime.Classify(100.0, VC, G, 0.0, out _, out _));
        }

        /// <summary>两条入口（Θ 直算 / ω·t_b 版）必须同解（同一件事不许两份口径）。</summary>
        [Fact]
        public void BothOverloads_Agree()
        {
            var a = ManeuverRegime.Classify(600.0, VC, G, A_HIGH, out double t1, out _);
            var b = ManeuverRegime.ClassifyByOmega(600.0, R0, Mu, A_HIGH);
            Assert.Equal(a, b);
            Assert.InRange(t1, 0.0, 1.0);
        }
    }
}
