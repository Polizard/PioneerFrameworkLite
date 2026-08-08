# 多棋子互射——数据结构优化

> 承接《Morton 码射线命中检测》的核心思想：**把空间问题坍缩为一维排序数组上的区间查询**。
> 本文覆盖四个"过度工程"方向，目标是把 Morton 哲学从 Entity 内部推向世界空间全局。

---

## 现状回顾

当前多棋子互射涉及三条主路径：

```
每 2 tick：

1. 射弹移动 → CheckProjectileCollision()
   └─ GetShipsNearProjectile()   ← 细网格 Slab 走格，上限 3 步
   └─ CheckSegmentGroupHit()     ← Entity AABB → QueryRayHit (Morton DDA) → Part 命中

2. 船-船碰撞 → CheckShipCollisions()
   └─ EnumerateShipPairs()       ← shipCoveredCells 字典，已经高效
   └─ 爆炸 → ApplyExplosionDamage()/ApplyExplosionImpulse()
      └─ 全量遍历所有 Group × Entity × Part   ← O(全局 Part 总数)

3. 武器更新 → UpdateWeapons()
   └─ 每 tick 重建全量武器字典
```

已有优化（不在本文范围）：
- CombatGrid 细网格（20km/格）多格注册船
- SpaceEntity.QueryRayHit 的 Morton 索引 + DDA 走格子
- 船-船碰撞的 shipCoveredCells 去重

---

## 优化一：CombatGrid Morton 化

### 问题

[CombatGrid.cs](file:///d:/Games/PF/PioneerFramework-master/Source/PioneerFramework/SpatialIndexing/CombatGrid.cs) 的细网格用 `Dictionary<(int cx, int cy, int cz), FineCell>` 做键，每次访问要算 `(cx, cy, cz)` 的 ValueTuple 哈希码。同类问题也存在于 [SpatialGrid3D.cs](file:///d:/Games/PF/PioneerFramework-master/Source/PioneerFramework/SpatialIndexing/SpatialGrid3D.cs) 的粗网格。

```csharp
// 当前：三元组 → 装箱 + 哈希
private Dictionary<(int cx, int cy, int cz), FineCell> cells = new();
```

### 方案

项目已有 [Morton3D.Encode](file:///d:/Games/PF/PioneerFramework-master/Source/PioneerFramework/SpatialIndexing/Morton3D.cs#L18) 实现。把格子坐标编码为 `ulong`，省掉 ValueTuple 的装箱和哈希计算：

```csharp
// 优化后：三元组 → ulong，天然适配 Dictionary<ulong, ...>
private Dictionary<ulong, FineCell> cells = new();

private static ulong CellToKey(int cx, int cy, int cz)
{
    // offset 使负坐标可编码（Morton3D.Encode 要求非负）
    const int OFFSET = 1 << 20;  // 1M，覆盖 ±1M 格范围
    return Morton3D.Encode(cx + OFFSET, cy + OFFSET, cz + OFFSET);
}

private static (int cx, int cy, int cz) KeyToCell(ulong key)
{
    var (x, y, z) = Morton3D.Decode(key);
    const int OFFSET = 1 << 20;
    return (x - OFFSET, y - OFFSET, z - OFFSET);
}
```

涉及改动的接口：

| 方法 | 当前 | 改为 |
|------|------|------|
| `SystemGrid.cells` | `Dictionary<(int,int,int), FineCell>` | `Dictionary<ulong, FineCell>` |
| `WorldToFineCell()` | 返回 `(int,int,int)` | 返回 `ulong` 或保留元组 + 新增 `WorldToFineCellKey()` |
| `GetNeighborCell()` | 接收/返回元组 | 改为位运算 |
| `EmptyQueue` | `Queue<(int,int,int)>` | `Queue<ulong>` |
| `QueuedEmpties` | `HashSet<(int,int,int)>` | `HashSet<ulong>` |

**同理改 `SpatialGrid3D<T>`**：将 `Dictionary<(int cx, int cy, int cz), RoughCell>` 改为 `Dictionary<ulong, RoughCell>`。

### 收益

- 每次字典访问省一次 `ValueTuple` 装箱和 `GetHashCode` 三个 int 的组合哈希
- 更"Morton 美学"：三维坐标坍缩为一个整数，字典键就是 Morton 码
- 改动纯机械替换，不改变逻辑

---

## 优化二：射弹完整 DDA 走格

### 问题

[GetShipsNearProjectile](file:///d:/Games/PF/PioneerFramework-master/Source/PioneerFramework/SpatialIndexing/CombatGrid.cs#L373-L417) 硬编码了 `maxSteps = 3`：

```csharp
int maxSteps = 3;
while (maxSteps-- > 0)
{
    // 只走 3 步就停
}
```

细网格一格 20km，dt ≈ 1/30s。极端情况：射弹速度 30000 m/s，一帧位移 ≈ 1000m，本不该跨格。但若将来调小格子或开时间加速，射弹可能跨过 ≥4 格时，路径末端的船会被漏检（隧穿）。

### 方案

去掉步数上限，用完整的 DDA 走格，直到到达终点格。逻辑与 [QueryRayHit 的 DDA](file:///d:/Games/PF/PioneerFramework-master/Source/PioneerFramework/Base/Core/SpaceEntity.cs#L383-L475) 完全同构：

```csharp
public void GetShipsNearProjectile(
    int systemBodyId,
    ulong startCellKey,       // Morton 化后
    DVector3 startPos, DVector3 endPos,
    out ulong finalCellKey,
    HashSet<SpaceEntityGroup> results)
{
    results.Clear();

    if (!systemGrids.TryGetValue(systemBodyId, out var grid))
    {
        finalCellKey = startCellKey;
        return;
    }

    ulong currentKey = startCellKey;
    var endCoord = WorldToFineCellKey(endPos);  // 直接返回 ulong

    // 收集起点格的船
    CollectShipsInCell(grid, currentKey, results);

    if (currentKey == endCoord)
    {
        finalCellKey = currentKey;
        return;
    }

    // DDA 初始化
    DVector3 dir = endPos - startPos;
    double totalLength = dir.magnitude;
    if (totalLength < 1e-9)
    {
        finalCellKey = currentKey;
        return;
    }

    var (cx, cy, cz) = KeyToCell(currentKey);

    int stepX = dir.x > 0 ? 1 : -1;
    int stepY = dir.y > 0 ? 1 : -1;
    int stepZ = dir.z > 0 ? 1 : -1;

    double tDeltaX = dir.x != 0 ? Math.Abs(FINE_CELL_SIZE / dir.x) : double.PositiveInfinity;
    double tDeltaY = dir.y != 0 ? Math.Abs(FINE_CELL_SIZE / dir.y) : double.PositiveInfinity;
    double tDeltaZ = dir.z != 0 ? Math.Abs(FINE_CELL_SIZE / dir.z) : double.PositiveInfinity;

    double cellMinX = cx * FINE_CELL_SIZE, cellMinY = cy * FINE_CELL_SIZE, cellMinZ = cz * FINE_CELL_SIZE;
    double nextBoundaryX = cellMinX + (stepX > 0 ? FINE_CELL_SIZE : 0);
    double nextBoundaryY = cellMinY + (stepY > 0 ? FINE_CELL_SIZE : 0);
    double nextBoundaryZ = cellMinZ + (stepZ > 0 ? FINE_CELL_SIZE : 0);

    double tMaxX = dir.x != 0 ? (nextBoundaryX - startPos.x) / dir.x : double.PositiveInfinity;
    double tMaxY = dir.y != 0 ? (nextBoundaryY - startPos.y) / dir.y : double.PositiveInfinity;
    double tMaxZ = dir.z != 0 ? (nextBoundaryZ - startPos.z) / dir.z : double.PositiveInfinity;

    double currentT = 0;

    while (currentT < totalLength)
    {
        // 步进到下一格
        if (tMaxX < tMaxY)
        {
            if (tMaxX < tMaxZ) { currentT = tMaxX; tMaxX += tDeltaX; cx += stepX; }
            else               { currentT = tMaxZ; tMaxZ += tDeltaZ; cz += stepZ; }
        }
        else
        {
            if (tMaxY < tMaxZ) { currentT = tMaxY; tMaxY += tDeltaY; cy += stepY; }
            else               { currentT = tMaxZ; tMaxZ += tDeltaZ; cz += stepZ; }
        }

        if (currentT >= totalLength) break;

        currentKey = CellToKey(cx, cy, cz);
        CollectShipsInCell(grid, currentKey, results);

        if (currentKey == endCoord) break;
    }

    finalCellKey = CellToKey(cx, cy, cz);
}
```

### 收益

- 消除硬编码上限，无论格子多小/射弹多快都不隧穿
- 与 QueryRayHit 的 DDA 实现统一风格
- 正常情况（不跨格）性能不变，极端情况才多走几步

---

## 优化三：全局 Part Morton 索引（爆炸查询）

### 问题

[ApplyExplosionDamage](file:///d:/Games/PF/PioneerFramework-master/Source/PioneerFramework/Combat/CombatManager.cs#L565-L606) 和 [ApplyExplosionImpulse](file:///d:/Games/PF/PioneerFramework-master/Source/PioneerFramework/Combat/CombatManager.cs#L609-L632) 在船-船碰撞爆炸时，遍历所有 Group → Entity → Part：

```csharp
foreach (var group in spaceGame.GetAllGroups())       // N 个编队
    foreach (int eid in group.GetEntityIds())          // M 个实体
        foreach (var part in entity.Parts)             // K 个部件
            if (distance <= explosionRadius)
                ApplyDamage(part);
```

复杂度 O(全局 Part 总数)，每次爆炸都扫一遍。当多艘船密集混战、频繁碰撞爆炸时，这是不可忽视的开销。

### 方案

构建**世界空间全局 Part Morton 索引**。这是 `SpaceEntity.QueryRayHit` 中已有 Morton 索引的"世界尺度版"——不是索引一艘船内部的 Part，而是索引所有船的所有 Part。

```
                     ┌─────────────────────────────────────┐
                     │        全局 Part Morton 索引         │
                     │  (ulong code, int partGlobalId)[]    │
                     │         按 code 排序                  │
                     └──────────────┬──────────────────────┘
                                    │
              ┌─────────────────────┼─────────────────────┐
              │                     │                     │
        Entity A 的 Parts      Entity B 的 Parts     Entity C 的 Parts
        (局部 Morton 索引)     (局部 Morton 索引)    (局部 Morton 索引)
```

#### 数据结构

```csharp
/// <summary>
/// 全局 Part Morton 条目。按世界空间 Morton 码排序。
/// </summary>
public struct GlobalPartEntry : IComparable<GlobalPartEntry>
{
    public ulong code;           // 世界坐标 Morton 码
    public int entityId;         // 所属 Entity
    public int partIndex;        // entity.Parts 中的索引
    public int groupId;          // 所属 Group（快速过滤友军）
    public DVector3 worldPos;    // Part 在世界空间中的中心位置（缓存，省去查询时重算）

    public int CompareTo(GlobalPartEntry other) => code.CompareTo(other.code);
}
```

#### 构建（飞船部署/部件变更时重建）

```csharp
public void BuildGlobalPartIndex(SpaceGameComp spaceGame, double cellSize)
{
    var entries = new List<GlobalPartEntry>();

    foreach (var group in spaceGame.GetAllGroups())
    {
        if (!group.IsActive || !group.IsCollidable) continue;
        DVector3 groupPos = GetGroupPos(group);

        foreach (int eid in group.GetEntityIds())
        {
            var entity = spaceGame.GetEntityById(eid);
            if (entity == null) continue;

            for (int i = 0; i < entity.Parts.Count; i++)
            {
                var part = entity.Parts[i];
                if (part.isDestroyed) continue;

                DVector3 worldPos = groupPos + entity.Position + part.localPosition;
                int ix = (int)((worldPos.x - globalOrigin.x) / cellSize);
                int iy = (int)((worldPos.y - globalOrigin.y) / cellSize);
                int iz = (int)((worldPos.z - globalOrigin.z) / cellSize);

                entries.Add(new GlobalPartEntry
                {
                    code = Morton3D.Encode(ix, iy, iz),
                    entityId = eid,
                    partIndex = i,
                    groupId = group.GroupId,
                    worldPos = worldPos
                });
            }
        }
    }

    entries.Sort();
    globalPartIndex = entries.ToArray();
}
```

#### 爆炸查询

```csharp
/// <summary>
/// 使用全局 Morton 索引查询爆炸范围内的所有 Part。
/// O(log N + K)，N = 全局 Part 总数，K = 范围内的 Part 数。
/// </summary>
public IEnumerable<(SpacePart part, double distance)> QueryPartsInRadius(
    DVector3 center, double radius,
    SpaceGameComp spaceGame)
{
    double cellSize = globalCellSize;
    int radCells = (int)Math.Ceiling(radius / cellSize);

    int cx = (int)((center.x - globalOrigin.x) / cellSize);
    int cy = (int)((center.y - globalOrigin.y) / cellSize);
    int cz = (int)((center.z - globalOrigin.z) / cellSize);

    double radiusSq = radius * radius;
    var seen = new HashSet<(int entityId, int partIndex)>();  // 去重

    for (int dx = -radCells; dx <= radCells; dx++)
        for (int dy = -radCells; dy <= radCells; dy++)
            for (int dz = -radCells; dz <= radCells; dz++)
            {
                ulong code = Morton3D.Encode(cx + dx, cy + dy, cz + dz);

                // 二分定位区间
                int lo = BinarySearchLowerBound(globalPartIndex, code);
                int hi = BinarySearchUpperBound(globalPartIndex, code);

                for (int i = lo; i < hi; i++)
                {
                    var entry = globalPartIndex[i];
                    var key = (entry.entityId, entry.partIndex);
                    if (!seen.Add(key)) continue;

                    double distSq = (entry.worldPos - center).sqrMagnitude;
                    if (distSq <= radiusSq)
                    {
                        var entity = spaceGame.GetEntityById(entry.entityId);
                        if (entity != null && entry.partIndex < entity.Parts.Count)
                        {
                            var part = entity.Parts[entry.partIndex];
                            if (!part.isDestroyed)
                                yield return (part, Math.Sqrt(distSq));
                        }
                    }
                }
            }
}
```

### 收益

| | 当前 | 优化后 |
|------|------|------|
| 爆炸损伤查询 | O(全局 Part 总数) | O(log N + 范围内 Part 数) |
| 爆炸冲量查询 | O(全局 Group 数) | 可复用相同空间拆分 |
| 适用场景 | 几艘船 | 几十艘船混战 |

### 注意事项

- **跨格处理**：Part 的 AABB 可能跨多个 Morton 格子。当前方案用 Part 中心点注册，可能导致边缘 Part 漏检。解决方案：把 Part 注册到其 AABB 覆盖的所有格子（类似文档中 `build_morton_index_full`），代价是增加重复引用。
- **索引失效**：Part 被摧毁时需惰性标记（或从数组中移除）。建议用惰性标记 + 定期重建，类似当前 [MarkPartDestroyed](file:///d:/Games/PF/PioneerFramework-master/Source/PioneerFramework/Base/Core/SpaceEntity.cs#L189-L206) 的做法。
- **坐标系**：不同参考系（不同行星）的 Part 不能混在同一个 Morton 索引中——Morton 码只在同一个欧氏空间中有意义。需按参考系分索引（每个 `systemBodyId` 一份）。

---

## 优化四：QueryRayHit 跳表加速

### 问题

[SpaceEntity.QueryRayHit](file:///d:/Games/PF/PioneerFramework-master/Source/PioneerFramework/Base/Core/SpaceEntity.cs#L416-L418) 每个 DDA 步进的格子做两次二分：

```csharp
int lo = BinarySearchMorton(index, code, false);
int hi = BinarySearchMorton(index, code, true);
```

当 Entity 有几百个 Part 时，每格两次 O(log n) 二分。如果 DDA 走过 10 个格子就是 20 次二分。

### 方案

复现[Morton 文档中的方案 2：分层混合跳表](file:///d:/Games/PF/PioneerFramework-master/Source/PioneerFramework/morton_hit_detection.md#L348-L387)。预计算 Morton 码前 N 层（每层 3 bits）的稠密跳表，深层退回二分：

```
第 1 层: 2^3  =     8 前缀 → 稠密跳表 (64 bytes)
第 2 层: 2^6  =    64 前缀 → 稠密跳表 (512 bytes)
第 3 层: 2^9  =   512 前缀 → 稠密跳表 (4 KB)
第 4+ 层: 2^12+ = 4096+ 前缀 → 二分查找
────────────────────────────────────────
总计：~5 KB 跳表，前 3 层 O(1) 定位
```

### 在 SpaceEntity 中的实现

```csharp
public class MortonJumpTable
{
    private readonly int[][] startTables;  // [level][prefix] = start index
    private readonly int[][] endTables;    // [level][prefix] = end index
    private readonly int maxJumpLevel;

    public MortonJumpTable(MortonEntry[] sortedEntries, int maxLevel = 3)
    {
        maxJumpLevel = maxLevel;
        startTables = new int[maxLevel + 1][];
        endTables = new int[maxLevel + 1][];

        for (int level = 0; level <= maxLevel; level++)
        {
            int numPrefixes = 1 << (level * 3);
            startTables[level] = new int[numPrefixes];
            endTables[level] = new int[numPrefixes];

            // 初始化：全部指向无效区间
            for (int p = 0; p < numPrefixes; p++)
            {
                startTables[level][p] = -1;
                endTables[level][p] = -1;
            }

            int shift = 63 - level * 3;
            int i = 0;
            while (i < sortedEntries.Length)
            {
                int prefix = (int)(sortedEntries[i].code >> shift);
                int start = i;
                while (i < sortedEntries.Length &&
                       (int)(sortedEntries[i].code >> shift) == prefix)
                    i++;
                startTables[level][prefix] = start;
                endTables[level][prefix] = i;
            }
        }
    }

    /// <summary>
    /// 在跳表覆盖的层级做 O(1) 查找，否则回退二分。
    /// level: 当前查询的层级（每层 3 bits）。
    /// </summary>
    public (int start, int end) FindRange(int level, int prefix, MortonEntry[] array)
    {
        if (level <= maxJumpLevel)
        {
            int s = startTables[level][prefix];
            int e = endTables[level][prefix];
            return s >= 0 ? (s, e) : (-1, -1);
        }
        else
        {
            // 回退二分
            int shift = 63 - level * 3;
            ulong minKey = (ulong)prefix << shift;
            ulong maxKey = (ulong)(prefix + 1) << shift;
            return (BinarySearchLowerBound(array, minKey),
                    BinarySearchUpperBound(array, maxKey));
        }
    }
}
```

### 收益

| Part 数 | 当前（每格 2 次二分） | 跳表（前 3 层 O(1)） |
|---------|----------------------|---------------------|
| 50 | ~12 次比较 | O(1) 数组下标 |
| 200 | ~16 次比较 | O(1) 数组下标 |
| 500 | ~18 次比较 | O(1) 数组下标 |

### 注意事项

- 跳表的内存开销：4 层跳表最多 = 8 + 64 + 512 + 4096 = 4680 个 int → ~18 KB，可接受
- 前 3 层覆盖了绝大多数实际查询场景（Entity 的 Part 在局部空间中比较密集）
- 跳表在 `mortonDirty = true` 时与 Morton 索引一起重建

---

## 各方案总览

| 优化 | 问题 | 方案 | 复杂度变化 | 内存 | Morton 美学 |
|------|------|------|-----------|------|------------|
| CombatGrid Morton 化 | `(cx,cy,cz)` 元组哈希 | 编码为 `ulong` | 常数级 | 0 | ★★★★★ |
| 射弹完整 DDA | 3 步硬上限 → 隧穿 | 无上限 DDA 走格 | 同构 | 0 | ★★★ |
| 全局 Part Morton 索引 | 爆炸全量遍历 | 世界空间 Morton 数组 | O(n)→O(log n) | ~16B/Part | ★★★★★ |
| QueryRayHit 跳表 | 每格两次二分 | 前 3 层 O(1) 跳表 | O(log n)→O(1) | ~18KB | ★★★★ |

---

## 算法美学

1. **尺度统一**：Entity 内部的 Morton 索引（局部）→ 全局 Part Morton 索引（世界）→ 同一套哲学在不同尺度上的递归应用
2. **降维不变性**：无论是 Part 局部坐标还是世界坐标，Morton 码都把三维空间坍缩为一维排序数组
3. **Cache 友好**：所有索引都是连续数组，CPU 预取获益，不跳指针
4. **渐进退化**：每个方案都可以在小规模时退化（跳表 → 二分 → 暴力），保持你的 `StrategySelector` 设计精神
