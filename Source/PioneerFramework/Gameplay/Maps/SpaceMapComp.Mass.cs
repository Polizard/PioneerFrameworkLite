using System;
using PioneerFramework.Components;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// <see cref="SpaceMapComp"/> 的**Map 质量维护**（2026-10-17 用户裁定）：
    ///
    /// **Map 的质量 = 建筑质量 + 屋顶质量 + 工质质量**。变化源：
    ///   · 建筑生成/拆除（`ThingSpawned`/`ThingDespawned`，只认 `Building`）；
    ///   · 屋顶覆盖/拆除（`RoofChanged`，按格计）；
    ///   · 工质加注/消耗（`TotalPropellantMass` 已由储罐与虚拟推进实时维护）。
    /// **不知道多重 ⇒ 1 kg**：建筑质量读 `StatDefOf.Mass`，≤0/NaN 时按 1 kg/件；
    /// `RoofDef` 没有质量字段 ⇒ 1 kg/格（`RoofCellMassKg`）。
    ///
    /// 落地方式：**结构质量（建筑+屋顶）写进挂图舱盒 part 的 `HullMassComponent`**
    /// （取代旧的 `mapW×50×mapH×90` 站体公式）；工质仍按既有口径在物理侧 `干质量+工质` 使用，
    /// `MapMassKg` 给出完整 Map 质量读数。事件只做 O(1) 增量；全量重算只发生在
    /// 读档/首次绑定（`RecomputeStructureMass`）。
    /// </summary>
    public partial class SpaceMapComp
    {
        /// <summary>屋顶单位面积质量（kg/格）：`RoofDef` 无质量字段，按用户裁定默认 1 kg。</summary>
        public const double RoofCellMassKg = 1.0;

        /// <summary>未知建筑质量兜底（kg/件）。</summary>
        public const double UnknownBuildingMassKg = 1.0;

        private double buildingMassKg;
        private int roofedCellCount;
        private bool[]? roofCounted;
        private SpaceEntity? ownerEntity;

        /// <summary>结构质量（建筑 + 屋顶，不含工质）。</summary>
        public double StructureMassKg => buildingMassKg + roofedCellCount * RoofCellMassKg;

        /// <summary>完整 Map 质量（结构 + 当前工质）；工质加注/消耗直接反映在这里。</summary>
        public double MapMassKg => StructureMassKg + TotalPropellantMass;

        public double BuildingMassKg => buildingMassKg;
        public int RoofedCellCount => roofedCellCount;
        public double MapPropellantMassKg => TotalPropellantMass;

        /// <summary>读数快照（建筑 / 屋顶格数 / 工质 / 总质量），供 UI 与一键验收。</summary>
        public (double building, int roofCells, double propellant, double total) MapMassSnapshot()
            => (buildingMassKg, roofedCellCount, TotalPropellantMass, MapMassKg);

        /// <summary>单件建筑质量：读 Mass 属性；未知/非正/非有限 ⇒ 1 kg。</summary>
        internal static double BuildingMassKgOf(Thing? thing)
        {
            if (thing?.def == null) return UnknownBuildingMassKg;
            float m = thing.GetStatValue(StatDefOf.Mass);
            if (float.IsNaN(m) || float.IsInfinity(m) || m <= 0f) return UnknownBuildingMassKg;
            return m;
        }

        /// <summary>只维护口袋图（船内/站内）的质量；行星地表图不做无谓扫描。</summary>
        private bool TracksMapMass => map.IsPocketMap;

        // ── 事件入口（任意地图安全：没有 SpaceMapComp / 非口袋图什么都不做）──

        public static void NotifyBuildingSpawned(Map? map, Thing? thing)
        {
            if (map == null || thing is not Building) return;
            var comp = map.GetComponent<SpaceMapComp>();
            if (comp == null || !comp.TracksMapMass) return;
            comp.AddBuildingMass(thing, +1);
        }

        public static void NotifyBuildingDespawned(Map? map, Thing? thing)
        {
            if (map == null || thing is not Building) return;
            var comp = map.GetComponent<SpaceMapComp>();
            if (comp == null || !comp.TracksMapMass) return;
            comp.AddBuildingMass(thing, -1);
        }

        public static void NotifyRoofChanged(Map? map, IntVec3 cell)
        {
            if (map == null) return;
            var comp = map.GetComponent<SpaceMapComp>();
            if (comp == null || !comp.TracksMapMass) return;
            comp.OnRoofChanged(cell);
        }

        private void AddBuildingMass(Thing thing, int sign)
        {
            buildingMassKg = Math.Max(0.0, buildingMassKg + sign * BuildingMassKgOf(thing));
            ApplyStructureMassToOwner();
        }

        private void OnRoofChanged(IntVec3 cell)
        {
            if (!cell.InBounds(map)) return;
            EnsureRoofTable();
            int idx = cell.z * map.Size.x + cell.x;
            bool has = map.roofGrid.RoofAt(cell) != null;
            if (has == roofCounted![idx]) return;
            roofCounted[idx] = has;
            roofedCellCount += has ? 1 : -1;
            ApplyStructureMassToOwner();
        }

        private void EnsureRoofTable()
        {
            int n = map.Size.x * map.Size.z;
            if (roofCounted != null && roofCounted.Length == n) return;
            RebuildRoofTable();
        }

        private void RebuildRoofTable()
        {
            int sizeX = map.Size.x, sizeZ = map.Size.z;
            roofCounted = new bool[sizeX * sizeZ];
            int count = 0;
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    if (map.roofGrid.RoofAt(new IntVec3(x, 0, z)) != null)
                    {
                        roofCounted[z * sizeX + x] = true;
                        count++;
                    }
                }
            }
            roofedCellCount = count;
        }

        /// <summary>从地图实际状态全量重算（读档 / 首次绑定 / 事件可能缺失的批处理之后）。</summary>
        public void RecomputeStructureMass()
        {
            buildingMassKg = 0.0;
            var things = map.listerThings?.AllThings;
            if (things != null)
            {
                for (int i = 0; i < things.Count; i++)
                {
                    var t = things[i];
                    if (t is Building && !t.Destroyed)
                        buildingMassKg += BuildingMassKgOf(t);
                }
            }
            RebuildRoofTable();
            ApplyStructureMassToOwner();
        }

        /// <summary>
        /// 图 ↔ part 绑定（`LinkEntityToMap` 调入；实体此时可能尚未 Register）。
        /// **B6**：partId ≥ 0 显式指定；-1 时按 `linkedMapId == 本图` 反查，再退回第一个未挂图 part。
        /// </summary>
        public void BindOwner(SpaceEntity entity, int partId = -1)
        {
            ownerEntity = entity;
            ownerEntityId = entity.EntityId;
            if (partId >= 0)
            {
                ownerPartId = partId;
            }
            else if (ownerPartId < 0)
            {
                var linked = entity.GetPartForMap(map.uniqueID);
                if (linked != null)
                {
                    ownerPartId = linked.partId;
                }
                else
                {
                    var parts = entity.Parts;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        var p = parts[i];
                        if (p != null && !p.isDestroyed && p.linkedMapId < 0)
                        {
                            ownerPartId = p.partId;
                            break;
                        }
                    }
                }
            }
            RecomputeStructureMass();
            SyncOwnerTankFuelFromPools();   // B4a：绑定后把既有工质池分布到 tank part
        }

        /// <summary>本图对应的 part（B6：ownerPartId 反查；未绑定/part 已被移除时 null）。</summary>
        public SpacePart? OwnerPart
        {
            get
            {
                var entity = ResolveOwner();
                return entity != null && ownerPartId >= 0 ? entity.GetPartById(ownerPartId) : null;
            }
        }

        private SpaceEntity? ResolveOwner()
        {
            if (ownerEntity != null) return ownerEntity;
            if (ownerEntityId < 0) return null;
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            ownerEntity = sg?.GetEntityById(ownerEntityId);
            return ownerEntity;
        }

        /// <summary>把结构质量写进挂图 part（`linkedMapId == 本图`），并让实体/群级缓存失效。</summary>
        private void ApplyStructureMassToOwner()
        {
            var entity = ResolveOwner();
            if (entity == null) return;

            float mass = (float)Math.Max(StructureMassKg, 1.0);   // 空图也至少 1kg（part 层亦有兜底）
            bool changed = false;
            var parts = entity.Parts;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null) continue;
                // B6：优先按 Part→Map 指针匹配（一图一件）；旧绑定的 ownerPartId 仅在该件尚未挂图时兜底。
                bool isOwnerPart = part.linkedMapId == map.uniqueID
                    || (ownerPartId >= 0 && part.partId == ownerPartId && part.linkedMapId < 0);
                if (!isOwnerPart) continue;
                var hull = part.GetComponent<HullMassComponent>();
                if (hull == null) continue;
                if (Math.Abs(hull.mass - mass) < 1e-9) continue;
                hull.mass = mass;
                part.InvalidateStats();   // 部件 stats 置脏
                changed = true;
            }
            if (changed)
                entity.InvalidateStats(); // 实体聚合 + partSetVersion（群级惯量/信号随之失效）
        }
    }
}
