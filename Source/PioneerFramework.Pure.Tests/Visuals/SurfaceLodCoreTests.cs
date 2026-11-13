using System;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class SurfaceLodCoreTests
{
    [Fact]
    public void FacingCapCosineAtRadiusTwoRadiiAndInfinity()
    {
        // 钉住可见帽判据 cos = R/d（归档 :543-547）。2026-10-09 的事故就是把半角写成 asin(R/d)，
        // 贴地时 6.2° vs 83°，整套 LOD 判据错档；这里用三个距离钉死正确关系。
        Assert.Equal(1f, SurfaceLodCore.FacingCapCosine(1f, 1f));          // 相机贴地：只剩正下方
        Assert.Equal(0.5f, SurfaceLodCore.FacingCapCosine(1f, 2f));        // d = 2R：cos = 1/2
        Assert.Equal(1f / 3f, SurfaceLodCore.FacingCapCosine(1f, 3f), 5);  // d = 3R：cos = 1/3
        Assert.Equal(0f, SurfaceLodCore.FacingCapCosine(1f, float.PositiveInfinity));   // 无穷远 = 半球
    }

    [Fact]
    public void FacingCapCosineDegenerateInputsReturnSafeSentinel()
    {
        // 钉住退化口径：0/负数/NaN 返回 1f（只剩正下方那格的保守哨兵），不抛也不产生 NaN。
        Assert.Equal(1f, SurfaceLodCore.FacingCapCosine(1f, 0f));
        Assert.Equal(1f, SurfaceLodCore.FacingCapCosine(1f, -5f));
        Assert.Equal(1f, SurfaceLodCore.FacingCapCosine(0f, 5f));
        Assert.Equal(1f, SurfaceLodCore.FacingCapCosine(-1f, 5f));
        Assert.Equal(1f, SurfaceLodCore.FacingCapCosine(float.NaN, 5f));
        Assert.Equal(1f, SurfaceLodCore.FacingCapCosine(1f, float.NaN));
        Assert.Equal(1f, SurfaceLodCore.FacingCapCosine(float.PositiveInfinity, 5f));
        Assert.Equal(1f, SurfaceLodCore.FacingCapCosine(1f, float.NegativeInfinity));
        // 相机在球内（d < R）：比值 > 1，按归档夹到 1。
        Assert.Equal(1f, SurfaceLodCore.FacingCapCosine(2f, 1f));
        Assert.False(float.IsNaN(SurfaceLodCore.FacingCapCosine(float.NaN, float.NaN)));
    }

    [Fact]
    public void FacingCapFractionMatchesCapAreaRatio()
    {
        // 钉住 cos → 面积比 (1−cos)/2：远看 = 0.5（半球），贴地 ≈ 0，完全背光 = 1。比例错了会直接错档。
        Assert.Equal(0f, SurfaceLodCore.FacingCapFraction(1f));
        Assert.Equal(0.5f, SurfaceLodCore.FacingCapFraction(0f));
        Assert.Equal(1f, SurfaceLodCore.FacingCapFraction(-1f));
        Assert.Equal(0.25f, SurfaceLodCore.FacingCapFraction(0.5f));
        // 与 FacingCapCosine 联动：d = 2R ⇒ 半角 60° 帽 = 整球 1/4。
        Assert.Equal(0.25f, SurfaceLodCore.FacingCapFraction(SurfaceLodCore.FacingCapCosine(1f, 2f)));
        Assert.Equal(0.5f, SurfaceLodCore.FacingCapFraction(SurfaceLodCore.FacingCapCosine(1f, float.PositiveInfinity)));
    }

    [Fact]
    public void FacingCapFractionDegenerateInputsReturnZero()
    {
        // 钉住：NaN/∞ 不进下游比例（否则像素数/格数全链路染 NaN，LOD 分支静默失控）。
        Assert.Equal(0f, SurfaceLodCore.FacingCapFraction(float.NaN));
        Assert.Equal(0f, SurfaceLodCore.FacingCapFraction(float.PositiveInfinity));
        Assert.Equal(0f, SurfaceLodCore.FacingCapFraction(float.NegativeInfinity));
    }

    [Fact]
    public void TileScreenPixelsShrinksWithDistance()
    {
        // 钉住像素数随距离反比衰减（归档 :553-558）：像素数算反会让"近档更糊、远档更细"。
        // 取值：tile=10 m、pixelHeight=1000 px、fov=60°，d=100 m ⇒ 86.60 px。
        float at100 = SurfaceLodCore.TileScreenPixels(10f, 100f, 1000f, 60f);
        float at200 = SurfaceLodCore.TileScreenPixels(10f, 200f, 1000f, 60f);
        float at400 = SurfaceLodCore.TileScreenPixels(10f, 400f, 1000f, 60f);

        Assert.True(MathF.Abs(at100 - 86.60254f) < 0.01f, "d=100 的像素数 " + at100);
        Assert.True(at100 > at200);
        Assert.True(at200 > at400);
        Assert.True(MathF.Abs(at100 / 2f - at200) < 0.01f);
        Assert.True(MathF.Abs(at100 / 4f - at400) < 0.01f);
    }

    [Fact]
    public void TileScreenPixelsDegenerateInputsReturnZero()
    {
        // 钉住：0/负数/NaN/∞/非法 fov 一律 0f。归档对 fov=0 会除零出 ∞、≥180 会翻出负像素数，
        // 本次显式挡掉（负数像素会让 ShouldUseTiles 静默走错分支）。
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(10f, 0f, 1000f, 60f));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(10f, -5f, 1000f, 60f));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(0f, 100f, 1000f, 60f));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(-5f, 100f, 1000f, 60f));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(10f, 100f, 1000f, 0f));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(10f, 100f, 1000f, 180f));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(10f, 100f, 1000f, 240f));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(10f, 100f, 1000f, float.NaN));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(10f, 100f, float.NaN, 60f));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(float.NaN, 100f, 1000f, 60f));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(10f, float.PositiveInfinity, 1000f, 60f));
        Assert.Equal(0f, SurfaceLodCore.TileScreenPixels(float.PositiveInfinity, 100f, 1000f, 60f));

        // pixelHeight = 0 是合法退化（归档用 Max(pixelHeight,1) 兜底）：不许出 NaN。
        float noHeight = SurfaceLodCore.TileScreenPixels(10f, 100f, 0f, 60f);
        Assert.True(float.IsFinite(noHeight));
        Assert.True(noHeight > 0f);
    }

    [Fact]
    public void EstimatedVisibleTilesGrowsWithDistance()
    {
        // 钉住"估算格数随距离单调不减"（归档 :564-574）：单调性坏了会出现"拉远反而铺更多格"，
        // 直接表现为近景切远档时闪一下或掉帧。
        const int tiles = 10000;
        int at1 = SurfaceLodCore.EstimatedVisibleTiles(tiles, 1f, 1f, 60f, 1f);
        int at2 = SurfaceLodCore.EstimatedVisibleTiles(tiles, 1f, 2f, 60f, 1f);
        int at3 = SurfaceLodCore.EstimatedVisibleTiles(tiles, 1f, 3f, 60f, 1f);
        int at4 = SurfaceLodCore.EstimatedVisibleTiles(tiles, 1f, 4f, 60f, 1f);
        int at5 = SurfaceLodCore.EstimatedVisibleTiles(tiles, 1f, 5f, 60f, 1f);

        Assert.True(at1 > 0);
        Assert.True(at1 <= at2);
        Assert.True(at2 <= at3);
        Assert.True(at3 <= at4);
        Assert.True(at4 <= at5);
        Assert.True(at3 > at1);
        Assert.True(at5 <= tiles);

        // d = 1R/2R 时"高度地板 1 m"生效 ⇒ 估算不变（归档同式，别以为是 bug）。
        Assert.Equal(at1, at2);
        // 钉住具体量级：d = 3R（高度 2 m）、fov 60°、aspect 1 ⇒ 足迹 5.3333 / 球面积 12.5664 ⇒ ceil(4244.13) = 4245。
        Assert.Equal(4245, at3);
        // d = 5R 时足迹盖过整球 ⇒ 夹到 tilesCount。
        Assert.Equal(tiles, at5);
    }

    [Fact]
    public void EstimatedVisibleTilesStaysWithinRange()
    {
        // 钉住不越界：估算值永远落在 [0, tilesCount]（否则分块预分配尺寸会用负数或爆内存）。
        foreach (float distance in new[] { 1f, 2f, 3f, 4f, 5f, 10f, 100f, 1e6f, float.PositiveInfinity })
        {
            int estimate = SurfaceLodCore.EstimatedVisibleTiles(10000, 1f, distance, 60f, 1f);
            Assert.InRange(estimate, 0, 10000);
        }

        Assert.Equal(10000, SurfaceLodCore.EstimatedVisibleTiles(10000, 1f, float.PositiveInfinity, 60f, 1f));
    }

    [Fact]
    public void EstimatedVisibleTilesDegenerateInputsReturnZero()
    {
        // 钉住退化口径：格数/半径/距离非法、fov 非法、NaN 一律 0（不抛）。
        // 与归档的唯一有意偏差：相机已在球内（d ≤ R）判 0 —— 归档会按"高度 1 m"估出一屏，纯属误导。
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(0, 1f, 2f, 60f, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(-5, 1f, 2f, 60f, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(1000, 0f, 2f, 60f, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(1000, -1f, 2f, 60f, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(1000, 1f, 0f, 60f, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(1000, 1f, -2f, 60f, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(1000, 1f, 2f, 0f, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(1000, 1f, 2f, 180f, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(1000, 1f, 2f, float.NaN, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(1000, float.NaN, 2f, 60f, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(1000, 1f, float.NaN, 60f, 1f));
        Assert.Equal(0, SurfaceLodCore.EstimatedVisibleTiles(1000, 1f, 2f, 60f, float.NaN));
        // aspect 为负按归档夹到 0.01，仍是有界正数（不是 0 也不是 NaN）。
        int narrow = SurfaceLodCore.EstimatedVisibleTiles(10000, 1f, 3f, 60f, -3f);
        Assert.InRange(narrow, 1, 10000);
    }

    [Fact]
    public void ShouldUseTilesBoundaries()
    {
        // 钉住档位判据（归档 :635-640）：格太小（< 2.5 px）或可见格数超预算（> 20000）就不铺 tile。
        // 边界按归档的严格小于：正好 2.5 px 仍铺、正好 20000 格仍铺。
        Assert.True(SurfaceLodCore.ShouldUseTiles(100, 10f));
        Assert.False(SurfaceLodCore.ShouldUseTiles(100, 2.49f));
        Assert.True(SurfaceLodCore.ShouldUseTiles(100, 2.5f));
        Assert.False(SurfaceLodCore.ShouldUseTiles(0, 10f));
        Assert.False(SurfaceLodCore.ShouldUseTiles(-1, 10f));
        Assert.True(SurfaceLodCore.ShouldUseTiles(20000, 10f));
        Assert.False(SurfaceLodCore.ShouldUseTiles(20001, 10f));
    }

    [Fact]
    public void ShouldUseTilesDegenerateInputsReturnFalse()
    {
        // 钉住退化口径：NaN/∞ 像素数一律"不铺"（分格关闭比按 ∞ 铺满屏安全），且不抛。
        Assert.False(SurfaceLodCore.ShouldUseTiles(100, float.NaN));
        Assert.False(SurfaceLodCore.ShouldUseTiles(100, float.PositiveInfinity));
        Assert.False(SurfaceLodCore.ShouldUseTiles(100, float.NegativeInfinity));
        Assert.False(SurfaceLodCore.ShouldUseTiles(100, 10f, maxVisibleTiles: 99));
        Assert.True(SurfaceLodCore.ShouldUseTiles(100, 10f, maxVisibleTiles: 100));
        Assert.False(SurfaceLodCore.ShouldUseTiles(100, 3f, minTileScreenPixels: 3.5f));
        Assert.True(SurfaceLodCore.ShouldUseTiles(100, 3.5f, minTileScreenPixels: 3.5f));
    }
}
