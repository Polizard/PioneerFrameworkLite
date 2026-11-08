using System;
using System.IO;
using System.Threading;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewStellarHeatCacheTests
{
    [Fact]
    public void CacheUsesTheShortDriftCycleAndWrapsBetweenLastAndFirstFrames()
    {
        float phase = PFViewStellarVisualCore.Phase(10);
        float nextCycle = PFViewStellarVisualCore.Phase(10 + 36000.0 / 34);
        Assert.Equal(PFViewStellarHeatCacheCore.FramePosition(phase),
            PFViewStellarHeatCacheCore.FramePosition(nextCycle), 4);
        Assert.InRange(PFViewStellarHeatCacheCore.FramePosition(PFViewStellarVisualCore.Phase(35999)),
            PFViewStellarHeatCacheCore.FrameCount - 4f, PFViewStellarHeatCacheCore.FrameCount);
        Assert.Equal(0f, PFViewStellarHeatCacheCore.FramePosition(PFViewStellarVisualCore.Phase(36000)));
    }

    [Fact]
    public void FramePaddingWrapsLongitudeAndClampsLatitudeWithoutBleedingIntoOtherFrames()
    {
        var pixels = new Color32[PFViewStellarHeatCacheCore.Width * PFViewStellarHeatCacheCore.Height];
        PFViewStellarHeatCacheCore.BakeFrame(pixels, 0, CancellationToken.None);

        const int stride = PFViewStellarHeatCacheCore.Width;
        const int tileWidth = PFViewStellarHeatCacheCore.TileWidth;
        const int tileHeight = PFViewStellarHeatCacheCore.TileHeight;
        Assert.Equal(pixels[stride + tileWidth - 2].r, pixels[stride].r);
        Assert.Equal(pixels[stride + 1].r, pixels[stride + tileWidth - 1].r);
        Assert.Equal(pixels[stride + 10].r, pixels[10].r);
        Assert.Equal(pixels[(tileHeight - 2) * stride + 10].r, pixels[(tileHeight - 1) * stride + 10].r);
        Assert.InRange(pixels[stride + 1].r, (byte)120, (byte)245);
        Assert.Equal(0, pixels[stride + tileWidth].r);
    }

    [Fact]
    public void FourAdjacentFramesShareTheTileWithoutOverwritingEachOther()
    {
        var pixels = new Color32[PFViewStellarHeatCacheCore.Width * PFViewStellarHeatCacheCore.Height];
        int interior = PFViewStellarHeatCacheCore.Width + 48;
        PFViewStellarHeatCacheCore.BakeFrame(pixels, 0, CancellationToken.None);
        byte firstFrame = pixels[interior].r;
        for (int frame = 1; frame < 4; frame++)
            PFViewStellarHeatCacheCore.BakeFrame(pixels, frame, CancellationToken.None);

        Assert.Equal(firstFrame, pixels[interior].r);
        Assert.NotEqual(0, pixels[interior].g);
        Assert.NotEqual(0, pixels[interior].b);
        Assert.NotEqual(0, pixels[interior].a);
        Assert.Equal(0, pixels[interior + PFViewStellarHeatCacheCore.TileWidth].r);
    }

    [Fact]
    public void ShaderSelectsTheCacheOnlyWhenReadyAndKeepsTheOriginalDepthMarch()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Shaders", "PF_StellarVolume.frag.glsl");
        string source = File.ReadAllText(path);
        Assert.Contains("uniform sampler2D _StellarHeatCache", source);
        Assert.Contains("uMaterial._StellarEye.w > .5", source);
        Assert.Contains("granulation(photoQ * 49.0", source);
        Assert.Contains("for (int i = 0; i < 64; ++i)", source);
        Assert.Contains("gl_FragDepth", source);
        var layout = Waylith.Rendering.ShaderMetadataParser.ExtractMaterialLayout(source, Path.GetFileName(path));
        Assert.False(layout.HasErrors);
        var slot = Assert.Single(layout.TextureSlots);
        Assert.Equal("_StellarHeatCache", slot.PropertyName);
        Assert.Equal(1, slot.Binding);
        Assert.Equal(Waylith.Rendering.MaterialTextureKind.Combined, slot.Kind);
    }

    [Fact]
    public void S3AShaderUsesTheSameHeatCacheSlotAndPrologueBranch()
    {
        // S3-a 只改 march 的积分方式；热缓存 prologue（每射线一次的光球热度）必须与 legacy 完全相同，
        // 否则"缓存开/关"的 A/B 与已批准观感都会漂移。
        string path = Path.Combine(AppContext.BaseDirectory, "Shaders", "PF_StellarVolumeS3A.frag.glsl");
        Assert.True(File.Exists(path));
        string source = File.ReadAllText(path);
        Assert.Contains("uniform sampler2D _StellarHeatCache", source);
        Assert.Contains("uMaterial._StellarEye.w > .5", source);
        Assert.Contains("granulation(photoQ * 49.0", source);
        Assert.Contains("gl_FragDepth", source);
        var layout = Waylith.Rendering.ShaderMetadataParser.ExtractMaterialLayout(source, Path.GetFileName(path));
        Assert.False(layout.HasErrors);
        var slot = Assert.Single(layout.TextureSlots);
        Assert.Equal("_StellarHeatCache", slot.PropertyName);
        Assert.Equal(1, slot.Binding);
        Assert.Equal(Waylith.Rendering.MaterialTextureKind.Combined, slot.Kind);
    }
}
