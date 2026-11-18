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

        private Dictionary<int, object> mapComponents = new Dictionary<int, object>();

        // 时间规则：1 tick = 1 秒，每 2 tick 更新一次物理
        private const int UPDATE_INTERVAL_TICKS = 2;
        private int lastUpdateTick = 0;

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
            Log.Message(string.Format("[Grid] Group {0} registered: bodyId={1} frameId={2}",
                group.GroupId, group.CelestialBodyId, frameId));

            // 粗网格：注册群组
            var targetGrid = GetGridForGroup(group.GroupId);
            if (targetGrid != null)
                targetGrid.Add(group, group.Position);

            // 细网格：注册群组到战斗网格
            var combatGrid = Current.Game?.GetComponent<CombatGrid>();
            if (combatGrid != null && group.IsCollidable)
                combatGrid.AddShip(group);

            if (group.CelestialBodyId >= 0)
            {
                var celestialMgr = Current.Game.GetComponent<CelestialBodyManager>();
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

            // 2.5 粗网格移除（此时 group 仍在 groups 字典中，GetGridForGroup 可用）
            var targetGrid = GetGridForGroup(groupId);
            if (targetGrid != null)
                targetGrid.Remove(group);

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

            // 解冻地图 tick（取档完成，恢复时间流动）
            var stationController = op.NewMap?.GetComponent<SpaceStationController>();
            if (stationController != null && stationController.Frozen)
                stationController.Unfreeze();

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

            DVector3 oldGroupPos = group.CurrentOrbit.position;

            group.CurrentOrbit = newOrbit;
            group.OrbitDirty = false;

            // 更新粗网格中的群组位置
            var targetGrid = GetGridForGroup(groupId);
            if (targetGrid != null)
                targetGrid.Update(group, oldGroupPos, newOrbit.position);

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

            var timeScaleManager = Current.Game?.GetComponent<TimeScaleManager>();
            int currentTick = Find.TickManager.TicksGame;
            
            if (currentTick - lastUpdateTick >= UPDATE_INTERVAL_TICKS)
            {
                lastUpdateTick = currentTick;
                UpdateAllMovements(timeScaleManager);
            }
            
            if (timeScaleManager != null && timeScaleManager.IsCombatMode)
            {
                UpdateCombatInterpolation(currentTick, timeScaleManager);
            }
            
            foreach (var pg in planetGrids.Values)
                pg.ApplyChanges();
        }

        private struct GroupPhysicsResult
        {
            public int GroupId;
            public bool NeedsUpdate;
            public DVector3 NewPosition;
            public DVector3 NewVelocity;
            public bool HasActiveBurn;
            public double BurnDvConsumed;
            public bool BurnExhausted;
            public bool HasOrbitKeeping;
            public double OrbitKeepingDvCost;
            public DVector3 SyncBodyPos;
            public DVector3 SyncBodyVel;
        }

        private GroupPhysicsResult ComputeGroupPhysics(SpaceEntityGroup group, double dtSeconds, int currentTick, double physicsTick, CelestialBodyManager celestialMgr, int subSteps = 1)
        {
            var result = new GroupPhysicsResult { GroupId = group.GroupId };

            if (group.HasActiveBurn)
            {
                double burnDv = Math.Min(SpaceEntityGroup.MAX_BURN_ACCEL * dtSeconds, group.remainingBurnDV);
                if (burnDv > 0.0)
                {
                    result.HasActiveBurn = true;
                    result.BurnDvConsumed = burnDv;
                    result.BurnExhausted = (group.remainingBurnDV - burnDv) <= 1e-9;
                    group.CurrentOrbit.velocity += group.burnDirection * burnDv;
                    group.remainingBurnDV -= burnDv;
                }
                else
                {
                    result.BurnExhausted = true;
                }
            }

            if (group.IsCircularOrbit)
            {
                DVector3 newPos = group.GetCircularPositionAtTick(physicsTick);
                if (newPos != group.CurrentOrbit.position)
                {
                    result.NeedsUpdate = true;
                    result.NewPosition = newPos;
                    result.NewVelocity = group.CurrentOrbit.velocity;
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
                        DVector3 bodyPos = celestialMgr.GetBodyLocalPosition(primaryBody.bodyId, currentTick);
                        DVector3 bodyVel = celestialMgr.GetBodyLocalVelocity(primaryBody.bodyId, currentTick);
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
                            currentTick, primaryBody, celestialMgr,
                            group.orbitKeepingWhitelist,
                            out var acc, out var jerk);

                        NBodyIntegrator.ThirdOrderTaylorStep(
                            relPos, relVel,
                            acc, jerk, dtSeconds,
                            out var newPos, out var newVel);

                        if (primaryBody.bodyType != CelestialBodyType.Planet)
                        {
                            DVector3 bodyPosNow = celestialMgr.GetBodyLocalPosition(primaryBody.bodyId, currentTick + UPDATE_INTERVAL_TICKS);
                            DVector3 bodyVelNow = celestialMgr.GetBodyLocalVelocity(primaryBody.bodyId, currentTick + UPDATE_INTERVAL_TICKS);
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
                                currentTick, primaryBody, celestialMgr,
                                group.orbitKeepingWhitelist,
                                out var acc, out var jerk);

                            NBodyIntegrator.ThirdOrderTaylorStep(
                                relPos, relVel,
                                acc, jerk, subDt,
                                out relPos, out relVel);
                        }

                        if (primaryBody.bodyType != CelestialBodyType.Planet)
                        {
                            DVector3 bodyPosNow = celestialMgr.GetBodyLocalPosition(primaryBody.bodyId, currentTick + UPDATE_INTERVAL_TICKS);
                            DVector3 bodyVelNow = celestialMgr.GetBodyLocalVelocity(primaryBody.bodyId, currentTick + UPDATE_INTERVAL_TICKS);
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
                        currentTick, primaryBody, celestialMgr,
                        group.orbitKeepingWhitelist);
                    result.OrbitKeepingDvCost = dvRate * dtSeconds;
                }
            }

            result.SyncBodyPos = GetBodyWorldOffset(group.CelestialBodyId, (int)System.Math.Round(physicsTick));
            result.SyncBodyVel = GetBodyWorldVelOffset(group.CelestialBodyId, (int)System.Math.Round(physicsTick));

            return result;
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
            
            int currentTick = Find.TickManager.TicksGame;
            var celestialMgr = Current.Game.GetComponent<CelestialBodyManager>();
            if (celestialMgr == null) return;

            var physicsResults = new ConcurrentBag<GroupPhysicsResult>();

            Parallel.ForEach(groups.Values, group =>
            {
                if (!group.IsMovable) return;
                var result = ComputeGroupPhysics(group, dtSeconds, currentTick, physicsTick, celestialMgr, subSteps);
                physicsResults.Add(result);
            });

            var groupsToCheck = new List<int>();

            foreach (var result in physicsResults)
            {
                if (!groups.TryGetValue(result.GroupId, out var group)) continue;

                if (result.HasActiveBurn)
                {
                    bool anyExhausted = ConsumeGroupDeltaV(group, result.BurnDvConsumed);
                    if (anyExhausted || result.BurnExhausted)
                    {
                        group.EndBurn();
                        trackedManeuvers.Remove(group.GroupId);
                        Log.Message(string.Format("[Burn] Group {0} active burn completed.", group.GroupId));
                    }
                }
                else if (result.BurnExhausted)
                {
                    group.EndBurn();
                    trackedManeuvers.Remove(group.GroupId);
                }

                if (result.NeedsUpdate)
                {
                    var newOrbit = new OrbitState
                    {
                        position = result.NewPosition,
                        velocity = result.NewVelocity,
                        rotation = group.CurrentOrbit.rotation
                    };
                    UpdateGroupOrbit(group.GroupId, newOrbit);
                }

                if (result.HasOrbitKeeping && result.OrbitKeepingDvCost > 0.0)
                {
                    bool anyExhausted = ConsumeGroupDeltaV(group, result.OrbitKeepingDvCost);
                    if (anyExhausted)
                    {
                        group.ExitOrbitKeeping();
                        Log.Message(string.Format("[OrbitKeeping] Group {0} dV exhausted, exited orbit-keeping mode.", group.GroupId));
                    }
                }

                group.localPosition = group.CurrentOrbit.position - result.SyncBodyPos;
                group.localVelocity = group.CurrentOrbit.velocity - result.SyncBodyVel;

                groupsToCheck.Add(group.GroupId);
            }

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

                // 射弹在飞：保持 Combat（1/60）。降速由 CombatManager.ManageProjectileTimeScale 负责，
                // 这里不恢复期望流速，避免与降速逻辑打架（否则每 240 tick 会把 Combat 拉回 Fast）。
                var combatMgr = Current.Game?.GetComponent<CombatManager>();
                if (combatMgr != null && combatMgr.ActiveProjectiles.Count > 0)
                {
                    // 射弹在飞，维持 Combat 档
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
                }
            }

            // ── 执行到期机动 ──
            foreach (var group in groups.Values)
            {
                if (group.plannedManeuvers.Count == 0)
                    continue;

                var nextManeuver = group.PeekNextManeuver((int)Math.Round(physicsTick));
                if (nextManeuver == null)
                    continue;

                var pm = nextManeuver.Value;

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

                DVector3 dV = OrbitMath.GetWorldDeltaV(
                    relPos2, relVel2,
                    pm.radialDV, pm.tangentialDV, pm.normalDV);

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

                group.StartBurn(dV);
                group.PopExecutedManeuver();
                // 标记追踪表：机动已开始推进
                if (trackedManeuvers.ContainsKey(group.GroupId))
                    trackedManeuvers[group.GroupId] = new ManeuverTracker { ExecutePhysicsTick = physicsTick, IsActive = true };

                int remaining = group.plannedManeuvers.Count;
                Log.Message(string.Format("[Maneuver] Group {0} started gradual burn! dv={1:F1} m/s remaining={2}",
                    group.GroupId, dV.magnitude, remaining));
            }
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
            if (primaryBody.bodyType != CelestialBodyType.Planet)
            {
                DVector3 bodyPos = celestialMgr.GetBodyLocalPosition(primaryBody.bodyId, currentTick);
                DVector3 bodyVel = celestialMgr.GetBodyLocalVelocity(primaryBody.bodyId, currentTick);
                relPos = currentPos - bodyPos;
                relVel = currentVel - bodyVel;
            }
            else
            {
                relPos = currentPos;
                relVel = currentVel;
            }

            NBodyIntegrator.MultiBodyAccelJerk(relPos, relVel,
                currentTick, primaryBody, celestialMgr,
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

        private bool ConsumeGroupDeltaV(SpaceEntityGroup group, double totalCost)
        {
            int chargeCount = 0;
            foreach (int eid in group.GetEntityIds())
            {
                if (entities.TryGetValue(eid, out var e) && e.RemainingDeltaV > 0f)
                    chargeCount++;
            }

            if (chargeCount == 0) return true;

            double costPerEntity = totalCost / chargeCount;
            bool anyExhausted = false;

            foreach (int eid in group.GetEntityIds())
            {
                if (!entities.TryGetValue(eid, out var e) || e.RemainingDeltaV <= 0f)
                    continue;

                float cost = (float)costPerEntity;
                if (e.RemainingDeltaV >= cost)
                {
                    e.RemainingDeltaV -= cost;
                }
                else
                {
                    e.RemainingDeltaV = 0f;
                    anyExhausted = true;
                }
            }

            return anyExhausted;
        }

        #endregion

        #endregion

        #region 持久化

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref entities, "entities", LookMode.Value, LookMode.Deep);
            Scribe_Collections.Look(ref groups, "groups", LookMode.Value, LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                planetGrids = new Dictionary<int, SpatialGrid3D<ISpatialEntity>>();
                foreach (var group in groups.Values)
                {
                    var targetGrid = GetGridForGroup(group.GroupId);
                    if (targetGrid != null)
                        targetGrid.Add(group, group.Position);
                }
            }
        }

        #endregion
    }
}
