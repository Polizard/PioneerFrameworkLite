using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **撞击事件的"着陆 / 坠毁"分界**（2026-09-23 补；测试体检查出的零覆盖项之一）。
    ///
    /// 判据本体 = <see cref="SpaceConstants.IsSafeLandingSpeed" />（唯一权威，`CheckPlanetCollision` 只调用它）。
    /// 本文件钉三件事：
    /// · **阈值本身** = 100 m/s（用户 2026-09-22 裁定"暂时设为100m/s"）——改它必须改这里；
    /// · **边界含在内**：等于阈值 ⇒ 着陆（与原来那句 `>` 的语义逐位一致）；
    /// · **入参是接地速度**（相对地表、已扣共转）—— 地球赤道共转 523.6 m/s，用惯性速度则任何着陆都判坠毁；
    ///   那条陷阱由 `SurfaceFrameTests.GroundSpeed_Equator_InertialReadingIsTheTrap` 独立钉住。
    /// </summary>
    public class LandingSpeedBoundaryTests
    {
        [Fact]
        public void ThresholdIsTheAgreedHundredMetersPerSecond()
            => Assert.Equal(100.0, SpaceConstants.MaxSafeLandingSpeedMps, 9);

        /// <summary>
        /// 边界值两侧 + **调试场景的两个实测读数**（`SpawnImpactEventTest`：地表上方 200 m，
        /// 下降 20 m/s ⇒ 接地 ≈60 ⇒ 着陆；下降 300 m/s ⇒ 接地 ≈305 ⇒ 坠毁）——判据与场景读数对齐。
        /// </summary>
        [Theory]
        [InlineData(0.0, true)]
        [InlineData(60.0, true)]        // 场景 A 的接地速度量级
        [InlineData(99.999, true)]
        [InlineData(100.0, true)]       // ★ 边界含在内
        [InlineData(100.001, false)]
        [InlineData(305.0, false)]      // 场景 B 的接地速度量级
        public void BoundaryIsInclusive_AndAgreesWithTheScenarioReadings(double groundSpeed, bool expectSafe)
            => Assert.Equal(expectSafe, SpaceConstants.IsSafeLandingSpeed(groundSpeed));
    }
}
