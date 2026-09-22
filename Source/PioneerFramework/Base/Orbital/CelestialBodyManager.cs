using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 天体管理器 GameComponent。
    /// </summary>
    public class CelestialBodyManager : GameComponent
    {
        private Dictionary<int, CelestialBodyDef> bodiesById;
        private bool initialized;

        /// <summary>行星系成员缓存（根天体 id → 该系全部天体，含根）。天体 Def 静态，构建一次即可。</summary>
        private Dictionary<int, List<CelestialBodyDef>> systemMembersCache = new Dictionary<int, List<CelestialBodyDef>>();

        private static readonly List<CelestialBodyDef> EmptyBodyList = new List<CelestialBodyDef>();

        // ── 天体-棋子双向索引 ──
        private Dictionary<int, HashSet<int>> bodyToGroupIds = new Dictionary<int, HashSet<int>>();
        private Dictionary<int, HashSet<int>> bodyToEntityIds = new Dictionary<int, HashSet<int>>();
        private Dictionary<int, int> groupToBodyId = new Dictionary<int, int>();
        private Dictionary<int, int> entityToBodyId = new Dictionary<int, int>();

        public CelestialBodyManager(Game game)
        {
        }

        private void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;

            var allDefs = DefDatabase<CelestialBodyDef>.AllDefsListForReading;
            bodiesById = new Dictionary<int, CelestialBodyDef>(allDefs.Count);

            foreach (var def in allDefs)
            {
                if (bodiesById.ContainsKey(def.bodyId))
                {
                    Log.Error(string.Format("[CelestialBodyManager] Duplicate bodyId {0} between '{1}' and '{2}'.",
                        def.bodyId, def.defName, bodiesById[def.bodyId].defName));
                    continue;
                }
                bodiesById.Add(def.bodyId, def);
            }

            // ── 将 RimWorld 世界对接到地球天体 ──
            SpaceConstants.SyncWithWorld();

            if (bodiesById.TryGetValue(1, out var earthDef))
            {
                earthDef.radius = SpaceConstants.PlanetRadiusMeters;
                // 仅当 XML 显式定义了有效引力参数时才覆盖默认值，否则保留 SpaceConstants 的硬编码兜底
                if (earthDef.gravitationalParameter > 0.0)
                    SpaceConstants.PrimaryGravitationalParameter = earthDef.gravitationalParameter;
            }
            else
            {
                Log.Warning("[CelestialBodyManager] Earth (bodyId=1) not found in DefDatabase. Cannot sync world radius.");
            }

            // ── 构建 行星→卫星 正向索引（运行时零查询开销）──
            foreach (var def in allDefs)
            {
                if (def.ParentBodyId >= 0 && bodiesById.TryGetValue(def.ParentBodyId, out var parent))
                {
                    parent.moonBodies.Add(def);
                }
            }

            // ── 天体 Def 集被重建（首局 / 读档 PostLoadInit 把它置回未初始化）→ 按 tick 记忆的采样
            //    必须一并作废，且槽位映射要重建：采样按"tick 逐位相等"命中，而读旧档会让 tick 回退到
            //    曾经用过的值（届时若槽位指向别的天体，命中就会给出错天体的值）。
            bodySlots = new Dictionary<int, int>(bodiesById.Count);
            int slot = 0;
            foreach (var kv in bodiesById)
                bodySlots[kv.Key] = slot++;
            localSamples.EnsureCapacity(slot);
            localSamples.Reset();
            worldSamples.EnsureCapacity(slot);
            worldSamples.Reset();
            systemMembersCache.Clear();

            Log.Message(string.Format("[CelestialBodyManager] Loaded {0} celestial bodies, built moon indices. World radius = {1:F0} m ({2} tiles).",
                allDefs.Count, SpaceConstants.PlanetRadiusMeters, (int)(SpaceConstants.PlanetRadiusMeters / SpaceConstants.MetersPerUnit)));

            // ── 构建自证（2026-09-17）────────────────────────────────────────────
            // 为什么必须有：这个 mod"交付物路径只在 Release 生效"，而开发期间每次重建都会覆盖它
            // ⇒ **日志里的读数到底来自哪一版代码**这个问题，历史上反复浪费排查时间
            //   （前两次"探针找不到"的根因都是旧 DLL）。把**内容身份**打进日志，
            //   任何一份日志都能自证版本，不用再靠时间戳猜。
            //   · MVID（模块版本 id）= 每次编译唯一、由内容决定 ⇒ 逐位可比；
            //   · 构建时间 = 人读友好，用来和 `1.6/Assemblies` 的时间戳对。
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var loc = string.IsNullOrEmpty(asm.Location) ? null : asm.Location;
                string built = "?";
                if (loc != null && System.IO.File.Exists(loc))
                    built = System.IO.File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss");
                Log.Message(string.Format(
                    "[PFL] 构建自证：MVID={0}｜DLL 构建时间={1}｜交会新律={2}",
                    asm.ManifestModule.ModuleVersionId, built,
                    SpaceGameComp.UseNewRendezvousLaw ? "开" : "关"));
            }
            catch (System.Exception e)
            {
                Log.Warning("[PFL] 构建自证失败（不影响运行）：" + e.Message);
            }

            // ── 移植面日志桥（2026-09-19）────────────────────────────────────────
            // 为什么必须有：移植来的 `MechJebLib.Utils.Logger` 靠宿主调 `GlobalRegister` 才会有输出，
            // 而在此之前**没有任何地方注册过** ⇒ `Logger.Print` 全库都是**静默空操作**。
            // 实测（2026-09-19）：规划器初始化那句 "发现 N 个操作" 在游戏日志里**查无此句**，
            // 而它其实已经跑过了。
            //
            // **教训：看不见的探针不是探针**——没接桥时，"日志里没有"与"事情没发生"**无法区分**，
            // 而这正是历史上反复浪费时间的那类误判。
            //
            // 注册之后：纯层（不含 Verse）的诊断经这个委托落到游戏日志 ✓ **纯层依旧是纯的**
            //（它只认 `Action<object>`，不认识 `Verse.Log`）。
            MechJebLib.Utils.Logger.GlobalRegister(o => Log.Message("[MJLib] " + o));
        }

        // ── 查询方法 ──

        public CelestialBodyDef GetBodyById(int bodyId)
        {
            EnsureInitialized();
            bodiesById.TryGetValue(bodyId, out var def);
            return def;
        }

        public List<CelestialBodyDef> GetAllBodies()
        {
            EnsureInitialized();
            return bodiesById.Values.ToList();
        }

        public List<CelestialBodyDef> GetBodiesByType(CelestialBodyType type)
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.bodyType == type).ToList();
        }

        public List<CelestialBodyDef> GetGravitySources()
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.IsGravitySource).ToList();
        }

        public List<CelestialBodyDef> GetChildrenOf(int parentBodyId)
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.ParentBodyId == parentBodyId).ToList();
        }

        /// <summary>行星系成员（含根）：根 = 沿父链遇到的第一个行星或恒星（与 GetReferenceFrameId 同规则）。</summary>
        public List<CelestialBodyDef> GetSystemMembers(int anyBodyId)
        {
            EnsureInitialized();

            int rootId = GetReferenceFrameId(anyBodyId);
            if (rootId < 0) return EmptyBodyList;

            if (systemMembersCache.TryGetValue(rootId, out var cached))
                return cached;

            var members = new List<CelestialBodyDef>();
            int guard = 0;
            var frontier = new Queue<int>();
            frontier.Enqueue(rootId);
            while (frontier.Count > 0 && guard++ < 64)
            {
                int id = frontier.Dequeue();
                var body = GetBodyById(id);
                if (body == null) continue;
                members.Add(body);
                foreach (var child in GetChildrenOf(id))
                    frontier.Enqueue(child.bodyId);
            }

            systemMembersCache[rootId] = members;
            return members;
        }

        /// <summary>
        /// 收集该行星系内**全部天体**在系统系（根天体为中心、根钉住）内的位置/速度。
        /// 根的贡献在原点、速度为零；子天体沿父链累加（GetBodyLocalPosition 是相对父天体的）。
        ///
        /// 只计入 GM &gt; 0 的天体（无质量装饰天体不产生引力）；**根例外**——根是参考系锚点，
        /// GM 缺失时按 SpaceConstants 兜底（沿用旧 EffectiveMu 语义，保证根一定在场）。
        /// </summary>
        public void CollectSystemSources(int anyBodyId, double tick, List<GravitySource> into)
        {
            if (into == null) return;
            into.Clear();

            var members = GetSystemMembers(anyBodyId);
            if (members.Count == 0) return;

            int rootId = members[0].bodyId;

            for (int i = 0; i < members.Count; i++)
            {
                var body = members[i];
                if (body.bodyId == rootId)
                {
                    into.Add(new GravitySource(DVector3.zero, DVector3.zero,
                        NBodyIntegrator.EffectiveMu(body.gravitationalParameter), body.bodyId));
                    continue;
                }

                if (body.gravitationalParameter <= 0.0) continue;

                DVector3 pos, vel;
                AccumulateToRoot(body, rootId, tick, out pos, out vel);
                into.Add(new GravitySource(pos, vel, body.gravitationalParameter, body.bodyId));
            }
        }

        /// <summary>
        /// **"场 − 计划"差分**（= 参考天体局部系里的潮汐/间接项）。这是全库唯一的差分口径：
        /// 验算仪器（PerfProbe 探针、GravityFieldSelfTest 电池）与轨道保持的抵消量都用它，
        /// 避免两处各写一遍再漂（2026-09-14 曾因此把"地球全量"误报成"潮汐"，实测 6.86%）。
        ///
        /// 计划 = 中心天体的二体项 + 中心天体**自身受迫**加速度（其父天体在中心天体处的拉力）。
        /// 物理含义：局部系随中心天体自由落体，故"外面那个天体"的均匀项被抵消，只剩差分。
        /// 1 层层级（如地球→月球）精确；更深的链需要逐级递归（PFL 数据无此情形，已注释）。
        /// </summary>
        public void ComputeFieldMinusPlan(DVector3 pos, DVector3 vel, int centerBodyId,
            List<GravitySource> sources, out DVector3 acc, out DVector3 jerk)
        {
            acc = DVector3.zero;
            jerk = DVector3.zero;
            if (sources == null || sources.Count == 0) return;

            bool hasCenter = NBodyIntegrator.TryFindSource(sources, centerBodyId, out var center);
            if (!hasCenter)
            {
                // 中心天体不在场里（如深空群组）：无计划可言，差分退化为全量场
                NBodyIntegrator.FieldAccelJerk(pos, vel, sources, out acc, out jerk);
                return;
            }

            var centerDef = GetBodyById(centerBodyId);
            GravitySource parent = default;
            bool hasParent = false;
            if (centerDef != null && centerDef.ParentBodyId >= 0)
                hasParent = NBodyIntegrator.TryFindSource(sources, centerDef.ParentBodyId, out parent);

            NBodyIntegrator.FieldMinusPlanAccelJerk(pos, vel, center, hasCenter, parent, hasParent,
                sources, out acc, out jerk);
        }

        /// <summary>把天体的位置/速度从"相对父天体"累加到"相对根天体"（不含根自身轨道）。</summary>
        private void AccumulateToRoot(CelestialBodyDef body, int rootId, double tick, out DVector3 pos, out DVector3 vel)        {
            pos = DVector3.zero;
            vel = DVector3.zero;

            var cur = body;
            int guard = 0;
            while (cur != null && cur.bodyId != rootId && guard++ < 64)
            {
                pos += GetBodyLocalPosition(cur.bodyId, tick);
                vel += GetBodyLocalVelocity(cur.bodyId, tick);
                cur = cur.ParentBodyId >= 0 ? GetBodyById(cur.ParentBodyId) : null;
            }
        }

        public CelestialBodyDef GetStar()
        {
            EnsureInitialized();
            return bodiesById.Values.FirstOrDefault(b => b.bodyType == CelestialBodyType.Star);
        }

        public List<CelestialBodyDef> GetPlanets()
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.bodyType == CelestialBodyType.Planet && b.ParentBodyId == -1).ToList();
        }

        public List<CelestialBodyDef> GetMoonsOf(int planetBodyId)
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.bodyType == CelestialBodyType.Moon && b.ParentBodyId == planetBodyId).ToList();
        }

        public string GetBodyName(int bodyId)
        {
            var def = GetBodyById(bodyId);
            return def != null ? def.label : "UnknownBody_" + bodyId;
        }

        // ── 天体-棋子双向索引管理 ──

        public void RegisterGroupToBody(int groupId, int bodyId)
        {
            EnsureInitialized();
            if (!bodiesById.ContainsKey(bodyId))
            {
                Log.Warning(string.Format("[CelestialBodyManager] Cannot register group {0} to unknown body {1}.", groupId, bodyId));
                return;
            }
            UnregisterGroupFromBody(groupId);

            groupToBodyId[groupId] = bodyId;
            if (!bodyToGroupIds.TryGetValue(bodyId, out var set))
            {
                set = new HashSet<int>();
                bodyToGroupIds[bodyId] = set;
            }
            set.Add(groupId);
        }

        public void UnregisterGroupFromBody(int groupId)
        {
            if (groupToBodyId.TryGetValue(groupId, out var oldBodyId))
            {
                groupToBodyId.Remove(groupId);
                if (bodyToGroupIds.TryGetValue(oldBodyId, out var set))
                {
                    set.Remove(groupId);
                    if (set.Count == 0)
                        bodyToGroupIds.Remove(oldBodyId);
                }
            }
        }

        public void RegisterEntityToBody(int entityId, int bodyId)
        {
            EnsureInitialized();
            if (!bodiesById.ContainsKey(bodyId))
            {
                Log.Warning(string.Format("[CelestialBodyManager] Cannot register entity {0} to unknown body {1}.", entityId, bodyId));
                return;
            }
            UnregisterEntityFromBody(entityId);

            entityToBodyId[entityId] = bodyId;
            if (!bodyToEntityIds.TryGetValue(bodyId, out var set))
            {
                set = new HashSet<int>();
                bodyToEntityIds[bodyId] = set;
            }
            set.Add(entityId);
        }

        public void UnregisterEntityFromBody(int entityId)
        {
            if (entityToBodyId.TryGetValue(entityId, out var oldBodyId))
            {
                entityToBodyId.Remove(entityId);
                if (bodyToEntityIds.TryGetValue(oldBodyId, out var set))
                {
                    set.Remove(entityId);
                    if (set.Count == 0)
                        bodyToEntityIds.Remove(oldBodyId);
                }
            }
        }

        public IReadOnlyCollection<int> GetGroupIdsForBody(int bodyId)
        {
            if (bodyToGroupIds.TryGetValue(bodyId, out var set))
                return set;
            return System.Array.Empty<int>();
        }

        public IReadOnlyCollection<int> GetEntityIdsForBody(int bodyId)
        {
            if (bodyToEntityIds.TryGetValue(bodyId, out var set))
                return set;
            return System.Array.Empty<int>();
        }

        public int GetBodyIdForGroup(int groupId)
        {
            return groupToBodyId.TryGetValue(groupId, out var bodyId) ? bodyId : -1;
        }

        public int GetBodyIdForEntity(int entityId)
        {
            return entityToBodyId.TryGetValue(entityId, out var bodyId) ? bodyId : -1;
        }

        /// <summary>
        /// 获取任意天体所属的根行星 ID。
        /// 月球 → 地球 ID；地球 → 自身 ID；恒星 → -1（不使用区块系统）。
        /// </summary>
        public int GetRootPlanetId(int bodyId)
        {
            var body = GetBodyById(bodyId);
            if (body == null) return -1;

            while (body != null && body.bodyType != CelestialBodyType.Planet)
            {
                if (body.ParentBodyId < 0)
                    return -1;
                body = GetBodyById(body.ParentBodyId);
            }

            return body?.bodyId ?? -1;
        }

        /// <summary>
        /// 获取天体所在的参考系 ID（粗网格分区键）。
        /// 行星 → 自身 ID；卫星 → 根行星 ID；恒星 → 恒星 ID；深空 → -1。
        /// </summary>
        public int GetReferenceFrameId(int bodyId)
        {
            var body = GetBodyById(bodyId);
            if (body == null) return -1;

            while (body != null)
            {
                if (body.bodyType == CelestialBodyType.Planet)
                    return body.bodyId;
                if (body.bodyType == CelestialBodyType.Star)
                    return body.bodyId;
                if (body.ParentBodyId < 0)
                    break;
                body = GetBodyById(body.ParentBodyId);
            }
            return -1;
        }

        // ── 轨道位置计算 ──

        // 第 8 条（2026-09-10）世界系 + 第 24 条（2026-09-14）局部系：天体位置/速度是 (bodyId, tick) 的
        // **纯函数**（递归父链 + 每级 Cos/Sin），同一个 tick 内会被反复查询 —— 世界系来自射弹弹道
        // （每段每引力源一次）、局部系来自 A0 之后的群组引力场（每群组每步一次）。故按天体槽位记住
        // "最近一次算过的 tick → 位置/速度"。
        // 缓存本体是 **BodySampleCache**（纯类型，见其文档）：不可变采样 + 原子槽位写入 ——
        // 因为群组物理在 Parallel.For 里，这些缓存会被多线程并发读写（2026-09-14 游戏内实测到
        // 值类型元组 + Dictionary 版本的撕裂写入，详见那个类型的注释）。
        private readonly BodySampleCache localSamples = new BodySampleCache();
        private readonly BodySampleCache worldSamples = new BodySampleCache();

        /// <summary>bodyId → 槽位（`EnsureInitialized` 里重建后**只读**；并发读字典安全，写只落在数组元素上）。</summary>
        private Dictionary<int, int> bodySlots = new Dictionary<int, int>();

        private int SlotOf(int bodyId)
            => bodySlots.TryGetValue(bodyId, out var s) ? s : -1;

        /// <summary>
        /// 计算天体在给定 tick 的**绝对位置**（递归到恒星：地球的值里含着 120,000 km 的绕日偏移）。
        ///
        /// ⚠ **坐标系约定 —— 全库唯一出处**。历史上两次事故都源于跨系混用
        /// （2026-09-10 星座锚点把绝对位置当群组系用 → 12 万 km 帧错；2026-09-14 弹头引力
        /// 拿系统系坐标减绝对坐标 → 太阳项被算成 800 m/s²）。改相关代码前先读这一段：
        ///
        ///   · **系统系**（根行星/恒星为中心、根钉住）：
        ///     `SpaceEntityGroup.CurrentOrbit.position`、`ProjectileInstance.position`、
        ///     `CollectSystemSources` 给出的源位置。**碰撞 / 圆轨解析 / N 体积分 / 制导全在此系** ——
        ///     做这些运算就别离开系统系（这也是 A0 让"引力不需要环绕天体参量"的前提）。
        ///   · **天体局部系**（相对该天体的父天体）：
        ///     `GetBodyLocalPosition/Velocity(bodyId)`、`SpaceEntityGroup.localPosition`（相对 `CelestialBodyId`）、
        ///     `GetCentralBodyOffset`。
        ///   · **绝对系**（递归到恒星）：本方法。**只用于渲染与跨系展示**（星图 / 3D 视图 / 轨道线）。
        ///     拿它去减系统系坐标 = 凭空叠加一个行星公转偏移，量级上直接毁掉结果。
        ///
        ///   跨系运算必须**显式换算**（例：目标系坐标 = 该天体在目标系中的位置 + 对象在目标系中的偏移）。
        ///   相关交叉引用：`ConstellationGameComp.AnchorWorldPosition`、`ConstellationCore.AnchorWorldPosition`、
        ///   `ConstellationSelfTest`（独立口径校验）、`NBodyIntegrator.FieldMinusPlanAccelJerk`。
        /// </summary>
        public DVector3 GetBodyWorldPosition(int bodyId, double tick)
        {
            var body = GetBodyById(bodyId);
            if (body == null) return DVector3.zero;

            int slot = SlotOf(bodyId);
            if (slot >= 0 && worldSamples.TryGet(slot, tick, out var cPos, out _))
            {
                if (PerfProbe.Enabled) PerfProbe.NoteBodyPosHit();
                return cPos;
            }

            var pos = GetBodyWorldPosition(body, tick);
            var vel = GetBodyWorldVelocity(body, tick);
            worldSamples.Store(slot, tick, pos, vel);   // slot<0 时 Store 空转（未登记天体：不缓存）
            if (PerfProbe.Enabled) PerfProbe.NoteBodyPosMiss();
            return pos;
        }

        private DVector3 GetBodyWorldPosition(CelestialBodyDef body, double tick)
        {
            DVector3 localPos = GetBodyLocalPositionCore(body, tick);

            if (body.ParentBodyId >= 0)
            {
                var parent = GetBodyById(body.ParentBodyId);
                if (parent != null)
                    return localPos + GetBodyWorldPosition(parent, tick);
            }

            return localPos;
        }

        /// <summary>
        /// 计算天体在给定 tick 的世界空间速度（圆轨解析）。
        /// 速度方向沿轨道切向，大小 = ω × orbitRadius。
        /// 对于 Star 或没有轨道的天体返回零向量。
        /// </summary>
        public DVector3 GetBodyWorldVelocity(int bodyId, double tick)
        {
            var body = GetBodyById(bodyId);
            if (body == null || !body.HasOrbit) return DVector3.zero;

            int slot = SlotOf(bodyId);
            if (slot >= 0 && worldSamples.TryGet(slot, tick, out _, out var cVel))
            {
                if (PerfProbe.Enabled) PerfProbe.NoteBodyPosHit();
                return cVel;
            }

            var pos = GetBodyWorldPosition(body, tick);
            var vel = GetBodyWorldVelocity(body, tick);
            worldSamples.Store(slot, tick, pos, vel);
            if (PerfProbe.Enabled) PerfProbe.NoteBodyPosMiss();
            return vel;
        }

        private DVector3 GetBodyWorldVelocity(CelestialBodyDef body, double tick)
        {
            if (!body.HasOrbit) return DVector3.zero;

            DVector3 localVel = GetBodyLocalVelocityCore(body, tick);

            // 若绕父天体公转，父天体本身也可能在运动 → 叠加父天体速度
            if (body.ParentBodyId >= 0)
            {
                var parent = GetBodyById(body.ParentBodyId);
                if (parent != null)
                    return localVel + GetBodyWorldVelocity(parent, tick);
            }

            return localVel;
        }

        /// <summary>
        /// （无缓存）天体在其父天体中心系中的轨道位置 = R·(cos·refX + sin·refY)。
        /// **全库唯一出处**——世界系递归（`GetBodyWorldPosition`）、局部系缓存入口
        /// （`GetBodyLocalPosition`）都经此，相位/参考系算式不再有第二份（改这里 = 改全部调用方）。
        /// （`GetOrbitPathUnity` 不在列：它画的是整条椭圆，逐点取 angle=2πi/N，不评估相位。）
        /// </summary>
        private DVector3 GetBodyLocalPositionCore(CelestialBodyDef body, double tick)
        {
            if (!body.HasOrbit) return DVector3.zero;

            double angularVelocity = 2.0 * System.Math.PI / body.orbitPeriod;
            double phase = body.initialPhase + angularVelocity * tick;

            DVector3 refX, refY;
            body.GetOrbitFrame(out refX, out refY);

            double cos = System.Math.Cos(phase);
            double sin = System.Math.Sin(phase);
            return body.orbitRadius * (cos * refX + sin * refY);
        }

        /// <summary>（无缓存）局部轨道速度 = ωR·(-sin·refX + cos·refY)。同上，唯一出处。</summary>
        private DVector3 GetBodyLocalVelocityCore(CelestialBodyDef body, double tick)
        {
            if (!body.HasOrbit) return DVector3.zero;

            double angularVelocity = 2.0 * System.Math.PI / body.orbitPeriod;
            double phase = body.initialPhase + angularVelocity * tick;

            DVector3 refX, refY;
            body.GetOrbitFrame(out refX, out refY);

            double cos = System.Math.Cos(phase);
            double sin = System.Math.Sin(phase);
            return (body.orbitRadius * angularVelocity) * (-sin * refX + cos * refY);
        }

        // 第 24 条（2026-09-14）：**局部系**变体此前没有缓存。A0 之后每个群组每物理步都要经
        // CollectSystemSources → AccumulateToRoot 沿父链对每颗子天体各查一次位置/速度，于是
        // 同一 tick 内同一颗月球被**每个群组各重算一遍**（N 组 × 天体数 × 2 次三角）。
        // 与第 8 条同型：局部位置是 (bodyId, tick) 的纯函数（轨道参数来自静态 Def，父链不经此）。
        // 注意读档会在 ExposeData 里把 initialized 置回 false，而 tick 会回退到曾经用过的值
        // ——所以 EnsureInitialized 重建天体表时必须 Reset 这些缓存（连同槽位映射一起重建）。

        /// <summary>
        /// 计算天体在其父天体中心坐标系中的轨道位置。
        /// 例如月球的返回值是「以地球为原点的月球轨道位置」。
        /// 对没有轨道或 Star 类型返回零向量。
        /// </summary>
        public DVector3 GetBodyLocalPosition(int bodyId, double tick)
        {
            var body = GetBodyById(bodyId);
            if (body == null || !body.HasOrbit) return DVector3.zero;

            int slot = SlotOf(bodyId);
            if (slot >= 0 && localSamples.TryGet(slot, tick, out var cPos, out _))
            {
                if (PerfProbe.Enabled) PerfProbe.NoteBodyLocalPosHit();
                if (PerfProbe.Enabled && PerfProbe.ShadowChecks)
                {
                    // 影子：命中时用无缓存算式再算一遍。算式本身已无第二份（同一 Core），所以这里
                    // 能抓的只有**采样与 tick 不一致**（撕裂写入 / 缓存陈旧）。2026-09-14 就是这么
                    // 抓到"值类型元组 + Dictionary"版本在 Parallel.For 下的撕裂（3 次，Δ=5–31 m）。
                    PerfProbe.BodyLocalShadow(bodyId, cPos, GetBodyLocalPositionCore(body, tick), tick, "局部位置");
                }
                return cPos;
            }

            var pos = GetBodyLocalPositionCore(body, tick);
            var vel = GetBodyLocalVelocityCore(body, tick);
            localSamples.Store(slot, tick, pos, vel);   // slot<0 = 未登记天体：不缓存，直接算
            if (PerfProbe.Enabled) PerfProbe.NoteBodyLocalPosMiss();
            return pos;
        }

        /// <summary>
        /// 计算天体在其父天体中心坐标系中的轨道速度。
        /// 对没有轨道或 Star 类型返回零向量。
        /// </summary>
        public DVector3 GetBodyLocalVelocity(int bodyId, double tick)
        {
            var body = GetBodyById(bodyId);
            if (body == null || !body.HasOrbit) return DVector3.zero;

            int slot = SlotOf(bodyId);
            if (slot >= 0 && localSamples.TryGet(slot, tick, out _, out var cVel))
            {
                if (PerfProbe.Enabled) PerfProbe.NoteBodyLocalPosHit();
                if (PerfProbe.Enabled && PerfProbe.ShadowChecks)
                    PerfProbe.BodyLocalShadow(bodyId, cVel, GetBodyLocalVelocityCore(body, tick), tick, "局部速度");
                return cVel;
            }

            var pos = GetBodyLocalPositionCore(body, tick);
            var vel = GetBodyLocalVelocityCore(body, tick);
            localSamples.Store(slot, tick, pos, vel);
            if (PerfProbe.Enabled) PerfProbe.NoteBodyLocalPosMiss();
            return vel;
        }

        /// <summary>
        /// 获取所有天体在指定 tick 时的世界空间位置字典。
        /// </summary>
        public Dictionary<int, DVector3> GetAllBodyWorldPositions(double tick)
        {
            EnsureInitialized();
            var result = new Dictionary<int, DVector3>(bodiesById.Count);
            foreach (var kv in bodiesById)
            {
                result[kv.Key] = GetBodyWorldPosition(kv.Key, tick);
            }
            return result;
        }

        /// <summary>
        /// 生成天体的轨道路径采样点（用于绘制轨道线）。
        /// 返回世界空间中的 Unity 单位位置列表。
        /// </summary>
        public List<Vector3> GetOrbitPathUnity(int bodyId, int tick, int segments = 64)
        {
            var body = GetBodyById(bodyId);
            if (body == null || !body.HasOrbit) return null;

            DVector3 centerWorld = DVector3.zero;
            if (body.ParentBodyId >= 0)
            {
                var parent = GetBodyById(body.ParentBodyId);
                if (parent != null)
                    centerWorld = GetBodyWorldPosition(parent, tick);
            }

            DVector3 refX, refY;
            body.GetOrbitFrame(out refX, out refY);

            var points = new List<Vector3>(segments);
            double toUnity = SpaceConstants.UnitsPerMeter;

            for (int i = 0; i < segments; i++)
            {
                double angle = 2.0 * System.Math.PI * i / segments;
                double cos = System.Math.Cos(angle);
                double sin = System.Math.Sin(angle);
                DVector3 pWorld = centerWorld + body.orbitRadius * (cos * refX + sin * refY);
                points.Add((Vector3)(pWorld * toUnity));
            }

            return points;
        }

        public static Color GetBodyColor(CelestialBodyDef body)
        {
            switch (body.bodyType)
            {
                case CelestialBodyType.Star:
                    return new Color(1f, 0.8f, 0.2f);
                case CelestialBodyType.Planet:
                    return new Color(0.2f, 0.5f, 0.8f);
                case CelestialBodyType.Moon:
                    return new Color(0.7f, 0.7f, 0.7f);
                default:
                    return Color.white;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                initialized = false;
            }
        }
    }
}
