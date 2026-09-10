using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimWorld.Planet;
using RimWorld;
using System.Reflection;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    public class SpaceGameComp : GameComponent
    {
        // ── 轨道实体/群组 ──
        private Dictionary<int, SpaceEntity> entities = new Dictionary<int, SpaceEntity>();
        private Dictionary<int, SpaceEntityGroup> groups = new Dictionary<int, SpaceEntityGroup>();

        // ── 参考系网格（根行星/恒星ID → 粗网格）──
        private Dictionary<int, SpatialGrid3D<ISpatialEntity>> planetGrids = new Dictionary<int, SpatialGrid3D<ISpatialEntity>>();

        /// <summary>
        /// 粗网格中 group 上次同步的位置（每 2 tick SyncGroupToCoarseGrid 维护）。
        /// 圆轨船位置是解析函数（GetCircularPositionAtTick），粗网格若停在入轨点会永久过期，
        /// 定向能 raycast 的 8 大区块粗筛就会漏检已绕出去的船 → 必须定期同步保鲜。
        /// </summary>
        private Dictionary<int, DVector3> groupLastCoarsePos = new Dictionary<int, DVector3>();

        private Dictionary<int, object> mapComponents = new Dictionary<int, object>();

        // 时间规则：1 tick = 1 秒，每 2 tick 更新一次物理
        internal const int UPDATE_INTERVAL_TICKS = 2;
        private int lastUpdateTick = 0;

        // PERF-5：物理并行下限——编组数低于此值时顺序执行（线程调度 + 并发容器分配 > 顺序开销）
        private const int PARALLEL_PHYSICS_MIN_GROUPS = 32;

        // ── 每物理步 scratch 复用（2026-09-10 性能第 2 条：消除每步固定容器分配）──
        // 仅主线程持有与消费；物理并行区只按索引写 physicsResultsScratch（组数与顺序步内稳定）。
        private readonly List<SpaceEntityGroup> movableGroupsScratch = new List<SpaceEntityGroup>();
        private GroupPhysicsResult[] physicsResultsScratch = new GroupPhysicsResult[0];
        private readonly List<int> groupsToCheckScratch = new List<int>();
        private readonly List<ThrustSlice> steeringSliceScratch = new List<ThrustSlice>();

        // ── 组件引用缓存（2026-09-10 性能第 5 条）：GameComponentTick / 每物理步都要查的组件 ──
        private readonly GameComponentRef<TimeScaleManager> timeScaleRef = new GameComponentRef<TimeScaleManager>();
        private readonly GameComponentRef<CombatManager> combatMgrRef = new GameComponentRef<CombatManager>();
        private readonly GameComponentRef<AutonomousVehicleManager> avmRef = new GameComponentRef<AutonomousVehicleManager>();
        private readonly GameComponentRef<CelestialBodyManager> celestialRef = new GameComponentRef<CelestialBodyManager>();

        /// <summary>
        /// 方向感知推进的默认矢量锥半角余弦（cos 10° ≈ 0.9848）。
        /// burn / 手动平移只使用「轴向世界方向与目标方向夹角 ≤ 锥角」的引擎。
        /// </summary>
        public const float DefaultThrustConeCos = 0.984807753f;
        /// <summary>姿态"对准完成"阈值（≈1.5° 死区）：主推轴收心到正中才点火/消旋（2026-09-06 收心语义）。
        /// 与 DefaultThrustConeCos（10° 引擎允许窗口）区分：窗口内可出力、收心到死区才点火。</summary>
        public const float DefaultThrustAlignCos = 0.999657325f;

        /// <summary>操舵/自动对准详细日志开关（验证用，默认关；Debug 菜单「PFL 测试」页签切换）。</summary>
        public static bool DebugDetailedSteering = false;
        /// <summary>无 Map 主推方向型机动详细日志（D15 新增改动验证）：每物理步输出网选择/对准角/RCS/燃料/燃尽抑制。</summary>
        public static bool DebugDetailedThrust = false;

        // 自动对准 / 手动操舵详细日志状态（主线程）
        private readonly Dictionary<int, bool> alignStateByGroup = new Dictionary<int, bool>();
        private int nextAlignLogTick = 0;
        private int nextSteerLogTick = 0;

        /// <summary>机动预点火窗口（physicsTick 秒）：进入节点前提前这么久开始处理机动
        /// （估燃烧时长 → 等待点火时刻），需覆盖最长燃烧的半程，保证"燃烧居中"生效。</summary>
        private const double ManeuverPreBurnLookaheadTicks = 600;

        /// <summary>交会引导：修正烧死区（修正量低于此不点火，m/s；终端消速用）。</summary>
        private const double GuidanceBurnDeadband = 2.0;
        /// <summary>交会引导：进入终端模式（消相对速度）的剩余时间窗口（physicsTick 秒）。
        /// Lambert 在 tof→0 时数值不稳，接近终点改用直接消相对速度；到达点过后立即结束引导。</summary>
        private const double GuidanceTerminalWindow = 60.0;
        /// <summary>交会引导：巡航检查间隔（physicsTick 秒）。未到间隔不重算（省算力、防高频点火）。</summary>
        private const double GuidanceCheckInterval = 30.0;
        /// <summary>交会引导：终端闭环反馈间隔（physicsTick 秒）。消速段更密的重算（needDv = 目标速度−当前速度），
        /// 逐步收敛、达到死区即停，避免一次巨烧过量。</summary>
        private const double GuidanceTerminalCheckInterval = 10.0;
        /// <summary>交会引导：修正烧后冷却（physicsTick 秒）。让轨迹稳定再评估，避免追逐残差。</summary>
        private const double GuidanceBurnCooldown = 60.0;
        /// <summary>交会引导：预测脱靶量门控（米）。传播当前状态到到达时刻，脱靶低于此视为 on-course（零烧）。</summary>
        private const double GuidanceMissThreshold = 2000.0; // 2 km

        /// <summary>接近引导：限速（m/s）。动态到达时刻 tof = dist / v_close，保证修正量有界、轨道扰动小。</summary>
        private const double ApproachMaxClosingSpeed = 50.0;
        /// <summary>接近引导：终端收拢时间常数 τ（s）。消相对速度的同时以 ≤ dist/τ 温柔贴近（仅近距离生效）。</summary>
        private const double ApproachTimeConstant = 300.0;
        /// <summary>接近引导：进入终端收拢的距离阈值（m）。低于此距离不再切回 Lambert 巡航，
        /// 避免 tof=dist/限速 在终端窗口边界反复横跳造成极限环振荡。</summary>
        private const double ApproachTerminalDistance = 5000.0; // 5 km
        /// <summary>接近引导：完成判定——相对距离（m）。</summary>
        private const double ApproachCompletionDistance = 300.0;
        /// <summary>接近引导：完成判定——相对速度（m/s，取 ≥ 引导死区以保证可达成）。</summary>
        private const double ApproachCompletionRelVel = 2.0;
        /// <summary>接近引导：允许启动的最大相对距离（m，太远建议用精确交会）。</summary>
        public const double ApproachMaxStartDistance = 1000000.0; // 1000 km

        // ── 地图封存/取档异步操作 ──
        public MapRestoreOperation ActiveRestoreOperation { get; private set; }
        public MapArchiveOperation ActiveArchiveOperation { get; private set; }

        // ── 时间流速管理 ──
        /// <summary>用户期望的时间流速（不受机动自动降速影响）。</summary>
        public TimeScaleLevel DesiredTimeScale { get; set; } = TimeScaleLevel.Normal;

        /// <summary>机动追踪表：groupId → 机动状态。半事件驱动，每16 display-tick检查。</summary>
        private struct ManeuverTracker
        {
            public double ExecutePhysicsTick;   // 机动执行的 physicsTick
            public bool IsActive;               // 是否已开始推进
        }
        private Dictionary<int, ManeuverTracker> trackedManeuvers = new();
        /// <summary>上次流速检查时的 physicsTick，每 16tick 检查一次。</summary>
        private int lastManeuverCheckRealTick = -240;

        public SpaceGameComp(Game game) { }

        #region 地图控件
        public void EnterHiddenMap(int mapId)
        {
            var map = SpaceMapGenerator.FindPocketMap(mapId);
            SpaceMapGenerator.EnterPocketMap(map);
        }
        #endregion

        // ── 网格路由辅助 ──

        /// <summary>获取指定 Group 所属参考系的粗网格。</summary>
        private SpatialGrid3D<ISpatialEntity> GetGridForGroup(int groupId)
        {
            if (groups.TryGetValue(groupId, out var group) && group.CelestialBodyId >= 0)
            {
                // 地表锚点（星座成员载体）不进任何空间索引：粗网格的唯一消费者是
                // 定向能粗筛 / TargetingSystem.GetGroupsInRange 的候选预过滤，锚点没有参与资格。
                // 在本方法短路 = RegisterGroup / SyncGroupToCoarseGrid / DestroyGroup 三处一并覆盖。
                if (group.IsSurfaceAnchor) return null;
                var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (mgr != null)
                {
                    int frameId = mgr.GetReferenceFrameId(group.CelestialBodyId);
                    if (frameId >= 0)
                    {
                        if (!planetGrids.TryGetValue(frameId, out var pg))
                        {
                            pg = new SpatialGrid3D<ISpatialEntity>();
                            planetGrids[frameId] = pg;
                        }
                        return pg;
                    }
                }
                return null;
            }
            return null;
        }

        /// <summary>获取所有参考系网格。</summary>
        private IEnumerable<SpatialGrid3D<ISpatialEntity>> GetAllGrids()
        {
            foreach (var pg in planetGrids.Values)
                yield return pg;
        }

        /// <summary>
        /// 粗网格位置同步（定向能 raycast 8 大区块粗筛的保鲜前提）：
        /// 用新鲜位置更新 group 在粗网格的区块。大区块 1000km、2 tick 移动 ~5km，
        /// 跨格极罕见，Update 内部新旧格相同 O(1) 返回，成本可忽略。
        /// 统一入口：RegisterGroup（初始）/ UpdateGroupOrbit（轨道变更）/ SyncAllShipsToGrid（每 2 tick）都走这里，
        /// 保证 groupLastCoarsePos 缓存与粗网格实际格坐标一致，不会用错误的旧位置移除导致泄漏。
        /// </summary>
        public void SyncGroupToCoarseGrid(int groupId, DVector3 newPos)
        {
            if (!groups.TryGetValue(groupId, out var group)) return;
            var targetGrid = GetGridForGroup(groupId);
            if (targetGrid == null) return;

            if (groupLastCoarsePos.TryGetValue(groupId, out var last))
            {
                targetGrid.Update(group, last, newPos);
            }
            else
            {
                // 防御分支：正常流程 RegisterGroup 已初始化缓存，这里仅兜底
                targetGrid.Add(group, newPos);
            }
            groupLastCoarsePos[groupId] = newPos;
        }

        /// <summary>获取参考系网格字典（只读），外部用。</summary>
        public IReadOnlyDictionary<int, SpatialGrid3D<ISpatialEntity>> GetPlanetGrids() => planetGrids;

        /// <summary>计算参考系 ID：行星/卫星群组 → 根行星 ID，恒星群组 → 恒星 ID，深空 → -1。</summary>
        private int GetReferenceFrameId(int celestialBodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            return mgr?.GetReferenceFrameId(celestialBodyId) ?? -1;
        }

        /// <summary>公开的参考系 ID 查询，供 CombatManager 等外部使用。</summary>
        public int ResolveReferenceFrameId(int celestialBodyId)
        {
            return GetReferenceFrameId(celestialBodyId);
        }

        /// <summary>将射弹注册到粗网格。</summary>
        public void RegisterProjectileToGrid(ProjectileInstance proj)
        {
            int frameId = GetReferenceFrameId(proj.systemBodyId);
            if (frameId >= 0)
            {
                if (!planetGrids.TryGetValue(frameId, out var grid))
                {
                    grid = new SpatialGrid3D<ISpatialEntity>();
                    planetGrids[frameId] = grid;
                }
                grid.Add(proj, proj.Position);
            }
        }

        /// <summary>从粗网格移除射弹。</summary>
        public void UnregisterProjectileFromGrid(ProjectileInstance proj)
        {
            int frameId = GetReferenceFrameId(proj.systemBodyId);
            if (frameId >= 0 && planetGrids.TryGetValue(frameId, out var grid))
                grid.Remove(proj);
        }

        #region 注册/注销

        public void RegisterEntity(SpaceEntity entity)
        {
            if (entity == null) return;
            if (entities.ContainsKey(entity.EntityId))
            {
                Log.Warning(string.Format("SpaceEntity {0} already registered.", entity.EntityId));
                return;
            }

            if (entity.GroupId == 0)
            {
                Log.Error(string.Format("Entity {0} has GroupId 0. It must belong to a group before registration.", entity.EntityId));
                return;
            }

            if (!groups.TryGetValue(entity.GroupId, out var group))
            {
                Log.Error(string.Format("Group {0} not found for entity {1}. Registration failed.", entity.GroupId, entity.EntityId));
                return;
            }

            if (!group.ContainsEntity(entity.EntityId))
                group.AddEntity(entity.EntityId);

            entities[entity.EntityId] = entity;

            if (group.CelestialBodyId >= 0)
            {
                var celestialMgr = Current.Game.GetComponent<CelestialBodyManager>();
                if (celestialMgr != null)
                {
                    celestialMgr.RegisterEntityToBody(entity.EntityId, group.CelestialBodyId);
                    celestialMgr.RegisterGroupToBody(group.GroupId, group.CelestialBodyId);
                }
            }
        }

        public void UnregisterEntity(SpaceEntity entity)
        {
            if (entity == null) return;
            if (!entities.Remove(entity.EntityId)) return;

            var celestialMgr = Current.Game.GetComponent<CelestialBodyManager>();
            if (celestialMgr != null)
                celestialMgr.UnregisterEntityFromBody(entity.EntityId);
        }

        public void RegisterGroup(SpaceEntityGroup group)
        {
            if (group == null) return;
            if (groups.ContainsKey(group.GroupId))
            {
                Log.Warning(string.Format("SpaceEntityGroup {0} already registered.", group.GroupId));
                return;
            }
            groups[group.GroupId] = group;

            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            int frameId = mgr?.GetReferenceFrameId(group.CelestialBodyId) ?? -1;

            // 粗网格：注册群组（同步初始化位置缓存，保证 SyncGroupToCoarseGrid 走 Update 路径）
            var targetGrid = GetGridForGroup(group.GroupId);
            if (targetGrid != null)
            {
                targetGrid.Add(group, group.Position);
                groupLastCoarsePos[group.GroupId] = group.Position;
            }

            // 打印实际结果而非"registered"：地表锚点按设计不入索引（GetGridForGroup 短路），
            // 若这里仍报 registered，会与星座自检「空间索引隔离」的结论互相矛盾，误导排查。
            Log.Message(string.Format("[Grid] Group {0} registered: bodyId={1} frameId={2} 粗网格={3}",
                group.GroupId, group.CelestialBodyId, frameId,
                targetGrid != null ? "已入" : group.IsSurfaceAnchor ? "锚点不入" : "无参考系"));

            // 细网格：注册群组到战斗网格
            var combatGrid = Current.Game?.GetComponent<CombatGrid>();
            if (combatGrid != null && group.IsCollidable)
                combatGrid.AddShip(group);

            if (group.CelestialBodyId >= 0)
            {
                var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>(); // 与全库其它调用点同款判空
                if (celestialMgr != null)
                    celestialMgr.RegisterGroupToBody(group.GroupId, group.CelestialBodyId);
            }
        }

        public void UnregisterGroup(SpaceEntityGroup group)
        {
            if (group == null) return;
            if (!groups.Remove(group.GroupId)) return;

            var celestialMgr = Current.Game.GetComponent<CelestialBodyManager>();
            if (celestialMgr != null)
                celestialMgr.UnregisterGroupFromBody(group.GroupId);

            foreach (var entityId in group.GetEntityIds())
            {
                if (entities.TryGetValue(entityId, out var entity))
                {
                    entity.GroupId = 0;
                    if (celestialMgr != null)
                        celestialMgr.UnregisterEntityFromBody(entityId);
                }
            }
        }

        /// <summary>
        /// 群组销毁事件（唯一收束点：<see cref="DestroyGroup"/> 内 fire，与 OnGroupDestroyed→CombatManager 同点）。
        /// 卫星与地表锚点的销毁都汇聚到 DestroyGroup，订阅方（星座收尸等）不必到处挂点。
        /// **注意**：fire 时刻群组仍在 groups 字典中（注销是第 4 步）→ 订阅者可读 group 自身身份字段。
        /// </summary>
        public event Action<int> GroupDestroyed;

        /// <summary>
        /// 销毁群组：从粗细网格移除 → 清理战斗缓存 → 注销实体 → 注销群组。
        /// </summary>
        public void DestroyGroup(int groupId)
        {
            if (!groups.TryGetValue(groupId, out var group))
                return;

            // 1. 细网格移除（此时 group 状态仍有效）
            var combatGrid = Current.Game?.GetComponent<CombatGrid>();
            combatGrid?.RemoveShip(group);

            // 2. 战斗缓存清理
            var combatMgr = Current.Game?.GetComponent<CombatManager>();
            combatMgr?.OnGroupDestroyed(groupId);

            // 2.7 收尸事件（星座等组织层订阅）：在粗网格/实体注销之前 fire，
            //     此刻 group 仍在 groups 中，订阅者能读到 IsSurfaceAnchor / anchorWorldObjectId。
            GroupDestroyed?.Invoke(groupId);

            // 2.5 粗网格移除（此时 group 仍在 groups 字典中，GetGridForGroup 可用）
            var targetGrid = GetGridForGroup(groupId);
            if (targetGrid != null)
                targetGrid.Remove(group);
            groupLastCoarsePos.Remove(groupId);

            // 3. 注销实体
            foreach (int entityId in group.GetEntityIds().ToList())
            {
                if (entities.TryGetValue(entityId, out var entity))
                    UnregisterEntity(entity);
            }

            // 4. 注销群组
            UnregisterGroup(group);

            // 5. 网格清理
            foreach (var grid in GetAllGrids())
                grid.ApplyChanges();
        }

        public void RegisterMapComponent(int mapId, object mapComp) => mapComponents[mapId] = mapComp;
        public void UnregisterMapComponent(int mapId) => mapComponents.Remove(mapId);
        public T GetMapComponent<T>(int mapId) where T : class => mapComponents.TryGetValue(mapId, out var comp) ? comp as T : null;

        /// <summary>
        /// 为群组添加机动规划并记录到追踪表（供自动降速使用）。
        /// </summary>
        public void QueueGroupManeuver(int groupId, SpaceEntityGroup.PlannedManeuver maneuver)
        {
            if (groups.TryGetValue(groupId, out var group))
            {
                group.QueueManeuver(maneuver);
                trackedManeuvers[groupId] = new ManeuverTracker
                {
                    ExecutePhysicsTick = maneuver.executeTick,
                    IsActive = false
                };
            }
        }

        #endregion

        #region 空间站创建与管理

        /// <summary>分配下一个 group id（扫 max+1，与调试生成器同规则，保证全局唯一）。</summary>
        public int AllocGroupId()
        {
            int max = 0;
            foreach (var g in groups.Keys)
                if (g > max) max = g;
            return max + 1;
        }

        /// <summary>分配下一个 entity id（扫 max+1）。</summary>
        public int AllocEntityId()
        {
            int max = 0;
            foreach (var e in entities.Keys)
                if (e > max) max = e;
            return max + 1;
        }

        public SpaceEntity CreateStation(int entityId, int groupId, int factionId,
            DVector3 initialPosition, int mapWidth = 60, int mapHeight = 60, string stationName = null)
        {
            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = 1   // EARTH_BODY_ID，确保空间站放入地球参考系网格
            };
            group.CurrentOrbit = new OrbitState { position = initialPosition, velocity = DVector3.zero, rotation = Quaternion.identity };
            RegisterGroup(group);

            // 用 Part 尺寸创建实体：mapWidth × 50m(厚度) × mapHeight
            var entity = SpaceMapGenerator.CreateStationEntity(entityId, factionId,
                initialPosition, mapWidth, mapHeight);
            entity.GroupId = groupId;
            // entity.Position/Velocity 为相对 group 原点的局部偏移（实体在 group 原点 → zero）
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);

            // 生成地图并用双向链接绑定
            var map = SpaceMapGenerator.GeneratePocketMap(mapWidth, mapHeight, "TestStation_" + entityId);
            SpaceMapGenerator.LinkEntityToMap(entity, map);

            RegisterEntity(entity);
            group.AddEntity(entityId);

            // 语义确认日志：entity 局部偏移应为 zero（实体在 group 原点），AABB 中心 = groupPos
            Log.Message(string.Format(
                "[创建] 空间站 Entity {0} Group {1} groupPos=({2:F0},{3:F0},{4:F0})km entityLocal=({5:F0},{6:F0},{7:F0})km AABB=({8:F0}x{9:F0}x{10:F0})m",
                entity.EntityId, groupId,
                initialPosition.x / 1000.0, initialPosition.y / 1000.0, initialPosition.z / 1000.0,
                entity.Position.x / 1000.0, entity.Position.y / 1000.0, entity.Position.z / 1000.0,
                entity.BoundsSize.x, entity.BoundsSize.y, entity.BoundsSize.z));

            return entity;
        }

        #endregion

        #region 地图封存/取档

        /// <summary>开始封存操作（异步多 tick）。</summary>
        public bool BeginArchiveMap(SpaceEntity entity)
        {
            if (ActiveArchiveOperation != null)
            {
                Messages.Message("已有地图正在封存中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (ActiveRestoreOperation != null)
            {
                Messages.Message("已有地图正在取档中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            var map = SpaceMapGenerator.FindPocketMap(entity.MapId);
            if (map == null) return false;

            var op = MapArchiveManager.BeginArchiveMap(map, entity);
            if (op == null) return false;

            ActiveArchiveOperation = op;
            Messages.Message("开始封存地图...", MessageTypeDefOf.SilentInput, false);
            return true;
        }

        /// <summary>开始取档操作（异步多 tick）。</summary>
        public bool BeginRestoreMap(SpaceEntity entity)
        {
            if (ActiveRestoreOperation != null)
            {
                Messages.Message("已有地图正在取档中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (ActiveArchiveOperation != null)
            {
                Messages.Message("已有地图正在封存中，请等待完成。", MessageTypeDefOf.RejectInput, false);
                return false;
            }

            var op = MapArchiveManager.BeginRestoreMap(entity);
            if (op == null) return false;

            ActiveRestoreOperation = op;
            Messages.Message("开始取出地图...", MessageTypeDefOf.SilentInput, false);
            return true;
        }

        /// <summary>每 tick 处理封存批次。</summary>
        private void ProcessArchiveOperation()
        {
            if (ActiveArchiveOperation == null) return;

            bool done = MapArchiveManager.ProcessArchiveBatch(
                ActiveArchiveOperation, MapArchiveOperation.BATCH_SIZE);

            if (done)
            {
                FinalizeArchiveOperation();
            }
        }

        /// <summary>每 tick 处理取档批次。</summary>
        private void ProcessRestoreOperation()
        {
            if (ActiveRestoreOperation == null) return;

            bool done = MapArchiveManager.ProcessBuildingBatch(
                ActiveRestoreOperation, MapRestoreOperation.BATCH_SIZE);

            if (done)
            {
                FinalizeRestoreOperation();
            }
        }

        private void FinalizeArchiveOperation()
        {
            var op = ActiveArchiveOperation;
            if (op == null) return;

            MapArchiveManager.FinalizeArchive(op);
            ActiveArchiveOperation = null;
        }

        private void FinalizeRestoreOperation()
        {
            var op = ActiveRestoreOperation;
            if (op == null) return;

            var entity = op.TargetEntity;
            entity.MapId = op.NewMap.uniqueID;
            entity.HasMap = true;
            entity.IsMapArchived = false;

            // 恢复双向映射
            SpaceMapGenerator.LinkEntityToMap(entity, op.NewMap);

            // 删除封存文件
            if (op.ArchiveFilePath != null && File.Exists(op.ArchiveFilePath))
                File.Delete(op.ArchiveFilePath);

            Log.Message(string.Format("[MapArchive] Restore complete: map {0} for entity {1}",
                op.NewMap.uniqueID, entity.EntityId));
            Messages.Message("地图取档完成。", MessageTypeDefOf.SilentInput, false);

            ActiveRestoreOperation = null;
        }

        /// <summary>取消正在进行的取档操作。</summary>
        public void CancelRestoreOperation()
        {
            if (ActiveRestoreOperation == null) return;

            // 销毁已创建的地图
            if (ActiveRestoreOperation.NewMap != null)
                PocketMapUtility.DestroyPocketMap(ActiveRestoreOperation.NewMap);

            ActiveRestoreOperation = null;
            Log.Message("[MapArchive] Restore operation cancelled.");
        }

        #endregion

        #region 查询

        public SpaceEntity GetEntityById(int id) => entities.TryGetValue(id, out var e) ? e : null;
        public SpaceEntityGroup GetGroupById(int id) => groups.TryGetValue(id, out var g) ? g : null;
        public IEnumerable<SpaceEntity> GetAllEntities() => entities.Values;
        public IEnumerable<SpaceEntityGroup> GetAllGroups() => groups.Values;

        /// <summary>
        /// 获取指定范围内所有空间群组（遍历所有参考系网格）。
        /// </summary>
        public IEnumerable<SpaceEntityGroup> GetGroupsInRange(DVector3 centerMeters, double radiusMeters, int factionFilter = -1, Func<SpaceEntityGroup, bool> extraFilter = null)
        {
            foreach (var pg in planetGrids.Values)
            {
                foreach (var entry in pg.GetNearby(centerMeters, radiusMeters))
                {
                    if (entry is SpaceEntityGroup group)
                    {
                        if (factionFilter != -1 && group.FactionId != factionFilter) continue;
                        if (extraFilter != null && !extraFilter(group)) continue;
                        yield return group;
                    }
                }
            }
        }

        #endregion

        #region 集群同步

        /// <summary>
        /// 更新集群轨道，同步所有成员实体位置，并更新粗网格。
        /// </summary>
        public void UpdateGroupOrbit(int groupId, OrbitState newOrbit)
        {
            if (!groups.TryGetValue(groupId, out var group))
                return;

            if (group.CurrentOrbit.position == newOrbit.position &&
                group.CurrentOrbit.velocity == newOrbit.velocity &&
                group.CurrentOrbit.rotation == newOrbit.rotation)
                return;

            group.CurrentOrbit = newOrbit;
            group.OrbitDirty = false;

            // 姿态同步：group 旋转 = 全实体旋转（单 part 化，实体无独立姿态）。
            // 碰撞 AABB（GetWorldAabb）与射弹/光束命中（局部系变换）都消费 entity.Rotation，
            // 必须跟随 group 姿态，否则船转了碰撞盒/命中判定不转。
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e != null && e.Rotation != newOrbit.rotation)
                    e.SetRotation(newOrbit.rotation);
            }

            // 更新粗网格中的群组位置（统一走 SyncGroupToCoarseGrid，与每 2 tick 同步共用位置追踪，
            // 缓存 last 即粗网格中 group 当前所在格的位置，不会用错误的旧位置移除导致泄漏）
            SyncGroupToCoarseGrid(groupId, newOrbit.position);

            // 注意：entity.Position 是 Entity 在 Group 内的局部偏移量，
            // Group 移动时实体偏移量不变，不要累加 deltaPos。
        }

        /// <summary>
        /// 直接设置实体的位置/速度/旋转。不涉及网格更新（实体不入网格）。
        /// </summary>
        public void MoveEntity(SpaceEntity entity, DVector3 newPositionMeters, DVector3 newVelocityMeters, Quaternion newRotation)
        {
            if (entity == null) return;
            entity.Position = newPositionMeters;
            entity.Velocity = newVelocityMeters;
            entity.Rotation = newRotation;

            if (entity.GroupId != 0 && groups.TryGetValue(entity.GroupId, out var group))
                group.OrbitDirty = true;
        }

        #endregion

        #region Tick 更新（物理积分）

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            // ── 地图封存/取档异步处理（每次 tick 推进一批）──
            if (ActiveRestoreOperation != null)
            {
                ProcessRestoreOperation();
            }
            if (ActiveArchiveOperation != null)
            {
                ProcessArchiveOperation();
            }

            var timeScaleManager = timeScaleRef.Get();
            int currentTick = Find.TickManager.TicksGame;
            
            if (currentTick - lastUpdateTick >= UPDATE_INTERVAL_TICKS)
            {
                lastUpdateTick = currentTick;
                UpdateAllMovements(timeScaleManager);
                // 每物理步消费者统一在推进完成后驱动（2026-09-07）：GameComponent tick 顺序按类型
                // 全名字母序（AutonomousVehicleManager → 本组件 → CombatManager），若制导/碰撞各自
                // %2 tick 会有一方读到推进前的旧位置 → 同 tick 位置错位 2s ≈ 5km（导弹制导报 0.7km
                // 命中、碰撞实测 2.6km 的日志实证）。此处推进完毕 = 所有消费者同 tick 同位。
                combatMgrRef.Get()?.OnPhysicsStepCompleted();
                avmRef.Get()?.OnPhysicsStepCompleted();
            }
            
            if (timeScaleManager != null && timeScaleManager.IsCombatMode)
            {
                UpdateCombatInterpolation(currentTick, timeScaleManager);
            }
            
            foreach (var pg in planetGrids.Values)
            {
                pg.ApplyChanges();
                // 粗网格空格回收（TTL 600 tick）：本方法此前**没有任何调用点**（同名 CombatGrid.DrainEmptyCells
                // 才有），于是空格与其 emptyQueue 条目永不回收——历史上被访问过的每个 1000km 格都常驻内存。
                // 2026-09-10 游戏内实证：空闲场景跑一次星座自检（造/销一枚圆轨探针）后，[网格状态] 恒为
                // 「网格数=1 格数=1 实体=0 空队=1」，tick 240/360/480 无回落。
                // 安全性：只回收"当前无实体"的格，查询侧（GetNearby→GetGroupsInRange）语义不变；
                // 被回收格的 LastCellCoord 只可能属于已 Remove 离开网格的实体，不会再走 Update。
                // 成本：每 tick 最多摊销 emptyQueue.Count/20（空队时零迭代）。
                pg.DrainEmptyCells();
            }

            // 空间站地图为 pocket map，Thing 的 CompTick 在隐藏时不可靠：
            // 统一在此驱动储罐差值同步（burn 扣向量后储罐本地逐 tick 追平）。
            foreach (var mc in mapComponents.Values)
            {
                if (mc is SpaceMapComp smc)
                    smc.TickPropellantTankSync();
            }

            // 游戏内架构验证测试（burn 差值同步/合冲量/推进器状态）——空闲时零开销
            BurnArchitectureTest.Tick();

            // 性能优化验收探针汇总（2026-09-10；开关关闭时仅一次静态 bool 读，内部限频）
            PerfProbe.MaybeLogSummary();

            // 游戏内一键验收状态机（未运行时立即返回，空闲零开销）
            PerfAcceptanceRunner.Tick();

            // 调试场景驱动（原 DebugShipSpawner.CompTick：场景自动下令 + 碰撞距离探针）
            DebugShipSpawner.Tick();
        }

        private struct GroupPhysicsResult
        {
            public int GroupId;
            public bool NeedsUpdate;
            public DVector3 NewPosition;
            public DVector3 NewVelocity;
            /// <summary>本步是否发生姿态变化（角速度积分产生新 rotation）。</summary>
            public bool HasRotationChange;
            /// <summary>姿态积分后的新 rotation（并行区只算不写，主线程统一施加）。</summary>
            public Quaternion NewRotation;
            public bool HasActiveBurn;
            public bool BurnExhausted;
            /// <summary>RCS 修正燃烧耗尽时不终结 burn（主网仍有料、可能仍在等对准），避免误终止（D15 弱 RCS）。</summary>
            public bool SuppressAutoBurnEnd;
            // ── D15 无 Map 主推/RCS 详细日志快照（并行区只写，主线程打日志）──
            public bool NoMapThrust;
            public bool UseMainSel;
            public bool UseRcsSel;
            public bool AligningMain;      // 主推型且本步未对准（主网等待）
            public bool RcsFallbackSel;    // 主推已对准但主网尽 → RCS 兜底
            public double MainDot;         // mainAxisWorld·burnDir（已归一；全向=1）
            public double AppliedDv;       // 本步实际 ΔV（m/s）
            /// <summary>本次 tick 应消耗的工质总量（kg），主线程按各网络切片流率比例分配扣除。</summary>
            public double PropellantConsumed;
            /// <summary>本次 burn 参与推进的方向切片（并行阶段构建的活动网络快照，含锥内推力/流率）。</summary>
            public List<ThrustSlice> ActiveSlices;
            public bool HasOrbitKeeping;
            public double OrbitKeepingDvCost;
            public DVector3 SyncBodyPos;
            public DVector3 SyncBodyVel;
        }

        private GroupPhysicsResult ComputeGroupPhysics(SpaceEntityGroup group, double dtSeconds, double physicsTick, CelestialBodyManager celestialMgr, int subSteps = 1)
        {
            var result = new GroupPhysicsResult { GroupId = group.GroupId };

            if (group.HasActiveBurn)
            {
                // 多工质网络并行烧：F = Σ(ve·ṁ)，a = F/m_now，各网络按 ṁ 比例消耗各自工质
                // （质量随工质消耗减小，加速度自然增大，无需对数记账）
                var burnSrc = GetGroupPropulsionSource(group);

                float fTotal = 0f, mfTotal = 0f;
                // 第 3 条：组级 scratch 复用（本组本步独占；下方非 Map 分支是 Add 累加，必须先清空）
                var activeSlices = group.thrustSliceScratch;
                activeSlices.Clear();
                // 探针：校验"每组独占一个 scratch 且身份恒定"（并行区两组共用会互相覆盖）
                if (PerfProbe.Enabled) PerfProbe.SliceBuffer(group.GroupId, activeSlices);

                if (burnSrc is SpaceMapComp mapComp)
                {
                    if (group.burnUseRcs && mapComp.HasRcs)
                    {
                        // ── RCS 全矢量（微调 burn）：任意方向出力，免对准/免锥 ──
                        // 交会/接近修正 burn 走此分支：无转向死区，修正即时。
                        mapComp.GetRcsThrustAlong(group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                        if (fTotal <= 0f)
                        {
                            // RCS 工质尽 → 回退主引擎（对准，现状）
                            mapComp.GetThrustAlong(group.burnDirection, group.CurrentOrbit.rotation, DefaultThrustConeCos,
                                group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                            AlignMainAxis(group, group.burnDirection, dtSeconds);
                            if (fTotal <= 0f && mapComp.TotalThrust <= 0f)
                                result.BurnExhausted = true; // 完全没有引擎 → 无法推进，终止
                        }
                        // RCS 有出力：不跑 AlignMainAxis（全矢量无需对准，船保持当前姿态）
                    }
                    else
                    {
                        if (!mapComp.HasDirectionalEngines)
                        {
                            // 纯全矢量船（仅虚拟推进/RCS，无 per-axis 引擎）：任意方向出力，免对准
                            mapComp.GetRcsThrustAlong(group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                            if (fTotal <= 0f && mapComp.TotalThrust <= 0f)
                                result.BurnExhausted = true; // 完全没有引擎 → 无法推进，终止
                        }
                        else
                        {
                            // ── 主引擎方向感知：矢量锥内轴向出力 + 自动对准兜底 ──
                            // GetThrustAlong 只取「轴向落在 burnDirection 矢量锥内」的引擎；
                            // 未对准（锥内无推力）→ 本步只旋转对准（Align 控制器），不推力不耗工质。
                            mapComp.GetThrustAlong(group.burnDirection, group.CurrentOrbit.rotation, DefaultThrustConeCos,
                                group.burnEnabledNetworks, activeSlices, out fTotal, out mfTotal);
                            // 已对准 → 消旋；未对准 → 转向（顺带零掉残余角速度）
                            AlignMainAxis(group, group.burnDirection, dtSeconds);
                            if (fTotal <= 0f && mapComp.TotalThrust <= 0f)
                                result.BurnExhausted = true; // 完全没有引擎 → 无法推进，终止
                        }
                    }
                }
                else
                {
                    // ── 无 Map 源：主推方向型（D15）+ 可选弱 RCS 副网，或纯全向 ──
                    // 主网（PropulsionData.isVectoredRcs=false）：主推型沿 mainAxisLocal 锥内出力；
                    // RCS 网（isVectoredRcs=true）：全矢量小推力——主推未对准时仍可出力修正、主网尽时兜底。
                    DVector3 mainAxisWorld = GetMainThrustWorldAxis(group);
                    bool isMainThrust = mainAxisWorld.sqrMagnitude > 1e-12;
                    double axisDot = 1.0;
                    if (isMainThrust)
                    {
                        // 姿态控制独立于燃料（魔动量轮不限料）：本步开头执行——
                        // 已收心（axis·burn ≥ cos~1.5°）→ 立即消旋，杜绝残余 ω 让弹体在主推燃烧时漂出；
                        // 未收心 → 全速转向（剩余夹角小于一步转角则降速收敛），直至收心正中才点火主网；
                        // 窗口内（锥内 10°）未收心时由 RCS/不点火继续修正对准。
                        axisDot = DVector3.Dot(mainAxisWorld.normalized, group.burnDirection.normalized);
                        AlignMainAxis(group, group.burnDirection, dtSeconds);
                    }
                    bool mainAligned = !isMainThrust || axisDot >= DefaultThrustAlignCos;

                    // pass1：各类网络可用性（启用 + ṁ>0 + 有料）+ RCS 存量/流率（预判本步是否耗尽）
                    bool hasMainFuel = false, hasRcsFuel = false;
                    double rcsAvailKg = 0.0, rcsFlowKgS = 0.0;
                    if (burnSrc != null)
                    {
                        foreach (var n in burnSrc.Networks)
                        {
                            if (n.MassFlow <= 0f) continue;
                            if (group.burnEnabledNetworks != null && !group.burnEnabledNetworks.Contains(n.propellant)) continue;
                            double avail = n.AvailableMass(burnSrc);
                            if (avail <= 0f) continue;
                            if (n.HasRcsEngines)
                            {
                                hasRcsFuel = true;
                                rcsAvailKg += avail;
                                rcsFlowKgS += n.MassFlow;
                            }
                            else hasMainFuel = true;
                        }
                    }
                    if (!hasMainFuel && !hasRcsFuel)
                    {
                        result.BurnExhausted = true; // 全工质尽 → 终止 burn
                    }
                    else
                    {
                        // 选网策略：
                        //  全向            → 主网 + RCS 全部出力；
                        //  主推已对准       → 主网出力（RCS 保留修正/兜底）；主网尽 → RCS 兜底；
                        //  主推未对准       → 主网等待（只转），RCS 小推力修正；无 RCS → 纯转向等对准。
                        bool useMain = false, useRcs = false;
                        if (!isMainThrust)
                        {
                            useMain = true;
                            useRcs = true;
                        }
                        else if (mainAligned)
                        {
                            if (hasMainFuel) useMain = true;
                            else useRcs = hasRcsFuel; // 主网尽 → RCS 兜底
                        }
                        else
                        {
                            useRcs = hasRcsFuel; // 未对准：主网等待（姿态已在上方 Align 转向）
                        }

                        // RCS 修正燃烧中若本步将耗尽、而主网仍有料 → 不终结 burn（主线程据 SuppressAutoBurnEnd 跳过 EndBurn），
                        // 让姿态继续转向直到对准主网接管；未到耗尽预判的普通修正步不刷 Suppress。
                        bool rcsMayExhaust = rcsFlowKgS > 0.0 && rcsAvailKg <= rcsFlowKgS * dtSeconds;
                        if (useRcs && !useMain && hasMainFuel && rcsMayExhaust)
                            result.SuppressAutoBurnEnd = true;

                        // ── D15 详细日志快照（主线程打 [Thrust]）──
                        result.NoMapThrust = true;
                        result.UseMainSel = useMain;
                        result.UseRcsSel = useRcs;
                        result.AligningMain = isMainThrust && !mainAligned;
                        result.RcsFallbackSel = isMainThrust && mainAligned && !hasMainFuel && useRcs;
                        result.MainDot = isMainThrust
                            ? Math.Max(-1.0, Math.Min(1.0, axisDot))
                            : 1.0;

                        if (burnSrc != null && (useMain || useRcs))
                        {
                            foreach (var n in burnSrc.Networks)
                            {
                                if (n.MassFlow <= 0f) continue;
                                if (group.burnEnabledNetworks != null && !group.burnEnabledNetworks.Contains(n.propellant)) continue;
                                if (n.AvailableMass(burnSrc) <= 0f) continue;
                                bool isRcs = n.HasRcsEngines;
                                if ((isRcs && !useRcs) || (!isRcs && !useMain)) continue;
                                activeSlices.Add(new ThrustSlice { network = n, thrust = n.Thrust, massFlow = n.MassFlow });
                                fTotal += n.Thrust;
                                mfTotal += n.MassFlow;
                            }
                        }
                        // 主推未对准且无 RCS（主网有料）：0 推力 0 耗料，burn 保持激活等对准（不标 exhausted）。
                        // RCS 修正/兜底燃烧时主网未动 → 主网不耗料；RCS 烧尽不误判整 burn 结束（主料仍可用）。
                    }
                }

                if (fTotal > 0f)
                {
                    double groupMass = GetGroupMass(group, burnSrc);
                    if (group.burnStartMass <= 0.0)
                        group.burnStartMass = groupMass; // burn 起始质量（实际 ΔV 评估基准）
                    double accel = fTotal / Math.Max(groupMass, 1.0); // m/s²
                    double dvThisTick = accel * dtSeconds;
                    double burnDv = Math.Min(dvThisTick, group.remainingBurnDV);
                    if (burnDv > 0.0)
                    {
                        result.HasActiveBurn = true;
                        result.BurnExhausted = (group.remainingBurnDV - burnDv) <= 1e-9;
                        // 总工质消耗：ṁ·dt 按实际 ΔV 折算（提前达成计划 ΔV 时按比例少烧）
                        result.PropellantConsumed = mfTotal * dtSeconds * (dvThisTick > 0.0 ? burnDv / dvThisTick : 0.0);
                        result.ActiveSlices = activeSlices; // 引用快照，主线程按切片流率比例分配扣除
                        if (PerfProbe.Enabled) // 探针：快照必须是本组自己的 scratch（否则并行区会被别的组改写）
                            PerfProbe.SliceAlias(ReferenceEquals(activeSlices, group.thrustSliceScratch), $"G{group.GroupId}");
                        // 冲量累积：并行区只写本 group 独占的累积器，主线程在下一物理步首统一施加
                        // （避免并行区直接改 CurrentOrbit.velocity，消除多冲量顺序依赖）
                        group.pendingImpulse += group.burnDirection * burnDv;
                        group.remainingBurnDV -= burnDv;
                        group.appliedBurnDV += burnDv; // 累计已施加 dV（诊断/日志）
                        result.AppliedDv = burnDv;
                    }
                    else
                    {
                        result.BurnExhausted = true;
                    }
                }
                else if (!result.BurnExhausted)
                {
                    // 自动对准中（Map 源，引擎存在但未对准）：保持 burn 激活，本步无推力无耗料，继续旋转
                    result.HasActiveBurn = true;
                }
            }

            // ── 预对准（前瞻）：coasting 段按待点火机动方向提前转向主推进轴 ──
            // 机动点火时已对准 → burn 准时居中（消除"先对准再烧"导致的初始 miss）。
            // 有 burn 时不干预（burn 内部自带的 AlignMainAxis 已接管）。
            if (!group.HasActiveBurn && group.pendingAlignDir.sqrMagnitude > 1e-12)
                AlignMainAxis(group, group.pendingAlignDir, dtSeconds);

            if (group.IsCircularOrbit)
            {
                DVector3 newPos = group.GetCircularPositionAtTick(physicsTick);
                // 圆轨下速度是随相位旋转的解析函数：同步 CurrentOrbit.velocity，
                // 使预览/机动规划/交会等所有读 velocity 的消费方得到当前相位正确值
                // （否则它们会拿到入轨瞬间的初始切线速度，导致轨道被算成漂移的椭圆）。
                DVector3 newVel = group.GetCircularVelocityAtTick(physicsTick);
                if (newPos != group.CurrentOrbit.position)
                {
                    result.NeedsUpdate = true;
                    result.NewPosition = newPos;
                    result.NewVelocity = newVel;
                }
            }
            else
            {
                var primaryBody = celestialMgr.GetBodyById(group.CelestialBodyId);
                if (primaryBody != null)
                {
                    DVector3 relPos, relVel;
                    if (primaryBody.bodyType != CelestialBodyType.Planet)
                    {
                        // 天体采样必须用 physicsTick（物理时间轴），与积分 dtSeconds / 圆轨解析 / 机动队列同基准。
                        // 之前用 realTick（Find.TickManager.TicksGame），Normal 档两者相等掩盖了问题，
                        // Fast/Combat 档倍率≠1 时近月参考系会系统性漂移。
                        DVector3 bodyPos = celestialMgr.GetBodyLocalPosition(primaryBody.bodyId, physicsTick);
                        DVector3 bodyVel = celestialMgr.GetBodyLocalVelocity(primaryBody.bodyId, physicsTick);
                        relPos = group.CurrentOrbit.position - bodyPos;
                        relVel = group.CurrentOrbit.velocity - bodyVel;
                    }
                    else
                    {
                        relPos = group.CurrentOrbit.position;
                        relVel = group.CurrentOrbit.velocity;
                    }

                    if (subSteps <= 1)
                    {
                        NBodyIntegrator.MultiBodyAccelJerk(
                            relPos, relVel,
                            physicsTick, primaryBody, celestialMgr,
                            group.orbitKeepingWhitelist,
                            out var acc, out var jerk);

                        NBodyIntegrator.ThirdOrderTaylorStep(
                            relPos, relVel,
                            acc, jerk, dtSeconds,
                            out var newPos, out var newVel);

                        if (primaryBody.bodyType != CelestialBodyType.Planet)
                        {
                            DVector3 bodyPosNow = celestialMgr.GetBodyLocalPosition(primaryBody.bodyId, physicsTick + dtSeconds);
                            DVector3 bodyVelNow = celestialMgr.GetBodyLocalVelocity(primaryBody.bodyId, physicsTick + dtSeconds);
                            newPos += bodyPosNow;
                            newVel += bodyVelNow;
                        }

                        result.NeedsUpdate = true;
                        result.NewPosition = newPos;
                        result.NewVelocity = newVel;
                    }
                    else
                    {
                        double subDt = dtSeconds / subSteps;
                        for (int i = 0; i < subSteps; i++)
                        {
                            NBodyIntegrator.MultiBodyAccelJerk(
                                relPos, relVel,
                                physicsTick, primaryBody, celestialMgr,
                                group.orbitKeepingWhitelist,
                                out var acc, out var jerk);

                            NBodyIntegrator.ThirdOrderTaylorStep(
                                relPos, relVel,
                                acc, jerk, subDt,
                                out relPos, out relVel);
                        }

                        if (primaryBody.bodyType != CelestialBodyType.Planet)
                        {
                            DVector3 bodyPosNow = celestialMgr.GetBodyLocalPosition(primaryBody.bodyId, physicsTick + dtSeconds);
                            DVector3 bodyVelNow = celestialMgr.GetBodyLocalVelocity(primaryBody.bodyId, physicsTick + dtSeconds);
                            relPos += bodyPosNow;
                            relVel += bodyVelNow;
                        }

                        result.NeedsUpdate = true;
                        result.NewPosition = relPos;
                        result.NewVelocity = relVel;
                    }
                }
            }

            if (group.HasOrbitKeeping)
            {
                result.HasOrbitKeeping = true;
                var primaryBody = celestialMgr.GetBodyById(group.CelestialBodyId);
                if (primaryBody != null)
                {
                    double dvRate = NBodyIntegrator.ComputeOrbitKeepingDvRate(
                        group.localPosition, group.localVelocity,
                        physicsTick, primaryBody, celestialMgr,
                        group.orbitKeepingWhitelist);
                    result.OrbitKeepingDvCost = dvRate * dtSeconds;
                }
            }

            // ── 姿态积分（魔法动量轮）──
            // 角速度 → rotation 增量；大步长（物理步 2s）下按角度限幅分子步，避免肉眼可见跳变。
            // 并行区只计算新姿态，主线程在结果应用时统一写 CurrentOrbit.rotation。
            if (group.angularVelocity.sqrMagnitude > 1e-12)
            {
                double w = group.angularVelocity.magnitude;
                DVector3 axis = group.angularVelocity / w;
                double totalAngle = w * dtSeconds;
                int steps = Math.Max(1, (int)Math.Ceiling(totalAngle / (10f * Mathf.Deg2Rad)));
                steps = Math.Min(steps, 32);
                DQuaternion rot = DQuaternion.FromQuaternion(group.CurrentOrbit.rotation);
                DQuaternion stepRot = DQuaternion.AngleAxis(totalAngle / steps, axis);
                for (int i = 0; i < steps; i++)
                    rot = stepRot * rot;
                result.HasRotationChange = true;
                result.NewRotation = rot.ToQuaternion();
                result.NeedsUpdate = true;
            }

            result.SyncBodyPos = GetBodyWorldOffset(group.CelestialBodyId, (int)System.Math.Round(physicsTick));
            result.SyncBodyVel = GetBodyWorldVelOffset(group.CelestialBodyId, (int)System.Math.Round(physicsTick));

            return result;
        }

        /// <summary>
        /// 主推轴世界方向（D15）：Map = mapComp.MainThrustAxis 随 rotation 旋转；
        /// 无 Map 主推方向型 = entity.mainAxisLocal 随 rotation 旋转；全向类（无主推轴）= zero。
        /// 并行区只读调用（与 GetGroupPropulsionSource 同纪律）。
        /// </summary>
        private DVector3 GetMainThrustWorldAxis(SpaceEntityGroup group)
        {
            var mapComp = group.GetLinkedMapComp();
            if (mapComp != null)
                return group.GetWorldAxis(mapComp.MainThrustAxis);
            // 无 Map：任一带主推轴的实体（导弹弹群成员同构同姿态；2026-09-08 思路 A——多实体组推进源
            // 是群级聚合器，不再 as SpaceEntity 取轴，改扫实体；单实体组结果与原来逐字节一致）
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e != null && e.HasMainAxis)
                    return DVector3.Rotate(e.mainAxisLocal.Value.normalized, group.CurrentOrbit.rotation);
            }
            return DVector3.zero;
        }

        /// <summary>
        /// 自动对准（SAS 兜底）：把主推进轴转到目标世界方向（收心正中，死区 ~1.5°），向量角速度（动量轮限速）。
        /// 已收心 → 零角速度（消旋）。未收心 → 按剩余夹角比例调速，一步转不完则满速。
        /// 主推轴来源：Map = mapComp.MainThrustAxis；无 Map 主推型 = entity.mainAxisLocal（D15）；
        /// 全向类（无主推轴）→ 直接返回（无对准语义）。
        /// 在并行物理区调用（只写本 group 独占的 angularVelocity，与 pendingImpulse 同纪律）。
        /// </summary>
        private void AlignMainAxis(SpaceEntityGroup group, DVector3 targetDir, double dtSeconds)
        {
            DVector3 mainAxisWorld = GetMainThrustWorldAxis(group);
            if (mainAxisWorld.sqrMagnitude < 1e-12)
                return; // 无主推轴（全向类）：无姿态对准
            DVector3 targetN = targetDir.normalized;
            if (targetN.sqrMagnitude < 1e-12)
            {
                group.angularVelocity = DVector3.zero;
                return;
            }
            double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(mainAxisWorld, targetN)));
            if (dot >= DefaultThrustAlignCos)
            {
                group.angularVelocity = DVector3.zero; // 已收心到正中（≤~1.5°）：消旋，防 burn 期间漂移
                return;
            }
            double error = Math.Acos(dot);
            // 最短旋转轴 = mainAxis × target（右手系）；反平行时叉积退化 → 任取垂直轴
            DVector3 rotAxis = DVector3.Cross(mainAxisWorld, targetN);
            if (rotAxis.sqrMagnitude < 1e-12)
                rotAxis = DVector3.Cross(mainAxisWorld, Math.Abs(mainAxisWorld.y) < 0.9 ? DVector3.up : DVector3.forward);
            DVector3 axisN = rotAxis.normalized;
            // 剩余夹角小于一步最大转角 → 降速收敛（避免越过目标震荡）
            double rate = Math.Min(group.MaxAngularRate, error / Math.Max(dtSeconds, 1e-9));
            group.angularVelocity = axisN * rate;
        }

        /// <summary>
        /// 手动操舵应用（每物理步）：平移冲量 + 旋转角速度 + SAS 对准 + 输入打断清队列（D10）。
        /// 在 UpdateAllMovements 步首调用（冲量随本步 ApplyPendingImpulses 立即生效）。
        /// 平移不走 burn 状态机（避免自动对准把船转向），方向感知推力逐引擎过滤动作组。
        /// </summary>
        private void ApplySteering(double dtSeconds)
        {
            if (!SteeringController.IsOpen) return;
            var group = GetGroupById(SteeringController.GroupId);
            if (group == null || !group.IsActive || !group.IsMovable)
            {
                SteeringController.Close();
                return;
            }
            var mapComp = group.GetLinkedMapComp();
            if (mapComp == null) return; // 无 Map 船暂不支持操舵

            bool anyInput = SteeringController.AnyTranslation || SteeringController.AnyRotation
                || SteeringController.SasKind != SasTargetKind.None;

            // D10：任何操舵输入 → 手动接管：中止 burn、清空机动队列与引导
            if (anyInput)
            {
                bool hadAuto = group.HasActiveBurn || group.plannedManeuvers.Count > 0 || group.HasRendezvousGuidance;
                if (group.HasActiveBurn)
                    group.EndBurn();
                if (group.plannedManeuvers.Count > 0 || group.HasRendezvousGuidance)
                {
                    group.ClearManeuvers();
                    trackedManeuvers.Remove(group.GroupId);
                }
                if (hadAuto)
                {
                    mapComp.SetThrustersBurning(false);
                    Log.Message(string.Format("[Steer] Group {0} 手动接管：清空机动队列/引导", group.GroupId));
                }
            }

            // ① 手动平移：方向感知直施冲量 + 扣工质
            DVector3 transDir = SteeringController.GetTranslationWorldDir(group);
            if (transDir.sqrMagnitude > 1e-9)
            {
                // 第 3 条：主线程 scratch 复用（本方法是单组逐步调用，无重入；显式清空不依赖被调方语义）
                var slices = steeringSliceScratch;
                slices.Clear();
                mapComp.GetManualThrustAlong(transDir, group.CurrentOrbit.rotation, DefaultThrustConeCos,
                    group.burnEnabledNetworks, mapComp.armedGroupsMask, slices, out float f, out float mf);
                bool burning = false;
                if (f > 0f && mf > 0f)
                {
                    double mass = GetGroupMass(group, mapComp);
                    double dv = f / Math.Max(mass, 1.0) * dtSeconds;
                    group.pendingImpulse += transDir.normalized * dv;
                    float mfTotal = 0f;
                    for (int i = 0; i < slices.Count; i++) mfTotal += slices[i].massFlow;
                    for (int i = 0; i < slices.Count; i++)
                    {
                        float share = mfTotal > 0f ? mf * (float)dtSeconds * (slices[i].massFlow / mfTotal) : 0f;
                        ConsumeGroupPropellant(group, slices[i].network.propellant, share);
                    }
                    burning = true;
                }
                mapComp.SetThrustersBurning(burning);
                if (DebugDetailedSteering && Find.TickManager.TicksGame >= nextSteerLogTick)
                {
                    DVector3 dirN = transDir.normalized;
                    Log.Message(string.Format("[Steer] Group {0} 平移 dir=({1:F2},{2:F2},{3:F2}) 推力={4:F1}kN 流率={5:F1}kg/s rcs={6} 实际推进={7}",
                        group.GroupId, dirN.x, dirN.y, dirN.z, f / 1000f, mf, mapComp.HasRcs, burning));
                    nextSteerLogTick = Find.TickManager.TicksGame + 10;
                }
            }
            else if (!SteeringController.AnyRotation && SteeringController.SasKind == SasTargetKind.None)
            {
                // 无任何操舵输入：熄火表现（清掉上一步手动平移留下的喷焰）
                mapComp.SetThrustersBurning(false);
            }

            // ② 手动旋转：动量轮直设角速度（无输入不动 angularVelocity → 自由自转）
            if (SteeringController.AnyRotation)
                group.angularVelocity = SteeringController.GetRotationWorldAngularVelocity(group);

            // ③ 手动 SAS：主推进轴对准目标（复用 AlignMainAxis；对准后消旋保持姿态）
            if (SteeringController.SasKind != SasTargetKind.None)
            {
                DVector3 target = SteeringController.GetSasTarget(group);
                if (target.sqrMagnitude > 1e-9)
                    AlignMainAxis(group, target, dtSeconds);
            }

            // 详细日志：旋转 / SAS（与平移共用节流）
            if (DebugDetailedSteering && Find.TickManager.TicksGame >= nextSteerLogTick)
            {
                if (SteeringController.AnyRotation)
                {
                    DVector3 w = group.angularVelocity;
                    Log.Message(string.Format("[Steer] Group {0} 旋转 ω=({1:F2},{2:F2},{3:F2})°/s 幅值={4:F2}°/s",
                        group.GroupId, w.x * Mathf.Rad2Deg, w.y * Mathf.Rad2Deg, w.z * Mathf.Rad2Deg,
                        (float)w.magnitude * Mathf.Rad2Deg));
                }
                if (SteeringController.SasKind != SasTargetKind.None)
                {
                    DVector3 t = SteeringController.GetSasTarget(group);
                    Log.Message(string.Format("[Steer] Group {0} SAS→{1} target=({2:F2},{3:F2},{4:F2})",
                        group.GroupId, SteeringController.SasKind, t.x, t.y, t.z));
                }
                nextSteerLogTick = Find.TickManager.TicksGame + 10;
            }
        }

        /// <summary>
        /// 物理步首：把各 group 累积的合冲量施加到速度并清零。
        /// 圆轨 group 先退出圆轨（解析位置/速度会被覆盖）；施加后标记轨道脏。
        /// </summary>
        private void ApplyPendingImpulses()
        {
            foreach (var group in groups.Values)
            {
                if (!group.IsMovable) continue;
                if (group.pendingImpulse.sqrMagnitude <= 0.0) continue;
                if (group.IsCircularOrbit)
                    group.ExitCircularOrbit();
                group.CurrentOrbit.velocity += group.pendingImpulse;
                group.pendingImpulse = DVector3.zero;
                group.OrbitDirty = true;
            }
        }

        protected virtual void UpdateAllMovements(TimeScaleManager timeScaleManager)
        {
            double dtSeconds = UPDATE_INTERVAL_TICKS;
            int subSteps = 1;
            double physicsTick = Find.TickManager.TicksGame;
            
            if (timeScaleManager != null)
            {
                float physicsSpeed = timeScaleManager.PhysicsSpeedMultiplier;
                dtSeconds = UPDATE_INTERVAL_TICKS * physicsSpeed;
                subSteps = timeScaleManager.SubSteps;
                physicsTick = timeScaleManager.GetPhysicsTick();
            }
            
            var celestialMgr = celestialRef.Get();
            if (celestialMgr == null) return;

            // ── 手动操舵（每物理步）：平移/旋转/SAS 输入应用 + 输入打断清队列（D10）──
            ApplySteering(dtSeconds);

            // ── 物理步首：统一施加上一步累积的合冲量（burn/碰撞动量交换/爆炸/手动 ΔV），
            //    然后清零。并行区此后只累积新冲量，不直接改速度。──
            ApplyPendingImpulses();

            // PERF-5：小规模回退顺序——预分配数组存结果，编组数低于阈值走顺序循环，避免并发容器分配
            var movableGroups = movableGroupsScratch;
            movableGroups.Clear();
            foreach (var group in groups.Values)
            {
                if (group.IsMovable)
                    movableGroups.Add(group);
            }

            if (physicsResultsScratch.Length < movableGroups.Count)
            {
                // 扩容留余量（翻倍）：组数持续增长时避免每步重新分配
                // （2026-09-10 探针实测：按精确尺寸扩容会在弹群解离/场景加船的步骤上反复重分配）
                int cap = Math.Max(movableGroups.Count, physicsResultsScratch.Length * 2);
                physicsResultsScratch = new GroupPhysicsResult[cap];
            }
            var physicsResults = physicsResultsScratch;

            // 探针（第 2 条）：容器身份稳定（确在复用）+ 复用数组尾部陈旧槽统计（下方按 Count 上界跳过）
            if (PerfProbe.Enabled) PerfProbe.StepScratch(movableGroups, physicsResults, movableGroups.Count);

            if (movableGroups.Count < PARALLEL_PHYSICS_MIN_GROUPS)
            {
                for (int i = 0; i < movableGroups.Count; i++)
                    physicsResults[i] = ComputeGroupPhysics(movableGroups[i], dtSeconds, physicsTick, celestialMgr, subSteps);
            }
            else
            {
                Parallel.For(0, movableGroups.Count, i =>
                {
                    physicsResults[i] = ComputeGroupPhysics(movableGroups[i], dtSeconds, physicsTick, celestialMgr, subSteps);
                });
            }

            var groupsToCheck = groupsToCheckScratch;
            if (PerfProbe.Enabled) PerfProbe.GroupsToCheckLeftover(groupsToCheck.Count); // 探针：上一步遗留项（应被清空）
            groupsToCheck.Clear();

            // 只遍历本步有效的前 Count 项：复用数组时尾部仍是上一步的结果，不得参与本步
            for (int ri = 0; ri < movableGroups.Count; ri++)
            {
                var result = physicsResults[ri];
                if (!groups.TryGetValue(result.GroupId, out var group)) continue;

                bool burnEndedThisStep = false;

                if (result.HasActiveBurn)
                {
                    // 多网络按切片流率比例分配消耗并扣各自工质；全部网络工质尽 → 燃烧终止
                    float totalConsumed = 0f;
                    int activeCount = result.ActiveSlices != null ? result.ActiveSlices.Count : 0;
                    int exhaustedCount = 0;
                    if (activeCount > 0)
                    {
                        float mfTotal = 0f;
                        for (int i = 0; i < activeCount; i++)
                            mfTotal += result.ActiveSlices[i].massFlow;
                        for (int i = 0; i < activeCount; i++)
                        {
                            var slice = result.ActiveSlices[i];
                            float share = mfTotal > 0f ? (float)result.PropellantConsumed * (slice.massFlow / mfTotal) : 0f;
                            float actual = ConsumeGroupPropellant(group, slice.network.propellant, share);
                            totalConsumed += actual;
                            if (actual < share - 0.001f)
                                exhaustedCount++;
                        }
                    }
                    group.burnConsumedPropellant += totalConsumed;

                    bool allNetworksExhausted = activeCount > 0 && exhaustedCount >= activeCount;
                    if ((allNetworksExhausted || result.BurnExhausted) && !result.SuppressAutoBurnEnd)
                    {
                        // 实际 ΔV 评估（质量口径）：ve·ln(m0/m1)，验证 ṁ/推力模型与火箭方程一致性
                        float veEff = GetGroupPropulsionSource(group)?.EffectiveVe ?? 0f;
                        double actualDv = veEff > 0f && group.burnStartMass > 0.0 && group.burnConsumedPropellant > 0.0
                            ? veEff * Math.Log(group.burnStartMass / Math.Max(group.burnStartMass - group.burnConsumedPropellant, 1.0))
                            : 0.0;
                        group.EndBurn();
                        // burn 结束：推进器退出燃烧状态（表现层）
                        group.GetLinkedMapComp()?.SetThrustersBurning(false);
                        trackedManeuvers.Remove(group.GroupId);
                        alignStateByGroup.Remove(group.GroupId);
                        Log.Message(string.Format("[Burn] Group {0} burn completed: appliedDv={1:F1} m/s actualDv(mass)={2:F1} m/s propellant={3:F1} kg (propellantOut={4})",
                            group.GroupId, group.appliedBurnDV, actualDv, group.burnConsumedPropellant, allNetworksExhausted));
                        burnEndedThisStep = true;
                    }
                }
                else if (result.BurnExhausted && !result.SuppressAutoBurnEnd)
                {
                    group.EndBurn();
                    // burn 结束：推进器退出燃烧状态（表现层）
                    group.GetLinkedMapComp()?.SetThrustersBurning(false);
                    trackedManeuvers.Remove(group.GroupId);
                    alignStateByGroup.Remove(group.GroupId);
                    burnEndedThisStep = true;
                }

                // ── 无 Map 主推/RCS 机动详细日志（DebugDetailedThrust，D15 改动验证）──
                // 每物理步：选网（主/RCS/兜底）、主推轴-目标夹角、Δv/余 dv、双罐燃料、燃尽抑制、转向角速度。
                if (DebugDetailedThrust && result.NoMapThrust)
                {
                    double angleDeg = Mathf.Rad2Deg * Mathf.Acos((float)Math.Max(-1.0, Math.Min(1.0, result.MainDot)));
                    string sel = result.UseMainSel && result.UseRcsSel ? "主+RCS"
                        : result.UseRcsSel ? "RCS" : result.UseMainSel ? "主" : "无";
                    if (result.RcsFallbackSel) sel += "(兜底)";
                    string alignState = result.AligningMain ? "对准中" : (result.UseMainSel ? "已对准" : "全向");
                    string note = result.SuppressAutoBurnEnd ? "  [!RCS尽主料存→不终burn]" : "";
                    Log.Message(string.Format(
                        "[Thrust] G{0} tick={1} {2} | 用网={3} | axis·burn={4:F3}({5:F1}°) | Δv={6:F1} m/s 余dv={7:F1} | 耗={8:F2}kg | {9}{10} | ω={11:F1}°/s",
                        group.GroupId, Find.TickManager.TicksGame, alignState, sel,
                        result.MainDot, angleDeg, result.AppliedDv, group.remainingBurnDV,
                        result.PropellantConsumed, NoMapFuelSummary(group), note,
                        (float)group.angularVelocity.magnitude * Mathf.Rad2Deg));
                }

                // ── 自动对准详细日志（DebugDetailedSteering）：对准开始/完成/进度 ──
                if (DebugDetailedSteering && result.HasActiveBurn)
                {
                    bool aligningNow = result.ActiveSlices == null || result.ActiveSlices.Count == 0;
                    bool wasAligning = alignStateByGroup.TryGetValue(group.GroupId, out var prevAlign) && prevAlign;
                    if (aligningNow && !wasAligning)
                    {
                        DVector3 mainAxisWorld = GetMainThrustWorldAxis(group);
                        Log.Message(string.Format(
                            "[Align] Group {0} 自动对准开始：burnDir=({1:F2},{2:F2},{3:F2}) 主轴世界=({4:F2},{5:F2},{6:F2})",
                            group.GroupId, group.burnDirection.x, group.burnDirection.y, group.burnDirection.z,
                            mainAxisWorld.x, mainAxisWorld.y, mainAxisWorld.z));
                    }
                    else if (!aligningNow && wasAligning)
                    {
                        Log.Message(string.Format("[Align] Group {0} 对准完成 → 进入 burn（主轴进入 {1}° 矢量锥）",
                            group.GroupId, Mathf.Rad2Deg * Mathf.Acos(Mathf.Clamp(SpaceGameComp.DefaultThrustConeCos, -1f, 1f))));
                    }
                    else if (aligningNow && Find.TickManager.TicksGame >= nextAlignLogTick)
                    {
                        DVector3 mainAxisWorld = GetMainThrustWorldAxis(group);
                        double dot = Math.Max(-1.0, Math.Min(1.0, DVector3.Dot(mainAxisWorld, group.burnDirection.normalized)));
                        Log.Message(string.Format(
                            "[Align] Group {0} 对准中：主轴=({1:F2},{2:F2},{3:F2}) vs burnDir 夹角 {4:F1}° 角速度={5:F2}°/s",
                            group.GroupId, mainAxisWorld.x, mainAxisWorld.y, mainAxisWorld.z,
                            Mathf.Rad2Deg * Mathf.Acos((float)dot), (float)group.angularVelocity.magnitude * Mathf.Rad2Deg));
                        nextAlignLogTick = Find.TickManager.TicksGame + 30;
                    }
                    alignStateByGroup[group.GroupId] = aligningNow;
                }

                if (result.NeedsUpdate)
                {
                    var newOrbit = new OrbitState
                    {
                        position = result.NewPosition,
                        velocity = result.NewVelocity,
                        rotation = result.HasRotationChange ? result.NewRotation : group.CurrentOrbit.rotation
                    };
                    UpdateGroupOrbit(group.GroupId, newOrbit);
                }

                // 圆轨两段式机动：仅当本步 burn 结束 → 轨道已落位（NeedsUpdate），再按当前轨道二次计算并结算入轨
                if (burnEndedThisStep && group.HasPendingCircularize)
                    TryCompleteCircularize(group, group.pendingCircularizeBodyId);

                if (result.HasOrbitKeeping && result.OrbitKeepingDvCost > 0.0)
                {
                    // 轨道保持消耗工质：Δm ≈ m·ΔV/ve（小 ΔV 线性近似，避免对数），按启用网络 ṁ 比例分配
                    var okSrc = GetGroupPropulsionSource(group);
                    float okVe = okSrc != null ? okSrc.EffectiveVe : 0f;
                    bool okExhausted;
                    if (okVe <= 0f)
                    {
                        okExhausted = true; // 未装引擎 → 无法维持
                    }
                    else
                    {
                        double okMass = GetGroupMass(group, okSrc);
                        double dmTotal = okMass * result.OrbitKeepingDvCost / okVe;
                        // 分配到启用网络
                        float mfTotal = 0f;
                        foreach (var n in okSrc.Networks)
                        {
                            if (n.MassFlow <= 0f) continue;
                            if (group.burnEnabledNetworks != null && !group.burnEnabledNetworks.Contains(n.propellant)) continue;
                            mfTotal += n.MassFlow;
                        }
                        okExhausted = false;
                        if (mfTotal <= 0f)
                        {
                            okExhausted = true;
                        }
                        else
                        {
                            double consumedTotal = 0.0;
                            int okActive = 0, okExhaustedCount = 0;
                            foreach (var n in okSrc.Networks)
                            {
                                if (n.MassFlow <= 0f) continue;
                                if (group.burnEnabledNetworks != null && !group.burnEnabledNetworks.Contains(n.propellant)) continue;
                                okActive++;
                                double share = dmTotal * (n.MassFlow / mfTotal);
                                float actual = ConsumeGroupPropellant(group, n.propellant, (float)share);
                                consumedTotal += actual;
                                if (actual < share - 0.001)
                                    okExhaustedCount++;
                            }
                            okExhausted = okActive > 0 && okExhaustedCount >= okActive;
                        }
                    }
                    if (okExhausted)
                    {
                        group.ExitOrbitKeeping();
                        Log.Message(string.Format("[OrbitKeeping] Group {0} propellant exhausted, exited orbit-keeping mode.", group.GroupId));
                    }
                }

                group.localPosition = group.CurrentOrbit.position - result.SyncBodyPos;
                group.localVelocity = group.CurrentOrbit.velocity - result.SyncBodyVel;

                groupsToCheck.Add(group.GroupId);
            }

            if (PerfProbe.Enabled) PerfProbe.GroupsToCheckConsume(groupsToCheck.Count, movableGroups.Count);
            foreach (int groupId in groupsToCheck)
            {
                if (!groups.TryGetValue(groupId, out var group)) continue;
                CheckPlanetCollision(group);
            }

            // ── 每 240 真实 tick（4秒）检查一次流速：有机动→Normal，无机动→恢复设置 ──
            // 注意：必须用真实 tick 而非 physicsTick——Combat 模式下 physicsTick 以 1/60 慢速推进，
            // 若以其为基准，射弹消失后要等 16×60=960 真实 tick 才恢复，产生"锁死 1/60x"的错觉。
            if (timeScaleManager != null && Find.TickManager.TicksGame - lastManeuverCheckRealTick >= 240)
            {
                lastManeuverCheckRealTick = Find.TickManager.TicksGame;

                // 射弹在飞 / 导弹 Attack 终末自动段：保持 Combat（1/60）。降速由
                // CombatManager.ManageProjectileTimeScale 负责，这里不恢复期望流速，
                // 避免与降速逻辑打架（否则每 240 tick 会把 Combat 拉回 Fast）。
                var combatMgr = Current.Game?.GetComponent<CombatManager>();
                var avmCheck = Current.Game?.GetComponent<AutonomousVehicleManager>();
                if ((combatMgr != null && combatMgr.ActiveProjectiles.Count > 0)
                    || (avmCheck != null && avmCheck.HasActiveAttack))
                {
                    // 射弹在飞 / 导弹终末撞击中，维持 Combat 档
                }
                else if (trackedManeuvers.Count > 0)
                {
                    // 有机动待执行或推进中 → 降到 Normal
                    if (timeScaleManager.CurrentLevel == TimeScaleLevel.Fast &&
                        DesiredTimeScale == TimeScaleLevel.Fast)
                    {
                        timeScaleManager.QueueCommand(TimeScaleCommand.ToNormal());
                    }
                }
                else if (timeScaleManager.CurrentLevel != DesiredTimeScale)
                {
                    // 无机动且当前流速与设置不符 → 恢复
                    if (DesiredTimeScale == TimeScaleLevel.Fast)
                        timeScaleManager.QueueCommand(TimeScaleCommand.ToFast());
                    else if (DesiredTimeScale == TimeScaleLevel.Combat)
                        timeScaleManager.QueueCommand(TimeScaleCommand.ToCombat());
                    else
                        timeScaleManager.QueueCommand(TimeScaleCommand.ToNormal()); // 默认 Normal 流速：缺此分支会锁死 Combat 档
                }
            }

            // ── 执行到期机动 ──
            foreach (var group in groups.Values)
            {
                if (group.plannedManeuvers.Count == 0)
                {
                    group.pendingAlignDir = DVector3.zero; // 无待执行机动 → 取消预对准
                    continue;
                }

                var nextManeuver = group.PeekNextManeuver();
                if (nextManeuver == null)
                    continue;

                var pm = nextManeuver.Value;

                // 上一段机动仍在燃烧时，等待其结束再点火（防止覆盖 burn 状态）
                if (group.HasActiveBurn)
                    continue;

                // 预点火窗口：进入节点前 ManeuverPreBurnLookaheadTicks 才开始处理本条机动
                if (physicsTick < pm.executeTick - ManeuverPreBurnLookaheadTicks)
                    continue;

                // ── 机动可行性校验（按本条机动选用的工质网络），区分失败原因 ──
                // 提前到点火判断之前：既用于拒绝不可行机动，也用于计算燃烧时长（燃烧居中需要）
                float dvMag = (float)Math.Sqrt(
                    pm.radialDV * pm.radialDV + pm.tangentialDV * pm.tangentialDV + pm.normalDV * pm.normalDV);
                var maneuverSrc = GetGroupPropulsionSource(group);
                double mDry = GetGroupMass(group, null); // 干质量（不含工质）
                var mEst = BurnCalculator.Estimate(dvMag, (float)mDry, maneuverSrc,
                    maneuverSrc != null ? maneuverSrc.Networks : null, pm.useNetworks);
                if (!mEst.Feasible)
                {
                    string reason;
                    if (mEst.Networks == null || mEst.Networks.Count == 0)
                    {
                        reason = "未安装引擎或未启用任何工质网络（无法执行机动）";
                        Log.Message(string.Format("[Maneuver] BLOCKED (no thruster): G{0}", group.GroupId));
                    }
                    else if (mEst.RequiredPropellant <= 0f)
                    {
                        reason = "机动 ΔV 为 0 或无法分配";
                        Log.Message(string.Format("[Maneuver] BLOCKED (zero dv): G{0}", group.GroupId));
                    }
                    else
                    {
                        // 找到第一个不可行的网络，给出明细
                        var badNet = mEst.Networks.Find(ne => !ne.Feasible);
                        if (badNet.Propellant != null)
                            reason = string.Format("工质不足：网络「{0}」需 {1:F0} kg，当前 {2:F0} kg（最大可达 ΔV {3:F0} m/s）",
                                badNet.Propellant.label, badNet.RequiredPropellant, badNet.Available, badNet.MaxDv);
                        else
                            reason = string.Format("工质不足（总需 {0:F0} kg，最大可达 ΔV {1:F0} m/s）",
                                mEst.RequiredPropellant, mEst.MaxDeltaV);
                        Log.Message(string.Format("[Maneuver] BLOCKED (insufficient propellant): G{0} need={1:F1}kg maxDV={2:F1}m/s",
                            group.GroupId, mEst.RequiredPropellant, mEst.MaxDeltaV));
                    }
                    Messages.Message(string.Format("Group {0} 机动被拒绝：{1}", group.GroupId, reason), MessageTypeDefOf.RejectInput);
                    group.PopExecutedManeuver();
                    continue;
                }

                // ── 预对准（前瞻）：本条机动可行 → 点火前 coasting 段就转向主推进轴，
                //    使点火时刻已对准（burn 准时居中，消除初始 miss）。
                //    用规划时刻的惯性 dV 方向（与点火时 StartBurn 的 burnDirection 一致）。
                DVector3 preAlignDv = pm.plannedWorldDv.sqrMagnitude > 0.0
                    ? pm.plannedWorldDv
                    : OrbitMath.GetWorldDeltaV(group.localPosition, group.localVelocity, pm.radialDV, pm.tangentialDV, pm.normalDV);
                group.pendingAlignDir = preAlignDv.sqrMagnitude > 1e-9 ? preAlignDv.normalized : DVector3.zero;

                // ── 燃烧居中：点火时刻 = 节点时刻 − 半程燃烧时长，让冲量模型的"瞬间"落在燃烧中点 ──
                // 与 MechJeb 一致（ignitionUT = node.UT − halfBurnTime），抵消有限时长燃烧的时序误差。
                double ignitionTick = pm.executeTick - mEst.BurnSeconds / 2.0;
                if (physicsTick < ignitionTick)
                    continue;

                bool hadOrbitKeeping = group.HasOrbitKeeping;
                HashSet<int> savedWhitelist = group.orbitKeepingWhitelist != null
                    ? new HashSet<int>(group.orbitKeepingWhitelist) : null;

                if (group.IsCircularOrbit)
                    group.ExitCircularOrbit();

                if (hadOrbitKeeping)
                {
                    group.HasOrbitKeeping = true;
                    group.orbitKeepingWhitelist = savedWhitelist;
                }

                DVector3 relPos2 = group.localPosition;
                DVector3 relVel2 = group.localVelocity;

                Log.Message(string.Format("[Maneuver] Group {0} localFrame: |pos|={1:F0}km |vel|={2:F1} m/s bodyId={3}",
                    group.GroupId, relPos2.magnitude / 1000.0, relVel2.magnitude, group.CelestialBodyId));

                // 优先用规划时刻的世界系 dV（惯性方向，避免点火时刻换算的帧旋转误差）；
                // 旧存档/手动机动未设置时退回点火时刻分量换算
                DVector3 dV = pm.plannedWorldDv.sqrMagnitude > 0.0
                    ? pm.plannedWorldDv
                    : OrbitMath.GetWorldDeltaV(relPos2, relVel2, pm.radialDV, pm.tangentialDV, pm.normalDV);

                double safeRadius = GetBodySafeRadius(group.CelestialBodyId);
                double mu = GetBodyGM(group.CelestialBodyId);
                DVector3 relVelSafe = group.localVelocity + dV;
                bool safe = OrbitMath.IsOrbitSafe(group.localPosition, relVelSafe, safeRadius, mu);
                if (!safe)
                {
                    double sma = OrbitMath.SemiMajorAxis(group.localPosition, relVelSafe);
                    OrbitMath.GetEccentricity(group.localPosition, relVelSafe, out _, out double ecc);
                    double pe = sma > 0 ? sma * (1.0 - ecc) : -1.0;
                    Log.Message(string.Format("[Maneuver] BLOCKED: G{0} sma={1:F0} e={2:F4} pe={3:F0}km safeR={4:F0}km |pos|={5:F0} |v|={6:F0} |dv|={7:F0}",
                        group.GroupId, sma, ecc, pe / 1000.0, safeRadius / 1000.0,
                        group.localPosition.magnitude / 1000.0,
                        relVelSafe.magnitude, dV.magnitude));
                    Messages.Message(string.Format("Group {0} 机动已跳过（将撞击行星）", group.GroupId), MessageTypeDefOf.RejectInput);
                    group.PopExecutedManeuver();
                    continue;
                }

                // 本条机动的推进配置：burn 只烧选用的工质网络（null = 全启用）
                group.burnEnabledNetworks = pm.useNetworks;
                group.StartBurn(dV);
                // 精确交会：出发段点火执行 → 激活闭环引导
                if (pm.armRendezvousGuidance)
                    group.ArmRendezvousGuidance();
                // 推进器喷焰表现：burn 期间地图内推进器标记为燃烧状态
                group.GetLinkedMapComp()?.SetThrustersBurning(true);
                group.PopExecutedManeuver();
                // 标记追踪表：机动已开始推进
                if (trackedManeuvers.ContainsKey(group.GroupId))
                    trackedManeuvers[group.GroupId] = new ManeuverTracker { ExecutePhysicsTick = physicsTick, IsActive = true };

                int remaining = group.plannedManeuvers.Count;
                Log.Message(string.Format("[Maneuver] Group {0} started gradual burn! dv={1:F1} m/s remaining={2}",
                    group.GroupId, dV.magnitude, remaining));
            }

            // ── 交会/接近引导（精确交会：固定到达时刻；近距离接近：动态到达时刻）──
            UpdateRendezvousGuidance(physicsTick);
        }

        /// <summary>
        /// 交会/接近引导：为处于引导的群组按实际状态修正轨迹（MechJeb 式"评估→修正→冷却"节奏）。
        /// 两种模式共用一套引擎：
        /// - 精确交会（rendezvousIsApproach=false）：出发段点火后生效，固定到达时刻，巡航 Lambert + 终端消速；
        /// - 近距离接近（rendezvousIsApproach=true）：登记即生效，到达时刻按当前距离动态重算（tof=dist/限速），
        ///   巡航 Lambert 拦截 + 终端"消速+温柔收拢"，贴近且相对速度归零即完成。
        /// 无引导 / 燃烧中 / 队列里还有未执行机动（出发段或玩家接管）时跳过。
        /// 巡航段：检查节流（30s）+ 预测脱靶量门控（&lt; 2km 视为 on-course 零烧），脱靶才 Lambert 修正；
        /// 终端段（tof &lt; 60s）：消相对速度。修正烧后冷却 60s，避免追逐残差。
        /// 修正烧复用 StartBurn（固定惯性方向 + 工质消耗 + 撞行星保护）。
        /// 计算成本：节流后每次检查 = 1~2 次解析传播 + 偶尔 1 次 Lambert（µs 级），无性能压力。
        /// </summary>
        private void UpdateRendezvousGuidance(double physicsTick)
        {
            foreach (var group in groups.Values)
            {
                if (!group.HasRendezvousGuidance) continue;
                if (!group.IsMovable) continue;
                if (group.HasActiveBurn && !group.burnIsCorrection) continue; // 机动 burn 不打断；修正 burn 允许重规划

                // 队列里还有未执行机动（出发段在排队 / 玩家新排队）→ 引导让位
                if (group.plannedManeuvers.Count > 0) continue;

                // 出发段尚未执行（仅精确交会模式）：若机动已被弹出（被阻止/取消）则引导作废
                // 接近模式无出发段（登记即生效），跳过此检查
                if (!group.rendezvousArmed && !group.rendezvousIsApproach)
                {
                    Log.Message(string.Format("[Guidance] Group {0} departure maneuver never executed, rendezvous guidance cancelled.",
                        group.GroupId));
                    group.ClearRendezvousGuidance();
                    continue;
                }

                if (!groups.TryGetValue(group.rendezvousTargetId, out var target) || !target.IsActive)
                {
                    Log.Message(string.Format("[Guidance] Group {0} rendezvous target {1} gone, guidance cancelled.",
                        group.GroupId, group.rendezvousTargetId));
                    group.ClearRendezvousGuidance();
                    continue;
                }

                // ── 巡航预对准（持续）：出发 burn 完成后立刻调头，为下一次主推（终端消速）提前对准 ──
                // 终端 dv ≈ (目标到达速度 − 本船速度)，用当前相对速度方向做代理姿态；
                // 终端点火时已对准 → 无转向死区（否则最后 180° 调头 + 4km/s 消速叠一起必炸）。
                // 修正 burn 点火时由 burn 内部 AlignMainAxis 覆盖此姿态，烧完自动回归。
                if (!group.HasActiveBurn)
                {
                    if (TryPredictTargetAtTick(target, group.rendezvousArrivalTick, out _, out var vTargetHold))
                    {
                        DVector3 killDir = (vTargetHold - group.localVelocity).normalized;
                        if (killDir.sqrMagnitude > 1e-9)
                            group.pendingAlignDir = killDir;
                    }
                }

                // 瞄准偏移（跳帮落点）：目标局部系偏移 → 世界米偏移（含目标实体偏移与姿态旋转）。
                // 零偏移 = 瞄准 group 中心（原行为）。
                DVector3 aimOffset = ResolveTargetAimOffset(group, target);

                DVector3 r1Rel = group.localPosition;
                DVector3 v1Rel = group.localVelocity;

                double arrivalTick;
                double tof;
                if (group.rendezvousIsApproach)
                {
                    // 接近模式：动态到达时刻 = now + dist / 限速（修正量有界、轨道扰动小）；
                    // 贴近且相对速度归零 → 接近完成
                    double distNow = (target.localPosition + aimOffset - r1Rel).magnitude;
                    double relvelNow = (target.localVelocity - v1Rel).magnitude;
                    if (distNow < ApproachCompletionDistance && relvelNow < ApproachCompletionRelVel)
                    {
                        Log.Message(string.Format("[Guidance] Group {0} approach complete: dist={1:F0}m relvel={2:F1} m/s",
                            group.GroupId, distNow, relvelNow));
                        Messages.Message(string.Format("Group {0} 已接近 Group {1}（距离 {2:F0} m）",
                            group.GroupId, group.rendezvousTargetId, distNow), MessageTypeDefOf.TaskCompletion);
                        group.ClearRendezvousGuidance();
                        continue;
                    }
                    tof = Math.Max(distNow / ApproachMaxClosingSpeed, 1.0);
                    if (distNow < ApproachTerminalDistance)
                    {
                        // 已进入近距离：直接进终端"消速+温柔收拢"，不再切回 Lambert 巡航
                        //（避免 tof=dist/限速 在终端窗口边界横跳 → 极限环振荡）
                        tof = 1.0;
                    }
                    arrivalTick = physicsTick + tof;
                }
                else
                {
                    arrivalTick = group.rendezvousArrivalTick;
                    tof = arrivalTick - physicsTick;
                    if (tof <= 0.0)
                    {
                        // 到达点已过：交会完成（终端消速已在上个检查周期内完成），不追打
                        Log.Message(string.Format("[Guidance] Group {0} rendezvous complete (arrival passed).", group.GroupId));
                        group.ClearRendezvousGuidance();
                        continue;
                    }
                }

                // ── 终端判定（动态）：固定窗口 60s，或「消相对速度所需时间已逼近剩余时间」→ 提前进终端 ──
                // 巡航 Lambert 只保证到点位置、不保证到点速度；必须提前开始消速（相对速度大时动辄数百秒）。
                bool inTerminal = tof < GuidanceTerminalWindow;
                double aMain = EstimateMainAcceleration(group);
                if (!inTerminal && aMain > 0.01
                    && TryPredictTargetAtTick(target, arrivalTick, out _, out var vTermRel0))
                {
                    DVector3 drEst = target.localPosition + aimOffset - r1Rel;
                    double distEst = drEst.magnitude;
                    double relVel = (vTermRel0 - v1Rel).magnitude;
                    // 终端 = 第二次主推（主引擎全力消速）：在"刹车距离"处入场——
                    // 刹车距离 = relVel²/(2·aMain)，此时全力消速恰好覆盖剩余距离，到目标时速度归零。
                    // 过早入场会停短（曾出现 ~1000km 停短）；过晚入场会冲过。
                    double brakingDist = relVel * relVel / (2.0 * aMain);
                    if (distEst <= brakingDist + 30000.0) // +30km 余量（引导 10s 节拍），末段位置收敛补正
                        inTerminal = true;
                }

                // ── 巡航预对准（持续）：出发 burn 完成后立刻调头，为下一次主推（终端消速）提前对准 ──
                // 终端 dv ≈ (目标到达速度 − 本船速度)，用当前相对速度方向做代理姿态；
                // 终端点火时已对准 → 无转向死区。修正 burn 点火时由 burn 内部 AlignMainAxis 临时覆盖。
                if (!group.HasActiveBurn)
                {
                    if (TryPredictTargetAtTick(target, arrivalTick, out _, out var vTargetHold))
                    {
                        DVector3 killDir = (vTargetHold - v1Rel).normalized;
                        if (killDir.sqrMagnitude > 1e-9)
                            group.pendingAlignDir = killDir;
                    }
                }

                // 修正烧后冷却 + 检查节流：避免追逐残差与高频点火（on-course 时大多更新直接跳过）。
                // 终端段跳过冷却（消速要连续迭代）；进行中的修正 burn 也不受冷却限制（允许打断重烧）。
                if (!group.HasActiveBurn && !inTerminal
                    && physicsTick < group.rendezvousLastBurnTick + GuidanceBurnCooldown) continue;
                double checkInterval = inTerminal ? GuidanceTerminalCheckInterval : GuidanceCheckInterval;
                if (physicsTick < group.rendezvousLastCheckTick + checkInterval) continue;
                group.rendezvousLastCheckTick = physicsTick;

                double gm = GetBodyGM(group.CelestialBodyId);
                double missM = double.NaN;
                DVector3 dv;

                if (inTerminal)
                {
                    // 终端 = 第二次主推（主引擎全力消速）：dv = 目标速度 − 当前速度（4.4km/s 级）。
                    // 入场已按刹车距离门控（relVel²/2aMain），全力消速恰好覆盖剩余距离 → 到目标速度归零。
                    // 速度接近匹配但距离仍大时补位置收敛（轨迹整形），避免"停在远处"。
                    if (!TryPredictTargetAtTick(target, arrivalTick, out _, out var vTargetRel))
                        continue;
                    DVector3 drClose = target.localPosition + aimOffset - r1Rel;
                    double distClose = drClose.magnitude;
                    dv = vTargetRel - v1Rel;
                    double relVel = dv.magnitude;
                    if (relVel < 100.0 && distClose > 10000.0)
                    {
                        // 速度已匹配但距离还大：补位置收敛（dr/tof，RCS 级小修正）
                        dv = vTargetRel + drClose / Math.Max(tof, 1.0) - v1Rel;
                    }
                    double needDv = dv.magnitude; // 数学模型：主推 Δv（或末段综合 dv）
                    // 对比日志：距离 + 相对速度 + 主推 Δv（数学模型目标）
                    if (DebugDetailedSteering)
                    {
                        Log.Message(string.Format("[Terminal] G{0} tof={1:F0}s dist={2:F0}km relVel={3:F1}m/s mainDv={4:F1}m/s",
                            group.GroupId, tof, distClose / 1000.0, relVel, needDv));
                    }
                }
                else
                {
                    // 巡航：预测脱靶量门控 → 只有真的会脱靶才修，on-course 零烧
                    if (!TryPredictTargetAtTick(target, arrivalTick, out var r2Rel, out _))
                        continue;
                    DVector3 r2Aim = r2Rel + aimOffset;
                    DVector3 r1ArrivalPred = OrbitMath.GetPositionAtTimeDelta(r1Rel, v1Rel, tof, gm);
                    missM = (r1ArrivalPred - r2Aim).magnitude;
                    if (double.IsNaN(missM) || missM < GuidanceMissThreshold)
                        continue;
                    TransferGeometry geom = group.rendezvousPrograde
                        ? TransferGeometry.ShortWay : TransferGeometry.LongWay;
                    DVector3 vi;
                    try
                    {
                        (vi, _) = Gooding.Solve(gm, r1Rel, r2Aim, tof, geom);
                    }
                    catch (Exception)
                    {
                        continue; // 本次无解，下次再试
                    }
                    dv = vi - v1Rel;
                }

                if (dv.magnitude < GuidanceBurnDeadband)
                {
                    // 修正量可忽略：若正在烧修正 burn → 当前修正已多余，中断（防过烧）
                    if (group.HasActiveBurn)
                        InterruptCorrectionBurn(group);
                    // 终端：相对速度已匹配（≤ 死区）→ 交会完成（提前于"到达点已过"）
                    if (inTerminal)
                    {
                        Log.Message(string.Format("[Guidance] Group {0} terminal complete: relVel={1:F1} m/s tof={2:F0}s",
                            group.GroupId, dv.magnitude, tof));
                        Messages.Message(string.Format("Group {0} 交会完成（相对速度已匹配）", group.GroupId),
                            MessageTypeDefOf.TaskCompletion);
                        group.ClearRendezvousGuidance();
                    }
                    continue;
                }

                // ── 修正 burn 打断重烧：方向大变（>30°）→ 重烧；否则让当前 burn 继续 ──
                // 避免修正过期：烧中重新评估，若轨迹变化导致修正方向偏离，立即换新解。
                if (group.HasActiveBurn)
                {
                    double curDot = DVector3.Dot(group.burnDirection.normalized, dv.normalized);
                    if (curDot >= Math.Cos(30f * Mathf.Deg2Rad))
                        continue; // 方向没大变 → 继续当前烧（不重复对准/浪费）
                    InterruptCorrectionBurn(group);
                    Log.Message(string.Format("[Guidance] Group {0} correction burn replanned: dv={1:F1} m/s (direction changed)",
                        group.GroupId, dv.magnitude));
                }

                // 可行性（继承出发机动的工质网络）
                var src = GetGroupPropulsionSource(group);
                double mDry = GetGroupMass(group, null);
                var est = BurnCalculator.Estimate((float)dv.magnitude, (float)mDry, src,
                    src != null ? src.Networks : null, group.rendezvousUseNetworks);
                if (!est.Feasible)
                {
                    Log.Message(string.Format("[Guidance] Group {0} correction infeasible (dv={1:F1} m/s), guidance cancelled.",
                        group.GroupId, dv.magnitude));
                    Messages.Message(string.Format("Group {0} 交会修正工质不足，精确交会已终止", group.GroupId), MessageTypeDefOf.RejectInput);
                    group.ClearRendezvousGuidance();
                    continue;
                }

                // 撞行星保护：修正烧后轨道近地点不低于安全半径
                double safeRadius = GetBodySafeRadius(group.CelestialBodyId);
                double mu = GetBodyGM(group.CelestialBodyId);
                if (!OrbitMath.IsOrbitSafe(r1Rel, v1Rel + dv, safeRadius, mu))
                {
                    Log.Message(string.Format("[Guidance] Group {0} correction skipped (would impact planet).", group.GroupId));
                    continue;
                }

                // 修正 burn 使用任务网络（继承出发机动的工质选择），避免沿用上一段机动残留的过滤
                group.burnEnabledNetworks = group.rendezvousUseNetworks;
                group.burnIsCorrection = true; // 标记引导修正 burn（允许引导打断重烧）
                // 分发：小修正用 RCS（全矢量免对准，秒级烧完，无转向死区）；
                // 大修正用主引擎（对准后全力烧）——RCS 推力小，大修正拖长引导冻结 → 修正过期 → 死循环。
                group.burnUseRcs = false;
                var rcsMc = group.GetLinkedMapComp();
                if (rcsMc != null && rcsMc.HasRcs)
                {
                    double rcsMass = GetGroupMass(group, rcsMc);
                    double aRcs = rcsMc.RcsTotalThrust / Math.Max(rcsMass, 1.0);
                    // RCS 能在 ~15s 内烧完的修正量才用 RCS（引导冻结时间可控）
                    group.burnUseRcs = aRcs > 0.0 && dv.magnitude <= aRcs * 15.0;
                }
                group.StartBurn(dv);
                group.rendezvousLastBurnTick = physicsTick; // 冷却：烧完等一个冷却期再评估
                group.GetLinkedMapComp()?.SetThrustersBurning(true);
                if (trackedManeuvers.ContainsKey(group.GroupId))
                    trackedManeuvers[group.GroupId] = new ManeuverTracker { ExecutePhysicsTick = physicsTick, IsActive = true };
                Log.Message(string.Format("[Guidance] Group {0} correction burn started: dv={1:F1} m/s miss={2:F0}m tof={3:F0}s rcs={4} {5}",
                    group.GroupId, dv.magnitude, missM, tof,
                    group.burnUseRcs,
                    group.rendezvousIsApproach
                        ? "(approach)"
                        : inTerminal ? "(terminal)" : ""));
            }
        }

        /// <summary>
        /// 该船当前最大可用加速度（m/s²）= 全网络推力（含 RCS）/ 当前质量。
        /// 终端消速时间估算用（上限口径：不区分方向，忽略对准）。
        /// </summary>
        private double EstimateMaxAcceleration(SpaceEntityGroup group)
        {
            var src = GetGroupPropulsionSource(group);
            if (src == null) return 0.0;
            double mass = GetGroupMass(group, src);
            if (mass <= 0.0) return 0.0;
            double f = 0.0;
            foreach (var n in src.Networks)
                f += n.Thrust; // RCS 引擎计入网络总推力
            return f / mass;
        }

        /// <summary>
        /// 该船主引擎加速度（m/s²）=（全网络推力 − RCS）/ 当前质量。
        /// 终端"第二次主推"的消速时间估算用（RCS 只做末段精修，不计入主推时长）。
        /// </summary>
        private double EstimateMainAcceleration(SpaceEntityGroup group)
        {
            var src = GetGroupPropulsionSource(group);
            if (src == null) return 0.0;
            double mass = GetGroupMass(group, src);
            if (mass <= 0.0) return 0.0;
            double f = 0.0;
            foreach (var n in src.Networks)
                f += n.Thrust;
            if (src is SpaceMapComp mc)
                f -= mc.RcsTotalThrust; // 主推 = 总推力 − RCS
            return f / mass;
        }

        /// <summary>
        /// 中断一次进行中的引导修正 burn（剩余 dV 丢弃），清理 burn 状态与表现层。
        /// 由引导"打断重烧"路径调用。
        /// </summary>
        private void InterruptCorrectionBurn(SpaceEntityGroup group)
        {
            if (group == null || !group.HasActiveBurn) return;
            group.EndBurn(); // 清 burnUseRcs / burnIsCorrection / remainingBurnDV
            alignStateByGroup.Remove(group.GroupId);
            trackedManeuvers.Remove(group.GroupId);
            group.GetLinkedMapComp()?.SetThrustersBurning(false);
        }

        /// <summary>
        /// 预测目标群在指定 physicsTick 的中心天体相对位置/速度（与 RendezvousPlanner 相同的坐标约定）。
        /// 圆轨：解析式（位置减中心天体偏移；速度取圆轨切向）。非圆轨：从当前相对状态开普勒传播。
        /// </summary>
        private bool TryPredictTargetAtTick(SpaceEntityGroup target, double tick,
            out DVector3 posRel, out DVector3 velRel)
        {
            posRel = DVector3.zero;
            velRel = DVector3.zero;
            if (target == null) return false;

            if (target.IsCircularOrbit)
            {
                posRel = target.GetCircularPositionAtTick(tick) - target.GetCentralBodyOffset(tick);
                DVector3 radialDir = posRel.normalized;
                DVector3 tangentDir = DVector3.Cross(target.circularOrbitNormal, radialDir).normalized;
                velRel = tangentDir * (target.circularAngularVelocity * target.circularRadius);
                return true;
            }

            if (target.CelestialBodyId < 0) return false;
            double gm = GetBodyGM(target.CelestialBodyId);
            double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();
            int nowTick = (int)Math.Round(physicsTickNow);
            DVector3 relPos = target.CurrentOrbit.position - target.GetCentralBodyOffset(nowTick);
            DVector3 relVel = target.CurrentOrbit.velocity - target.GetCentralBodyVelocity(nowTick);
            double tof = tick - physicsTickNow;
            posRel = OrbitMath.GetPositionAtTimeDelta(relPos, relVel, tof, gm);
            velRel = OrbitMath.GetVelocityAtTimeDelta(relPos, relVel, tof, gm);
            return true;
        }

        /// <summary>
        /// 解析引导瞄准偏移（世界米）：追逐者配置的目标局部系偏移 →
        /// 目标实体偏移 + 姿态旋转(offset)。零偏移返回零向量（兼容瞄准 group 中心）。
        /// 跳帮落点用：落点格子 → 目标空间站局部系偏移。
        /// </summary>
        private DVector3 ResolveTargetAimOffset(SpaceEntityGroup chaser, SpaceEntityGroup target)
        {
            if (chaser.rendezvousTargetOffset == DVector3.zero) return DVector3.zero;
            foreach (int eid in target.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null) continue;
                return e.Position + DVector3.Rotate(chaser.rendezvousTargetOffset, e.Rotation);
            }
            return chaser.rendezvousTargetOffset;
        }

        private void UpdateCombatInterpolation(int currentTick, TimeScaleManager timeScaleManager)
        {
            double secondsPerTick = 1.0 / 60.0;

            foreach (var group in groups.Values)
            {
                if (!group.IsMovable || group.CombatCache == null) continue;

                // 插值时间相对 CombatCache 基准连续推进（不再 elapsed % 2.0 循环）。
                // 原实现取模导致非圆轨目标位置每 2s 跳回战斗开始位置，射弹（物理位置连续）
                // 与其时间基准错位 → 相对位置突变 → 命中检测永远失败。
                double t = (currentTick - group.CombatCache.baseTick) * secondsPerTick;

                // CombatCache 覆盖区间（dt）已过 → 以当前状态重建，防止 acc/jerk 外推漂移
                if (t > group.CombatCache.dt)
                    RebuildCombatCache(group, currentTick);

                DVector3 interpolatedPos = group.CombatCache.InterpolatePosition(t);
                DVector3 interpolatedVel = group.CombatCache.InterpolateVelocity(t);

                // 物理/渲染状态分离：插值结果只写渲染缓存，绝不污染 CurrentOrbit。
                // CurrentOrbit 只由物理积分（UpdateAllMovements）在 physicsTick 时间轴推进，
                // 碰撞/射弹检测统一读物理状态，与射弹同基准（万物统一物理时间步长）。
                group.SetRenderedPosition(interpolatedPos);
            }
        }

        /// <summary>
        /// 以当前轨道状态重建 Combat 插值缓存（更新基准 tick + 重新采样 acc/jerk）。
        /// 重建起点 = 当前插值末端 → 位置/速度连续无跳变。
        /// </summary>
        private void RebuildCombatCache(SpaceEntityGroup group, int currentTick)
        {
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (celestialMgr == null) return;

            var primaryBody = celestialMgr.GetBodyById(group.CelestialBodyId);
            if (primaryBody == null) return;

            DVector3 currentPos = group.CurrentOrbit.position;
            DVector3 currentVel = group.CurrentOrbit.velocity;

            DVector3 relPos, relVel;
            double physicsTickSample = TimeScaleManager.GetPhysicsTickSafe();
            if (primaryBody.bodyType != CelestialBodyType.Planet)
            {
                // 重力采样用 physicsTick（物理时间轴）；baseTick 保持 realTick 供插值索引
                DVector3 bodyPos = celestialMgr.GetBodyLocalPosition(primaryBody.bodyId, physicsTickSample);
                DVector3 bodyVel = celestialMgr.GetBodyLocalVelocity(primaryBody.bodyId, physicsTickSample);
                relPos = currentPos - bodyPos;
                relVel = currentVel - bodyVel;
            }
            else
            {
                relPos = currentPos;
                relVel = currentVel;
            }

            NBodyIntegrator.MultiBodyAccelJerk(relPos, relVel,
                physicsTickSample, primaryBody, celestialMgr,
                group.orbitKeepingWhitelist,
                out var acc, out var jerk);

            group.CombatCache = new SpaceEntityGroup.PhysicsStateCache
            {
                baseTick = currentTick,
                startPos = currentPos,
                startVel = currentVel,
                acc = acc,
                jerk = jerk,
                dt = 2.0,
                offset = 0.0
            };
        }

        #region 圆轨进入（两段式 dV 机动）

        /// <summary>
        /// 圆轨进入的残差阈值（m/s）：第二段一次性消耗允许的最大残差；
        /// 第一段 dV 可行性检查时也固定加此余量（两段机动最坏消耗 = 机动所需 + 阈值）。
        /// </summary>
        public const double CircularizeDvThreshold = 10.0;

        /// <summary>
        /// 发起进入圆轨的两段式机动（取代直接 EnterCircularOrbit 的免费置态）：
        /// ① 按当前轨道计算圆轨机动节点 ΔV → 检查工质（所需+阈值）→ 有则立即开始缓释 burn；
        /// ② burn 完成后由 <see cref="TryCompleteCircularize"/> 按当前轨道二次计算圆轨，
        ///    残差 ≤ 阈值则一次性扣除对应工质并入轨（第二次计算的圆轨），否则保持正常轨道。
        /// </summary>
        public bool BeginCircularize(SpaceEntityGroup group, int bodyId)
        {
            if (group == null) return false;
            if (group.IsCircularOrbit)
            {
                Messages.Message(string.Format("Group {0} 已在圆轨。", group.GroupId), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (group.HasActiveBurn || group.HasPendingCircularize)
            {
                Messages.Message(string.Format("Group {0} 正在执行机动，请稍候。", group.GroupId), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            int realTick = Find.TickManager.TicksGame;
            if (!TryComputeCircularState(bodyId, group.CurrentOrbit.position, group.CurrentOrbit.velocity, realTick,
                out DVector3 vCircWorld, out double r))
            {
                Messages.Message(string.Format("Group {0} 无法计算天体 {1} 的圆轨状态。", group.GroupId, bodyId), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            DVector3 dvVec = vCircWorld - group.CurrentOrbit.velocity;
            double nodeDV = dvVec.magnitude;
            if (nodeDV < 0.5)
            {
                // 已接近圆轨（<0.5 m/s）：免 burn，直接走第二段结算
                return TryCompleteCircularize(group, bodyId, vCircWorld);
            }

            // ── 工质检查：所需 + 阈值（两段机动最坏消耗 = 机动所需 + 残差阈值）──
            var src = GetGroupPropulsionSource(group);
            if (src == null || src.Networks.Count == 0)
            {
                Messages.Message(string.Format("Group {0} 无推进能力，无法消耗 dV 进入圆轨。", group.GroupId), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            double mDry = GetGroupMass(group, null);
            var est = BurnCalculator.Estimate((float)(nodeDV + CircularizeDvThreshold), (float)mDry, src, src.Networks, null);
            if (!est.Feasible)
            {
                string reason = est.Networks == null || est.Networks.Count == 0
                    ? "未安装引擎或未启用任何工质网络"
                    : string.Format("工质不足（需约 {0:F0} kg，最大可达 ΔV {1:F0} m/s）", est.RequiredPropellant, est.MaxDeltaV);
                Messages.Message(string.Format("Group {0} 无法进入圆轨：{1}。", group.GroupId, reason), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            // ── 立即执行节点：退出圆轨残留，开始缓释 burn（全网络）──
            if (group.IsCircularOrbit)
                group.ExitCircularOrbit();
            group.burnEnabledNetworks = null;
            group.StartBurn(dvVec);
            group.GetLinkedMapComp()?.SetThrustersBurning(true);
            group.HasPendingCircularize = true;
            group.pendingCircularizeBodyId = bodyId;
            // 机动追踪：进入圆轨 burn 期间自动降速到 Normal
            trackedManeuvers[group.GroupId] = new ManeuverTracker
            {
                ExecutePhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                IsActive = true
            };

            Log.Message(string.Format("[Circularize] Group {0} 圆轨机动开始: body={1} r={2:F0}km 节点ΔV={3:F1}m/s（检查含阈值余量 {4:F1}）",
                group.GroupId, bodyId, r / 1000.0, nodeDV, CircularizeDvThreshold));
            return true;
        }

        /// <summary>
        /// 圆轨第二段结算（在缓释 burn 完成后调用）：按当前轨道二次计算圆轨，
        /// 当前速度与圆轨速度残差 ≤ 阈值则一次性扣除对应工质并入轨（第二次计算的圆轨）；
        /// 残差过大则放弃入轨，保持正常轨道（可再次点击圆轨重试）。
        /// </summary>
        /// <param name="precomputedVCirc">已接近圆轨时直接传入的圆轨速度（避免重复计算）。</param>
        private bool TryCompleteCircularize(SpaceEntityGroup group, int bodyId, DVector3? precomputedVCirc = null)
        {
            if (group == null) return false;
            group.HasPendingCircularize = false;
            group.pendingCircularizeBodyId = -1;

            int realTick = Find.TickManager.TicksGame;
            // 最后一步 burn 冲量尚未施加：并入当前速度得到真实速度
            DVector3 vNow = group.CurrentOrbit.velocity + group.pendingImpulse;

            DVector3 vCirc;
            double r;
            if (precomputedVCirc.HasValue)
            {
                vCirc = precomputedVCirc.Value;
                r = 0.0;
            }
            else if (!TryComputeCircularState(bodyId, group.CurrentOrbit.position, vNow, realTick, out vCirc, out r))
            {
                Log.Warning(string.Format("[Circularize] Group {0} 二次计算圆轨失败，保持正常轨道。", group.GroupId));
                return false;
            }

            double residual = (vCirc - vNow).magnitude;
            if (residual > CircularizeDvThreshold)
            {
                Log.Message(string.Format("[Circularize] Group {0} 残差 {1:F1} m/s 超过阈值 {2:F1}，未进入圆轨（保持正常轨道，可重试）。",
                    group.GroupId, residual, CircularizeDvThreshold));
                Messages.Message(string.Format("Group {0} 圆轨失败：残差 {1:F1} m/s 超过阈值 {2:F1} m/s。",
                    group.GroupId, residual, CircularizeDvThreshold), MessageTypeDefOf.RejectInput, false);
                return false;
            }

            // ── 成功：真实速度（含最后冲量）落位 → 清空 pending → 一次性扣残差工质 → 进入圆轨 ──
            group.CurrentOrbit.velocity = vNow;
            group.pendingImpulse = DVector3.zero; // 已并入 vNow，不再叠加
            ConsumeDeltaVOneShot(group, residual);
            group.EnterCircularOrbit(realTick, bodyId);

            Log.Message(string.Format("[Circularize] Group {0} 进入圆轨: body={1} r={2:F0}km 残差ΔV={3:F1} m/s（≤阈值 {4:F1}）已一次性扣工质",
                group.GroupId, bodyId, r > 0.0 ? r / 1000.0 : group.circularRadius / 1000.0, residual, CircularizeDvThreshold));
            return true;
        }

        /// <summary>
        /// 计算某天体在当前状态下的圆轨速度向量（世界系）与轨道半径。
        /// 数学与 EnterCircularOrbit 一致（含月球等非行星天体的坐标换算）。
        /// </summary>
        private bool TryComputeCircularState(int bodyId, DVector3 worldPos, DVector3 worldVel, int realTick,
            out DVector3 vCircWorld, out double radiusMeters)
        {
            vCircWorld = DVector3.zero;
            radiusMeters = 0.0;
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return false;
            var body = mgr.GetBodyById(bodyId);
            if (body == null) return false;

            DVector3 relPos = worldPos, relVel = worldVel;
            if (body.bodyType != CelestialBodyType.Planet)
            {
                relPos -= mgr.GetBodyLocalPosition(bodyId, realTick);
                relVel -= mgr.GetBodyLocalVelocity(bodyId, realTick);
            }
            radiusMeters = relPos.magnitude;
            if (radiusMeters < 0.01) return false;

            double mu = body.gravitationalParameter > 0.0
                ? body.gravitationalParameter : SpaceConstants.PrimaryGravitationalParameter;
            double vTan = System.Math.Sqrt(mu / radiusMeters);

            DVector3 radialDir = relPos.normalized;
            DVector3 normal = DVector3.Cross(relPos, relVel).normalized;
            if (normal.sqrMagnitude < 0.01)
                normal = DVector3.up; // 速度为零/纯径向退化
            DVector3 tangentDir = DVector3.Cross(normal, radialDir).normalized;
            if (tangentDir.sqrMagnitude < 0.01)
            {
                DVector3 refVec = System.Math.Abs(radialDir.y) < 0.9 ? DVector3.up : DVector3.forward;
                tangentDir = DVector3.Cross(refVec, radialDir).normalized;
            }
            vCircWorld = tangentDir * vTan;
            if (body.bodyType != CelestialBodyType.Planet)
                vCircWorld += mgr.GetBodyLocalVelocity(bodyId, realTick);
            return true;
        }

        /// <summary>
        /// 一次性按火箭方程把残差 dV 换算为工质并扣除（按各网络 ṁ 比例分配，与 burn/轨道保持一致）。
        /// 对应「小于阈值就一次性减去 dV（减去对应工质）」。
        /// </summary>
        private void ConsumeDeltaVOneShot(SpaceEntityGroup group, double dv)
        {
            if (dv <= 0.001) return;
            var src = GetGroupPropulsionSource(group);
            if (src == null) return;
            double ve = src.EffectiveVe;
            if (ve <= 0.0) return;
            double m0 = GetGroupMass(group, src);
            // Δm = m0·(1 − e^(−ΔV/ve))
            double dm = m0 * (1.0 - Math.Exp(-dv / ve));
            if (dm <= 0.0) return;

            float mfTotal = 0f;
            foreach (var n in src.Networks)
                mfTotal += n.MassFlow;
            if (mfTotal <= 0f) return;
            foreach (var n in src.Networks)
            {
                double share = dm * (n.MassFlow / mfTotal);
                ConsumeGroupPropellant(group, n.propellant, (float)share);
            }
            Log.Message(string.Format("[Circularize] Group {0} 残差 dV 一次性消耗: {1:F1} m/s → 工质 {2:F1} kg",
                group.GroupId, dv, dm));
        }

        /// <summary>
        /// 紧急终止当前缓释 burn：丢弃剩余 dV（已烧的工质不退，已累积未施加的冲量照常施加），
        /// 恢复网络/喷焰表现，清掉圆轨结算标记与机动追踪。
        /// </summary>
        public void EmergencyStopBurn(SpaceEntityGroup group)
        {
            if (group == null || !group.HasActiveBurn) return;
            double applied = group.appliedBurnDV;
            double planned = applied + group.remainingBurnDV;
            group.EndBurn();
            group.burnEnabledNetworks = null;
            group.GetLinkedMapComp()?.SetThrustersBurning(false);
            trackedManeuvers.Remove(group.GroupId);
            if (group.HasPendingCircularize)
            {
                group.HasPendingCircularize = false;
                group.pendingCircularizeBodyId = -1;
            }
            Log.Message(string.Format("[Burn] Group {0} burn EMERGENCY STOPPED by player (applied {1:F1} / {2:F1} m/s)",
                group.GroupId, applied, planned));
            Messages.Message(string.Format("Group {0} 机动已紧急终止（已施加 {1:F1} / {2:F1} m/s）。",
                group.GroupId, applied, planned), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>
        /// 取消 group 从指定索引（含）开始的机动规划（保留前面的）。
        /// 队列清空时同时移除机动追踪，避免自动降速卡在 Normal。
        /// </summary>
        public void CancelGroupManeuvers(SpaceEntityGroup group, int index)
        {
            if (group == null || index < 0) return;
            group.CancelManeuversFrom(index);
            if (group.plannedManeuvers.Count == 0)
                trackedManeuvers.Remove(group.GroupId);
            Messages.Message(string.Format("Group {0} 已取消第 {1} 段及以后的机动规划。",
                group.GroupId, index + 1), MessageTypeDefOf.NeutralEvent, false);
        }

        #endregion

        #region 行星碰撞

        public double GetBodySafeRadius(int celestialBodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return SpaceConstants.PlanetRadiusMeters;
            var body = mgr.GetBodyById(celestialBodyId);
            if (body == null) return SpaceConstants.PlanetRadiusMeters;
            return body.radius + body.atmosphereThickness;
        }

        public double GetBodyGM(int celestialBodyId)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return SpaceConstants.PrimaryGravitationalParameter;
            var body = mgr.GetBodyById(celestialBodyId);
            if (body == null || body.gravitationalParameter <= 0.0)
                return SpaceConstants.PrimaryGravitationalParameter;
            return body.gravitationalParameter;
        }

        private DVector3 GetBodyWorldOffset(int celestialBodyId, int tick)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return DVector3.zero;
            var body = mgr.GetBodyById(celestialBodyId);
            if (body == null || body.bodyType == CelestialBodyType.Planet)
                return DVector3.zero;
            return mgr.GetBodyLocalPosition(celestialBodyId, tick);
        }

        private DVector3 GetBodyWorldVelOffset(int celestialBodyId, int tick)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null) return DVector3.zero;
            var body = mgr.GetBodyById(celestialBodyId);
            if (body == null || body.bodyType == CelestialBodyType.Planet)
                return DVector3.zero;
            return mgr.GetBodyLocalVelocity(celestialBodyId, tick);
        }

        private void CheckPlanetCollision(SpaceEntityGroup group)
        {
            if (group == null || !group.IsActive) return;
            if (group.exemptPlanetCollision) return; // 火箭爬升段豁免：出生在大气层内、初速高于着陆阈值，入轨时清除

            int tick = Find.TickManager.TicksGame;
            DVector3 bodyOffset = GetBodyWorldOffset(group.CelestialBodyId, tick);
            DVector3 relPos = group.CurrentOrbit.position - bodyOffset;
            double safeRadius = GetBodySafeRadius(group.CelestialBodyId);

            double distSqr = relPos.sqrMagnitude;
            if (distSqr > safeRadius * safeRadius)
                return;

            double speed = group.CurrentOrbit.velocity.magnitude;
            int groupId = group.GroupId;

            if (speed > SpaceConstants.MaxSafeLandingSpeedMps)
            {
                Log.Message(string.Format(
                    "[Collision] Group {0} CRASHED into planet! Impact speed: {1:F1} m/s (max safe: {2:F0} m/s). Entities destroyed.",
                    groupId, speed, SpaceConstants.MaxSafeLandingSpeedMps));

                Messages.Message(string.Format(
                    "Group {0} 坠毁！撞击速度 {1:F1} m/s，超出安全限值 {2:F0} m/s。",
                    groupId, speed, SpaceConstants.MaxSafeLandingSpeedMps),
                    MessageTypeDefOf.ThreatBig);
            }
            else
            {
                Log.Message(string.Format(
                    "[Collision] Group {0} LANDED safely on planet surface. Touchdown speed: {1:F1} m/s.",
                    groupId, speed));

                Messages.Message(string.Format(
                    "Group {0} 成功着陆于行星表面！着陆速度 {1:F1} m/s。",
                    groupId, speed),
                    MessageTypeDefOf.TaskCompletion);
            }

            DestroyGroup(groupId);
        }

        /// <summary>
        /// 从 group 的推进源工质池扣除工质。返回实际扣除量（kg，受存量截断）。
        /// 实际扣除 < 需求表示工质已耗尽（或无推进源）。
        /// </summary>
        private float ConsumeGroupPropellant(SpaceEntityGroup group, PropellantDef def, float kg)
        {
            if (def == null || kg <= 0f) return 0f;
            var src = GetGroupPropulsionSource(group);
            if (src == null) return 0f; // 无推进源 → 扣 0
            return src.TryConsumePropellant(def, kg);
        }

        /// <summary>无 Map 主推型双罐燃料摘要（主网 vs RCS 网，同 def 去重后合计）供 [Thrust] 日志。</summary>
        private string NoMapFuelSummary(SpaceEntityGroup group)
        {
            var src = GetGroupPropulsionSource(group);
            if (src == null) return "无源";
            double main = 0.0, rcs = 0.0;
            var seenMain = new HashSet<PropellantDef>();
            var seenRcs = new HashSet<PropellantDef>();
            foreach (var n in src.Networks)
            {
                if (n.propellant == null) continue;
                double m = src.GetPropellantMass(n.propellant);
                if (n.HasRcsEngines)
                {
                    if (seenRcs.Add(n.propellant)) rcs += m;
                }
                else if (seenMain.Add(n.propellant))
                {
                    main += m;
                }
            }
            return string.Format("主={0:F0} RCS={1:F0}kg", main, rcs);
        }

        /// <summary>
        /// 取 group 的推进源：优先任一实体关联的 SpaceMapComp（Map 建筑引擎/工质）；
        /// 无 Map 时 = 自带推进实体（导弹/载荷等纯物理实体，PropulsionData）：
        ///   1 个 → 该实体（单发/火箭热路径，行为不变）；
        ///   &gt;1 个（弹群，2026-09-08 思路 A）→ 群级聚合器 GroupPropulsionAggregator
        ///   （N 引擎同步：推力/流率全组合并、工质按实体分摊——燃料归实体自己，无记账）。
        /// 聚合器按「实体集版本 ⊕ 各推进实体推进版本」指纹缓存在 group 上复用（2026-09-10
        /// 性能第 1 条）：多实体组每物理步被查 4+ 次，缓存后 BuildNetworks 每变更只跑一次。
        /// 并行只读安全：指纹与 Networks 只依赖实体集与推进数据，物理步内无人变更它们
        /// （消耗工质只改存量，不入指纹）；唯一并行区 Parallel.For 每组恰好一个迭代。
        /// </summary>
        public IPropulsionSource GetGroupPropulsionSource(SpaceEntityGroup group)
        {
            var mapComp = group.GetLinkedMapComp();
            if (mapComp != null) return mapComp;
            SpaceEntity first = null;
            int n = 0;
            // 指纹复用既有遍历计算（每实体 2 次整数运算），无需在增删实体/改推进处回调置脏。
            int stamp = group.entitySetVersion;
            foreach (int eid in group.GetEntityIds())
            {
                var e = GetEntityById(eid);
                if (e == null || !e.HasPropulsionData) continue;
                if (first == null) first = e;
                n++;
                stamp = stamp * 31 + e.PropulsionVersion;
            }
            if (n == 1) return first;
            if (n > 1)
            {
                var agg = group.cachedPropulsionAggregator;
                if (agg == null || agg.SourceStamp != stamp)
                {
                    agg = new GroupPropulsionAggregator(group, this, stamp);
                    group.cachedPropulsionAggregator = agg;
                    if (PerfProbe.Enabled) PerfProbe.AggRebuild(group.GroupId, group.entitySetVersion, stamp, n);
                }
                else if (PerfProbe.Enabled)
                {
                    PerfProbe.AggReuse(group.GroupId);
                    if (PerfProbe.ShadowChecks && PerfProbe.ShouldShadowAgg())
                        ProbeAggregatorShadow(group, agg);
                }
                return agg;
            }
            return null;
        }

        /// <summary>
        /// 探针（第 1 条影子校验）：用同一 group/指纹现建一个聚合器（= 优化前的旧行为：每次现建），
        /// 与缓存实例逐网络比对（工质引用 / 推力 / 流率 / 主-RCS 归属），并核对燃料总量与容量是否实时。
        /// 任何差异即"缓存陈旧"，由 PerfProbe 红字报出。仅在探针开启且命中节流时调用。
        /// </summary>
        private void ProbeAggregatorShadow(SpaceEntityGroup group, GroupPropulsionAggregator cached)
        {
            var fresh = new GroupPropulsionAggregator(group, this, cached.SourceStamp);
            var a = cached.Networks;
            var b = fresh.Networks;
            if (a.Count != b.Count)
            {
                PerfProbe.AggShadowResult(false, $"G{group.GroupId} 网络数 缓存={a.Count} 现建={b.Count}");
                return;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (!ReferenceEquals(a[i].propellant, b[i].propellant))
                {
                    PerfProbe.AggShadowResult(false, $"G{group.GroupId} 网络{i} 工质引用不同：{a[i].propellant}≠{b[i].propellant}");
                    return;
                }
                if (a[i].HasRcsEngines != b[i].HasRcsEngines)
                {
                    PerfProbe.AggShadowResult(false, $"G{group.GroupId} 网络{i} 主/RCS 归属不同");
                    return;
                }
                if (Math.Abs(a[i].Thrust - b[i].Thrust) > 1e-3f || Math.Abs(a[i].MassFlow - b[i].MassFlow) > 1e-6f)
                {
                    PerfProbe.AggShadowResult(false,
                        $"G{group.GroupId} 网络{i} 参数不同：推力 {a[i].Thrust:F6}≠{b[i].Thrust:F6} 流率 {a[i].MassFlow:F6}≠{b[i].MassFlow:F6}");
                    return;
                }
            }
            // 燃料/容量必须实时（缓存不得固化存量——这是第 1 条"物理是否不及时"的关键判据）
            if (Math.Abs(cached.TotalPropellantMass - fresh.TotalPropellantMass) > 1e-3f
                || Math.Abs(cached.TotalPropellantCapacity - fresh.TotalPropellantCapacity) > 1e-3f)
            {
                PerfProbe.AggShadowResult(false,
                    $"G{group.GroupId} 燃料/容量不一致：存量 {cached.TotalPropellantMass:F3}≠{fresh.TotalPropellantMass:F3} 容量 {cached.TotalPropellantCapacity:F3}≠{fresh.TotalPropellantCapacity:F3}");
                return;
            }
            PerfProbe.AggShadowResult(true, null);
        }

        /// <summary>group 当前质量（kg）= Σ 实体结构质量 + 推进源工质总量。</summary>
        private double GetGroupMass(SpaceEntityGroup group, IPropulsionSource src)
        {
            double mass = 0.0;
            foreach (int eid in group.GetEntityIds())
            {
                if (entities.TryGetValue(eid, out var e))
                    mass += e.TotalMass;
            }
            if (src != null)
                mass += src.TotalPropellantMass;
            return mass;
        }

        /// <summary>
        /// group 级 dV 显示数据（剩余/最大/工质），供信息面板展示。
        /// 与实体 RemainingDeltaV 同口径：ve·ln(wet/dry)。
        /// </summary>
        public (float remainingDv, float maxDv, float propellantMass, float propellantCapacity) GetGroupDeltaV(SpaceEntityGroup group)
        {
            if (group == null) return (0f, 0f, 0f, 0f);
            var src = GetGroupPropulsionSource(group);
            if (src == null) return (0f, 0f, 0f, 0f);
            if (src.EffectiveVe <= 0f)
                return (0f, 0f, src.TotalPropellantMass, src.TotalPropellantCapacity);
            double dry = GetGroupMass(group, null);
            double wet = dry + src.TotalPropellantMass;
            double wetMax = dry + src.TotalPropellantCapacity;
            float remaining = wet > dry ? (float)(src.EffectiveVe * Math.Log(wet / dry)) : 0f;
            float maxDv = wetMax > dry ? (float)(src.EffectiveVe * Math.Log(wetMax / dry)) : 0f;
            return (remaining, maxDv, src.TotalPropellantMass, src.TotalPropellantCapacity);
        }

        #endregion

        #endregion

        #region 持久化

        public override void LoadedGame()
        {
            base.LoadedGame();
            SpacecraftMapRegistry.Reconcile();
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            SpacecraftMapRegistry.Reconcile();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref entities, "entities", LookMode.Value, LookMode.Deep);
            Scribe_Collections.Look(ref groups, "groups", LookMode.Value, LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                planetGrids = new Dictionary<int, SpatialGrid3D<ISpatialEntity>>();
                groupLastCoarsePos = new Dictionary<int, DVector3>();
                foreach (var group in groups.Values)
                {
                    var targetGrid = GetGridForGroup(group.GroupId);
                    if (targetGrid != null)
                    {
                        targetGrid.Add(group, group.Position);
                        groupLastCoarsePos[group.GroupId] = group.Position;
                    }
                }
            }
        }

        #endregion
    }
}
