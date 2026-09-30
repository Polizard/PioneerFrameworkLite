using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>跳帮单位类型。</summary>
    public enum VesselType
    {
        Eva,     // 单兵 EVA：1 成员，小型命中盒，各选落点可多点突入
        Shuttle  // 穿梭机：N 成员乘客池，大命中盒，被击毁乘客全灭
    }

    /// <summary>跳帮单位状态。</summary>
    public enum BoardingState
    {
        Flying,    // 飞行中（含降落判定窗口）
        Landed,    // 已降落（单兵已 Spawn / 穿梭机已停靠），记录待注销
        Destroyed  // 飞行中被击毁（成员已结算死亡）
    }

    /// <summary>
    /// 跳帮成员：母船 Map pawn 的引用（LookMode.Reference）。
    /// 飞行中 pawn 为未生成（DeSpawn）状态，由本对象持有；降落时 Spawn 进目标 Map。
    /// </summary>
    public class BoardingMember : IExposable
    {
        public Pawn pawn;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
        }
    }

    /// <summary>
    /// 跳帮飞行单位：一个飞行物体 = 独立物理 group + 临时 3D 实体 + 一个编号。
    /// 编号贯穿 3D/Map 两态身份（单兵逐兵编号 / 穿梭机整机编号）。
    /// 存档只存 id 与数据：group/entity 由 SpaceGameComp 原生序列化，载入后懒解析（ResolveUnit）。
    /// </summary>
    public class BoardingUnit : IExposable
    {
        public int id;
        public VesselType type;
        public int groupId = -1;      // 临时 3D 物理 group
        public int entityId = -1;     // 临时 3D 实体
        public List<BoardingMember> members = new List<BoardingMember>();
        public int targetGroupId = -1;
        public int targetPartId = -1;
        public DVector3 landingOffsetShip;  // 落点 = 目标空间站局部系 3D 偏移（格 → 米）
        public int shuttleId = -1;           // 穿梭机稳定身份；EVA 恒 -1
        public BoardingState state = BoardingState.Flying;

        // 运行时懒解析缓存（不序列化）
        [Unsaved] public SpaceEntityGroup ResolvedGroup;
        [Unsaved] public SpaceEntity ResolvedEntity;
        /// <summary>降落窗口提示日志节流（真实 tick，不序列化）。</summary>
        [Unsaved] public int LoggedWindowEntryTick = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref type, "type", VesselType.Eva);
            Scribe_Values.Look(ref groupId, "groupId", -1);
            Scribe_Values.Look(ref entityId, "entityId", -1);
            Scribe_Collections.Look(ref members, "members", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && members == null)
                members = new List<BoardingMember>();
            Scribe_Values.Look(ref targetGroupId, "targetGroupId", -1);
            Scribe_Values.Look(ref targetPartId, "targetPartId", -1);
            DVector3.Scribe("landingOffsetShip", ref landingOffsetShip);
            Scribe_Values.Look(ref shuttleId, "shuttleId", -1);
            Scribe_Values.Look(ref state, "state", BoardingState.Flying);
        }
    }

    /// <summary>
    /// 跳帮（登舰突击）管理器：唯一管理跳帮单位 + 穿梭机全生命周期。
    /// 与 SpaceGameComp / CombatManager 平级（GameComponent，RimWorld 自动实例化）。
    /// 职责边界：不做实体/group 创建（归本类内部工厂，见 CreateTransitVehicle）、
    /// 不做物理积分（归 SpaceGameComp）；本类只做生命周期逻辑 + 降落判定 + 序列化。
    /// </summary>
    public class BoardingManager : GameComponent
    {
        // ── 判定参数 ──
        /// <summary>距移动落点（世界）小于此值 → 降落窗口内（EVA/穿梭机共用基准）。</summary>
        public const double LandingDistanceThreshold = 400.0;
        /// <summary>降落需相对速度低于此值（m/s，低速接舷）。</summary>
        public const double LandingRelVelThreshold = 15.0;
        /// <summary>接舷分支消费碰撞的最高相对速度（m/s）：低于此值且对准落点 → 不弹飞不爆炸，交给降落判定。</summary>
        public const double ContactConsumeRelVel = 30.0;
        /// <summary>EVA 单兵命中盒边长（m，比视觉略大，避免"永远擦过"无敌）。</summary>
        public const double EvaSizeMeters = 3.0;
        /// <summary>穿梭机命中盒边长（m）。</summary>
        public const double ShuttleSizeMeters = 60.0;
        /// <summary>EVA 单兵 HP（炮火可擦伤，一击可毁）。</summary>
        public const float EvaMaxHp = 50f;
        /// <summary>穿梭机 part HP（装甲量）。</summary>
        public const float ShuttleMaxHp = 300f;

        // ── 跳帮专属持续引导参数 ──
        // 实测通用接近引导不适合跳帮：节流（30tick 检查 + 60tick 冷却）+ 巡航 Lambert（近距离 dv 60+ 反复
        // 振荡）+ 目标漂移 → 单位 20 分钟才收拢 2km、卡在 5km 巡航边缘。跳帮单位每物理步直接算 dv 立即烧。
        /// <summary>收拢速度上限（m/s）：朝移动落点的最大接近速度。</summary>
        public const double ApproachMaxSpeed = 25.0;
        /// <summary>接近减速时间常数（s）：接近时收拢速度 = dist / τ，保证贴到落点前减速到接近 0。</summary>
        public const double ApproachTimeConst = 200.0;
        /// <summary>引导修正死区（m/s）：低于此值不烧。</summary>
        public const double GuidanceDeadband = 0.5;

        private List<BoardingUnit> units = new List<BoardingUnit>();
        private int nextBoardingId = 1;
        private int nextShuttleId = 1;

        // ── 调试/测试 ──
        /// <summary>详细日志开关（每物理步的引导/窗口状态）。测试面板可勾选。</summary>
        public static bool DebugDetailedBoarding = false;
        /// <summary>状态探针间隔（真实 tick）。0 = 关闭探针日志；&gt;0 时每间隔打印全部飞行单位状态。</summary>
        public static int ProbeInterval = 0;
        private int probeNextTick = 0;

        // ── 累计统计（不序列化，测试面板/日志用）──
        /// <summary>累计发射数（EVA 兵逐兵计数 / 穿梭机整机计数）。</summary>
        public int TotalLaunched;
        /// <summary>累计降落成功数。</summary>
        public int TotalLanded;
        /// <summary>累计失败/击毁数（成员全灭）。</summary>
        public int TotalDestroyed;

        /// <summary>单位结算记录（不序列化；Landed 单位注销后用于测试面板显示/验证）。</summary>
        public class BoardingResultRecord
        {
            public int unitId;
            public VesselType type;
            public BoardingState outcome;
            public int tick;
            public string detail;
            public int memberCount;
            public List<Pawn> memberPawns;
        }

        /// <summary>最近结算记录（最多保留 20 条）。</summary>
        public readonly List<BoardingResultRecord> RecentResults = new List<BoardingResultRecord>();

        private void RecordResult(BoardingUnit unit, BoardingState outcome, string detail)
        {
            var record = new BoardingResultRecord
            {
                unitId = unit.id,
                type = unit.type,
                outcome = outcome,
                tick = Find.TickManager.TicksGame,
                detail = detail,
                memberCount = unit.members.Count
            };
            if (unit.members.Count > 0)
            {
                record.memberPawns = new List<Pawn>(unit.members.Count);
                for (int i = 0; i < unit.members.Count; i++)
                    record.memberPawns.Add(unit.members[i].pawn);
            }
            RecentResults.Add(record);
            if (RecentResults.Count > 20)
                RecentResults.RemoveAt(0);
        }

        public BoardingManager(Game game) { }

        // 性能第 5 条：接舷每物理步每单位都要取，按 Game 缓存
        private readonly GameComponentRef<SpaceGameComp> spaceGameRef = new GameComponentRef<SpaceGameComp>();
        private SpaceGameComp SpaceGame => spaceGameRef.Get();

        public IReadOnlyList<BoardingUnit> Units => units;

        public int NextShuttleId => nextShuttleId;

        private int AllocBoardingId() => nextBoardingId++;

        /// <summary>分配穿梭机稳定身份（CompShuttleDock 停靠 Thing 用）。</summary>
        public int AllocateShuttleId() => nextShuttleId++;

        // ════════════════════════════════════════════
        //  驱动（每 2 tick = 物理步，与 CombatManager 碰撞同节奏）
        // ════════════════════════════════════════════

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (Find.TickManager.TicksGame % 2 != 0) return;

            // 状态探针（测试/观察用）：每 ProbeInterval tick 打印全部飞行单位状态
            if (ProbeInterval > 0 && Find.TickManager.TicksGame >= probeNextTick)
            {
                probeNextTick = Find.TickManager.TicksGame + ProbeInterval;
                if (units.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[Boarding探针] t=").Append(Find.TickManager.TicksGame);
                    foreach (var unit in units)
                    {
                        var st = GetStatus(unit);
                        sb.Append(" | U").Append(unit.id).Append(unit.type == VesselType.Eva ? "EVA" : "SHL")
                          .Append(" ").Append(unit.state)
                          .Append(" dist=").Append(st.distToAim.ToString("F0"))
                          .Append("m relv=").Append(st.relVel.ToString("F1"))
                          .Append(" guid=").Append(st.guidanceActive ? "on" : "off")
                          .Append(st.partDestroyed ? " [击毁]" : "");
                    }
                    Log.Message(sb.ToString());
                }
            }

            for (int i = units.Count - 1; i >= 0; i--)
            {
                var unit = units[i];
                if (!ResolveUnit(unit))
                {
                    // group/entity 已消失且非正常结算 → 成员按死亡处理
                    Log.Message(string.Format("[Boarding] Unit {0} ({1}) 3D 对象已消失，按击毁结算", unit.id, unit.type));
                    KillMembers(unit, "3D 对象消失");
                    units.RemoveAt(i);
                    continue;
                }

                if (unit.state == BoardingState.Flying)
                    TickFlying(unit);
                else if (unit.state != BoardingState.Flying)
                    units.RemoveAt(i); // Landed/Destroyed 已结算，注销
            }
        }

        /// <summary>懒解析 group/entity（载入后 / group 重建后）。失败返回 false。</summary>
        private bool ResolveUnit(BoardingUnit unit)
        {
            bool wasNull = unit.ResolvedGroup == null || unit.ResolvedEntity == null;
            if (wasNull)
            {
                unit.ResolvedGroup = SpaceGame?.GetGroupById(unit.groupId);
                unit.ResolvedEntity = SpaceGame?.GetEntityById(unit.entityId);
                if (DebugDetailedBoarding)
                    Log.Message(string.Format("[Boarding] Unit {0} 懒解析 group={1}({2}) entity={3}({4})",
                        unit.id, unit.groupId, unit.ResolvedGroup != null ? "ok" : "缺失",
                        unit.entityId, unit.ResolvedEntity != null ? "ok" : "缺失"));
            }
            return unit.ResolvedGroup != null && unit.ResolvedEntity != null;
        }

        private void TickFlying(BoardingUnit unit)
        {
            var group = unit.ResolvedGroup;
            var target = ResolveTargetGroup(unit);

            // 目标空间站已消失 → 放弃任务（成员送回：EVA 回母船不可行，按失败处理）
            if (target == null)
            {
                Log.Message(string.Format("[Boarding] Unit {0} 目标空间站 {1} 已消失，任务取消", unit.id, unit.targetGroupId));
                KillMembers(unit, "目标空间站消失");
                SpaceGame?.DestroyGroup(unit.groupId); // 同步销毁临时 3D 实体，防孤儿幽灵船
                unit.state = BoardingState.Destroyed;
                return;
            }

            // 被击毁：part HP ≤ 0 → 单兵死亡 / 穿梭机乘客全灭
            foreach (var part in unit.ResolvedEntity.Parts)
            {
                if (part.isDestroyed)
                {
                    Log.Message(string.Format("[Boarding] Unit {0} ({1}) 飞行中被击毁", unit.id, unit.type));
                    KillMembers(unit, "跳帮单位被击毁");
                    SpaceGame?.DestroyGroup(unit.groupId);
                    unit.state = BoardingState.Destroyed;
                    return;
                }
            }

            // 跳帮专属持续引导：不依赖通用接近引导（节流 + 巡航 Lambert 对近距离极慢，实测卡在 5km 振荡）
            if (group.HasApproachGuidance)
                group.ClearApproachGuidance();

            // 降落判定：距移动落点 < 阈值 且 相对速度 < 阈值 → 降落成功
            DVector3 aim = GetAimPointSystem(unit);
            DVector3 groupPos = GetGroupPos(group);
            DVector3 unitVel = GetGroupVelocity(group);
            DVector3 targetVel = GetGroupVelocity(target);
            DVector3 toAim = aim - groupPos;
            double dist = toAim.magnitude;
            double relVel = (unitVel - targetVel).magnitude;

            if (DebugDetailedBoarding)
                Log.Message(string.Format("[Boarding] Unit {0} ({1}) 状态 dist落点={2:F0}m relv={3:F1}m/s aim=({4:F0},{5:F0},{6:F0})km 位=({7:F0},{8:F0},{9:F0})km",
                    unit.id, unit.type, dist, relVel,
                    aim.x / 1000.0, aim.y / 1000.0, aim.z / 1000.0,
                    groupPos.x / 1000.0, groupPos.y / 1000.0, groupPos.z / 1000.0));

            // 进入降落窗口但相对速度仍高：提示等待消速（每单位最多打一次，避免刷屏）
            if (dist < LandingDistanceThreshold && relVel >= LandingRelVelThreshold
                && unit.LoggedWindowEntryTick != Find.TickManager.TicksGame)
            {
                unit.LoggedWindowEntryTick = Find.TickManager.TicksGame;
                Log.Message(string.Format("[Boarding] Unit {0} ({1}) 已进入降落窗口 dist={2:F0}m（&lt;{3}），等待消速 relv={4:F1}m/s（&lt;{5}）",
                    unit.id, unit.type, dist, LandingDistanceThreshold, relVel, LandingRelVelThreshold));
            }

            if (BoardingMath.InLandingWindow(dist, relVel, LandingDistanceThreshold, LandingRelVelThreshold))
            {
                Log.Message(string.Format("[Boarding] Unit {0} ({1}) 降落判定通过 dist={2:F0}m relvel={3:F1}m/s",
                    unit.id, unit.type, dist, relVel));
                Land(unit, target);
                return;
            }

            // 持续引导：期望速度 = 目标速度 + 朝落点的收拢速度（≤上限，接近时按 dist/τ 减速）
            group.EndBurn(); // 清掉上一物理步的残余 burn，避免过量
            double approachSpeed = System.Math.Min(ApproachMaxSpeed, dist / ApproachTimeConst);
            DVector3 desiredVel = targetVel + toAim.normalized * approachSpeed;
            DVector3 dv = desiredVel - unitVel;
            if (dv.magnitude > GuidanceDeadband)
            {
                group.StartBurn(dv);
                if (DebugDetailedBoarding)
                    Log.Message(string.Format("[Boarding] Unit {0} 持续引导 dv={1:F1}m/s 收拢={2:F1}m/s",
                        unit.id, dv.magnitude, approachSpeed));
            }
        }

        // ════════════════════════════════════════════
        //  起飞（EVA / 穿梭机）
        // ════════════════════════════════════════════

        /// <summary>
        /// 单兵 EVA 起飞：母船 Map pawn → 未生成 → 临时 3D 实体 + 接近引导。
        /// 落点 = 目标空间站局部系偏移（由 UI/调用方给出）。
        /// </summary>
        /// <param name="initialVelocityOffset">初始速度偏移（m/s，测试用：模拟高速接近/偏离共轨）。null = 共轨起飞。</param>
        /// <param name="initialPositionOffset">初始位置偏移（m，相对母船原点，测试用：多兵分散出生避免 AABB 重叠互撞）。null = 母船原点。</param>
        public BoardingUnit LaunchEva(Pawn pawn, int targetGroupId, int targetPartId, DVector3 landingOffsetShip,
            DVector3? initialVelocityOffset = null, DVector3? initialPositionOffset = null)
        {
            if (pawn == null || pawn.Destroyed) return null;
            var spaceGame = SpaceGame;
            if (spaceGame == null) return null;

            var (motherEntity, motherGroup) = ResolveMothership(pawn.Map);
            if (motherEntity == null || motherGroup == null)
            {
                Log.Message("[Boarding] LaunchEva 失败：pawn 不在空间站内部 Map 中");
                return null;
            }

            int id = AllocBoardingId();
            pawn.DeSpawn();

            var (entity, group) = CreateTransitVehicle(id, VesselType.Eva, pawn.Faction?.loadID ?? 0,
                EvaSizeMeters, EvaMaxHp, dryMass: 100f, EvaPropulsion(), motherGroup,
                motherGroup.CelestialBodyId, initialVelocityOffset ?? DVector3.zero,
                initialPositionOffset ?? DVector3.zero);

            var unit = new BoardingUnit
            {
                id = id,
                type = VesselType.Eva,
                groupId = group.GroupId,
                entityId = entity.EntityId,
                targetGroupId = targetGroupId,
                targetPartId = targetPartId,
                landingOffsetShip = landingOffsetShip,
                state = BoardingState.Flying
            };
            unit.members.Add(new BoardingMember { pawn = pawn });
            units.Add(unit);
            TotalLaunched++;

            DVector3 startPos = GetGroupPos(group);
            DVector3 startVel = GetGroupVelocity(group);
            Log.Message(string.Format("[Boarding] EVA 起飞 Unit {0} 兵 {1} → 站 G{2}/Part{3} 参考系 body{4} " +
                "落点({5:F0},{6:F1},{7:F0})m 初位({8:F0},{9:F0},{10:F0})km 初速({11:F1},{12:F1},{13:F1})m/s",
                id, pawn.LabelShort, targetGroupId, targetPartId, group.CelestialBodyId,
                landingOffsetShip.x, landingOffsetShip.y, landingOffsetShip.z,
                startPos.x / 1000.0, startPos.y / 1000.0, startPos.z / 1000.0,
                startVel.x, startVel.y, startVel.z));
            return unit;
        }

        /// <summary>
        /// 穿梭机起飞：停靠 Thing（ShuttleId）→ 乘客上船(DeSpawn) → 销毁 Thing →
        /// 临时 3D 实体（继承 ShuttleId）+ 接近引导。
        /// </summary>
        public BoardingUnit LaunchShuttle(Thing shuttleThing, List<Pawn> passengers,
            int targetGroupId, int targetPartId, DVector3 landingOffsetShip,
            DVector3? initialPositionOffset = null, DVector3? initialVelocityOffset = null)
        {
            if (shuttleThing == null || shuttleThing.Destroyed) return null;
            var dock = shuttleThing.TryGetComp<CompShuttleDock>();
            if (dock == null || passengers == null || passengers.Count == 0) return null;

            // 只收编已生成的乘客：未生成 pawn（世界 pawn/caravan 成员）不随船，防"绑架"
            passengers = passengers.Where(p => p != null && !p.Destroyed && p.Spawned).ToList();
            if (passengers.Count == 0) return null;

            var spaceGame = SpaceGame;
            if (spaceGame == null) return null;

            var (motherEntity, motherGroup) = ResolveMothership(shuttleThing.Map);
            if (motherEntity == null || motherGroup == null)
            {
                Log.Message("[Boarding] LaunchShuttle 失败：穿梭机不在空间站内部 Map 中");
                return null;
            }

            int id = AllocBoardingId();
            int shuttleId = dock.shuttleId >= 0 ? dock.shuttleId : AllocateShuttleId();
            int factionId = shuttleThing.Faction?.loadID ?? 0; // 销毁前捕获

            // 乘客上船（转未生成）
            foreach (var p in passengers)
                if (p != null && !p.Destroyed && p.Spawned) p.DeSpawn();

            // 销毁停靠 Thing（停泊态 → 飞行态）
            shuttleThing.Destroy();

            var (entity, group) = CreateTransitVehicle(id, VesselType.Shuttle, factionId,
                ShuttleSizeMeters, ShuttleMaxHp, dryMass: 50000f, ShuttlePropulsion(), motherGroup,
                motherGroup.CelestialBodyId, initialVelocityOffset ?? DVector3.zero,
                initialPositionOffset ?? DVector3.zero);

            var unit = new BoardingUnit
            {
                id = id,
                type = VesselType.Shuttle,
                groupId = group.GroupId,
                entityId = entity.EntityId,
                targetGroupId = targetGroupId,
                targetPartId = targetPartId,
                landingOffsetShip = landingOffsetShip,
                shuttleId = shuttleId,
                state = BoardingState.Flying
            };
            foreach (var p in passengers)
                if (p != null && !p.Destroyed)
                    unit.members.Add(new BoardingMember { pawn = p });
            units.Add(unit);
            TotalLaunched++;

            Log.Message(string.Format("[Boarding] 穿梭机起飞 Unit {0} Shuttle{1} 乘客 {2} 名 → 站 G{3} 落点({4:F0},{5:F1},{6:F0})m",
                id, shuttleId, unit.members.Count, targetGroupId,
                landingOffsetShip.x, landingOffsetShip.y, landingOffsetShip.z));
            return unit;
        }

        // ════════════════════════════════════════════
        //  降落（EVA Spawn pawn / 穿梭机停靠 Thing + 乘客下船）
        // ════════════════════════════════════════════

        private void Land(BoardingUnit unit, SpaceEntityGroup target)
        {
            var spaceGame = SpaceGame;
            if (spaceGame == null) return;

            // 目标空间站内部 Map
            var targetEntity = ResolveTargetEntity(unit);
            Map targetMap = targetEntity != null ? SpaceMapGenerator.GetEntityMap(targetEntity) : null;
            if (targetMap == null)
            {
                Log.Message(string.Format("[Boarding] Unit {0} 目标空间站内部 Map 不可用，降落失败", unit.id));
                KillMembers(unit, "目标站内部 Map 不可用");
                spaceGame.DestroyGroup(unit.groupId); // 同步销毁临时 3D 实体，防孤儿幽灵船
                unit.state = BoardingState.Destroyed;
                return;
            }

            IntVec3 cell = MapCoordMapper.ShipToMapCell(targetMap, (Vector3)unit.landingOffsetShip);

            if (unit.type == VesselType.Shuttle)
            {
                // 生成停靠 Thing（继承 ShuttleId）
                var def = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_Shuttle");
                if (def == null)
                {
                    Log.Error("[Boarding] PF_Building_Shuttle Def 缺失，降落失败");
                    KillMembers(unit, "穿梭机 Def 缺失");
                    spaceGame.DestroyGroup(unit.groupId); // 同步销毁临时 3D 实体，防孤儿幽灵船
                    unit.state = BoardingState.Destroyed;
                    return;
                }
                // 占地足迹钳制：ShipToMapCell 只收敛单格，3x3 足迹需整体不出界
                IntVec2 foot = def.size;
                int fx = Mathf.Clamp(cell.x, 0, targetMap.Size.x - foot.x);
                int fz = Mathf.Clamp(cell.z, 0, targetMap.Size.z - foot.z);
                IntVec3 footCell = new IntVec3(fx, 0, fz);

                var thing = ThingMaker.MakeThing(def);
                GenSpawn.Spawn(thing, footCell, targetMap, Rot4.North, WipeMode.Vanish, false);
                var dock = thing.TryGetComp<CompShuttleDock>();
                if (dock != null)
                    dock.shuttleId = unit.shuttleId;
                Log.Message(string.Format("[Boarding] 穿梭机 Unit {0} 降落 → 站 Map {1} Shuttle{2} 停靠格 {3}",
                    unit.id, targetMap.uniqueID, unit.shuttleId, footCell));
            }

            // 乘客/单兵下船：Spawn 到落点附近可站立格（落点可能点在建筑上，统一校验）
            foreach (var m in unit.members)
            {
                if (m.pawn == null || m.pawn.Destroyed) continue;
                IntVec3 spawnCell = FindWalkableNear(targetMap, cell);
                GenSpawn.Spawn(m.pawn, spawnCell, targetMap, Rot4.North, WipeMode.Vanish, false);
                Log.Message(string.Format("[Boarding] 单位 {0} 成员 {1} 已登陆站 Map {2} 格 {3}",
                    unit.id, m.pawn.LabelShort, targetMap.uniqueID, spawnCell));
            }

            // 销毁临时 3D 实体/group，注销飞行记录
            spaceGame.DestroyGroup(unit.groupId);
            unit.state = BoardingState.Landed;
            TotalLanded++;
            RecordResult(unit, BoardingState.Landed, "着陆完成");
            Log.Message(string.Format("[Boarding] Unit {0} ({1}) 着陆完成，已销毁 3D 实体，成员转入目标站 Map",
                unit.id, unit.type));
        }

        // ════════════════════════════════════════════
        //  碰撞分支（CombatManager 唯一耦合点）
        // ════════════════════════════════════════════

        /// <summary>
        /// 接舷接触消费：a/b 中一方是跳帮单位且相对速度低于接舷阈值、且已对准落点 →
        /// 返回 true（消费碰撞，不弹飞不爆炸，降落判定由 Tick 驱动）。
        /// 高速（≥ ContactConsumeRelVel）→ false，走正常船-船碰撞爆炸。
        /// </summary>
        public bool TryResolveBoardingContact(SpaceEntityGroup a, SpaceEntityGroup b, double vRel)
        {
            if (vRel >= ContactConsumeRelVel) return false;

            BoardingUnit unit = FindUnitByGroup(a.GroupId);
            SpaceEntityGroup unitGroup = a;
            if (unit == null)
            {
                unit = FindUnitByGroup(b.GroupId);
                unitGroup = b;
            }
            if (unit == null || unit.state != BoardingState.Flying || unitGroup == null) return false;

            // 对方必须是本单位的目标空间站：撞到第三方/友军不消费，走正常碰撞
            int otherGroupId = a.GroupId == unitGroup.GroupId ? b.GroupId : a.GroupId;
            if (otherGroupId != unit.targetGroupId) return false;

            // 已对准落点（距移动落点 < 阈值）→ 消费
            double dist = (GetGroupPos(unitGroup) - GetAimPointSystem(unit)).magnitude;
            bool consumed = dist < LandingDistanceThreshold;
            if (consumed)
                Log.Message(string.Format("[Boarding] 接舷消费 G{0}(Unit{1}) vs G{2} vRel={3:F1}m/s dist落点={4:F0}m",
                    unitGroup.GroupId, unit.id, a.GroupId == unitGroup.GroupId ? b.GroupId : a.GroupId, vRel, dist));
            return consumed;
        }

        public BoardingUnit FindUnitByGroup(int groupId)
        {
            for (int i = 0; i < units.Count; i++)
                if (units[i].groupId == groupId)
                    return units[i];
            return null;
        }

        // ════════════════════════════════════════════
        //  状态查询（测试面板 / 探针共用）
        // ════════════════════════════════════════════

        /// <summary>跳帮单位实时状态快照（测试面板 / 探针日志用）。</summary>
        public struct BoardingUnitStatus
        {
            public bool resolved;       // group/entity 解析成功
            public double distToAim;    // 距移动落点（m）
            public double relVel;       // 相对目标空间站速度（m/s）
            public bool guidanceActive; // 接近引导是否激活
            public bool partDestroyed;  // part 是否已被击毁
            public DVector3 unitPos;    // 单位位置（世界，m）
            public DVector3 aimPoint;   // 移动落点（世界，m）
        }

        /// <summary>单位实时状态（懒解析失败时 resolved=false，其余字段无效）。</summary>
        public BoardingUnitStatus GetStatus(BoardingUnit unit)
        {
            var s = new BoardingUnitStatus();
            if (!ResolveUnit(unit)) return s; // 先尝试懒解析，避免探针/面板拿到全默认假数据
            var group = unit.ResolvedGroup;
            var target = ResolveTargetGroup(unit);

            s.resolved = true;
            s.unitPos = GetGroupPos(group);
            s.aimPoint = GetAimPointSystem(unit);
            s.distToAim = (s.aimPoint - s.unitPos).magnitude;
            if (target != null)
                s.relVel = (GetGroupVelocity(group) - GetGroupVelocity(target)).magnitude;
            s.guidanceActive = group.HasApproachGuidance;
            foreach (var part in unit.ResolvedEntity.Parts)
                if (part.isDestroyed) s.partDestroyed = true;
            return s;
        }

        // ════════════════════════════════════════════
        //  内部辅助
        // ════════════════════════════════════════════

        /// <summary>pawn/thing 所在 Map → 母船（ownerEntity + 其 group）。</summary>
        private (SpaceEntity entity, SpaceEntityGroup group) ResolveMothership(Map map)
        {
            var spaceGame = SpaceGame;
            if (spaceGame == null || map == null) return (null, null);
            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp == null || mapComp.ownerEntityId < 0) return (null, null);
            var entity = spaceGame.GetEntityById(mapComp.ownerEntityId);
            if (entity == null) return (null, null);
            var group = spaceGame.GetGroupById(entity.GroupId);
            return (entity, group);
        }

        private SpaceEntityGroup ResolveTargetGroup(BoardingUnit unit)
        {
            return SpaceGame?.GetGroupById(unit.targetGroupId);
        }

        private SpaceEntity ResolveTargetEntity(BoardingUnit unit)
        {
            var target = ResolveTargetGroup(unit);
            if (target == null) return null;
            foreach (int eid in target.GetEntityIds())
            {
                var e = SpaceGame?.GetEntityById(eid);
                if (e != null) return e;
            }
            return null;
        }

        /// <summary>移动落点（世界，米）= 目标 group 位置 + 实体偏移 + 姿态旋转(落点局部偏移)。</summary>
        private DVector3 GetAimPointSystem(BoardingUnit unit)
        {
            var target = ResolveTargetGroup(unit);
            if (target == null) return DVector3.zero;
            DVector3 groupPos = GetGroupPos(target);
            var targetEntity = ResolveTargetEntity(unit);
            if (targetEntity == null) return groupPos + unit.landingOffsetShip;
            return BoardingMath.AimPointSystem(groupPos, targetEntity.Position, targetEntity.Rotation, unit.landingOffsetShip);
        }

        /// <summary>与 CombatManager.GetGroupPos 同基准：圆轨解析位置 / 非圆轨 CurrentOrbit.positionSystem。</summary>
        private static DVector3 GetGroupPos(SpaceEntityGroup group)
        {
            if (group.IsCircularOrbit)
                return group.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return group.CurrentOrbit.positionSystem.v;
        }

        private static DVector3 GetGroupVelocity(SpaceEntityGroup group)
        {
            if (group.IsCircularOrbit)
                return group.GetCircularVelocityAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return group.CurrentOrbit.velocitySystem.v;
        }

        /// <summary>
        /// 创建临时 3D 实体（无 Map 纯物理）：初始位置/速度 = 母船 group（共轨出发），
        /// 参考系 = 母船参考系（与目标空间站同天体）。
        /// </summary>
        private (SpaceEntity entity, SpaceEntityGroup group) CreateTransitVehicle(int unitId, VesselType type,
            int factionId, double size, float hp, float dryMass, List<PropulsionData> propulsion,
            SpaceEntityGroup originGroup, int celestialBodyId, DVector3 velocityOffset = default,
            DVector3 positionOffset = default)
        {
            var spaceGame = SpaceGame;
            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();

            DVector3 pos = GetGroupPos(originGroup) + positionOffset;
            DVector3 vel = GetGroupVelocity(originGroup) + velocityOffset;

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsActive = true,
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = celestialBodyId
            };
            group.CurrentOrbit = new OrbitState { positionSystem = (SystemVec3)pos, velocitySystem = (SystemVec3)vel, rotation = Quaternion.identity };
            spaceGame.RegisterGroup(group);

            var entity = new SpaceEntity(entityId, factionId)
            {
                HasMap = false,
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true
            };
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);   // 不设就留 (0,0,0,0)：物理端恰好等价单位姿态，但已不是合法四元数

            var part = new SpacePart
            {
                partId = 1,
                defName = type == VesselType.Eva ? "PF_ShipPart_Eva" : "PF_ShipPart_Shuttle",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(size, size, size),
                hp = hp,
                isIndestructible = false
            };
            if (dryMass > 0f)
                part.AddComponent(new HullMassComponent { mass = dryMass });
            // [V7] 跳帮载具的短程传感器（300 km）——它对 300 m 级靠帮精度够用，但不给远距探测
            part.AddComponent(new SensorArrayComponentData { passiveRangeMeters = 300_000f });
            entity.SetParts(new List<SpacePart> { part });
            foreach (var p in propulsion)
                entity.AddPropulsion(p);

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);
            return (entity, group);
        }

        /// <summary>EVA 背包推进：小 ve/ṁ，少量工质。</summary>
        private static List<PropulsionData> EvaPropulsion()
        {
            var prop = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrogen");
            return new List<PropulsionData>
            {
                new PropulsionData { propellant = prop, ve = 2200f, massFlow = 1.5f, mass = 300f, capacity = 300f }
            };
        }

        /// <summary>穿梭机推进：强推进，独立机动。</summary>
        private static List<PropulsionData> ShuttlePropulsion()
        {
            var prop = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrogen");
            return new List<PropulsionData>
            {
                new PropulsionData { propellant = prop, ve = 3500f, massFlow = 40f, mass = 8000f, capacity = 8000f }
            };
        }

        /// <summary>成员全部死亡（击毁陪葬 / 任务失败）。不销毁 pawn，用 Kill 结算。</summary>
        private void KillMembers(BoardingUnit unit, string cause)
        {
            TotalDestroyed++;
            RecordResult(unit, BoardingState.Destroyed, cause);
            foreach (var m in unit.members)
            {
                if (m.pawn == null || m.pawn.Destroyed) continue;
                if (m.pawn.Spawned)
                    m.pawn.DeSpawn();
                m.pawn.Kill(new DamageInfo(DamageDefOf.Crush, 99999f));
                Log.Message(string.Format("[Boarding] 成员 {0} 死亡（原因：{1}）", m.pawn.LabelShort, cause));
            }
            unit.members.Clear();
        }

        /// <summary>在格附近寻找最近可站立格（穿梭机乘客下船分散用）。</summary>
        private static IntVec3 FindWalkableNear(Map map, IntVec3 cell)
        {
            if (cell.Walkable(map)) return cell;
            for (int radius = 1; radius <= 8; radius++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dz * dz > radius * radius + 1) continue; // 环形
                        IntVec3 c = new IntVec3(cell.x + dx, 0, cell.z + dz);
                        if (c.InBounds(map) && c.Walkable(map))
                            return c;
                    }
                }
            }
            return cell;
        }

        // ════════════════════════════════════════════
        //  序列化
        // ════════════════════════════════════════════

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nextBoardingId, "nextBoardingId", 1);
            Scribe_Values.Look(ref nextShuttleId, "nextShuttleId", 1);
            Scribe_Collections.Look(ref units, "units", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && units == null)
                units = new List<BoardingUnit>();
        }
    }

    /// <summary>
    /// 跳帮纯逻辑（静态，便于单元测试）：落点世界坐标换算 + 降落窗口判定。
    /// </summary>
    public static class BoardingMath
    {
        /// <summary>
        /// 世界落点 = 目标 group 位置 + 目标实体偏移 + 姿态旋转(落点局部偏移)。
        /// 落点局部偏移在目标空间站局部系（格 → 米，y=0）；空间站旋转时随姿态换算。
        /// </summary>
        public static DVector3 AimPointSystem(DVector3 groupPos, DVector3 entityPosition,
            UnityEngine.Quaternion rotation, DVector3 offsetShip)
        {
            return groupPos + entityPosition + DVector3.Rotate(offsetShip, rotation);
        }

        /// <summary>降落窗口判定：距移动落点 &lt; 阈值 且 相对速度 &lt; 阈值（低速接舷）。</summary>
        public static bool InLandingWindow(double distToAim, double relVel,
            double distThreshold, double relVelThreshold)
        {
            return distToAim < distThreshold && relVel < relVelThreshold;
        }
    }
}
