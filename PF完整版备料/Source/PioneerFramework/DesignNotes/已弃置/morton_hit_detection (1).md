# Morton 码射线命中检测

## 问题

三维空间中，一艘飞船由数百个部件组成。一颗射弹沿射线方向飞行，需要找出所有被击中的部件，且性能开销要小。

## 核心思路

把空间填充曲线（Z-order curve）用作**隐式八叉树**——将三维坐标通过比特交错映射为一维 Morton 码，排序后即隐含了空间层级关系。用排好序的数组替代显式树结构。

```
  坐标:  X = (x₃x₂x₁x₀)₂
        Y = (y₃y₂y₁y₀)₂
        Z = (z₃z₂z₁z₀)₂

 Morton  = x₃y₃z₃ x₂y₂z₂ x₁y₁z₁ x₀y₀z₀       （比特交错）

         高位 → 粗粒度空间划分（八叉树上层）
         低位 → 细粒度空间划分（八叉树叶层）
```

## 与其他方案的对比

| | 暴力遍历 | BVH 树 | Morton 数组 |
|---|---|---|---|
| 预处理 | 无 | 建树 O(n log n) | 算码 O(n) + 排序 O(n log n) |
| 内存 | 0 额外 | ~40 bytes/节点 | 16 bytes/元素 |
| Cache 友好度 | ★★★ | ★★（指针跳跃） | ★★★★★（连续数组） |
| 查询复杂度 | O(n) | 典型 O(log n) | O(log n + k) |
| 实现复杂度 | 最低 | 中 | 中低 |
| 穿透查询 | 天然支持 | 需特殊处理 | 天然支持 |

## 算法流程

```
船造好时（离线，只做一次）
┌──────────────────────────────────────┐
│ 1. 为每个部件算包围盒中心的 Morton 码  │
│ 2. 按 Morton 码排序，存入数组          │
│ 3. 完毕——排序后的数组就是"树"          │
└──────────────────────────────────────┘

射弹命中时（在线，每帧可能多次）
┌──────────────────────────────────────┐
│ 1. 先测是否命中飞船外层 AABB          │
│ 2. 用 DDA 算法沿射线"走格子"          │
│ 3. 每到一个格子，二分查找该格内的部件   │
│ 4. 对格子内的部件做精确 AABB-射线检测   │
│ 5. 去重，收集所有命中的部件            │
└──────────────────────────────────────┘
```

## 代码实现

### Morton 码编码

```python
def morton3d_encode(x: int, y: int, z: int, bits: int = 21) -> int:
    """将 3D 坐标编码为 Morton 码，每维使用 bits 位"""
    def spread(v: int) -> int:
        v = (v | (v << 32)) & 0xFFFF00000000FFFF  # 注意：以下按 64 位处理
        v = (v | (v << 16)) & 0x00FF0000FF0000FF
        v = (v | (v << 8))  & 0xF00F00F00F00F00F
        v = (v | (v << 4))  & 0x30C30C30C30C30C3
        v = (v | (v << 2))  & 0x1249249249249249
        return v
    return spread(x) | (spread(y) << 1) | (spread(z) << 2)
```

### Morton 码解码

```python
def morton3d_decode(code: int) -> tuple[int, int, int]:
    """将 Morton 码解码回 3D 坐标"""
    def compact(v: int) -> int:
        v = v & 0x1249249249249249
        v = (v | (v >> 2))  & 0x30C30C30C30C30C3
        v = (v | (v >> 4))  & 0xF00F00F00F00F00F
        v = (v | (v >> 8))  & 0x00FF0000FF0000FF
        v = (v | (v >> 16)) & 0xFFFF00000000FFFF
        v = (v | (v >> 32)) & 0xFFFFFFFFFFFFFFFF
        return v
    x = compact(code)
    y = compact(code >> 1)
    z = compact(code >> 2)
    return x, y, z
```

### 预处理（飞船造好时执行一次）

```python
def build_morton_index(parts: list, ship_aabb: AABB, cell_size: float) -> list:
    """
    parts: 部件列表，每个部件有 AABB 和 ID
    ship_aabb: 飞船整体包围盒
    cell_size: 网格粒度（越小越精细，通常取最小部件尺寸的 1/2 ~ 1 倍）
    """
    min_corner = ship_aabb.min
    
    entries = []
    for part in parts:
        cx, cy, cz = part.aabb.center
        # 相对于飞船原点的格子坐标
        ix = int((cx - min_corner.x) / cell_size)
        iy = int((cy - min_corner.y) / cell_size)
        iz = int((cz - min_corner.z) / cell_size)
        
        code = morton3d_encode(ix, iy, iz)
        entries.append((code, part))

    entries.sort(key=lambda x: x[0])
    return entries
```

### DDA 沿射线走格子

```python
def dda_walk(ray_origin, ray_dir, t_start: float, t_end: float,
             ship_aabb: AABB, cell_size: float):
    """
    三维 DDA（Digital Differential Analyzer），沿射线逐个格子步进。
    参考：Amanatides & Woo, 1987
    """
    min_corner = ship_aabb.min
    max_corner = ship_aabb.max
    
    # 当前格子坐标
    pos = ray_origin + ray_dir * t_start
    x = int((pos.x - min_corner.x) / cell_size)
    y = int((pos.y - min_corner.y) / cell_size)
    z = int((pos.z - min_corner.z) / cell_size)
    
    # 步进方向
    step_x = 1 if ray_dir.x > 0 else -1
    step_y = 1 if ray_dir.y > 0 else -1
    step_z = 1 if ray_dir.z > 0 else -1
    
    # 到下一个格子边界的 t 增量
    t_delta_x = abs(cell_size / ray_dir.x) if ray_dir.x != 0 else float('inf')
    t_delta_y = abs(cell_size / ray_dir.y) if ray_dir.y != 0 else float('inf')
    t_delta_z = abs(cell_size / ray_dir.z) if ray_dir.z != 0 else float('inf')
    
    # 到第一个格子边界的 t
    next_boundary_x = (min_corner.x + (x + (1 if step_x > 0 else 0)) * cell_size)
    next_boundary_y = (min_corner.y + (y + (1 if step_y > 0 else 0)) * cell_size)
    next_boundary_z = (min_corner.z + (z + (1 if step_z > 0 else 0)) * cell_size)
    
    t_max_x = (next_boundary_x - pos.x) / ray_dir.x if ray_dir.x != 0 else float('inf')
    t_max_y = (next_boundary_y - pos.y) / ray_dir.y if ray_dir.y != 0 else float('inf')
    t_max_z = (next_boundary_z - pos.z) / ray_dir.z if ray_dir.z != 0 else float('inf')
    
    current_t = t_start
    visited = set()
    
    while current_t < t_end:
        code = morton3d_encode(x, y, z)
        if code not in visited:
            visited.add(code)
            yield code, x, y, z
        
        # 走到最近的下一格
        if t_max_x < t_max_y:
            if t_max_x < t_max_z:
                current_t = t_max_x
                t_max_x += t_delta_x
                x += step_x
            else:
                current_t = t_max_z
                t_max_z += t_delta_z
                z += step_z
        else:
            if t_max_y < t_max_z:
                current_t = t_max_y
                t_max_y += t_delta_y
                y += step_y
            else:
                current_t = t_max_z
                t_max_z += t_delta_z
                z += step_z
```

### 射线查询（在线，每次射弹命中时调用）

```python
def query_ray_hit(ray_origin, ray_dir, max_distance: float,
                  morton_index: list, ship_aabb: AABB, cell_size: float) -> list:
    """
    找出所有被射线击中的部件。
    返回 [(part_id, hit_point, hit_normal), ...] 已按命中距离排序。
    """
    results = {}
    
    # 射线与飞船整体 AABB 求交（第一级过滤，假设已命中）
    t_entry, t_exit = intersect_ray_aabb(ray_origin, ray_dir, ship_aabb)
    if t_entry is None:
        return []
    
    t_exit = min(t_exit, max_distance)
    
    # DDA 走格子
    for code, gx, gy, gz in dda_walk(ray_origin, ray_dir, t_entry, t_exit,
                                       ship_aabb, cell_size):
        # 二分查找该 Morton 码区间
        import bisect
        lo = bisect.bisect_left(morton_index, code, key=lambda e: e[0])
        hi = bisect.bisect_right(morton_index, code, key=lambda e: e[0])
        
        # 测试该格子内所有部件
        for _, part in morton_index[lo:hi]:
            if part.id not in results:
                hit = intersect_ray_aabb(ray_origin, ray_dir, part.aabb)
                if hit is not None:
                    results[part.id] = (part.id, hit.point, hit.normal)
    
    # 按命中距离排序（离射线源最近的在前）
    return sorted(results.values(), key=lambda r: (r[1] - ray_origin).magnitude())
```

### AABB-射线求交（Slab 方法）

```python
def intersect_ray_aabb(ray_origin, ray_dir, aabb) -> tuple | None:
    """
    slab 法求射线与 AABB 交点。
    返回 (t_entry, t_exit)，无交则返回 None。
    """
    inv_dir = 1.0 / ray_dir.x, 1.0 / ray_dir.y, 1.0 / ray_dir.z
    
    t1 = (aabb.min.x - ray_origin.x) * inv_dir[0]
    t2 = (aabb.max.x - ray_origin.x) * inv_dir[0]
    t_entry = min(t1, t2)
    t_exit = max(t1, t2)
    
    for i in range(1, 3):
        t1 = (aabb.min[i] - ray_origin[i]) * inv_dir[i]
        t2 = (aabb.max[i] - ray_origin[i]) * inv_dir[i]
        t_entry = max(t_entry, min(t1, t2))
        t_exit = min(t_exit, max(t1, t2))
    
    if t_entry <= t_exit and t_exit >= 0:
        return t_entry, t_exit
    return None
```

## 参数调优

### 格子粒度 cell_size

| cell_size | 效果 |
|-----------|------|
| 太大 | 每个格子部件过多，退化到类似暴力遍历 |
| 太小 | 每个部件跨多个格子，DDA 走的步数增多 |
| 推荐值 | 取所有部件 AABB 最长边中位数的 1 倍 ~ 2 倍 |

### 部件跨格处理

如果部件跨多个格子，可以在预处理时将它注册到所有覆盖的格子：

```python
def build_morton_index_full(parts: list, ship_aabb: AABB, cell_size: float) -> list:
    entries = []
    for part in parts:
        # 找出部件 AABB 覆盖的所有格子
        ix_min = int((part.aabb.min.x - ship_aabb.min.x) / cell_size)
        ix_max = int((part.aabb.max.x - ship_aabb.min.x) / cell_size)
        iy_min = int((part.aabb.min.y - ship_aabb.min.y) / cell_size)
        iy_max = int((part.aabb.max.y - ship_aabb.min.y) / cell_size)
        iz_min = int((part.aabb.min.z - ship_aabb.min.z) / cell_size)
        iz_max = int((part.aabb.max.z - ship_aabb.min.z) / cell_size)
        
        for ix in range(ix_min, ix_max + 1):
            for iy in range(iy_min, iy_max + 1):
                for iz in range(iz_min, iz_max + 1):
                    code = morton3d_encode(ix, iy, iz)
                    entries.append((code, part))
    
    entries.sort(key=lambda x: x[0])
    return entries
```

这样每个格子都有完整的部件列表，查询时不需要担心跨格遗漏。代价是增加了一些重复引用（数百个部件量级可以忽略）。

## 退化方案：从跳表到暴力，按数据规模自适应

跳表（O(1) 定位任意层级的子空间区间）在小数据量下是奢侈的。一份设计良好的系统应该根据部件数量和分布，**动态选择最合适的方案**。

### 方案总览

```
        ┌─────────────────────────────────────────────┐
        │          一条射线命中飞船 AABB                 │
        └────────────────────┬────────────────────────┘
                             │
                    ┌────────┴────────┐
                    │  数据规模判断    │
                    └────────┬────────┘
            ┌────────┬───────┼───────┬──────────┐
            ▼        ▼       ▼       ▼          ▼
        纯暴力   排序+二分  稠密跳表  多级页表    完美哈希
        < 50    < 1000   1K~10K   10K~100K  极端稀疏
```

### 方案 0：纯暴力遍历（最简退化）

当部件数量少于阈值（如 50），跳过所有索引结构，直接 O(n) 遍历。

```csharp
// 适合：部件极少时，建索引的开销 > 直接遍历
List<HitResult> BruteForce(Ray ray, Part[] parts)
{
    var results = new List<HitResult>();
    foreach (var part in parts)
    {
        var hit = IntersectRayAabb(ray, part.AABB);
        if (hit.HasValue)
            results.Add(new HitResult(part.Id, hit.Value.Point, hit.Value.Normal));
    }
    return results;
}
```

### 方案 1：排序数组 + 二分（无额外内存）

不建任何辅助结构，直接对 Morton 数组做两次二分。

```csharp
// 内存：0 额外开销
// 查询：2 × O(log n) 次比较
// 适合：< 1000 部件，不想浪费任何内存

(ulong code, int partIndex)[] mortonArray; // 唯一的数据结构

(int start, int end) FindRange(int level, int prefix)
{
    int shift = 63 - level * 3;
    ulong minKey = (ulong)prefix << shift;
    ulong maxKey = (ulong)(prefix + 1) << shift;

    // 两次二分，找前缀区间
    int lo = BinarySearchLowerBound(minKey);
    int hi = BinarySearchLowerBound(maxKey);
    return (lo, hi);
}
```

优点：只维护一份排好序的数组，零冗余。对 300 个部件来说，两次二分 = 约 18 次比较，全在 L1 cache 里，实际速度不输跳表。

### 方案 2：分层混合（性价比最高）

前几层（空间划分的粗糙层）用稠密跳表，底层退回二分。因为前缀空间在深层急剧膨胀（第 N 层有 2^(3N) 个可能前缀），但实际被占用的极少。

```
第 1 层: 2^3  =     8 前缀 → 稠密跳表 (64 bytes)
第 2 层: 2^6  =    64 前缀 → 稠密跳表 (512 bytes)
第 3 层: 2^9  =   512 前缀 → 稠密跳表 (4 KB)
第 4+ 层: 2^12+ = 4096+ 前缀 → 二分查找（大部分为空）
────────────────────────────────────────
总计：~5 KB 跳表 + 0 额外 = 5 KB
```

```csharp
public class HybridIndex
{
    private int[][] startTables; // 前 N 层的稠密跳表
    private int[][] endTables;
    private int jumpTableMaxLevel; // 跳表覆盖的最高层
    private (ulong code, int partIdx)[] mortonArray; // 底层用二分

    public (int start, int end) FindRange(int level, int prefix)
    {
        if (level <= jumpTableMaxLevel)
        {
            // 前 N 层：O(1) 跳表
            return (startTables[level][prefix], endTables[level][prefix]);
        }
        else
        {
            // 深层：O(log n) 二分
            int shift = 63 - level * 3;
            ulong minKey = (ulong)prefix << shift;
            ulong maxKey = (ulong)(prefix + 1) << shift;
            return (BinarySearchLowerBound(minKey),
                    BinarySearchLowerBound(maxKey));
        }
    }
}
```

这是最实用的折中方案：顶部几层（4KB 跳表）就足以把 32K 个可能前缀砍到几百个实际命中的格子，剩下的二分代价微乎其微。

### 方案 3：多级页表（OS 虚拟内存的思路）

借鉴操作系统页表（PGD → PMD → PTE）的设计，每层只有 8 个子节点（因为每层 3 bits），不存在时就为 null，不分配内存。

```
结构：
  第0层（根）: 8 个槽位，指向第1层子表或 null
  第1层     : 每个子表 8 个槽位，指向第2层子表或 null
  ...
  第N层（叶）: 存 [start, end) 区间

查询 prefix = 0b_101_010_011:
  root[0b101]           // 第一级 3 bits
    → children[0b010]   // 第二级 3 bits
      → children[0b011] // 第三级 3 bits
        → 叶子，取 (start, end)
```

```csharp
public class PageTableEntry
{
    public PageTableEntry[] Children; // null 或 8 个子节点
    public int Start;                 // 叶子：区间起点
    public int End;                   // 叶子：区间终点
    public bool IsLeaf;
}

public class MultiLevelPageTable
{
    private PageTableEntry root;

    public (int start, int end) FindRange(ulong prefix, int level)
    {
        var node = root;
        int pathLevel = 0;

        while (node != null && pathLevel < level)
        {
            // 从 Morton 码高位每次取 3 bits
            int slot = (int)((prefix >> (63 - pathLevel * 3)) & 0b111);
            node = node.Children?[slot];
            pathLevel++;
        }

        if (node == null || !node.IsLeaf)
            return (-1, -1); // 该前缀无部件

        return (node.Start, node.End);
    }

    public void Insert(ulong code, int arrayIndex, int maxLevel)
    {
        var node = root;
        for (int lv = 0; lv < maxLevel; lv++)
        {
            int slot = (int)((code >> (63 - lv * 3)) & 0b111);
            node.Children ??= new PageTableEntry[8];
            node.Children[slot] ??= new PageTableEntry();
            node = node.Children[slot];
        }
        node.IsLeaf = true;
        node.Start = Math.Min(node.Start == 0 ? arrayIndex : node.Start, arrayIndex);
        node.End   = Math.Max(node.End, arrayIndex + 1);
    }
}
```

- 内存 = 实际被占用的节点数 × 常量
- 稀疏场景（飞船只占空间 1%）下内存降两个数量级
- 查询仍是 O(层数)，固定深度，无分支预测问题

### 方案 4：完美哈希 / 最小完美哈希

只存储实际出现的 prefix，通过哈希表映射到区间。

```csharp
// 内存 = 实际 prefix 数 × (key + value) × 负载因子
// 300 个 prefix × 16 bytes ≈ 5 KB

public class HashIndex
{
    private Dictionary<int, (int start, int end)> table;

    public void Build(ulong[] mortonCodes, int level)
    {
        int shift = 63 - level * 3;
        table = new Dictionary<int, (int, int)>();

        int i = 0;
        while (i < mortonCodes.Length)
        {
            int prefix = (int)(mortonCodes[i] >> shift);
            int start = i;
            while (i < mortonCodes.Length &&
                   (int)(mortonCodes[i] >> shift) == prefix)
                i++;
            table[prefix] = (start, i);
        }
    }

    public (int start, int end) FindRange(int prefix)
    {
        return table.TryGetValue(prefix, out var range) ? range : (-1, -1);
    }
}
```

适合：当数据量中等，但空间分布极度稀疏（飞船长条形，填充率极低）。

### 策略选择器

```csharp
public enum IndexStrategy
{
    BruteForce,         // 方案 0
    BinarySearch,       // 方案 1
    HybridJumpTable,    // 方案 2（推荐默认）
    MultiLevelPage,     // 方案 3
    PerfectHash         // 方案 4
}

public static class StrategySelector
{
    /// <summary>
    /// 根据部件数量和空间填充率自动选择最优策略。
    /// 填充率 = 被占格子数 / 该层总格子数
    /// </summary>
    public static IndexStrategy Select(int partCount, float fillRate)
    {
        // 极少部件：建索引不如直接遍历
        if (partCount < 50)
            return IndexStrategy.BruteForce;

        // 小规模：二分就够了，跳表是多余
        if (partCount < 1000)
            return IndexStrategy.BinarySearch;

        // 中等规模 + 正常填充：跳表性价比最高
        if (partCount < 10000 && fillRate > 0.01f)
            return IndexStrategy.HybridJumpTable;

        // 中大规模 + 稀疏：多级页表
        if (partCount < 100000 && fillRate < 0.01f)
            return IndexStrategy.MultiLevelPage;

        // 中等 + 极度稀疏：哈希
        if (fillRate < 0.001f)
            return IndexStrategy.PerfectHash;

        // 默认
        return IndexStrategy.HybridJumpTable;
    }
}
```

### 各方案对比

| 方案 | 额外内存 | 建造成本 | 查询 | 适用 |
|------|---------|---------|------|------|
| 纯暴力 | 0 | 0 | O(n) | < 50 部件 |
| 排序+二分 | 0 | O(n log n) | O(log n) | < 1000 部件 |
| 分层混合 | ~5 KB | O(n log n) + O(k) | O(1) → O(log k) | 1K~10K，通用 |
| 多级页表 | 按需分配 | O(n × 层数) | O(层数) | 10K~100K，稀疏 |
| 完美哈希 | ~5 KB~ | O(n) | O(1) | 极度稀疏分布 |

### 运行时动态切换

系统可以在飞船设计完成时评估数据特征，自动选择策略；也可以在运行时根据性能计数器降级/升级：

```csharp
public class AdaptiveMortonIndex
{
    private IndexStrategy currentStrategy;
    private IMortonIndex activeIndex;

    public void Build(Part[] parts, AABB shipAabb, float cellSize)
    {
        var mortonArray = MortonEncoder.BuildSortedArray(parts, shipAabb, cellSize);
        float fillRate = ComputeFillRate(mortonArray);
        currentStrategy = StrategySelector.Select(parts.Length, fillRate);

        activeIndex = currentStrategy switch
        {
            IndexStrategy.BruteForce      => new BruteForceIndex(parts),
            IndexStrategy.BinarySearch    => new BinarySearchIndex(mortonArray),
            IndexStrategy.HybridJumpTable => new HybridJumpTableIndex(mortonArray, maxJumpLevel: 3),
            IndexStrategy.MultiLevelPage  => new MultiLevelPageIndex(mortonArray),
            IndexStrategy.PerfectHash     => new PerfectHashIndex(mortonArray, level: 3),
            _ => new HybridJumpTableIndex(mortonArray, maxJumpLevel: 3)
        };
    }
}
```

## 算法美学

1. **降维**：用空间填充曲线把三维空间问题坍缩为一维排序数组上的连续区间查找
2. **树隐含在排序中**：不需要显式建树，排序后树的层级自然蕴含在 Morton 码的位模式里
3. **Cache 友好**：全部是连续数组，没有指针跳跃，CPU 预取收益最大化
4. **代码量小**：核心逻辑远少于传统 BVH/八叉树的实现

## 参考

- Morton, G. M. (1966). *A computer Oriented Geodetic Data Base and a New Technique in File Sequencing*
- Amanatides, J. & Woo, A. (1987). *A Fast Voxel Traversal Algorithm for Ray Tracing*
- Laine, S. & Karras, T. (2011). *Efficient Sparse Voxel Octrees*
