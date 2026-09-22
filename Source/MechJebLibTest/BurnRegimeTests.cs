using System;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **燃烧区制判据**（B2.1）的验收。
    ///
    /// 最重要的一条是那个**锚点**：本 mod 当前默认船在 LEO→GEO 档实测 `Θ ≈ 10° = 0.175 rad`
    /// ⇒ 判据必须把它判成**准脉冲**（现有一切执行行为都建立在这个前提上；判成连续就是把行为改坏）。
    /// </summary>
    public class BurnRegimeTests
    {
        private static readonly double Mu = OrbitMath.GM; // 2e14 量级（与游戏内一致）
        private const double R0 = 7_000_000.0;

        /// <summary>两种写法恒等：`ω·t_b` 与计划稿的 `(Δv/v_c)·(g_local/a)`。</summary>
        [Fact]
        public void Theta_MatchesThePlansAlternativeForm()
        {
            double dv = 1726.0, a = 7.87, r = R0, mu = Mu;

            double vc = Math.Sqrt(mu / r);
            double gLocal = mu / (r * r);
            double thetaPlansForm = (dv / vc) * (gLocal / a); // 计划稿 B2.1 的写法

            BurnRegime b = BurnRegime.Classify(dv, r, mu, a);

            Assert.True(Math.Abs(b.theta - thetaPlansForm) < 1e-12 * thetaPlansForm,
                $"两种写法应当恒等：ω·t_b={b.theta:E6} vs (Δv/v_c)·(g/a)={thetaPlansForm:E6}");
        }

        /// <summary>
        /// ⭐ **锚点**：默认船 LEO→GEO（Δv≈1726 m/s、a≈7.87 m/s²）⇒ `Θ ≈ 0.175 rad（10°）` ⇒ **准脉冲**。
        /// 这条判据红了就说明"判据门"选错了，会把现有的执行行为改坏。
        /// </summary>
        [Fact]
        public void DefaultShip_LeoToGeo_IsQuasiImpulse()
        {
            BurnRegime b = BurnRegime.Classify(1726.0, R0, Mu, 7.87);

            Assert.True(b.QuasiImpulse, $"锚点必须是准脉冲，实得：{b.Describe()}");
            Assert.True(Math.Abs(b.theta - 0.175) < 0.02, $"Θ 应 ≈ 0.175 rad（≈10°）：{b.Describe()}");
            Assert.True(Math.Abs(b.burnSeconds - 219.0) < 5.0, $"t_b 应 ≈ 219 s：{b.Describe()}");
        }

        /// <summary>门就在 0.2：恰好等于 ⇒ 准脉冲；刚过头 ⇒ 连续。</summary>
        [Fact]
        public void BoundaryIsAtTheta02_Inclusive()
        {
            double dv = 500.0;
            double omega = Math.Sqrt(Mu / (R0 * R0 * R0));

            // ① **规则本身含端点**：直接构造 Θ = 门值（精确）——用除法重建"正好 0.2"是做不到的
            //    （浮点会给 0.20000000000000004 ⇒ 那是**测试假象**，不是规则的问题）。
            Assert.True(new BurnRegime(BurnRegime.QuasiImpulseThetaMax, 1.0, 1.0).QuasiImpulse,
                "Θ 正好在门上算准脉冲（含端点）");
            Assert.False(new BurnRegime(BurnRegime.QuasiImpulseThetaMax + 1e-12, 1.0, 1.0).QuasiImpulse,
                "刚过门就算连续");

            // ② 公式的落点：推力取到门值附近时，判据应当跟着翻
            double aAtGate = omega * dv / BurnRegime.QuasiImpulseThetaMax;
            BurnRegime atGate = BurnRegime.Classify(dv, R0, Mu, aAtGate);
            Assert.True(Math.Abs(atGate.theta - BurnRegime.QuasiImpulseThetaMax) < 1e-9, atGate.Describe());

            BurnRegime justOver = BurnRegime.Classify(dv, R0, Mu, aAtGate * 0.99); // 推力小一点 ⇒ Θ 大一点
            Assert.False(justOver.QuasiImpulse, $"刚过门就要判连续：{justOver.Describe()}");
        }

        /// <summary>低推力（电推档）必然是连续段——判据不能把它当脉冲。</summary>
        [Fact]
        public void LowThrustIsContinuous()
        {
            BurnRegime b = BurnRegime.Classify(1726.0, R0, Mu, 0.01); // 1 cm/s²
            Assert.False(b.QuasiImpulse, b.Describe());
            Assert.True(b.theta > 100.0, $"低推档 Θ 应当很大：{b.Describe()}");
        }

        /// <summary>退化输入返回 NaN 且**不**判成准脉冲（不许把"不知道"当"脉冲"）。</summary>
        [Fact]
        public void DegenerateInputs_DoNotClaimQuasiImpulse()
        {
            foreach (BurnRegime b in new[]
                     {
                         BurnRegime.Classify(100.0, 0.0, Mu, 1.0),
                         BurnRegime.Classify(100.0, R0, 0.0, 1.0),
                         BurnRegime.Classify(100.0, R0, Mu, 0.0),
                     })
            {
                Assert.True(double.IsNaN(b.theta), "退化输入应当给 NaN");
                Assert.False(b.QuasiImpulse, "不知道就不许说'是脉冲'");
            }
        }
    }
}
