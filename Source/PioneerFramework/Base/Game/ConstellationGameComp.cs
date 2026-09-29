using System.Collections.Generic;
using PioneerFramework.Launch;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 星座成员：星座内的一个轨道 Group（groupId），卫星/空间站或**地表锚点 group** 统一。
    /// 元数据字段（role / slotIndex / deployedBy）本期不启用（保留结构，为 Walker 槽位布局 / 按角色调度预留）。
    /// </summary>
    public struct ConstellationMember : IExposable
    {
        /// <summary>唯一键：同一星座内不重复（多归属 = 同一 groupId 出现在不同星座）。</summary>
        public int groupId;
        /// <summary>角色标签（"comms"/"nav"/"relay"…）。本期不启用，恒 null。</summary>
        public string? role;
        /// <summary>槽位号，-1 = 未分配。本期不启用，恒 -1。</summary>
        public int slotIndex;
        /// <summary>部署来源载荷 Def 名（追溯用）。本期不启用，恒 null。</summary>
        public string? deployedBy;

        public void ExposeData()
        {
            Scribe_Values.Look(ref groupId, "groupId");
            Scribe_Values.Look(ref role, "role");
            Scribe_Values.Look(ref slotIndex, "slotIndex", -1);
            Scribe_Values.Look(ref deployedBy, "deployedBy");
        }
    }

    /// <summary>
    /// 星座实例：命名编组（Id + 名称 + 成员表）。纯运行时创建，无 Def；成员表是唯一事实源。
    /// </summary>
    public class Constellation : IExposable
    {
        public int ConstellationId;
        public string? Name;
        public List<ConstellationMember> Members = new List<ConstellationMember>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref ConstellationId, "constellationId");
            Scribe_Values.Look(ref Name, "name");
            Scribe_Collections.Look(ref Members, "members", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Members == null)
                Members = new List<ConstellationMember>();
        }
    }

    /// <summary>
    /// 星座组网（PFL 组织层）：在 <see cref="SpaceGameComp"/> 之上维护「星座 → 成员 Group」的编组关系，
    /// 不碰物理/轨道/燃烧——轨道能力全部复用 Group 既有 API。
    ///
    /// 定位与纪律（设计文档 §2《星座组网系统设计-20260828.md》）：
    ///   · **零 Tick**：不覆盖 GameComponentTick。维护只在「成员加入 / 群组被销毁 / 据点被摧毁」时发生；
    ///   · **纯事件驱动**：唯一挂点是 SpaceGameComp.GroupDestroyed（DestroyGroup 内 fire），同步完成当帧；
    ///   · **实时查询**：聚合查询不做常驻缓存，调用时遍历成员表实时读 group 状态（规模典型 &lt;50）；
    ///   · **纯运行时 + 纯 API**：无 Def、本组件不写 UI，对外只暴露 API（DMS:NF 载荷部署侧调用）。
    ///
    /// 成员类型统一为 groupId：地表据点入编时**懒创建锚点 group**（合成的不动 group，见 §5 D11），
    /// 于是星座核心不必区分"天上/地面"。
    ///
    /// 锚点 group 的全局约束：IsActive=false / IsMovable=false / IsCollidable=false / IsSurfaceAnchor=true。
    /// IsActive=false 让它对既有的全部「遍历 groups」消费方天然不可见（渲染 / 索敌 / 目标列表 / 战斗网格），
    /// 无需逐站点补过滤；GetGridForGroup 另有 IsSurfaceAnchor 短路使其根本不进空间索引。
    /// </summary>
    public class ConstellationGameComp : GameComponent
    {
        /// <summary>地表层根天体 id（全库约定：SpaceConstants 未收编该常量，各处自查同值）。</summary>
        private const int EARTH_BODY_ID = 1;

        // ── 核心数据 ──
        private List<Constellation> constellations = new List<Constellation>();

        /// <summary>星座 id 单调计数器（A5a 统一；≤0 = 未初始化 → 首次分配时播种）。</summary>
        private int nextConstellationId;
        /// <summary>派生索引：groupId → 星座 id 集（多归属）。由成员表全量推导，读档后惰性重建。</summary>
        private readonly ConstellationIndex index = new ConstellationIndex();
        /// <summary>据点 → 锚点 group 登记表（WorldObject.ID → groupId）：防重复创建 + 惰性回收。</summary>
        private readonly Dictionary<int, int> settlementToAnchor = new Dictionary<int, int>();

        /// <summary>惰性就绪标记：false 时首次使用会重建索引 + 校验悬挂引用（读档后 groups 可能尚未就绪）。</summary>
        private bool indexesReady;
        /// <summary>FinalizeInit 已跑过（新游戏与读档两条路径都会走到）：在此之前 groups 的完整性不可信。</summary>
        private bool gameReady;

        // 性能第 5 条：每次 API 调用都要取，按 Game 缓存（GameComponentRef 跨档自动失效）
        private readonly GameComponentRef<SpaceGameComp> spaceGameRef = new GameComponentRef<SpaceGameComp>();
        /// <summary>当前已订阅 GroupDestroyed 的宿主（防重复订阅；换档自动改订）。</summary>
        private SpaceGameComp? subscribedComp;

        /// <summary>锚点回收收集区（收集-执行两段式：不在迭代成员表时改集合）。</summary>
        private readonly List<int> anchorsToReleaseScratch = new List<int>();
        /// <summary>存活世界物体 id 集合缓存（惰性回收用；一次扫表、整轮复用）。</summary>
        private readonly HashSet<int> liveWorldObjectScratch = new HashSet<int>();

        /// <summary>详细日志开关（默认关；Debug 菜单「PFL 测试」页签切换）。</summary>
        public static bool DebugDetailedConstellation = false;

        public ConstellationGameComp(Game game) { }

        // ════════════════════════════════════════════════════════════
        //  就绪 / 订阅（惰性）
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 惰性订阅 <see cref="SpaceGameComp.GroupDestroyed"/>：RimWorld 不保证 GameComponent 构造顺序，
        /// 构造期取组件可能为 null，故不在构造器订阅；每次入口 + FinalizeInit 补订阅，且按宿主身份去重。
        /// </summary>
        private SpaceGameComp? EnsureSubscribed()
        {
            var sg = spaceGameRef.Get();
            if (sg == null) return null;
            if (!ReferenceEquals(subscribedComp, sg))
            {
                if (subscribedComp != null) subscribedComp.GroupDestroyed -= OnGroupDestroyed;
                sg.GroupDestroyed += OnGroupDestroyed;
                subscribedComp = sg;
            }
            return sg;
        }

        /// <summary>
        /// 就绪检查：反查索引与登记表都是派生数据，读档后（或换档后）惰性重建。
        /// groups 完整性不可信时（组件为 null / 尚未 FinalizeInit）只建不依赖 groups 的反查索引，
        /// **悬挂校验与锚点登记表延后**——否则会把"还没载入的群组"误判成悬挂引用而整批清掉成员。
        /// </summary>
        private SpaceGameComp? EnsureReady()
        {
            var sg = EnsureSubscribed();
            if (indexesReady) return sg;
            if (sg == null || !gameReady)
            {
                RebuildReverseIndex();
                return sg;
            }
            RebuildIndexes(sg);
            indexesReady = true;
            return sg;
        }

        /// <summary>全量重建派生数据：悬挂引用校验 → 同星座内去重 → 反查索引 → 锚点登记表。</summary>
        private void RebuildIndexes(SpaceGameComp sg)
        {
            int dangling = 0;
            foreach (var c in constellations)
            {
                if (c == null) continue;
                dangling += c.Members.RemoveAll(m => sg.GetGroupById(m.groupId) == null);
            }

            int duplicates = 0;
            foreach (var c in constellations)
            {
                if (c == null) continue;
                var seen = new HashSet<int>();
                duplicates += c.Members.RemoveAll(m => !seen.Add(m.groupId));
            }

            RebuildReverseIndex();

            settlementToAnchor.Clear();
            foreach (var c in constellations)
            {
                if (c == null) continue;
                foreach (var m in c.Members)
                {
                    var g = sg.GetGroupById(m.groupId);
                    if (g != null && g.IsSurfaceAnchor && g.anchorWorldObjectId >= 0)
                        settlementToAnchor[g.anchorWorldObjectId] = g.GroupId;
                }
            }

            if (dangling > 0)
                Log.Warning(string.Format("[星座] 读档校验：{0} 个悬挂成员引用已移除（群组不存在）", dangling));
            if (duplicates > 0)
                Log.Warning(string.Format("[星座] 读档校验：{0} 个重复成员已移除（同星座内 groupId 重复）", duplicates));
            if (DebugDetailedConstellation)
                Log.Message(string.Format("[星座] 派生数据重建完成：{0} 星座 / {1} 有归属群组 / {2} 锚点登记",
                    constellations.Count, index.GroupCount, settlementToAnchor.Count));
        }

        private void RebuildReverseIndex()
        {
            index.Clear();
            foreach (var c in constellations)
            {
                if (c == null) continue;
                for (int i = 0; i < c.Members.Count; i++)
                    index.Add(c.Members[i].groupId, c.ConstellationId);
            }
        }

        // ════════════════════════════════════════════════════════════
        //  M1 星座 CRUD
        // ════════════════════════════════════════════════════════════

        /// <summary>全部星座（只读视图；增删走本组件 API）。</summary>
        public IReadOnlyList<Constellation> AllConstellations => constellations;

        public Constellation? GetConstellation(int constellationId)
        {
            for (int i = 0; i < constellations.Count; i++)
                if (constellations[i].ConstellationId == constellationId)
                    return constellations[i];
            return null;
        }

        /// <summary>按名称查（返回第一个匹配；名称不强制唯一，重名由调用方避免）。</summary>
        public Constellation? FindByName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < constellations.Count; i++)
                if (constellations[i].Name == name)
                    return constellations[i];
            return null;
        }

        /// <summary>创建星座，返回新 id（扫 max+1，与 SpaceGameComp.AllocGroupId 同规则）。</summary>
        public int CreateConstellation(string name)
        {
            EnsureReady();
            int id = AllocConstellationId();
            constellations.Add(new Constellation
            {
                ConstellationId = id,
                Name = string.IsNullOrEmpty(name) ? string.Format("星座{0}", id) : name
            });
            if (DebugDetailedConstellation)
                Log.Message(string.Format("[星座] 创建 C{0}「{1}」", id, name));
            return id;
        }

        /// <summary>删除星座。其成员就地摘除；失去全部引用的锚点 group 走收尸链路释放。</summary>
        public bool DeleteConstellation(int constellationId)
        {
            var sg = EnsureReady();
            var c = GetConstellation(constellationId);
            if (c == null) return false;

            var affected = new List<int>();
            foreach (var m in c.Members)
            {
                index.Remove(m.groupId, constellationId);
                if (!affected.Contains(m.groupId)) affected.Add(m.groupId);
            }
            constellations.Remove(c);

            if (sg != null)
                for (int i = 0; i < affected.Count; i++)
                    MaybeReleaseAnchor(sg, affected[i]);

            if (DebugDetailedConstellation)
                Log.Message(string.Format("[星座] 删除 C{0}「{1}」，摘除 {2} 成员", constellationId, c.Name, affected.Count));
            return true;
        }

        /// <summary>重命名（空名拒绝，返回 false）。</summary>
        public bool RenameConstellation(int constellationId, string newName)
        {
            if (string.IsNullOrEmpty(newName)) return false;
            var c = GetConstellation(constellationId);
            if (c == null) return false;
            c.Name = newName;
            return true;
        }

        /// <summary>
        /// 分配星座 id：**持久化单调计数器**（2026-09-14 随 A5a 统一）。
        /// 旧注释说"不序列化计数器 → 无复用冲突"，实则相反：扫 max+1 是**派生状态**，
        /// 删掉当前最大 id 的星座后，下一个新星座会复用该 id（成员索引/存档引用会认错星座）。
        /// 现在与 SpaceGameComp 的 group/entity 分配同一条规则：单调计数器 + 缺失时一次性自愈播种。
        /// </summary>
        private int AllocConstellationId()
        {
            if (nextConstellationId <= 0) nextConstellationId = MaxLiveConstellationId() + 1;
            return nextConstellationId++;
        }

        private int MaxLiveConstellationId()
        {
            int max = 0;
            for (int i = 0; i < constellations.Count; i++)
                if (constellations[i].ConstellationId > max) max = constellations[i].ConstellationId;
            return max;
        }

        // ════════════════════════════════════════════════════════════
        //  M2 成员管理
        // ════════════════════════════════════════════════════════════

        /// <summary>入编（统一 group 语义）。失败原因见 <see cref="TryAddMember"/>。</summary>
        public bool AddMember(int constellationId, int groupId)
            => TryAddMember(constellationId, groupId) == ConstellationAddResult.Ok;

        /// <summary>
        /// 入编（带原因码）：校验「星座存在 / group 存在 / 星座内未重复 / 圆轨或锚点」。
        /// 多归属照常放行——同一 groupId 可以进不同星座。
        /// </summary>
        public ConstellationAddResult TryAddMember(int constellationId, int groupId)
        {
            var sg = EnsureReady();
            var c = GetConstellation(constellationId);
            if (c == null) return ConstellationAddResult.ConstellationNotFound;

            var g = sg?.GetGroupById(groupId);
            bool already = false;
            for (int i = 0; i < c.Members.Count; i++)
                if (c.Members[i].groupId == groupId) { already = true; break; }

            var result = ConstellationCore.CheckAdd(true, g != null,
                g?.IsCircularOrbit ?? false, g?.IsSurfaceAnchor ?? false, already);
            if (result != ConstellationAddResult.Ok)
            {
                if (DebugDetailedConstellation)
                    Log.Message(string.Format("[星座] 入编拒绝（{0}）：C{1} ← G{2}",
                        result, constellationId, groupId));
                return result;
            }

            // 元数据本期不启用：role 空 / slotIndex = -1（设计 §3 D3）
            c.Members.Add(new ConstellationMember { groupId = groupId, slotIndex = -1 });
            index.Add(groupId, constellationId);
            if (DebugDetailedConstellation)
                Log.Message(string.Format("[星座] 入编 C{0}「{1}」← G{2}（{3}）",
                    constellationId, c.Name, groupId, g != null && g.IsSurfaceAnchor ? "锚点" : "圆轨"));
            return ConstellationAddResult.Ok;
        }

        /// <summary>
        /// 据点入编便捷入口：worldObjectId 对应的 Settlement → 懒创建锚点 group → AddMember。
        /// 一个据点跨多个星座共享同一个锚点 group（settlementToAnchor 防重复创建）。
        /// </summary>
        public bool AddSettlementMember(int constellationId, int worldObjectId)
        {
            var sg = EnsureReady();
            if (sg == null) return false;
            // 先验星座再建锚点：星座 id 拼错时不留下一只"无人认领"的锚点 group
            if (GetConstellation(constellationId) == null)
            {
                Log.Warning(string.Format("[星座] 据点入编失败：星座 C{0} 不存在", constellationId));
                return false;
            }
            var settlement = ResolveSettlement(worldObjectId);
            if (settlement == null)
            {
                Log.Warning(string.Format("[星座] 据点入编失败：世界物体 {0} 不存在（或已摧毁）", worldObjectId));
                return false;
            }
            int anchorId = EnsureAnchorGroup(settlement);
            if (anchorId < 0) return false;
            if (AddMember(constellationId, anchorId)) return true;
            // 入编失败（重复/资格）→ 若该锚点此刻无任何星座引用（本次刚建出来的），当场回收，不留孤儿
            MaybeReleaseAnchor(sg, anchorId);
            return false;
        }

        /// <summary>摘除成员。移除后若无星座再引用该锚点 group → 释放锚点。</summary>
        public bool RemoveMember(int constellationId, int groupId)
        {
            var sg = EnsureReady();
            var c = GetConstellation(constellationId);
            if (c == null) return false;
            int removed = c.Members.RemoveAll(m => m.groupId == groupId);
            if (removed == 0) return false;
            index.Remove(groupId, constellationId);
            if (sg != null) MaybeReleaseAnchor(sg, groupId);
            if (DebugDetailedConstellation)
                Log.Message(string.Format("[星座] 摘除 C{0}「{1}」→ G{2}", constellationId, c.Name, groupId));
            return true;
        }

        /// <summary>
        /// 多归属反查：该群组所属的全部星座 id（无归属返回空集）。
        /// 返回的是索引内部集合本体（零分配，符合"实时查询"口径）——调用方若要跨增删持有请自行复制。
        /// </summary>
        public IReadOnlyCollection<int> GetConstellationIdsOfGroup(int groupId)
        {
            EnsureReady();
            return index.Of(groupId);
        }

        /// <summary>星座成员表快照（含未解析条目；结构查询用）。</summary>
        public IReadOnlyList<ConstellationMember> GetMembers(int constellationId)
        {
            var c = GetConstellation(constellationId);
            return c != null ? (IReadOnlyList<ConstellationMember>)c.Members : EmptyMembers;
        }

        private static readonly List<ConstellationMember> EmptyMembers = new List<ConstellationMember>();

        // ════════════════════════════════════════════════════════════
        //  M3 生命周期联动（触发器：被销毁）
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 群组销毁收尸（唯一触发器）：卫星与锚点**统一**走此路径。
        /// 此刻群组仍在 SpaceGameComp.groups 中（DestroyGroup 在第 4 步才注销）→ 能读到 IsSurfaceAnchor。
        /// </summary>
        private void OnGroupDestroyed(int groupId)
        {
            var sg = EnsureReady();
            if (sg == null) return;

            bool removed = RemoveGroupFromAllConstellations(groupId);

            // 锚点：清理「据点 → 锚点」登记表（据点被毁的惰性回收会先清表再销毁，此处幂等兜底）
            var g = sg.GetGroupById(groupId);
            if (g != null && g.IsSurfaceAnchor && g.anchorWorldObjectId >= 0)
                settlementToAnchor.Remove(g.anchorWorldObjectId);

            if (removed && DebugDetailedConstellation)
                Log.Message(string.Format("[星座] 收尸：G{0} 已从全部所属星座摘除", groupId));
        }

        /// <summary>把群组从所有所属星座摘除。索引未就绪时退回全量扫描（罕见路径）。</summary>
        private bool RemoveGroupFromAllConstellations(int groupId)
        {
            bool removed = false;
            if (indexesReady)
            {
                var cids = index.Of(groupId);
                if (cids.Count == 0) return false;
                var ids = new List<int>(cids); // 快照：下方 Remove 会改索引集合
                for (int i = 0; i < ids.Count; i++)
                {
                    var c = GetConstellation(ids[i]);
                    if (c != null && c.Members.RemoveAll(m => m.groupId == groupId) > 0) removed = true;
                    index.Remove(groupId, ids[i]);
                }
            }
            else
            {
                foreach (var c in constellations)
                    if (c != null && c.Members.RemoveAll(m => m.groupId == groupId) > 0) removed = true;
                if (removed) RebuildReverseIndex();
            }
            return removed;
        }

        /// <summary>锚点在无星座引用时释放（DestroyGroup → 复用收尸链路）。</summary>
        private void MaybeReleaseAnchor(SpaceGameComp sg, int groupId)
        {
            var g = sg.GetGroupById(groupId);
            if (g == null || !g.IsSurfaceAnchor) return;
            if (!index.IsUnreferenced(groupId)) return;
            ReleaseAnchorGroupInternal(sg, groupId);
        }

        /// <summary>显式释放锚点组（要求已无星座引用）。返回是否有实际动作。</summary>
        public bool ReleaseAnchorGroup(int groupId)
        {
            var sg = EnsureReady();
            if (sg == null) return false;
            var g = sg.GetGroupById(groupId);
            if (g == null || !g.IsSurfaceAnchor) return false;
            if (!index.IsUnreferenced(groupId))
            {
                Log.Warning(string.Format("[星座] 锚点 G{0} 仍被星座引用，拒绝释放", groupId));
                return false;
            }
            ReleaseAnchorGroupInternal(sg, groupId);
            return true;
        }

        private void ReleaseAnchorGroupInternal(SpaceGameComp sg, int groupId)
        {
            var g = sg.GetGroupById(groupId);
            if (g == null) return;
            if (g.anchorWorldObjectId >= 0) settlementToAnchor.Remove(g.anchorWorldObjectId);
            if (DebugDetailedConstellation)
                Log.Message(string.Format("[星座] 释放锚点 G{0}（据点 {1}）", groupId, g.anchorWorldObjectId));
            sg.DestroyGroup(groupId); // 唯一收尸链路：GroupDestroyed → OnGroupDestroyed（幂等，不会回环）
        }

        // ════════════════════════════════════════════════════════════
        //  地表锚点（据点 → 合成不动 group）
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 据点锚点 group 懒创建（防重复：settlementToAnchor 登记表）。
        /// 位置 = 天体世界位置 + R_body × dir(tile)，静态初始化（编组占位，不参与物理/碰撞/渲染）。
        /// </summary>
        public int EnsureAnchorGroup(Settlement settlement)
        {
            var sg = EnsureReady();
            if (sg == null || settlement == null) return -1;

            if (settlementToAnchor.TryGetValue(settlement.ID, out int existing) && sg.GetGroupById(existing) != null)
                return existing;

            var tile = settlement.Tile;
            int bodyId = ResolveBodyIdForTile(tile);
            var group = new SpaceEntityGroup(sg.AllocGroupId(), settlement.Faction?.loadID ?? 0)
            {
                // 非活动/非移动/非碰撞：对既有全部"遍历 groups"的消费方天然不可见
                IsActive = false,
                IsMovable = false,
                IsCollidable = false,
                IsSurfaceAnchor = true,
                anchorWorldObjectId = settlement.ID,
                CelestialBodyId = bodyId
            };
            group.CurrentOrbit = new OrbitState
            {
                // 位置需 group.CelestialBodyId 已置位（对象初始化器先于本行完成）→ 见 AnchorPositionSystem
                position = AnchorPositionSystem(group, bodyId, tile),
                velocity = DVector3.zero,
                rotation = Quaternion.identity
            };
            sg.RegisterGroup(group);
            settlementToAnchor[settlement.ID] = group.GroupId;

            Log.Message(string.Format("[星座] 创建锚点 G{0}：据点「{1}」(wo{2}) body={3} tile={4} pos=({5:F0},{6:F0},{7:F0})km |pos|={8:F0}km",
                group.GroupId, settlement.Label, settlement.ID, bodyId, tile.tileId,
                group.CurrentOrbit.position.x / 1000.0, group.CurrentOrbit.position.y / 1000.0,
                group.CurrentOrbit.position.z / 1000.0,
                group.CurrentOrbit.position.magnitude / 1000.0)); // 根行星锚点：|pos| 应等于 R_body（系统系原点在地球）
            return group.GroupId;
        }

        /// <summary>
        /// 锚点世界位置（**系统系** = 根行星为原点）：天体在该系中的位置 + R_body × tile 固连方向。
        /// 天体位置必须走 <see cref="SpaceEntityGroup.GetCentralBodyOffset"/> 的口径（行星 → 零；卫星 → 绕行星偏移），
        /// 与碰撞检查（SpaceGameComp.GetBodyOffsetSystem）、圆轨解析（GetCircularPositionAtTick）、
        /// N 体积分器（NBodyIntegrator 用 GetBodyPositionParent）同系。
        /// ⚠ 不可改用 CelestialBodyManager.GetBodyPositionAbs：那是"递归到恒星"的绝对位置
        /// （坐标系约定的**唯一出处**就在那个方法的文档块里）
        /// （地球绕日 orbitRadius = 120,000 km），当成系统系坐标会把地球锚点甩到 12 万 km 外，且随公转时变
        /// ——2026-09-10 初版即此错，游戏内日志 pos=(116920,3284,-3453)km 暴露。
        /// </summary>
        private DVector3 AnchorPositionSystem(SpaceEntityGroup group, int bodyId, PlanetTile tile)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            PlanetLayer? layer = null;
            double radius = SpaceConstants.PlanetRadiusMeters;
            if (mgr != null)
            {
                layer = GetSurfaceState.GetLayerForBody(mgr, bodyId);
                radius = GetSurfaceState.BodyRadius(mgr, bodyId);
            }
            layer ??= tile.Layer ?? Find.WorldGrid?.Surface;
            Vector3 tileCenter = layer != null ? layer.GetTileCenter(tile.tileId) : Vector3.up;
            // 调用时 group.CelestialBodyId 必须已置位（GetCentralBodyOffset 据此判定行星/卫星口径）
            DVector3 frameOffset = group.GetCentralBodyOffset(TimeScaleManager.GetPhysicsTickSafe());
            return ConstellationCore.AnchorPositionSystem(frameOffset, new DVector3(tileCenter.x, tileCenter.y, tileCenter.z), radius);
        }

        /// <summary>tile 所属天体（按 PlanetLayer 反查；无匹配退回地球）。月球 tile 链见设计 §12（本期不启用）。</summary>
        private static int ResolveBodyIdForTile(PlanetTile tile)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var layer = tile.Layer;
            if (mgr == null || layer == null) return EARTH_BODY_ID;
            foreach (var body in mgr.GetAllBodies())
            {
                if (GetSurfaceState.GetLayerForBody(mgr, body.bodyId) == layer)
                    return body.bodyId;
            }
            return EARTH_BODY_ID;
        }

        /// <summary>世界物体 id → Settlement（AllWorldObjects 是存活的权威表：Destroyed 的已被移除）。</summary>
        private static Settlement? ResolveSettlement(int worldObjectId)
        {
            var holder = Find.WorldObjects;
            if (holder == null) return null;
            var all = holder.AllWorldObjects;
            for (int i = 0; i < all.Count; i++)
                if (all[i] is Settlement s && s.ID == worldObjectId)
                    return s;
            return null;
        }

        // ════════════════════════════════════════════════════════════
        //  M4 查询（实时读 groups，无派生缓存）
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 成员 + 实时群组状态（一轮一发：先做锚点惰性回收，再解析 group 引用）。
        /// 返回快照列表而非惰性迭代器——调用方（UI / 部署链）常在迭代中做增删，实时读不回写。
        /// </summary>
        public IEnumerable<(ConstellationMember member, SpaceEntityGroup group)> GetMembersWithState(int constellationId)
        {
            var sg = EnsureReady();
            var c = GetConstellation(constellationId);
            var result = new List<(ConstellationMember, SpaceEntityGroup)>();
            if (c == null || sg == null) return result;

            ValidateAnchorMembers(sg); // 惰性回收：据点被摧毁（原版无事件）→ 摘成员 + 释放锚点

            for (int i = 0; i < c.Members.Count; i++)
            {
                var g = sg.GetGroupById(c.Members[i].groupId);
                if (g != null) result.Add((c.Members[i], g));
            }
            return result;
        }

        /// <summary>该星座的「有效成员」数：成员资格与在轨状态解耦，此处按当下仍圆轨的口径实时过滤（设计 §3 资格保留）。</summary>
        public int CountMembersOnOrbit(int constellationId)
        {
            int n = 0;
            foreach (var pair in GetMembersWithState(constellationId))
                if (!pair.group.IsSurfaceAnchor && pair.group.IsCircularOrbit) n++;
            return n;
        }

        /// <summary>
        /// 锚点惰性回收：据点被摧毁时原版**不发事件**，只能在查询侧反查 Settlement 存活。
        /// 收集-执行两段式：先摘成员，再统一 DestroyGroup（不在迭代成员表时改集合/销毁群组）。
        /// </summary>
        private void ValidateAnchorMembers(SpaceGameComp sg)
        {
            if (settlementToAnchor.Count == 0) return;

            anchorsToReleaseScratch.Clear();
            bool liveLoaded = false;

            for (int ci = 0; ci < constellations.Count; ci++)
            {
                var c = constellations[ci];
                if (c == null) continue;
                for (int i = c.Members.Count - 1; i >= 0; i--)
                {
                    int groupId = c.Members[i].groupId;
                    var g = sg.GetGroupById(groupId);
                    if (g == null)
                    {
                        // 悬挂兜底（正常由 GroupDestroyed 收尸；此处防"事件丢失"的单向泄漏）
                        c.Members.RemoveAt(i);
                        index.Remove(groupId, c.ConstellationId);
                        continue;
                    }
                    if (!g.IsSurfaceAnchor || g.anchorWorldObjectId < 0) continue;

                    if (!liveLoaded)
                    {
                        BuildLiveWorldObjectIds();
                        liveLoaded = true;
                    }
                    if (liveWorldObjectScratch.Contains(g.anchorWorldObjectId)) continue;

                    c.Members.RemoveAt(i);
                    index.Remove(groupId, c.ConstellationId);
                    anchorsToReleaseScratch.Add(groupId);
                    Log.Message(string.Format("[星座] 据点 wo{0} 已摧毁 → 摘除成员并回收锚点 G{1}",
                        g.anchorWorldObjectId, groupId));
                }
            }

            for (int i = 0; i < anchorsToReleaseScratch.Count; i++)
                ReleaseAnchorGroupInternal(sg, anchorsToReleaseScratch[i]);
            anchorsToReleaseScratch.Clear();
        }

        private void BuildLiveWorldObjectIds()
        {
            liveWorldObjectScratch.Clear();
            var holder = Find.WorldObjects;
            if (holder == null) return;
            var all = holder.AllWorldObjects;
            for (int i = 0; i < all.Count; i++)
            {
                var wo = all[i];
                if (wo != null && !wo.Destroyed) liveWorldObjectScratch.Add(wo.ID);
            }
        }

        // ════════════════════════════════════════════════════════════
        //  M5 序列化
        // ════════════════════════════════════════════════════════════

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref constellations, "constellations", LookMode.Deep);
            Scribe_Values.Look(ref nextConstellationId, "nextConstellationId", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (constellations == null) constellations = new List<Constellation>();
                // 派生数据（反查索引 / 锚点登记表）不序列化：读档后由成员表全量推导。
                // 此刻 groups 未必就绪 → 惰性延后到首次使用（EnsureReady）。
                index.Clear();
                settlementToAnchor.Clear();
                indexesReady = false;
            }
        }

        /// <summary>
        /// 读档/新游戏收尾（两条路径都会走到）：补订阅（构造期取不到 SpaceGameComp）+ 立刻做一次
        /// 派生数据重建——此刻 groups 已载完，是一次性完成悬挂校验的正确时点；取不到组件时保持惰性。
        /// </summary>
        public override void FinalizeInit()
        {
            base.FinalizeInit();
            EnsureSubscribed();
            gameReady = true;
            EnsureReady();
        }
    }
}
