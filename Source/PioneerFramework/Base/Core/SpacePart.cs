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
    public partial class SpacePart : IExposable
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
        public float hp = MaxHp;
        /// <summary>满血值（2026-10-21，维修 R1）：`hp` 的初值与维修上限共用这一个常量。
        /// ⚠ 若将来要"按件区分血量"，改成实例字段并**同时**进 `ExposeData`（现在全是 100，不值得占存档字节）。</summary>
        public const float MaxHp = 100f;

        /// <summary>
        /// **维护值**（0–1，上限 = `hp / MaxHp`；2026-10-22 部件功能化 §11.3 双血条）。
        ///
        /// 与 `hp`（结构值）分工：
        /// · `hp = 0` ⇒ 摧毁（终态，维修拒绝）；
        /// · `condition` 只决定"能不能用 / 效率多少"：`&lt; 0.2` 完全停机，`0.2→1.0` 线性升到满效（见 <see cref="Efficiency"/>）。
        ///
        /// **只由事件改变**（§11.5）：命中（<see cref="ApplyWear"/>）与将来的维护动作；**没有被动磨损、没有任何定时器**。
        /// 新件 = 1.0 ⇒ 聚合里 `贡献 × 1.0` 是精确的 ⇒ 现有读数/确定性判据逐位不变（§11.3 末尾）。
        /// </summary>
        public float condition = 1f;
        public bool isDestroyed;

        // ── §11.1 装甲（2026-10-22）：只影响伤害计算 ──
        /// <summary>**装甲厚度**（mm，0 = 无装甲）。只进 <see cref="ArmorValue"/>，**不参与质量/几何/命中**。</summary>
        public float armorThicknessMm;

        /// <summary>**装甲材料**（Def 引用，null = 无装甲）。装配时从 `ShipPartDef` 抄来；运行时只读。</summary>
        public ArmorMaterialDef? armorMaterial;

        /// <summary>**装甲值**（无量纲）= 厚度(mm) × 材料每毫米防护。只在命中事件读（不进 `ShipStats` 求和）。</summary>
        public float ArmorValue
            => armorMaterial != null && armorThicknessMm > 0f
                ? armorThicknessMm * armorMaterial.protectionPerMm
                : 0f;

        /// <summary>B5：实体级损毁善后（漏光/失能/通知）已执行（不序列化；防残骸再被命中时重复扣池）。</summary>
        internal bool destroyHandled;

        /// <summary>
        /// 不可摧毁（无血条）：空间站等地图载体 Part 置位——存亡由 Map 内建筑决定，
        /// 射弹命中仅做碰撞/贯穿，不扣 Part HP、不触发链式销毁地图。
        /// </summary>
        public bool isIndestructible;

        /// <summary>**part 级碰撞豁免**（碰8）：船-船窄相跳过本件（装饰/传感器杆等），不影响射弹命中/伤害。</summary>
        public bool noCollide;

        /// <summary>B6：**供油组**（0 = 全船主网；切成新组 = **只把该 part 的储罐**切出主网）。
        /// 罐质量仍计入物理质量/COM/惯量，只是不再被主网计划取用；引擎不按此字段分组（仍走地图/实体级聚合）。
        /// 切出的罐在引擎侧分组落地前是“封存/预留”，不是独立供油网。</summary>
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

        // ════════════════════════════════════════════
        //  双血条（§11）：维护值上限与效率曲线
        // ════════════════════════════════════════════

        /// <summary>**结构给维护值的上限**（§11.3）：`hp / MaxHp`，夹在 0–1；`hp ≤ 0` ⇒ 0。
        /// ⇒ 结构被打到 50 ⇒ 维护上限 0.5 ⇒ 效率上限 0.375（结构伤顺带压死上限，是设计耦合不是 bug）。</summary>
        public float ConditionCap
        {
            get
            {
                if (!(hp > 0f)) return 0f;          // 含 hp = 0 / 负数 / NaN
                if (hp >= MaxHp) return 1f;
                return hp / MaxHp;
            }
        }

        /// <summary>
        /// **运转效率**（0–1，§11.3）：`eff = clamp((min(condition, ConditionCap) − 0.2) / 0.8, 0, 1)`。
        /// `condition &lt; 0.2` ⇒ 0（完全停机）；新件走 <c>≥ 1 ⇒ 1.0f</c> 快路径 ⇒ **乘 1.0 精确**
        /// （逐位不变的前提；别把 0.2/0.8 换成"差不多"的常数）。
        /// </summary>
        public float Efficiency
        {
            get
            {
                float cap = ConditionCap;
                float c = condition < cap ? condition : cap;
                if (c >= 1f) return 1f;             // ★ 新件快路径：逐位 1.0（×1.0 = 原值精确）
                if (!(c > 0.2f)) return 0f;         // 含 NaN ⇒ 停机
                float e = (c - 0.2f) / 0.8f;
                return e >= 1f ? 1f : e;
            }
        }

        /// <summary>
        /// **维护值伤害**（§11.2 第二通道 / §11.6）：扣 `condition`（下限 0、上限 `ConditionCap`），
        /// 返回实际扣掉的量。与 <see cref="ApplyDamage"/> 分离：结构伤害不动 `condition`（装甲对它作用），
        /// 维护伤害默认**不吃装甲**（代表震动/烧蚀/过载；否则重甲件永不失修）。
        /// 只在命中事件里调 ⇒ 无每步成本；有变化才 <see cref="InvalidateStats"/>（效率会改功能聚合）。
        /// </summary>
        public float ApplyWear(float amount)
        {
            if (isDestroyed || !(amount > 0f)) return 0f;
            ClampConditionToCap();                  // 先把坏值收进 [0, cap]（含 NaN → 0）
            float before = condition;
            float after = before - amount;
            if (after < 0f) after = 0f;
            if (after == before) return 0f;
            condition = after;
            InvalidateStats();
            return before - after;
        }

        /// <summary>把 `condition` 收进 `[0, ConditionCap]`；NaN / 负值 ⇒ 0（坏数据不给满效）。
        /// 写入口（伤害/磨损/读档）共用，保证"读侧再夹一次"不是唯一防线。</summary>
        private void ClampConditionToCap()
        {
            if (!(condition >= 0f)) { condition = 0f; return; }   // 含 NaN
            float cap = ConditionCap;
            if (condition > cap) condition = cap;
        }

        private void RecalculateStats()
        {
            cachedStats = default(ShipStats);
            // §11.4：整件只取一次效率；**每个组件自己决定哪些贡献乘效率**（质量绝不乘）。
            // 求和顺序仍是"part 内按组件插入顺序" ⇒ 确定性红线不动（§3）。
            float efficiency = Efficiency;
            for (int i = 0; i < components.Count; i++)
                components[i].AccumulateStats(ref cachedStats, efficiency);
            // §5 电闭环：总标称功耗 = 四通道求和（固定顺序 ⇒ 确定性）。
            // ⚠ 组件只能写通道（powerDrawPropulsion/Shield/Weapon/Sensor），**不许**直接写 powerDraw——
            //   直接写会被这一行覆盖，而且分不出优先级。
            cachedStats.powerDraw = cachedStats.powerDrawPropulsion + cachedStats.powerDrawShield
                + cachedStats.powerDrawWeapon + cachedStats.powerDrawSensor;
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
        /// **§11：结构伤害同时压维护上限**（`condition ← min(condition, hp/MaxHp)`）；摧毁时 `condition` 归零。
        /// </summary>
        public void ApplyDamage(float damage)
        {
            if (isDestroyed || isIndestructible || damage <= 0f) return;

            hp -= damage;
            ClampConditionToCap();      // §11.3：结构伤顺带压死维护上限（设计耦合，非 bug）
            for (int i = 0; i < components.Count; i++)
                components[i].OnDamaged(damage);
            InvalidateStats();

            if (hp <= 0f)
            {
                isDestroyed = true;
                condition = 0f;         // §11.6 判据：摧毁 ⇒ 维护值归零
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

        /// <summary>
        /// **命中伤害的两个通道**（§11.2，2026-10-22）：结构走装甲、维护**不吃装甲**。
        ///
        /// 这是**战斗侧唯一该调的入口**（射弹/激光/爆炸都走它）；`ApplyDamage` 仍是纯结构通道
        /// （旧调用点/调试直接杀件不变）。
        /// · 结构：`dmg × clamp(pen/(pen+armor), 0.05, 1)`，无装甲 ⇒ **×1.0 精确**（旧行为逐位不变）；
        /// · 维护：`dmg × k_wear / MaxHp`（`ArmorMath.WearAmount`），与装甲无关。
        ///
        /// 只在命中事件调用（低频）⇒ 零每步成本；装甲值不是求和量 ⇒ 不碰求和顺序红线。
        /// </summary>
        public void ApplyProjectileDamage(float damage, float penetration)
        {
            if (isDestroyed || isIndestructible || !(damage > 0f)) return;

            float factor = ArmorMath.StructuralFactor(penetration, ArmorValue);
            ApplyDamage(damage * factor);              // 结构通道（无装甲时 factor == 1f，逐位不变）
            ApplyWear(ArmorMath.WearAmount(damage));   // 维护通道（不吃装甲）
        }

        /// <summary>
        /// **维修**（2026-10-21，机制缺口第一阶段 R1/R2）：回涨 hp、通知组件、失效 stats。
        ///
        /// 与 <see cref="ApplyDamage"/> 对称，但三处**先判**：
        /// · `isDestroyed` ⇒ **拒绝，返回 0**。真摧毁是**终态**（方案 R2）—— 这条是"维修不是复活"的
        ///   唯一保证；删掉它残骸就会变回好件（变异 **M34** 钉住）；
        /// · `amount &lt;= 0`（含 NaN）⇒ 返回 0；`hp &gt;= MaxHp` ⇒ 返回 0（满血不通知，避免空转）；
        /// · 夹在 `MaxHp` 内，返回值 = **实际**回涨量。
        ///
        /// 代价：只在"有人调它"时发生（事件/玩家指令），**不进 tick**（用户 2026-10-21 红线：不加每步成本）。
        /// </summary>
        public float Repair(float amount)
        {
            if (isDestroyed || !(amount > 0f)) return 0f;
            float before = hp;
            if (before >= MaxHp) return 0f;

            hp = System.Math.Min(before + amount, MaxHp);
            float applied = hp - before;
            for (int i = 0; i < components.Count; i++)
                components[i].OnRepaired(applied);
            InvalidateStats();
            return applied;
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
            Scribe_Values.Look(ref condition, "condition", 1f);   // §11：维护值（新档/旧档缺字段 ⇒ 1）
            Scribe_Values.Look(ref isDestroyed, "isDestroyed", false);
            // §11.1 装甲：材料是 Def 引用（不深拷），厚度是实例量
            Scribe_Defs.Look(ref armorMaterial, "armorMaterial");
            Scribe_Values.Look(ref armorThicknessMm, "armorThicknessMm", 0f);
            Scribe_Values.Look(ref isIndestructible, "isIndestructible", false);   // ★ 修复（2026-10-17 批 -1）：曾漏盘 ⇒ 读档后站体失去不可毁性（可被摧毁并连锁毁图）
            Scribe_Values.Look(ref noCollide, "noCollide", false);                 // 批 3 碰8：part 级船-船窄相豁免
            Scribe_Values.Look(ref fuelGroupId, "fuelGroupId", 0);                // B6：供油组
            Scribe_Values.Look(ref fuelPriority, "fuelPriority", (byte)0);          // B6：燃料优先级
            Scribe_Values.Look(ref linkedMapId, "linkedMapId", -1);
            Scribe_Values.Look(ref isSealed, "isSealed", false);   // B6：封闭舱不显示浮雕 + 100kg 占位
            Scribe_Collections.Look(ref components, "components", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                ClampConditionToCap();   // §11：读档后维护值不得高于结构上限
                InvalidateStats();
            }
        }

        public void PostLoad()
        {
            ClampConditionToCap();
            for (int i = 0; i < components.Count; i++)
                components[i].PostLoad();
            InvalidateStats();
        }
    }
}
