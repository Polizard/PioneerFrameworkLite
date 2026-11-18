using System.Collections.Generic;
using UnityEngine;
using Verse;
using PioneerFramework.Combat;
using PioneerFramework.Components;

namespace PioneerFramework
{
    public class SpaceEntity : IExposable
    {
        private int entityId;
        public int EntityId => entityId;

        private int groupId;
        public int GroupId
        {
            get => groupId;
            set => groupId = value;
        }

        private int factionId;
        public int FactionId
        {
            get => factionId;
            set => factionId = value;
        }

        public bool CanInteract = false;
        public bool IsSelectable = true;
        public bool HasMap = false;
        public bool IsMapArchived = false;  // 地图已被封存到磁盘
        public bool IsMovable = true;
        public bool IsAffectedByGravity = true;
        public bool IsCollidable = true;

        public DVector3 BoundsSize = new DVector3(5000.0, 3000.0, 5000.0);

        /// <summary>
        /// AABB 半边长（旋转保守放大 2 倍）。所有 AABB 消费方必须用此属性而非 BoundsSize*0.5：
        /// 忽略 Rotation 的轴对齐近似下，半边长取整个 BoundsSize 才能覆盖任意旋转的最坏投影。
        /// </summary>
        public DVector3 AabbHalfSize => BoundsSize;

        private DVector3 positionMeters;
        private DVector3 velocityMeters;
        private Quaternion rotation;

        public float MaxDeltaV = 0f;
        public float RemainingDeltaV = 0f;

        public int MapId = -1;

        /// <summary>飞船设计数据引用（仅运行时，不序列化）。用于 3D 视角渲染实际部件。</summary>
        public Ship3D shipDesign;

        // ── 部件系统 ──
        private List<SpacePart> parts = new List<SpacePart>();
        public IReadOnlyList<SpacePart> Parts => parts;

        // ── 实体级 stats 缓存（kg 等，Parts / 组件变更时置脏）──
        private ShipStats shipStats;
        private bool statsDirty = true;

        /// <summary>实体聚合属性：Σ 未摧毁部件的 ShipStats，双级缓存（部件级 cachedStats + 实体级聚合）。</summary>
        public ShipStats AggregatedStats
        {
            get
            {
                if (statsDirty)
                    RecalculateShipStats();
                return shipStats;
            }
        }

        /// <summary>实体总质量（kg）：Σ 未摧毁部件质量，缓存防重复计算。</summary>
        public float TotalMass => AggregatedStats.mass;

        private void RecalculateShipStats()
        {
            shipStats = default(ShipStats);
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.isDestroyed) continue;
                var ps = p.Stats;
                shipStats.mass += ps.mass;
                shipStats.thrust += ps.thrust;
                shipStats.powerGen += ps.powerGen;
                shipStats.powerDraw += ps.powerDraw;
                shipStats.heatProduction += ps.heatProduction;
                shipStats.heatDissipation += ps.heatDissipation;
                shipStats.propellant += ps.propellant;
                shipStats.powerStorage += ps.powerStorage;
                shipStats.shieldCapacity += ps.shieldCapacity;
                shipStats.sensorRange += ps.sensorRange; // 聚合语义待定（max vs sum），先求和
                shipStats.cargoCapacity += ps.cargoCapacity;
            }
            statsDirty = false;
        }

        /// <summary>实体级 stats 失效。所有"部件集 / 部件组件可能变化"的路径必须走这里。</summary>
        public void InvalidateStats()
        {
            statsDirty = true;
        }

        // ── 武器系统（从部件收集，不序列化）──
        private List<WeaponInstance> cachedWeapons;
        private bool weaponsDirty = true;
        /// <summary>武器缓存版本号：失效时递增，供 CombatManager 检测 group 级武器列表是否变化。</summary>
        private int weaponsVersion;
        /// <summary>武器缓存版本号（CombatManager 用于版本比对，版本不变则不重建 group 武器列表）。</summary>
        internal int WeaponsVersion => weaponsVersion;

        /// <summary>
        /// 武器缓存失效：置脏 + 版本号递增。所有"武器可能变化"的路径必须走这里。
        /// </summary>
        private void InvalidateWeapons()
        {
            weaponsDirty = true;
            weaponsVersion++;
        }

        // ── Morton 空间索引（不序列化，从 Parts 重建）──
        private MortonEntry[] mortonIndex;
        private bool mortonDirty = true;
        private double mortonCellSize;
        private DVector3 mortonOrigin;
        private const double DEFAULT_MORTON_CELL_SIZE = 30.0;

        // #4：QueryRayHit 查询缓冲复用（避免射弹雨/激光连发时每次查询分配 HashSet/List）
        // 注意：返回的 results 是此缓冲引用，调用方须立即消费，不得跨查询持有。
        private readonly HashSet<ulong> queryVisitedScratch = new HashSet<ulong>();
        private readonly List<(int partIndex, double hitT)> queryResultsScratch = new List<(int, double)>();

        // 调试计数器
        internal long debugQueryCount = 0;
        internal long debugCellVisitedCount = 0;
        internal long debugPartTestCount = 0;
        internal long debugHitCount = 0;

        // 物理状态
        public DVector3 Position
        {
            get => positionMeters;
            set => positionMeters = value;
        }

        public DVector3 Velocity
        {
            get => velocityMeters;
            set => velocityMeters = value;
        }

        public Quaternion Rotation
        {
            get => rotation;
            set => rotation = value;
        }

        // 辅助：Unity 世界坐标（用于渲染）
        public Vector3 PositionUnity => (Vector3)(positionMeters * SpaceConstants.UnitsPerMeter);
        public Vector3 VelocityUnity => (Vector3)(velocityMeters * SpaceConstants.UnitsPerMeter);

        public SpaceEntity() { }
        public SpaceEntity(int entityId, int factionId = 0)
        {
            this.entityId = entityId;
            FactionId = factionId;
        }

        public void SetPositionVelocity(DVector3 posMeters, DVector3 velMeters)
        {
            positionMeters = posMeters;
            velocityMeters = velMeters;
        }

        public void SetRotation(Quaternion rot) => rotation = rot;

        public bool TryConsumeDeltaV(float amountMps)
        {
            if (RemainingDeltaV >= amountMps)
            {
                RemainingDeltaV -= amountMps;
                return true;
            }
            return false;
        }

        public void RefillDeltaV() => RemainingDeltaV = MaxDeltaV;

        // ── 部件管理 ──

        /// <summary>
        /// 从飞船设计数据初始化部件列表。会从 Parts 重新计算 BoundsSize。
        /// </summary>
        public void SetParts(List<SpacePart> newParts, bool recalculateBounds = true)
        {
            parts = newParts ?? new List<SpacePart>();
            mortonDirty = true;
            InvalidateWeapons();
            InvalidateStats();
            if (recalculateBounds && parts.Count > 0)
                RecalculateBoundsFromParts();
        }

        /// <summary>
        /// 根据所有 Part 的 AABB 重新计算 Entity 的 BoundsSize。
        /// 取全部 Part 在三个轴向上的最大跨度。
        /// </summary>
        public void RecalculateBoundsFromParts()
        {
            if (parts.Count == 0) return;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var part in parts)
            {
                if (part.isDestroyed) continue;
                DVector3 half = part.localBoundsSize * 0.5;
                double px = part.localPosition.x, py = part.localPosition.y, pz = part.localPosition.z;
                if (px - half.x < minX) minX = px - half.x;
                if (px + half.x > maxX) maxX = px + half.x;
                if (py - half.y < minY) minY = py - half.y;
                if (py + half.y > maxY) maxY = py + half.y;
                if (pz - half.z < minZ) minZ = pz - half.z;
                if (pz + half.z > maxZ) maxZ = pz + half.z;
            }

            if (maxX > minX && maxY > minY && maxZ > minZ)
            {
                BoundsSize = new DVector3(maxX - minX, maxY - minY, maxZ - minZ);
                mortonDirty = true;
            }
        }

        /// <summary>
        /// 标记部件被摧毁（惰性标记，不立即重建 Morton 索引）。
        /// 舱室部件被毁时联动销毁其内部地图（先杀光内部 pawns）。
        /// </summary>
        public void MarkPartDestroyed(int partId)
        {
            foreach (var part in parts)
            {
                if (part.partId == partId)
                {
                    part.isDestroyed = true;
                    part.DisableWeapon();
                    InvalidateWeapons();
                    InvalidateStats();
                    RecalculateBoundsFromParts();
                    // 舱室联动：销毁内部地图（含 pawns 清除）
                    if (part.linkedMapId >= 0)
                        SpaceMapGenerator.DestroyRoomMap(part.linkedMapId);
                    return;
                }
            }
        }

        // ── 武器管理 ──

        /// <summary>
        /// 获取该实体上所有可用的武器实例（从武器部件收集）。
        /// 结果会被缓存，直到部件变更。
        /// </summary>
        public List<WeaponInstance> GetWeapons()
        {
            if (!weaponsDirty && cachedWeapons != null)
                return cachedWeapons;

            weaponsDirty = false;
            if (cachedWeapons == null)
                cachedWeapons = new List<WeaponInstance>();
            else
                cachedWeapons.Clear();

            foreach (var part in parts)
            {
                if (part.isDestroyed) continue;
                if (part.weaponInstance != null && !part.weaponInstance.isDisabled)
                    cachedWeapons.Add(part.weaponInstance);
            }

            return cachedWeapons;
        }

        /// <summary>
        /// 获取特定部件的武器实例。
        /// </summary>
        public WeaponInstance GetWeaponForPart(int partId)
        {
            foreach (var part in parts)
            {
                if (part.partId == partId && !part.isDestroyed)
                    return part.weaponInstance;
            }
            return null;
        }

        /// <summary>
        /// 调试：获取 Morton 索引统计信息。
        /// </summary>
        public string DebugGetMortonStats()
        {
            if (parts.Count == 0)
                return "Morton: 无部件";
            var index = GetOrBuildMortonIndex();
            if (index == null)
                return "Morton: 索引为空";
            return string.Format(
                "Morton: cellSize={0:F0}m, origin=({1:F0},{2:F0},{3:F0}), entries={4}, parts={5}, query={6}, cells={7}, tests={8}, hits={9}",
                mortonCellSize, mortonOrigin.x, mortonOrigin.y, mortonOrigin.z,
                index.Length, parts.Count,
                debugQueryCount, debugCellVisitedCount, debugPartTestCount, debugHitCount);
        }

        /// <summary>
        /// 调试：获取 Morton 网格可视化数据（Cell 中心点列表，世界坐标）。
        /// </summary>
        public List<(DVector3 center, double size)> DebugGetMortonCells()
        {
            var result = new List<(DVector3, double)>();
            var index = GetOrBuildMortonIndex();
            if (index == null) return result;

            var visited = new HashSet<ulong>();
            foreach (var entry in index)
            {
                if (visited.Contains(entry.code)) continue;
                visited.Add(entry.code);
                var (ix, iy, iz) = Morton3D.Decode(entry.code);
                DVector3 center = mortonOrigin + new DVector3(
                    (ix + 0.5) * mortonCellSize,
                    (iy + 0.5) * mortonCellSize,
                    (iz + 0.5) * mortonCellSize);
                result.Add((center, mortonCellSize));
            }
            return result;
        }

        /// <summary>
        /// 获取或构建 Morton 索引。首次调用或 Parts 变更后自动重建。
        /// </summary>
        public MortonEntry[] GetOrBuildMortonIndex()
        {
            if (!mortonDirty && mortonIndex != null)
                return mortonIndex;

            mortonDirty = false;

            if (parts.Count == 0)
            {
                mortonIndex = null;
                return null;
            }

            // 计算 Morton 格子原点（所有 Part AABB 最小角）
            double originX = double.MaxValue, originY = double.MaxValue, originZ = double.MaxValue;
            foreach (var part in parts)
            {
                if (part.isDestroyed) continue;
                DVector3 half = part.localBoundsSize * 0.5;
                double mx = part.localPosition.x - half.x;
                double my = part.localPosition.y - half.y;
                double mz = part.localPosition.z - half.z;
                if (mx < originX) originX = mx;
                if (my < originY) originY = my;
                if (mz < originZ) originZ = mz;
            }
            mortonOrigin = new DVector3(originX, originY, originZ);

            // 自动计算合适的 cellSize：取 Part bounds 最大边长的 2 倍
            double maxDim = 10.0;
            foreach (var part in parts)
            {
                if (part.isDestroyed) continue;
                double dim = part.localBoundsSize.x;
                if (part.localBoundsSize.y > dim) dim = part.localBoundsSize.y;
                if (part.localBoundsSize.z > dim) dim = part.localBoundsSize.z;
                if (dim > maxDim) maxDim = dim;
            }
            mortonCellSize = System.Math.Max(DEFAULT_MORTON_CELL_SIZE, maxDim * 2.0);

            // 构建 MortonEntry 数组
            var entries = new List<MortonEntry>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part.isDestroyed) continue;
                int ix = (int)((part.localPosition.x - mortonOrigin.x) / mortonCellSize);
                int iy = (int)((part.localPosition.y - mortonOrigin.y) / mortonCellSize);
                int iz = (int)((part.localPosition.z - mortonOrigin.z) / mortonCellSize);
                entries.Add(new MortonEntry
                {
                    code = Morton3D.Encode(ix, iy, iz),
                    partIndex = i
                });
            }

            entries.Sort();
            mortonIndex = entries.ToArray();
            return mortonIndex;
        }

        /// <summary>
        /// 射线命中查询。在 Entity 本地坐标系中沿射线走 Morton 格子，
        /// 返回所有被命中且未摧毁的 Part 索引（按命中距离排序）。
        /// </summary>
        public List<(int partIndex, double hitT)> QueryRayHit(
            DVector3 localOrigin, DVector3 localDir, double maxDist)
        {
            debugQueryCount++;
            // 复用内部缓冲（调用方须立即消费返回的引用，不得跨查询持有）
            var results = queryResultsScratch;
            results.Clear();
            queryVisitedScratch.Clear();

            if (parts.Count == 0)
                return results;

            // Entity 整体 AABB（快速拒绝）
            DVector3 half = AabbHalfSize;
            DVector3 aabbMin = -half;
            DVector3 aabbMax = half;
            if (!CombatGrid.RaySlabIntersection(localOrigin, localOrigin + localDir * maxDist,
                    aabbMin, aabbMax, out double tEntry, out double tExit, out _))
                return results;
            if (tEntry > tExit) return results;

            double tStart = System.Math.Max(0, tEntry);
            double tEnd = System.Math.Min(maxDist, tExit);

            // 获取 Morton 索引
            var index = GetOrBuildMortonIndex();
            if (index == null || index.Length == 0)
                return results;

            // DDA 沿射线步进
            var visited = queryVisitedScratch;
            DVector3 pos = localOrigin + localDir * tStart;
            double currentT = tStart;

            int cx = (int)((pos.x - mortonOrigin.x) / mortonCellSize);
            int cy = (int)((pos.y - mortonOrigin.y) / mortonCellSize);
            int cz = (int)((pos.z - mortonOrigin.z) / mortonCellSize);

            int stepX = localDir.x > 0 ? 1 : -1;
            int stepY = localDir.y > 0 ? 1 : -1;
            int stepZ = localDir.z > 0 ? 1 : -1;

            double tDeltaX = localDir.x != 0 ? System.Math.Abs(mortonCellSize / localDir.x) : double.PositiveInfinity;
            double tDeltaY = localDir.y != 0 ? System.Math.Abs(mortonCellSize / localDir.y) : double.PositiveInfinity;
            double tDeltaZ = localDir.z != 0 ? System.Math.Abs(mortonCellSize / localDir.z) : double.PositiveInfinity;

            double nextBoundaryX = mortonOrigin.x + (cx + (stepX > 0 ? 1 : 0)) * mortonCellSize;
            double nextBoundaryY = mortonOrigin.y + (cy + (stepY > 0 ? 1 : 0)) * mortonCellSize;
            double nextBoundaryZ = mortonOrigin.z + (cz + (stepZ > 0 ? 1 : 0)) * mortonCellSize;

            double tMaxX = localDir.x != 0 ? (nextBoundaryX - pos.x) / localDir.x : double.PositiveInfinity;
            double tMaxY = localDir.y != 0 ? (nextBoundaryY - pos.y) / localDir.y : double.PositiveInfinity;
            double tMaxZ = localDir.z != 0 ? (nextBoundaryZ - pos.z) / localDir.z : double.PositiveInfinity;

            while (currentT <= tEnd)
            {
                ulong code = Morton3D.Encode(cx, cy, cz);
                if (!visited.Contains(code))
                {
                    visited.Add(code);
                    debugCellVisitedCount++;

                    // 二分查找 Morton 区间
                    int lo = BinarySearchMorton(index, code, false);
                    int hi = BinarySearchMorton(index, code, true);

                    for (int j = lo; j < hi; j++)
                    {
                        var part = parts[index[j].partIndex];
                        if (part.isDestroyed) continue;

                        debugPartTestCount++;

                        DVector3 partMin = part.localPosition - part.localBoundsSize * 0.5;
                        DVector3 partMax = part.localPosition + part.localBoundsSize * 0.5;

                        if (CombatGrid.RaySlabIntersection(
                                localOrigin, localOrigin + localDir * maxDist,
                                partMin, partMax,
                                out double ptEntry, out double _, out _))
                        {
                            if (ptEntry >= 0 && ptEntry <= maxDist)
                            {
                                debugHitCount++;
                                results.Add((index[j].partIndex, ptEntry));
                            }
                        }
                    }
                }

                // 走到下一个格子
                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ)
                    {
                        currentT = tMaxX;
                        tMaxX += tDeltaX;
                        cx += stepX;
                    }
                    else
                    {
                        currentT = tMaxZ;
                        tMaxZ += tDeltaZ;
                        cz += stepZ;
                    }
                }
                else
                {
                    if (tMaxY < tMaxZ)
                    {
                        currentT = tMaxY;
                        tMaxY += tDeltaY;
                        cy += stepY;
                    }
                    else
                    {
                        currentT = tMaxZ;
                        tMaxZ += tDeltaZ;
                        cz += stepZ;
                    }
                }
            }

            results.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return results;
        }

        private static int BinarySearchMorton(MortonEntry[] array, ulong code, bool upperBound)
        {
            int lo = 0, hi = array.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                // upperBound=true → 第一个 > code；false → 第一个 ≥ code（lowerBound）
                if (array[mid].code < code || (upperBound && array[mid].code == code))
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref entityId, "entityId");
            Scribe_Values.Look(ref groupId, "groupId");
            Scribe_Values.Look(ref factionId, "factionId");
            Scribe_Values.Look(ref CanInteract, "canInteract", false);
            Scribe_Values.Look(ref IsSelectable, "isSelectable", true);
            Scribe_Values.Look(ref HasMap, "hasMap", false);
            Scribe_Values.Look(ref IsMovable, "isMovable", true);
            Scribe_Values.Look(ref IsAffectedByGravity, "isAffectedByGravity", true);
            DVector3.Scribe("positionMeters", ref positionMeters);
            DVector3.Scribe("velocityMeters", ref velocityMeters);
            Scribe_Values.Look(ref rotation, "rotation");
            Scribe_Values.Look(ref MaxDeltaV, "maxDeltaV", 0f);
            Scribe_Values.Look(ref RemainingDeltaV, "remainingDeltaV", 0f);
            Scribe_Values.Look(ref MapId, "mapId", -1);
            Scribe_Values.Look(ref IsMapArchived, "isMapArchived", false);

            // 部件列表序列化
            Scribe_Collections.Look(ref parts, "parts", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (parts == null) parts = new List<SpacePart>();
                mortonDirty = true;
                mortonIndex = null;
                InvalidateWeapons();
                InvalidateStats();
                cachedWeapons = null;
                foreach (var part in parts)
                {
                    part.ResolveReferences();
                    part.PostLoad();
                    if (part.GetComponent<WeaponMountComponentData>() != null)
                        part.CreateWeaponInstance();
                }
                if (parts.Count > 0)
                    RecalculateBoundsFromParts();
            }
        }
    }
}
