using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// 飞船部件的运行时数据。记录部件在 Entity 本地坐标下的位置、尺寸、生命值，
    /// 以及组件实例数据（引擎/储罐/货舱/舱室等功能由组件承载）。
    /// 坐标使用 DVector3（物理米），存档持久化。
    /// Lite 裁剪：从 Combat 命名空间迁出，作为「空间实体体积/质量」基础层（无战斗语义）；
    /// 武器挂载链路（WeaponMountComponentData）已删除，武器来源改由 Map 炮塔桥接（§5.5）。
    /// </summary>
    public class SpacePart : IExposable
    {
        public int partId;
        public string defName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        public DVector3 positionShip;    // 相对 Entity 原点（物理米）
        public DVector3 boundsSizeShip;  // AABB 尺寸（物理米）
        public float hp = 100f;
        public bool isDestroyed;

        /// <summary>
        /// 不可摧毁（无血条）：空间站等地图载体 Part 置位——存亡由 Map 内建筑决定，
        /// 射弹命中仅做碰撞/贯穿，不扣 Part HP、不触发链式销毁地图。
        /// </summary>
        public bool isIndestructible;

        /// <summary>关联的地图 ID（-1 = 无 Map part，如导弹等纯物理 part）。Part → Map 方向。</summary>
        public int linkedMapId = -1;

        /// <summary>组件实例数据（序列化落盘）。功能判定一律走组件，不再依赖几何形状。</summary>
        public List<PartComponentData> components = new();

        // 舱室类型缓存（不序列化）
        [Unsaved] private RoomTypeDef cachedRoomTypeDef = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）;

        // ── 部件级 stats 热缓存（不序列化）──
        private ShipStats cachedStats;
        private bool statsDirty = true;
        private int statsVersion;

        /// <summary>stats 缓存版本号：失效时递增，供实体级聚合检测变化。</summary>
        public int StatsVersion => statsVersion;

        public SpacePart() { }

        // ════════════════════════════════════════════
        //  组件访问
        // ════════════════════════════════════════════

        /// <summary>获取指定类型组件；不存在返回 null。</summary>
        public T? GetComponent<T>() where T : PartComponentData
        {
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] is T typed)
                    return typed;
            }
            return null;
        }

        public bool HasComponent<T>() where T : PartComponentData => GetComponent<T>() != null;

        /// <summary>
        /// 添加组件（变更入口，自动置脏 stats）。任何"组件增删"必须走这里。
        /// </summary>
        public void AddComponent(PartComponentData c)
        {
            if (c == null) return;
            components.Add(c);
            c.OnAttached();
            InvalidateStats();
        }

        /// <summary>移除指定实例（变更入口，自动置脏 stats）。</summary>
        public bool RemoveComponent(PartComponentData c)
        {
            if (components.Remove(c))
            {
                InvalidateStats();
                return true;
            }
            return false;
        }

        /// <summary>移除指定类型的所有组件（变更入口，自动置脏 stats）。</summary>
        public bool RemoveComponents<T>() where T : PartComponentData
        {
            bool removed = false;
            for (int i = components.Count - 1; i >= 0; i--)
            {
                if (components[i] is T)
                {
                    components.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed)
                InvalidateStats();
            return removed;
        }

        // ════════════════════════════════════════════
        //  stats 聚合（空间换时间：变更路径置脏，热路径只读缓存）
        // ════════════════════════════════════════════

        /// <summary>部件聚合属性（懒计算缓存）。</summary>
        public ShipStats Stats
        {
            get
            {
                if (statsDirty)
                    RecalculateStats();
                return cachedStats;
            }
        }

        /// <summary>部件质量（kg）：Σ组件贡献。未配置时最小 1kg，防除零。</summary>
        public float Mass => Stats.mass;

        private void RecalculateStats()
        {
            cachedStats = default(ShipStats);
            for (int i = 0; i < components.Count; i++)
                components[i].AccumulateStats(ref cachedStats);
            if (cachedStats.mass <= 0f)
                cachedStats.mass = 1f;
            statsDirty = false;
            statsVersion++;
        }

        /// <summary>
        /// stats 失效：置脏 + 版本号递增。所有"组件可能变化"的路径必须走这里。
        /// </summary>
        public void InvalidateStats()
        {
            statsDirty = true;
        }

        // ════════════════════════════════════════════
        //  功能判定（组件驱动）
        // ════════════════════════════════════════════

        /// <summary>是否为舱室部件（带 RoomComponentData 且未摧毁）。</summary>
        public bool IsRoomModulePart
        {
            get
            {
                if (isDestroyed) return false;
                return HasComponent<RoomComponentData>();
            }
        }

        public RoomTypeDef RoomTypeDef
        {
            get
            {
                var room = GetComponent<RoomComponentData>();
                if (room == null) return null;
                if (cachedRoomTypeDef == null && !string.IsNullOrEmpty(room.roomTypeDefName))
                    cachedRoomTypeDef = DefDatabase<RoomTypeDef>.GetNamedSilentFail(room.roomTypeDefName);
                return cachedRoomTypeDef = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
            }
        }

        public void ResolveReferences()
        {
            cachedRoomTypeDef = null;
        }

        // ════════════════════════════════════════════
        //  损伤（组件回调入口，低频）
        // ════════════════════════════════════════════

        /// <summary>
        /// 应用伤害：扣 hp、通知组件 OnDamaged；hp 归零时标记摧毁并通知 OnDestroyed。
        /// 任何组件导致的质量/功能变化都走 InvalidateStats。
        /// </summary>
        public void ApplyDamage(float damage)
        {
            if (isDestroyed || isIndestructible || damage <= 0f) return;

            hp -= damage;
            for (int i = 0; i < components.Count; i++)
                components[i].OnDamaged(damage);
            InvalidateStats();

            if (hp <= 0f)
            {
                isDestroyed = true;
                for (int i = 0; i < components.Count; i++)
                    components[i].OnDestroyed();
                InvalidateStats();
            }
        }

        // ════════════════════════════════════════════
        //  序列化
        // ════════════════════════════════════════════

        public void ExposeData()
        {
            Scribe_Values.Look(ref partId, "partId");
            Scribe_Values.Look(ref defName, "defName");
            DVector3.Scribe("positionShip", ref positionShip);
            DVector3.Scribe("boundsSizeShip", ref boundsSizeShip);
            Scribe_Values.Look(ref hp, "hp", 100f);
            Scribe_Values.Look(ref isDestroyed, "isDestroyed", false);
            Scribe_Values.Look(ref linkedMapId, "linkedMapId", -1);
            Scribe_Collections.Look(ref components, "components", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                InvalidateStats();
        }

        public void PostLoad()
        {
            for (int i = 0; i < components.Count; i++)
                components[i].PostLoad();
            InvalidateStats();
        }
    }
}
