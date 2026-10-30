using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>动量轮 + RCS 卸载的纯数学判据（2026-10-26 用户裁定：连续比例 / 可变容量 / 空了就空）。</summary>
    public class WheelDesaturationCoreTests
    {
        private const double Ve = 3000.0;
        private const double Arm = 10.0;
        private const double Eff = 0.8;
        private const double PerKg = Ve * Arm * Eff;   // 24,000 (kg·m²/s)/kg

        private static WheelDesaturationCore.Inputs In(double dt, DVector3 torque, DVector3 L,
            double cap = 1e9, double T = 100.0, double availKg = 1000.0, double eff = Eff)
            => new(dt, torque, L, cap, T, Ve, Arm, eff, availKg);

        [Fact]
        public void NoTorqueNoMomentum_NoFuel()
        {
            var o = WheelDesaturationCore.Step(In(1.0, DVector3.zero, DVector3.zero));
            Assert.Equal(0.0, o.FuelKg, 9);
            Assert.Equal(0.0, o.DumpedMomentum, 9);
            Assert.Equal(0.0, o.Momentum.magnitude, 9);
        }

        [Fact]
        public void TorqueIsAbsorbed_Linearly()
        {
            var o = WheelDesaturationCore.Step(In(1.0, new DVector3(0, 100, 0), DVector3.zero, T: 3600.0));
            Assert.Equal(-(100.0 - 100.0 / 3600.0), o.Momentum.y, 6);   // L = −(τ·dt − 同一步卸掉的那点)
            Assert.False(o.Saturated);
            Assert.Equal(100.0 / 3600.0, o.DumpedMomentum, 6); // 同一步按比例卸掉一点点
        }

        [Fact]
        public void Capacity_ClampsAndReportsSaturation()
        {
            var o = WheelDesaturationCore.Step(In(1.0, new DVector3(0, 1000, 0), DVector3.zero, cap: 10.0));
            Assert.True(o.Saturated);
            Assert.Equal(10.0 * (1.0 - 1.0 / 100.0), o.Momentum.magnitude, 6);
        }

        [Fact]
        public void Desaturation_IsProportionalAndFuelMatches()
        {
            var o = WheelDesaturationCore.Step(In(1.0, DVector3.zero, new DVector3(0, 1000, 0), T: 100.0));
            Assert.Equal(10.0, o.DumpedMomentum, 6);          // |L|·dt/T
            Assert.Equal(990.0, o.Momentum.y, 6);
            Assert.Equal(10.0 / PerKg, o.FuelKg, 9);
            Assert.Equal(o.DumpedMomentum, o.FuelKg * PerKg, 6);   // 守恒
        }

        [Fact]
        public void EmptyRcs_LeavesMomentumUntouched()
        {
            var o = WheelDesaturationCore.Step(In(1.0, DVector3.zero, new DVector3(0, 1000, 0), availKg: 0.0));
            Assert.Equal(0.0, o.FuelKg, 9);
            Assert.Equal(0.0, o.DumpedMomentum, 9);
            Assert.Equal(1000.0, o.Momentum.y, 6);
        }

        [Fact]
        public void FuelLimited_DumpIsCapped()
        {
            // 只剩 0.001 kg ⇒ 可卸 24 动量；想要 10 ⇒ 不受限
            var tight = WheelDesaturationCore.Step(In(1.0, DVector3.zero, new DVector3(0, 1000, 0), availKg: 0.0001));
            Assert.Equal(0.0001 * PerKg, tight.DumpedMomentum, 6);
            Assert.Equal(0.0001, tight.FuelKg, 9);
        }

        [Fact]
        public void NoWheelOrNoRcs_StoresNothingAndBurnsNothing()
        {
            var noWheel = WheelDesaturationCore.Step(In(1.0, new DVector3(0, 100, 0), DVector3.zero, cap: 0.0));
            Assert.Equal(0.0, noWheel.Momentum.magnitude, 9);

            var noEff = WheelDesaturationCore.Step(In(1.0, DVector3.zero, new DVector3(0, 1000, 0), eff: 0.0));
            Assert.Equal(0.0, noEff.FuelKg, 9);
            Assert.Equal(1000.0, noEff.Momentum.y, 6);
        }
    }
}

