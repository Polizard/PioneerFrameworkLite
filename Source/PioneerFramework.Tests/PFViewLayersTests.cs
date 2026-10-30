using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **PFView 画序（层次）口径的钉子**（2026-10-11「甲：分档」第一刀）。
    ///
    /// 背景：壳（1.01R）与行星面（R）是**同心球面** ⇒ 在"球缘"处必然相切，深度差 → 0 ⇒
    /// 无论 δz 多小都有一条"谁在前由量化噪声定"的带 ⇒ 壳逐帧翻面（用户实测"动视角就闪"）。
    /// ⇒ 确定性只能来自**画序**：壳排在行星**之前**，行星随后不透明覆盖壳的盘面部分 ⇒ 两者不进深度比较。
    /// 用例钉的就是这条次序——**次序写乱，球缘闪会回来**。
    /// </summary>
    public class PFViewLayersTests
    {
        [Fact]
        public void Layering_IsStrictlyIncreasing()
        {
            Assert.True(PFViewLayers.IsLayeredInOrder(),
                "必须满足 星空 < 大气壳 < 不透明天体/船："
                + PFViewLayers.Starfield + " < "
                + PFViewLayers.AtmosphereShell + " < " + PFViewLayers.PlanetAndShips);
        }

        [Fact]
        public void Shell_SitsBeforeOpaqueBodies_AndAfterTheStarfield()
        {
            // ① 壳在行星之前 ⇒ 行星（不透明、后画）覆盖壳的盘面部分 ⇒ 球缘不进深度比较（消闪的关键）
            Assert.True(PFViewLayers.AtmosphereShell < PFViewLayers.PlanetAndShips,
                "壳必须排在行星之前（否则壳与行星在球缘处靠深度竞争 ⇒ 闪）");
            Assert.True(PFViewLayers.Starfield < PFViewLayers.AtmosphereShell,
                "星空必须在场景和大气之前");
        }
    }
}
