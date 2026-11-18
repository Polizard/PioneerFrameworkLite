using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using PioneerFramework.Components;

namespace PioneerFramework.Combat
{
    /// <summary>
    /// 飞船部件的运行时数据。记录部件在 Entity 本地坐标下的位置、尺寸、生命值，
    /// 以及组件实例数据（引擎/储罐/货舱/武器/舱室等功能由组件承载）。
    /// 坐标使用 DVector3（物理米），存档持久化。
    /// </summary>
    public class SpacePart : IExposable
    {
        public int partId;
        public string defName;
        public DVector3 localPosition;    // 相对 Entity 原点（物理米）
        public DVector3 localBoundsSize;  // AABB 尺寸（物理米），由 ShipPartDef.bounds * 10 转换
        public float hp = 100f;
        public bool isDestroyed;

        /// <summary>关联的地图 ID（-1 = 不是舱室部件）。Part → Map 方向。</summary>
        public int linkedMapId = -1;

        /// <summary>组件实例数据（序列化落盘）。功能判定一律走组件，不再依赖几何形状。</summary>
        public List<PartComponentData> components = new List<PartComponentData>();

        // 不序列化，PostLoad 时重建
        [Unsaved] public ShipPartDef cachedDef;

        // 舱室类型缓存（不序列化）
        [Unsaved] private RoomTypeDef cachedRoomTypeDef;

        // 运行时武器实例引用（不序列化）
        [Unsaved] public WeaponInstance weaponInstance;

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
        public T GetComponent<T>() where T : PartComponentData
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

        /// <summary>部件质量（kg）：massBase + Σ组件贡献。未配置时最小 1kg，防除零。</summary>
        public float Mass => Stats.mass;

        private void RecalculateStats()
        {
            cachedStats = default(ShipStats);
            if (cachedDef != null)
                cachedStats.mass = cachedDef.massBase;
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
                return cachedRoomTypeDef;
            }
        }

        public void ResolveReferences()
        {
            cachedDef = ShipPartDefs.GetDef(defName);
            if (cachedDef == null)
                Log.Warning($"[SpacePart] Part def '{defName}' not found for part {partId}");

            cachedRoomTypeDef = null;
        }

        // ════════════════════════════════════════════
        //  武器
        // ════════════════════════════════════════════

        /// <summary>部件未指定武器名（或名字无效）时回退的默认武器 Def。</summary>
        public const string DefaultWeaponDefName = "PF_Weapon_Default";

        /// <summary>
        /// 为此部件创建武器实例。仅当存在 WeaponMountComponentData 时调用。
        /// 未填写武器名或名字无效时回退到默认武器（PF_Weapon_Default）。
        /// </summary>
        public WeaponInstance CreateWeaponInstance()
        {
            var mount = GetComponent<WeaponMountComponentData>();
            if (mount == null) return null;

            var weaponDef = string.IsNullOrEmpty(mount.weaponDefName)
                ? null
                : DefDatabase<WeaponDef>.GetNamedSilentFail(mount.weaponDefName);

            if (weaponDef == null)
            {
                if (!string.IsNullOrEmpty(mount.weaponDefName))
                    Log.Warning($"[SpacePart] WeaponDef '{mount.weaponDefName}' not found for part {partId}，回退默认武器");

                weaponDef = DefDatabase<WeaponDef>.GetNamedSilentFail(DefaultWeaponDefName);
                if (weaponDef == null)
                {
                    Log.Warning($"[SpacePart] 默认武器 '{DefaultWeaponDefName}' not found for part {partId}");
                    return null;
                }
            }

            weaponInstance = new WeaponInstance
            {
                def = weaponDef,
                mountPartIndex = partId,
            };
            return weaponInstance;
        }

        /// <summary>
        /// 当部件被摧毁时禁用其武器。
        /// </summary>
        public void DisableWeapon()
        {
            if (weaponInstance != null)
                weaponInstance.Disable();
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
            if (isDestroyed || damage <= 0f) return;

            hp -= damage;
            for (int i = 0; i < components.Count; i++)
                components[i].OnDamaged(damage);
            InvalidateStats();

            if (hp <= 0f)
            {
                isDestroyed = true;
                DisableWeapon();
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
            DVector3.Scribe("localPosition", ref localPosition);
            DVector3.Scribe("localBoundsSize", ref localBoundsSize);
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
