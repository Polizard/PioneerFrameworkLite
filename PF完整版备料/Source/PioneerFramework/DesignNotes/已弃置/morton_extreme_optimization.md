# Morton 码极致优化——让算法消失在硬件里

> 承接《Morton 码射线命中检测》的基础算法，本文描述如何将其推向极致：
> 不是让算法更快，而是让算法溶解，只剩下硬件原生操作。

## 目标

一次射弹命中查询，全程不离开 L1/L2 cache，不递归，不跳指针，不分支预测失败。

---

## 第一层：PDEP/PEXT — 一条指令完成 Morton 编解码

### 问题

传统 spread 编解码需要十几条位操作指令。

### 硬件解法

Intel Haswell (2013) 起提供 **PDEP**（Parallel Bits Deposit）和 **PEXT**（Parallel Bits Extract），专门做比特的交错与解交错：

```
PDEP: 把 value 的位按 mask 中为 1 的位置"沉积"到结果中
PEXT: 逆操作，从 value 中按 mask 提取位，压缩到一起
```

```
mask = 0b_001_001_001_001_001_001_001_001...  （每隔两位一个 1）

X = 0b...000101 (5) ──PDEP(mask)──→ 0b...001001001  编码完成
                                   ──PEXT(mask)──→ 0b...000101  解码完成
```

300 个部件预处理：~15 条位操作/个 → 1 条指令/个，纳秒级。

### C# 调用

```csharp
using System.Runtime.Intrinsics.X86;

public static class MortonCodec
{
    // 每维 21 位，共 63 位 Morton 码
    const ulong X_MASK = 0b001_001_001_001_001_001_001_001_001_001_001_001_001_001_001_001_001_001_001_001_001UL;
    const ulong Y_MASK = X_MASK << 1;
    const ulong Z_MASK = X_MASK << 2;

    public static ulong Encode(int x, int y, int z)
    {
        // 注意：x, y, z 必须是 21 位无符号值
        return Bmi2.X64.ParallelBitDeposit((ulong)x, X_MASK)
             | Bmi2.X64.ParallelBitDeposit((ulong)y, Y_MASK)
             | Bmi2.X64.ParallelBitDeposit((ulong)z, Z_MASK);
    }

    public static void Decode(ulong code, out int x, out int y, out int z)
    {
        x = (int)Bmi2.X64.ParallelBitExtract(code, X_MASK);
        y = (int)Bmi2.X64.ParallelBitExtract(code, Y_MASK);
        z = (int)Bmi2.X64.ParallelBitExtract(code, Z_MASK);
    }

    public static bool IsSupported => Bmi2.IsSupported;
}
```

若 CPU 不支持 BMI2，退回 bit-twiddling 实现即可。

---

## 第二层：位前缀就是指针

### 核心观察

Morton 码排序后，共享前缀的所有码形成连续区间。八叉树的每一层就是 Morton 码的每 3 个 bit：

```
第 0 层（根） ：最高 3 bits     → 空间分为 8 块
第 1 层      ：次高 3 bits     → 每块再分 8 块
...
第 N 层（叶） ：最低 3 bits
```

不需要显式树结构。区间 `[二分下界, 二分上界)` 就是子节点的"指针"。

```
      ┌──────────────────────────────┐
      │  110 001 000  ←── 起点       │
      │  110 001 011                 │  前缀 "110" 的子树
      │  110 010 100                 │  = [start, end)
      │  110 101 111  ←── 终点       │
      ├──────────────────────────────┤
      │  111 000 001                 │
      │  ...                         │  前缀 "111" 的子树
      └──────────────────────────────┘
```

---

## 第三层：预计算跳表，O(1) 定位任意层级的子空间

### 原理

对 Morton 码取前缀（右移 + 掩码），得到的整数就是该节点在八叉树某一层的标识符。所有可能前缀是一个稠密整数空间，可以用数组做完美哈希。

### 构建

```csharp
public class MortonJumpTable
{
    // levelTables[L] = 第 L 层前缀 → 区间起止的查找表
    private int[][] startTables;
    private int[][] endTables;
    private int maxLevel;

    public void Build(ulong[] mortonCodes, int maxLevel)
    {
        this.maxLevel = maxLevel;
        startTables = new int[maxLevel + 1][];
        endTables   = new int[maxLevel + 1][];

        for (int level = 0; level <= maxLevel; level++)
        {
            int prefixBits = level * 3;
            int shift = 63 - prefixBits;
            int tableSize = 1 << prefixBits;

            startTables[level] = new int[tableSize];
            endTables[level]   = new int[tableSize];

            // 初始化为无效值
            Array.Fill(startTables[level], -1);

            for (int i = 0; i < mortonCodes.Length; i++)
            {
                int prefix = (int)(mortonCodes[i] >> shift);
                if (startTables[level][prefix] == -1)
                    startTables[level][prefix] = i;
                endTables[level][prefix] = i + 1;
            }
        }
    }

    /// <summary>
    /// O(1) 获取某层某前缀对应的数组区间
    /// </summary>
    public (int start, int end) GetRange(int level, int prefix)
    {
        int start = startTables[level][prefix];
        if (start == -1) return (-1, -1); // 该前缀无部件
        return (start, endTables[level][prefix]);
    }
}
```

### 查询示例

```csharp
// 射弹从前方来 → 直接定位"前半段"（八叉树第 1 层的前 4 个象限）
int frontPrefix = 0;  // 最高 3 位 = 000~011 表示前半段
var (start, end) = jumpTable.GetRange(level: 1, prefix: frontPrefix);

// 只扫描 start..end 这段
for (int i = start; i < end; i++)
{
    // SIMD 批量测 AABB（见下一层）
}
```

### 内存分析

```
第 5 层: 2^15 = 32768 个前缀 → start[] + end[] = 32768 × 4 × 2 = 256 KB
第 4 层: 2^12 =  4096 个前缀 → = 32 KB
第 3 层: 2^9  =   512 个前缀 → = 4 KB
第 2 层: 2^6  =    64 个前缀 → = 512 B
第 1 层: 2^3  =     8 个前缀 → = 64 B
────────────────────────────────────────
总计 ≈ 292 KB，L2 cache 通常 ≥ 256 KB，实际只用需要的层
```

---

## 第四层：SIMD 批量 AABB-射线求交

### 原理

AABB-射线求交（slab 法）是天然的向量计算——对每个部件做同一组运算。AVX2 一次算 8 个部件。

### C# 实现

```csharp
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

public static class SimdRayAabb
{
    /// <summary>
    /// 批量射线 vs AABB 求交。
    /// 返回命中部件的索引列表。
    /// parts 数量应为 8 的倍数（不足则补齐 dummy）。
    /// </summary>
    public static unsafe List<int> IntersectBatch(
        Vector3 rayOrigin, Vector3 rayDir, float maxT,
        (Vector3 min, Vector3 max)[] aabbs)
    {
        var results = new List<int>();
        int count = aabbs.Length;
        int padded = (count + 7) & ~7; // 上取整到 8 的倍数

        // 广播射线参数到 8 路
        var rayOx = Vector256.Create(rayOrigin.X);
        var rayOy = Vector256.Create(rayOrigin.Y);
        var rayOz = Vector256.Create(rayOrigin.Z);
        var invDirX = Vector256.Create(1f / rayDir.X);
        var invDirY = Vector256.Create(1f / rayDir.Y);
        var invDirZ = Vector256.Create(1f / rayDir.Z);
        var maxTVec = Vector256.Create(maxT);

        for (int i = 0; i < padded; i += 8)
        {
            // 加载 8 个部件的 min/max
            Span<float> mins = stackalloc float[24]; // 8 × 3
            Span<float> maxs = stackalloc float[24];

            for (int j = 0; j < 8; j++)
            {
                int idx = i + j;
                var (min, max) = idx < count ? aabbs[idx] : aabbs[count - 1];
                mins[j * 3 + 0] = min.X; mins[j * 3 + 1] = min.Y; mins[j * 3 + 2] = min.Z;
                maxs[j * 3 + 0] = max.X; maxs[j * 3 + 1] = max.Y; maxs[j * 3 + 2] = max.Z;
            }

            // X 轴
            var minX = Vector256.Create(mins[0], mins[3], mins[6], mins[9], mins[12], mins[15], mins[18], mins[21]);
            var maxX = Vector256.Create(maxs[0], maxs[3], maxs[6], maxs[9], maxs[12], maxs[15], maxs[18], maxs[21]);

            var t1x = Avx.Multiply(Avx.Subtract(minX, rayOx), invDirX);
            var t2x = Avx.Multiply(Avx.Subtract(maxX, rayOx), invDirX);
            var tEntry = Avx.Min(t1x, t2x);
            var tExit  = Avx.Max(t1x, t2x);

            // Y 轴
            var minY = Vector256.Create(mins[1], mins[4], mins[7], mins[10], mins[13], mins[16], mins[19], mins[22]);
            var maxY = Vector256.Create(maxs[1], maxs[4], maxs[7], maxs[10], maxs[13], maxs[16], maxs[19], maxs[22]);

            var t1y = Avx.Multiply(Avx.Subtract(minY, rayOy), invDirY);
            var t2y = Avx.Multiply(Avx.Subtract(maxY, rayOy), invDirY);
            tEntry = Avx.Max(tEntry, Avx.Min(t1y, t2y));
            tExit  = Avx.Min(tExit,  Avx.Max(t1y, t2y));

            // Z 轴
            var minZ = Vector256.Create(mins[2], mins[5], mins[8], mins[11], mins[14], mins[17], mins[20], mins[23]);
            var maxZ = Vector256.Create(maxs[2], maxs[5], maxs[8], maxs[11], maxs[14], maxs[17], maxs[20], maxs[23]);

            var t1z = Avx.Multiply(Avx.Subtract(minZ, rayOz), invDirZ);
            var t2z = Avx.Multiply(Avx.Subtract(maxZ, rayOz), invDirZ);
            tEntry = Avx.Max(tEntry, Avx.Min(t1z, t2z));
            tExit  = Avx.Min(tExit,  Avx.Max(t1z, t2z));

            // 条件：tEntry <= tExit && tExit >= 0 && tEntry <= maxT
            var hit = Avx.And(
                Avx.And(
                    Avx.CompareLessThanOrEqual(tEntry, tExit),
                    Avx.CompareGreaterThanOrEqual(tExit, Vector256<float>.Zero)
                ),
                Avx.CompareLessThanOrEqual(tEntry, maxTVec)
            );

            // 8 个 bool → 1 个 byte mask
            int mask = Avx.MoveMask(hit);
            for (int j = 0; mask != 0 && j < 8; j++)
            {
                if ((mask & 1) != 0)
                    results.Add(i + j);
                mask >>= 1;
            }
        }
        return results;
    }
}
```

### 开销

```
标量遍历 16 个部件: 16 × ~20 条标量指令 ≈ 320 条
SIMD 批量 (2 批) : 2 × ~30 条 SIMD 指令 ≈ 60 条

吞吐量 ≈ 5 倍提升，且全在向量寄存器中，不碰 L1。
```

---

## 第五层：完整 Cache 布局分析

### 各数据结构的大小和 Cache 层级

```
┌─────────────────────┬──────────┬───────────────────┐
│ 数据结构            │ 大小     │ Cache 层级         │
├─────────────────────┼──────────┼───────────────────┤
│ Morton 码数组       │ 2.4 KB   │ L1 (32 KB) ✓      │
│ 部件 AABB 数组      │ 7.2 KB   │ L1 (32 KB) ✓      │
│ 部件 ID / 引用       │ 2.4 KB   │ L1 (32 KB) ✓      │
│ 跳表（活跃层 2 层）  │ ~40 KB   │ L2 (256 KB) ✓     │
│ 命中结果列表         │ ~几十 B  │ 寄存器 / L1        │
└─────────────────────┴──────────┴───────────────────┘

总计 < 60 KB → L1 + L2 全覆盖 → 零主存访问
```

### 一次完整查询的执行路径

```
C# 伪代码（一次射弹命中查询的完整流程）:

HitResult[] QueryHit(Ray ray, Ship ship)
{
    // ① 第一级：飞船整体 AABB（已有优化，假设命中）
    //    1 次 slab 检测，~20 条标量指令

    // ② 第二级：朝向判断 + 跳表定位子空间
    int quadrant = (ray.direction.Z > 0) ? FRONT_HALF : BACK_HALF;
    var (start, end) = jumpTable.GetRange(level: 1, prefix: quadrant);
    //    1 次数组索引，不存在分支预测失败（前缀是整数 key）

    // ③ 第三级：SIMD 批量 AABB 检测
    var hits = SimdRayAabb.IntersectBatch(
        ray.origin, ray.direction, maxDistance,
        ship.aabbs[start..end]   // 只测候选部件（~几十个）
    );
    //    几批 SIMD 指令，全在向量寄存器

    // ④ 如果命中数 > 0，可以递归到更深层做精确三角检测
    //    但通常 AABB 级别就足够精确

    return hits;
}
```

### 从 CPU 视角看的完整流程

```
┌─────────────────────────────────────────────────────┐
│ CPU 看到的是：                                       │
│                                                     │
│ → 一段连续的、可完美预测的指令流                     │
│ → 所有数据已预取到 L1/L2                             │
│ → 零分支预测失败（跳表查表无分支，SIMD 用 mask 替代）│
│ → 零 cache miss                                     │
│ → 零指针追逐（全是数组索引）                         │
│ → 零函数调用（全部内联）                             │
│                                                     │
│ 在硬件层面，这不是"算法执行"，是"数据在流水线中流过"。│
└─────────────────────────────────────────────────────┘
```

---

## 各层优化收益汇总

| 层级 | 优化 | 从 | 到 | 收益 |
|------|------|----|----|------|
| Morton 编码 | PDEP 指令 | ~15 条指令 | 1 条指令 | 预处理加速 15× |
| 空间定位 | 跳表 O(1) | 二分 O(log n) | 数组索引 | 消除递归开销 |
| 候选检测 | SIMD 批量 | 标量逐个 | 8 路并行 | 查询加速 ~5× |
| 内存 | 全连续数组 | 随机指针访问 | 顺序扫描 | Cache miss → 0 |
| 分支 | 跳表 + mask | branch-heavy | branch-free | 分支预测失败 → 0 |

---

## 结语

> 算法最优形态不是"最快的算法"，而是"不再有算法"。
>
> 当你的 Morton 编码是 PDEP 指令，
> 你的空间定位是查表，
> 你的碰撞检测是 SIMD，
> 你的数据全在 L1 cache 里——
>
> 这时候你已经不是在写碰撞检测系统了。
> 你是在教 CPU 用它的母语说话。

## 参考

- Intel Intrinsics Guide: `_pdep_u64`, `_pext_u64`, `_mm256_min_ps`, `_mm256_cmp_ps`
- .NET `System.Runtime.Intrinsics.X86.Bmi2`, `Avx`
- [Bits'n'Bobs 系列: Morton 码与空间填充曲线](https://www.forceflow.be/)
- Amanatides, J. & Woo, A. (1987). *A Fast Voxel Traversal Algorithm for Ray Tracing*
