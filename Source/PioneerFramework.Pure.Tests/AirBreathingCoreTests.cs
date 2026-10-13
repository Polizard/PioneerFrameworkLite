using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **[A3]+[A4]+[A5] 纯核心测试**（计划稿 §2.3/§2.4 判据）：
    /// 进气速率 ∝ 面积·ρ·空速且按容量封顶 · 包线**火箭退化恰 1.0** · Mach 折线 · 混合比与熄火门。
    /// </summary>
    public class AirBreathingCoreTests
    {
        // ── ① 进气（D4：速率 = k·A·ρ·|v_air|；封顶由容量给）──

        [Fact]
        public void IntakeRate_IsLinearInEachFactor()
        {
            double b = AirBreathingCore.IntakeRateKgPerSec(1.0, 2.0, 0.5, 100.0);   // = 100
            Assert.Equal(100.0, b);
            Assert.Equal(2.0 * b, AirBreathingCore.IntakeRateKgPerSec(2.0, 2.0, 0.5, 100.0));
            Assert.Equal(2.0 * b, AirBreathingCore.IntakeRateKgPerSec(1.0, 4.0, 0.5, 100.0));
            Assert.Equal(2.0 * b, AirBreathingCore.IntakeRateKgPerSec(1.0, 2.0, 1.0, 100.0));
            Assert.Equal(2.0 * b, AirBreathingCore.IntakeRateKgPerSec(1.0, 2.0, 0.5, 200.0));
        }

        [Theory]
        [InlineData(0.0, 2.0, 0.5, 100.0)]   // 系数未配
        [InlineData(1.0, 0.0, 0.5, 100.0)]   // 无进气面积
        [InlineData(1.0, 2.0, 0.0, 100.0)]   // 真空（ρ≡0）⇒ 精确 0
        [InlineData(1.0, 2.0, 0.5, 0.0)]     // 与空气相对静止（悬浮）⇒ 0
        [InlineData(-1.0, 2.0, 0.5, 100.0)]
        public void IntakeRate_NonPositiveInputs_ExactZero(double k, double a, double rho, double v)
        {
            Assert.Equal(0.0, AirBreathingCore.IntakeRateKgPerSec(k, a, rho, v));
        }

        [Fact]
        public void FillStep_CapsAtCapacity_AndNeverNegative()
        {
            // 一步能灌 100，罐只剩 30 ⇒ 只灌 30
            Assert.Equal(30.0, AirBreathingCore.FillStep(50.0, 2.0, 170.0, 200.0));
            // 有空间时 = 速率·dt
            Assert.Equal(100.0, AirBreathingCore.FillStep(50.0, 2.0, 0.0, 200.0));
            // 满罐 ⇒ 0（不带负号）
            Assert.Equal(0.0, AirBreathingCore.FillStep(50.0, 2.0, 200.0, 200.0));
        }

        // ── ② 包线（D5：φ = (ρ/ρ0)^a·f(M)；**火箭 = a=0 且无折线 ⇒ 恰 1.0**）──

        [Fact]
        public void ThrustFactor_RocketDegenerate_IsExactlyOne()
        {
            // 火箭退化：a = 0、折线 null 或空 ⇒ **逐位 1.0**（消费者乘它是恒等 ⇒ 现有四台零改动）
            Assert.Equal(1.0, AirBreathingCore.ThrustFactor(1.2, 1.2, 0.0, null, 5.0));
            Assert.Equal(1.0, AirBreathingCore.ThrustFactor(1.2, 1.2, 0.0, new MachPoint[0], 5.0));
            Assert.Equal(1.0, AirBreathingCore.ThrustFactor(0.0, 1.2, 0.0, null, 5.0));   // 真空也不碰分支
        }

        [Fact]
        public void ThrustFactor_DensityExponent_ScalesWithRhoOverRho0()
        {
            // a = 1（吸气式的默认）：ρ = ρ0/4 ⇒ φ = 0.25
            Assert.Equal(0.25, AirBreathingCore.ThrustFactor(0.3, 1.2, 1.0, null, 5.0));
            Assert.Equal(1.0, AirBreathingCore.ThrustFactor(1.2, 1.2, 1.0, null, 5.0));
            // 真空里吸气式 ⇒ 0（"真空不得出力"的下游保证之一）
            Assert.Equal(0.0, AirBreathingCore.ThrustFactor(0.0, 1.2, 1.0, null, 5.0));
        }

        [Fact]
        public void ThrustFactor_MachCurve_Multiplies()
        {
            var curve = new[]
            {
                new MachPoint(0.0, 1.0),
                new MachPoint(1.0, 1.0),
                new MachPoint(2.0, 0.5),
            };
            Assert.Equal(1.0, AirBreathingCore.ThrustFactor(1.2, 1.2, 1.0, curve, 0.5));
            Assert.Equal(0.75, AirBreathingCore.ThrustFactor(1.2, 1.2, 1.0, curve, 1.5));
            Assert.Equal(0.5, AirBreathingCore.ThrustFactor(1.2, 1.2, 1.0, curve, 9.0));   // 平化
        }

        // ── Mach 折线求值（两端平化 + 线性插值）──

        [Fact]
        public void EvaluateMach_EmptyOrSingle_IsFlat()
        {
            Assert.Equal(1.0, AirBreathingCore.EvaluateMach(null, 3.0));
            Assert.Equal(1.0, AirBreathingCore.EvaluateMach(new MachPoint[0], 3.0));
            Assert.Equal(0.6, AirBreathingCore.EvaluateMach(new[] { new MachPoint(2.0, 0.6) }, 3.0));
        }

        [Fact]
        public void EvaluateMach_Interpolates_AndClampsBothEnds()
        {
            var curve = new[]
            {
                new MachPoint(0.0, 1.0),
                new MachPoint(1.0, 1.2),
                new MachPoint(3.0, 0.4),
            };
            Assert.Equal(1.0, AirBreathingCore.EvaluateMach(curve, -5.0));      // 低端平化
            Assert.Equal(1.0, AirBreathingCore.EvaluateMach(curve, 0.0));       // 采样点
            Assert.Equal(1.2, AirBreathingCore.EvaluateMach(curve, 1.0));       // 采样点
            Assert.Equal(1.1, AirBreathingCore.EvaluateMach(curve, 0.5));       // 段内插值
            Assert.Equal(0.8, AirBreathingCore.EvaluateMach(curve, 2.0));       // 段内插值
            Assert.Equal(0.4, AirBreathingCore.EvaluateMach(curve, 99.0));      // 高端平化
        }

        // ── ③ 混合比 + 熄火门（D4：空气免费但燃料照烧；抽干 ⇒ 熄火、燃料不扣）──

        [Fact]
        public void AirRequired_ScalesWithFuelAndRatio()
        {
            Assert.Equal(150.0, AirBreathingCore.AirRequired(10.0, 15.0));
            Assert.Equal(0.0, AirBreathingCore.AirRequired(0.0, 15.0));
            Assert.Equal(0.0, AirBreathingCore.AirRequired(10.0, 0.0));
        }

        [Fact]
        public void CanRun_Flameout_WhenAirShort()
        {
            // 想烧 1 kg 燃料、混合比 15 ⇒ 需 15 kg 空气
            Assert.True(AirBreathingCore.CanRun(100.0, 15.0, 1.0, 15.0));    // 正好够 ⇒ 能烧
            Assert.True(AirBreathingCore.CanRun(100.0, 20.0, 1.0, 15.0));
            Assert.False(AirBreathingCore.CanRun(100.0, 14.9, 1.0, 15.0));   // 空气差一点 ⇒ 熄火（燃料不扣）
            Assert.False(AirBreathingCore.CanRun(0.5, 100.0, 1.0, 15.0));    // 燃料不够 ⇒ 也点不着
            Assert.False(AirBreathingCore.CanRun(100.0, 100.0, 0.0, 15.0));  // 没要烧 ⇒ 不"点火"
        }

        // ── ④ 进气限流（切片构造处用的那一条；与 CanRun 判据不同，见其注释）──

        [Fact]
        public void IntakeThrottleCap_RocketIsExactlyOne_NoDivision()
        {
            // 纯火箭（无进气需求）⇒ 恒 1.0，且**空气存量取什么值都不进除法**（含 0 / 负）
            Assert.Equal(1.0, AirBreathingCore.IntakeThrottleCap(0.0, 2.0, 0.0, 100.0));
            Assert.Equal(1.0, AirBreathingCore.IntakeThrottleCap(-5.0, 2.0, 0.0, 100.0));
            Assert.Equal(1.0, AirBreathingCore.IntakeThrottleCap(1000.0, 2.0, 0.0, 100.0));
        }

        [Fact]
        public void IntakeThrottleCap_ScalesWithAvailableAir()
        {
            // 全流量一步耗气 = airFlow·dt = 20·2 = 40 kg
            Assert.Equal(1.0, AirBreathingCore.IntakeThrottleCap(40.0, 2.0, 20.0, 100.0));    // 正好够 ⇒ 不受限
            Assert.Equal(1.0, AirBreathingCore.IntakeThrottleCap(1000.0, 2.0, 20.0, 100.0));  // 富余 ⇒ 夹到 1
            Assert.Equal(0.5, AirBreathingCore.IntakeThrottleCap(20.0, 2.0, 20.0, 100.0));    // 半步的量 ⇒ 半流率
            Assert.Equal(0.25, AirBreathingCore.IntakeThrottleCap(10.0, 2.0, 20.0, 100.0));
            Assert.Equal(0.0, AirBreathingCore.IntakeThrottleCap(0.0, 2.0, 20.0, 100.0));     // 抽干 ⇒ 熄火（0）
            Assert.Equal(0.0, AirBreathingCore.IntakeThrottleCap(-1.0, 2.0, 20.0, 100.0));    // 异常负值也不得漏成负
        }

        [Fact]
        public void IntakeThrottleCap_NonPositiveDtOrFlow_IsOne()
        {
            Assert.Equal(1.0, AirBreathingCore.IntakeThrottleCap(0.0, 0.0, 20.0, 100.0));   // dt=0（无步长）
            Assert.Equal(1.0, AirBreathingCore.IntakeThrottleCap(0.0, -1.0, 20.0, 100.0));
            Assert.Equal(1.0, AirBreathingCore.IntakeThrottleCap(0.0, 2.0, 20.0, 0.0));     // 主工质无流率 ⇒ 不是引擎
            Assert.Equal(1.0, AirBreathingCore.IntakeThrottleCap(0.0, 2.0, -3.0, 100.0));
        }

        // ── ⑤ 网络包线（[A4]：按 Σ(ve·ṁ) 加权 + 真空门控靠 φ=0）──

        private static EnvelopeTerm Term(double veMf, float a, params MachPoint[] curve)
            => new EnvelopeTerm { veMf = veMf, densityExponent = a, machCurve = curve };

        [Fact]
        public void EnvelopeFactor_RocketOnly_IsExactlyOne_EvenInVacuum()
        {
            Assert.Equal(1.0, AirBreathingCore.EnvelopeFactor(null, 1000.0, 0.0, 1.2, 3.0));
            Assert.Equal(1.0, AirBreathingCore.EnvelopeFactor(new EnvelopeTerm[0], 1000.0, 0.0, 1.2, 3.0));
        }

        [Fact]
        public void EnvelopeFactor_PureJet_InVacuum_IsZero()
        {
            // 纯吸气式网（总权重全在入表项上）⇒ 真空里 φ = 0 ⇒ 调用方不路由（D5 网络可用性门控）
            var terms = new[] { Term(100.0, 1f) };
            Assert.Equal(0.0, AirBreathingCore.EnvelopeFactor(terms, 100.0, 0.0, 1.2, 0.0));
            Assert.Equal(1.0, AirBreathingCore.EnvelopeFactor(terms, 100.0, 1.2, 1.2, 0.0));   // ρ=ρ0 ⇒ φ=1
        }

        [Fact]
        public void EnvelopeFactor_MixedNetwork_WeightsRocketPartAtOne()
        {
            // 网 = 火箭 100 + 吸气式 100（总权重 200）；吸气式 φ = ρ/ρ0 = 0.25
            // ⇒ φ_net = (100·0.25 + 100·1)/200 = 0.625（**火箭那半不该被吸气式拉低**）
            var terms = new[] { Term(100.0, 1f) };
            Assert.Equal(0.625, AirBreathingCore.EnvelopeFactor(terms, 200.0, 0.3, 1.2, 0.0), 12);
        }

        [Fact]
        public void EnvelopeFactor_TermsHeavierThanTotal_ClampsRocketPart()
        {
            // 病态输入（表权重 > 总权重，正常构表不会出现）⇒ 火箭部分夹到 0，不得算出负数
            var terms = new[] { Term(300.0, 1f) };
            Assert.Equal(1.0, AirBreathingCore.EnvelopeFactor(terms, 100.0, 1.2, 1.2, 0.0));
        }

        [Fact]
        public void EnvelopeFactor_MachCurve_AppliesThroughTheSameUniqueFormula()
        {
            // a=0 + 折线 ⇒ φ 就是折线值。段 (1.0→1.2) 与 (2.0→0.8)：
            //   Mach 1.25 ⇒ t=0.25 ⇒ 1.2 + (0.8−1.2)·0.25 = **1.1**（Mach 1.5 恰是 1.0，会被"巧合通过"掩盖
            //   —— 所以取 1.25；"我的算术"本身就是一类仪器事故，见 [[verification-instruments-must-be-verified]]）
            var curve = new[] { new MachPoint(0.0, 1.0), new MachPoint(1.0, 1.2), new MachPoint(2.0, 0.8) };
            var terms = new[] { Term(100.0, 0f, curve) };
            Assert.Equal(1.1, AirBreathingCore.EnvelopeFactor(terms, 100.0, 1.2, 1.2, 1.25), 12);
            Assert.Equal(0.8, AirBreathingCore.EnvelopeFactor(terms, 100.0, 1.2, 1.2, 99.0), 12);   // 高端平化
        }

        [Fact]
        public void MachFromAirSpeed_UsesThePlaceholderSoundSpeed()
        {
            Assert.Equal(1.0, AirBreathingCore.MachFromAirSpeed(340.0));
            Assert.Equal(2.0, AirBreathingCore.MachFromAirSpeed(680.0));
            Assert.Equal(0.0, AirBreathingCore.MachFromAirSpeed(0.0));
            Assert.Equal(0.0, AirBreathingCore.MachFromAirSpeed(-10.0));
            Assert.Equal(1.0, AirBreathingCore.MachFromAirSpeed(300.0, 300.0));   // 音速可换（参数）
            Assert.Equal(0.0, AirBreathingCore.MachFromAirSpeed(300.0, 0.0));     // 音速缺席 ⇒ 0 = "无 Mach 信息"
        }

        [Fact]
        public void FullThrottleEndurance_IsPropellantOverFlow()
        {
            // 面板对吸气式该显示的口径（不是 Δv）："这么开还能开多久"
            Assert.Equal(100.0, AirBreathingCore.FullThrottleEnduranceSeconds(500.0, 5.0));
            Assert.Equal(0.0, AirBreathingCore.FullThrottleEnduranceSeconds(0.0, 5.0));
            Assert.Equal(0.0, AirBreathingCore.FullThrottleEnduranceSeconds(500.0, 0.0));
            Assert.Equal(0.0, AirBreathingCore.FullThrottleEnduranceSeconds(-1.0, 5.0));
        }
    }
}
