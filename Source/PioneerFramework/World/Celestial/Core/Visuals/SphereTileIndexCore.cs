// ─────────────────────────────────────────────────────────────────────────────
// 纯核（无 Verse / Unity / RimWorld —— 本文件被 PioneerFramework.Pure.Tests 通配链接，定义 PURE_MATH）。
//
// 对应参考模组的球面桶拾取：CelestialLazyGrid 的 Coord / Key / Index / Pick（CelestialLazyGrid.cs:70-91；
// 分桶分辨率在 :27 = Math.Max(2, Math.Min(128, Math.Sqrt(count)/5))，查询时按 range = 1..3 向外扩桶、
// 命中即返回）。那份实现用"多边形半空间"判定"点是否落在格里"，需要整份格多边形；PFL 这一层先只吃
// **格心方向**（等距网格的格心间距远小于格宽，"归一化点积最大"与"点在多边形内"在绝大多数查询上等价），
// 于是拾取核可以只依赖 System.Numerics.Vector3（纯托管），headless 可钉。
//
// 为什么用计数排序而不是参考模组的 Dictionary 分桶：10 万格时桶数约 25 万，Dictionary 的节点分配与
// 随机访问都更贵；这里把"桶 → 格下标"拍成两个扁平数组（构建 O(n)，拾取最多扫 7³ 个桶），
// 也顺手消掉了字典带来的迭代顺序不确定性。
//
// 语义（测试钉住，改动前先看 Pure.Tests/Visual/SphereTileIndexCoreTests.cs）：
//   · 输入允许未归一化（内部归一化）；零长度 / NaN 的格心忽略，但 tile 下标仍等于输入下标；
//   · 查询方向不需要归一化；零长度查询返回 false（不制造除零，也不猜一个方向）；
//   · 反向方向同样可命中 —— 参考模组此处只是"点在哪格"，不做可见性 / 背面剔除，PFL 也不做；
//   · 命中 = 本次扩桶（第 1 圈含查询自身所在桶，最多 3 圈）里归一化点积最大的格；点积相同取最小下标
//     （确定性：同一查询在同一份数据上必须永远返回同一个数，否则高亮会闪）；
//   · 3 圈内没有任何候选 ⇒ false：格太稀时宁可"没点中"，也不要返回一个明显错的格。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Numerics;

namespace PioneerFramework.Celestial;

/// <summary>
/// 单位球上"格心方向 → 最近格"的桶索引拾取核（参考模组球面桶拾取的纯托管版本）。
/// </summary>
public sealed class SphereTileIndexCore
{
    // 平局判据：浮点归一化后同一方向的点积可能差 1 ulp，仍算平局；平局一律取最小 tile 下标。
    private const float TieEpsilon = 1e-7f;

    // 零长度判据：|v|² 小于它按零处理（远小于任何有效单位向量的 1.0）。
    private const float MinLengthSquared = 1e-24f;

    private const int MinResolution = 2;
    private const int MaxResolution = 128;
    private const int MaxRings = 3;

    private readonly Vector3[] normalizedCenters;
    private readonly int[] bucketStart;
    private readonly int[] tileIds;
    private readonly int resolution;
    private readonly int bucketCount;

    /// <param name="centers">格心方向（允许未归一化；零长度项忽略，索引仍与输入下标一致）。</param>
    /// <param name="bucketResolution">分桶分辨率；&lt;= 0 表示用 <see cref="DefaultBucketResolution"/> 推导。</param>
    public SphereTileIndexCore(IReadOnlyList<Vector3> centers, int bucketResolution = 0)
    {
        ArgumentNullException.ThrowIfNull(centers);

        Count = centers.Count;
        normalizedCenters = new Vector3[Count];
        resolution = bucketResolution > 0 ? Math.Clamp(bucketResolution, 1, MaxResolution) : DefaultBucketResolution(Count);
        bucketCount = resolution * resolution * resolution;

        var keys = new int[Count];          // -1 = 该下标被忽略（零长度 / 非有限）
        var counts = new int[bucketCount];
        int validTiles = 0;
        for (int i = 0; i < Count; i++)
        {
            Vector3 center = centers[i];
            float lengthSquared = center.X * center.X + center.Y * center.Y + center.Z * center.Z;
            if (!(lengthSquared > MinLengthSquared) || float.IsNaN(lengthSquared) || float.IsInfinity(lengthSquared))
            {
                keys[i] = -1;
                continue;
            }

            Vector3 normalized = center / MathF.Sqrt(lengthSquared);
            normalizedCenters[i] = normalized;
            int key = BucketKey(Coord(normalized.X), Coord(normalized.Y), Coord(normalized.Z));
            keys[i] = key;
            counts[key]++;
            validTiles++;
        }

        // 前缀和 = 每个桶在 tileIds 里的 [from, to) 区间。
        bucketStart = new int[bucketCount + 1];
        int running = 0;
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            bucketStart[bucket] = running;
            running += counts[bucket];
        }

        bucketStart[bucketCount] = running;

        tileIds = new int[validTiles];
        Array.Copy(bucketStart, counts, bucketCount);   // counts 复用为写游标，省一份 int[bucketCount]
        for (int i = 0; i < Count; i++)
        {
            int key = keys[i];
            if (key < 0) continue;
            tileIds[counts[key]++] = i;   // 同桶内按 tile 下标升序（稳定 ⇒ 平局判据可复现）
        }
    }

    /// <summary>输入格数（含被忽略的零长度项；输出的 tile 下标与输入下标一一对应）。</summary>
    public int Count { get; }

    /// <summary>实际使用的桶分辨率（参考模组口径：clamp(√count/5, 2, 128)）。</summary>
    public int BucketResolution => resolution;

    /// <summary>
    /// 默认分桶分辨率：clamp((int)(√count / 5), 2, 128)。
    /// 与参考模组 CelestialLazyGrid.cs:27 同一式（整数除法与先除后取整在该式下等价）。
    /// 供测试直接钉住"格数 → 桶数"这张表。
    /// </summary>
    internal static int DefaultBucketResolution(int count)
    {
        if (count <= 0) return MinResolution;
        int derived = (int)(Math.Sqrt(count) / 5.0);
        return Math.Clamp(derived, MinResolution, MaxResolution);
    }

    /// <summary>
    /// 用查询方向拾取格。由内向外扩桶（第 1 圈含查询自身所在桶，最多 3 圈），
    /// 在最先出现的非空圈里取归一化点积最大的格；点积相同取最小下标。
    /// </summary>
    public bool TryPick(Vector3 direction, out int tile)
    {
        tile = -1;
        float lengthSquared = direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z;
        if (!(lengthSquared > MinLengthSquared) || float.IsNaN(lengthSquared) || float.IsInfinity(lengthSquared)) return false;

        Vector3 query = direction / MathF.Sqrt(lengthSquared);
        int x = Coord(query.X);
        int y = Coord(query.Y);
        int z = Coord(query.Z);

        int bestTile = -1;
        float bestDot = float.NegativeInfinity;
        for (int ring = 1; ring <= MaxRings; ring++)
        {
            int x0 = Math.Max(0, x - ring);
            int x1 = Math.Min(resolution - 1, x + ring);
            int y0 = Math.Max(0, y - ring);
            int y1 = Math.Min(resolution - 1, y + ring);
            int z0 = Math.Max(0, z - ring);
            int z1 = Math.Min(resolution - 1, z + ring);
            for (int a = x0; a <= x1; a++)
            {
                for (int b = y0; b <= y1; b++)
                {
                    for (int c = z0; c <= z1; c++)
                    {
                        int key = BucketKey(a, b, c);
                        int to = bucketStart[key + 1];
                        for (int slot = bucketStart[key]; slot < to; slot++)
                        {
                            int id = tileIds[slot];
                            float dot = Vector3.Dot(query, normalizedCenters[id]);
                            if (dot > bestDot + TieEpsilon)
                            {
                                bestDot = dot;
                                bestTile = id;
                            }
                            else if (bestTile >= 0 && MathF.Abs(dot - bestDot) <= TieEpsilon && id < bestTile)
                            {
                                bestTile = id;
                            }
                        }
                    }
                }
            }

            if (bestTile >= 0)
            {
                tile = bestTile;
                return true;
            }
        }

        return false;
    }

    private int Coord(float value)
    {
        // 参考模组同一式：把 [-1,1] 映到 [0,res)；恰好 +1 时夹到最后一格（否则 Floor 会越界）。
        int coord = (int)MathF.Floor((value + 1f) * resolution * 0.5f);
        if (coord < 0) return 0;
        if (coord >= resolution) return resolution - 1;
        return coord;
    }

    private int BucketKey(int x, int y, int z) => x + resolution * (y + resolution * z);
}
