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

        public OrbitState CurrentOrbit;
        public bool OrbitDirty = true;

        private HashSet<int> entityIds = new HashSet<int>();

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
                return startPos + t * startVel + 0.5 * t * t * acc + (t * t * t) / 6.0 * jerk;
            }

            public DVector3 InterpolateVelocity(double t)
            {
                return startVel + t * acc + 0.5 * t * t * jerk;
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

        public void AddEntity(int entityId) => entityIds.Add(entityId);
        public void RemoveEntity(int entityId) => entityIds.Remove(entityId);
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
            ClearRendezvousGuidance(); // 玩家新排队机动 = 手动接管，交会/接近引导让位（接近是其特化，一并清除）
            plannedManeuvers.Add(maneuver);
        }

        /// <summary>
        /// 添加一个机动规划（指定 dV 和相对时间）。同样继承「当前网络」。
        /// 注意：executeTick 使用 physicsTick 基准（与 ManeuverPlanner 一致）；
        /// 检查/执行在 SpaceGameComp 中用 physicsTick 判定，两基准错位会导致 Fast 档到期失真。
        /// </summary>
        public void QueueManeuver(float radDV, float tanDV, float normDV, int deltaTicks)
        {
            int execTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe()) + deltaTicks;
            ClearRendezvousGuidance(); // 玩家手动机动 = 接管，交会/接近引导让位
            plannedManeuvers.Add(new PlannedManeuver
            {
                radialDV = radDV,
                tangentialDV = tanDV,
                normalDV = normDV,
                executeTick = execTick,
                active = true,
                useNetworks = InheritNetworks()
            });
        }

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

        #endregion

        #region 主动燃烧 (Active Burn)

        // ── 姿态控制（魔法动量轮）──
        /// <summary>角速度（rad/s，魔法动量轮直设；每物理步积分到 CurrentOrbit.rotation）。</summary>
        public DVector3 angularVelocity;
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
            burnDirection = dV.normalized;
            remainingBurnDV = dV.magnitude;
            appliedBurnDV = 0.0;
            burnStartMass = 0.0;      // 首次 burn tick 时记录（此时质量已知）
            burnConsumedPropellant = 0.0;
        }

        /// <summary>
        /// 立即结束燃烧（剩余 dV 丢弃）。
        /// </summary>
        public void EndBurn()
        {
            HasActiveBurn = false;
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
        /// <summary>上次引导修正烧开始的 physicsTick（冷却用，不序列化）。</summary>
        public double rendezvousLastBurnTick = double.NegativeInfinity;

        /// <summary>
        /// 登记交会引导（精确交会）。出发段机动需带 <see cref="PlannedManeuver.armRendezvousGuidance"/>，
        /// 其点火时调用 <see cref="ArmRendezvousGuidance"/> 使引导生效；出发段被阻止/取消则引导自动作废。
        /// </summary>
        public void RegisterRendezvousGuidance(int targetId, double arrivalTick, bool prograde, HashSet<PropellantDef> useNetworks)
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
        public void RegisterApproachGuidance(int targetId, HashSet<PropellantDef> useNetworks)
        {
            RegisterApproachGuidance(targetId, useNetworks, DVector3.zero);
        }

        /// <summary>
        /// 带瞄准偏移的接近引导：瞄准 targetGroupPos + 目标实体偏移 + 姿态旋转(targetOffset)。
        /// 跳帮落点用（落点 = 目标空间站局部系偏移）；零偏移 = 瞄准 group 中心（原行为）。
        /// </summary>
        public void RegisterApproachGuidance(int targetId, HashSet<PropellantDef> useNetworks, DVector3 targetOffset)
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
            Scribe_Deep.Look(ref CurrentOrbit, "currentOrbit");
            DVector3.Scribe("angularVelocity", ref angularVelocity);
            Scribe_Values.Look(ref maxAngularRate, "maxAngularRate", -1f); // -1 = 默认常量（旧存档兼容）
            Scribe_Values.Look(ref OrbitDirty, "orbitDirty", true);
            Scribe_Collections.Look(ref entityIds, "entityIds", LookMode.Value);
            Scribe_Values.Look(ref CelestialBodyId, "celestialBodyId", -1);
            Scribe_Values.Look(ref exemptPlanetCollision, "exemptPlanetCollision", false);
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
