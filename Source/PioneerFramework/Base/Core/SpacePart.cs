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
        /// <summary>B3：零件自身在船体系里的姿态（默认 identity）。碰撞/命中/惯量/渲染共用。</summary>
        public DQuaternion rotationShip = DQuaternion.identity;
        /// <summary>B3：形状预设（Box/Cylinder/Sphere）；`PartInertiaComponent` 存在时以组件为准。</summary>
        public InertiaTensor.Shape shape = InertiaTensor.Shape.Box;
        /// <summary>B3：分级预留（只存不用；分级行为后置）。</summary>
        public int stageId;
        /// <summary>B3：挂接父件 id（0 = 根；预留）。</summary>
        public int parentId;
        public float hp = 100f;
        public bool isDestroyed;
        /// <summary>B5：实体级损毁善后（漏光/失能/通知）已执行（不序列化；防残骸再被命中时重复扣池）。</summary>
        internal bool destroyHandled;

        /// <summary>
        /// 不可摧毁（无血条）：空间站等地图载体 Part 置位——存亡由 Map 内建筑决定，
        /// 射弹命中仅做碰撞/贯穿，不扣 Part HP、不触发链式销毁地图。
        /// </summary>
        public bool isIndestructible;

        /// <summary>**part 级碰撞豁免**（碰8）：船-船窄相跳过本件（装饰/传感器杆等），不影响射弹命中/伤害。</summary>
        public bool noCollide;

        /// <summary>B6：**供油组**（0 = 全船主网；切成新组 = 该 part 的罐/引擎脱离主网）。
        /// 罐质量仍计入物理质量/COM/惯量，只是不再被该组之外的引擎取用。</summary>
        public int fuelGroupId;
        /// <summary>B6：**燃料优先级层级**（0 = 无优先级，按剩余质量比例；1..N 低层先烧）。
        /// 消费者改这里后需通知燃料计划重建（见 `FuelPlan`）；纯数据层，不参与 tick。</summary>
        public byte fuelPriority;

        /// <summary>关联的地图 ID（-1 = 无 Map part，如导弹等纯物理 part）。Part → Map 方向。</summary>
        public int linkedMapId = -1;

        /// <summary>
        /// **B6：地图载体是否封闭**。封闭舱**不显示地图浮雕**（只画盒体）；非封闭舱显示浮雕。
        /// 重量口径以后会按封闭/非封闭分开算 —— **当前先 +`SealedPlaceholderMassKg`（100 kg）占位**
        /// （这是占位值，不是最终封闭壳重量；改口径时只动这里与 `RecalculateStats`）。
        /// 习惯默认（**不是机制**）：单 part 船开放、多 part 船封闭；可按件覆写。
        /// ⚠ 运行中改本字段后必须调 `InvalidateStats()`（质量缓存才会重算）。
        /// </summary>
        public bool isSealed;
        public const float SealedPlaceholderMassKg = 100f;

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

        /// <summary>移除指定实例（变更入口，自动置脏 stats；组件收到 OnDetached）。</summary>
        public bool RemoveComponent(PartComponentData c)
        {
            if (components.Remove(c))
            {
                c.OnDetached();
                InvalidateStats();
                return true;
            }
            return false;
        }

        /// <summary>移除指定类型的所有组件（变更入口，自动置脏 stats；组件收到 OnDetached）。</summary>
        public bool RemoveComponents<T>() where T : PartComponentData
        {
            bool removed = false;
            for (int i = components.Count - 1; i >= 0; i--)
            {
                if (components[i] is T)
                {
                    components[i].OnDetached();
                    components.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed)
                InvalidateStats();
            return removed;
        }

        /// <summary>part 从实体移除时调用：所有组件收到 OnDetached（不改变组件列表本身）。</summary>
        public void DetachComponents()
        {
            for (int i = 0; i < components.Count; i++)
                components[i]?.OnDetached();
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
            // B6 占位：封闭壳的重量口径未定，先固定 +100 kg（见 isSealed 注释）。
            if (isSealed)
                cachedStats.mass += SealedPlaceholderMassKg;
            // B8：**part 不再逐个兜底 1kg**；空 part 质量 = 0，整船质量 0 时才在实体层兜底 1kg。
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
                return cachedRoomTypeDef!;   // ★ 修复（2026-10-17 批 -1）：原为 `cachedRoomTypeDef = null!`（恒返回 null ⇒ 舱室类型/生成器选择全失效）；Def 缺失时仍为 null，消费方已判空
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
                NotifyDestroyed();
                InvalidateStats();
            }
        }

        /// <summary>通知组件“已损毁”（唯一收口：ApplyDamage 与 SpaceEntity.MarkPartDestroyed 共用）。
        /// 调用方负责保证只调一次（`isDestroyed` 由 false→true 的那一次）。</summary>
        internal void NotifyDestroyed()
        {
            for (int i = 0; i < components.Count; i++)
                components[i].OnDestroyed();
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
            DQuaternion.Scribe("rotationShip", ref rotationShip);
            Scribe_Values.Look(ref shape, "shape", InertiaTensor.Shape.Box);
            Scribe_Values.Look(ref stageId, "stageId", 0);
            Scribe_Values.Look(ref parentId, "parentId", 0);
            Scribe_Values.Look(ref hp, "hp", 100f);
            Scribe_Values.Look(ref isDestroyed, "isDestroyed", false);
            Scribe_Values.Look(ref isIndestructible, "isIndestructible", false);   // ★ 修复（2026-10-17 批 -1）：曾漏盘 ⇒ 读档后站体失去不可毁性（可被摧毁并连锁毁图）
            Scribe_Values.Look(ref noCollide, "noCollide", false);                 // 批 3 碰8：part 级船-船窄相豁免
            Scribe_Values.Look(ref fuelGroupId, "fuelGroupId", 0);                // B6：供油组
            Scribe_Values.Look(ref fuelPriority, "fuelPriority", (byte)0);          // B6：燃料优先级
            Scribe_Values.Look(ref linkedMapId, "linkedMapId", -1);
            Scribe_Values.Look(ref isSealed, "isSealed", false);   // B6：封闭舱不显示浮雕 + 100kg 占位
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
