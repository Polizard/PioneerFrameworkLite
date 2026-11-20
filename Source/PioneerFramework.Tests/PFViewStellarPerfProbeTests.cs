using System;
using System.IO;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// P0 恒星性能探针（2026-10-28）的自动验收：
/// · 帧时统计核的采样口径（非法值丢弃、nearest-rank 百分位、环形覆盖、Reset）；
/// · 光球和日冕开启，64 步、RT 1.0（原分辨率）、光球缓存默认开，探针不记录；
/// · shader 步数下限放宽后，默认上传值 64 与循环上限 64 不变。
/// 这里不测 GPU 帧率（本机无 GPU 计时 API，实测由用户按计划 §3.3 跑）。
/// </summary>
public class PFViewStellarPerfProbeTests
{
    [Fact]
    public void FrameStatsRejectNonFiniteAndNegativeSamples()
    {
        var stats = new PFViewFrameStatsCore(4);
        Assert.False(stats.Add(float.NaN));
        Assert.False(stats.Add(float.PositiveInfinity));
        Assert.False(stats.Add(float.NegativeInfinity));
        Assert.False(stats.Add(-0.5f));
        Assert.Equal(0, stats.Count);
        Assert.Equal(0f, stats.Median());

        Assert.True(stats.Add(16f));
        Assert.Equal(1, stats.Count);
        Assert.Equal(16f, stats.Last);
    }

    [Fact]
    public void FrameStatsUseNearestRankPercentiles()
    {
        var stats = new PFViewFrameStatsCore(8);
        foreach (float ms in new[] { 30f, 10f, 20f }) stats.Add(ms);
        Assert.Equal(20f, stats.Median());      // p50 nearest-rank: ceil(1.5)-1 = 1 -> 排序后第 2 个
        Assert.Equal(30f, stats.P99());         // 样本少时 p99 = 最大值
        Assert.Equal(20f, stats.Average());
    }

    [Fact]
    public void FrameStatsMedianOfEvenCountTakesTheLowerMedian()
    {
        var stats = new PFViewFrameStatsCore(8);
        foreach (float ms in new[] { 10f, 20f, 30f, 40f }) stats.Add(ms);
        Assert.Equal(20f, stats.Median());      // ceil(2)-1 = 1 -> 第 2 个（下中位），口径写死
    }

    [Fact]
    public void FrameStatsWrapAtCapacityAndReset()
    {
        var stats = new PFViewFrameStatsCore(3);
        foreach (float ms in new[] { 1f, 2f, 3f, 4f, 5f }) stats.Add(ms);
        Assert.Equal(3, stats.Count);
        Assert.Equal(5f, stats.Last);
        Assert.Equal(4f, stats.Median());       // 仅剩 {3,4,5}
        Assert.Equal(5f, stats.P99());
        stats.Reset();
        Assert.Equal(0, stats.Count);
        Assert.Equal(0f, stats.Average());
    }

    [Fact]
    public void StellarDebugSwitchesDefaultToCurrentBehaviour()
    {
        // 两个 pass 都开、步数 0（=64）、RT 1.0（原分辨率），光球缓存默认开。
        // 注意：只读 PFViewStellarProbe（纯静态类）——不碰 MainTabWindow_PFView 的静态构造器
        // （那里会建 Mesh，headless 测试需要 GPU/Vulkan 依赖）。
        Assert.True(PFViewStellarProbe.SurfacePass);
        Assert.True(PFViewStellarProbe.HaloPass);
        Assert.Equal(0, PFViewStellarProbe.StepBudget);
        Assert.Equal(1f, PFViewStellarProbe.RenderScale);
        Assert.True(PFViewStellarProbe.DefaultHeatCacheEnabled);
        Assert.False(PFViewStellarProbe.Active);
        Assert.False(PFViewStellarProbe.FastRendererEnabled);   // 默认旧版视觉
        // S3-a（区域解析积分）默认档：离线保真度验证 PASS ⇒ true，FAIL ⇒ false（Lead 冻结）。
        // TODO(Lead): 先按"PASS=true"占位；结论出来若为 FAIL 改成 Assert.False。
        Assert.True(PFViewStellarProbe.DefaultS3AEnabled);
        Assert.Equal(PFViewStellarProbe.DefaultS3AEnabled, PFViewStellarProbe.S3AEnabled);
        // S3-b 是玩家显式选择的性能档，默认必须关闭（默认观感仍是全分辨率 S3-a/legacy）。
        Assert.False(PFViewStellarProbe.DefaultS3BEnabled);
        Assert.Equal(PFViewStellarProbe.DefaultS3BEnabled, PFViewStellarProbe.S3BEnabled);
        Assert.Equal(64, PFViewStellarVisualCore.SurfaceSteps);
        Assert.Equal(64, PFViewStellarProbe.ResolveStepBudget());
    }

    [Fact]
    public void StellarDebugResetRestoresDefaults()
    {
        PFViewStellarProbe.SurfacePass = false;
        PFViewStellarProbe.HaloPass = false;
        PFViewStellarProbe.StepBudget = 8;
        PFViewStellarProbe.RenderScale = 0.5f;
        PFViewStellarProbe.S3AEnabled = !PFViewStellarProbe.DefaultS3AEnabled;
        PFViewStellarProbe.S3BEnabled = !PFViewStellarProbe.DefaultS3BEnabled;
        try
        {
            PFViewStellarProbe.ResetDefaults();
            Assert.True(PFViewStellarProbe.SurfacePass);
            Assert.True(PFViewStellarProbe.HaloPass);
            Assert.Equal(0, PFViewStellarProbe.StepBudget);
            Assert.Equal(1f, PFViewStellarProbe.RenderScale);
            Assert.Equal(PFViewStellarProbe.DefaultS3AEnabled, PFViewStellarProbe.S3AEnabled);
            Assert.Equal(PFViewStellarProbe.DefaultS3BEnabled, PFViewStellarProbe.S3BEnabled);
            Assert.Equal(64, PFViewStellarProbe.ResolveStepBudget());
        }
        finally
        {
            PFViewStellarProbe.ResetDefaults();
        }
    }

    [Fact]
    public void StellarQualityTiersMapToDocumentedStepBudgets()
    {
        Assert.Equal(64, PFViewStellarQualityCore.StepsFor(PFViewStellarQuality.VisualFirst));
        Assert.Equal(48, PFViewStellarQualityCore.StepsFor(PFViewStellarQuality.Balanced));
        Assert.Equal(32, PFViewStellarQualityCore.StepsFor(PFViewStellarQuality.PerformanceFirst));
        Assert.Equal(PFViewStellarQuality.VisualFirst, PFViewStellarQualityCore.Sanitize(12345));
        Assert.Equal(PFViewStellarQuality.Balanced, PFViewStellarQualityCore.Sanitize((int)PFViewStellarQuality.Balanced));
    }

    [Fact]
    public void LegacyShaderKeepsTheOriginalVolumeMarch()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Shaders", "PF_StellarVolume.frag.glsl");
        Assert.True(File.Exists(path));
        string source = File.ReadAllText(path);
        // Both cached and original photosphere detail use the shared 64-step volume march.
        // P0 only widened the lower clamp so the debug probe can A/B step budgets.
        Assert.Contains("clamp(uMaterial._StellarParams.x, 4.0, 64.0)", source);
        Assert.Contains("for (int i = 0; i < 64; ++i)", source);
        Assert.DoesNotContain("photosphereHit(", source);
    }

    [Fact]
    public void FastShaderUsesAnalyticPhotosphereAndBoundedCoronaSteps()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Shaders", "PF_StellarVolumeFast.frag.glsl");
        Assert.True(File.Exists(path));
        string source = File.ReadAllText(path);
        // Optional fast renderer contract: surface pass shades the photosphere once (analytic hit);
        // halo pass integrates the transparent corona with a bounded step count.
        Assert.Contains("photosphereHit(", source);
        Assert.Contains("photosphereColor(", source);
        Assert.Contains("const float SHELL = 1.65;", source);
        Assert.Contains("quality = clamp(uMaterial._StellarParams.x / 64.0, .25, 1.0)", source);
        Assert.Contains("for (int i = 0; i < 24; ++i)", source);
        Assert.DoesNotContain("for (int i = 0; i < 64; ++i)", source);
        // Both passes must still write real depth and keep the two texture-space anchors the
        // existing visual regression pins (granulation/prominences).
        Assert.Contains("gl_FragDepth", source);
        Assert.Contains("granulation(", source);
        Assert.Contains("prominences(", source);
    }
}
