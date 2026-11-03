using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 自动抛级判据（2026-10-28）：油比到位 **且（熄火 或 已空）** 才切。
    /// 旧判据只看油比 ⇒ 主发还在烧就切级，丢掉本级最后一段 Δv。
    /// </summary>
    public class AutoStageCoreTests
    {
        private const double Cap = 10_000.0;
        private const double Frac = 0.02;

        [Fact]
        public void BurningWithFuelLeft_DoesNotStage()
        {
            Assert.False(AutoStageCore.ShouldStageByFuel(100.0, Cap, Frac, burning: true, 100.0, 20.0));
        }

        [Fact]
        public void BurnedOut_Stages()
        {
            Assert.True(AutoStageCore.ShouldStageByFuel(100.0, Cap, Frac, burning: false, 100.0, 20.0));
        }

        [Fact]
        public void EmptyStage_StagesEvenIfBurnFlagLags()
        {
            Assert.True(AutoStageCore.ShouldStageByFuel(0.0, Cap, Frac, burning: true, 100.0, 20.0));
        }

        [Fact]
        public void AboveFraction_OrTooEarly_DoesNotStage()
        {
            Assert.False(AutoStageCore.ShouldStageByFuel(500.0, Cap, Frac, burning: false, 100.0, 20.0));
            Assert.False(AutoStageCore.ShouldStageByFuel(100.0, Cap, Frac, burning: false, 5.0, 20.0));
        }

        [Fact]
        public void UnknownPool_DoesNotStage()
        {
            Assert.False(AutoStageCore.ShouldStageByFuel(-1.0, Cap, Frac, burning: false, 100.0, 20.0));
        }
    }
}
