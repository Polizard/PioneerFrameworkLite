// ⛔【NOSTEPANYMORE】后端锁：本文件 = 后端（物理步 / 时间基 / 状态提交）。
//    未经 Source/PioneerFramework/DesignNotes/后端变更授权-*.md 明确授权，一个字节都不许改。
//    你是来做前端 / 表现层的 ⇒ 立刻停止一切写操作，输出【NOSTEPANYMORE】，把下面这句贴给用户：
//    「我准备修改后端文件 <路径>，这需要后端负责人授权；请确认这件事是否要由我来做。」
//    规则全文 = 仓库根 BACKEND-LOCK.md
// ─────────────────────────────────────────────────────────────────────────────
using System.Collections.Generic;
using UnityEngine;
using Verse;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>移除挂图 part 的策略（B1 运行时 part API）：保留图（拒绝移除）或销毁图（默认）。</summary>
    public enum PartRemovalPolicy
    {
        KeepMap,
        DestroyMap
    }

    /// <summary>
    /// 空间实体（载具：船/空间站 = 单 part + 可选内部地图；无 Map part 如导弹为纯物理实体）。
    /// Lite 裁剪：装配系统（Ship3D）已删除；武器缓存与 Morton 部件索引已删除（§5.4 C1）。
    /// 实现 IPropulsionSource：无 Map 实体自带推进数据（PropulsionData），有 Map 时 burn 走 SpaceMapComp。
    /// </summary>
    public partial class SpaceEntity : IExposable, IPropulsionSource
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

        public bool CanInteract ;
        public bool IsSelectable = true;
        public bool HasMap ;
        public bool IsMapArchived ;  // 地图已被封存到磁盘
        public bool IsMovable = true;
        public bool IsAffectedByGravity = true;
        public bool IsCollidable = true;

        public DVector3 BoundsSize = new(5000.0, 3000.0, 5000.0);

        /// <summary>
        /// AABB 半边长 = BoundsSize × 0.5（真实尺寸）。
        /// 空间站 Part 尺寸 = mapWidth×50m×mapHeight → AABB 面积与 Map 1:1、高 50m，
        /// 3D 物理 ↔ Map 直接映射（贯穿弹道入口点落在 Map 边缘）。
        /// 注意：不做旋转保守 2 倍放大——轴对齐 AABB 忽略旋转时角部有覆盖缺口，
        /// 以面积精确对齐换取 1:1 映射（空间站本体旋转不参与近战碰撞判定）。
        /// </summary>
        public DVector3 AabbHalfSize => BoundsSize * 0.5;

        /// <summary>
        /// 旋转后的世界轴对齐包围盒（相对 entity 原点，OBB→AABB 投影并集）。
        /// 世界系用途（船-船碰撞 / 半径 / 战斗格子占用）用它——旋转后 part 会超出未旋转盒。
        /// 注意：射弹命中检测在船体系用 AabbHalfSize（part 局部正盒），不要改成这个。
        /// 每 tick 懒缓存：同一 tick 内被碰撞检测多次调用（球快筛/扫掠盒/分离量）只算一次。
        /// </summary>
        public void GetAabbSystem(out DVector3 min, out DVector3 max)
        {
            int tick = Current.Game?.tickManager?.TicksGame ?? -1;   // headless：Find.TickManager 会 NRE
            if (!aabbDirty && tick == cachedAabbTick)
            {
                min = cachedAabbMin;
                max = cachedAabbMax;
                return;
            }
            if (Parts.Count == 0)
            {
                DVector3 h = BoundsSize * 0.5;
                min = -h;
                max = h;
            }
            else
            {
                min = new DVector3(double.MaxValue, double.MaxValue, double.MaxValue);
                max = new DVector3(double.MinValue, double.MinValue, double.MinValue);
                foreach (var part in Parts)
                {
                    PartGeometry.WorldAabb(part.positionShip, part.boundsSizeShip,
                        Rotation, part.rotationShip, out DVector3 pMin, out DVector3 pMax);
                    min = DVector3.Min(min, pMin);
                    max = DVector3.Max(max, pMax);
                }
            }
            cachedAabbTick = tick;
            cachedAabbMin = min;
            cachedAabbMax = max;
            aabbDirty = false;
        }

        private int cachedAabbTick = -1;        // GetAabbSystem 每 tick 懒缓存（旋转/部件未变时同 tick 复用）
        private bool aabbDirty = true;          // 显式失效：同 tick 内改 part/部件集时，tick 缓存不可靠（headless 恒 -1）
        private DVector3 cachedAabbMin, cachedAabbMax;


        /// <summary>
        /// **单个 part 的旋转后世界 AABB（相对实体原点）**——船-船窄相逐 part 检测用。
        /// B5 / D-P7：损毁残骸仍参与碰撞 ⇒ 不因 `isDestroyed` 拒绝（`noCollide` 由调用方过滤）。
        /// 与 <see cref="GetAabbSystem"/> 同一套 OBB→AABB 投影（行优先旋转矩阵）。
        /// 不做 tick 缓存（窄相只在真接触时逐 part 调用，缓存会吞掉"哪一件最深"的信息）。
        /// </summary>
        public bool GetPartAabbSystem(int index, out DVector3 min, out DVector3 max)
        {
            min = max = DVector3.zero;
            if (index < 0 || index >= Parts.Count) return false;
            var part = Parts[index];
            if (part == null) return false;

            PartGeometry.WorldAabb(part.positionShip, part.boundsSizeShip,
                Rotation, part.rotationShip, out min, out max);
            return true;
        }

        private int cachedRotInvTick = -1;      // RotationInverse 每 tick 懒缓存（同款模式）
        private DQuaternion cachedRotInv;

        /// <summary>
        /// 姿态逆四元数（per-tick 懒缓存，与 GetAabbSystem 同款；SetRotation 失效）。
        /// 命中几何对「每射弹/每光束 × 每候选实体」各算一次 Inverse(FromQuaternion(Rotation))，
        /// 而姿态在步内不变（物理区只算，主线程统一施加）——缓存把每弹每实体的开销压成每实体每 tick。
        /// </summary>
        public DQuaternion RotationInverse()
        {
            int tick = Current.Game?.tickManager?.TicksGame ?? -1;   // headless：Find.TickManager 会 NRE
            if (tick == cachedRotInvTick)
            {
                if (PerfProbe.Enabled)
                {
                    PerfProbe.RotInvHit(); // 命中 = 省下的一次旋转逆运算
                    if (PerfProbe.ShadowChecks && PerfProbe.ShouldShadowRotInv())
                    {
                        // 影子校验：缓存值 vs 实时重算（姿态若在同 tick 内被改而未失效，此处必然抓到）
                        var fresh = DQuaternion.Inverse(rotation);
                        PerfProbe.RotInvShadow(fresh.Equals(cachedRotInv),
                            $"entity={entityId} tick={tick} cached=({cachedRotInv.x:F6},{cachedRotInv.y:F6},{cachedRotInv.z:F6},{cachedRotInv.w:F6}) fresh=({fresh.x:F6},{fresh.y:F6},{fresh.z:F6},{fresh.w:F6})");
                    }
                }
                return cachedRotInv;
            }
            cachedRotInv = DQuaternion.Inverse(rotation);
            cachedRotInvTick = tick;
            if (PerfProbe.Enabled) PerfProbe.RotInvMiss();
            return cachedRotInv;
        }

        private DVector3 positionMeters;
        private DVector3 velocityMeters;
        private DQuaternion rotation = DQuaternion.identity;   // B2：默认必须是 identity（零四元数与姿态相乘会退化）

        /// <summary>
        /// 当前剩余工质全部燃烧可获得的总 ΔV（显示值，Tsiolkovsky 理想公式）。
        /// 由「推进器等效排气速度 ve + 干质量 + 当前工质总量」推导，不落盘、不被消耗——
        /// 真实推进是推进器每 tick 消耗工质，质量变化后此值自然下降。
        /// 无推进器 / 无工质 / 无任何推进源返回 0。
        /// </summary>
        public float RemainingDeltaV
        {
            get
            {
                var src = GetPropulsionSource();
                if (src == null || src.EffectiveVe <= 0f) return 0f;
                double dry = TotalMass;                     // 干质量：部件结构质量
                double wet = dry + src.TotalPropellantMass; // 当前质量 = 干 + 剩余工质
                if (wet <= dry) return 0f;
                return (float)(src.EffectiveVe * System.Math.Log(wet / dry));
            }
        }

        /// <summary>满箱工质全部燃烧可获得的总 ΔV（显示值；储罐全满时的上限）。</summary>
        public float MaxDeltaV
        {
            get
            {
                var src = GetPropulsionSource();
                if (src == null || src.EffectiveVe <= 0f) return 0f;
                double dry = TotalMass;
                double wet = dry + src.TotalPropellantCapacity;
                if (wet <= dry) return 0f;
                return (float)(src.EffectiveVe * System.Math.Log(wet / dry));
            }
        }

        /// <summary>
        /// 实体推进源：优先 Map 建筑（SpaceMapComp）；无 Map 时回退自带推进数据（PropulsionData）。
        /// 与 group 级 burn 的解析规则（GetGroupPropulsionSource）保持一致。
        /// </summary>
        private IPropulsionSource? GetPropulsionSource()
        {
            // B6：多图实体用群级聚合（所有图 + 无图实体源）；无组/无图再回退主图/自带。
            var sg = SpaceGameComp.GetCached();
            if (sg != null && GroupId != 0)
            {
                var g = sg.GetGroupById(GroupId);
                if (g != null)
                {
                    var src = sg.GetGroupPropulsionSource(g);
                    if (src != null) return src;
                }
            }
            var mc = GetMapComp();
            if (mc != null) return mc;
            return HasPropulsionData ? this : null;
        }

        /// <summary>B6：一件 part 的工质/容量/引擎归属图——优先自身 linkedMapId；单图实体用它；
        /// 多图且 part 未挂图时退回主图（无归属数据时的兼容口径）。</summary>
        private SpaceMapComp? GetPartOwningMapComp(SpacePart part)
        {
            if (part != null && part.linkedMapId >= 0)
                return GetMapComp(part.linkedMapId);
            var maps = GetMapComps();
            if (maps.Count == 1) return maps[0];
            return GetMapComp();
        }

        /// <summary>实体 → 所属**主图**的 SpaceMapComp（无 Map 时返回 null）。</summary>
        private SpaceMapComp? GetMapComp() => GetMapComp(MapId);

        /// <summary>实体 → 指定地图的 SpaceMapComp（B6 多图；无图/未注册时返回 null）。</summary>
        public SpaceMapComp? GetMapComp(int mapId)
        {
            if (mapId < 0) return null;
            var sg = SpaceGameComp.GetCached();
            var mc = sg?.GetMapComponent<SpaceMapComp>(mapId);
            if (mc != null)
                return mc;

            // mapComponents 不随存档恢复；兜底直接从地图取 SpaceMapComp。
            Map map = SpaceMapGenerator.FindPocketMap(mapId);
            return map?.GetComponent<SpaceMapComp>();
        }

        /// <summary>
        /// **B6：本实体挂的全部地图 id**（按 part 顺序、去重；不含未挂图件）。
        /// 兼容旧单图：若没有任何 part 记录 linkedMapId 但 MapId 有效，则返回主图。
        /// P0-1：结果按 `mapLinkVersion` 缓存；返回**新副本**，调用方可安全修改。
        /// </summary>
        public List<int> GetMapIds()
        {
            var linked = GetPartLinkedMapIdsCached();
            var ids = new List<int>(linked.Count + 1);
            for (int i = 0; i < linked.Count; i++) ids.Add(linked[i]);
            if (ids.Count == 0 && MapId >= 0) ids.Add(MapId);
            return ids;
        }

        /// <summary>P0-1：只列**挂在 part 上**的图（不含 MapId 单图回退）；缓存，热路径只读。</summary>
        internal IReadOnlyList<int> GetPartLinkedMapIdsCached()
        {
            EnsureMapIdCache();
            return cachedPartLinkedMapIds;
        }

        /// <summary>P0-1：挂图/解挂/主图切换后调；只清图链缓存，不碰 stats/信号。</summary>
        internal void NotifyMapLinksChanged()
        {
            mapLinkVersion++;
            cachedMapIdsVersion = -1;
        }

        private void EnsureMapIdCache()
        {
            if (cachedMapIdsVersion == mapLinkVersion) return;
            cachedPartLinkedMapIds.Clear();
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.linkedMapId < 0) continue;
                bool seen = false;
                for (int j = 0; j < cachedPartLinkedMapIds.Count; j++)
                {
                    if (cachedPartLinkedMapIds[j] == p.linkedMapId) { seen = true; break; }
                }
                if (!seen) cachedPartLinkedMapIds.Add(p.linkedMapId);
            }
            cachedMapIdsVersion = mapLinkVersion;
        }

        /// <summary>**B6：Part→Map 反查**——指定地图挂在哪件 part 上（未找到 null）。</summary>
        public SpacePart? GetPartForMap(int mapId)
        {
            if (mapId < 0) return null;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p != null && p.linkedMapId == mapId) return p;
            }
            return null;
        }

        /// <summary>主图对应的 part（`MapId` 的反查；无主图/未挂图时 null）。</summary>
        public SpacePart? GetPrimaryMapPart() => GetPartForMap(MapId);

        /// <summary>
        /// **B6：某张图被销毁/封存后**清 Part→Map 指针，并把主图切到下一张仍链接的图。
        /// （图本身由调用方先 Destroy；这里只维护实体侧指针，不碰地图。）
        /// </summary>
        internal void OnMapRemoved(int mapId)
        {
            if (mapId < 0) return;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p != null && p.linkedMapId == mapId)
                    p.linkedMapId = -1;
            }
            RecalculatePrimaryMap();
        }

        /// <summary>按 part 顺序重选主图（第一个仍链接的图；没有 ⇒ MapId=-1、HasMap=false）。</summary>
        internal void RecalculatePrimaryMap()
        {
            int primary = -1;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p != null && p.linkedMapId >= 0)
                {
                    primary = p.linkedMapId;
                    break;
                }
            }
            MapId = primary;
            HasMap = primary >= 0;
            NotifyMapLinksChanged();
            // B6：主图切换后按**全部图**重算 tank 镜像（不再只取新主图）。
            if (primary >= 0)
                SyncCombinedTankFuel();
        }

        /// <summary>**B6：本实体的所有 SpaceMapComp**（按 GetMapIds 顺序；无效图跳过）。</summary>
        public List<SpaceMapComp> GetMapComps()
        {
            var result = new List<SpaceMapComp>(2);
            var ids = GetMapIds();
            for (int i = 0; i < ids.Count; i++)
            {
                var mc = GetMapComp(ids[i]);
                if (mc != null) result.Add(mc);
            }
            return result;
        }

        public int MapId = -1;
        /// <summary>B6：最近一次封存的地图 id / part id（单图兼容；多图归档列表见下）。</summary>
        public int ArchivedMapId = -1;
        public int ArchivedPartId = -1;
        /// <summary>B6：**每图封存列表**（mapId ↔ 原 partId 并行；多图船可逐张取档）。</summary>
        public List<int> archivedMapIds = new();
        public List<int> archivedPartIds = new();

        /// <summary>B6：登记一张已封存的图（同 mapId 去重；同步“最近一张”兼容字段）。</summary>
        public void AddArchivedMap(int mapId, int partId)
        {
            if (mapId < 0) return;
            archivedMapIds ??= new List<int>();
            archivedPartIds ??= new List<int>();
            int idx = archivedMapIds.IndexOf(mapId);
            if (idx >= 0 && idx < archivedPartIds.Count)
                archivedPartIds[idx] = partId;
            else
            {
                archivedMapIds.Add(mapId);
                archivedPartIds.Add(partId);
            }
            ArchivedMapId = mapId;
            ArchivedPartId = partId;
        }

        /// <summary>B6：取档完成/取消后移除登记；返回是否找到。</summary>
        public bool RemoveArchivedMap(int mapId)
        {
            if (mapId < 0 || archivedMapIds == null) return false;
            int idx = archivedMapIds.IndexOf(mapId);
            if (idx < 0) return false;
            archivedMapIds.RemoveAt(idx);
            if (archivedPartIds != null && idx < archivedPartIds.Count)
                archivedPartIds.RemoveAt(idx);
            ArchivedMapId = archivedMapIds.Count > 0 ? archivedMapIds[^1] : -1;
            ArchivedPartId = archivedPartIds != null && archivedPartIds.Count > 0 ? archivedPartIds[^1] : -1;
            return true;
        }

        /// <summary>B6：该 part 是否有已封存的图（返回 mapId；无 ⇒ -1）。</summary>
        public int GetArchivedMapForPart(int partId)
        {
            if (archivedMapIds == null || archivedPartIds == null) return -1;
            int n = System.Math.Min(archivedMapIds.Count, archivedPartIds.Count);
            for (int i = 0; i < n; i++)
                if (archivedPartIds[i] == partId) return archivedMapIds[i];
            return -1;
        }

        // ── 部件系统（单 part 化：载具至多一个 part，导弹等无 Map part 也走此列表）──
        private List<SpacePart> parts = new();
        public IReadOnlyList<SpacePart> Parts => parts;

        /// <summary>
        /// **热循环专用**：直接拿 `List&lt;SpacePart&gt;` 本体（不经过 `IReadOnlyList` 接口派发）。
        /// 用法：`var span = CollectionsMarshal.AsSpan(entity.PartsList);` —— 只在本程序集内、
        /// 只在**不增删** parts 的循环里取；任何写操作仍走 `AddPart` / `RemovePart` / `SetParts`
        /// （唯一收口 = `OnPartsChanged`）。对外面（其它程序集）仍是只读的 `Parts`。
        ///
        /// 为什么（部件功能化 §2-A，2026-10-22）：`part盒重组` 的每件成本 ≈42 ns，其中一刀是
        /// 每件一次的接口 `get_Item` 派发 + 边界检查。`Span&lt;T&gt;` 用 ref 取值，JIT 可去边界。
        /// 判据：台架 ns/件**不升**（`PartHotAccessBench`），且逐位不变（只换访问方式）。
        /// </summary>
        internal List<SpacePart> PartsList => parts;

        /// <summary>下一个可用 partId（B1：持久单调、只增不复用；SetParts 会按现有最大 id 追平）。</summary>
        private int nextPartId = 1;

        /// <summary>
        /// **是否还有活着的件**（全部损毁 ⇒ 纯残骸）。
        ///
        /// 2026-10-30 用户裁定"射弹/光束/弹头跳过残骸"之后，这是三条路径共用的**唯一判据**：
        /// 纯残骸实体的 AABB 不再挡弹、不再产生接触。⚠ 线性扫描（件数 10¹ 级）—— 不要改成
        /// 按 `partSetVersion` 缓存：`isDestroyed` 有可能被直接写（不全走 `MarkPartDestroyed`）。
        ///
        /// 成本/调用纪律：≈1–2 ns/件（Release，只有 bool 读 + 边界检查），20 件 ≈ 30 ns、256 件 ≈ 0.4 µs。
        /// **只允许在"几何上已经命中"之后调用**（见 `ResolveEntityContacts`）——候选对每步都查会把它
        /// 放大成"弹群实体 × 目标实体"级别的调用量。
        /// </summary>
        public bool HasAlivePart
        {
            get
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p != null && !p.isDestroyed) return true;
                }
                return false;
            }
        }

        // ── 控制核心（2026-10-26；用户口径 = KSP 命令舱/探测器核心）──
        /// <summary>**装过**控制核心（粘性，落盘）：一旦为真，失去核心就**永久失控**，不会因残骸消失而复活。
        /// 没装过的舰（EVA/导弹/无人机/调试船）恒 false ⇒ 完全不受影响。</summary>
        public bool RequiresControlCore;

        /// <summary>当前是否还有**未损毁**的控制核心（不落盘，由 `SyncControlCorePresence` 按部件集重算）。</summary>
        public bool HasIntactControlCore;

        /// <summary>该实体是否可被操作（点火/操舵）：没装过核心 ⇒ 恒 true；装过 ⇒ 必须有完好核心。</summary>
        public bool IsControllable => !RequiresControlCore || HasIntactControlCore;

        /// <summary>按部件集重算控制核心在位情况（`OnPartsChanged` 单一收口里调；O(件)，只在变更时）。</summary>
        internal void SyncControlCorePresence()
        {
            bool intact = false;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.isDestroyed) continue;
                if (p.HasComponent<ControlCoreComponentData>()) { intact = true; break; }
            }
            HasIntactControlCore = intact;
            if (intact) RequiresControlCore = true;   // 装过就粘住
        }

        /// <summary>控制核心损毁（`PartRole.Control` 的收口）：失控 + 走关键件失能（清机动/熄火）。</summary>
        internal void HandleControlCoreLost()
        {
            RequiresControlCore = true;
            HasIntactControlCore = false;
            DisableOnKeyPartDestroyed();
        }

        /// <summary>**按活动级自动开关工质罐阀门**（2026-10-27 用户裁定）：
        /// 活动级 = 当前最大 `stageId`；只有活动级的罐 `feedEnabled=true`，其余关闭（油保住）。
        /// 抛级/部件变化 ⇒ `InvalidateStats` 调用；阀门变了就让供油计划失效。</summary>
        internal void SyncStageFeedValves()
        {
            // 2026-11-03（C-full）：活动集 = 最大级 + 并联点火的下级；唯一出处 StagePartition。
            var activeStages = StagePartition.ActiveStages(parts);

            bool changed = false;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.isDestroyed) continue;
                var tank = p.GetComponent<PropellantTankComponentData>();
                if (tank == null || tank.leaked) continue;
                bool want = activeStages.Contains(p.stageId);
                if (tank.feedEnabled == want) continue;
                tank.feedEnabled = want;
                changed = true;
            }
            if (changed) fuelPlanVersion++;
        }

        // ── 实体级 stats 缓存（kg 等，Parts / 组件变更时置脏）──
        private ShipStats shipStats;
        private bool statsDirty = true;

        /// <summary>部件集/组件可能变化的版本号（批 0：群级惯量缓存指纹读它；与 `PropulsionVersion` 同款模式）。
        /// **唯一自增点 = `InvalidateStats`**（"部件集可能变化"的唯一既有收口）。</summary>
        internal int partSetVersion;

        /// <summary>P0-2：**罐内工质质量版本**。只影响 COM/惯量（燃料不进 `ShipStats.mass`），
        /// 不进功能 stats、不打脏群信号。燃烧/加注/漏光只自增它，不再走 `InvalidateStats`。</summary>
        internal int tankMassVersion;

        /// <summary>P0-1：**图链版本**（挂图/解挂/主图切换/部件集变化）。`GetPartLinkedMapIdsCached` 的缓存指纹。</summary>
        internal int mapLinkVersion;
        private List<int> cachedPartLinkedMapIds = new(2);
        private int cachedMapIdsVersion = -1;

        /// <summary>B6：供油计划结构版本（部件集/切网/优先级/外部镜像重分配时自增；**质量变化不增**）。</summary>
        internal int fuelPlanVersion;
        private Dictionary<(string defName, int group, int stage, int block), FuelPlan> fuelPlans = new();

        /// <summary>实体聚合属性：Σ 部件 ShipStats（质量含损毁件；功能项只算未毁件，见 B5）。</summary>
        public ShipStats AggregatedStats
        {
            get
            {
                if (statsDirty)
                    RecalculateShipStats();
                return shipStats;
            }
        }

        /// <summary>实体总质量（kg）：Σ 全部部件质量（**损毁件仍计入**，B5 的 D2 乙形态），缓存防重复计算。</summary>
        public float TotalMass => AggregatedStats.mass;

        private void RecalculateShipStats()
        {
            shipStats = default(ShipStats);
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null) continue;
                var ps = p.Stats;
                // B5 / D2 乙：损毁件**仍在、有质量**（残骸不凭空消失）；功能项在此后出局。
                shipStats.mass += ps.mass;
                if (p.isDestroyed) continue;
                shipStats.thrust += ps.thrust;
                shipStats.powerGen += ps.powerGen;
                shipStats.powerDraw += ps.powerDraw;
                // §5 电闭环：四通道 + 电池限值（**新增 ShipStats 字段必须在此加一行**）
                shipStats.powerDrawPropulsion += ps.powerDrawPropulsion;
                shipStats.powerDrawShield += ps.powerDrawShield;
                shipStats.powerDrawWeapon += ps.powerDrawWeapon;
                shipStats.powerDrawSensor += ps.powerDrawSensor;
                shipStats.powerDischargeLimit += ps.powerDischargeLimit;
                shipStats.powerChargeLimit += ps.powerChargeLimit;
                shipStats.heatProduction += ps.heatProduction;
                shipStats.heatDissipation += ps.heatDissipation;
                shipStats.propellant += ps.propellant;
                shipStats.powerStorage += ps.powerStorage;
                shipStats.shieldCapacity += ps.shieldCapacity;
                shipStats.sensorRange += ps.sensorRange; // 聚合语义待定（max vs sum），先求和
                shipStats.cargoCapacity += ps.cargoCapacity;
                // [N2] 抗压制 / 抗欺骗：与其余字段同为求和。**新增 ShipStats 字段必须在此加一行**，
                // 否则组件贡献进不了实体级聚合——本方法是唯一的"必须同步点"。
                // [V3]（2026-10-02 裁定 ②）记忆时长同走此路：部件组件 → 本行 → 群级 `SensorTech.memorySeconds`。
                shipStats.memorySeconds += ps.memorySeconds;
                shipStats.jamStrength += ps.jamStrength;   // [E3] 干扰效力（同上：新增槽必须在此加一行）
                shipStats.ecmResist += ps.ecmResist;
                shipStats.eccmResist += ps.eccmResist;
                shipStats.activeGainBonus += ps.activeGainBonus;   // ★ 修复（2026-10-17 批 -1）：曾漏聚合 ⇒ 群级主动增益恒 0
                shipStats.activeRadiation += ps.activeRadiation;   // ★ 修复（2026-10-17 批 -1）：同上（主动辐射增量）
            }
            // B8：整船质量 0 才兜底 1kg（不再每件 1kg ⇒ 256 件空壳不再凭空 +256kg）
            if (shipStats.mass <= 0f)
                shipStats.mass = 1f;
            statsDirty = false;
        }

        /// <summary>实体级 stats 失效。所有"部件集 / 部件组件可能变化"的路径必须走这里。</summary>
        public void InvalidateStats()
        {
            statsDirty = true;
            SyncStageFeedValves();   // 活动级阀门（2026-10-27）：抛级/换件后自动切罐
            partSetVersion++;   // 批 0：惯量缓存指纹（群级 `SpaceEntityGroup.Inertia` 读它；与 PropulsionVersion 同款模式）
            // 动量轮容量随质量走（2026-10-26）：只补空，不覆盖已升级/已调的容量。
            if (wheelCapacity <= 0.0) RefreshWheelCapacity();
            // 2026-10-26：零件驱动的推进要在"引擎/罐增删损毁"后重建（唯一收口 = 这里）。
            // 显式推进的实体 `PropulsionFromParts=false` ⇒ 一次布尔判断，零影响。
            if (PropulsionFromParts) PartPropulsionBuilder.RebuildMapless(this);
            // 部件集变化 ⇒ 本船所属群组的派生信号（[V8]，吃 AggregatedStats 的热/电/尺寸项）也失效。
            // 本方法是"部件集可能变化"的**唯一既有收口**（AddComponent / RemoveComponent / 部件销毁都汇聚到这里），
            // 所以同步点放这里最省。⚠ 若将来新增改部件的路径绕开本方法，群组信号会静默过期
            // —— 与 AVM:824 的"派生点"是同一类陷阱，新加部件变更入口时请对照此处。
            SpaceGameComp.GetCached()?.GetGroupById(GroupId)?.InvalidateSignal();
        }

        // ── 无 Map 实体的自带推进数据（IPropulsionSource）──
        // 导弹/载荷等纯物理实体没有内部地图，引擎参数与工质直接挂在实体上。
        // 有 Map 的实体忽略此数据：burn/机动估算统一走 SpaceMapComp（见 GetPropulsionSource）。
        public List<PropulsionData> propulsion = new();
        public bool HasPropulsionData => propulsion.Count > 0;

        /// <summary>**推进由零件合成**（2026-10-26）：true ⇒ `InvalidateStats` 时按零件重建；
        /// 显式 `AddPropulsion` 的实体保持 false，绝不被覆盖。</summary>
        public bool PropulsionFromParts;

        // ── 动量轮（2026-10-26 用户裁定：Entity 自己存一个角动量向量 + 可变上限）──
        /// <summary>轮子当前储存的角动量（**船体系**，kg·m²/s）。RCS 卸载消耗的就是它。</summary>
        public DVector3 wheelMomentum;

        /// <summary>轮子容量上限（可变；≤0 ⇒ 视为没装轮子）。默认随质量给，见 `RefreshWheelCapacity`。</summary>
        public double wheelCapacity;

        /// <summary>按当前结构质量重算容量：`k · mass`（k 默认 500 (kg·m²/s)/kg）。
        /// 只在"还没容量/质量显著变化"时调；调大是升级件的入口（以后）。</summary>
        public void RefreshWheelCapacity(float perKg = 500f)
        {
            double mass = AggregatedStats.mass;
            wheelCapacity = mass > 0f ? mass * perKg : 0.0;
        }

        // ── 无 Map 主推方向型（D15，可选）──
        // 有 Map 实体忽略此字段（主推轴 = mapComp.MainThrustAxis）。
        // null = 全向（穿梭机/EVA/无人机等默认，任意方向推力、无姿态对准）；
        // 有值 = 主推方向型（导弹/火箭：只沿该 body 局部轴锥角内出力，需魔动量轮对准）。
        public DVector3? mainAxisShip;
        public bool HasMainAxis => mainAxisShip.HasValue && mainAxisShip.Value.sqrMagnitude > 1e-12;

        /// <summary>
        /// **B8：无 Map 实体由 part 引擎聚合出的主推轴**（船体系、单位向量）。
        ///
        /// 口径：
        /// · 逐件读 `EngineComponentData`，**跳过损毁件与全矢量 RCS**（RCS 不定义主推方向）；
        /// · 每台推力 = `ve × ṁ`，方向 = `thrustAxis`（part 局部）；引擎件带姿态时按 `rotationShip`
        ///   旋进船体系，再按推力加权求和；
        /// · 没有任何可用主引擎 / 矢量和退化（对称对消）⇒ 返回 `mainAxisShip`（D15 旧字段兼容），
        ///   仍为空 ⇒ 零向量（全向，无对准语义）。
        ///
        /// 这是**无 Map** 侧的轴来源；有 Map 组仍以 `SpaceMapComp.MainThrustAxis` 为准（地图已按轴向
        /// 引擎注册聚合）。只读、无分配，可在物理步/表现层调用。
        /// </summary>
        public DVector3 GetMainThrustAxisShip()
        {
            DVector3 sum = DVector3.zero;
            bool anyEngine = false;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null || part.isDestroyed) continue;
                var engine = part.GetComponent<EngineComponentData>();
                if (engine == null || engine.isVectoredRcs) continue;
                if (engine.massFlowRate <= 0f || engine.exhaustVelocity <= 0f) continue;
                int axisIndex = (int)engine.thrustAxis;
                if (axisIndex < 0 || axisIndex >= 6) continue;

                DVector3 axisShip = ((ThrustAxis)axisIndex).ShipVector();
                if (part.rotationShip != DQuaternion.identity)
                    axisShip = DVector3.Rotate(axisShip, part.rotationShip);
                // 2026-10-27 口径修正：`thrustAxis` 是**喷口/排气方向**（Back = −Z，主推进默认）。
                // 推力 = **反方向**（尾部引擎推动船体朝 +Z 机头走）⇒ 主推轴取反。
                sum -= axisShip * (engine.exhaustVelocity * engine.massFlowRate);
                anyEngine = true;
            }

            if (anyEngine && sum.sqrMagnitude > 1e-12)
                return sum.normalized;

            // 旧字段兼容：导弹/调试船等仍可显式给主推轴；全向实体保持零向量。
            return HasMainAxis ? mainAxisShip!.Value.normalized : DVector3.zero;
        }

        /// <summary>
        /// **引擎偏心力矩（标称推力）**（2026-10-26，动量轮的输入）。
        /// 口径 = "引擎全开"：每台 `F = ve·ṁ` 沿自身 `thrustAxis`（含件姿态），对 COM 取矩：
        /// `τ = Σ(pos×F) − COM×ΣF`。只用缓存值（part 位置/组件），无分配、无 sqrt 之外的开销。
        /// 倾斜安装暂不允许（用户 2026-10-26 裁定）；损毁件跳过。
        /// </summary>
        public DVector3 GetEngineTorqueAboutCom(in DVector3 com, out double totalThrust)
        {
            DVector3 sumFPos = DVector3.zero;
            DVector3 sumF = DVector3.zero;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null || part.isDestroyed) continue;
                var engine = part.GetComponent<EngineComponentData>();
                if (engine == null || engine.massFlowRate <= 0f || engine.exhaustVelocity <= 0f) continue;
                int axisIndex = (int)engine.thrustAxis;
                if (axisIndex < 0 || axisIndex >= 6) continue;

                DVector3 axis = ((ThrustAxis)axisIndex).ShipVector();
                if (part.rotationShip != DQuaternion.identity)
                    axis = DVector3.Rotate(axis, part.rotationShip);

                DVector3 f = axis * -(engine.exhaustVelocity * engine.massFlowRate);   // 力 = −排气方向（2026-10-27 口径）
                sumF += f;
                sumFPos += Cross(part.positionShip, f);
            }
            totalThrust = sumF.magnitude;
            return sumFPos - Cross(com, sumF);
        }

        private static DVector3 Cross(in DVector3 a, in DVector3 b)
            => new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        // 运行时缓存（并行物理线程只读；propulsion 变更时重建）
        private List<PropellantNetwork> cachedPropulsionNetworks = new();
        private bool propulsionDirty = true;
        private double totalPropulsionMass;      // ⚠ double：见 PropulsionData.mass 的说明（累加量不许用 float）
        private double totalPropulsionCapacity;

        /// <summary>推进数据版本：InvalidatePropulsion 自增（不序列化）。
        /// 群级推进源（GroupPropulsionAggregator）缓存以它判陈旧——与 Networks 缓存同一失效入口。</summary>
        public int PropulsionVersion { get; private set; }

        /// <summary>全部自带工质网络（由 propulsion 数据懒构建；ve/ṁ 变更需 InvalidatePropulsion）。</summary>
        public IReadOnlyList<PropellantNetwork> Networks
        {
            get
            {
                if (propulsionDirty)
                {
                    var list = new List<PropellantNetwork>(propulsion.Count);
                    for (int i = 0; i < propulsion.Count; i++)
                    {
                        var d = propulsion[i];
                        if (d == null || d.massFlow <= 0f) continue;
                        var net = new PropellantNetwork { propellant = d.propellant, stageId = d.stageId, blockId = d.blockId };
                        if (d.isVectoredRcs)
                            net.RegisterEngineRcs(d.ve, d.massFlow); // RCS 副网：入总聚合 + RCS 聚合（D15/D13 同构）
                        else
                            net.RegisterEngine(d.ve, d.massFlow);     // 主引擎：总聚合（无 Map 由实体主推轴约束）
                        net.RegisterGasDraws(d.gasIntakes, d.massFlow);   // [A5] 附带进气（空 = 纯火箭）
                        net.RegisterEnvelope(d.ve, d.massFlow, d.densityExponent, d.machCurve);   // [A4] 包线（火箭项不入表）
                        list.Add(net);
                    }
                    cachedPropulsionNetworks = list;
                    propulsionDirty = false;
                }
                return cachedPropulsionNetworks;
            }
        }

        /// <summary>某工质自带存量（kg）。**double**：见 <see cref="PropulsionData.mass"/>。
        /// ⚠ C-full（2026-11-03）起同一工质可有**多个块条目** ⇒ 必须求和，不能取第一条。</summary>
        public double GetPropellantMass(PropellantDef def)
        {
            if (def == null) return 0.0;
            double total = 0.0;
            for (int i = 0; i < propulsion.Count; i++)
            {
                var d = propulsion[i];
                if (d?.propellant == def) total += d.mass;
            }
            return total;
        }

        /// <summary>消耗自带工质（kg），返回实际扣除量（受存量截断）。**double**：见 <see cref="PropulsionData.mass"/>。</summary>
        public double TryConsumePropellant(PropellantDef def, double kg)
        {
            if (def == null || kg <= 0.0) return 0.0;

            // B6：有 tank part 的实体按供油计划消耗（优先级/切网生效）；无 tank 走旧条目路径。
            if (TryConsumeViaPlan(def, 0, kg, out double planned, out bool handled))
                return planned > 0.0 ? ConsumeOwnMassRaw(def, planned) : 0.0;

            // ⚠ 原来这里是"**扣第一个匹配的条目就 return**" ✗ —— 同一工质有多个条目（多罐同种料）时
            //   只烧得到第一罐，余量被**静默丢弃**（调用方会把剩余传给下一个**实体**，永不回头）⇒
            //   燃耗记不满、群质量掉不到位 ⇒ 燃烧按偏大的质量算 `a` ⇒ **多给 Δv**（2026-09-19 实测：
            //   `Σ(a·dt)=808` 而工质只够 749.6 ⇒ 白送 58 m/s）。
            // ⇒ 跨**全部**同工质条目扣足本次请求的量。
            double consumed = ConsumeOwnMassRaw(def, kg);
            if (consumed > 0.0)
                SyncCombinedTankFuel();
            return consumed;
        }

        /// <summary>只扣自带推进条目的质量，不碰 tank 镜像（计划已改罐时用）。</summary>
        private double ConsumeOwnMassRaw(PropellantDef def, double kg)
        {
            double consumed = 0.0;
            for (int i = 0; i < propulsion.Count && consumed < kg; i++)
            {
                var d = propulsion[i];
                if (d?.propellant != def) continue;
                double take = System.Math.Min(kg - consumed, d.mass);
                if (take <= 0.0) continue;
                d.mass -= take;
                consumed += take;
            }
            if (consumed > 0.0)
                totalPropulsionMass = System.Math.Max(0.0, totalPropulsionMass - consumed);
            return consumed;
        }

        /// <summary>按 `(stageId, blockId)` 扣本实体自带推进条目（C-full 块池；2026-11-03）。</summary>
        public double TryConsumePropellantBlock(PropellantDef def, int stageId, int blockId, double kg)
        {
            if (def == null || kg <= 0.0) return 0.0;
            if (TryConsumeViaPlan(def, 0, stageId, blockId, kg, out double planned, out bool handled))
                return planned > 0.0 ? ConsumeOwnMassRawBlock(def, stageId, blockId, planned) : 0.0;

            double consumed = ConsumeOwnMassRawBlock(def, stageId, blockId, kg);
            if (consumed > 0.0) SyncCombinedTankFuel();
            return consumed;
        }

        private double ConsumeOwnMassRawBlock(PropellantDef def, int stageId, int blockId, double kg)
        {
            double consumed = 0.0;
            for (int i = 0; i < propulsion.Count && consumed < kg; i++)
            {
                var d = propulsion[i];
                if (d?.propellant != def || d.stageId != stageId || d.blockId != blockId) continue;
                double take = System.Math.Min(kg - consumed, d.mass);
                if (take <= 0.0) continue;
                d.mass -= take;
                consumed += take;
            }
            if (consumed > 0.0)
                totalPropulsionMass = System.Math.Max(0.0, totalPropulsionMass - consumed);
            return consumed;
        }

        /// <summary>
        /// **[A3] 充填自带工质（kg）**，返回实际加入量（受**容量**截断）—— 进气充填的唯一写入口。
        /// ⚠ 与 <see cref="TryConsumePropellant"/> 同一条纪律：跨**全部**同工质条目分摊，且
        /// `totalPropulsionMass` 与逐条 `mass` **同相**更新 —— 它在这两条路径上都是**增量维护的缓存**
        /// （不在读时重算）⇒ 这里不维护就会与逐条之和对不上，进而喂错 `a = F/m`。
        /// </summary>
        public double TryFillPropellant(PropellantDef def, double kg)
        {
            if (def == null || kg <= 0.0 || propulsion == null) return 0.0;

            // B6：有 tank part ⇒ 计划按高层先加满，再 raw 加自带池（不再全量重分配）。
            if (TryFillViaPlan(def, 0, kg, out double planned, out bool handled))
                return planned > 0.0 ? AddOwnMassRaw(def, planned) : 0.0;

            double added = 0.0;
            for (int i = 0; i < propulsion.Count && added < kg; i++)
            {
                var d = propulsion[i];
                if (d == null || d.propellant != def) continue;
                double room = d.capacity - d.mass;
                if (!(room > 0.0)) continue;
                double take = System.Math.Min(kg - added, room);
                d.mass += take;
                added += take;
            }
            if (added > 0.0)
            {
                totalPropulsionMass += added;
                SyncCombinedTankFuel();
            }
            return added;
        }

        /// <summary>自带工质总质量（kg）。**double**：它是"每步扣一点"的累加量，float 会在 72 t 处只剩 7.8 g 分辨率。</summary>
        public double TotalPropellantMass => totalPropulsionMass;
        /// <summary>自带燃料总容量（kg，MaxDeltaV 用）。**double**（同 <see cref="TotalPropellantMass"/>）。</summary>
        public double TotalPropellantCapacity => totalPropulsionCapacity;

        /// <summary>等效排气速度 ve = Σ(ve·ṁ)/Σṁ（m/s，全网络并行烧的加权平均）。</summary>
        public float EffectiveVe
        {
            get
            {
                float f = 0f, mf = 0f;
                var nets = Networks;
                for (int i = 0; i < nets.Count; i++)
                {
                    f += nets[i].Thrust;
                    mf += nets[i].MassFlow;
                }
                return mf > 0f ? f / mf : 0f;
            }
        }

        /// <summary>添加/合并一条自带推进配置（同工质 + 同 (stage, block) 合并 ve/ṁ，累加存量与容量）。</summary>
        public void AddPropulsion(PropulsionData d)
        {
            if (d == null || d.propellant == null) return;
            for (int i = 0; i < propulsion.Count; i++)
            {
                if (propulsion[i].propellant == d.propellant && propulsion[i].isVectoredRcs == d.isVectoredRcs
                    && propulsion[i].stageId == d.stageId && propulsion[i].blockId == d.blockId)
                {
                    var existing = propulsion[i];
                    existing.ve = d.ve;
                    existing.massFlow = d.massFlow;
                    existing.mass += d.mass;
                    existing.capacity += d.capacity;
                    InvalidatePropulsion();
                    SyncCombinedTankFuel();
                    return;
                }
            }
            propulsion.Add(d);
            InvalidatePropulsion();
            SyncCombinedTankFuel();
        }

        /// <summary>B6：切网/优先级/部件集变化后让供油计划失效（下次消耗重建）。</summary>
        public void InvalidateFuelPlans()
        {
            fuelPlanVersion++;
        }

        private FuelPlan GetFuelPlan(string defName, int group)
            => GetFuelPlan(defName, group, -1, -1);

        private FuelPlan GetFuelPlan(string defName, int group, int stageId, int blockId)
        {
            var key = (defName, group, stageId, blockId);
            if (!fuelPlans.TryGetValue(key, out var plan))
            {
                plan = new FuelPlan();
                fuelPlans[key] = plan;
            }
            if (plan.Version != fuelPlanVersion)
            {
                plan.Build(parts, defName, group, stageId, blockId);
                plan.Version = fuelPlanVersion;
            }
            return plan;
        }

        /// <summary>
        /// **B6：按供油计划消耗**（低优先级先烧；组内按剩余质量比例）。
        /// 返回 handled=false ⇒ 该组没有 tank 计划（调用方回退旧池路径，例如纯建筑站）。
        /// handled=true 且 actual=0 ⇒ 该组被切出/无料，**不得**回退主池。
        /// </summary>
        public bool TryConsumeViaPlan(PropellantDef def, int group, double kg,
            out double actual, out bool handled)
            => TryConsumeViaPlan(def, group, -1, -1, kg, out actual, out handled);

        /// <summary>同 <see cref="TryConsumeViaPlan(PropellantDef,int,double,out double,out bool)"/>，
        /// 但只从 `(stageId, blockId)` 这一块的罐扣（C-full；-1 = 全部块）。2026-11-03。</summary>
        public bool TryConsumeViaPlan(PropellantDef def, int group, int stageId, int blockId, double kg,
            out double actual, out bool handled)
        {
            actual = 0.0;
            handled = false;
            if (def == null || kg <= 0.0) return false;
            var plan = GetFuelPlan(def.defName, group, stageId, blockId);
            handled = plan.HasTanks || group != 0;
            if (!handled) return false;
            if (plan.TotalMass <= 0.0) return true;
            actual = plan.Consume(kg);
            if (actual > 0.0)
                tankMassVersion++;   // 罐质量变了 ⇒ COM/惯量缓存失效；功能 stats/群信号不受影响
            return true;
        }

        /// <summary>B6：按供油计划加注（高层先加满）；handled 语义同消耗。</summary>
        public bool TryFillViaPlan(PropellantDef def, int group, double kg,
            out double actual, out bool handled)
        {
            actual = 0.0;
            handled = false;
            if (def == null || kg <= 0.0) return false;
            var plan = GetFuelPlan(def.defName, group);
            handled = plan.HasTanks || group != 0;
            if (!handled) return false;
            double room = plan.TotalCapacity - plan.TotalMass;
            if (!(room > 0.0)) return true;
            actual = plan.Fill(System.Math.Min(kg, room));
            if (actual > 0.0)
                tankMassVersion++;   // 罐质量变了 ⇒ COM/惯量缓存失效；功能 stats/群信号不受影响
            return true;
        }

        /// <summary>只加自带推进条目质量，不碰 tank 镜像（计划已加罐时用）。</summary>
        private double AddOwnMassRaw(PropellantDef def, double kg)
        {
            double added = 0.0;
            for (int i = 0; i < propulsion.Count && added < kg; i++)
            {
                var d = propulsion[i];
                if (d?.propellant != def) continue;
                double room = d.capacity - d.mass;
                if (!(room > 0.0)) continue;
                double take = System.Math.Min(kg - added, room);
                d.mass += take;
                added += take;
            }
            if (added > 0.0)
                totalPropulsionMass += added;
            return added;
        }

        /// <summary>
        /// **B6：把本实体所有图（SpaceMapComp）+ 自带 PropulsionData 的工质按工质汇总**，
        /// 再按 tank 容量比例写回镜像。所有图的工质都作数（不再只主图）。
        /// </summary>
        public void SyncCombinedTankFuel()
        {
            var names = new List<string>(3);
            for (int i = 0; i < parts.Count; i++)
            {
                var tank = parts[i]?.GetComponent<PropellantTankComponentData>();
                if (tank == null || string.IsNullOrEmpty(tank.propellantDefName)) continue;
                if (!names.Contains(tank.propellantDefName)) names.Add(tank.propellantDefName);
            }
            if (names.Count == 0) return;

            var maps = GetMapComps();
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                var def = ResolvePropellantDef(name);
                if (def == null) continue;
                double total = GetPropellantMass(def);
                for (int m = 0; m < maps.Count; m++)
                    total += maps[m].GetPropellantMass(def);
                SyncTankFuelDistribution(name, total);
            }
        }

        /// <summary>
        /// **B4a：把某工质的总存量按储罐容量比例写入 tank part 的分布镜像**（Σtank = 池）。
        /// 权威池仍是推进源；这里只维护“燃料质量在哪件 part 上”，供 COM/惯量使用。
        /// 任何差异都会 `tankMassVersion++` / `fuelPlanVersion++` ⇒ 群级惯量重算、计划质量基线失效；
        /// **不**打脏功能 stats 或群信号。
        /// </summary>
        public void SyncTankFuelDistribution(string propellantDefName, double totalMass)
        {
            if (string.IsNullOrEmpty(propellantDefName)) return;

            double totalCapacity = 0.0;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null || part.isDestroyed) continue;
                var tank = part.GetComponent<PropellantTankComponentData>();
                if (tank == null || tank.propellantDefName != propellantDefName) continue;
                if (!tank.feedEnabled) continue;   // 阀门关：不参与分配，保住它自己的油
                if (tank.capacityKg > 0.0) totalCapacity += tank.capacityKg;
            }
            if (!(totalCapacity > 0.0)) return;

            double clamped = System.Math.Max(0.0, totalMass);
            bool changed = false;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null || part.isDestroyed) continue;
                var tank = part.GetComponent<PropellantTankComponentData>();
                if (tank == null || tank.propellantDefName != propellantDefName) continue;
                if (!tank.feedEnabled) continue;   // 阀门关：不重写镜像
                if (!(tank.capacityKg > 0.0)) continue;
                double want = clamped * (tank.capacityKg / totalCapacity);
                if (System.Math.Abs(tank.massKg - want) < 1e-9) continue;
                tank.massKg = want;
                changed = true;
            }
            if (changed)
            {
                fuelPlanVersion++;   // 外部镜像重分配 ⇒ 计划里的质量基线失效
                tankMassVersion++;   // 只影响 COM/惯量；功能 stats/群信号不变
            }
        }

        /// <summary>自带推进数据变更入口：重建网络缓存与总量。</summary>
        public void InvalidatePropulsion()
        {
            propulsionDirty = true;
            PropulsionVersion++;
            totalPropulsionMass = 0f;
            totalPropulsionCapacity = 0f;
            if (propulsion == null) return; // 旧存档无此节点
            for (int i = 0; i < propulsion.Count; i++)
            {
                totalPropulsionMass += propulsion[i].mass;
                totalPropulsionCapacity += propulsion[i].capacity;
            }
        }

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

        public DQuaternion Rotation
        {
            get => rotation;
            set
            {
                rotation = value;
                cachedAabbTick = -1;          // 旋转参与 GetAabbSystem → 失效缓存
                aabbDirty = true;
                cachedRotInvTick = -1;        // 姿态逆四元数随之失效
            }
        }

        /// <summary>Unity 渲染/UI 边界（B2：物理状态全程 DQuaternion，只有这里截断成 float）。</summary>
        public Quaternion RotationUnity => rotation.ToQuaternion();

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

        public void SetRotation(DQuaternion rot)
        {
            Rotation = rot;
        }

        /// <summary>Unity 边界的便捷入口（**截断**）；物理/姿态路径请用 <see cref="SetRotation(DQuaternion)"/>。</summary>
        public void SetRotation(Quaternion rot)
        {
            Rotation = (DQuaternion)rot;
        }

        // ── 部件管理 ──

        /// <summary>
        /// 从飞船设计数据初始化部件列表。会从 Parts 重新计算 BoundsSize。
        /// </summary>
        public void SetParts(List<SpacePart> newParts, bool recalculateBounds = true)
        {
            parts = newParts ?? new List<SpacePart>();
            fuelPlanVersion++;
            SyncNextPartIdFromParts();
            SyncControlCorePresence();   // 一次性换件也要重算控制核心（SetParts 不走 OnPartsChanged）
            NotifyMapLinksChanged();
            aabbDirty = true;
            cachedAabbTick = -1; // 部件参与 GetAabbSystem → 失效缓存
            InvalidateStats();
            if (recalculateBounds && parts.Count > 0)
                RecalculateBoundsFromParts();
        }

        /// <summary>
        /// **新增一个 part**（B1 运行时 API）：partId ≤ 0 时由分配器给出；显式 id 必须唯一且会推进分配器。
        /// 所有缓存走 <see cref="OnPartsChanged"/> 单一收口。
        /// </summary>
        public bool AddPart(SpacePart part, bool recalculateBounds = true)
        {
            if (part == null) return false;
            if (part.partId <= 0)
            {
                part.partId = AllocPartId();
            }
            else if (GetPartById(part.partId) != null)
            {
                Log.Warning($"[SpaceEntity] AddPart: duplicate partId {part.partId} on entity {entityId}; rejected.");
                return false;
            }
            else
            {
                nextPartId = System.Math.Max(nextPartId, part.partId + 1);
            }
            parts.Add(part);
            fuelPlanVersion++;
            OnPartsChanged(recalculateBounds);
            return true;
        }

        /// <summary>
        /// **移除一个 part**（B1 运行时 API）。挂图 part 按 <see cref="PartRemovalPolicy"/>：
        /// `KeepMap` = 拒绝；`DestroyMap` = 销毁其内部地图（默认，与 MarkPartDestroyed 同口径）。
        /// </summary>
        public bool RemovePart(int partId, PartRemovalPolicy policy = PartRemovalPolicy.DestroyMap)
        {
            int index = IndexOfPart(partId);
            if (index < 0) return false;
            SpacePart part = parts[index];
            int removedMapId = part.linkedMapId;
            if (removedMapId >= 0)
            {
                if (policy == PartRemovalPolicy.KeepMap)
                {
                    Log.Warning($"[SpaceEntity] RemovePart: part {partId} is map-linked; KeepMap policy refuses removal.");
                    return false;
                }
                SpaceMapGenerator.DestroyRoomMap(removedMapId);   // B6：只毁这一张
            }
            part.DetachComponents();
            parts.RemoveAt(index);
            fuelPlanVersion++;
            OnPartsChanged(true);
            if (removedMapId >= 0)
                RecalculatePrimaryMap();   // 主图若被移除 ⇒ 切到下一张；无图 ⇒ MapId=-1/HasMap=false
            return true;
        }

        public SpacePart? GetPartById(int partId)
        {
            int index = IndexOfPart(partId);
            return index >= 0 ? parts[index] : null;
        }

        /// <summary>
        /// **批量移除**（2026-10-30，抛级用）：语义与逐件 <see cref="RemovePart"/> 相同
        /// （挂图件按 policy 毁图、`DetachComponents`、出列、`fuelPlanVersion++`），
        /// 但 **`OnPartsChanged` 只在末尾跑一次** —— 逐件移除会每件重建一次推进池/统计/包围盒
        /// （实测抛 13 件 ≈ 40–100 µs、256 件船抛 128 件 ≈ 0.5–1.5 ms），批量后只剩一次。
        /// 返回实际移除的件数。
        /// </summary>
        public int RemoveParts(IReadOnlyList<int>? partIds,
            PartRemovalPolicy policy = PartRemovalPolicy.DestroyMap)
        {
            if (partIds == null || partIds.Count == 0) return 0;

            int removed = 0;
            bool mapRemoved = false;
            for (int i = 0; i < partIds.Count; i++)
            {
                int index = IndexOfPart(partIds[i]);
                if (index < 0) continue;
                SpacePart part = parts[index];
                int removedMapId = part.linkedMapId;
                if (removedMapId >= 0)
                {
                    if (policy == PartRemovalPolicy.KeepMap)
                    {
                        Log.Warning($"[SpaceEntity] RemoveParts: part {partIds[i]} is map-linked; KeepMap policy refuses removal.");
                        continue;
                    }
                    SpaceMapGenerator.DestroyRoomMap(removedMapId);
                    mapRemoved = true;
                }
                part.DetachComponents();
                parts.RemoveAt(index);
                removed++;
            }

            if (removed > 0)
            {
                fuelPlanVersion++;
                OnPartsChanged(true);                    // ← 唯一一次：版本/统计/包围盒/控制核心/推进重建
                if (mapRemoved) RecalculatePrimaryMap();
            }
            return removed;
        }

        /// <summary>
        /// **整体平移零件坐标**（2026-10-30，原点重定用）：`part.positionShip -= bodyOffset`，末尾走一次
        /// `OnPartsChanged(true)` —— 位置变了必须让包围盒/惯量指纹/统计一起刷新（`partSetVersion` 只看件集，
        /// 不看位置，所以**不能**只改坐标）。
        /// </summary>
        public void RecenterParts(in DVector3 bodyOffset)
        {
            if (parts.Count == 0) return;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null) continue;
                p.positionShip -= bodyOffset;
            }
            OnPartsChanged(true);
        }

        /// <summary>移动 part（体坐标；part 自身姿态字段归 B3）。</summary>
        public bool MovePart(int partId, DVector3 positionShip)
        {
            var part = GetPartById(partId);
            if (part == null) return false;
            part.positionShip = positionShip;
            OnPartsChanged(false);
            return true;
        }

        /// <summary>分配一个本实体唯一的 partId（持久单调，只增不复用）。</summary>
        internal int AllocPartId() => nextPartId++;

        /// <summary>**所有 part 集合变更的单一收口**：stats/版本 + AABB 缓存 + 可选包围盒。</summary>
        private void OnPartsChanged(bool recalculateBounds)
        {
            aabbDirty = true;
            cachedAabbTick = -1;
            NotifyMapLinksChanged();           // 部件集变化可能带来挂图件的增删/移动
            SyncControlCorePresence();         // 控制核心在位/失控（O(件)，只在变更时）
            InvalidateStats();                 // partSetVersion++ 与群组信号失效都在这里
            if (recalculateBounds)
                RecalculateBoundsFromParts();
        }

        private int IndexOfPart(int partId)
        {
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null && parts[i].partId == partId) return i;
            return -1;
        }

        private void SyncNextPartIdFromParts()
        {
            int max = 0;
            for (int i = 0; i < parts.Count; i++)
                if (parts[i] != null && parts[i].partId > max) max = parts[i].partId;
            nextPartId = System.Math.Max(nextPartId, max + 1);
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
                if (part == null) continue;
                // B5：损毁件仍在、仍有体积（残骸参与碰撞/包围盒）⇒ 不跳过。
                DVector3 half = part.boundsSizeShip * 0.5;
                double px = part.positionShip.x, py = part.positionShip.y, pz = part.positionShip.z;
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
            }
        }

        /// <summary>
        /// **B5 关键件判定（§5 默认值）**：挂图件（舱盒）是关键件；没有挂图件时**首件**兜底
        /// （单件 EVA/穿梭机/导弹，其唯一件就是关键件）。
        /// 关键件损毁 ⇒ 整船失控/失能（不立即解体，R9 不裂）；非关键件只掉功能。
        /// </summary>
        public bool IsKeyPart(SpacePart part)
        {
            if (part == null) return false;
            bool hasMapPart = false;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i] != null && parts[i].linkedMapId >= 0)
                {
                    hasMapPart = true;
                    break;
                }
            }
            if (hasMapPart) return part.linkedMapId >= 0;
            return parts.Count > 0 && ReferenceEquals(parts[0], part);
        }

        /// <summary>是否还有存活的关键件（没有关键件可用 ⇒ 失能）。</summary>
        public bool HasIntactKeyPart
        {
            get
            {
                if (parts.Count == 0) return false;
                bool hasMapPart = false;
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i] != null && parts[i].linkedMapId >= 0)
                    {
                        hasMapPart = true;
                        if (!parts[i].isDestroyed) return true;
                    }
                }
                if (hasMapPart) return false;
                return parts[0] != null && !parts[0].isDestroyed;
            }
        }

        /// <summary>储罐件容量合计（默认只算未毁件；验收/读数用）。</summary>
        public double GetTankCapacitySum(string propellantDefName, bool includeDestroyed = false)
        {
            if (string.IsNullOrEmpty(propellantDefName)) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null || (!includeDestroyed && part.isDestroyed)) continue;
                var tank = part.GetComponent<PropellantTankComponentData>();
                if (tank == null || tank.propellantDefName != propellantDefName) continue;
                if (tank.capacityKg > 0.0) sum += tank.capacityKg;
            }
            return sum;
        }

        /// <summary>按 DefName 解析工质（DefDatabase 优先；测试/无图环境回退自带推进条目里的实例）。</summary>
        private PropellantDef? ResolvePropellantDef(string propellantDefName)
        {
            if (string.IsNullOrEmpty(propellantDefName)) return null;
            var def = DefDatabase<PropellantDef>.GetNamedSilentFail(propellantDefName);
            if (def != null) return def;
            for (int i = 0; i < propulsion.Count; i++)
            {
                var d = propulsion[i];
                if (d?.propellant != null && d.propellant.defName == propellantDefName)
                    return d.propellant;
            }
            return null;
        }

        /// <summary>
        /// B5：储罐损毁漏光（D-P7）——镜像存量清零 + 从权威池扣掉同等燃料 + 容量出局。
        /// 权威池可能是地图（`SpaceMapComp`）或无图实体（`this`），统一由 `GetPropulsionSource()` 选。
        /// 2026-10-22 §7：改为**按组件实例**收口（`PartRoleTable` 分派）——一件上挂多个罐时逐个漏。
        /// </summary>
        internal void LeakTankPart(SpacePart part, PropellantTankComponentData? tank)
        {
            if (tank == null) return;
            if (tank.leaked)
            {
                if (tank.massKg != 0.0)
                {
                    tank.massKg = 0.0;   // 已漏过：读档后残骸再被命中也不二次扣池/扣容量
                    tankMassVersion++;
                }
                return;
            }
            tank.leaked = true;

            double leakedMass = tank.massKg;
            double lostCapacity = tank.capacityKg;
            tank.massKg = 0.0;
            if (leakedMass > 0.0)
                tankMassVersion++;

            var def = ResolvePropellantDef(tank.propellantDefName);
            if (def == null) return;

            var ownerMap = GetPartOwningMapComp(part);
            if (leakedMass > 0.0)
            {
                double consumed = ownerMap != null
                    ? ownerMap.TryConsumePropellant(def, leakedMass)
                    : (HasPropulsionData ? TryConsumePropellant(def, leakedMass) : 0.0);
                if (consumed < leakedMass)
                    GetPropulsionSource()?.TryConsumePropellant(def, leakedMass - consumed);
            }
            if (lostCapacity <= 0.0) return;

            if (ownerMap != null)
            {
                ownerMap.AddPropellantCapacity(def, -lostCapacity);
                ownerMap.AddPropellantCapacity(-lostCapacity);
                ownerMap.RemoveVirtualTankCapacity(lostCapacity);   // 读档重建时不许加回旧容量
            }
            else if (HasPropulsionData)
            {
                RemovePropellantCapacity(def, lostCapacity);
            }
        }

        /// <summary>B5：引擎件损毁 ⇒ 该台的质量流率/推力出局（地图虚拟推进或无图 PropulsionData）。
        /// 2026-10-22 §7：按组件实例收口（一件上多台引擎时逐台出局，旧实现只出局第一台）。</summary>
        internal void DisableEnginePart(SpacePart part, EngineComponentData? engine)
        {
            if (engine == null || engine.massFlowRate <= 0f) return;
            var def = ResolvePropellantDef(engine.propellantDefName);
            if (def == null) return;

            var ownerMap = GetPartOwningMapComp(part);
            if (ownerMap != null)
                ownerMap.RemoveVirtualEngineFlow(def, engine.exhaustVelocity, engine.massFlowRate);
            else if (HasPropulsionData)
                RemoveEngineFlow(def, engine.exhaustVelocity, engine.massFlowRate, engine.isVectoredRcs);
        }

        /// <summary>无图实体：按工质/主-RCS 扣减引擎质量流率，并重算等效 ve（储罐/引擎损毁用）。</summary>
        public float RemoveEngineFlow(PropellantDef def, float ve, float massFlow, bool isVectoredRcs)
        {
            if (def == null || massFlow <= 0f) return 0f;
            float remaining = massFlow;
            for (int i = 0; i < propulsion.Count && remaining > 0f; i++)
            {
                var d = propulsion[i];
                if (d == null || d.propellant != def || d.isVectoredRcs != isVectoredRcs) continue;
                float oldFlow = d.massFlow;
                if (oldFlow <= 0f) continue;
                float take = System.Math.Min(remaining, oldFlow);
                float newFlow = oldFlow - take;
                float newThrust = System.Math.Max(0f, d.ve * oldFlow - ve * take);
                d.massFlow = newFlow;
                d.ve = newFlow > 1e-6f ? newThrust / newFlow : 0f;
                remaining -= take;
            }
            if (remaining < massFlow)
                InvalidatePropulsion();
            return massFlow - remaining;
        }

        /// <summary>无图实体：按工质扣减自带推进容量（储罐损毁用）。返回实际扣减量。</summary>
        public double RemovePropellantCapacity(PropellantDef def, double kg)
        {
            if (def == null || kg <= 0.0) return 0.0;
            double remaining = kg;
            for (int i = 0; i < propulsion.Count && remaining > 0.0; i++)
            {
                var d = propulsion[i];
                if (d == null || d.propellant != def) continue;
                double take = System.Math.Min(remaining, d.capacity);
                if (take <= 0.0) continue;
                d.capacity -= take;
                remaining -= take;
            }
            if (remaining < kg)
                InvalidatePropulsion();   // totalPropulsionCapacity 按条目重算
            return kg - remaining;
        }

        /// <summary>关键件刚损毁：**整船**失能但不解体（清单机动/引导/点火；R9 不裂）。
        /// 只对单实体组生效——多实体组（弹群）里死一枚不等于整群失能。</summary>
        private void DisableOnKeyPartDestroyed()
        {
            if (GroupId == 0) return;
            var sg = SpaceGameComp.GetCached();
            var g = sg?.GetGroupById(GroupId);
            if (g == null) return;

            int entities = 0;
            foreach (int _ in g.GetEntityIds())
            {
                if (++entities > 1) return;
            }
            g.IsCrippled = true;
            g.ClearManeuvers();
            g.EndBurn();
        }

        /// <summary>
        /// 标记部件被摧毁（惰性标记，不立即重建）。
        /// B5：损毁件仍在/可见/有质量、参与碰撞；功能出局；储罐漏光；关键件损毁 ⇒ 失能。
        /// 舱室部件被毁时联动销毁其内部地图（先杀光内部 pawns）。
        /// </summary>
        public void MarkPartDestroyed(int partId)
        {
            int index = IndexOfPart(partId);
            if (index < 0) return;
            var part = parts[index];
            if (part.destroyHandled) return;   // 残骸再被命中：善后已做过（防重复漏光/失能）
            part.destroyHandled = true;
            bool wasDestroyed = part.isDestroyed;
            part.isDestroyed = true;
            if (!wasDestroyed)
                part.NotifyDestroyed();   // ApplyDamage 已通知过时不再重复
            // §11.6：摧毁 ⇒ 维护值归零。两条摧毁路径（ApplyDamage / 本方法）口径一致。
            part.condition = 0f;
            // §7：统一失效语义 —— 按**角色**查表收口（储罐漏光 / 引擎出局 / 其余由聚合出局承担）。
            // ⛔ 不要在这里加 if-else 分支：要加角色行为就加 `PartRoleTable` 的一行（缺行会在启动期红）。
            PartRoleTable.ApplyFailure(this, part);
            fuelPlanVersion++;
            OnPartsChanged(true);
            // 舱室联动：只销毁这一件挂的图；实体还有其他图就继续存在（B6）
            int removedMapId = part.linkedMapId;
            if (removedMapId >= 0)
            {
                SpaceMapGenerator.DestroyRoomMap(removedMapId);
                OnMapRemoved(removedMapId);
            }
            if (IsKeyPart(part))
                DisableOnKeyPartDestroyed();
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
            DQuaternion.Scribe("rotation", ref rotation);
            Scribe_Values.Look(ref MapId, "mapId", -1);
            Scribe_Values.Look(ref ArchivedMapId, "archivedMapId", -1);
            Scribe_Values.Look(ref ArchivedPartId, "archivedPartId", -1);
            Scribe_Collections.Look(ref archivedMapIds, "archivedMapIds", LookMode.Value);
            Scribe_Collections.Look(ref archivedPartIds, "archivedPartIds", LookMode.Value);
            if (archivedMapIds == null) archivedMapIds = new List<int>();
            if (archivedPartIds == null) archivedPartIds = new List<int>();
            Scribe_Values.Look(ref IsMapArchived, "isMapArchived", false);
            // 控制核心（2026-10-26）："装过"是粘性状态，必须落盘；"现在还完好"由部件集重算。
            Scribe_Values.Look(ref RequiresControlCore, "requiresControlCore", false);
            // 动量轮（2026-10-26）：角动量向量 + 可变容量都要落盘（RCS 卸载的账本）。
            DVector3.Scribe("wheelMomentum", ref wheelMomentum);
            Scribe_Values.Look(ref wheelCapacity, "wheelCapacity", 0.0);

            // 无 Map 实体自带推进数据序列化
            Scribe_Collections.Look(ref propulsion, "propulsion", LookMode.Deep);
            Scribe_Values.Look(ref PropulsionFromParts, "propulsionFromParts", false);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                InvalidatePropulsion();

            // D15 主推轴（null = 全向；旧存档无此节点 → 保持全向，向后兼容）
            DVector3.ScribeNullable("mainAxisShip", ref mainAxisShip);

            // 部件列表序列化
            Scribe_Collections.Look(ref parts, "parts", LookMode.Deep);
            Scribe_Values.Look(ref nextPartId, "nextPartId", 1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (parts == null) parts = new List<SpacePart>();
                if (propulsion == null) propulsion = new List<PropulsionData>();
                SyncNextPartIdFromParts();   // 首次引入 nextPartId 的旧档：按现有 id 追平
                SyncControlCorePresence();   // 读档后重算"核心是否还在"（Requires 已落盘）
                aabbDirty = true;
                InvalidateStats();
                foreach (var part in parts)
                {
                    part.ResolveReferences();
                    part.PostLoad();
                }
                if (parts.Count > 0)
                    RecalculateBoundsFromParts();
            }
        }
    }

    /// <summary>
    /// 无 Map 实体的单工质推进配置（工质种类 + 引擎参数 + 燃料存量/容量）。
    /// burn 通过 SpaceEntity（IPropulsionSource）读取；有 Map 实体忽略此数据（走 SpaceMapComp）。
    /// </summary>
    /// <summary>
    /// **[A5] 引擎的"附带进气"配比**：每消耗 1 kg 主工质，还要另外抽走 <see cref="perUnitRatio"/> kg
    /// 本工质（通常 = 空气）。无 Map 侧写在 `PropulsionData.gasIntakes` 里；**空 = 纯火箭**。
    /// </summary>
    public class GasIntake
    {
        /// <summary>被抽走的工质（通常 = `PF_Propellant_IntakeAir`）。缺席 = 本项不生效（注册端会跳过）。</summary>
        public PropellantDef? propellant = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        /// <summary>每单位主工质的抽取比（kg/kg，>0）。0 或负 = 本项不生效。</summary>
        public float perUnitRatio = 1f;
    }

    public partial class PropulsionData : IExposable
    {
        /// <summary>工质种类。</summary>
        public PropellantDef propellant;

        /// <summary>排气速度 ve（m/s）。</summary>
        public float ve;

        /// <summary>质量流率 ṁ（kg/s），推力 F = ve·ṁ。</summary>
        public float massFlow;

        /// <summary>燃料存量（kg）。
        /// ⚠ **必须 double**（2026-09-27 用户裁定）：原 float 在 72 t 处 1 ULP = **0.0078 kg**，
        /// 而消耗是 `mass -= take` 的**累加** ⇒ 一步的舍入差就是 **2 ULP = 0.0156 kg**，
        /// 再被 `accel = F/m` 放大成 **3.4e-6 m/s** 的 Δv 差、末端 **7e-5 m** 的位置差
        /// （由逐步指纹的输入列实测钉死：`burnDbgMass Δ=0.015625 kg`）。
        /// 这是**精度缺陷**、不是竞态 —— 工质质量是物理量，不该只有 7.8 g 分辨率。</summary>
        public double mass;

        /// <summary>燃料容量（kg，MaxDeltaV / 补给上限用）。见 <see cref="mass"/>（同批改 double）。</summary>
        public double capacity;

        /// <summary>
        /// **密度指数 a**（[A4]/D5 包线；无 Map 侧）：`F = ve·ṁ·油门·(ρ/ρ0)^a·f(M)`。
        /// **0 = 火箭**（退化：包线因子恒 1 ⇒ 既有无 Map 载具零改动）。吸气式常规取 1。
        /// 求值在 `AirBreathingCore.ThrustFactor`（唯一算式）。
        /// </summary>
        public float densityExponent;

        /// <summary>**Mach 折线**（[A4]/D5；无 Map 侧）：空 = 不折（f≡1）。见 `AirBreathingCore`。</summary>
        public List<MachPoint>? machCurve;

        /// <summary>
        /// **[A5] 附带进气配比**（无 Map 侧）：本引擎每消耗 1 kg <see cref="propellant"/> 还要抽走的别的工质。
        /// **空 = 纯火箭**（既有载具零改动、门控路径不进）；吸气式 = 一项"空气 15:1"这样的条目。
        /// 消费端 = 本船的进气道充填（[A3]）往同一个空气条目里灌；**抽干 ⇒ 熄火**（见 `AirBreathingCore`）。
        /// </summary>
        public List<GasIntake>? gasIntakes;

        /// <summary>
        /// 是否弱 RCS 副网（D15/D13 同构，仅无 Map 实体使用）：
        /// true = 全矢量小推力修正（主推未对准时仍可出力、主网尽时兜底），不参与主推轴向约束；
        /// false = 主引擎（无 Map 主推方向型沿 entity.mainAxisShip 锥内出力；全向型无约束）。
        /// 约束：同一实体内主网与 RCS 网须用不同 PropellantDef（同工质无法区分消耗）。
        /// </summary>
        public bool isVectoredRcs;

        /// <summary>所属级（C-full，2026-11-03；旧档/外部显式推进默认 0）。</summary>
        public int stageId;

        /// <summary>所属分离块（同 stage 内；旧档/导弹/外部显式推进 = -1 ⇒ 走聚合口径）。</summary>
        public int blockId = -1;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref propellant, "propellant");
            Scribe_Values.Look(ref ve, "ve", 0f);
            Scribe_Values.Look(ref massFlow, "massFlow", 0f);
            // ⚠ 这两个的默认值写 `0.0`（它们已是 double）：留 `0f` 能隐式转换、编译过，
            //    但读起来像"字段还是 float" ⇒ 下一个人会误判（同批改 double，见 `mass` 的说明）。
            //    **旧档读不出来是有意的**（开发期不考虑旧档兼容：不为旧表示写迁移）。
            Scribe_Values.Look(ref mass, "mass", 0.0);
            Scribe_Values.Look(ref capacity, "capacity", 0.0);
            Scribe_Values.Look(ref isVectoredRcs, "isVectoredRcs", false);
            Scribe_Values.Look(ref stageId, "stageId", 0);
            Scribe_Values.Look(ref blockId, "blockId", -1);
            Scribe_Values.Look(ref densityExponent, "densityExponent", 0f);
            Scribe_Collections.Look(ref machCurve, "machCurve", LookMode.Deep);
            Scribe_Collections.Look(ref gasIntakes, "gasIntakes", LookMode.Deep);
        }
    }
}
