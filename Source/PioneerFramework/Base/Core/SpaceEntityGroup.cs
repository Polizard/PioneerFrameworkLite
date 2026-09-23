using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 轨道状态（位置+速度+朝向），用于缓存和计算。
    /// 位置/速度使用 DVector3（双精度）以避免累积误差。
    /// </summary>
    public struct OrbitState : IExposable
    {
        public DVector3 position;
        public DVector3 velocity;
        public Quaternion rotation;

        public void ExposeData()
        {
            DVector3.Scribe("position", ref position);
            DVector3.Scribe("velocity", ref velocity);
            Scribe_Values.Look(ref rotation, "rotation");
        }
    }

    public class SpaceEntityGroup : ISpatialEntity, IExposable
    {
        public int GroupId;
        public int FactionId;

        public bool IsActive = true;
        public bool IsMovable = true;
        public bool IsCollidable = true;
        public bool HasGravityWell = false;

        /// <summary>
        /// 匿踪闸门（[V1]）：false = **公开坐标**（应答机语义，对所有阵营可见、不做任何探测计算）；
        /// true = 走雷达探测，只有在某阵营的认知层里被判定可见才出现。
        ///
        /// **默认值按"是否自报坐标"定**（[N3] 裁定）：飞船 / 空间站 `false`（应答机广播位置），
        /// **导弹与弹头 `true`**（不广播，只能被探测到）——设置点在 `CombatManager` 的两个弹体工厂。
        /// 这不是"隐蔽"而是"是否广播"：不匿踪的船**开雷达也不会更暴露**（本来就公开），
        /// 而匿踪的船一旦主动辐射就立刻可被探测（辐射与暴露的对称性，[E1]）。
        ///
        /// 准入判据（§1.8 [O5]）：它是"关于这艘船的客观事实"（是否在广播）⇒ 属真值层。
        /// ⚠ **目前尚无消费方**——认知层 [V3] 才读它，这是地基步骤的正常状态。
        /// </summary>
        public bool IsStealth = false;

        /// <summary>
        /// 侦测技术三分量（[V1] / W2）：灵敏度 / 抗压制 / 抗欺骗（见 `SensorTech`）。
        ///
        /// **字段名与类型名 `SensorTech` 刻意不同**——同名会让"类内引用该类型"变成字段引用
        /// （`SensorTech x = ...` 报 CS0119），是个会埋很久的坑。取 `DetectTech`（＝侦测技术）避开。
        ///
        /// **来源（[V7]，2026-09-24 起）**：**派生量** —— 由所属各实体的部件聚合
        /// （`ShipStats.sensorRange` / `ecmResist` / `eccmResist`）求和得到，与 <see cref="SignalStrength"/>
        /// **共用一个缓存、同一批失效点**（部件集变化 ⇒ `SpaceEntity.InvalidateStats` ⇒ 本组失效 ✓）。
        ///
        /// ⚠ **不落盘**（序列化已删）：派生量存进去只会在读档时带出一份陈旧副本，
        /// 而"陈旧"正是本库反复踩的坑（`SignalStrength` 那批失效点注释、AVM:824 的派生点清单）。
        /// 原来的"投影写入"形态（[N7] 的建筑级通道）留给 EW **建筑**；**部件级**不需要它——
        /// 部件聚合链是既有的、每帧不需要跑的那种（读触发重算）。
        ///
        /// 消费者：[V3] 认知层（`EffectiveDetectRange` / 抗压制 / 抗欺骗三个判据输入）；
        /// 探针：世界视图面板、`VisibilityProbe` 快照、`FrameworkSelfTest` 的"三分量"项。
        /// </summary>
        public SensorTech DetectTech
        {
            get
            {
                if (derivedDirty) RecomputeDerived();
                return cachedDetect;
            }
        }

        private bool activePing;
        /// <summary>
        /// 主动探测开关（[V1] / [E2]）：`true` = 雷达开机，`false`（默认）= 被动。
        ///
        /// **这是 W1 枢纽（辐射与暴露的对称性）在 Group 上的唯一开关**，两个后果同时发生：
        /// ①自己的 `SignalStrength` 升高 ⇒ **更早被别人发现**（开机即暴露）；
        /// ②有效探测距离提高 ⇒ 看得更远。EW 最经典的取舍。
        ///
        /// **刻意做成属性而非 public 字段**（与 `IsActive` / `IsCollidable` / `HasActiveBurn` 那批不同）：
        /// 它的翻转必须让派生信号失效，而属性 setter 是**唯一写入点**、不可能被绕过。
        /// （对照教训：`HasActiveBurn` 是 public 字段，失效钩子只能挂在 `StartBurn`/`EndBurn` 两处，
        ///  将来谁在别处直接赋值就会静默过期——新字段有机会做对就做对。）
        ///
        /// ⚠ 本步**不建 UI 开关**（Gizmo 属 [E2]，P4），所以除探针外永远为默认值。
        /// </summary>
        public bool ActivePingEnabled
        {
            get => activePing;
            set
            {
                if (activePing == value) return;
                activePing = value;
                derivedDirty = true;   // 派生量（信号 + DetectTech 视角）失效
            }
        }

        // ── 信号强度（[V8]；W1 枢纽的量）──
        // 形态：**带失效钩子的缓存值**。之所以不做"静态项缓存 + 开关项按步读"的拆分
        // （§1.8 [F] 的简化）：[L](b)(c)+[O1] 之后**没有任何东西每步读它**——探测只在
        // "读触发刷新"时读，所以既不需要每步重算，也不需要把算式拆成两半。
        private double cachedSignal;
        private SensorTech cachedDetect;
        private bool derivedDirty = true;   // 失效位**同时**管 cachedSignal 与 cachedDetect（同一批驱动点，见 InvalidateSignal）

        /// <summary>
        /// 信号强度（[V8]；无量纲，标称 1.0 = 一艘静默基准船）。
        ///
        /// **一个量、两侧共用**（W1）：目标侧"别人探测我时读它"；观测侧"我开机/干扰/转发时它升高"。
        /// 算式本体是纯核心 `SignalModel.Total`——**禁止在别处再写一份**（[C7] 的对称性不变式）。
        ///
        /// 失效由三处驱动：①部件集变化 → `SpaceEntity.InvalidateStats` 收口（唯一的部件变更入口）
        /// ②点火翻转 → `StartBurn` / `EndBurn` ③主动开关翻转 → `ActivePingEnabled` 的 setter。
        /// </summary>
        public double SignalStrength
        {
            get
            {
                if (derivedDirty) RecomputeDerived();
                return cachedSignal;
            }
        }

        /// <summary>
        /// **派生量失效**：一次置脏同时管 `SignalStrength` 与 `DetectTech`（两者同一批驱动点 —— 见 `SignalStrength` 文档的四处）。
        /// 名字保留 `InvalidateSignal`（既有调用点与测试都在用它）；语义已扩到"全部派生量"。
        /// </summary>
        public void InvalidateSignal() => derivedDirty = true;

        private void RecomputeDerived()
        {
            var x = new SignalInputs
            {
                burning = HasActiveBurn,
                activePing = ActivePingEnabled,
                // sharingIntel（[E8] 情报转发）/ revealedByFire（[N6] 开火揭示）尚未落地，
                // 各自会带一个窗口字段；落地时在此读它们。
            };

            var dt = new SensorTech();   // 编队级 = 各实体聚合的**求和**（与实体级聚合同口径）
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg != null)
            {
                foreach (int eid in entityIds)
                {
                    var e = sg.GetEntityById(eid);
                    if (e == null) continue;
                    // 尺寸口径**复用既有约定**（AABB 半对角线：AVM:924 / CombatManager:1138），不新发明一份
                    e.GetWorldAabb(out var mn, out var mx);
                    double r = (mx - mn).magnitude * 0.5;
                    if (r > x.rcsRadiusMeters) x.rcsRadiusMeters = r;
                    // 热/电项今天恒为 0（无组件写那两个槽），组件就位后自动生效（§3.2 的空槽）
                    var st = e.AggregatedStats;
                    x.heatWatts += st.heatProduction + st.heatDissipation;
                    x.powerWatts += st.powerGen + st.powerDraw;
                    // [V7]：侦测技术（传感器组件 / 未来的 EW 建筑投影都进这里）
                    dt.sensitivityMeters += st.sensorRange;
                    dt.ecmResist += st.ecmResist;
                    dt.eccmResist += st.eccmResist;
                }
            }

            cachedSignal = SignalModel.Total(x);
            cachedDetect = dt;
            derivedDirty = false;
        }

        public OrbitState CurrentOrbit;
        public bool OrbitDirty = true;

        private HashSet<int> entityIds = new HashSet<int>();

        /// <summary>实体集版本：AddEntity/RemoveEntity 实际变更时自增（不序列化）。
        /// 供群级推进源缓存判陈旧（见 SpaceGameComp.GetGroupPropulsionSource）。</summary>
        internal int entitySetVersion;

        // ── 群级推进源聚合器缓存（无 Map 多推进实体组，不序列化）──
        // 指纹 = entitySetVersion ⊕ 各推进实体 PropulsionVersion，两者之外无输入，
        // 故指纹匹配即可复用实例（聚合器 Networks 懒构建一次，此后只读）。
        internal GroupPropulsionAggregator? cachedPropulsionAggregator;
        internal int cachedPropulsionStamp;

        /// <summary>燃烧切片 scratch（第 3 条：每物理步复用，免每步 new List）。
        /// 物理并行区内由本组唯一迭代独占写入，主线程在同一步消费其引用快照后不再保留。</summary>
        internal readonly List<ThrustSlice> thrustSliceScratch = new List<ThrustSlice>();

        // ── 圆轨模式 ──
        public bool IsCircularOrbit = false;
        public double circularRadius = 0.0;          // 轨道半径 (m)
        public double circularAngularVelocity = 0.0; // 角速度 (rad/s)
        public double circularStartAngle = 0.0;      // 角度偏移 (进入时 = 0，兼容旧存档保留)
        public double circularStartTick = 0;           // 进入圆轨时的物理 Tick (physicsTick)
        public DVector3 circularOrbitNormal = DVector3.up;  // 轨道平面法线 (序列化)
        public DVector3 circularRadialRef = DVector3.right; // 进入圆轨时的径向参考方向 (角度0)
        /// <summary>圆轨的中心天体 ID。-1 表示沿用 CelestialBodyId（旧存档兼容）。</summary>
        public int circularCentralBodyId = -1;

        // ── 圆轨进入的两段式机动状态（第一段 burn 完成后由 SpaceGameComp 结算第二段）──
        /// <summary>是否有等待 burn 完成后结算的圆轨进入请求（BeginCircularize 置位）。</summary>
        public bool HasPendingCircularize = false;
        /// <summary>待结算圆轨的中心天体 ID。</summary>
        public int pendingCircularizeBodyId = -1;

        // ── ISpatialEntity（粗网格索引） ──
        int ISpatialEntity.EntityId => GroupId;
        int ISpatialEntity.GroupId => GroupId;
        int ISpatialEntity.FactionId => FactionId;
        public DVector3 Position
        {
            get => CurrentOrbit.position;
            set => CurrentOrbit.position = value;
        }
        public (int cx, int cy, int cz) LastCellCoord { get; set; }
        public bool IsSpatialDirty { get; set; }

        // ── 归属天体 ──
        /// <summary>归属天体的 bodyId，-1 表示未归属（深空自由飞行）</summary>
        public int CelestialBodyId = -1;
        /// <summary>
        /// 豁免行星碰撞检查（火箭爬升段出生在大气层内 R+10km &lt; 安全半径 R+100km、
        /// 初速 ~524 m/s &gt; 安全着陆阈值 10 m/s，会被 CheckPlanetCollision 首步误毁）。
        /// 仅爬升段置 true，入轨时清除。
        /// </summary>
        public bool exemptPlanetCollision = false;

        // ── 地表锚点（星座组网：为地表据点合成的不动 group，见《星座组网系统设计-20260828.md》§5 D11）──
        /// <summary>
        /// 地表据点的合成锚点 group：仅作星座成员载体（成员类型统一为 groupId），不是飞船。
        /// 约束 IsActive=false / IsMovable=false / IsCollidable=false——三者共同保证它既不参与物理积分与碰撞，
        /// 也不出现在任何「遍历 groups 的飞船消费方」（渲染 / 索敌 / 目标列表）视野内；
        /// <see cref="SpaceGameComp.GetGridForGroup"/> 另有 IsSurfaceAnchor 短路，使其根本不进空间索引。
        /// </summary>
        public bool IsSurfaceAnchor = false;
        /// <summary>锚点对应的原版世界物体 ID（Settlement.ID）；非锚点 = -1。据点被摧毁时据此惰性回收。</summary>
        public int anchorWorldObjectId = -1;

        /// <summary>中心天体相对位置（中心天体参考系），用于 R/T/N 基向量计算。</summary>
        public DVector3 localPosition;
        /// <summary>中心天体相对速度（中心天体参考系），用于 R/T/N 基向量计算。</summary>
        public DVector3 localVelocity;

        // ── 轨道保持 (OrbitKeeping) ──
        /// <summary>是否启用轨道保持模式（选择性抵消非白名单天体的引力，消耗 dV）。</summary>
        public bool HasOrbitKeeping = false;
        /// <summary>不抵消的天体 ID 白名单。null 或空 = 未启用过滤。</summary>
        public HashSet<int> orbitKeepingWhitelist;

        // ── 主动燃烧（缓释 dV） ──
        /// <summary>是否有正在执行的主动燃烧。</summary>
        public bool HasActiveBurn = false;
        /// <summary>燃烧方向单位向量。</summary>
        public DVector3 burnDirection;
        /// <summary>本次燃烧剩余 dV (m/s)。</summary>
        public double remainingBurnDV;
        /// <summary>本次 burn 已累计施加的 dV (m/s，诊断/日志用；StartBurn 清零)。</summary>
        public double appliedBurnDV;
        /// <summary>本次 burn 开始时的总质量（kg，首次 burn tick 记录，实际 ΔV 评估用）。</summary>
        public double burnStartMass;
        /// <summary>本次 burn 已实际消耗的工质（kg，主线程累计）。</summary>
        public double burnConsumedPropellant;
        /// <summary>
        /// 本次 burn 是否用 RCS（全矢量免对准）：交会/接近修正 burn 置 true，
        /// 主引擎大 burn 保持 false。瞬态标记，EndBurn 清零，不序列化。
        /// </summary>
        public bool burnUseRcs;

        /// <summary>
        /// 本次 burn 是否为引导修正 burn（交会/接近）：引导允许打断重烧的前提。
        /// 瞬态标记，EndBurn 清零，不序列化。
        /// </summary>
        public bool burnIsCorrection;

        /// <summary>
        /// 预对准目标（主推进轴应对准的世界方向；coasting 段由机动执行设置，
        /// 点火前提前转向 → burn 准时居中）。零 = 未启用。瞬态，不序列化。
        /// </summary>
        public DVector3 pendingAlignDir;

        /// <summary>
        /// 本物理步累积的合冲量（速度 ΔV，m/s）。
        /// 并行物理区 / 战斗层只累积（每 group 独占写自己的字段），主线程在下一物理步
        /// 开头统一施加并清零。多来源（burn / 碰撞动量交换 / 爆炸 / 手动 ΔV）顺序无关地
        /// 累加，避免一步内多次直接改 CurrentOrbit.velocity 的顺序混乱与并行写冲突。
        /// 瞬态字段，不序列化（物理步间即清空）。
        /// </summary>
        public DVector3 pendingImpulse;
        /// <summary>
        /// 机动燃烧策略：启用的工质网络（键 = 工质 Def）。null/空 = 全启用。
        /// 玩家可在机动队列 UI 调整；burn 只烧启用网络。
        /// </summary>
        public HashSet<PropellantDef> burnEnabledNetworks;

        /// <summary>
        /// 空间站「当前网络」（单选）：此后加入的机动都继承此单一网络（null = 全启用）。
        /// 在机动队列窗口顶部切换。
        /// </summary>
        public PropellantDef currentNetwork;

        // 机动规划队列（顺序执行）
        public List<PlannedManeuver> plannedManeuvers = new List<PlannedManeuver>();

        // 战斗模式缓存（用于插值）
        public PhysicsStateCache CombatCache;

        // ── Combat 插值渲染位置（仅渲染层读取；物理检测一律用 CurrentOrbit.position）──
        private DVector3 renderedPosition;
        private bool hasRenderedPosition;

        /// <summary>
        /// 渲染用位置：Combat 模式返回插值平滑位置；圆轨非战斗返回解析精确位置；其余返回物理位置。
        /// ⚠️ 物理/碰撞/射弹检测严禁使用此属性——物理状态统一在 CurrentOrbit（physicsTick 时间轴），
        /// 插值仅用于显示层平滑，二者分离保证"万物统一物理时间步长"。
        /// </summary>
        public DVector3 RenderPosition
        {
            get
            {
                var tsm = Current.Game?.GetComponent<TimeScaleManager>();
                if (tsm != null && tsm.IsCombatMode && hasRenderedPosition)
                    return renderedPosition;
                if (IsCircularOrbit)
                    return GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
                return CurrentOrbit.position;
            }
        }

        /// <summary>写入 Combat 插值渲染位置（仅由 UpdateCombatInterpolation 调用）。</summary>
        internal void SetRenderedPosition(DVector3 pos)
        {
            renderedPosition = pos;
            hasRenderedPosition = true;
        }

        // ── 圆轨解析结果懒缓存（per-physicsTick，不序列化）──
        // 圆轨位置/速度是 physicsTick 的纯解析函数（同一 tick 内无人修改轨道参数），
        // 战斗循环同一 tick 内会被查询 4~6 次，用 tick 值做键缓存，其余调用自动命中。
        private double cachedPosTick = double.NaN;
        private DVector3 cachedPos;
        private double cachedVelTick = double.NaN;
        private DVector3 cachedVel;

#if DEBUG
        // 缓存命中/未命中统计（调试验证：每 tick 每组应只 miss 1 次）
        internal long debugPosCacheHits;
        internal long debugPosCacheMisses;
        internal long debugVelCacheHits;
        internal long debugVelCacheMisses;
#endif

        private void InvalidateOrbitCache()
        {
            cachedPosTick = double.NaN;
            cachedVelTick = double.NaN;
        }

        public class PhysicsStateCache
        {
            public int baseTick;
            public DVector3 startPos;
            public DVector3 startVel;
            public DVector3 acc;
            public DVector3 jerk;
            public double dt;
            public double offset;

            public DVector3 InterpolatePosition(double t)
            {
                NBodyIntegrator.TaylorInterpolate(startPos, startVel, acc, jerk, t, out var p, out _);
                return p;
            }

            public DVector3 InterpolateVelocity(double t)
            {
                NBodyIntegrator.TaylorInterpolate(startPos, startVel, acc, jerk, t, out _, out var v);
                return v;
            }
        }

        /// <summary>
        /// 机动规划结构体。序列化 + 预览缓存。
        /// </summary>
        public struct PlannedManeuver : IExposable
        {
            public float radialDV;
            public float tangentialDV;
            public float normalDV;
            /// <summary>节点时刻（冲量模型的瞬间，即燃烧中点；physicsTick 基准，与 SpaceGameComp 点火检查一致；Fast 档 TicksGame 会失真）。
            /// 燃烧居中：实际点火时刻 = executeTick − 半程燃烧时长。</summary>
            public int executeTick;
            public bool active;

            /// <summary>
            /// 本次机动使用的工质网络（键 = 工质 Def）。null/空 = 全启用。
            /// 规划机动时选择，执行 burn 只烧这些网络。
            /// </summary>
            public HashSet<PropellantDef> useNetworks;

            /// <summary>规划时刻算好的世界系 dV 向量（节点帧）。执行时直接用作惯性方向，
            /// 避免在点火时刻用实际状态换算产生的帧旋转误差（MechJeb 同款）。零 = 未设置（退回分量换算）。</summary>
            public DVector3 plannedWorldDv;

            /// <summary>
            /// ★★ **本机动的参考轨道**（2026-09-21）——"这一发的 Δv 是在哪条轨道上算的"。
            /// 执行器的靶 = `参考轨道@节点UT 的速度 + plannedWorldDv`（见 `PlanNode.refOrbit` 的完整说明）。
            /// ⚠ `false` ⇒ 退回旧口径（点火前轨道@UT + Δv）⇒ 既有条目行为逐位不变。
            /// </summary>
            public bool hasResolvedRefOrbit;
            public OrbitView resolvedRefOrbit;

            /// <summary>本条目所属的**阶梯计划代号**（见 `SpaceEntityGroup.ladderPlanEpoch`）。
            /// 0 = 非阶梯产出（手动/引导/微调）⇒ 永远不被 `InvalidateStaleLadderNodes` 清掉。</summary>
            public int ladderEpoch;

            /// <summary>
            /// **本机动的意图（目标条件）**——《大修计划》§2.5 / B1.2。null = 纯冻结向量（玩家手动节点 / 旧计划）。
            /// 执行层接上活重解（P2 下一步）后，用它按活轨道重算 Δv 与方向；现在它至少是**可读可存**的。
            /// </summary>
            public PlanIntent? intent;

            /// <summary>
            /// 是否**已按活轨道重解过一次**（P2）。进预点火窗口时解一次就置位——解失败也置位，
            /// 免得每 tick 反复重试同一条解不出来的意图。重解结果会**写回本条机动的分量与 plannedWorldDv**
            /// ⇒ 下游（预对准方向 / 可行性估算 / 点火）自动用活值，不需要各自改。
            /// </summary>
            public bool intentResolved;

            /// <summary>
            /// **本条机动的归属**（§2.4 / B1.1）：`Manual` = 玩家排的（夺走引导）、`Guidance` = 引导/规划器排的。
            /// ⚠ 枚举零值是 `Manual` ⇒ `default(PlannedManeuver)` 视为手动（保守，与旧行为一致）。
            /// 规则本体在 `PlanOwnerRules`，**这里只存不判**。
            /// </summary>
            public PlanOwner owner;

            /// <summary>计划自报的燃烧时长（s，0 = 未声明）。执行层用它做"燃烧居中"，不再自己估（B2.2）。</summary>
            public double burnSecondsPlanned;

            /// <summary>本条机动点火时激活群组上已登记的"待激活交会引导"（精确交会：出发段执行后才开始引导）。
            /// 出发段被阻止/取消时引导自动作废。</summary>
            public bool armRendezvousGuidance;

            // 预览缓存 (不序列化，确认机动时由 Planner 填充)
            public List<Vector3> previewCurrentOrbit;
            public DVector3 previewNodePosition;
            public List<Vector3> previewPostOrbit;

            public void ExposeData()
            {
                Scribe_Values.Look(ref radialDV, "radialDV", 0f);
                Scribe_Values.Look(ref tangentialDV, "tangentialDV", 0f);
                Scribe_Values.Look(ref normalDV, "normalDV", 0f);
                Scribe_Values.Look(ref executeTick, "executeTick", 0);
                Scribe_Values.Look(ref active, "active", false);
                Scribe_Values.Look(ref armRendezvousGuidance, "armRendezvousGuidance", false);
                DVector3.Scribe("plannedWorldDv", ref plannedWorldDv);
                Scribe_Collections.Look(ref useNetworks, "useNetworks", LookMode.Def);

                // 意图（目标条件）：逐字段自己 Scribe（`PlanIntent` 是纯类型、不认识 Verse）
                string? intentName = intent?.operationName;
                List<double>? intentValues = intent == null ? null : new List<double>(intent.paramValues);
                List<string>? intentLabels = intent == null ? null : new List<string>(intent.paramLabels);
                int intentTimeRef = intent?.timeRefIndex ?? -1;
                double intentLead = intent?.leadTime ?? 0.0;
                double intentAlt = intent?.circularizeAltitude ?? 0.0;
                int intentTarget = intent?.targetGroupId ?? -1;
                double intentTick = intent?.absoluteTick ?? -1.0;
                Scribe_Values.Look(ref intentName, "intentOperation");
                Scribe_Collections.Look(ref intentValues, "intentValues", LookMode.Value);
                Scribe_Collections.Look(ref intentLabels, "intentLabels", LookMode.Value);
                Scribe_Values.Look(ref intentTimeRef, "intentTimeRef", -1);
                Scribe_Values.Look(ref intentLead, "intentLead", 0.0);
                Scribe_Values.Look(ref intentAlt, "intentAlt", 0.0);
                Scribe_Values.Look(ref intentTarget, "intentTarget", -1);
                Scribe_Values.Look(ref intentTick, "intentTick", -1.0);
                Scribe_Values.Look(ref intentResolved, "intentResolved", false);
                Scribe_Values.Look(ref owner, "owner", PlanOwner.Manual);
                Scribe_Values.Look(ref burnSecondsPlanned, "burnSecondsPlanned", 0.0);
                if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    intent = string.IsNullOrEmpty(intentName)
                        ? null
                        : new PlanIntent
                        {
                            operationName = intentName!,
                            paramValues = intentValues != null ? intentValues.ToArray() : new double[0],
                            paramLabels = intentLabels != null ? intentLabels.ToArray() : new string[0],
                            timeRefIndex = intentTimeRef,
                            leadTime = intentLead,
                            circularizeAltitude = intentAlt,
                            targetGroupId = intentTarget,
                            absoluteTick = intentTick,
                        };
                }
                if (Scribe.mode == LoadSaveMode.PostLoadInit && useNetworks != null && useNetworks.Count == 0)
                    useNetworks = null; // 空 = 全启用
                // 预览缓存不序列化
            }
        }

        public SpaceEntityGroup() { }
        public SpaceEntityGroup(int groupId, int factionId = 0)
        {
            GroupId = groupId;
            FactionId = factionId;
        }

        public void AddEntity(int entityId)
        {
            if (entityIds.Add(entityId)) entitySetVersion++;
        }

        public void RemoveEntity(int entityId)
        {
            if (entityIds.Remove(entityId)) entitySetVersion++;
        }
        public bool ContainsEntity(int entityId) => entityIds.Contains(entityId);
        public IReadOnlyCollection<int> GetEntityIds() => entityIds;

        /// <summary>取 group 任一实体所属地图的 SpaceMapComp（无 Map 实体 / 未关联时返回 null）。</summary>
        public SpaceMapComp GetLinkedMapComp()
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return null;
            foreach (int eid in GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e == null || e.MapId < 0)
                    continue;

                var mc = sg.GetMapComponent<SpaceMapComp>(e.MapId);
                if (mc != null)
                    return mc;

                // mapComponents 不随存档恢复；兜底直接从地图取 SpaceMapComp。
                Map map = SpaceMapGenerator.FindPocketMap(e.MapId);
                if (map != null)
                {
                    mc = map.GetComponent<SpaceMapComp>();
                    if (mc != null)
                        return mc;
                }
            }
            return null;
        }

        #region 机动队列管理

        /// <summary>
        /// 向队列末尾添加一个机动规划。新机动自动继承空间站「当前网络」配置
        /// （null/空 = 全启用）。
        /// </summary>
        public void QueueManeuver(PlannedManeuver maneuver)
        {
            maneuver.useNetworks = InheritNetworks();
            // §2.4 / B1.1：**显式归属**取代原来的"无条件清引导"。
            // 旧行为是"任何排队都杀引导"，于是引导自己派出的出发段也会把自己的引导清掉，
            // 只能靠"先排队后登记"的调用顺序 + `armRendezvousGuidance` 再带回来 ✗（脆弱且静默）。
            // 现在只有**手动**条目会夺走引导；引导自己的条目不清（规则见 `PlanOwnerRules`）。
            if (PlanOwnerRules.TakesOverGuidance(maneuver.owner))
                ClearRendezvousGuidance(); // 玩家手动接管：交会/接近引导让位（接近是其特化，一并清除）
            plannedManeuvers.Add(maneuver);
        }

        /// <summary>
        /// 添加一个机动规划（指定 dV 和相对时间）。同样继承「当前网络」。
        /// 注意：executeTick 使用 physicsTick 基准（与 ManeuverPlanner 一致）；
        /// 检查/执行在 SpaceGameComp 中用 physicsTick 判定，两基准错位会导致 Fast 档到期失真。
        /// </summary>
        public void QueueManeuver(float radDV, float tanDV, float normDV, int deltaTicks,
            PlanOwner owner = PlanOwner.Manual)
            => QueueManeuver(radDV, tanDV, normDV, deltaTicks, owner, DVector3.zero);

        /// <summary>
        /// 同上，但**带计划的惯性（world）向量**。
        /// ★★ 为什么必须有（2026-09-21 游戏内实测的真 bug）：点火门的判据是
        /// `MainAxisBurnAngleDeg(group, group.burnDirection)`，而 `burnDirection` 要到 `StartBurn`
        /// （**门的后面**）才被设 ⇒ 门比的是**上一次燃烧**留下的旧方向；同时 `pendingAlignDir` 的
        /// 预对准瞄的是**队列里下一条**机动 ⇒ 船被转到那个方向、停进 1.5° 死区，
        /// 而门要 2.0° ⇒ **船永远点不着火**（实测：`节点 T+19s` 之后计数器一路走负到 `T--6104s`，
        /// 船在"等对准"里卡死 1.7 小时、`夹角=0.0° |ω|=0` 与 `主轴还差 2.9°` 自相矛盾 ✗✗）。
        /// ⇒ 计划向量是"本条机动朝哪"的**唯一权威**，必须在排队时带上（与 MJ 的
        /// "节点自己携带 `node.patch`/`nextPatch`" 同构）。
        /// </summary>
        public void QueueManeuver(float radDV, float tanDV, float normDV, int deltaTicks,
            PlanOwner owner, DVector3 plannedWorldDv)
        {
            int execTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe()) + deltaTicks;
            // 归属规则（`PlanOwnerRules`，唯一权威）：**只有手动**条目才夺走引导。
            // 旧写法是无条件 `ClearRendezvousGuidance()`，B2.2-b 的微调（`Executor`）走这里时
            // 会把自己的交会引导清掉 ⇒ 改成按规则判。
            if (PlanOwnerRules.TakesOverGuidance(owner))
                ClearRendezvousGuidance(); // 玩家手动机动 = 接管，交会/接近引导让位
            plannedManeuvers.Add(new PlannedManeuver
            {
                radialDV = radDV,
                tangentialDV = tanDV,
                normalDV = normDV,
                executeTick = execTick,
                active = true,
                owner = owner,
                useNetworks = InheritNetworks(),
                plannedWorldDv = plannedWorldDv
            });
        }

        /// <summary>
        /// 同上，另带**参考轨道**（2026-09-21，两冲量转移的第二发用）。
        /// 见 `PlanNode.refOrbit` 与 `PlannedManeuver.resolvedRefOrbit` 的说明：
        /// 执行器的靶 = `参考轨道@节点 + Δv`；不带参考轨道时退回"点火前轨道@节点 + Δv"。
        /// </summary>
        public void QueueManeuver(float radDV, float tanDV, float normDV, int deltaTicks,
            PlanOwner owner, DVector3 plannedWorldDv, bool hasRefOrbit, OrbitView refOrbit)
        {
            int execTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe()) + deltaTicks;
            if (PlanOwnerRules.TakesOverGuidance(owner))
                ClearRendezvousGuidance();
            plannedManeuvers.Add(new PlannedManeuver
            {
                radialDV = radDV,
                tangentialDV = tanDV,
                normalDV = normDV,
                executeTick = execTick,
                active = true,
                owner = owner,
                useNetworks = InheritNetworks(),
                plannedWorldDv = plannedWorldDv,
                hasResolvedRefOrbit = hasRefOrbit,
                resolvedRefOrbit = refOrbit,
                ladderEpoch = ladderPlanEpoch
            });
        }

        /// <summary>阶梯新计划开始：**推进代号**（本代号 = 即将排入的那一对）。调用方随后用
        /// <see cref="InvalidateStaleLadderNodes" /> 清掉上一代的残留，或直接让下一次入队自动带新代号。</summary>
        public void BeginLadderPlanEpoch() => ladderPlanEpoch++;

        /// <summary>把「当前网络」继承到新机动；null = 全启用。</summary>
        private HashSet<PropellantDef> InheritNetworks()
        {
            if (currentNetwork == null)
                return null;
            return new HashSet<PropellantDef> { currentNetwork };
        }

        /// <summary>
        /// 清除所有机动规划。
        /// </summary>
        public void ClearManeuvers()
        {
            plannedManeuvers.Clear();
            ClearRendezvousGuidance();
            pendingAlignDir = DVector3.zero; // 取消预对准（手动接管）
        }

        /// <summary>
        /// 取消从指定索引（含）到末尾的所有机动规划（保留前面的）。
        /// </summary>
        public void CancelManeuversFrom(int index)
        {
            if (index < 0 || index >= plannedManeuvers.Count) return;
            plannedManeuvers.RemoveRange(index, plannedManeuvers.Count - index);
            ClearRendezvousGuidance(); // 取消出发段 = 交会/接近计划作废
        }

        /// <summary>
        /// 获取队列中第一个启用的机动（不判断是否到期；点火时机由执行循环按"燃烧居中"计算）。
        /// </summary>
        public PlannedManeuver? PeekNextManeuver()
        {
            if (plannedManeuvers.Count == 0) return null;
            var next = plannedManeuvers[0];
            if (!next.active) return null;
            return next;
        }

        /// <summary>
        /// 弹出（移除）第一个已执行的机动。
        /// </summary>
        public void PopExecutedManeuver()
        {
            if (plannedManeuvers.Count > 0)
                plannedManeuvers.RemoveAt(0);
        }

        /// <summary>
        /// ★★ **阶梯计划的代号**（2026-09-21）。阶梯一次规划产出**一对**冲量（起飞 + 到达刹车），
        /// 两发都盖同一个代号。<see cref="InvalidateStaleLadderNodes" /> 用它把**上一条计划**
        /// 尚未执行的节点清掉。
        ///
        /// ## 为什么必须清（这就是"一抽一抽"的来路）
        /// 起飞执行后出队，**刹车还挂在队列里**（它排在 100 秒外）。而阶梯每 30 秒重规划一次，
        /// 新计划从**活状态**另算一对 ⇒ **旧计划那条刹车仍然会执行**，且它是按
        /// "船应该在**旧计划**的到达点" 施加的 ⇒ 一记**按过期计划**打的错向冲量
        /// （实测：一步从 1.5 km 甩到 11.2 km）。
        /// ⇔ 与 MJ 的"有节点就只执行、不立新节点"是同一条纪律的另一半：
        /// **我们的承诺是两发，那么撤回也必须是两发**。
        /// </summary>
        public int ladderPlanEpoch;

        /// <summary>
        /// 阶梯开始新一轮规划时调用：把**上一代号**残留的节点全部移除（本代号 = 即将排入的那一对）。
        /// ⚠ **手动条目不动**（`owner` 判定），玩家的手动机动不该被自动规划擦掉。
        /// ⚠ 正在执行的那一条**已经出队**（`PopExecutedManeuver` 在 `StartBurn` 时调用）⇒ 这里清的是"未来的"。
        /// </summary>
        public int InvalidateStaleLadderNodes()
        {
            int removed = 0;
            for (int i = plannedManeuvers.Count - 1; i >= 0; i--)
            {
                var m = plannedManeuvers[i];
                if (m.owner == PlanOwner.Manual) continue;      // 玩家的，不动
                if (m.ladderEpoch >= ladderPlanEpoch) continue; // 本代或更新 ⇒ 保留
                plannedManeuvers.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// 队列里**有没有手动条目**（§2.4：引导只在有手动条目时让位；只有引导自己的条目则不让位）。
        /// 扫描本身写在这里（无分配），**判据**统一走 `PlanOwnerRules.GuidanceMustYieldTo`。
        /// </summary>
        public bool HasManualManeuver()
        {
            for (int i = 0; i < plannedManeuvers.Count; i++)
                if (PlanOwnerRules.GuidanceMustYieldTo(plannedManeuvers[i].owner)) return true;
            return false;
        }

        #endregion

        #region 主动燃烧 (Active Burn)

        // ── 姿态控制（魔法动量轮）──
        /// <summary>角速度（rad/s，魔法动量轮直设；每物理步积分到 CurrentOrbit.rotation）。</summary>
        public DVector3 angularVelocity;
        /// <summary>上次诊断的本地状态 + tick + 剩余 Δv（用于把"真正施加的冲量"从状态里扣出来）。</summary>
        /// <summary>⚠ 存的是**适配层系**的状态（与 `Shepperd` 同系 ⇒ 消除换系机会）。</summary>
        public MechJebLib.Primitives.V3 burnDiagR, burnDiagV;
        public double burnDiagTick = -1.0;
        public double burnDiagRemaining = -1.0;

        /// <summary>燃烧弧预测的输入（点火时暂存，供"预测 vs 实测"并排读数用；运行期，不序列化）。</summary>
        public MechJebLib.Primitives.V3 burnArcDv;
        public double burnArcNodeUt = -1.0;
        public double burnArcSeconds;

        /// <summary>诊断：活差额律因**差额反向**（超调）而收工的次数。超调在正常情况下不该发生
        /// （差额是每拍重测的）⇒ 非零就是在提示"某处的 Δv 记账与活状态不一致"，必须查。</summary>
        public int burnReversedStopCount;

        /// <summary>诊断：因**节点窗口已过**而停火的次数（见 `SpaceGameComp.BurnNodeWindowPostSeconds`）。
        /// **正常燃烧恒为 0**：燃烧以节点为中心、收手远早于窗口截止 ⇒ 非零就是在提示
        /// "某一发追着过期靶在烧"（2026-09-21 那个 4412 m/s / 9080s 的形态）——这是一条**故障计数**。</summary>
        public int burnNodeWindowAborts;

        /// <summary>诊断：因**差额小到没意义**（&lt; `BurnDeficitIgnoreFloor`）而停手的次数
        /// （见 `SpaceGameComp.BurnDeficitIgnoreFloor`）。收尾段为正常值；**非零很多**说明收尾在反复磨。</summary>
        public int burnDeficitIgnoredCount;

        /// <summary>
        /// ★★ **节点的目标速度**（**游戏系**惯性向量；点火时由计划算出，整段燃烧**不变**）——
        /// **这是 MJ 的靶**（`MechJebModuleNodeExecutor.NextDirection` 里 `node.GetBurnVector(Orbit)`：
        /// 靶 = 结果轨道的**速度**，活的是 `Orbit`）⇒ 每拍 `dv_rem = v_target(UT) − v_live_prop(UT)`。
        /// ⚠ 它**没有几何分支**（靶是固定向量，任何活状态都能算差额）⇒ 不存在"靶跟着本点跑"那一类失效。
        /// （我们一度换用过"意图轨道 (a,h向量) + 出范围政策"当靶：台架 e 好两个数量级，但**真实几何上
        ///   出范围支是自证式的**，游戏实测 430.01 m/s 只交付 164.9 ✗ —— 用户裁定"暂时全盘照抄 MJ"
        ///   ⇒ 换回本靶；精度由收口微调负责，见 `trimTarget*` 那组字段。）
        /// = `StateAtUT(点火前轨道, 节点UT).v + 计划 Δv`。
        /// ⚠ 未设（微调条目、无计划向量的燃烧）⇒ 退回 RTN 分量律（见 `burnRadial` 字段）。
        /// </summary>
        public DVector3 burnTargetV;
        public bool burnHasTargetV;

        /// <summary>B2.2 诊断：实际燃烧的起止 tick（-1 = 未记录）。用于打印"实际燃烧时长"。</summary>
        public double burnActualStartTick = -1.0;
        public double burnActualEndTick = -1.0;

        /// <summary>
        /// B2.3：点火时刻的 RTN 分量（"提交的解"，方向刷新时只换框架、不换解）。
        /// ⚠ **本轮起降级为退路**：有计划向量的燃烧走 `burnTargetV` 的活差额律（更精确，见那个字段）；
        /// 只有拿不到目标速度的燃烧（微调条目、引导逐帧指令）才用这条 RTN 律。
        /// ⚠ **分解必须用正本**（`CurrentOrbit` 的位置/速度），所以它**不在 `StartBurn` 里算** ——
        /// 那里只有 `localPosition/localVelocity`（**副本**，与正本差一个帧速 ≈14–16 m/s ⇒ 方向偏 ~0.13°）。
        /// 改由刷新回路在**首次刷新时**用正本分解一次（`burnComponentsInit`），见 `SpaceGameComp`。
        /// </summary>
        public double burnRadial, burnTangential, burnNormal;
        public bool burnComponentsInit;

        /// <summary>
        /// 用**计划自带的解**（节点时刻的 RTN 分量）设定本次燃烧的方向锚点。
        /// ⚠ 为什么必须走这条路（2026-09-19 实测）：`StartBurn` 只拿到**世界向量**，若在**点火帧**重新分解，
        /// 锚点就从节点漂到点火时刻（相差 ω·49s ≈ **2.44°**）——实测「模型方向 vs 执行方向」恒为 2.23–2.36° ✓
        /// 与之吻合，而 e 因此停在 7.5e-3（台架同口径 7.465e-3）。**计划里本来就有正确的分量**
        /// （`OrbitAdapter.TryQueuePlan` 用**节点时刻**位姿分解 ⇒ `PlannedManeuver.radialDV/…`），直接用即可。
        /// 分量全 0（老档/引导逐帧指令没有节点解）⇒ 保持未初始化，由刷新回路退回"点火帧分解"（对那类燃烧是对的）。
        /// </summary>
        public void SetBurnSolution(double rad, double tan, double nor)
        {
            if (rad == 0.0 && tan == 0.0 && nor == 0.0) return;
            burnRadial = rad;
            burnTangential = tan;
            burnNormal = nor;
            burnComponentsInit = true;
        }

        /// <summary>
        /// B2.2：**点火前**的轨道 —— 前向模型（`BurnArc`）的基准。
        /// ⚠ 必须是**点火前**的（模型自己会推进到点火时刻）；喂"当前（已烧过的）轨道"会让预测
        /// 随**已烧累计 Δv** 漂移（2026-09-19 台架实测：t=100s 时差 694 m/s ≈ 已烧 Δv 本身）。
        /// </summary>
        public OrbitView burnPreBurnOrbit;
        public bool burnHasPreBurnOrbit;

        /// <summary>B2.2 诊断：执行器**实际所用**网络的 ΣF·dt 与 Σṁ·dt（⇒ 真实 ve = F/ṁ 的唯一来源）。
        /// 合并口径（全部网络）会把未点火的 RCS 网算进来 —— 那正是"白送 Δv"假警报的来源。</summary>
        public double burnThrustDt, burnFlowDt;

        /// <summary>
        /// **本次燃烧的命令加速度**（m/s²，0 = 未设 ⇒ 退回全油门）。点火那一步锁存 = `F参与/m₀`
        /// （用户 2026-09-19 裁定："机动 = Δv + 加速度，油门随时适配质量"）。
        /// 整段按它走 ⇒ Δv 均匀摊在居中窗口里 ⇒ 冲量质心落在节点上（全油门会因料耗让 a 上升 ⇒ 质心偏晚 ~3.5 s）。
        /// ⚠ **0 有两种含义**，由 <see cref="burnThrottleCap" /> 区分（2026-09-21）：
        ///   · `burnThrottleCap ≤ 0` ⇒ **真的没设**（RTN 退路律）⇒ 全油门 + 逐拍递减记账；
        ///   · `burnThrottleCap > 0` ⇒ **有节点靶**：不用常量加速度，油门由**剩余 Δv** 推（见那个字段）。
        /// </summary>
        public double burnCommandAccel;

        /// <summary>
        /// ★★ **本轮燃烧"还差多少 m/s"**（`&gt; 0` ⇒ 走**需求推油门**，MJ 的 `ThrustForDv` 口径；2026-09-21）。
        ///
        /// ## 为什么要把这个数单列出来（读 MJ 本体之后改的）
        /// MJ：`ThrustForDv(_dvLeft, τ)` ⇒ `desiredAccel = _dvLeft/τ` ⇒ `throttle = desiredAccel/MaxAccel`
        /// ⇒ 展开就是 **`throttle × MaxAccel × τ = _dvLeft`** —— **油门与"还差多少"是同一件事**，
        /// 而且 `_dvLeft` 每帧从活轨道重算 ⇒ **没有任何一帧可以被锁死**。
        /// 我们原来：点火锁 `burnCommandAccel = Δv/t_b` 常量化 ⇒ `BurnStep.Compute` 按它烧满整段，
        /// **发动机实际给不出那个加速度时，整段燃烧都按错的加速度走，没有任何一帧会去纠正它** ✗。
        ///
        /// ⇒ 现在（仅有节点靶的燃烧）**不用常量加速度**，每步把"还差多少"喂进来：
        ///   `dv本步 = min(a_可用·dt, burnThrottleCap)` ⇒ 自然就是**需求推油门**，
        ///   且需求（差额）与可得（`a_可用·dt`）**用同一把尺子量**，两边不可能分家。
        ///
        /// 认一个账：供/需仍可差一个数量级（船就是给不出），但那时燃烧会**因为差额真的减小而收手**，
        /// 而不是像记账式那样"我欠 431.7、永远还不完"。
        /// </summary>
        public double burnThrottleCap;

        /// <summary>本次机动是否已做过**点火相位扫描**（B2.2 弧补偿）：等待点火的每个物理步都扫一遍
        /// 会白烧 CPU + 刷屏 ⇒ 每次机动只扫一次（`StartBurn` 复位）。</summary>
        public bool burnPhaseSwept;

        /// <summary>头步定点读数计时（只打头 6 步；`StartBurn` 复位）。</summary>
        public int burnHeadStepsLogged;

        // ── 意图轨道靶（B2.2-b 的收口微调 + "MJ 优先"第 5 件起的**整段燃烧**共用）─────────────
        // 一个靶，两种时机（收口时调一次 = 微调；逐拍调 = 燃烧律）⇒ **只允许一份算式算它**（J2）。
        /// <summary>本次机动的**意图轨道**：计划在节点处的**脉冲**结果 `(a, |h|, ĥ)`。
        /// ⚠ 必须是"脉冲结果"而不是"模型预测结果" —— 意图 = 计划想做的事，不是模型猜我会做成什么。
        /// ⚠ `trimTargetHVec`（**游戏系**角动量向量）不是可选项：**纯面外变轨不改 a 也不改 |h|**
        ///   ⇒ 只有标量靶时整段平面改变会被判"无差额"而**静默跳过**（台架实测：一步不烧 ✗）。</summary>
        public double trimTargetA, trimTargetH;
        public DVector3 trimTargetHVec;
        public bool trimHasTarget;
        /// <summary>本机动是否允许收口微调。★ **默认 false**（2026-09-21 用户裁定：“老老实实移植融合 MJ”）。
        /// **微调是我们自己的东西，上游没有** ⇒ 它属于“最后总修缮”阶段的补充，
        /// 在移植验收完成前**不该在流程里**（否则又是“两套机制互相插队” ✗）。
        /// 对照实验/将来接回时显式置 true ✓。</summary>
        public bool trimEnabled = false;
        /// <summary>本次机动是否已经微调过（**每次机动最多一次**：二次微调要靠它收敛 ⇒ 先不做，避免自激）。</summary>
        public bool trimFired;
        /// <summary>上次微调的 Δv 大小（诊断；`StartBurn` 复位）。</summary>
        public double trimLastDv;
        /// <summary>**主燃**的累计 Δv（微调排队那一刻抓走 —— 微调自己 `StartBurn` 会把 `appliedBurnDV` 清零，
        /// 不抓的话验收读数只剩微调那 1.3 m/s）。0 = 本机动没走微调。</summary>
        public double trimMainBurnDv;

        // ── 对照验收开关（2026-09-19）：**per-group**（几条船会同时燃烧，全局静态会互相污染 ✗）
        //    默认 false ⇒ 生产行为逐位不变。见 `BurnABAcceptanceRunner`。
        /// <summary>冻结燃烧方向（关掉"按当前 RTN 重取"）。</summary>
        public bool abFreezeDirection;
        /// <summary>用全油门（关掉"命令恒定加速度"政策）。</summary>
        public bool abFullThrottle;
        /// <summary>不做预对准（测点火瞬间姿态是否参与）。</summary>
        public bool abNoPreAlign;
        /// <summary>**强制点火相位**（对照实验用；NaN = 走自动扫描）。单位 s，正 = 推迟点火。</summary>
        public double abForcePhaseShift = double.NaN;
        /// <summary>**单脉冲执行**（对照 F）：到节点把计划 Δv 一次施加（不走有限燃烧）——
        /// 判据的"期望"算的正是这一件事 ⇒ 它直接把"计划→空间的映射"与"有限燃烧的执行"分开。</summary>
        public bool abOneShotImpulse;
        public double abNodeTick = -1.0;

        /// <summary>逐步对账（2026-09-19）：`burnLastCmdDv` = 本步命令的 Δv，`appliedDvAudit` = 累计实收。</summary>
        public double burnLastCmdDv;
        public double appliedDvAudit;
        public int burnStepAuditCount;

        /// <summary>
        /// 相位扫描选出的**点火偏移**（s）。⚠ 必须存在群上：它是**跨物理步**的结果
        /// （扫描在等待期某一步做一次、点火在之后某一步才发生）。
        /// **实测教训（2026-09-19）**：第一版把它写成 `ignitionTick` 计算处的**局部变量** ✗ ⇒
        /// 每一步都被重置为 0 ⇒ 扫描白扫（点火仍在 −53、e 与未扫描的第⑤轮**逐位相同** 1.108e-3 ✓ 反证）。
        /// 与"a_cmd 锁存"同一个坑：**跨步状态不能放局部**。
        /// </summary>
        public double burnPhaseShift;

        /// <summary>动量轮最大角速度（rad/s；无转动惯量，限速即手感，暂定 ~5°/s）。</summary>
        public const float DefaultMaxAngularRate = 5f * Mathf.Deg2Rad;
        /// <summary>
        /// 实际限速（rad/s）。&lt;=0 = 用默认（旧存档/普通船兼容）。
        /// 导弹等敏捷自主体出生时按 MissileDef.maxAngularRateDegrees 覆盖（D15 主推对准收敛速度）。
        /// </summary>
        public float maxAngularRate = -1f;
        /// <summary>实际限速（rad/s；per-group 覆盖，未设用默认常量）。</summary>
        public float MaxAngularRate => maxAngularRate > 0f ? maxAngularRate : DefaultMaxAngularRate;

        /// <summary>
        /// 船体局部轴向 → 世界方向（当前姿态 rotation 旋转）。
        /// 方向感知推进 / 转向机动的唯一换算入口。
        /// </summary>
        public DVector3 GetWorldAxis(ThrustAxis axis)
        {
            return DVector3.Rotate(axis.LocalVector(), CurrentOrbit.rotation);
        }

        /// <summary>
        /// 开始一次缓释燃烧。
        /// </summary>
        /// <param name="dV">本次燃烧的总 dV 向量 (m/s)</param>
        public void StartBurn(DVector3 dV)
        {
            HasActiveBurn = true;
            // ⚠ **信号失效点之一**：点火会让 SignalStrength 跃升（[V8] 的 burning 项）——
            // 这正是"冷发射信号 profile"的来源（巡航低信号 / 末段高信号）。
            // `HasActiveBurn` 是 public 字段，钩子只能挂在本方法与 EndBurn 两处；
            // **任何别处直接赋 `HasActiveBurn = true` 都会让派生信号静默过期**（与 AVM:824 的"派生点"同类陷阱）。
            derivedDirty = true;
            burnDirection = dV.normalized;
            remainingBurnDV = dV.magnitude;
            appliedBurnDV = 0.0;
            burnStartMass = 0.0;      // 首次 burn tick 时记录（此时质量已知）
            burnActualStartTick = -1.0; // 诊断：实际点火 tick（B2.2 的"预测 vs 实测"要用）
            burnActualEndTick = -1.0;
            burnConsumedPropellant = 0.0;
            burnThrustDt = 0.0;
            burnFlowDt = 0.0;
            burnCommandAccel = 0.0;        // 由点火块按 `Δv/t_b` 设定（见执行器注释）
            burnThrottleCap = 0.0;         // 0 = 未启用"需求推油门"（RTN 退路律走记账式）；>0 见该字段说明
            burnPhaseSwept = false;        // 新机动 ⇒ 允许再扫一次点火相位
            burnPhaseShift = 0.0;          // …且偏移归零（扫描结果在 StartBurn 之前已用完）
            burnHeadStepsLogged = 0;       // 头步读数重新开始
            burnLastCmdDv = 0.0;
            appliedDvAudit = 0.0;
            burnStepAuditCount = 0;
            burnHasPreBurnOrbit = false;   // 点火 tick 由执行侧填（那时才有"点火前的轨道"）
            burnHasTargetV = false;        // 目标速度同上（**必须复位**：不复位会继承上一段的残留靶 ⇒ 静默烧错方向）
            // ⚠ **等对准计时也必须复位**（2026-09-21 实测抓到的真 bug）：我上一版加了"没对准就不点火"
            //    的门却没复位这个时间戳 ⇒ 第二次以后的机动一算就"等超时" ⇒ **门形同虚设** ✗✗
            //    （日志证据：`带角度点火 4 次`，角度 93.4° / 74.0°，其中 93° 根本进不了 10° 主推锥）。
            burnIgnitionAlignWaitTick = -1.0;
            burnComponentsInit = false;    // RTN 分量由刷新回路用**正本**分解一次（见字段注释）
            trimHasTarget = false;         // 意图轨道由点火块按计划（脉冲结果）算出
            trimFired = false;             // 每次机动最多微调一次

            // ── B2.3 燃烧弧方向律（§2.2-2：`burnDirection` 原来整段燃烧期冻结）──
            // 解 = 点火时刻的 **RTN 分量**（在 `SpaceGameComp` 的刷新回路由**正本**分解一次），
            // 燃烧中**每步**用**当前 RTN** 重新合成世界方向 ⇒ 解不动、方向按时间取。
            //
            // ⚠ 2026-09-19：原来按"扫过 2°"的节拍刷新（`BurnArcPolicy`）。台架（`BurnArcExecutorBench`）
            //   实测该量化**吃掉 4.8 倍残差**（e 3.76e-3 vs 每步连续 7.79e-4；不变量 0.9897 vs 0.9975，
            //   而游戏实测 0.9897 —— 逐位对上 ⇒ 量化就是那个差）。
            //   政策的理由（"每 tick **重解**太贵"）不适用：这里只是把**同一个解**在当前 RTN 里重新表达
            //   （~15 次浮点 + 一次归一化），没有求根也没有搜索 ⇒ 节拍机制整体删除。
            //
            // ⚠ **也不能在这里分解**：本方法的入参只有 `localPosition/localVelocity`（**副本**，
            //   与正本差一个帧速 ≈14–16 m/s ⇒ 方向偏 ~0.13°）。这正是 604 行那段注释说的同一个 trap 的
            //   另一半 —— 2026-09-19 修刷新时漏了这处分解。
        }

        /// <summary>
        /// 立即结束燃烧（剩余 dV 丢弃）。
        /// </summary>
        public void EndBurn()
        {
            HasActiveBurn = false;
            derivedDirty = true;   // 失效点之一（熄火 ⇒ burning 项归零），与 StartBurn 成对
            remainingBurnDV = 0.0;
            burnUseRcs = false;
            burnIsCorrection = false;
        }

        #endregion

        #region 轨道保持 (OrbitKeeping)

        /// <summary>
        /// 进入轨道保持模式。
        /// </summary>
        /// <param name="whitelistBodyIds">不抵消的天体 ID 白名单。白名单内的天体的引力正常参与轨迹积分，
        /// 白名单外的引力被抵消并消耗 dV。</param>
        public void EnterOrbitKeeping(HashSet<int> whitelistBodyIds)
        {
            HasOrbitKeeping = true;
            orbitKeepingWhitelist = whitelistBodyIds ?? new HashSet<int>();
        }

        /// <summary>
        /// 退出轨道保持模式。清空白名单。
        /// </summary>
        public void ExitOrbitKeeping()
        {
            HasOrbitKeeping = false;
            if (orbitKeepingWhitelist != null)
            {
                orbitKeepingWhitelist.Clear();
                orbitKeepingWhitelist = null;
            }
        }

        #endregion

        #region 交会引导 (Rendezvous Guidance)

        /// <summary>交会引导状态已登记（精确交会模式）。<see cref="rendezvousArmed"/> 为 false 时表示等待出发段执行。</summary>
        public bool HasRendezvousGuidance = false;
        /// <summary>出发段已点火执行，引导生效（闭环修正开始）。false = 等待出发段。</summary>
        public bool rendezvousArmed = false;
        /// <summary>引导为"近距离接近"模式（动态到达时刻、无出发段）。true 时绕开出发段与固定到达时刻逻辑。</summary>
        public bool rendezvousIsApproach = false;
        /// <summary>目标群 ID。</summary>
        public int rendezvousTargetId = -1;
        /// <summary>
        /// 目标局部系瞄准偏移（m，序列化）：瞄准 targetGroupPos + 目标实体偏移 + 姿态旋转(offset)。
        /// 零 = 瞄准 group 中心（原行为）。跳帮落点用：落点格子 → 目标空间站局部系偏移。
        /// </summary>
        public DVector3 rendezvousTargetOffset;
        /// <summary>到达时刻（physicsTick 基准）。</summary>
        public double rendezvousArrivalTick;
        /// <summary>转移方向（ShortWay/LongWay）。</summary>
        public bool rendezvousPrograde = true;
        /// <summary>引导修正烧使用的工质网络（继承出发机动；null = 全启用）。</summary>
        public HashSet<PropellantDef> rendezvousUseNetworks;
        /// <summary>上次引导检查的 physicsTick（节流用，不序列化；载入后首次检查立即进行即可）。</summary>
        public double rendezvousLastCheckTick = double.NegativeInfinity;

        // ── 交会精度（**2026-09-22 用户裁定：`100 m` 太近，阈值可以换 `1 km` / `10 km`**）──
        /// <summary>交会**完成**的距离判据（m）= **1000（用户裁定放宽）**。
        /// ⚠ 演进史（留档，别再来回搬）：300 m（测试）→ 2000 m（2026-09-20"最后几米太贵"，实测一轮 1284 发微修正）
        /// → 100 m（2026-09-21 跟上游 MJ）→ **1000 m（2026-09-22 用户裁定：100 m 太近）**。
        /// ⚠⚠ **`100 m` 的实际代价（低推力档实测，这是本次放宽的直接依据）**：船每次都贴到 **246 m**、
        /// 残余相对速度 2.5 m/s，而 100 m 的门把它挡在外面 ⇒ 一路磨到燃料被判不可行
        /// （`BLOCKED maxDV=3361.9`）。**246 m 在任何实用意义上就是"到了"**——
        /// 而这个数是**引擎推力**决定的（低推力船做不到更细），不是规划错。
        /// ⇒ **判据不该严到"只有高推力船才够得着"**。要更严/更松请用 <see cref="rendezvousGoal" /> 的各预设：
        ///   要贴近用 `Precise(100, 1)`、要更松用 `Loose(10000)`、战斗用 `Combat()`。
        /// </summary>
        public double rendezvousArriveDist = 1000.0;
        /// <summary>★ **"停下/收口"的距离尺度**（m）——**与完成容差分开**是纪律（哪怕它们取同一个数）。
        /// 上游 MJ 只有一个 `desiredDistance = 100` 同时当两者用，之所以不矛盾是**因为它足够小**：
        /// 接近级按 `dist/100`（≤ `maxClosingSpeed` 100 m/s）闭合 ⇒ 一次闭合就能把 20 km 收到 ~100 m
        /// ⇒ **停下门比接近级的结果还严 ⇒ 永不打架** ✓。我们一度把完成容差放宽到 2 km 并照抄同一个数
        /// ⇒ 门比接近尺度松 20 倍 ⇒ **2 km 就"停"而接近级还能干 100 m/s ⇒ 停-接近打架 = 过冲** ✗✗
        /// （实测：`Intercept`×58 ⇄ `MatchAtClosestApproach`×27、总 Δv 8438 vs 估计 642）。
        /// ⇒ 收口/清队列/在最近点停下这一族判据一律用它（= 上游的 100 ✓）。
        /// ⚠ **两个字段保留**（不合并）：数值允许不同（例如日后想把完成容差放宽），但"停下"永远必须小 ✓。</summary>
        public double rendezvousMatchDist = 100.0;
        /// <summary>★ **本机动是否允许收口微调**（由阶梯按**选中的级**置位）。
        /// 微调的语义是“把 `(a,|h|)` 残差收干净”（它自己的类注释）⇒ **只有“立轨道”那几级才该用**：
        /// `MatchPlanes` / `Hohmann` / `PhasingOrbit` / `Circularize`；
        /// 而 `Intercept` / `MatchVelocitiesNow` / `MatchAtClosestApproach` 的使命是“**到达**”，
        /// 它们的“意图轨道”是“过到达点的那条轨道”、**不是目标船的轨道**
        /// ⇒ 微调会每轮打一发径向烧把船往那条轨道上拽、**跟拦截的闭合对抗** ✗✗
        /// （实测：42 轮×(拦截+微调) ≈ 2500–3000 m/s；一轮只收 332 m 而计划该收 ~9.5 km）。</summary>
        public bool trimAllowedByRung = true;
        /// <summary>点火前“等对准”的起算 tick（-1 = 本机动还没开始等）。见 `IgnitionAlignGateDeg`：到了点但主推轴还没对准 ⇒ **等**（上游纪律）。</summary>
        public double burnIgnitionAlignWaitTick = -1.0;
        /// <summary>交会**完成**的相对速度判据（m/s）= **10.0（2026-09-22 用户裁定时随距离一起放宽）**。
        /// 演进：2.0 → 5.0（配 2 km 站位）→ 1.0（2026-09-21 跟上游 MJ）→ **10.0**。
        /// ⚠ **必须与 <see cref="rendezvousArriveDist" /> 一起调**（只放距离不放速度 ⇒ 距离够了但船还在
        /// 10 m/s 上飘 = **假完成**）；本库 2026-09-22 的取值对是 **1000 m / 10 m/s**（约"半个接近段的量级"）。
        /// ⚠ 留档的实测教训（当时是对的，现在仍成立）：**若速度判据比距离判据更宽，距离再窄也停不下来**
        /// ——那时卡住的正是相对速度。⇒ 放宽可以，但两者要**成对**放，不能只放一个。
        /// 要更严：`RendezvousGoal.Precise(100, 1)`（上游口径）。</summary>
        public double rendezvousArriveRelVel = 10.0;

        /// <summary>★★ **交会的目的**（2026-09-21）。默认 = 由 <see cref="rendezvousArriveDist" /> /
        /// <see cref="rendezvousArriveRelVel" /> 组装出的**精确贴靠** ⇒ **既有行为逐位不变**。
        /// 要"为战斗而交会"等其它目的时设它（见 <see cref="RendezvousGoal" />）。
        /// ⚠ 设了它就**以它为准**：上面那两个字段只作为 <see cref="Kind.Precise"/> 的入参来源。</summary>
        public RendezvousGoal rendezvousGoal;

        /// <summary>**当前生效的到达判据**（唯一权威）。未显式设过 <see cref="rendezvousGoal" /> 时
        /// 用两个旧字段组装精确贴靠 ⇒ 兼容既有全部调用点。</summary>
        public RendezvousGoal EffectiveRendezvousGoal
            => rendezvousGoal.distanceTol > 0.0
                ? rendezvousGoal
                : RendezvousGoal.Precise(rendezvousArriveDist, rendezvousArriveRelVel);

        /// <summary>
        /// ★★ **到达判据的统一求值口**（2026-09-22）——把"目的需要哪些量"这件事**收在一处**：
        /// 距离/速度目的只用 `localPosition`/`localVelocity`；**相位目的还要目标的位置**（算有符号相位差），
        /// 且它的"到位门"读的是**半长轴差**（见 `RendezvousGoal.IsMet` 三参版）。
        ///
        /// ⚠ 为什么不让调用方自己拼：`SpaceGameComp` 那边已经在用它做完成判据与捷径，两处**必须同一个口径**
        /// （本会话"改一处忘一处"吃过太多次）。**这里返回的 `met` 就是"到达"的唯一答案。**
        ///
        /// ⚠ 半长轴差用 `SemiMajorAxis(r, v, gm)` 现成式，**不另写**；`gm` = 中心天体 GM
        /// （拿不到时只对相位目的失效：保守返回 false，不让它冒充达成）。
        /// </summary>
        /// <param name="target">目标编队（须同中心天体；不同则相位目的保守判 false）</param>
        /// <param name="gm">中心天体 GM（m³/s²）</param>
        /// <param name="phaseErrDeg">输出的**有符号相位差**（度；非相位目的为 NaN）</param>
        public bool IsRendezvousGoalMet(SpaceEntityGroup? target, double gm, out double phaseErrDeg)
        {
            phaseErrDeg = double.NaN;
            var goal = EffectiveRendezvousGoal;

            DVector3 rSelf = localPosition, vSelf = localVelocity;
            double dist = double.NaN, relVel = double.NaN;
            if (target != null)
            {
                DVector3 rTgt = target.localPosition, vTgt = target.localVelocity;
                dist = (rTgt - rSelf).magnitude;
                relVel = (vTgt - vSelf).magnitude;
                if (goal.kind == RendezvousGoal.Kind.PhaseSynced)
                    phaseErrDeg = RendezvousGoal.SignedPhaseDeg(rSelf, vSelf, rTgt);
            }

            switch (goal.kind)
            {
                case RendezvousGoal.Kind.PhaseSynced:
                    if (target == null || double.IsNaN(phaseErrDeg) || !(gm > 0.0)
                        || target.CelestialBodyId != CelestialBodyId)
                        return false;   // 缺量 ⇒ 保守不判达成（不冒充）
                    double aSelf = OrbitMath.SemiMajorAxis(rSelf, vSelf, gm);
                    double aTgt = OrbitMath.SemiMajorAxis(target.localPosition, target.localVelocity, gm);
                    return goal.IsMet(System.Math.Abs(aSelf - aTgt), relVel, phaseErrDeg);
                default:
                    return goal.IsMet(dist, relVel);
            }
        }
        /// <summary>**终端 CW 的交班窗口**（占轨道半径的比例）。**默认保持原值 0.05** —— 它是**收敛性**参数
        /// （远场律在近距离能否收住是几十轮调出来的），2026-09-20 只把它**提成可调**，**不改默认行为** ✓。
        /// 若去掉"磨"之后仍卡，再考虑动它（那时要用"交会耗时 + 收敛"两个判据一起看）。</summary>
        public double rendezvousTerminalFraction = 0.05;

        // ── 区制分派（B2.1，2026-09-20）：准脉冲走阶梯，连续走连续律 ──────────────
        /// <summary>允许本船用**交会阶梯**（准脉冲区）。默认开；对照实验可关掉 ⇒ 生产行为可二分。</summary>
        public bool ladderEnabled = true;
        /// <summary>上次**阶梯**规划的时刻（物理 tick = 秒）。**最小重规划间隔**靠它：
        /// 没有它会出现"每步重规划"（阶梯给出小修正 ⇒ 一步打完 ⇒ 队列又空 ⇒ 又问……）。</summary>
        public double ladderLastPlanTick = double.NegativeInfinity;
        /// <summary>最小重规划间隔（秒）。与"点火死区/控制周期"同族：防"律在算、白算"。</summary>
        public double ladderMinReplanSeconds = 30.0;
        /// <summary>
        /// **远场归属**：本船当前的连续机动由**阶梯**负责（true）还是**连续律**（false）。
        /// ⚠ 必须是**粘滞**的（2026-09-20 游戏内实测）——第一版把归属只放在"规划的拍子"上，
        ///   两次规划之间的空档就落回了连续律：日志里于是交替出现
        ///   `[阶梯] … 排入 2 发` 与 `[Guidance] … correction burn started (new-law) dv=0.4 m/s miss=1e7 m` ✗。
        ///   这是 `PlanLeg` 那条"**模式必须按段决定、一段内固定**"教训的**上一层**版本
        ///   （那里是形状/相位两通道互相抵消，这里是两套机制互相插队）。
        /// </summary>
        public bool ladderOwnsFarField = false;
        /// <summary>上次引导修正烧开始的 physicsTick（冷却用，不序列化）。</summary>
        public double rendezvousLastBurnTick = double.NegativeInfinity;

        // ── 新远场律（RendezvousGuidanceLaw）的跨周期状态（全部不序列化）────────────
        // 为什么可以不序列化：新律是**无截止时刻的反馈律**，任何初值都合法（最坏是重起一段）。
        // 它与旧路径的"固定到达时刻"不同 —— 旧路径的 rendezvousArrivalTick 是有语义的约定，
        // 这里的状态只是律的积分器/迟滞器的记忆，丢了只损失一点收敛速度。
        /// <summary>新律：e 通道迟滞继电器状态（见 RendezvousGuidanceLaw 的"不能用连续比例常开"）。</summary>
        public bool rendezvousEccRelay = false;
        /// <summary>新律：当前一段的时间预算终点（physicsTick 秒；-∞ = 尚未分腿）。</summary>
        public double rendezvousLawLegEndTick = double.NegativeInfinity;
        /// <summary>新律：上一周期的相位差（rad）——用于估下一段腿长（相位变化慢，足够）。</summary>
        public double rendezvousLawLastPhaseErr = 0.0;
        /// <summary>新律：首次求值时刻（physicsTick 秒；-∞ = 尚未求值）——"给够机会"的兜底交班判据用。</summary>
        public double rendezvousLawStartTick = double.NegativeInfinity;
        /// <summary>
        /// 新律：**每段（腿）的时间预算长度（s）**。0 = 用游戏侧的默认值。
        /// 这是"时间当自由变量"的那个旋钮：相位守恒律 Δv·t = (2/3)·r·Δφ ⇒ 腿越长越省 Δv、越慢。
        /// （台架固定 60000；游戏内留给场景/将来的 UI 选择"快而贵 / 慢而省"。）
        /// </summary>
        public double rendezvousLawLegSeconds = 0.0;
        /// <summary>
        /// 新律：**本段的模式**。true = 形状段（先把两条轨道开到同一半长轴，`Compose(closePhase:false)`）；
        /// false = 相位段（a 已对上，收相位差，`closePhase:true`）。
        ///
        /// ⚠ 必须**按段决定、一段内固定**：相位通道本来就靠"改 a"来收相位（δn≠0 正是它的工作手段），
        /// 若按瞬时 δn 连续判断模式，相位段一建起 δn 就会被形状通道当成"轨道不匹配"给抵消掉。
        /// </summary>
        public bool rendezvousLawShapeMode = false;
        /// <summary>
        /// 新律：**最近一次交会接近距离**（m，不序列化）。
        /// 终端卡在极限环时界面看起来"已经交会了"，而判据（300m/2m/s）过不去 —— 差多少必须能被日志读到。
        /// </summary>
        public double rendezvousBestDistM = double.PositiveInfinity;
        /// <summary>新律：终端段下一次打印读数的时刻（physicsTick，不序列化；节流 60 模拟秒）。</summary>
        public double rendezvousNextLogTick = double.NegativeInfinity;

        /// <summary>
        /// 登记交会引导（精确交会）。出发段机动需带 <see cref="PlannedManeuver.armRendezvousGuidance"/>，
        /// 其点火时调用 <see cref="ArmRendezvousGuidance"/> 使引导生效；出发段被阻止/取消则引导自动作废。
        /// </summary>
        /// <param name="useNetworks">限定使用的工质网络；**null = 全启用**（这是正常用法，不是缺省容错）。</param>
        public void RegisterRendezvousGuidance(int targetId, double arrivalTick, bool prograde, HashSet<PropellantDef>? useNetworks)
        {
            ClearApproachGuidance(); // 互斥：规划交会即取消接近
            HasRendezvousGuidance = true;
            rendezvousArmed = false;
            rendezvousTargetId = targetId;
            rendezvousArrivalTick = arrivalTick;
            rendezvousPrograde = prograde;
            rendezvousUseNetworks = useNetworks != null ? new HashSet<PropellantDef>(useNetworks) : null;
            rendezvousLastCheckTick = double.NegativeInfinity;
            rendezvousLastBurnTick = double.NegativeInfinity;
            ResetRendezvousLawState();
        }

        /// <summary>出发段点火执行，引导生效。</summary>
        public void ArmRendezvousGuidance()
        {
            rendezvousArmed = true;
        }

        /// <summary>清除交会引导（玩家手动接管 / 出发段失败 / 交会完成）。</summary>
        public void ClearRendezvousGuidance()
        {
            HasRendezvousGuidance = false;
            rendezvousArmed = false;
            rendezvousTargetId = -1;
            rendezvousArrivalTick = 0.0;
            rendezvousPrograde = true;
            rendezvousUseNetworks = null;
            rendezvousTargetOffset = DVector3.zero;
            ResetRendezvousLawState();
        }

        /// <summary>新远场律的跨周期状态复位（每次重新登记/清除引导都做，避免上一段交会的记忆泄漏过来）。</summary>
        public void ResetRendezvousLawState()
        {
            rendezvousEccRelay = false;
            rendezvousLawLegEndTick = double.NegativeInfinity;
            rendezvousLawLastPhaseErr = 0.0;
            rendezvousLawStartTick = double.NegativeInfinity;
            rendezvousLawLegSeconds = 0.0;
            rendezvousLawShapeMode = false;
            rendezvousBestDistM = double.PositiveInfinity;
            rendezvousNextLogTick = double.NegativeInfinity;
        }

        #endregion

        #region 近距离接近 (Close-Range Approach)

        /// <summary>是否处于近距离接近模式（交会引导的特化：动态到达时刻、无出发段）。</summary>
        public bool HasApproachGuidance => HasRendezvousGuidance && rendezvousIsApproach;

        /// <summary>接近目标群 ID（等价 rendezvousTargetId）。</summary>
        public int ApproachTargetId => rendezvousTargetId;

        /// <summary>
        /// 登记近距离接近引导：复用交会引导引擎（Lambert 拦截 + 终端消速），但到达时刻不固定，
        /// 每检查周期按当前距离动态重算（tof = dist / 限速）。无出发段，登记即生效。
        /// 由玩家手动触发（空间信息面板「近距离接近」按钮选择目标）。
        /// </summary>
        public void RegisterApproachGuidance(int targetId, HashSet<PropellantDef>? useNetworks)
        {
            RegisterApproachGuidance(targetId, useNetworks, DVector3.zero);
        }

        /// <summary>
        /// 带瞄准偏移的接近引导：瞄准 targetGroupPos + 目标实体偏移 + 姿态旋转(targetOffset)。
        /// 跳帮落点用（落点 = 目标空间站局部系偏移）；零偏移 = 瞄准 group 中心（原行为）。
        /// </summary>
        public void RegisterApproachGuidance(int targetId, HashSet<PropellantDef>? useNetworks, DVector3 targetOffset)
        {
            ClearRendezvousGuidance(); // 互斥：清除旧的交会/接近引导
            HasRendezvousGuidance = true;
            rendezvousIsApproach = true;
            rendezvousArmed = true;      // 接近无出发段，登记即生效
            rendezvousTargetId = targetId;
            rendezvousArrivalTick = 0.0; // 动态重算，占位
            rendezvousPrograde = true;
            rendezvousUseNetworks = useNetworks != null ? new HashSet<PropellantDef>(useNetworks) : null;
            rendezvousTargetOffset = targetOffset;
            rendezvousLastCheckTick = double.NegativeInfinity;
            rendezvousLastBurnTick = double.NegativeInfinity;
            ResetRendezvousLawState();
        }

        /// <summary>清除接近引导（等价清除交会引导）。</summary>
        public void ClearApproachGuidance() => ClearRendezvousGuidance();

        #endregion

        /// <summary>
        /// 获取中心天体在行星参考系中的位置偏移。
        /// 如果中心天体是行星（地球自身），返回零向量。
        /// </summary>
        public DVector3 GetCentralBodyOffset(int currentTick)
        {
            return GetCentralBodyOffset((double)currentTick);
        }

        public DVector3 GetCentralBodyOffset(double currentTick)
        {
            if (CelestialBodyId < 0) return DVector3.zero;
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr == null) return DVector3.zero;
            var centralDef = celestialMgr.GetBodyById(CelestialBodyId);
            if (centralDef == null || centralDef.bodyType == CelestialBodyType.Planet)
                return DVector3.zero;
            return celestialMgr.GetBodyLocalPosition(CelestialBodyId, currentTick);
        }

        /// <summary>
        /// 获取中心天体在行星参考系中的速度。
        /// 如果中心天体是行星（地球自身），返回零向量。
        /// </summary>
        public DVector3 GetCentralBodyVelocity(int currentTick)
        {
            if (CelestialBodyId < 0) return DVector3.zero;
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr == null) return DVector3.zero;
            var centralDef = celestialMgr.GetBodyById(CelestialBodyId);
            if (centralDef == null || centralDef.bodyType == CelestialBodyType.Planet)
                return DVector3.zero;
            return celestialMgr.GetBodyLocalVelocity(CelestialBodyId, currentTick);
        }

        /// <summary>
        /// 进入圆轨模式（自动获取 physicsTick）。
        /// 兼容旧调用，内部会从 TimeScaleManager 获取 physicsTick。
        /// </summary>
        public void EnterCircularOrbit(int realTick, int centralBodyId)
        {
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            double physicsTick = tsm != null ? tsm.GetPhysicsTick() : (double)Find.TickManager.TicksGame;
            EnterCircularOrbit(realTick, physicsTick, centralBodyId);
        }

        /// <summary>
        /// 进入圆轨模式。
        /// 自动开启轨道保持（白名单 = {centralBodyId}），仅保留中心天体的引力。
        /// </summary>
        /// <param name="realTick">当前真实游戏 tick（用于天体位置查询）</param>
        /// <param name="physicsTick">当前物理 tick（用于圆轨起始时间，受时间挡位影响）</param>
        /// <param name="centralBodyId">环绕的中心天体 ID</param>
        public void EnterCircularOrbit(int realTick, double physicsTick, int centralBodyId)
        {
            DVector3 pos = CurrentOrbit.position;
            DVector3 vel = CurrentOrbit.velocity;

            // 如果中心天体不是行星（如月球），将 pos/vel 转为相对中心天体
            // 采样用 physicsTick：CurrentOrbit 是 physicsTick 时间轴上的状态，
            // 与后续 GetCircularPositionAtTick / N 体积分同基准（realTick 在倍率≠1 时错位）
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr != null)
            {
                var centralDef = celestialMgr.GetBodyById(centralBodyId);
                if (centralDef != null && centralDef.bodyType != CelestialBodyType.Planet)
                {
                    pos -= celestialMgr.GetBodyLocalPosition(centralBodyId, physicsTick);
                    vel -= celestialMgr.GetBodyLocalVelocity(centralBodyId, physicsTick);
                }
            }

            double r = pos.magnitude;
            if (r < 0.01)
            {
                Log.Warning(string.Format("SpaceEntityGroup {0}: Cannot enter circular orbit at near-zero radius.", GroupId));
                return;
            }

            // 最小轨道半径 ≥ 天体半径：低于表面强行入轨 = 圆轨解析位置在地表以下，必坠毁
            var centralDefForRadius = celestialMgr?.GetBodyById(centralBodyId);
            if (centralDefForRadius != null && centralDefForRadius.radius > 0.0 && r < centralDefForRadius.radius)
            {
                Log.Warning(string.Format("SpaceEntityGroup {0}: Cannot enter circular orbit around body {1}: orbit radius {2:F0} km < body radius {3:F0} km.",
                    GroupId, centralBodyId, r / 1000.0, centralDefForRadius.radius / 1000.0));
                return;
            }

            DVector3 radialDir = pos.normalized;
            // 不校验切向速度：调用方（空间站/飞船部署等）可能只提供位置、速度为零。
            // 下方会强制用中心天体 GM 重算圆轨速度 sqrt(mu/r)，零速度也能入轨。

            // 用中心天体的 GM 重新计算正确的环轨速度，避免入轨速度用了错误 GM
            double mu = SpaceConstants.PrimaryGravitationalParameter;
            if (celestialMgr != null)
            {
                var centralDef = celestialMgr.GetBodyById(centralBodyId);
                if (centralDef != null && centralDef.gravitationalParameter > 0.0)
                    mu = centralDef.gravitationalParameter;
            }
            double v_tan = System.Math.Sqrt(mu / r);
            double omega = v_tan / r;

            // 轨道平面法线 = pos × vel 的方向；速度为零 / 纯径向时退化为默认平面（up）
            DVector3 normal = DVector3.Cross(pos, vel).normalized;
            if (normal.sqrMagnitude < 0.01)
                normal = DVector3.up; // 退化情况（纯径向速度 / 零速度）

            IsCircularOrbit = true;
            circularCentralBodyId = centralBodyId;
            circularRadius = r;
            circularAngularVelocity = omega;
            circularStartAngle = 0.0;
            circularStartTick = physicsTick;
            circularOrbitNormal = normal;
            circularRadialRef = radialDir;
            InvalidateOrbitCache();

            // 将 CurrentOrbit.velocity 同步为圆轨切向速度
            DVector3 tangentDir = DVector3.Cross(normal, radialDir).normalized;
            if (tangentDir.sqrMagnitude < 0.01)
            {
                // 极罕见：radialDir 与轨道平面法线平行（位置沿法线方向且速度为零）
                DVector3 refVec = System.Math.Abs(radialDir.y) < 0.9 ? DVector3.up : DVector3.forward;
                tangentDir = DVector3.Cross(refVec, radialDir).normalized;
            }
            DVector3 circularVel = tangentDir * v_tan;

            // 更新中心天体相对坐标（用于 R/T/N 基向量）
            localPosition = pos;        // 进入圆轨时已转为相对坐标
            localVelocity = circularVel; // 纯切向速度，不含中心天体轨道速度
            Log.Message(string.Format("[Orbit] Group {0} EnterCircularOrbit: body={1} r={2:F0}km |localPos|={3:F0}km |localVel|={4:F1} m/s",
                GroupId, centralBodyId, r / 1000.0, localPosition.magnitude / 1000.0, localVelocity.magnitude));

            // 如果中心天体不是行星，加回其轨道速度（转回地球参考系）
            if (celestialMgr != null)
            {
                var centralDefInner = celestialMgr.GetBodyById(centralBodyId);
                if (centralDefInner != null && centralDefInner.bodyType != CelestialBodyType.Planet)
                {
                    circularVel += celestialMgr.GetBodyLocalVelocity(centralBodyId, physicsTick);
                }
            }
            CurrentOrbit.velocity = circularVel;
            CelestialBodyId = centralBodyId;

            // 自动开启轨道保持：白名单只保留中心天体，抵消其他摄动
            EnterOrbitKeeping(new HashSet<int> { centralBodyId });
        }

        /// <summary>
        /// 退出圆轨模式，根据当前角度计算切向速度和位置，更新 CurrentOrbit。
        /// 使用记录的轨道平面法线和径向参考方向，支持倾斜轨道。
        /// </summary>
        public void ExitCircularOrbit()
        {
            if (!IsCircularOrbit) return;

            var timeScaleManager = Current.Game?.GetComponent<TimeScaleManager>();
            double physicsTick = timeScaleManager != null ? timeScaleManager.GetPhysicsTick() : (double)Find.TickManager.TicksGame;

            DVector3 pos = GetCircularPositionAtTick(physicsTick);
            DVector3 radialDir = (pos - GetCentralBodyOffset(physicsTick)).normalized;

            // 切向方向：在轨道平面内垂直于径向
            DVector3 tangentDir;
            if (circularOrbitNormal.sqrMagnitude > 0.001)
            {
                tangentDir = DVector3.Cross(circularOrbitNormal, radialDir).normalized;
            }
            else
            {
                // 旧存档兼容：XZ 平面
                tangentDir = new DVector3(-radialDir.z, 0, radialDir.x).normalized;
            }

            DVector3 newVel = tangentDir * (circularAngularVelocity * circularRadius);

            // 更新中心天体相对坐标（用于 R/T/N 基向量）
            localPosition = pos - GetCentralBodyOffset(physicsTick);
            localVelocity = newVel;

            CurrentOrbit.velocity = newVel;

            // 如果中心天体不是行星，速度要加回中心天体的轨道速度
            if (circularCentralBodyId >= 0)
            {
                var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (celestialMgr != null)
                {
                    var centralDef = celestialMgr.GetBodyById(circularCentralBodyId);
                    if (centralDef != null && centralDef.bodyType != CelestialBodyType.Planet)
                    {
                        CurrentOrbit.velocity += celestialMgr.GetBodyLocalVelocity(circularCentralBodyId, physicsTick);
                    }
                }
            }

            CurrentOrbit.position = pos;

            IsCircularOrbit = false;
            circularCentralBodyId = -1;
            circularRadius = 0.0;
            circularAngularVelocity = 0.0;
            circularStartAngle = 0.0;
            circularStartTick = 0;
            circularOrbitNormal = DVector3.up;
            circularRadialRef = DVector3.right;
            InvalidateOrbitCache();

            // 退出轨道保持
            ExitOrbitKeeping();
        }

        /// <summary>
        /// 获取圆轨模式下当前时刻的 3D 位置（使用轨道平面法线）。
        /// 支持倾斜轨道。旧存档（无法线数据）回退到 XZ 平面。
        /// 注意：currentTick 应为 physicsTick（受时间挡位影响的虚拟tick）。
        /// </summary>
        public DVector3 GetCircularPositionAtTick(int currentTick)
        {
            return GetCircularPositionAtTick((double)currentTick);
        }

        /// <summary>
        /// 获取圆轨模式下当前时刻的 3D 位置（支持子tick，用于战斗模式）。
        /// currentTick 应为 physicsTick（受时间挡位影响的虚拟tick）。
        /// </summary>
        public DVector3 GetCircularPositionAtTick(double currentTick)
        {
            if (!IsCircularOrbit) return DVector3.zero;

            // 同一 physicsTick 下圆轨位置是纯解析函数 → 懒缓存，其余调用直接命中
            if (currentTick == cachedPosTick)
            {
#if DEBUG
                debugPosCacheHits++;
#endif
                return cachedPos;
            }
#if DEBUG
            debugPosCacheMisses++;
#endif

            // circularAngularVelocity 是 rad/秒；physicsTick 是游戏世界秒（1 tick = 1 秒），
            // 与 N 体积分 / 天体公转 / 机动规划使用同一时间基准，无需额外换算。
            double deltaAngle = circularAngularVelocity * (currentTick - circularStartTick);
            double angle = circularStartAngle + deltaAngle;
            double cosA = System.Math.Cos(angle);
            double sinA = System.Math.Sin(angle);

            DVector3 localPos;
            if (circularOrbitNormal.sqrMagnitude > 0.001 && circularRadialRef.sqrMagnitude > 0.001)
            {
                // 使用轨道平面：pos = r * (radialRef * cos(θ) + yDir * sin(θ))
                DVector3 yDir = DVector3.Cross(circularOrbitNormal, circularRadialRef).normalized;
                localPos = circularRadius * (circularRadialRef * cosA + yDir * sinA);
            }
            else
            {
                // 旧存档兼容：XZ 平面
                localPos = new DVector3(System.Math.Cos(angle) * circularRadius, 0, System.Math.Sin(angle) * circularRadius);
            }

            // 如果中心天体不是行星，将局部坐标转回行星参考系
            // 注意：使用 currentTick（physicsTick），与发动机积分器保持一致
            if (circularCentralBodyId >= 0)
            {
                localPos += GetCentralBodyOffset(currentTick);
            }

            cachedPosTick = currentTick;
            cachedPos = localPos;
            return localPos;
        }

        /// <summary>
        /// 获取圆轨模式下当前时刻的解析速度（世界系，含中心天体公转速度），支持子 tick。
        /// 与 <see cref="GetCircularPositionAtTick"/> 相同采用 per-physicsTick 懒缓存。
        /// 非圆轨模式返回零向量（调用方应先判断 IsCircularOrbit）。
        /// </summary>
        public DVector3 GetCircularVelocityAtTick(double currentTick)
        {
            if (!IsCircularOrbit) return DVector3.zero;

            if (currentTick == cachedVelTick)
            {
#if DEBUG
                debugVelCacheHits++;
#endif
                return cachedVel;
            }
#if DEBUG
            debugVelCacheMisses++;
#endif

            DVector3 pos = GetCircularPositionAtTick(currentTick); // 命中位置缓存，不重复解析
            DVector3 radialDir = (pos - GetCentralBodyOffset(currentTick)).normalized;
            DVector3 tangentDir;
            if (circularOrbitNormal.sqrMagnitude > 0.001)
                tangentDir = DVector3.Cross(circularOrbitNormal, radialDir).normalized;
            else
                tangentDir = new DVector3(-radialDir.z, 0, radialDir.x).normalized;
            DVector3 vel = tangentDir * (circularAngularVelocity * circularRadius);
            // 绕卫星（月球）时：世界速度 = 圆轨切向 + 中心天体公转速度（行星中心体为世界原点，速度为零）
            if (circularCentralBodyId >= 0)
            {
                var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (celestialMgr != null)
                {
                    var centralDef = celestialMgr.GetBodyById(circularCentralBodyId);
                    if (centralDef != null && centralDef.bodyType != CelestialBodyType.Planet)
                        vel += celestialMgr.GetBodyLocalVelocity(circularCentralBodyId, currentTick);
                }
            }

            cachedVelTick = currentTick;
            cachedVel = vel;
            return vel;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref GroupId, "groupId");
            Scribe_Values.Look(ref FactionId, "factionId");
            Scribe_Values.Look(ref IsActive, "isActive", true);
            Scribe_Values.Look(ref IsMovable, "isMovable", true);
            Scribe_Values.Look(ref IsCollidable, "isCollidable", true);
            Scribe_Values.Look(ref HasGravityWell, "hasGravityWell", false);
            // 匿踪闸门（[V1]）：旧存档无此节点 → 默认 false（公开坐标），与"飞船/站不广播"一致
            Scribe_Values.Look(ref IsStealth, "isStealth", false);
            // 主动探测开关（[V1]）：旧存档无节点 → false（被动）
            Scribe_Values.Look(ref activePing, "activePing", false);
            Scribe_Deep.Look(ref CurrentOrbit, "currentOrbit");
            DVector3.Scribe("angularVelocity", ref angularVelocity);
            Scribe_Values.Look(ref maxAngularRate, "maxAngularRate", -1f); // -1 = 默认常量（旧存档兼容）
            Scribe_Values.Look(ref OrbitDirty, "orbitDirty", true);
            Scribe_Collections.Look(ref entityIds, "entityIds", LookMode.Value);
            Scribe_Values.Look(ref CelestialBodyId, "celestialBodyId", -1);
            Scribe_Values.Look(ref exemptPlanetCollision, "exemptPlanetCollision", false);
            // 地表锚点（星座成员载体）：旧存档无此节点 → 默认非锚点
            Scribe_Values.Look(ref IsSurfaceAnchor, "isSurfaceAnchor", false);
            Scribe_Values.Look(ref anchorWorldObjectId, "anchorWorldObjectId", -1);
            // 圆轨模式数据序列化
            Scribe_Values.Look(ref IsCircularOrbit, "isCircularOrbit", false);
            Scribe_Values.Look(ref circularRadius, "circularRadius", 0.0);
            Scribe_Values.Look(ref circularAngularVelocity, "circularAngularVelocity", 0.0);
            Scribe_Values.Look(ref circularStartAngle, "circularStartAngle", 0.0);
            Scribe_Values.Look(ref circularStartTick, "circularStartTick", 0.0);
            Scribe_Values.Look(ref circularCentralBodyId, "circularCentralBodyId", -1);
            DVector3.Scribe("circularOrbitNormal", ref circularOrbitNormal);
            DVector3.Scribe("circularRadialRef", ref circularRadialRef);

            // 机动规划队列序列化
            Scribe_Collections.Look(ref plannedManeuvers, "plannedManeuvers", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && plannedManeuvers == null)
                plannedManeuvers = new List<PlannedManeuver>();

            // 空间站「当前网络」（单选，新机动继承）序列化
            Scribe_Defs.Look(ref currentNetwork, "currentNetwork");

            // 轨道保持序列化
            Scribe_Values.Look(ref HasOrbitKeeping, "hasOrbitKeeping", false);
            if (Scribe.mode == LoadSaveMode.LoadingVars && HasOrbitKeeping && orbitKeepingWhitelist == null)
                orbitKeepingWhitelist = new HashSet<int>();
            // Scribe 不支持直接序列化 HashSet<int>，转 List<int>
            List<int> whitelistList = null;
            if (Scribe.mode == LoadSaveMode.Saving)
                whitelistList = orbitKeepingWhitelist != null ? new List<int>(orbitKeepingWhitelist) : new List<int>();
            Scribe_Collections.Look(ref whitelistList, "orbitKeepingWhitelist", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && whitelistList != null)
            {
                if (orbitKeepingWhitelist == null)
                    orbitKeepingWhitelist = new HashSet<int>(whitelistList);
                else
                {
                    orbitKeepingWhitelist.Clear();
                    orbitKeepingWhitelist.UnionWith(whitelistList);
                }
            }

            // 交会引导序列化
            Scribe_Values.Look(ref HasRendezvousGuidance, "hasRendezvousGuidance", false);
            Scribe_Values.Look(ref rendezvousArmed, "rendezvousArmed", false);
            Scribe_Values.Look(ref rendezvousIsApproach, "rendezvousIsApproach", false);
            Scribe_Values.Look(ref rendezvousTargetId, "rendezvousTargetId", -1);
            DVector3.Scribe("rendezvousTargetOffset", ref rendezvousTargetOffset);
            Scribe_Values.Look(ref rendezvousArrivalTick, "rendezvousArrivalTick", 0.0);
            Scribe_Values.Look(ref rendezvousPrograde, "rendezvousPrograde", true);
            Scribe_Collections.Look(ref rendezvousUseNetworks, "rendezvousUseNetworks", LookMode.Def);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && !HasRendezvousGuidance)
            {
                // 旧存档 / 无效状态兜底：清空残留引导字段
                rendezvousArmed = false;
                rendezvousIsApproach = false;
                rendezvousTargetId = -1;
                rendezvousUseNetworks = null;
            }
        }
    }
}
