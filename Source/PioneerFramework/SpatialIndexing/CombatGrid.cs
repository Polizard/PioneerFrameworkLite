using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework.Combat
{
    public class CombatGrid : GameComponent
    {
        public const double FINE_CELL_SIZE = 20000.0;
        public const int FINE_EMPTY_TTL = 600;
        private const int FULL_SWEEP_INTERVAL = 600;   // 全量兜底扫描周期（真实 tick，10s 一次，避免周期尖峰）
        private int lastFullSweepTick = -1;

        public class FineCell
        {
            public List<SpaceEntityGroup> ships = new List<SpaceEntityGroup>();
            public List<ProjectileInstance> projectiles = new List<ProjectileInstance>();

            public double minX, maxX;
            public double minY, maxY;
            public double minZ, maxZ;

            public int emptyTick = -1;
            public readonly int createdTick;

            public FineCell()
            {
                createdTick = Find.TickManager?.TicksGame ?? 0;
            }

            public void SetBounds(double minX, double maxX, double minY, double maxY, double minZ, double maxZ)
            {
                this.minX = minX; this.maxX = maxX;
                this.minY = minY; this.maxY = maxY;
                this.minZ = minZ; this.maxZ = maxZ;
            }

            public bool IsEmpty => ships.Count == 0 && projectiles.Count == 0;
        }

        private class SystemGrid
        {
            public Dictionary<(int cx, int cy, int cz), FineCell> cells = new Dictionary<(int cx, int cy, int cz), FineCell>();
            public Queue<(int cx, int cy, int cz)> emptyQueue = new Queue<(int cx, int cy, int cz)>();
            // 队列去重：已在队列中的空格（防止同一格"空→非空→空"重复入队导致队列膨胀）
            public HashSet<(int cx, int cy, int cz)> queuedEmpties = new HashSet<(int, int, int)>();
        }

        private readonly Dictionary<int, SystemGrid> systemGrids = new Dictionary<int, SystemGrid>();

        /// <summary>
        /// 船的世界 AABB 覆盖的细网格格盒（闭区间）。CoverCells 恒产出完整矩形盒，
        /// 故用 6 个 int 表示即可：比较 O(1)、零分配（替代原来的 List + O(k²) Contains 比较）。
        /// </summary>
        private struct CoverBox
        {
            public int minX, maxX, minY, maxY, minZ, maxZ;

            public bool Equals(CoverBox other)
            {
                return minX == other.minX && maxX == other.maxX
                    && minY == other.minY && maxY == other.maxY
                    && minZ == other.minZ && maxZ == other.maxZ;
            }

            /// <summary>格数（用于调试/统计）。</summary>
            public int CellCount => (maxX - minX + 1) * (maxY - minY + 1) * (maxZ - minZ + 1);
        }

        // 每艘船当前覆盖的细网格格盒（多格注册：AABB 覆盖 2x2x2=最多 8 格，母舰几十格）
        private readonly Dictionary<SpaceEntityGroup, CoverBox> shipCoveredCells = new Dictionary<SpaceEntityGroup, CoverBox>();

        // #4：EnumerateShipPairs 的船对去重缓冲（复用，避免每 2 tick 分配 HashSet）
        private readonly HashSet<long> pairScratch = new HashSet<long>();

#if DEBUG
        /// <summary>DEBUG：船跨格（覆盖格盒变化）累计次数，供 CombatManager 汇总日志。</summary>
        internal long debugCellCrossCount;
#endif

        // ── 组件引用缓存（2026-09-10 性能第 5 条）：本类每船同步 / 每弹每步都要查这两个组件，
        //    GetComponent 是组件表线性扫描，故按 Game 缓存（跨局自动失效，见 GameComponentRef）。──
        private readonly GameComponentRef<CelestialBodyManager> celestialMgrRef = new GameComponentRef<CelestialBodyManager>();
        private readonly GameComponentRef<SpaceGameComp> spaceGameRef = new GameComponentRef<SpaceGameComp>();

        // 引力源按 tick 缓存已随 A2 移除（射弹引力改由 CelestialBodyManager 的引力场 +
        // TaylorSegment 承载，见 CombatManager.BuildGravitySegment）。

        public CombatGrid(Game game) { }

        public static (int cx, int cy, int cz) WorldToFineCell(DVector3 posMeters)
        {
            return (
                (int)System.Math.Floor(posMeters.x / FINE_CELL_SIZE),
                (int)System.Math.Floor(posMeters.y / FINE_CELL_SIZE),
                (int)System.Math.Floor(posMeters.z / FINE_CELL_SIZE)
            );
        }

        private SystemGrid GetOrCreateSystem(int systemBodyId)
        {
            if (!systemGrids.TryGetValue(systemBodyId, out var grid))
            {
                grid = new SystemGrid();
                systemGrids[systemBodyId] = grid;
            }
            return grid;
        }

        /// <summary>
        /// 解析编队所属的根行星/恒星参考系 ID（绕卫星的船与绕行星的船统一进同一网格，
        /// 坐标基准均为行星系相对坐标，见 GetCircularPositionAtTick）。
        /// </summary>
        private int GetFrameId(SpaceEntityGroup ship) => ResolveFrameId(ship.CelestialBodyId);

        /// <summary>
        /// 参考系解析（与 AddShip / GetFrameId 同源）。外部按帧查网格（如爆炸 AoE 候选）必须走本方法：
        /// CelestialBodyManager 缺失时注册帧退回 celestialBodyId 本身，若查询侧改用
        /// SpaceGameComp.ResolveReferenceFrameId（返回 -1）就会与注册帧错位、查不到船。
        /// </summary>
        public int ResolveFrameId(int celestialBodyId)
        {
            var mgr = celestialMgrRef.Get();
            return mgr != null ? mgr.GetReferenceFrameId(celestialBodyId) : celestialBodyId;
        }

        /// <summary>
        /// 多格注册：将船加入其世界 AABB 覆盖的所有细网格格（普通船最多 8 格，巨型船更多）。
        /// 保证射弹经过船的任何部分所在格都能命中，且两船重叠必共享至少一个覆盖格。
        /// </summary>
        public void AddShip(SpaceEntityGroup ship)
        {
            var bounds = GetGroupBoundsSystem(ship);
            var box = CoverBoxOf(bounds.min, bounds.max);
            int frameId = GetFrameId(ship);
            var grid = GetOrCreateSystem(frameId);

            // 内联三重循环（闭区间）注册到每个覆盖格，零 List 分配
            for (int x = box.minX; x <= box.maxX; x++)
                for (int y = box.minY; y <= box.maxY; y++)
                    for (int z = box.minZ; z <= box.maxZ; z++)
                    {
                        var cell = (x, y, z);
                        if (!grid.cells.TryGetValue(cell, out var fCell))
                        {
                            fCell = new FineCell();
                            fCell.SetBounds(
                                x * FINE_CELL_SIZE, (x + 1) * FINE_CELL_SIZE,
                                y * FINE_CELL_SIZE, (y + 1) * FINE_CELL_SIZE,
                                z * FINE_CELL_SIZE, (z + 1) * FINE_CELL_SIZE
                            );
                            grid.cells[cell] = fCell;
                        }

                        if (!fCell.ships.Contains(ship))
                        {
                            fCell.ships.Add(ship);
                            fCell.emptyTick = -1;
                        }
                    }

            shipCoveredCells[ship] = box;
        }

        public void RemoveShip(SpaceEntityGroup ship)
        {
            if (!shipCoveredCells.TryGetValue(ship, out var box))
                return;

            var grid = GetOrCreateSystem(GetFrameId(ship));
            for (int x = box.minX; x <= box.maxX; x++)
                for (int y = box.minY; y <= box.maxY; y++)
                    for (int z = box.minZ; z <= box.maxZ; z++)
                        RemoveShipFromCell(grid, ship, (x, y, z));

            shipCoveredCells.Remove(ship);
        }

        public void UpdateShipPosition(SpaceEntityGroup ship, DVector3 oldPosMeters)
        {
            var newBounds = GetGroupBoundsSystem(ship);
            var newBox = CoverBoxOf(newBounds.min, newBounds.max);

            if (!shipCoveredCells.TryGetValue(ship, out var oldBox))
            {
                AddShip(ship);
                return;
            }

            if (oldBox.Equals(newBox)) // O(1) 盒比较，替代 O(k²) 列表逐元素比较
                return;

#if DEBUG
            debugCellCrossCount++;
#endif

            var grid = GetOrCreateSystem(GetFrameId(ship));

            // 移除：旧盒中不在新盒内的格
            for (int x = oldBox.minX; x <= oldBox.maxX; x++)
                for (int y = oldBox.minY; y <= oldBox.maxY; y++)
                    for (int z = oldBox.minZ; z <= oldBox.maxZ; z++)
                        if (x < newBox.minX || x > newBox.maxX ||
                            y < newBox.minY || y > newBox.maxY ||
                            z < newBox.minZ || z > newBox.maxZ)
                            RemoveShipFromCell(grid, ship, (x, y, z));

            // 新增：新盒中不在旧盒内的格
            for (int x = newBox.minX; x <= newBox.maxX; x++)
                for (int y = newBox.minY; y <= newBox.maxY; y++)
                    for (int z = newBox.minZ; z <= newBox.maxZ; z++)
                    {
                        if (x >= oldBox.minX && x <= oldBox.maxX &&
                            y >= oldBox.minY && y <= oldBox.maxY &&
                            z >= oldBox.minZ && z <= oldBox.maxZ)
                            continue;
                        if (!grid.cells.TryGetValue((x, y, z), out var fCell))
                        {
                            fCell = new FineCell();
                            fCell.SetBounds(
                                x * FINE_CELL_SIZE, (x + 1) * FINE_CELL_SIZE,
                                y * FINE_CELL_SIZE, (y + 1) * FINE_CELL_SIZE,
                                z * FINE_CELL_SIZE, (z + 1) * FINE_CELL_SIZE
                            );
                            grid.cells[(x, y, z)] = fCell;
                        }
                        if (!fCell.ships.Contains(ship))
                        {
                            fCell.ships.Add(ship);
                            fCell.emptyTick = -1;
                        }
                    }

            shipCoveredCells[ship] = newBox;
        }

        /// <summary>
        /// 计算编队的世界 AABB（groupPos + Σ entity 的 position ± BoundsSize/2）。
        /// 坐标基准：行星系相对坐标（与 GetGroupPosMeters 一致）。
        /// </summary>
        private (DVector3 min, DVector3 max) GetGroupBoundsSystem(SpaceEntityGroup ship)
        {
            DVector3 groupPos = GetGroupPosMeters(ship);
            DVector3 min = groupPos, max = groupPos;
            bool hasEntity = false;

            var spaceGame = spaceGameRef.Get();
            if (spaceGame != null)
            {
                foreach (int eid in ship.GetEntityIds())
                {
                    var entity = spaceGame.GetEntityById(eid);
                    if (entity == null || !entity.IsCollidable) continue;

                    // 旋转后的世界轴对齐包围盒（覆盖倾斜姿态）
                    entity.GetAabbSystem(out var relMin, out var relMax);
                    DVector3 eMin = groupPos + entity.Position + relMin;
                    DVector3 eMax = groupPos + entity.Position + relMax;

                    if (!hasEntity)
                    {
                        min = eMin;
                        max = eMax;
                        hasEntity = true;
                    }
                    else
                    {
                        min = new DVector3(
                            System.Math.Min(min.x, eMin.x),
                            System.Math.Min(min.y, eMin.y),
                            System.Math.Min(min.z, eMin.z));
                        max = new DVector3(
                            System.Math.Max(max.x, eMax.x),
                            System.Math.Max(max.y, eMax.y),
                            System.Math.Max(max.z, eMax.z));
                    }
                }
            }

            return (min, max);
        }

        /// <summary>
        /// 计算世界 AABB 覆盖的细网格格盒（闭区间，语义与旧 CoverCells 完全一致，零分配）。
        /// </summary>
        private static CoverBox CoverBoxOf(DVector3 min, DVector3 max)
        {
            return new CoverBox
            {
                minX = (int)System.Math.Floor(min.x / FINE_CELL_SIZE),
                maxX = (int)System.Math.Floor(max.x / FINE_CELL_SIZE),
                minY = (int)System.Math.Floor(min.y / FINE_CELL_SIZE),
                maxY = (int)System.Math.Floor(max.y / FINE_CELL_SIZE),
                minZ = (int)System.Math.Floor(min.z / FINE_CELL_SIZE),
                maxZ = (int)System.Math.Floor(max.z / FINE_CELL_SIZE)
            };
        }

        /// <summary>
        /// 枚举所有共享至少一个覆盖格的船对（去重）。用于同格两两碰撞检测，
        /// 由"重叠必共享覆盖格"保证完备。
        /// 只遍历与船相关的覆盖格（而非全部格子），与网格膨胀解耦：
        /// 复杂度 O(Σ 船覆盖格 × 格内船数)，不随空格数增长。
        /// </summary>
        public IEnumerable<(SpaceEntityGroup a, SpaceEntityGroup b)> EnumerateShipPairs()
        {
            var checkedPairs = pairScratch;
            checkedPairs.Clear();

            foreach (var kv in shipCoveredCells)
            {
                var shipA = kv.Key;
                // GetFrameId 每船一次（多格船从每格一次降为每船一次）
                if (!systemGrids.TryGetValue(GetFrameId(shipA), out var grid))
                    continue;

                var boxA = kv.Value;
                for (int x = boxA.minX; x <= boxA.maxX; x++)
                    for (int y = boxA.minY; y <= boxA.maxY; y++)
                        for (int z = boxA.minZ; z <= boxA.maxZ; z++)
                        {
                            if (!grid.cells.TryGetValue((x, y, z), out var fCell))
                                continue;

                            var ships = fCell.ships;
                            for (int i = 0; i < ships.Count; i++)
                            {
                                var shipB = ships[i];
                                if (ReferenceEquals(shipB, shipA)) continue;

                                int minId = shipA.GroupId < shipB.GroupId ? shipA.GroupId : shipB.GroupId;
                                int maxId = shipA.GroupId < shipB.GroupId ? shipB.GroupId : shipA.GroupId;
                                long key = ((long)minId << 32) | (uint)maxId;
                                if (checkedPairs.Add(key))
                                    yield return (shipA, shipB);
                            }
                        }
            }
        }

        private void RemoveShipFromCell(SystemGrid grid, SpaceEntityGroup ship, (int cx, int cy, int cz) cell)
        {
            if (grid.cells.TryGetValue(cell, out var fCell))
            {
                fCell.ships.Remove(ship);
                if (fCell.IsEmpty)
                    EnqueueEmpty(grid, cell, fCell);
            }
        }

        public void AddProjectile(ProjectileInstance proj)
        {
            var cell = WorldToFineCell(proj.position);
            var grid = GetOrCreateSystem(proj.systemBodyId);

            if (!grid.cells.TryGetValue(cell, out var fCell))
            {
                fCell = new FineCell();
                fCell.SetBounds(
                    cell.cx * FINE_CELL_SIZE, (cell.cx + 1) * FINE_CELL_SIZE,
                    cell.cy * FINE_CELL_SIZE, (cell.cy + 1) * FINE_CELL_SIZE,
                    cell.cz * FINE_CELL_SIZE, (cell.cz + 1) * FINE_CELL_SIZE
                );
                grid.cells[cell] = fCell;
            }

            fCell.projectiles.Add(proj);
            proj.fineCx = cell.cx;
            proj.fineCy = cell.cy;
            proj.fineCz = cell.cz;
        }

        public void RemoveProjectile(ProjectileInstance proj)
        {
            var cell = (proj.fineCx, proj.fineCy, proj.fineCz);
            if (!systemGrids.TryGetValue(proj.systemBodyId, out var grid)) return;

            if (grid.cells.TryGetValue(cell, out var fCell))
            {
                fCell.projectiles.Remove(proj);
                if (fCell.IsEmpty)
                    EnqueueEmpty(grid, cell, fCell);
            }
        }

        public void UpdateProjectileCell(ProjectileInstance proj, DVector3 oldPosMeters, DVector3 newPosMeters)
        {
            var oldCell = (proj.fineCx, proj.fineCy, proj.fineCz);
            var newCell = WorldToFineCell(newPosMeters);

            if (oldCell.Equals(newCell)) return;

            if (!systemGrids.TryGetValue(proj.systemBodyId, out var grid)) return;

            if (grid.cells.TryGetValue(oldCell, out var oldFCell))
            {
                oldFCell.projectiles.Remove(proj);
                if (oldFCell.IsEmpty)
                    EnqueueEmpty(grid, oldCell, oldFCell);
            }

            if (!grid.cells.TryGetValue(newCell, out var newFCell))
            {
                newFCell = new FineCell();
                newFCell.SetBounds(
                    newCell.cx * FINE_CELL_SIZE, (newCell.cx + 1) * FINE_CELL_SIZE,
                    newCell.cy * FINE_CELL_SIZE, (newCell.cy + 1) * FINE_CELL_SIZE,
                    newCell.cz * FINE_CELL_SIZE, (newCell.cz + 1) * FINE_CELL_SIZE
                );
                grid.cells[newCell] = newFCell;
            }

            newFCell.projectiles.Add(proj);
            proj.fineCx = newCell.cx;
            proj.fineCy = newCell.cy;
            proj.fineCz = newCell.cz;
        }

        /// <summary>
        /// 沿线段从 startCell 走格到 endCoord（含起点/终点格），把格内活跃船汇入 results。
        /// 射弹与定向能射线共用的 DDA 核心（GetShipsNearProjectile / GetShipsNearSegment 薄封装共用）。
        /// maxSteps 按线段跨格范围动态计算（+2 保险），而非固定 3：
        /// 大步进射弹（如 Fast 档 16s/步跨多格）时 DDA 必须完整遍历到终点格，
        /// 否则 finalCell ≠ 射弹实际所在格，调用方覆盖 fineC 后与格子记录错位。
        /// </summary>
        private (int cx, int cy, int cz) WalkSegmentCells(SystemGrid grid,
            (int cx, int cy, int cz) startCell, (int cx, int cy, int cz) endCoord,
            DVector3 startPos, DVector3 endPos, HashSet<SpaceEntityGroup> results)
        {
            var currentCell = startCell;
            int maxSteps = System.Math.Abs(endCoord.cx - startCell.cx)
                         + System.Math.Abs(endCoord.cy - startCell.cy)
                         + System.Math.Abs(endCoord.cz - startCell.cz) + 2;

            while (maxSteps-- > 0)
            {
                if (grid.cells.TryGetValue(currentCell, out var fCell))
                {
                    foreach (var ship in fCell.ships)
                    {
                        if (ship.IsActive)
                            results.Add(ship);
                    }
                }

                if (currentCell.Equals(endCoord))
                    break;

                // 空格（不在 cells 字典）也继续走格：cell 边界由坐标直接推导（索引 × cellSize），
                // 不再依赖 cells 字典查格，避免 DDA 走到无船格就提前中断而漏掉空格之后的船。
                GetCellBounds(currentCell, out var cellMin, out var cellMax);
                var (hitFace, _) = SlabMethodExitFace(startPos, endPos, cellMin, cellMax);
                if (hitFace == -1)
                    break;

                currentCell = GetNeighborCell(currentCell, hitFace);
            }

            return currentCell;
        }

        public (int cx, int cy, int cz) GetShipsNearProjectile(
            int systemBodyId,
            (int cx, int cy, int cz) startCell,
            DVector3 startPos, DVector3 endPos,
            out (int cx, int cy, int cz) finalCell,
            HashSet<SpaceEntityGroup> results)
        {
            results.Clear();
            finalCell = startCell;

            if (!systemGrids.TryGetValue(systemBodyId, out var grid))
                return startCell;

            finalCell = WalkSegmentCells(grid, startCell, WorldToFineCell(endPos), startPos, endPos, results);
            return finalCell;
        }

        /// <summary>
        /// 沿线段走格收集候选船（定向能射线 B 预留 / 射弹共用同一 DDA 核心）。
        /// 丢弃 GetShipsNearProjectile 的 startCell 输入与 finalCell 输出语义，射线当前不调用。
        /// </summary>
        public void GetShipsNearSegment(int systemBodyId, DVector3 startPos, DVector3 endPos,
            HashSet<SpaceEntityGroup> results)
        {
            results.Clear();
            if (!systemGrids.TryGetValue(systemBodyId, out var grid)) return;

            WalkSegmentCells(grid, WorldToFineCell(startPos), WorldToFineCell(endPos), startPos, endPos, results);
        }

        /// <summary>
        /// 收集以 center 为中心、边长 2×halfSize 的正方体覆盖的所有细网格内的船。
        /// 覆盖格数 ≈ (2·halfSize/20)³；射程 50km → 5×5×5=125 格（多数不存在，TryGetValue 跳过）。
        /// 定位能即时命中候选粗筛（方案 A）；射程球内过滤由调用方做。
        /// </summary>
        public void CollectShipsInCube(int systemBodyId, DVector3 center, double halfSize,
            HashSet<SpaceEntityGroup> results)
        {
            results.Clear();
            if (!systemGrids.TryGetValue(systemBodyId, out var grid)) return;

            var minCell = WorldToFineCell(center - new DVector3(halfSize, halfSize, halfSize));
            var maxCell = WorldToFineCell(center + new DVector3(halfSize, halfSize, halfSize));

            for (int x = minCell.cx; x <= maxCell.cx; x++)
                for (int y = minCell.cy; y <= maxCell.cy; y++)
                    for (int z = minCell.cz; z <= maxCell.cz; z++)
                    {
                        if (!grid.cells.TryGetValue((x, y, z), out var fCell)) continue;
                        var ships = fCell.ships;
                        for (int i = 0; i < ships.Count; i++)
                            results.Add(ships[i]);
                    }
        }

        /// <summary>
        /// 空格回收（快路径为主 + 低频全量兜底）：
        /// - 快路径（每 2 tick）：按队列长度 20% 斩杀 TTL 已到期的空格（队列已去重，稳定高效）；
        /// - 慢路径（每 600 tick）：全量扫描删除所有 TTL 到期的空格并重建队列，纯兜底。
        /// 队列去重（queuedEmpties）消除了"空→非空→空"重复入队导致的膨胀，
        /// 因此快路径即可自足，全量兜底降频到 600 tick 避免周期尖峰（1 万格约 0.1ms/10s）。
        /// </summary>
        public void DrainEmptyCells()
        {
            int now = Find.TickManager.TicksGame;

            foreach (var kv in systemGrids)
            {
                var grid = kv.Value;

                // 快路径：百分比斩杀（只处理队首 TTL 已到期的，保持 FIFO）
                int drainCount = System.Math.Max(1, grid.emptyQueue.Count / 5);
                for (int i = 0; i < drainCount && grid.emptyQueue.Count > 0; i++)
                {
                    var coord = grid.emptyQueue.Peek();
                    if (!grid.cells.TryGetValue(coord, out var fCell) || !fCell.IsEmpty)
                    {
                        grid.emptyQueue.Dequeue();
                        grid.queuedEmpties.Remove(coord);
                        continue;
                    }
                    if (now - fCell.emptyTick >= FINE_EMPTY_TTL)
                    {
                        grid.emptyQueue.Dequeue();
                        grid.queuedEmpties.Remove(coord);
                        grid.cells.Remove(coord);
                    }
                    else
                        break;
                }
            }

            // 慢路径：低频全量兜底
            if (now - lastFullSweepTick < FULL_SWEEP_INTERVAL) return;
            lastFullSweepTick = now;

            foreach (var kv in systemGrids)
            {
                var grid = kv.Value;
                var expired = new List<(int cx, int cy, int cz)>();
                foreach (var pair in grid.cells)
                {
                    if (pair.Value.IsEmpty && now - pair.Value.emptyTick >= FINE_EMPTY_TTL)
                        expired.Add(pair.Key);
                }
                foreach (var coord in expired)
                    grid.cells.Remove(coord);

                // 队列重建：TTL 未到的空格仍在 cells 中（由后续全量斩杀回收），清空队列防积压
                grid.emptyQueue.Clear();
                grid.queuedEmpties.Clear();
            }
        }

        private void EnqueueEmpty(SystemGrid grid, (int cx, int cy, int cz) coord, FineCell cell)
        {
            cell.emptyTick = Find.TickManager.TicksGame; // 刷新 TTL（无论是否已在队列）
            if (grid.queuedEmpties.Add(coord))
                grid.emptyQueue.Enqueue(coord);
        }

        public void ClearSystem(int systemBodyId)
        {
            systemGrids.Remove(systemBodyId);
        }

        // ⚠ [V9] 2026-09-16：原 public void Clear() 已删除——它是零调用者的死 API，
        // 且若被调用会**静默破坏碰撞检测**：它只清 systemGrids，不同步清 CombatManager.shipLastPositions，
        // 于是位置未变的船在 SyncAllShipsToGrid 里既不 AddShip 也不 UpdateShipPosition，永远缺席细网格。
        // ClearSystem 保留（语义上可用于卸某个行星系），但**同一个坑存在**：调用方若要用它，
        // 必须同时让 CombatManager 忘记该系的 shipLastPositions。

        public int CellCount
        {
            get
            {
                int count = 0;
                foreach (var kv in systemGrids)
                    count += kv.Value.cells.Count;
                return count;
            }
        }

        public void DebugPrintStats(System.Text.StringBuilder sb)
        {
            sb.Append(" 细网格系统数=").Append(systemGrids.Count);
            int totalCells = 0, totalShips = 0, totalProjs = 0, totalQueue = 0;
            int recentCells = 0, now = Find.TickManager?.TicksGame ?? 0;
            foreach (var kv in systemGrids)
            {
                var g = kv.Value;
                int cells = g.cells.Count;
                int ships = 0, projs = 0, recent = 0;
                var shipSet = new HashSet<SpaceEntityGroup>();
                foreach (var c in g.cells.Values)
                {
                    foreach (var s in c.ships)
                        shipSet.Add(s);
                    projs += c.projectiles.Count;
                    if (now - c.createdTick < 120) recent++;
                }
                ships = shipSet.Count; // 多格注册去重
                totalCells += cells;
                totalShips += ships;
                totalProjs += projs;
                totalQueue += g.emptyQueue.Count;
                recentCells += recent;
                sb.Append(" [Sys").Append(kv.Key)
                  .Append(":格").Append(cells)
                  .Append(" 船").Append(ships)
                  .Append(" 弹").Append(projs)
                  .Append(" 队").Append(g.emptyQueue.Count)
                  .Append("]");
            }
            sb.Append(" 合计格=").Append(totalCells)
              .Append(" 船=").Append(totalShips)
              .Append(" 弹=").Append(totalProjs)
              .Append(" 队=").Append(totalQueue)
              .Append(" 新/2s=").Append(recentCells);
        }

        private static void GetCellBounds((int cx, int cy, int cz) cell, out DVector3 min, out DVector3 max)
        {
            min = new DVector3(cell.cx * FINE_CELL_SIZE, cell.cy * FINE_CELL_SIZE, cell.cz * FINE_CELL_SIZE);
            max = new DVector3((cell.cx + 1) * FINE_CELL_SIZE, (cell.cy + 1) * FINE_CELL_SIZE, (cell.cz + 1) * FINE_CELL_SIZE);
        }

        /// <summary>
        /// Ray-AABB 相交检测（Slab 方法，实现已抽到纯核心 <see cref="SlabMath"/> 以便单测——
        /// 2026-09-08 退化零长段恒真导致弹群首拍全转化，几何判据必须有测试覆盖）。
        /// exitFace: 0=-X, 1=+X, 2=-Y, 3=+Y, 4=-Z, 5=+Z，-1 = 无交点。
        /// </summary>
        internal static bool RaySlabIntersection(DVector3 start, DVector3 end, DVector3 min, DVector3 max,
            out double tNear, out double tFar, out int exitFace)
            => SlabMath.RaySlabIntersection(start, end, min, max, out tNear, out tFar, out exitFace);

        private (int face, double t) SlabMethodExitFace(DVector3 start, DVector3 end, DVector3 min, DVector3 max)
        {
            if (!RaySlabIntersection(start, end, min, max, out double tNear, out _, out int exitFace))
                return (-1, 0);

            return (exitFace, tNear);
        }

        private (int cx, int cy, int cz) GetNeighborCell((int cx, int cy, int cz) cell, int face)
        {
            switch (face)
            {
                case 0: return (cell.cx - 1, cell.cy, cell.cz);
                case 1: return (cell.cx + 1, cell.cy, cell.cz);
                case 2: return (cell.cx, cell.cy - 1, cell.cz);
                case 3: return (cell.cx, cell.cy + 1, cell.cz);
                case 4: return (cell.cx, cell.cy, cell.cz - 1);
                case 5: return (cell.cx, cell.cy, cell.cz + 1);
                default: return cell;
            }
        }

        private DVector3 GetGroupPosMeters(SpaceEntityGroup group)
        {
            if (group.IsCircularOrbit)
                return group.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return group.CurrentOrbit.position;
        }

        public override void ExposeData()
        {
            base.ExposeData();
        }
    }
}
