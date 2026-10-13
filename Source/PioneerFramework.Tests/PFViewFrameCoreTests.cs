using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **PFView 参考系档位的纯核心钉子**（2026-10-12「PFView 参考系按场景选」）。
    ///
    /// 钉三件事：① 自动档阈值 = `max(大气厚度/2, 100 km)`（与大气壳同一处口径）；② 自动档按**相机高度**判
    /// （贴地/低空 ⇒ 固连、其余 ⇒ 惯性）；③ 显式档（惯性/固连）直接生效、不受高度影响（对比/调试用）。
    /// 换系本身的算术（"绝对系→固连系"旋转及其逆）由 `PFViewBodyRotationTests` 钉。
    /// </summary>
    public class PFViewFrameCoreTests
    {
        const double EarthThickness = 100_000.0;    // 地球档大气厚度（本 mod XML：100 km ⇒ 壳在 50 km）
        const double MoonThickness = 0.0;

        [Fact]
        public void Threshold_IsHalfAtmosphereButNeverBelowHundredKm()
        {
            // 大气厚度一半 = 50 km < 100 km 兜底 ⇒ 取 100 km
            Assert.Equal(PFViewFrameCore.LowAltitudeFallbackMeters,
                PFViewFrameCore.FixedThresholdMeters(EarthThickness), 3);
            // 无大气 ⇒ 兜底
            Assert.Equal(PFViewFrameCore.LowAltitudeFallbackMeters,
                PFViewFrameCore.FixedThresholdMeters(MoonThickness), 3);
            // 巨厚大气（比如 400 km）⇒ 一半 200 km > 兜底 ⇒ 取 200 km
            Assert.Equal(200_000.0, PFViewFrameCore.FixedThresholdMeters(400_000.0), 3);
        }

        [Fact]
        public void Auto_UsesFixedFrameWhenTheCameraIsLow()
        {
            // 贴地/悬浮（相机 30 km）⇒ 固连（地面静止，否则地面以 523 m/物理秒掠过，近景没法看）
            Assert.True(PFViewFrameCore.UseFixedFrame(PFViewFrameMode.Auto, 30_000.0, EarthThickness));
            // 轨道（相机 500 km）⇒ 惯性
            Assert.False(PFViewFrameCore.UseFixedFrame(PFViewFrameMode.Auto, 500_000.0, EarthThickness));
            // 阈值边界：恰好等于 ⇒ 惯性（判据是**严格小于**）
            Assert.False(PFViewFrameCore.UseFixedFrame(PFViewFrameMode.Auto,
                PFViewFrameCore.FixedThresholdMeters(EarthThickness), EarthThickness));
        }

        [Fact]
        public void ExplicitModes_OverrideAltitude()
        {
            // 轨道高度也可以强制固连（看"地面不动、飞船绕行"的对照）
            Assert.True(PFViewFrameCore.UseFixedFrame(PFViewFrameMode.Fixed, 500_000.0, EarthThickness));
            // 贴地也可以强制惯性（看"地面掠过"的老行为）
            Assert.False(PFViewFrameCore.UseFixedFrame(PFViewFrameMode.Inertial, 1_000.0, EarthThickness));
        }

        [Fact]
        public void ModeNames_AreStableForUiAndLogs()
        {
            Assert.Equal("自动", PFViewFrameCore.ModeName(PFViewFrameMode.Auto));
            Assert.Equal("惯性", PFViewFrameCore.ModeName(PFViewFrameMode.Inertial));
            Assert.Equal("固连", PFViewFrameCore.ModeName(PFViewFrameMode.Fixed));
        }
    }
}
