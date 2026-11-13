using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class SphereTileIndexCoreTests
{
    // 均匀球面格心（Fibonacci 球）：确定性、无 RNG，10 万格也只是 O(n) 生成。
    private static Vector3[] FibonacciSphere(int count)
    {
        var centers = new Vector3[count];
        double goldenAngle = Math.PI * (3.0 - Math.Sqrt(5.0));
        for (int i = 0; i < count; i++)
        {
            double y = 1.0 - (2.0 * i + 1.0) / count;
            double ring = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
            double theta = goldenAngle * i;
            centers[i] = new Vector3((float)(Math.Cos(theta) * ring), (float)y, (float)(Math.Sin(theta) * ring));
        }

        return centers;
    }

    [Fact]
    public void DefaultBucketResolutionMatchesReferenceTable()
    {
        // 钉住参考模组的 桶数 = clamp(√count/5, 2, 128)（CelestialLazyGrid.cs:27）。
        // 这张表决定"3 圈外扩够不够"：数算错会让拾取要么扫太多桶、要么经常 3 圈落空。
        Assert.Equal(2, SphereTileIndexCore.DefaultBucketResolution(0));
        Assert.Equal(2, SphereTileIndexCore.DefaultBucketResolution(1));
        Assert.Equal(2, SphereTileIndexCore.DefaultBucketResolution(25));
        Assert.Equal(2, SphereTileIndexCore.DefaultBucketResolution(100));
        Assert.Equal(2, SphereTileIndexCore.DefaultBucketResolution(101));
        Assert.Equal(3, SphereTileIndexCore.DefaultBucketResolution(225));
        Assert.Equal(3, SphereTileIndexCore.DefaultBucketResolution(250));
        Assert.Equal(6, SphereTileIndexCore.DefaultBucketResolution(1000));
        Assert.Equal(20, SphereTileIndexCore.DefaultBucketResolution(10000));
        Assert.Equal(63, SphereTileIndexCore.DefaultBucketResolution(100000));
        Assert.Equal(128, SphereTileIndexCore.DefaultBucketResolution(1000000));
    }

    [Fact]
    public void BucketResolutionConstructorArgumentIsRespectedAndClamped()
    {
        // 钉住：显式分辨率原样生效（测试要用它造"第 2/3 圈才命中"的几何），超大值夹到 128。
        var ten = FibonacciSphere(10);
        Assert.Equal(4, new SphereTileIndexCore(ten, 4).BucketResolution);
        Assert.Equal(128, new SphereTileIndexCore(ten, 1000).BucketResolution);
        Assert.Equal(SphereTileIndexCore.DefaultBucketResolution(10), new SphereTileIndexCore(ten, -1).BucketResolution);
        Assert.Equal(SphereTileIndexCore.DefaultBucketResolution(10), new SphereTileIndexCore(ten, 0).BucketResolution);
    }

    [Fact]
    public void QueryAtTileCenterReturnsThatTile()
    {
        // 钉住最基本判据：查询方向正好等于某格心 ⇒ 必须返回那一格（拾取高亮的锚点，错了整片 UI 都偏）。
        var centers = FibonacciSphere(1000);
        var index = new SphereTileIndexCore(centers);
        foreach (int expected in new[] { 0, 1, 137, 500, 998, 999 })
        {
            Assert.True(index.TryPick(centers[expected], out int tile));
            Assert.Equal(expected, tile);
        }
    }

    [Fact]
    public void ReversedDirectionHitsTheOppositeTile()
    {
        // 钉住"拾取不做可见性判断"：反向方向照样可命中（参考模组这里只是"点在哪个格"，
        // 背面剔除是渲染的事；若在拾取里偷偷做可见性，背面的格会点不中）。
        var centers = new[]
        {
            new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f),
            new Vector3(0f, 1f, 0f), new Vector3(0f, -1f, 0f),
            new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f),
        };
        var index = new SphereTileIndexCore(centers);

        Assert.True(index.TryPick(new Vector3(1f, 0f, 0f), out int plusX));
        Assert.True(index.TryPick(new Vector3(-1f, 0f, 0f), out int minusX));
        Assert.Equal(0, plusX);
        Assert.Equal(1, minusX);
        Assert.NotEqual(plusX, minusX);

        Assert.True(index.TryPick(new Vector3(0f, -1f, 0f), out int minusY));
        Assert.Equal(3, minusY);
    }

    [Fact]
    public void QueryDirectionAndCentersDoNotNeedNormalization()
    {
        // 钉住"允许未归一化输入"：调用方拿到的是任意长度的方向向量（例如相机到球心的差）。
        // 不归一化就点积，会让最远的格反而赢 —— 所以内部必须归一化，且结果与长度无关。
        var normalized = FibonacciSphere(300);
        var scaled = new Vector3[normalized.Length];
        for (int i = 0; i < normalized.Length; i++) scaled[i] = normalized[i] * 7.5f;

        var index = new SphereTileIndexCore(scaled);
        for (int i = 0; i < normalized.Length; i += 37)
        {
            Assert.True(index.TryPick(normalized[i] * 0.01f, out int tile));
            Assert.Equal(i, tile);
        }
    }

    [Fact]
    public void ZeroLengthAndNonFiniteCentersAreIgnoredButIndicesStayAligned()
    {
        // 钉住"忽略零长度但索引仍与输入下标一致"：被忽略的格不能挪动其他格的编号，
        // 否则拾取返回的 tile 会和 PlanetLayer.Tiles 对不上（高亮错格）。
        var centers = new[]
        {
            Vector3.Zero,
            new Vector3(1f, 0f, 0f),
            new Vector3(float.NaN, 0f, 0f),
            new Vector3(0f, -1f, 0f),
            new Vector3(float.PositiveInfinity, 0f, 0f),
        };
        var index = new SphereTileIndexCore(centers);

        Assert.Equal(5, index.Count);
        Assert.True(index.TryPick(new Vector3(1f, 0f, 0f), out int plusX));
        Assert.Equal(1, plusX);
        Assert.True(index.TryPick(new Vector3(0f, -1f, 0f), out int minusY));
        Assert.Equal(3, minusY);
        Assert.False(index.TryPick(Vector3.Zero, out _));   // 零长度查询：不猜方向
    }

    [Fact]
    public void EmptyAndAllZeroInputsCannotPick()
    {
        // 钉住空数据：没有可用格心时 TryPick 必须返回 false（不得返回 -1 之外的垃圾下标）。
        Assert.False(new SphereTileIndexCore(Array.Empty<Vector3>()).TryPick(new Vector3(1f, 0f, 0f), out int tile));
        Assert.Equal(-1, tile);

        var zeros = new[] { Vector3.Zero, Vector3.Zero, Vector3.Zero };
        var index = new SphereTileIndexCore(zeros);
        Assert.Equal(3, index.Count);
        Assert.False(index.TryPick(new Vector3(1f, 0f, 0f), out tile));
        Assert.Equal(-1, tile);
    }

    [Fact]
    public void TieBreaksToSmallestTileIndex()
    {
        // 钉住确定性平局：完全相同的两个格心（数据退化/重复刷格）必须永远返回最小下标。
        // 若按桶内遍历顺序返回，同一查询在高亮时会在两格之间闪。
        var shared = new Vector3(0.6f, 0.8f, 0f);
        var centers = new[] { new Vector3(-1f, 0f, 0f), shared, shared, shared };
        var index = new SphereTileIndexCore(centers);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            Assert.True(index.TryPick(shared, out int tile));
            Assert.Equal(1, tile);
        }
    }

    [Fact]
    public void BucketSearchExpandsToSecondAndThirdRing()
    {
        // 钉住"由内向外扩桶，最多 3 圈"：桶挖空时（大分辨率 + 稀格）必须能扩到 2/3 圈才命中；
        // 超过 3 圈则宁可不命中 —— 这条是把参考模组 range 1..3 的语义钉死，改坏会导致拾取"时灵时不灵"。
        // 所有坐标按 res=8 手算：Coord(v) = clamp(floor((v+1)*4), 0, 7)，查询 +Z 的桶是 (4,4,7)。
        var ring1Tile = new[] { new Vector3(0f, 0f, 1f) };                 // (4,4,7) 同桶
        var ring2Tile = new[] { new Vector3(0f, 0.5f, 0.8660254f) };       // (4,6,7) 切比雪夫 2
        var ring3Tile = new[] { new Vector3(0f, 0.8f, 0.6f) };             // (4,7,6) 切比雪夫 3
        var tooFarTile = new[] { new Vector3(-0.9f, 0.4f, 0.1732f) };      // (0,5,4) 切比雪夫 4

        var query = new Vector3(0f, 0f, 1f);
        Assert.True(new SphereTileIndexCore(ring1Tile, 8).TryPick(query, out int tile1));
        Assert.Equal(0, tile1);
        Assert.True(new SphereTileIndexCore(ring2Tile, 8).TryPick(query, out int tile2));
        Assert.Equal(0, tile2);
        Assert.True(new SphereTileIndexCore(ring3Tile, 8).TryPick(query, out int tile3));
        Assert.Equal(0, tile3);
        Assert.False(new SphereTileIndexCore(tooFarTile, 8).TryPick(query, out _));
    }

    [Fact]
    public void PickChoosesLargestNormalizedDotInsideTheHitRing()
    {
        // 钉住"点积最大"而不是"第一个找到的"：同一个桶里有多格时必须挑最正的，否则高亮会落在隔壁格。
        // 两格都放进查询桶附近：近的太远就赢不了。
        var near = new Vector3(0.1f, 0f, 0.995f);
        var far = new Vector3(0.5f, 0f, 0.866f);
        var centers = new[] { far, near };   // far 下标更小：若只按"最小下标"就会错选
        var index = new SphereTileIndexCore(centers);

        Assert.True(index.TryPick(new Vector3(0f, 0f, 1f), out int tile));
        Assert.Equal(1, tile);
    }

    [Fact]
    public void HundredThousandTilesBuildAndPickStayFast()
    {
        // 钉住复杂度：10 万格构建 + 单次拾取必须是"快到用户感觉不到"的量级。
        // 上界刻意给得很宽（构建/200 次拾取各 5 s），只抓 O(n²) 之类的结构性退化，不做 flaky 的严格计时。
        var centers = FibonacciSphere(100000);
        var build = Stopwatch.StartNew();
        var index = new SphereTileIndexCore(centers);
        build.Stop();

        Assert.Equal(63, index.BucketResolution);
        Assert.True(build.Elapsed.TotalMilliseconds < 5000.0, "10 万格构建耗时 " + build.Elapsed.TotalMilliseconds + " ms");

        var pick = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
        {
            int expected = (i * 487) % centers.Length;
            Assert.True(index.TryPick(centers[expected], out int tile));
            Assert.Equal(expected, tile);
        }

        pick.Stop();
        Assert.True(pick.Elapsed.TotalMilliseconds < 5000.0, "200 次拾取耗时 " + pick.Elapsed.TotalMilliseconds + " ms");
    }
}
