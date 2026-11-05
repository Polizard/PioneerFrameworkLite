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

        /// <summary>
        /// 单船扫掠注册的格盒体积上限。超过即视为**位置跳变**（地图转移/调试传送/读档落位），
        /// 只注册当前格、不注册路径：真实飞行一步最多十几格，而传送可能一步跨几千格，
        /// 按矩形盒扫掠会瞬间注册数万格并产出海量假候选。
        /// </summary>
        private const int MAX_SWEPT_CELLS = 4096;
        private const int FULL_SWEEP_INTERVAL = 600;   // 全量兜底扫描周期（真实 tick，10s 一次，避免周期尖峰）
        private int lastFullSweepTick = -1;

        public class FineCell
        {
            public List<SpaceEntityGroup> ships = new();
            public List<ProjectileInstance> projectiles = new();

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
            public Dictionary<(int cx, int cy, int cz), FineCell> cells = new();
            public Queue<(int cx, int cy, int cz)> emptyQueue = new();
            // 队列去重：已在队列中的空格（防止同一格"空→非空→空"重复入队导致队列膨胀）
            public HashSet<(int cx, int cy, int cz)> queuedEmpties = new();
        }

        private readonly Dictionary<int, SystemGrid> systemGrids = new();

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

            /// <summary>格数（long 口径：传送跳变的跨度可能让 int 乘法溢出）。</summary>
            public long CellCountLong =>
                (long)(maxX - minX + 1) * (maxY - minY + 1) * (maxZ - minZ + 1);

            /// <summary>返回覆盖两个格盒的轴对齐格盒（闭区间）。</summary>
            public static CoverBox Union(CoverBox a, CoverBox b)
            {
                return new CoverBox
                {
                    minX = System.Math.Min(a.minX, b.minX),
                    maxX = System.Math.Max(a.maxX, b.maxX),
                    minY = System.Math.Min(a.minY, b.minY),
                    maxY = System.Math.Max(a.maxY, b.maxY),
                    minZ = System.Math.Min(a.minZ, b.minZ),
                    maxZ = System.Math.Max(a.maxZ, b.maxZ)
                };
            }
        }

        // 每艘船本物理步注册的细网格格盒（多格注册）。语义 = **扫掠覆盖**：
        // 上一步的当前 AABB 覆盖格 ∪ 本步的当前 AABB 覆盖格。
        // 只按当前格注册会漏掉"两船一步对穿相邻格"的高速对：步末 AABB 分处两格、
        // 当前覆盖格不共享 ⇒ EnumerateShipPairs 根本不产出这对，后面的球/扫掠/TOI 全部没机会跑。
        private readonly Dictionary<SpaceEntityGroup, CoverBox> shipCoveredCells = new();

        // 每艘船的上一步/本步**精确**世界 AABB（米）。格盒只负责粗筛，
        // 同格内再用精确扫掠 AABB 过滤：256 艘 500m 间距同格时格内 32,640 对里
        // 真实可能相交的是 0 对，旧实现却把 32,640 对全送进逐对几何判定（实测 4–8ms/步）。
        private struct ShipAabb
        {
            public DVector3 prevMin, prevMax;
            public DVector3 curMin, curMax;
        }

        private readonly Dictionary<SpaceEntityGroup, ShipAabb> shipAabb = new();

        // 格内 SAP：按扫掠 AABB 的**跨度最大轴**排序后扫描（2026-11-01：固定 X 轴在同格密集场景
        // 退化成全扫；同格 256 艘实测 31k 次配对测试/步）。复用缓冲区，避免每步分配。
        // `_sapSortAxis` 仅主线程枚举期间使用（CombatGrid 的配对枚举不在并行段里）。
        private static int _sapSortAxis;
        private struct SapEntry : System.IComparable<SapEntry>
        {
            public double minX, maxX, minY, maxY, minZ, maxZ;
            public int origIndex;
            public bool singleCell;   // 预计算：单格-单格对天然唯一，不需要 pair 哈希
            public SpaceEntityGroup ship;

            public int CompareTo(SapEntry other)
            {
                double a = MinOnAxis(this, _sapSortAxis);
                double b = MinOnAxis(other, _sapSortAxis);
                if (a < b) return -1;
                if (a > b) return 1;
                return origIndex.CompareTo(other.origIndex);
            }
        }

        private static double MinOnAxis(in SapEntry e, int axis)
            => axis == 0 ? e.minX : axis == 1 ? e.minY : e.minZ;
        private static double MaxOnAxis(in SapEntry e, int axis)
            => axis == 0 ? e.maxX : axis == 1 ? e.maxY : e.maxZ;

        /// <summary>另外两轴的区间重叠检查（排序轴由 break 处理）。</summary>
        private static bool OtherAxesOverlap(in SapEntry a, in SapEntry b, int axis)
        {
            if (axis != 0 && (a.minX > b.maxX || b.minX > a.maxX)) return false;
            if (axis != 1 && (a.minY > b.maxY || b.minY > a.maxY)) return false;
            if (axis != 2 && (a.minZ > b.maxZ || b.minZ > a.maxZ)) return false;
            return true;
        }

        private SapEntry[] sapScratch = new SapEntry[64];

        // 每艘船**上一步的当前 AABB 覆盖格**（扫掠并集的基准；与 shipCoveredCells 分开存，
        // 保证下一步求并时不会把更早的路径格累积进来）。
        private readonly Dictionary<SpaceEntityGroup, CoverBox> shipCurrentCells = new();

        // 扫掠盒仍大于当前盒（本步跨了格）的船：下一步若不再移动，由 SettleSweptBox 收缩回当前盒。
        // 不这样做的话，停船会永久保留最后一步的多余格（正确性无害，但候选对白跑）。
        private readonly HashSet<SpaceEntityGroup> sweepSettlePending = new();

        // #4：EnumerateShipPairs 的船对去重缓冲（复用，避免每 2 tick 分配 HashSet）
        private readonly HashSet<long> pairScratch = new();

        // 2026-11-01（碰撞配对循环拆账）：只数不改。`原始对` 看格内 C(n,2) 总量，
        // `扫掠` 看真正调用 SweptAabbOverlap 的次数，`产出` 看过了扫掠后 yield 的对数。
        internal long DebugSweptChecks;
        internal long DebugYieldedPairs;
        internal void ResetPairDebugCounters() { DebugSweptChecks = 0; DebugYieldedPairs = 0; }

#if DEBUG
        /// <summary>DEBUG：船跨格（覆盖格盒变化）累计次数，供 CombatManager 汇总日志。</summary>
        internal long debugCellCrossCount;
#endif

        // ── 组件引用缓存（2026-09-10 性能第 5 条）：本类每船同步 / 每弹每步都要查这两个组件，
        //    GetComponent 是组件表线性扫描，故按 Game 缓存（跨局自动失效，见 GameComponentRef）。──
        private readonly GameComponentRef<CelestialBodyManager> celestialMgrRef = new();
        private readonly GameComponentRef<SpaceGameComp> spaceGameRef = new();

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
            var grid = GetOrCreateSystem(GetFrameId(ship));

            AddShipToCells(grid, ship, box);

            shipCoveredCells[ship] = box;
            shipCurrentCells[ship] = box;
            shipAabb[ship] = new ShipAabb
            {
                prevMin = bounds.min, prevMax = bounds.max,
                curMin = bounds.min, curMax = bounds.max
            };
            sweepSettlePending.Remove(ship);
        }

        public void RemoveShip(SpaceEntityGroup ship)
        {
            if (!shipCoveredCells.TryGetValue(ship, out var box))
                return;

            var grid = GetOrCreateSystem(GetFrameId(ship));
            RemoveShipFromCells(grid, ship, box);

            shipCoveredCells.Remove(ship);
            shipCurrentCells.Remove(ship);
            shipAabb.Remove(ship);
            sweepSettlePending.Remove(ship);
        }

        /// <summary>
        /// 位置更新（每物理步一次）：把船的注册格盒推进到 **扫掠覆盖**
        /// = 上一步的当前覆盖格 ∪ 本步的当前覆盖格。
        /// 旧路径格在上一步的当前盒之外，因此下一步求并时不会累积。
        /// </summary>
        public void UpdateShipPosition(SpaceEntityGroup ship, DVector3 oldPosMeters)
        {
            var newBounds = GetGroupBoundsSystem(ship);
            var newCurrentBox = CoverBoxOf(newBounds.min, newBounds.max);

            if (!shipCoveredCells.TryGetValue(ship, out var oldSweptBox))
            {
                AddShip(ship);
                return;
            }

            if (!shipCurrentCells.TryGetValue(ship, out var oldCurrentBox))
                oldCurrentBox = oldSweptBox; // 旧档/异常状态兜底：无当前盒时至少不丢已注册格

            var newSweptBox = CoverBox.Union(oldCurrentBox, newCurrentBox);

            // 位置跳变（地图转移/调试传送/读档落位）不是物理飞行路径：只注册当前格。
            // 不做这层保护的话，一次 1e9 m 的传送会注册 ~5 万格并让同场所有船对白跑。
            bool teleported = newSweptBox.CellCountLong > MAX_SWEPT_CELLS;
            if (teleported)
                newSweptBox = newCurrentBox;

            // 精确 AABB 同步推进：平移扫掠 = 上一步当前 AABB ∪ 本步当前 AABB。
            // 传送按“不扫掠”处理（prev=cur），与格注册的跳变保护保持一致。
            if (shipAabb.TryGetValue(ship, out var aabb))
            {
                aabb.prevMin = teleported ? newBounds.min : aabb.curMin;
                aabb.prevMax = teleported ? newBounds.max : aabb.curMax;
                aabb.curMin = newBounds.min;
                aabb.curMax = newBounds.max;
                shipAabb[ship] = aabb;
            }
            else
            {
                shipAabb[ship] = new ShipAabb
                {
                    prevMin = newBounds.min, prevMax = newBounds.max,
                    curMin = newBounds.min, curMax = newBounds.max
                };
            }

            if (oldSweptBox.Equals(newSweptBox)) // O(1) 盒比较，替代 O(k²) 列表逐元素比较
            {
                shipCurrentCells[ship] = newCurrentBox;
                return;
            }

#if DEBUG
            debugCellCrossCount++;
#endif

            var grid = GetOrCreateSystem(GetFrameId(ship));
            ReplaceShipCells(grid, ship, oldSweptBox, newSweptBox);

            shipCoveredCells[ship] = newSweptBox;
            shipCurrentCells[ship] = newCurrentBox;

            // 本步跨格 ⇒ 扫掠盒大于当前盒；下一步若原地不动，由 SettleSweptBox 收缩。
            if (newSweptBox.Equals(newCurrentBox))
                sweepSettlePending.Remove(ship);
            else
                sweepSettlePending.Add(ship);
        }

        /// <summary>
        /// 船停下后收缩扫掠注册：把 "上一步当前格 ∪ 本步当前格" 中的旧格移除，只保留当前格。
        /// 同步器在"本步位置未变"时调用；无待收缩状态时 O(1) 返回（不重算 AABB）。
        /// </summary>
        public void SettleSweptBox(SpaceEntityGroup ship)
        {
            if (!sweepSettlePending.Remove(ship)) return;
            if (!shipCoveredCells.TryGetValue(ship, out var sweptBox)) return;
            if (!shipCurrentCells.TryGetValue(ship, out var currentBox)) return;
            if (sweptBox.Equals(currentBox)) return;

            var grid = GetOrCreateSystem(GetFrameId(ship));
            ReplaceShipCells(grid, ship, sweptBox, currentBox);
            shipCoveredCells[ship] = currentBox;
        }

        /// <summary>把船注册到格盒覆盖的每个细网格格（闭区间，零 List 分配）。</summary>
        private static void AddShipToCells(SystemGrid grid, SpaceEntityGroup ship, CoverBox box)
        {
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
        }

        private void RemoveShipFromCells(SystemGrid grid, SpaceEntityGroup ship, CoverBox box)
        {
            for (int x = box.minX; x <= box.maxX; x++)
                for (int y = box.minY; y <= box.maxY; y++)
                    for (int z = box.minZ; z <= box.maxZ; z++)
                        RemoveShipFromCell(grid, ship, (x, y, z));
        }

        /// <summary>把注册格盒从 oldBox 差集推进到 newBox（移除 old−new，加入 new−old）。</summary>
        private void ReplaceShipCells(SystemGrid grid, SpaceEntityGroup ship, CoverBox oldBox, CoverBox newBox)
        {
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
        /// 枚举所有**本步扫掠 AABB 真相交**的船对（去重）。用于船-船碰撞候选。
        /// 格注册只做粗筛；每格内再按精确扫掠 AABB 过滤（格内 SAP），
        /// 避免把 20km 格里 32,640 个“同格但对向不可能相交”的船对全送进逐对几何判定。
        /// 完备性：平移扫掠体相交 ⇒ 两船精确扫掠 AABB 必相交；精确盒覆盖相对路径。
        ///
        /// 2026-10-20：混合格也走同一 SAP（不再有“格内有跨格船 ⇒ 整格 O(n²) 回退”的悬崖）。
        /// 单格-单格对天然只在一个格出现，不需要 pair 哈希；涉及跨格扫掠船的对仍用哈希去重。
        /// </summary>
        public IEnumerable<(SpaceEntityGroup a, SpaceEntityGroup b)> EnumerateShipPairs()
        {
            var checkedPairs = pairScratch;
            checkedPairs.Clear();
            // 2026-11-01：把"遍历格/SAP/过滤"的时间单独计入 `枚举` 子段；
            // 每次 yield 前后切换，yield 出去的逐对体时间不算在枚举里。
            long enumClock = PerfProbe.CollisionStageStart();

            foreach (var kvGrid in systemGrids)
            {
                foreach (var kvCell in kvGrid.Value.cells)
                {
                    var ships = kvCell.Value.ships;
                    int n = ships.Count;
                    if (n < 2) continue;

                    if (n <= 8)
                    {
                        for (int i = 0; i < n; i++)
                            for (int j = i + 1; j < n; j++)
                            {
                                var a = ships[i];
                                var b = ships[j];
                                if (PerfProbe.CollisionStagesEnabled) DebugSweptChecks++;
                                if (!SweptAabbOverlap(a, b)) continue;
                                bool single = IsSingleCell(a) && IsSingleCell(b);
                                if (single || TryMarkPair(a, b, checkedPairs))
                                {
                                    if (PerfProbe.CollisionStagesEnabled) DebugYieldedPairs++;
                                    PerfProbe.CollisionStageEnd(PerfProbe.ColStageEnum, enumClock);
                                    yield return OrderPair(a, b);
                                    enumClock = PerfProbe.CollisionStageStart();
                                }
                            }
                        continue;
                    }

                    // 格内 SAP：按扫掠 AABB minX 排序，扫描到 minX > maxX 即停。
                    // 256 艘 500m 间距同格：真实相交 0 对（旧实现产出 32,640 对）。
                    EnsureSapCapacity(n);
                    var entries = sapScratch;
                    for (int s = 0; s < n; s++)
                        entries[s] = BuildSapEntry(ships[s], s);

                    // 2026-11-01：选**扫掠 AABB 跨度最大**的轴做排序/扫描轴。
                    // 同格 256 艘时 X 轴可能几乎重合，固定 X 会退化成 n²/2 全扫（实测 31k 测试/步）。
                    int axis = 0;
                    double bestSpan = -1.0;
                    for (int ax = 0; ax < 3; ax++)
                    {
                        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                        for (int s = 0; s < n; s++)
                        {
                            double v0 = MinOnAxis(entries[s], ax);
                            double v1 = MaxOnAxis(entries[s], ax);
                            if (v0 < lo) lo = v0;
                            if (v1 > hi) hi = v1;
                        }
                        double span = hi - lo;
                        if (span > bestSpan) { bestSpan = span; axis = ax; }
                    }
                    _sapSortAxis = axis;
                    System.Array.Sort(entries, 0, n);

                    for (int i = 0; i < n; i++)
                    {
                        var ei = entries[i];
                        double eiMax = MaxOnAxis(ei, axis);
                        for (int j = i + 1; j < n; j++)
                        {
                            var ej = entries[j];
                            if (MinOnAxis(ej, axis) > eiMax) break;
                            if (PerfProbe.CollisionStagesEnabled) DebugSweptChecks++;
                            if (!OtherAxesOverlap(ei, ej, axis)) continue;

                            var a = ei.ship;
                            var b = ej.ship;
                            bool single = ei.singleCell && ej.singleCell;
                            if (single || TryMarkPair(a, b, checkedPairs))
                            {
                                if (PerfProbe.CollisionStagesEnabled) DebugYieldedPairs++;
                                PerfProbe.CollisionStageEnd(PerfProbe.ColStageEnum, enumClock);
                                yield return OrderPair(a, b);
                                enumClock = PerfProbe.CollisionStageStart();
                            }
                        }
                    }
                }
            }
            PerfProbe.CollisionStageEnd(PerfProbe.ColStageEnum, enumClock);
        }

        /// <summary>该船本步只覆盖一个格（单格-单格对天然只在一个格出现）。</summary>
        private bool IsSingleCell(SpaceEntityGroup ship)
        {
            return shipCoveredCells.TryGetValue(ship, out var box) && box.CellCountLong == 1;
        }

        private static bool TryMarkPair(SpaceEntityGroup a, SpaceEntityGroup b, HashSet<long> checkedPairs)
        {
            int minId = a.GroupId < b.GroupId ? a.GroupId : b.GroupId;
            int maxId = a.GroupId < b.GroupId ? b.GroupId : a.GroupId;
            return checkedPairs.Add(((long)minId << 32) | (uint)maxId);
        }

        /// <summary>固定按 GroupId 小者在前，保证同一对在多个格出现时方向一致。</summary>
        private static (SpaceEntityGroup a, SpaceEntityGroup b) OrderPair(SpaceEntityGroup a, SpaceEntityGroup b)
        {
            return a.GroupId <= b.GroupId ? (a, b) : (b, a);
        }

        private void EnsureSapCapacity(int n)
        {
            if (sapScratch.Length >= n) return;
            int cap = sapScratch.Length;
            while (cap < n) cap *= 2;
            sapScratch = new SapEntry[cap];
        }

        private SapEntry BuildSapEntry(SpaceEntityGroup ship, int origIndex)
        {
            bool singleCell = IsSingleCell(ship);
            if (shipAabb.TryGetValue(ship, out var a))
            {
                return new SapEntry
                {
                    minX = System.Math.Min(a.prevMin.x, a.curMin.x),
                    maxX = System.Math.Max(a.prevMax.x, a.curMax.x),
                    minY = System.Math.Min(a.prevMin.y, a.curMin.y),
                    maxY = System.Math.Max(a.prevMax.y, a.curMax.y),
                    minZ = System.Math.Min(a.prevMin.z, a.curMin.z),
                    maxZ = System.Math.Max(a.prevMax.z, a.curMax.z),
                    origIndex = origIndex,
                    singleCell = singleCell,
                    ship = ship
                };
            }

            // 缺 AABB 的船（异常/旧档兜底）：按无穷大盒处理，保守地与所有船配对。
            return new SapEntry
            {
                minX = double.NegativeInfinity, maxX = double.PositiveInfinity,
                minY = double.NegativeInfinity, maxY = double.PositiveInfinity,
                minZ = double.NegativeInfinity, maxZ = double.PositiveInfinity,
                origIndex = origIndex,
                singleCell = singleCell,
                ship = ship
            };
        }

        /// <summary>两船本步扫掠精确 AABB 是否相交（闭区间）。缺状态时保守返回 true。</summary>
        private bool SweptAabbOverlap(SpaceEntityGroup a, SpaceEntityGroup b)
        {
            if (!shipAabb.TryGetValue(a, out var sa) || !shipAabb.TryGetValue(b, out var sb))
                return true;

            return System.Math.Min(sa.prevMin.x, sa.curMin.x) <= System.Math.Max(sb.prevMax.x, sb.curMax.x)
                && System.Math.Min(sb.prevMin.x, sb.curMin.x) <= System.Math.Max(sa.prevMax.x, sa.curMax.x)
                && System.Math.Min(sa.prevMin.y, sa.curMin.y) <= System.Math.Max(sb.prevMax.y, sb.curMax.y)
                && System.Math.Min(sb.prevMin.y, sb.curMin.y) <= System.Math.Max(sa.prevMax.y, sa.curMax.y)
                && System.Math.Min(sa.prevMin.z, sa.curMin.z) <= System.Math.Max(sb.prevMax.z, sb.curMax.z)
                && System.Math.Min(sb.prevMin.z, sb.curMin.z) <= System.Math.Max(sa.prevMax.z, sa.curMax.z);
        }

        /// <summary>诊断：当前有多艘船的扫掠覆盖格 &gt;1（会让全单格快速路径整体关闭）。</summary>
        internal int DebugMultiCellShipCount()
        {
            int n = 0;
            foreach (var kv in shipCoveredCells)
                if (kv.Value.CellCountLong > 1) n++;
            return n;
        }

        /// <summary>诊断：格内 i&lt;j 的原始对数（不按精确 AABB 过滤，多格船会重复计数）。</summary>
        internal int DebugRawCellPairCount()
        {
            int n = 0;
            foreach (var kvGrid in systemGrids)
                foreach (var kvCell in kvGrid.Value.cells)
                {
                    int c = kvCell.Value.ships.Count;
                    n += c * (c - 1) / 2;
                }
            return n;
        }

        /// <summary>诊断：格总数 / ≥2 艘的格数 / 船-格条目总数（判断枚举是否在扫大量 0/1 艘格）。</summary>
        internal string DebugCellStats()
        {
            int total = 0, multi = 0, entries = 0;
            foreach (var kvGrid in systemGrids)
                foreach (var kvCell in kvGrid.Value.cells)
                {
                    int c = kvCell.Value.ships.Count;
                    total++;
                    entries += c;
                    if (c >= 2) multi++;
                }
            return $"{total}/{multi}/{entries}";
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
            var cell = WorldToFineCell(proj.position.v);   // 批6 S3-3c：索引吃裸三分量 ⇒ 显式跨界
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
            cell.emptyTick = Find.TickManager?.TicksGame ?? 0; // 刷新 TTL（无论是否已在队列；headless 无 TickManager）
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
                  .Append(']');
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
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                => face switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                {
            0 => (cell.cx - 1, cell.cy, cell.cz),
            1 => (cell.cx + 1, cell.cy, cell.cz),
            2 => (cell.cx, cell.cy - 1, cell.cz),
            3 => (cell.cx, cell.cy + 1, cell.cz),
            4 => (cell.cx, cell.cy, cell.cz - 1),
            5 => (cell.cx, cell.cy, cell.cz + 1),
            _ => cell,
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                };

        private DVector3 GetGroupPosMeters(SpaceEntityGroup group)
        {
            if (group.IsCircularOrbit)
                return group.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return group.CurrentOrbit.positionSystem.v;   // 批6 S3-3c：读带系字段（旧名是过渡桥）
        }

        public override void ExposeData()
        {
            base.ExposeData();
        }
    }
}
