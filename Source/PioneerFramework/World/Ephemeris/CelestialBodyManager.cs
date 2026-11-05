using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using PioneerFramework.Celestial;

namespace PioneerFramework
{
    /// <summary>
    /// 天体管理器 GameComponent。
    /// </summary>
    public partial class CelestialBodyManager : GameComponent
    {
        /// <summary>
        /// **带缓存的管理器自取**（R5，2026-09-26）：并行区里 `Current.Game.GetComponent&lt;T&gt;()` 是一次
        /// **组件表线性扫描**（`GameComponentRef` 的注释原话），而"每组每步"的量级让它同时是性能问题与
        /// "相位里问引擎"的坏味道。本方法走**既有的** <see cref="GameComponentRef{T}"/>（不新造第二套机制）：
        /// 首次实查后是纯缓存命中 ⇒ 相位里零扫描、零写入。
        /// ⚠ 首次实查若**发生在相位里**，`GameComponentRef` 的 miss 路径会打一条 G1/R5 告警；
        ///   覆盖不到"绕过 `GameComponentRef` 直接调 `GetComponent`"——那条只能靠验收判据守。
        /// </summary>
        public static CelestialBodyManager? GetCached() => selfRef.Get();

        private static readonly GameComponentRef<CelestialBodyManager> selfRef =
            new();

        private Dictionary<int, CelestialBodyInfo> bodiesById = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        // ── P32（2026-09-27）：**参数无关的天体列表在初始化时建一次** ──────────────
        // 原来 `GetAllBodies()` / `GetPlanets()` 每次都 `Values…ToList()` ⇒ **一次分配一份新 List**；
        // 而两者分别有 **12 / 5** 处调用（渲染侧每帧就有几处）⇒ 每帧 GC。
        // 依据与 `systemMembersCache` **同一条**：天体 Def 是静态的（`EnsureInitialized` 里建好后只读）
        // ⇒ "构建一次即可"本来就写在 `systemMembersCache` 的注释里，这里只是把它贯彻到这两个列表上。
        // ⚠ **调用方只读**（实测 17 处无一处 `Add/Remove/Sort`）——文档注释里写明这条规则。
        // ⚠ 带参数的兄弟（`GetBodiesByType`/`GetChildrenOf`/`GetMoonsOf`）**不动**：它们当前 0 调用者，
        //   按"先有替代再改"的纪律不顺手优化（要做也是另一件事：按参数记忆化）。
        // ⚠ 声明处就赋空表（**nullable 分析要求**，与 `systemMembersCache` 同写法）⇒ 新增警告 0；
        //   访问器一律先 `EnsureInitialized()` ⇒ 返回时必然已是真表（空表只在"初始化之前"这一瞬可见）。
        private IReadOnlyList<CelestialBodyInfo> cachedAllBodies = System.Array.Empty<CelestialBodyInfo>();
        private List<CelestialBodyInfo> cachedPlanets = new();
        private bool initialized;

        /// <summary>行星系成员缓存（根天体 id → 该系全部天体，含根）。天体 Def 静态，构建一次即可。</summary>
        private Dictionary<int, List<CelestialBodyInfo>> systemMembersCache = new();

        // ── S-a（2026-09-27）：**每 (系统, tick) 只建一次引力源表** ─────────────────────────
        // 问题：`CollectSystemSources` 原来**每群每步**调一次（N 个组 = N 遍"查成员字典 + 走父链 +
        //   缓存命中判定 + `GravitySource` 构造入列"）。天体位置本身有 `BodySampleCache` 记忆化
        //   （按 (body,tick) **逐位**命中）⇒ 重复的**不是轨道求值**，是上面那一串清单构建。
        //   实测：`[步耗时]` 的"建表段"占**引力场段 80%**（≈1.3µs/次，随 N 线性涨）。
        // 做法：**进并行区之前、主线程**对"本步用到的每个系统"建一次；相位里只**拷贝**它。
        //   ⚠ 拷贝而不是"改用引用"：`scratch.all` 有 8 个**只读**消费点（轨道保持差分 / `TryFindSource` /
        //     `FieldAccelJerk` / `ComputeFieldMinusPlan`）⇒ 拷进 `scratch.all` 让**所有消费者一行不改**。
        //   ⚠ **等价性**：预取走的是**同一个** `CollectSystemSources(同 bodyId, 同 tick)`
        //     ⇒ 内容与**顺序**逐位相同 ⇒ float 求和结果不变（顺序不变即结果不变）。
        //   ⚠ **tick 校验**：分子步档（`timeScaleManager.SubSteps`）里 tick 逐子步变化 ⇒ 预取表**不适用**
        //     ⇒ 不匹配就退回逐群收集（一次 double 比较）。
        private sealed class PrefetchedSources
        {
            public double tick;
            public readonly List<GravitySource> srcs = new();
        }

        /// <summary>SOI 偏移表：每系统每 tick 预取一次 `systemRoot → body` 的帧偏移（组内只做加法）。</summary>
        private sealed class PrefetchedSoiOffsets
        {
            public double tick;
            public readonly Dictionary<int, (DVector3 Position, DVector3 Velocity)> byBody = new();
        }

        private readonly Dictionary<int, PrefetchedSoiOffsets> soiOffsetPrefetch = new();

        private readonly Dictionary<int, PrefetchedSources> systemSourcesPrefetch =
            new();

        /// <summary>读数：本步预取了几张表（= 本步用到的系统数）。主线程写。</summary>
        public static long DebugSourcesPrefetched;
        /// <summary>读数：多少次**用上了预取表**（**正对照**：&gt;0 才说明预取真被消费 ——
        /// 否则"建表段下降"也可能是"根本没建"）。⚠ 相位里递增 ⇒ 原子（聚合量那一档）。</summary>
        public static long DebugSourcesHits;
        /// <summary>读数：多少次**退回逐群收集**（分子步档 / 未预取的 bodyId）。⚠ 相位里递增 ⇒ 原子。</summary>
        public static long DebugSourcesFallback;

        /// <summary>`systemMembersCache` 建立以来的**未命中次数**（**只作读数**：预暖自己第一次取就是要建表，合法）。</summary>
        public static long DebugSystemMembersMisses;

        /// <summary>**在并行区相位里**写 `systemMembersCache` 的次数（R1 的**直接判据**；正常应恒为 0）。</summary>
        public static long DebugParallelRegionCacheWrites;

        private static long parallelWriteWarned;

        private static readonly List<CelestialBodyInfo> EmptyBodyList = new();

        // ── 天体-棋子双向索引 ──
        private Dictionary<int, HashSet<int>> bodyToGroupIds = new();
        private Dictionary<int, HashSet<int>> bodyToEntityIds = new();
        private Dictionary<int, int> groupToBodyId = new();
        private Dictionary<int, int> entityToBodyId = new();

        public CelestialBodyManager(Game game)
        {
            hostGame = game;
        }

        private CelestialRuntime? celestialRuntime;
        internal bool TryGetCelestialRuntime(out CelestialRuntime runtime)
        {
            runtime = celestialRuntime!;
            return runtime != null;
        }
        internal CelestialResult<bool> AttachCelestialRuntime(CelestialRuntime runtime)
        {
            if (celestialRuntime != null && celestialRuntime != runtime)
                return CelestialResult.Reject<bool>(new CelestialError("RuntimeAlreadyAttached"));
            if (celestialRuntime != runtime) runtime.AttachConsumerResources(new ConsumerResources(this));
            celestialRuntime = runtime;
            initialized = false;
            return CelestialResult.Ok(true);
        }
        internal void DetachCelestialRuntime(CelestialRuntime runtime)
        {
            if (celestialRuntime == runtime) celestialRuntime = null;
        }

        private void EnsureInitialized()
        {
            // 并行计算使用主线程已发布的实例表，禁止在工作线程取宿主锁或重建缓存。
            if ((SpaceGameComp.ParallelPhysicsInProgress || ParallelOps.InParallelRegion) && initialized) return;
            if (celestialRuntime != null)
            {
                var catalog = celestialRuntime.CaptureCatalog();
                if (initialized && instanceCatalogRevision == catalog.Revision && instanceSession == celestialRuntime.WorldSessionId &&
                    instanceRestorePending == celestialRuntime.IsRestoring) return;
                BuildInstanceTable(celestialRuntime, catalog);
            }
            else
            {
                if (initialized) return;
                bodiesById = new();
            }
            initialized = true;
            bodySlots = new Dictionary<int, int>(bodiesById.Count);
            int slot = 0;
            foreach (var pair in bodiesById) bodySlots[pair.Key] = slot++;
            parentSamples.EnsureCapacity(slot); parentSamples.Reset();
            absSamples.EnsureCapacity(slot); absSamples.Reset();
            systemMembersCache.Clear(); systemSourcesPrefetch.Clear(); soiOffsetPrefetch.Clear();
            cachedAllBodies = bodiesById.Values.ToList().AsReadOnly();
            cachedPlanets = bodiesById.Values.Where(body => body.bodyType == CelestialBodyType.Planet).ToList();
            foreach (var body in bodiesById.Values) { body.EnsureOrbitFrame(); body.EnsureAtmosphereProfile(); }
            MechJebLib.Utils.Logger.GlobalRegister(o => Log.Message("[MJLib] " + o));
        }

        // ── 查询方法 ──

        public CelestialBodyInfo? GetBodyById(int bodyId)
        {
            EnsureInitialized();
            bodiesById.TryGetValue(bodyId, out var def);
            return def;
        }

        /// <summary>全部天体（**返回的是初始化时建好的缓存 List**：P32 —— 调用方**只读**，不许改）。</summary>
        public IReadOnlyList<CelestialBodyInfo> GetAllBodies()
        {
            EnsureInitialized();
            return cachedAllBodies;
        }

        public List<CelestialBodyInfo> GetBodiesByType(CelestialBodyType type)
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.bodyType == type).ToList();
        }

        public List<CelestialBodyInfo> GetGravitySources()
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.IsGravitySource).ToList();
        }

        public List<CelestialBodyInfo> GetChildrenOf(int parentBodyId)
        {
            EnsureInitialized();
            return bodiesById.Values.Where(b => b.ParentBodyId == parentBodyId).ToList();
        }

        /// <summary>行星系成员（含根）：根 = 沿父链遇到的第一个行星或恒星（与 GetReferenceFrameId 同规则）。</summary>
        public List<CelestialBodyInfo> GetSystemMembers(int anyBodyId)
        {
            EnsureInitialized();

            int rootId = GetReferenceFrameId(anyBodyId);
            if (rootId < 0) return EmptyBodyList;

            if (systemMembersCache.TryGetValue(rootId, out var cached))
                return cached;

            var members = new List<CelestialBodyInfo>();
            var visited = new HashSet<int>();
            var frontier = new Queue<int>();
            frontier.Enqueue(rootId);
            while (frontier.Count > 0)
            {
                int id = frontier.Dequeue();
                if (!visited.Add(id)) continue;
                var body = GetBodyById(id);
                if (body == null) continue;
                members.Add(body);
                foreach (var child in GetChildrenOf(id))
                    frontier.Enqueue(child.bodyId);
            }

            DebugSystemMembersMisses++;

            // ⭐⭐ **R1 的直接判据（2026-09-26 实测抓到过：277 次 IndexOutOfRange）**
            //   判的是**真正的不变式**：**建表不许发生在"并行区相位"里**。
            //   ⚠ 第一版我用"当前线程 != 构造器捕获的线程 id"来判 —— **那是错的**：本组件的构造
            //     并不在游戏主循环那个线程上（RimWorld 读档/建 GameComponent 会在别的线程），
            //     于是**主线程的合法调用被误报**（实测栈明明是 `… UpdateAllMovements ← GameComponentTick
            //     ← Verse.Root_Play.Update`，主线程）。
            //   ⇒ 改用 `SpaceGameComp.ParallelPhysicsInProgress`：主线程在 `Parallel.For` 前后置位，
            //     它表达的正是"此刻处于并行区相位"，与线程身份无关。
            //   成本：只在这条罕见路径上一次静态 bool 读 ⇒ 稳态零开销。
            if (SpaceGameComp.ParallelPhysicsInProgress)
            {
                System.Threading.Interlocked.Increment(ref DebugParallelRegionCacheWrites);
                if (System.Threading.Interlocked.CompareExchange(ref parallelWriteWarned, 1L, 0L) == 0L)
                    Log.Error("[CelestialBodyManager] **在并行区相位里建表**（R1）：rootId=" + rootId +
                        " ⇒ 32+ 线程并发写同一个 Dictionary，会破坏桶链（实测 IndexOutOfRangeException）。" +
                        "调用者应在进并行区前预暖（见 SpaceGameComp.UpdateAllMovements 的 R1 修复）。栈：" +
                        System.Environment.NewLine + System.Environment.StackTrace);
                // G1 的中央守卫（计数与首报）：与上面那条**不是重复判据**，是**子集**关系 ——
                // `DebugParallelRegionCacheWrites` 只数"这个字典建表"（R1 的判据，语义窄），
                // `ParallelRegionGuard.SharedWrites` 数"任何被贴了守卫的共享写"（G1 的判据，语义宽）。
                // ⇒ 两条读数**不可互换**：拿宽的去判 R1 会把 R2/R3/R4 的写入混进来。
                // ⚠ 参数是**编译期字面量**（守卫在热路径上，拼接字符串=每次一次分配，违反并行区零分配）。
                //    rootId 由上面那条 R1 专属报错给出（它只在首次报出时打，不在热路径稳态上）。
                ParallelRegionGuard.GuardWrite("CelestialBodyManager.GetSystemMembers 建表");
            }

            systemMembersCache[rootId] = members;
            return members;
        }

        /// <summary>
        /// 收集该行星系内**全部天体**在系统系（根天体为中心、根钉住）内的位置/速度。
        /// 根的贡献在原点、速度为零；子天体沿父链累加（GetBodyPositionParent 是相对父天体的）。
        ///
        /// 只计入 GM &gt; 0 的天体（无质量装饰天体不产生引力）；**根例外**——根是参考系锚点，
        /// GM 缺失时按 SpaceConstants 兜底（沿用旧 EffectiveMu 语义，保证根一定在场）。
        /// </summary>
        public void CollectSystemSources(int anyBodyId, double tick, List<GravitySource> into)
        {
            if (into == null) return;
            into.Clear();

            // 位置基准 = 帧根（与 positionSystem 同系）。成员按中心天体挑：
            //   · 帧根（原点）
            //   · 中心天体本身 + 其直接子天体
            //   · 中心的整条祖先链（含父恒星）——所有 Group 都受恒星引力
            // 恒星帧：只算恒星（帧根 + 兄弟/后代恒星）。
            int rootId = GetReferenceFrameId(anyBodyId);
            var root = rootId >= 0 ? GetBodyById(rootId) : null;
            var center = GetBodyById(anyBodyId);
            if (root == null || center == null) return;

            into.Add(new GravitySource(DVector3.zero, DVector3.zero,
                celestialRuntime == null ? NBodyIntegrator.EffectiveMu(root.gravitationalParameter) : root.gravitationalParameter, rootId));

            // 恒星帧：**中心天体是恒星**时只算恒星（多星系统含兄弟恒星；行星/卫星不算）。
            if (center.bodyType == CelestialBodyType.Star)
            {
                var starMembers = GetSystemMembers(rootId);
                for (int i = 0; i < starMembers.Count; i++)
                {
                    var other = starMembers[i];
                    if (other.bodyId == rootId || other.bodyType != CelestialBodyType.Star) continue;
                    if (other.gravitationalParameter <= 0.0) continue;
                    AccumulateToRoot(other, rootId, tick, out var op, out var ov);
                    into.Add(new GravitySource(op, ov, other.gravitationalParameter, other.bodyId));
                }
                return;
            }

            void AddRelative(CelestialBodyInfo body)
            {
                if (body.bodyId == rootId || body.gravitationalParameter <= 0.0) return;
                AccumulateToRoot(body, rootId, tick, out var p, out var v);
                into.Add(new GravitySource(p, v, body.gravitationalParameter, body.bodyId));
            }

            AddRelative(center);
            foreach (var child in GetChildrenOf(center.bodyId)) AddRelative(child);

            // 祖先在帧根外侧：ancestor 相对帧根 = −(帧根相对 ancestor)，不能从 ancestor 往 root 累加。
            // **恒星祖先 → 均匀场**（BOI 内常向量，方向/大小取帧根处，随公转每 tick 更新）；
            // 行星祖先（卫星帧的父行星）仍按位置算。
            void AddAncestor(CelestialBodyInfo body)
            {
                if (body.bodyId == rootId || body.gravitationalParameter <= 0.0) return;
                AccumulateToRoot(root, body.bodyId, tick, out var rel, out var relVel);
                if (body.bodyType == CelestialBodyType.Star)
                    return; // 希尔盒内：恒星引力被行星系非惯性（公转）抵消，不计（用户 2026-11-05 裁定）
                into.Add(new GravitySource(-rel, -relVel, body.gravitationalParameter, body.bodyId));
            }

            int cur = center.ParentBodyId;
            int guard = bodiesById.Count;
            while (cur >= 0 && guard-- > 0)
            {
                var ancestor = GetBodyById(cur);
                if (ancestor == null) break;
                AddAncestor(ancestor);
                cur = ancestor.ParentBodyId;
            }
        }

        // ── S-a：预取与填充（2026-09-27）──────────────────────────────────────────────
        /// <summary>
        /// **预取一个系统的引力源表**（主线程、进并行区之前调）。同一 (系统, tick) 调多次只有第一次真建
        /// （幂等）⇒ 调用方可以对每个组的 `CelestialBodyId` 都调一遍，代价是 O(1) 字典查。
        /// </summary>
        public void PrefetchSystemSources(int anyBodyId, double tick)
        {
            EnsureInitialized();
            if (anyBodyId < 0) return;

            // 槽按**中心天体**分：同帧根的地球/月球成员不同（月球 SOI 内不含太阳），不能共槽。
            if (systemSourcesPrefetch.TryGetValue(anyBodyId, out var slot) && slot.tick == tick)
                return;   // 本步已建好（幂等）

            if (slot == null)
            {
                slot = new PrefetchedSources();
                systemSourcesPrefetch[anyBodyId] = slot;
            }
            slot.tick = tick;
            // S-a 读数：这一格是"真正走成员/父链/构造"的那一次（主线程）；相位里的只是拷贝。
            long pfClock = PerfProbe.PrefetchBuildStart();
            CollectSystemSources(anyBodyId, tick, slot.srcs);
            PerfProbe.PrefetchBuildEnd(pfClock);
            System.Threading.Interlocked.Increment(ref DebugSourcesPrefetched);
        }

        /// <summary>
        /// **取本步该系统的引力源表（不拷贝）**：本步已预取且 tick **逐位相同** ⇒ 返回**共享表**（只读！）；
        /// 否则返回 null（调用方自己 `CollectSystemSources` 到**本组自己的** list 里）。
        ///
        /// ⚠ **只读约定**：返回的这张表是**多个群共用**的 ⇒ 调用方**绝不许改它**（这一点由类型层面保证：
        /// 只有 `CollectSystemSources` 会往 list 里写，而它只被"填入本组自己 list"的路径调用）。
        /// 设计上刻意**不做"把共享表塞进 `scratch.all`"那种别名**：那会让退回路径可能写进共享表 ✗。
        /// 这里交出去的只是"读哪一张"的选择 ⇒ 引用生命周期与写者边界都保持原样。
        /// </summary>
        public List<GravitySource>? TryGetPrefetchedSources(int anyBodyId, double tick)
        {
            if (anyBodyId >= 0 && systemSourcesPrefetch.TryGetValue(anyBodyId, out var slot) && slot.tick == tick)
            {
                System.Threading.Interlocked.Increment(ref DebugSourcesHits);
                return slot.srcs;
            }
            System.Threading.Interlocked.Increment(ref DebugSourcesFallback);
            return null;
        }

        /// <summary>
        /// **SOI 偏移表预取**（主线程、进并行区前）：每帧根每 tick 一次，
        /// 缓存 `frameRoot → body` 的帧偏移；组内 SOI 判据只做加法，不再每群每步建父链。
        /// </summary>
        public void PrefetchSoiOffsets(int frameRootId, double tick)
        {
            EnsureInitialized();
            if (frameRootId < 0 || !bodiesById.ContainsKey(frameRootId)) return;
            if (soiOffsetPrefetch.TryGetValue(frameRootId, out var slot) && slot.tick == tick) return;
            if (slot == null)
            {
                slot = new PrefetchedSoiOffsets();
                soiOffsetPrefetch[frameRootId] = slot;
            }
            else slot.byBody.Clear();
            slot.tick = tick;
            foreach (var body in bodiesById.Values)
                slot.byBody[body.bodyId] = GetFrameOffset(frameRootId, body.bodyId, tick);
        }

        /// <summary>取本 tick 缓存的 `frameRoot → body` 帧偏移；未预取/tick 不匹配返回 false。</summary>
        public bool TryGetSoiOffset(int frameRootId, int bodyId, double tick,
            out (DVector3 Position, DVector3 Velocity) offset)
        {
            if (soiOffsetPrefetch.TryGetValue(frameRootId, out var slot) && slot.tick == tick
                && slot.byBody.TryGetValue(bodyId, out offset))
                return true;
            offset = default;
            return false;
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

            NBodyIntegrator.FieldAccelJerk(pos, vel, sources, out var fieldAcc, out var fieldJerk);
            NBodyIntegrator.ComputeBodyAccelJerk(pos, vel, center.position, center.velocity, center.mu,
                out var twoBody, out var twoBodyJerk);

            // 中心天体在参考系里自由落体：把它沿**整条父链**受到的引力全部从场里减掉
            // （月球：地球 + 太阳；地球：太阳），而不是只减直接父体。
            DVector3 forced = DVector3.zero, forcedJerk = DVector3.zero;
            var centerDef = GetBodyById(centerBodyId);
            int cur = centerDef?.ParentBodyId ?? -1;
            int guard = bodiesById.Count;
            while (cur >= 0 && guard-- > 0)
            {
                if (NBodyIntegrator.TryFindSource(sources, cur, out var ancestor))
                {
                    if (ancestor.isUniform)
                    {
                        forced += ancestor.uniformAccel; // 均匀场在中心处就是该常向量
                    }
                    else
                    {
                        NBodyIntegrator.ComputeBodyAccelJerk(center.position, center.velocity,
                            ancestor.position, ancestor.velocity, ancestor.mu, out var a, out var j);
                        forced += a; forcedJerk += j;
                    }
                }
                var ancestorDef = GetBodyById(cur);
                cur = ancestorDef?.ParentBodyId ?? -1;
            }

            acc = fieldAcc - twoBody - forced;
            jerk = fieldJerk - twoBodyJerk - forcedJerk;
        }

        /// <summary>把天体的位置/速度从"相对父天体"累加到"相对根天体"（不含根自身轨道）。</summary>
        private void AccumulateToRoot(CelestialBodyInfo body, int rootId, double tick, out DVector3 pos, out DVector3 vel)        {
            pos = DVector3.zero;
            vel = DVector3.zero;

            var cur = body;
            int remaining = bodiesById.Count;
            while (cur != null && cur.bodyId != rootId)
            {
                if (remaining-- <= 0) throw new System.InvalidOperationException("CelestialParentCycle");
                pos += GetBodyPositionParent(cur.bodyId, tick);
                vel += GetBodyVelocityParent(cur.bodyId, tick);
                cur = cur.ParentBodyId >= 0 ? GetBodyById(cur.ParentBodyId) : null;
            }
        }

        public CelestialBodyInfo? GetStar()
        {
            EnsureInitialized();
            if (celestialRuntime != null) return GetPrimaryLightInstance();
            return bodiesById.Values.FirstOrDefault(b => b.bodyType == CelestialBodyType.Star);
        }

        /// <summary>
        /// **行星**（不含恒星、不含卫星）。
        ///
        /// ⚠ 2026-09-24 修：原来还要求 `ParentBodyId == -1`（"父天体 id 为 -1"，本意 = 绕恒星）——**恒不成立**：
        /// 星系定义里地球写的是 `<parentBodyDefName>Sun</parentBodyDefName>` ⇒ 它的 `ParentBodyId` 是
        /// **太阳的 bodyId**，不是 -1 ⇒ 本方法**永远返回空列表**。唯一调用者 `ViewFrame.RootPlanetId` 恰好有
        /// `planets.Count > 0 ? … : 1` 的兜底 ⇒ 症状被那个硬编码 1 盖住了（"根行星"实际一直写死为 1）。
        /// 行星的定义本来就是"绕恒星"⇒ 过滤器只留 `bodyType`。
        /// </summary>
        /// <summary>全部行星（**返回初始化时建好的缓存 List**：P32 —— 调用方**只读**，不许改）。</summary>
        public List<CelestialBodyInfo> GetPlanets()
        {
            EnsureInitialized();
            return cachedPlanets;
        }

        public List<CelestialBodyInfo> GetMoonsOf(int planetBodyId)
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
            if (!bodiesById.ContainsKey(bodyId) || !CanAttachConsumer(bodyId))
            {
                Log.Warning($"[CelestialBodyManager] Cannot register group {groupId} to unknown body {bodyId}.");
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
            if (!bodiesById.ContainsKey(bodyId) || !CanAttachConsumer(bodyId))
            {
                Log.Warning($"[CelestialBodyManager] Cannot register entity {entityId} to unknown body {bodyId}.");
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
            if (body.InstanceId.IsValid)
                return GetBodyById(body.ReferenceFrameBodyId)?.hasSurface == true ? body.ReferenceFrameBodyId : -1;

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
            if (body.InstanceId.IsValid) return body.ReferenceFrameBodyId;

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
        private readonly BodySampleCache parentSamples = new();
        private readonly BodySampleCache absSamples = new();

        /// <summary>bodyId → 槽位（`EnsureInitialized` 里重建后**只读**；并发读字典安全，写只落在数组元素上）。</summary>
        private Dictionary<int, int> bodySlots = new();

        private int SlotOf(int bodyId)
            => bodySlots.TryGetValue(bodyId, out var s) ? s : -1;

        /// <summary>
        /// 计算天体在给定 tick 的**绝对位置**（递归到恒星：地球的值里含着 120,000 km 的绕日偏移）。
        ///
        /// ⚠ **坐标系约定 —— 全库唯一出处**。历史上两次事故都源于跨系混用
        /// （2026-09-10 星座锚点把绝对位置当系统系用 → 12 万 km 帧错；2026-09-14 弹头引力
        /// 拿系统系坐标减绝对坐标 → 太阳项被算成 800 m/s²）。改相关代码前先读这一段：
        ///
        ///   · **系统系**（根行星/恒星为中心、根钉住）：
        ///     `SpaceEntityGroup.CurrentOrbit.position`、`ProjectileInstance.position`、
        ///     `CollectSystemSources` 给出的源位置。**碰撞 / 圆轨解析 / N 体积分 / 制导全在此系** ——
        ///     做这些运算就别离开系统系（这也是 A0 让"引力不需要环绕天体参量"的前提）。
        ///   · **天体局部系**（相对该天体的父天体）：
        ///     `GetBodyPositionParent/Velocity(bodyId)`、`SpaceEntityGroup.positionBody`（相对 `CelestialBodyId`）、
        ///     `GetCentralBodyOffset`。
        ///   · **绝对系**（递归到恒星）：本方法。**只用于渲染与跨系展示**（星图 / 3D 视图 / 轨道线）。
        ///     拿它去减系统系坐标 = 凭空叠加一个行星公转偏移，量级上直接毁掉结果。
        ///
        ///   跨系运算必须**显式换算**（例：目标系坐标 = 该天体在目标系中的位置 + 对象在目标系中的偏移）。
        ///   相关交叉引用：`ConstellationGameComp.AnchorPositionSystem`、`ConstellationCore.AnchorPositionSystem`、
        ///   `ConstellationSelfTest`（独立口径校验）、`NBodyIntegrator.FieldMinusPlanAccelJerk`。
        /// </summary>
        public DVector3 GetBodyPositionAbs(int bodyId, double tick)
        {
            var body = GetBodyById(bodyId);
            if (body == null) return DVector3.zero;
            if (celestialRuntime != null) return SampleInstanceMotion(body, tick).Position;

            int slot = SlotOf(bodyId);
            if (slot >= 0 && absSamples.TryGet(slot, tick, out var cPos, out _))
            {
                if (PerfProbe.Enabled) PerfProbe.NoteBodyPosHit();
                return cPos;
            }

            var pos = GetBodyPositionAbs(body, tick);
            var vel = GetBodyVelocityAbs(body, tick);
            absSamples.Store(slot, tick, pos, vel);   // slot<0 时 Store 空转（未登记天体：不缓存）
            if (PerfProbe.Enabled) PerfProbe.NoteBodyPosMiss();
            return pos;
        }

        private DVector3 GetBodyPositionAbs(CelestialBodyInfo body, double tick)
        {
            DVector3 localPos = GetBodyPositionParentCore(body, tick);

            if (body.ParentBodyId >= 0)
            {
                var parent = GetBodyById(body.ParentBodyId);
                if (parent != null)
                    return localPos + GetBodyPositionAbs(parent, tick);
            }

            return localPos;
        }

        /// <summary>
        /// 计算天体在给定 tick 的世界空间速度（圆轨解析）。
        /// 速度方向沿轨道切向，大小 = ω × orbitRadius。
        /// 对于 Star 或没有轨道的天体返回零向量。
        /// </summary>
        public DVector3 GetBodyVelocityAbs(int bodyId, double tick)
        {
            var body = GetBodyById(bodyId);
            if (body == null) return DVector3.zero;
            if (celestialRuntime != null) return SampleInstanceMotion(body, tick).LinearVelocity;
            if (!body.HasOrbit) return DVector3.zero;

            int slot = SlotOf(bodyId);
            if (slot >= 0 && absSamples.TryGet(slot, tick, out _, out var cVel))
            {
                if (PerfProbe.Enabled) PerfProbe.NoteBodyPosHit();
                return cVel;
            }

            var pos = GetBodyPositionAbs(body, tick);
            var vel = GetBodyVelocityAbs(body, tick);
            absSamples.Store(slot, tick, pos, vel);
            if (PerfProbe.Enabled) PerfProbe.NoteBodyPosMiss();
            return vel;
        }

        private DVector3 GetBodyVelocityAbs(CelestialBodyInfo body, double tick)
        {
            if (!body.HasOrbit) return DVector3.zero;

            DVector3 localVel = GetBodyVelocityParentCore(body, tick);

            // 若绕父天体公转，父天体本身也可能在运动 → 叠加父天体速度
            if (body.ParentBodyId >= 0)
            {
                var parent = GetBodyById(body.ParentBodyId);
                if (parent != null)
                    return localVel + GetBodyVelocityAbs(parent, tick);
            }

            return localVel;
        }

        /// <summary>
        /// （无缓存）天体在其父天体中心系中的轨道位置 = R·(cos·refX + sin·refY)。
        /// **全库唯一出处**——世界系递归（`GetBodyPositionAbs`）、局部系缓存入口
        /// （`GetBodyPositionParent`）都经此，相位/参考系算式不再有第二份（改这里 = 改全部调用方）。
        /// （`GetOrbitPathUnity` 不在列：它画的是整条椭圆，逐点取 angle=2πi/N，不评估相位。）
        /// </summary>
        private DVector3 GetBodyPositionParentCore(CelestialBodyInfo body, double tick)
        {
            if (celestialRuntime?.Physics is ExistingCelestialEphemerisBridge) return SampleExistingParent(body, tick).Position;
            if (celestialRuntime != null)
                return SampleInstanceMotion(body, tick).Position - (body.ParentBodyId < 0 ? DVector3.zero : GetBodyPositionAbs(body.ParentBodyId, tick));
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
        private DVector3 GetBodyVelocityParentCore(CelestialBodyInfo body, double tick)
        {
            if (celestialRuntime?.Physics is ExistingCelestialEphemerisBridge) return SampleExistingParent(body, tick).Velocity;
            if (celestialRuntime != null)
                return SampleInstanceMotion(body, tick).LinearVelocity - (body.ParentBodyId < 0 ? DVector3.zero : GetBodyVelocityAbs(body.ParentBodyId, tick));
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
        public DVector3 GetBodyPositionParent(int bodyId, double tick)
        {
            var body = GetBodyById(bodyId);
            if (body != null && celestialRuntime != null) return GetBodyPositionParentCore(body, tick);
            if (body == null || !body.HasOrbit) return DVector3.zero;

            int slot = SlotOf(bodyId);
            if (slot >= 0 && parentSamples.TryGet(slot, tick, out var cPos, out _))
            {
                if (PerfProbe.Enabled) PerfProbe.NoteBodyPositionParentHit();
                if (PerfProbe.Enabled && PerfProbe.ShadowChecks)
                {
                    // 影子：命中时用无缓存算式再算一遍。算式本身已无第二份（同一 Core），所以这里
                    // 能抓的只有**采样与 tick 不一致**（撕裂写入 / 缓存陈旧）。2026-09-14 就是这么
                    // 抓到"值类型元组 + Dictionary"版本在 Parallel.For 下的撕裂（3 次，Δ=5–31 m）。
                    PerfProbe.BodyLocalShadow(bodyId, cPos, GetBodyPositionParentCore(body, tick), tick, "局部位置");
                }
                return cPos;
            }

            var pos = GetBodyPositionParentCore(body, tick);
            var vel = GetBodyVelocityParentCore(body, tick);
            parentSamples.Store(slot, tick, pos, vel);   // slot<0 = 未登记天体：不缓存，直接算
            if (PerfProbe.Enabled) PerfProbe.NoteBodyPositionParentMiss();
            return pos;
        }

        /// <summary>
        /// 计算天体在其父天体中心坐标系中的轨道速度。
        /// 对没有轨道或 Star 类型返回零向量。
        /// </summary>
        public DVector3 GetBodyVelocityParent(int bodyId, double tick)
        {
            var body = GetBodyById(bodyId);
            if (body != null && celestialRuntime != null) return GetBodyVelocityParentCore(body, tick);
            if (body == null || !body.HasOrbit) return DVector3.zero;

            int slot = SlotOf(bodyId);
            if (slot >= 0 && parentSamples.TryGet(slot, tick, out _, out var cVel))
            {
                if (PerfProbe.Enabled) PerfProbe.NoteBodyPositionParentHit();
                if (PerfProbe.Enabled && PerfProbe.ShadowChecks)
                    PerfProbe.BodyLocalShadow(bodyId, cVel, GetBodyVelocityParentCore(body, tick), tick, "局部速度");
                return cVel;
            }

            var pos = GetBodyPositionParentCore(body, tick);
            var vel = GetBodyVelocityParentCore(body, tick);
            parentSamples.Store(slot, tick, pos, vel);
            if (PerfProbe.Enabled) PerfProbe.NoteBodyPositionParentMiss();
            return vel;
        }

        /// <summary>
        /// from 参考系原点相对 to 参考系原点的偏移（位置/速度），沿父链 LCA 求和，**不经过绝对帧**。
        /// 出/进 SOI 换系用；同体返回零。薄适配：真正的 LCA/求和/搬运在 FrameTransitionCore（纯核，已单测）。
        /// </summary>
        internal (DVector3 Position, DVector3 Velocity) GetFrameOffset(int fromBodyId, int toBodyId, double tick)
        {
            if (fromBodyId == toBodyId) return (DVector3.zero, DVector3.zero);
            return FrameTransitionCore.Offset(BuildFramePath(fromBodyId, tick), BuildFramePath(toBodyId, tick));
        }

        private List<FrameNode> BuildFramePath(int bodyId, double tick)
        {
            var path = new List<FrameNode>(8);
            var visited = new HashSet<int>();
            int id = bodyId;
            while (id >= 0)
            {
                if (!visited.Add(id)) throw new InvalidOperationException($"FrameCycle: {bodyId}");
                var body = GetBodyById(id) ?? throw new InvalidOperationException($"BodyMissing: {id}");
                path.Add(new FrameNode(id, body.ParentBodyId, GetBodyPositionParent(id, tick), GetBodyVelocityParent(id, tick)));
                id = body.ParentBodyId;
            }
            return path;
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
                result[kv.Key] = GetBodyPositionAbs(kv.Key, tick);
            }
            return result;
        }

        /// <summary>
        /// 生成天体的轨道路径采样点（用于绘制轨道线）。
        /// 返回世界空间中的 Unity 单位位置列表。
        /// </summary>
        public List<Vector3>? GetOrbitPathUnity(int bodyId, int tick, int segments = 64)
        {
            var body = GetBodyById(bodyId);
            if (body == null || !body.HasOrbit) return null;

            DVector3 centerAbs = DVector3.zero;
            if (body.ParentBodyId >= 0)
            {
                var parent = GetBodyById(body.ParentBodyId);
                if (parent != null)
                    centerAbs = GetBodyPositionAbs(parent, tick);
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
                DVector3 pSystem = centerAbs + body.orbitRadius * (cos * refX + sin * refY);
                points.Add((Vector3)(pSystem * toUnity));
            }

            return points;
        }

        public static Color GetBodyColor(CelestialBodyInfo body)
            => body.bodyType switch
            {
                CelestialBodyType.Star => new Color(1f, 0.8f, 0.2f),
                CelestialBodyType.Planet => new Color(0.2f, 0.5f, 0.8f),
                CelestialBodyType.Moon => new Color(0.7f, 0.7f, 0.7f),
                _ => Color.white,
            };

        public override void ExposeData()
        {
            base.ExposeData();
            ExposeCelestialData();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                initialized = false;
            }
        }
        private CelestialMotionState GetOfflineBodyMotion(CelestialBodyInfo body, double seconds, DQuaternion orientation, DVector3 angularVelocity)
            => new(body.DescriptorRevision, GetBodyPositionAbs(body.bodyId, seconds), GetBodyVelocityAbs(body.bodyId, seconds), orientation, angularVelocity);
    }
}
