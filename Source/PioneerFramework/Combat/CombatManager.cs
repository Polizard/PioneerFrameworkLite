using System.Collections.Generic;
using UnityEngine;
using Verse;
using System.Text;
using System;

namespace PioneerFramework.Combat
{
    public class CombatManager : GameComponent
    {
        private readonly List<ProjectileInstance> activeProjectiles = new List<ProjectileInstance>();
        // 群组武器列表（Lite 裁剪后：实体级武器收集已删除，此容器由 M4 Map 炮塔桥接填充）
        private readonly Dictionary<int, List<WeaponInstance>> groupWeapons = new Dictionary<int, List<WeaponInstance>>();
        private readonly HashSet<SpaceEntityGroup> projectileQueryResults = new HashSet<SpaceEntityGroup>();
        // 定向能射线候选集（每炮一次收集复用，方案 A 正方体粗筛 / B 走格预留共用）
        private readonly HashSet<SpaceEntityGroup> rayCandidateResults = new HashSet<SpaceEntityGroup>();
        // 命中诊断：已记录过的射弹 id（每个射弹只打印一次）
        private readonly HashSet<int> hitDiagnosticLogged = new HashSet<int>();
        private readonly Dictionary<int, DVector3> shipLastPositions = new Dictionary<int, DVector3>();
        // #4：SyncAllShipsToGrid 的本 tick 存活船缓冲（复用，避免每 2 tick 分配 HashSet）
        private readonly HashSet<int> aliveScratch = new HashSet<int>();

        // 激光射线（仅视觉残留，不参与物理模拟）
        private readonly List<LaserBeamInstance> activeLaserBeams = new List<LaserBeamInstance>();
        internal IReadOnlyList<LaserBeamInstance> ActiveLaserBeams => activeLaserBeams;

        // 爆炸视觉残留（仅视觉，物理效果即时结算）
        private readonly List<ExplosionInstance> activeExplosions = new List<ExplosionInstance>();
        internal IReadOnlyList<ExplosionInstance> ActiveExplosions => activeExplosions;

        // 船-船碰撞参数（可配置）──
        public const float CollisionExplosionSpeedThreshold = 50f;   // m/s：低于此值按撞击等效（动量交换），高于触发真爆炸
        public const float CollisionRestitution = 0f;                // 弹性系数（撞击等效用，默认完全非弹性）
        public const double CollisionExplosionBaseRadius = 8000.0;   // 爆炸基础半径 m
        public const double CollisionExplosionRadiusK = 2.0;         // R = base + K·E^(1/3)
        public const double CollisionExplosionDamageK = 0.005;       // D0 = K·√E（中心损伤，随距离衰减）
        public const double CollisionImpulseK = 500.0;               // 爆炸径向冲量 Δv = K·√E·falloff/mass

        // 碰撞性能统计（仅 DEBUG 日志使用）
        private readonly System.Diagnostics.Stopwatch collisionStopwatch = new System.Diagnostics.Stopwatch();
        private double collisionMsAccum = 0;
        private int collisionSamples = 0;

        public TargetingSystem TargetingSystem { get; private set; }
        public bool IsBattleActive => activeProjectiles.Count > 0 || TargetingSystem != null;

        internal IReadOnlyList<ProjectileInstance> ActiveProjectiles => activeProjectiles;

        private int lastLogTick = -1;
        private const int LOG_INTERVAL = 120;

        // 船-船碰撞扫掠膨胀盒：保存"上一次 SyncAllShipsToGrid 读到的 group 位置"（上一步）。
        // 检测时用「当前 AABB ∪ 上一步 AABB」并集，覆盖两步间运动路径，消除大步长 tunneling
        // （离散采样点全部落在重叠窗口外导致穿模）。组件 tick 顺序不保证 → 用上次 sync 位置，
        // 两种顺序下每个物理步运动段都会被并集覆盖一次，仅响应延迟不同（0 或 2 tick）。
        private readonly Dictionary<int, DVector3> shipPrevPositions = new Dictionary<int, DVector3>();

        // #2 性能优化：CheckShipCollisions 本 tick 组级缓存（位置/半径/当前盒/上一步盒每组只算一次，
        // 消除每对重复的 GetWorldAabb 旋转计算——原先每对最多 6 次）
        private readonly Dictionary<int, GroupCollisionCache> groupCollisionCache = new Dictionary<int, GroupCollisionCache>();

        /// <summary>船对碰撞检测的组级缓存条目（每 tick 重建）。</summary>
        private struct GroupCollisionCache
        {
            public DVector3 pos;
            public double radius;
            public DVector3 curMin;
            public DVector3 curMax;
            public bool hasPrev;
            public DVector3 prevPos;
            public DVector3 prevMin;
            public DVector3 prevMax;
        }

        /// <summary>详细碰撞日志开关（调试测试场景置 true；正式玩法保持 false）。</summary>
        public static bool DebugDetailedCollision = false;
        private int detailedLogNextTick = 0;
        private int detailPairNextTick = 0;

        /// <summary>炮塔战斗全链详细日志开关（开火/出膛/视觉弹/命中/贯穿，调试测试场景自动开启）。</summary>
        public static bool DebugDetailedCombat = false;
        /// <summary>射弹飞行状态探针间隔（tick，0=关）：每 N tick 打印全部在飞射弹位置/速度/寿命。</summary>
        public static int DebugProjectileProbeInterval = 0;
        private int probeNextTick;

#if DEBUG
        // 圆轨缓存统计增量基准（验证 per-tick 懒缓存：每组每 tick 应只 miss 1 次）
        private long lastPosCacheHits, lastPosCacheMisses, lastVelCacheHits, lastVelCacheMisses;
        // 战斗阶段耗时 + 事件计数（每 LOG_INTERVAL 汇总输出，用于日志分析）
        private readonly System.Diagnostics.Stopwatch stageStopwatch = new System.Diagnostics.Stopwatch();
        private double projMsAccum, weaponsMsAccum, syncMsAccum;
        private long projSpawnedTotal, projDestroyedTotal;
        private long lastProjSpawned, lastProjDestroyed, lastCellCross;
#endif

        // 射弹在飞 → 自动降速到 1/60（Combat 档）。限频防抖：每 60 tick 最多请求一次。
        private int lastProjectileSlowdownTick = -9999;
        private const int PROJECTILE_SLOWDOWN_MIN_INTERVAL = 60;

        private DVector3 lastHitPosition;
        private int lastHitGroupId = -1;
        private int lastHitTick = -1;
        private WeaponType lastHitWeaponType;
        public DVector3 LastHitPosition => lastHitPosition;
        public int LastHitGroupId => lastHitGroupId;
        public int LastHitTick => lastHitTick;
        public WeaponType LastHitWeaponType => lastHitWeaponType;
        public bool HasRecentHit => lastHitTick >= 0 && Find.TickManager.TicksGame - lastHitTick < 30;

        public CombatManager(Game game) { }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            int currentTick = Find.TickManager.TicksGame;

            // 清理过期激光射线
            for (int i = activeLaserBeams.Count - 1; i >= 0; i--)
            {
                activeLaserBeams[i].remainingTicks--;
                if (activeLaserBeams[i].remainingTicks <= 0)
                    activeLaserBeams.RemoveAt(i);
            }

            // 清理过期爆炸视觉
            for (int i = activeExplosions.Count - 1; i >= 0; i--)
            {
                activeExplosions[i].remainingTicks--;
                if (activeExplosions[i].remainingTicks <= 0)
                    activeExplosions.RemoveAt(i);
            }

            if (currentTick % 2 != 0) return;

            var combatGrid = GetCombatGrid();
            if (combatGrid != null)
                combatGrid.DrainEmptyCells();

#if DEBUG
            stageStopwatch.Restart();
#endif
            UpdateProjectiles();
            // 射弹飞行状态探针：每 N tick 打印全部在飞射弹（验证出膛点/弹道/寿命）
            if (DebugProjectileProbeInterval > 0 && currentTick >= probeNextTick)
            {
                probeNextTick = currentTick + DebugProjectileProbeInterval;
                LogProjectileProbe();
            }
#if DEBUG
            stageStopwatch.Stop();
            projMsAccum += stageStopwatch.Elapsed.TotalMilliseconds;
            stageStopwatch.Restart();
#endif
            UpdateWeapons(currentTick);
#if DEBUG
            stageStopwatch.Stop();
            weaponsMsAccum += stageStopwatch.Elapsed.TotalMilliseconds;
            stageStopwatch.Restart();
#endif
            SyncAllShipsToGrid();
#if DEBUG
            stageStopwatch.Stop();
            syncMsAccum += stageStopwatch.Elapsed.TotalMilliseconds;
#endif

            collisionStopwatch.Restart();
            CheckShipCollisions();
            collisionStopwatch.Stop();
            collisionMsAccum += collisionStopwatch.Elapsed.TotalMilliseconds;
            collisionSamples++;
            ManageProjectileTimeScale();
            LogGridStats(currentTick);
        }

        /// <summary>
        /// 射弹飞行状态探针：单行汇总全部在飞射弹的位置/速度/距生成点/寿命。
        /// 由 DebugProjectileProbeInterval 驱动（调试炮塔对射时观察出膛点→目标全弹道）。
        /// </summary>
        private void LogProjectileProbe()
        {
            if (activeProjectiles.Count == 0) return;
            var sb = new StringBuilder();
            sb.Append(string.Format("[射弹探针] physicsTick={0:F1} 在飞={1}",
                TimeScaleManager.GetPhysicsTickSafe(), activeProjectiles.Count));
            foreach (var p in activeProjectiles)
            {
                double flown = (p.position - p.spawnPosition).magnitude;
                sb.Append(string.Format(" | proj{0}(G{1}): pos=({2:F0},{3:F0},{4:F0}) v=({5:F0},{6:F0},{7:F0}) 速率={8:F0}m/s 距生成点={9:F0}m 飞行={10:F1}s 剩余={11:F1}s",
                    p.id, p.sourceGroupId,
                    p.position.x, p.position.y, p.position.z,
                    p.velocity.x, p.velocity.y, p.velocity.z,
                    p.velocity.magnitude, flown, p.totalFlightSeconds, p.remainingLife));
            }
            Log.Message(sb.ToString());
        }

        /// <summary>
        /// 射弹在飞时自动将时间流速降到 1/60（Combat 档），实现"子弹时间"。
        /// 只降不升：恢复由 SpaceGameComp 的流速检查（每 240 tick）处理，避免射弹清空/再生反复切换。
        /// 限频（每 60 tick 最多请求一次）：覆盖用户手动切走高流速的情况，同时避免 QueueCommand 刷屏。
        /// </summary>
        private void ManageProjectileTimeScale()
        {
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            if (tsm == null) return;

            if (activeProjectiles.Count > 0 &&
                tsm.CurrentLevel != TimeScaleLevel.Combat &&
                Find.TickManager.TicksGame - lastProjectileSlowdownTick >= PROJECTILE_SLOWDOWN_MIN_INTERVAL)
            {
                lastProjectileSlowdownTick = Find.TickManager.TicksGame;
                tsm.QueueCommand(TimeScaleCommand.ToCombat());
            }
        }

        private CombatGrid GetCombatGrid()
        {
            return Current.Game?.GetComponent<CombatGrid>();
        }

        private SpaceGameComp GetSpaceGameComp()
        {
            return Current.Game?.GetComponent<SpaceGameComp>();
        }

        internal DVector3 GetGroupPos(SpaceEntityGroup group)
        {
            if (group.IsCircularOrbit)
                return group.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return group.CurrentOrbit.position;
        }

        private void SyncAllShipsToGrid()
        {
            var combatGrid = GetCombatGrid();
            var spaceGame = GetSpaceGameComp();
            if (combatGrid == null || spaceGame == null) return;

            HashSet<int> aliveThisTick = aliveScratch;
            aliveThisTick.Clear();

            foreach (var group in spaceGame.GetAllGroups())
            {
                if (!group.IsActive || !group.IsCollidable) continue;

                int gid = group.GroupId;
                aliveThisTick.Add(gid);
                DVector3 currentPos = GetGroupPos(group);

                // 粗网格位置保鲜（定向能 raycast 8 大区块粗筛依赖）：圆轨船位置是解析函数，
                // 不顺手同步则粗网格永久停在入轨点；大区块 1000km、2 tick 移动 ~5km，跨格罕见 O(1)。
                spaceGame.SyncGroupToCoarseGrid(gid, currentPos);

                if (!shipLastPositions.TryGetValue(gid, out var lastPos))
                {
                    combatGrid.AddShip(group);
                    shipPrevPositions.Remove(gid);
                }
                else
                {
                    // 扫掠膨胀盒：保留覆盖前的旧值作为"上一步位置"（供 CheckShipCollisions 并集检测）
                    shipPrevPositions[gid] = lastPos;
                    if (!lastPos.Equals(currentPos))
                        combatGrid.UpdateShipPosition(group, lastPos);
                }

                shipLastPositions[gid] = currentPos;
            }

            List<int> toRemove = null;
            foreach (var gid in shipLastPositions.Keys)
            {
                if (!aliveThisTick.Contains(gid))
                {
                    if (toRemove == null) toRemove = new List<int>();
                    toRemove.Add(gid);
                }
            }
            if (toRemove != null)
            {
                foreach (var gid in toRemove)
                {
                    // 船销毁/失活：从细网格移除，防止覆盖格泄漏
                    var g = spaceGame.GetGroupById(gid);
                    if (g != null)
                        combatGrid.RemoveShip(g);
                    shipLastPositions.Remove(gid);
                    shipPrevPositions.Remove(gid);
                }
            }
        }

        private void UpdateProjectiles()
        {
            var spaceGame = GetSpaceGameComp();
            var combatGrid = GetCombatGrid();
            if (spaceGame == null) return;

            for (int i = activeProjectiles.Count - 1; i >= 0; i--)
            {
                var proj = activeProjectiles[i];

                // deltaTime 用 physicsTick 差值（游戏世界秒，1 tick = 1 秒），与飞船物理同基准。
                // 射弹生成于任意 tick（可能非物理步），首次更新只走实际经过时间，避免射弹比
                // 目标（连续轨道运动）超前 1 物理步，否则相对弹道方向被扭曲、擦肩而过。
                // 战斗模式（1/60）下 physicsTick 差值与真实秒相等，行为不变；Normal/Fast 档位
                // 下射弹与飞船同速推进，不再出现射弹被船甩开 60 倍的问题。
                double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();
                double deltaTime = physicsTickNow - proj.prevPhysicsTick;
                if (deltaTime <= 0.0)
                    deltaTime = (Find.TickManager.TicksGame - proj.lastUpdateRealTick) / 60.0;
                // 物理严格基于 physicsTick：deltaTime = physicsTick 差值，不做任何截断。
                // 射弹位置 = spawnPos + velocity×Δt 是 physicsTick 的连续函数，配合渲染精确外推
                // 即得平滑轨迹；人为 clamp 步长反而造成物理点与渲染外推错位 → 折线。
                proj.prevUpdatePhysicsTick = proj.prevPhysicsTick; // 记录上一步（碰撞检测回退用，prevPhysicsTick 随后被覆盖）
                proj.prevPhysicsTick = physicsTickNow;

                DVector3 gravity = DVector3.zero;
                if (proj.affectedByGravity && combatGrid != null)
                    gravity = combatGrid.ComputeProjectileGravity(proj.position, proj.systemBodyId);

                proj.prevUpdateRealTick = proj.lastUpdateRealTick;
                proj.lastUpdateRealTick = Find.TickManager.TicksGame;
                proj.UpdatePosition((float)deltaTime, gravity);

                if (!proj.IsAlive())
                {
                    // ── 调试日志：超时脱靶汇总（验证时间基准 B1 与提前量 A1）──
                    double flightDist = (proj.position - proj.spawnPosition).magnitude;
                    Log.Message(string.Format(
                        "[射弹脱靶] proj={0} 超时销毁 | 生成=({1:F0},{2:F0},{3:F0}) 终点=({4:F0},{5:F0},{6:F0}) 飞行距离={7:F0}m 累计游戏秒={8:F2}s 平均速度={9:F0}m/s 末速={10:F0}m/s physicsTick={11:F1}",
                        proj.id,
                        proj.spawnPosition.x, proj.spawnPosition.y, proj.spawnPosition.z,
                        proj.position.x, proj.position.y, proj.position.z,
                        flightDist, proj.totalFlightSeconds,
                        proj.totalFlightSeconds > 0.001 ? flightDist / proj.totalFlightSeconds : 0.0,
                        proj.velocity.magnitude,
                        TimeScaleManager.GetPhysicsTickSafe()));
                    combatGrid?.RemoveProjectile(proj);
                    spaceGame.UnregisterProjectileFromGrid(proj);
                    activeProjectiles.RemoveAt(i);
#if DEBUG
                    projDestroyedTotal++;
#endif
                    continue;
                }

                if (combatGrid != null)
                {
                    combatGrid.UpdateProjectileCell(proj, proj.prevPosition, proj.position);
                }

                CheckProjectileCollision(proj, spaceGame);
            }
        }

        private void CheckProjectileCollision(ProjectileInstance proj, SpaceGameComp spaceGame)
        {
            var combatGrid = GetCombatGrid();
            if (combatGrid == null) return;

            DVector3 startPos = proj.prevPosition;
            DVector3 endPos = proj.position;

            // 起点格必须用 prev 位置（射弹移动前的格），不能用 proj.fineCx——
            // 它已被 UpdateProjectileCell 更新为新格，会导致旧格（含目标船）漏检。
            var startCell = CombatGrid.WorldToFineCell(startPos);
            combatGrid.GetShipsNearProjectile(proj.systemBodyId, startCell, startPos, endPos,
                out var nextCell, projectileQueryResults);

            foreach (var group in projectileQueryResults)
            {
                if (!group.IsActive || !group.IsCollidable) continue;
                if (group.GroupId == proj.sourceGroupId) continue;

                // 命中诊断：射弹接近目标但未命中时，打印一次关键数据定位漏检
                if (!hitDiagnosticLogged.Contains(proj.id))
                {
                    DVector3 gPos = GetGroupPos(group);
                    double dist = (gPos - proj.position).magnitude;
                    if (dist < 800.0)
                    {
                        hitDiagnosticLogged.Add(proj.id);
                        Log.Message(string.Format("[命中诊断] proj={0} group={1} 查询数={2} TicksGame={3} physicsTick={4:F0} 距离={5:F0}m | 线段({6:F0},{7:F0})->({8:F0},{9:F0}) | 目标=({10:F0},{11:F0}) | 射弹速=({12:F0},{13:F0},{14:F0})",
                            proj.id, group.GroupId, projectileQueryResults.Count,
                            Find.TickManager.TicksGame,
                            TimeScaleManager.GetPhysicsTickSafe(), dist,
                            proj.prevPosition.x, proj.prevPosition.z,
                            proj.position.x, proj.position.z,
                            gPos.x, gPos.z,
                            proj.velocity.x, proj.velocity.y, proj.velocity.z));
                    }
                }

                bool hit = CheckSegmentGroupHit(proj, group);
                if (hit)
                {
                    combatGrid.RemoveProjectile(proj);
                    spaceGame.UnregisterProjectileFromGrid(proj);
                    activeProjectiles.Remove(proj);
                    break;
                }
            }

            proj.fineCx = nextCell.cx;
            proj.fineCy = nextCell.cy;
            proj.fineCz = nextCell.cz;
        }

        private bool CheckSegmentGroupHit(ProjectileInstance proj, SpaceEntityGroup group)
        {
            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null) return false;

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();

            // 目标参考系碰撞检测：射弹与目标同轨高速飞行，绝对坐标系中检测采样间隔内
            // 目标 AABB 相对射弹线段快速掠过（高速弹隧穿，3000m/s 下 2tick 相对位移
            // 100m > AABB 直径 50m，线段两端都在 AABB 外）。
            // 转为目标参考系：目标静止、射弹以相对速度逼近，线段稳定穿过 AABB。
            DVector3 groupPos = GetGroupPos(group);
            // 目标"上一步"位置：用射弹上一步 physicsTick（prevUpdatePhysicsTick）回退 dt，
            // 圆轨用解析函数，非圆轨用当前速度×dt 线性回退。
            // 注意：不能直接用 proj.prevPhysicsTick（在 UpdateProjectiles 中已被覆盖为当前值，
            // dt 恒为 0 导致参考系退化、相对位移算成纯射弹世界位移）。
            double dtPhysics = physicsTick - proj.prevUpdatePhysicsTick;
            DVector3 groupPosPrev = group.IsCircularOrbit
                ? group.GetCircularPositionAtTick(physicsTick - dtPhysics)
                : group.CurrentOrbit.position - group.CurrentOrbit.velocity * dtPhysics;
            DVector3 startPos = proj.prevPosition - groupPosPrev;
            DVector3 endPos = proj.position - groupPos;
            DVector3 worldDir = endPos - startPos;
            double segmentLength = worldDir.magnitude;

            // 防御：相对参考系中无有效位移（射弹生成与首次检测同 tick、或与目标相对静止）时，
            // 零方向线段会让 slab 检测恒命中（tEntry=-Inf tExit=Inf）→ 射弹误判命中任意 group。
            // 跳过本次检测，射弹继续飞行，下一物理步相对位移恢复后再正常判定。
            if (segmentLength < 0.5)
                return false;

#if DEBUG
            // 命中逐步诊断：仅在射弹已触发[命中诊断]（接近目标）后打印，定位检测在哪一步失败
            bool verboseHit = hitDiagnosticLogged.Contains(proj.id);
            if (verboseHit)
                Log.Message(string.Format(
                    "[命中调试] proj={0} group={1} | groupPos=({2:F0},{3:F0},{4:F0}) groupPosPrev=({5:F0},{6:F0},{7:F0}) | start=({8:F0},{9:F0},{10:F0}) end=({11:F0},{12:F0},{13:F0}) | segLen={14:F0}m 射弹速=({15:F0},{16:F0},{17:F0})",
                    proj.id, group.GroupId,
                    groupPos.x, groupPos.y, groupPos.z,
                    groupPosPrev.x, groupPosPrev.y, groupPosPrev.z,
                    startPos.x, startPos.y, startPos.z,
                    endPos.x, endPos.y, endPos.z,
                    segmentLength, proj.velocity.x, proj.velocity.y, proj.velocity.z));
#endif

            foreach (int entityId in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(entityId);
                if (entity == null || !entity.IsCollidable) continue;

                DVector3 entityLocal = entity.Position;   // 目标参考系中实体局部位置（不随时间变）
                DVector3 halfSize = entity.AabbHalfSize;

                // 射弹线段 → 船体局部系（含旋转）：part 局部 = 固定轴对齐盒（地图 1:1 基准）。
                // entity.Rotation 参与命中几何——倾斜/翻滚的船，命中点反映真实姿态。
                DQuaternion rotInv = DQuaternion.Inverse(DQuaternion.FromQuaternion(entity.Rotation));
                DVector3 localStart = DVector3.Rotate(startPos - entityLocal, rotInv);
                DVector3 localEnd = DVector3.Rotate(endPos - entityLocal, rotInv);

                // 粗检测（shipLocal 系，盒中心 = 实体原点）：相对坐标系中目标静止，
                // slab 线段检测是连续几何检测（与线段长度无关），射弹无论多快，
                // 只要相对路径穿过 AABB 就会被捕捉，无需子段化。
                DVector3 min = -halfSize;
                DVector3 max = halfSize;
                if (!CombatGrid.RaySlabIntersection(localStart, localEnd, min, max,
                        out double tEntry, out double tExit, out _))
                {
#if DEBUG
                    if (verboseHit)
                        Log.Message(string.Format("[命中调试] slab MISS tEntry={0:F3} tExit={1:F3}", tEntry, tExit));
#endif
                    continue;
                }

#if DEBUG
                if (verboseHit)
                    Log.Message(string.Format("[命中调试] slab HIT tEntry={0:F3} tExit={1:F3}", tEntry, tExit));
#endif

                // 命中详情（详细日志，Release 下亦生效）：slab 进入/出射参数 + 射弹-目标几何
                if (DebugDetailedCombat)
                {
                    Log.Message(string.Format(
                        "[命中详情] proj={0}(G{1})→G{2} slab HIT tEntry={3:F3} tExit={4:F3} 线段长={5:F0}m 射弹速=({6:F0},{7:F0},{8:F0})m/s 目标pos=({9:F0},{10:F0},{11:F0})",
                        proj.id, proj.sourceGroupId, group.GroupId,
                        tEntry, tExit, segmentLength,
                        proj.velocity.x, proj.velocity.y, proj.velocity.z,
                        groupPos.x, groupPos.y, groupPos.z));
                }

                // Entity AABB 被命中，检查 Part 级碰撞（单 part 化：唯一 part slab 直判，§5.3）。
                // 线段基准 = 粗检测进入点（shipLocal）：part 检测从盒进入点扫盒内区间，
                // 而不是从射弹线段起点扫（起点在盒外时永远 miss）。
                if (entity.Parts.Count > 0 && segmentLength > 0.001)
                {
                    DVector3 localDir = (localEnd - localStart) / segmentLength; // 归一化（旋转保持长度）
                    DVector3 entryPoint = localStart + localDir * (tEntry * segmentLength);
                    double localMaxDist = (tExit - tEntry) * segmentLength;

                    var hitPart = entity.Parts[0];
                    DVector3 partMin = hitPart.localPosition - hitPart.localBoundsSize * 0.5;
                    DVector3 partMax = hitPart.localPosition + hitPart.localBoundsSize * 0.5;
                    bool partHit = CombatGrid.RaySlabIntersection(entryPoint, entryPoint + localDir * localMaxDist,
                        partMin, partMax, out double partTEntry, out double partTExit, out _);
                    // partTEntry 可为微负：盒入口恰在 part 盒表面时浮点抖动（±1e-7 级）或起点已在盒内。
                    // 命中参数取 max(0, ·)——否则"起点在盒内"被误判 miss，直射 AABB 表面时交替命中/未命中。
                    // RaySlabIntersection 已保证 tFar>=0 且区间与 [0,1] 有交集，去掉 >=0 检查安全。
                    partHit = partHit && partTEntry <= localMaxDist;
                    double partEnterT = partTEntry < 0 ? 0 : partTEntry;
#if DEBUG
                    if (verboseHit)
                        Log.Message(string.Format(
                            "[命中调试] PartSlab origin=({0:F0},{1:F0},{2:F0}) dir=({3:F3},{4:F3},{5:F3}) maxDist={6:F0} partHit={7} t={8:F3}",
                            entryPoint.x, entryPoint.y, entryPoint.z,
                            localDir.x, localDir.y, localDir.z, localMaxDist, partHit, partTEntry));
#endif
                    if (partHit)
                    {
                        var firstHitPartIndex = 0;
                        var firstHitT = partEnterT;
                        DVector3 entityWorldPos = groupPos + entity.Position;
                        lastHitPosition = proj.position;
                        lastHitGroupId = group.GroupId;
                        lastHitTick = Find.TickManager.TicksGame;
                        lastHitWeaponType = proj.weaponDef.weaponType;
                        Log.Message(string.Format(
                            "[命中Part!] Tick={8} 射弹{0}→Group{1} Entity{2} Part[{3}]\"{4}\" " +
                            "hitT={5:F3} PartHP={6:F0}→{7:F0} 飞行秒={18:F2} " +
                            "EntityPos=({9:F0},{10:F0},{11:F0}) PartLocalPos=({12:F0},{13:F0},{14:F0}) PartSize=({15:F0},{16:F0},{17:F0})",
                            proj.id, group.GroupId, entityId, firstHitPartIndex, hitPart.defName,
                            firstHitT, hitPart.hp, hitPart.hp - proj.damage,
                            lastHitTick,
                            entityWorldPos.x, entityWorldPos.y, entityWorldPos.z,
                            hitPart.localPosition.x, hitPart.localPosition.y, hitPart.localPosition.z,
                            hitPart.localBoundsSize.x, hitPart.localBoundsSize.y, hitPart.localBoundsSize.z,
                            proj.totalFlightSeconds));
                        // 应用伤害（走组件回调：OnDamaged / OnDestroyed）
                        hitPart.ApplyDamage(proj.damage);
                        if (hitPart.isDestroyed)
                        {
                            entity.MarkPartDestroyed(hitPart.partId);
                            Log.Message(string.Format("[Part摧毁] Entity{0} Part[{1}]\"{2}\" 已被摧毁",
                                entityId, firstHitPartIndex, hitPart.defName));
                        }

                        // F3 命中 → Map 落点瞬爆：命中点已在船体局部系（含旋转），
                        // part 局部 = 地图 1:1，直接映射即可。
                        if (entity.MapId >= 0)
                        {
                            Map targetMap = SpaceMapGenerator.FindPocketMap(entity.MapId);
                            if (targetMap != null)
                            {
                                DVector3 hitLocal = entryPoint + localDir * (partEnterT * localMaxDist);
                                if (DebugDetailedCombat)
                                {
                                    DVector3 penDir = hitLocal - entryPoint;
                                    Log.Message(string.Format(
                                        "[命中详情] proj={0}→G{1} Entity{2} partT={3:F3} 船壳入口局部=({4:F1},{5:F1},{6:F1}) 命中局部=({7:F1},{8:F1},{9:F1})m 贯穿长度={10:F1}m 贯穿方向=({11:F3},{12:F3},{13:F3})",
                                        proj.id, group.GroupId, entityId, partTEntry,
                                        entryPoint.x, entryPoint.y, entryPoint.z,
                                        hitLocal.x, hitLocal.y, hitLocal.z,
                                        penDir.magnitude,
                                        penDir.x / Math.Max(penDir.magnitude, 0.001),
                                        penDir.y / Math.Max(penDir.magnitude, 0.001),
                                        penDir.z / Math.Max(penDir.magnitude, 0.001)));
                                }
                                SpawnPenetrationMote(entity, targetMap, hitLocal);
                            }
                        }
                        return true;
                    }
                }
                else
                {
                    // 无 Part 数据，回退为 Entity AABB 命中即算
                    lastHitPosition = proj.position;
                    lastHitGroupId = group.GroupId;
                    lastHitTick = Find.TickManager.TicksGame;
                    lastHitWeaponType = proj.weaponDef.weaponType;
                    Log.Message(string.Format("[命中!] 射弹击中 Entity {0} (Group {1}) 位置({2:F0},{3:F0},{4:F0})",
                        entityId, group.GroupId, groupPos.x, groupPos.y, groupPos.z));
                    return true;
                }
            }

            return false;
        }

        // ════════════════════════════════════════════════════
        //  船-船碰撞（多格注册 + 速度分层：撞击等效 / 真爆炸）
        // ════════════════════════════════════════════════════

        private void CheckShipCollisions()
        {
            var spaceGame = GetSpaceGameComp();
            var combatGrid = GetCombatGrid();
            if (spaceGame == null || combatGrid == null) return;

            // #2：每 tick 重建组级缓存
            groupCollisionCache.Clear();

            foreach (var (a, b) in combatGrid.EnumerateShipPairs())
            {
                if (!a.IsActive || !b.IsActive || !a.IsCollidable || !b.IsCollidable) continue;
                if (a.GroupId == b.GroupId) continue;

                // 枚举对诊断：确认 G1/G2/G3 是否被枚举到（限频）
                if (DebugDetailedCollision && Find.TickManager.TicksGame >= detailPairNextTick)
                {
                    detailPairNextTick = Find.TickManager.TicksGame + 30;
                    DVector3 pa0 = GetGroupPos(a);
                    DVector3 pb0 = GetGroupPos(b);
                    Log.Message(string.Format("[碰撞枚举] t={0} 对 G{1}-G{2} 距离={3:F1}m",
                        Find.TickManager.TicksGame, a.GroupId, b.GroupId, (pb0 - pa0).magnitude));
                }

                // #2 组级缓存：每组本 tick 只算一次（位置/球半径/当前盒/上一步盒），每对直接复用
                var ca = GetOrBuildCollisionCache(a, spaceGame);
                var cb = GetOrBuildCollisionCache(b, spaceGame);
                DVector3 pa = ca.pos;
                DVector3 pb = cb.pos;

                // 球包围快筛
                double ra = ca.radius;
                double rb = cb.radius;
                double rr = ra + rb;
                if ((pb - pa).sqrMagnitude > rr * rr) continue;

                // 当前实际盒（curOverlap 判定与分离量的基准）
                DVector3 curAMin = ca.curMin, curAMax = ca.curMax;
                DVector3 curBMin = cb.curMin, curBMax = cb.curMax;

                // 扫掠膨胀盒：当前 AABB ∪ 上一步 AABB（覆盖两步间运动路径，消除 tunneling）
                DVector3 aMin = curAMin, aMax = curAMax;
                DVector3 bMin = curBMin, bMax = curBMax;
                DVector3 prevPa = ca.hasPrev ? ca.prevPos : pa; // 无上一步记录时并集 = 当前盒（首拍退化）
                if (ca.hasPrev)
                {
                    aMin = new DVector3(Math.Min(aMin.x, ca.prevMin.x), Math.Min(aMin.y, ca.prevMin.y), Math.Min(aMin.z, ca.prevMin.z));
                    aMax = new DVector3(Math.Max(aMax.x, ca.prevMax.x), Math.Max(aMax.y, ca.prevMax.y), Math.Max(aMax.z, ca.prevMax.z));
                }
                DVector3 prevPb = cb.hasPrev ? cb.prevPos : pb;
                if (cb.hasPrev)
                {
                    bMin = new DVector3(Math.Min(bMin.x, cb.prevMin.x), Math.Min(bMin.y, cb.prevMin.y), Math.Min(bMin.z, cb.prevMin.z));
                    bMax = new DVector3(Math.Max(bMax.x, cb.prevMax.x), Math.Max(bMax.y, cb.prevMax.y), Math.Max(bMax.z, cb.prevMax.z));
                }

                bool sweptHit = AabbOverlap(aMin, aMax, bMin, bMax, out double ox, out double oy, out double oz);
                if (DebugDetailedCollision && Find.TickManager.TicksGame >= detailedLogNextTick)
                {
                    detailedLogNextTick = Find.TickManager.TicksGame + 30;
                    Log.Message(string.Format(
                        "[碰撞详细] t={0} G{1}扫掠盒=({2:F0},{3:F0},{4:F0})-({5:F0},{6:F0},{7:F0}) " +
                        "G{8}扫掠盒=({9:F0},{10:F0},{11:F0})-({12:F0},{13:F0},{14:F0}) " +
                        "距离={15:F1}m prevG{16}=({17:F0},{18:F0},{19:F0}) prevG{20}=({21:F0},{22:F0},{23:F0}) 重叠={24}",
                        Find.TickManager.TicksGame,
                        a.GroupId, aMin.x, aMin.y, aMin.z, aMax.x, aMax.y, aMax.z,
                        b.GroupId, bMin.x, bMin.y, bMin.z, bMax.x, bMax.y, bMax.z,
                        (pb - pa).magnitude,
                        a.GroupId, prevPa.x, prevPa.y, prevPa.z,
                        b.GroupId, prevPb.x, prevPb.y, prevPb.z,
                        sweptHit ? string.Format("{0:F1},{1:F1},{2:F1}m", ox, oy, oz) : "无"));
                }
                if (!sweptHit) continue;

                // 纯 tunneling（当前盒不重叠但路径相交）→ 快速船停在对方膨胀盒表面；
                // 否则用当前实际盒重叠量分离（扫掠盒 oz 含运动路径长度，直接分离会把船推飞）
                if (!AabbOverlap(curAMin, curAMax, curBMin, curBMax, out ox, out oy, out oz))
                {
                    ResolveTunneled(b, prevPb, curBMin, curBMax, curAMin, curAMax, a.GroupId);
                    InvalidateCollisionCache(a, b); // B 位置已改变
                    continue;
                }

                DVector3 nDir = pb - pa;
                double nLen = nDir.magnitude;
                if (nLen < 1e-6)
                    nDir = new DVector3(1, 0, 0);
                else
                    nDir /= nLen;

                DVector3 va = GetGroupVelocity(a);
                DVector3 vb = GetGroupVelocity(b);
                double vRel = DVector3.Dot(va - vb, nDir); // >0 正在接近

                // 跳帮接舷分支（§9）：一方是跳帮单位且低速对准落点 → 消费碰撞（不弹飞/不爆炸，
                // 降落判定由 BoardingManager 每物理步驱动）。高速 → 走正常爆炸。
                if (Current.Game?.GetComponent<BoardingManager>() is BoardingManager boarding
                    && boarding.TryResolveBoardingContact(a, b, vRel))
                {
                    InvalidateCollisionCache(a, b); // 跳帮单位位置可能改变
                    continue;
                }

                if (vRel >= CollisionExplosionSpeedThreshold)
                {
                    TriggerExplosion(a, b, pa, pb, va, vb, nDir, vRel, ox, oy, oz);
                }
                else if (vRel > 0)
                {
                    ResolveImpact(a, b, pa, pb, va, vb, nDir, vRel, ox, oy, oz);
                }
                else
                {
                    // 正在分离但 AABB 仍重叠：仅位置分离防卡死
                    SeparatePositions(a, b, ox, oy, oz);
                    Log.Message(string.Format("[ShipCollision] G{0} vs G{1} 分离中重叠，仅位置分离 重叠=({2:F0},{3:F0},{4:F0})m",
                        a.GroupId, b.GroupId, ox, oy, oz));
                }
                // 任何碰撞解决都可能改变 a/b 位置 → 失效缓存，后续对该组的对重建
                InvalidateCollisionCache(a, b);
            }
        }

        /// <summary>本 tick 组级碰撞缓存：每组只算一次位置/球半径/当前盒/上一步盒，每对重复时直接复用。</summary>
        private GroupCollisionCache GetOrBuildCollisionCache(SpaceEntityGroup group, SpaceGameComp spaceGame)
        {
            if (groupCollisionCache.TryGetValue(group.GroupId, out var c))
                return c;

            DVector3 pos = GetGroupPos(group);
            double radius = GetGroupRadius(group);
            GetGroupWorldAabb(group, pos, spaceGame, out var curMin, out var curMax);
            var cache = new GroupCollisionCache
            {
                pos = pos,
                radius = radius,
                curMin = curMin,
                curMax = curMax
            };
            if (shipPrevPositions.TryGetValue(group.GroupId, out var prevPos))
            {
                cache.hasPrev = true;
                cache.prevPos = prevPos;
                GetGroupWorldAabb(group, prevPos, spaceGame, out var pMin, out var pMax);
                cache.prevMin = pMin;
                cache.prevMax = pMax;
            }
            groupCollisionCache[group.GroupId] = cache;
            return cache;
        }

        /// <summary>碰撞解决（改变位置/销毁）后使 a/b 的组缓存失效，下次访问重建。</summary>
        private void InvalidateCollisionCache(SpaceEntityGroup a, SpaceEntityGroup b)
        {
            groupCollisionCache.Remove(a.GroupId);
            groupCollisionCache.Remove(b.GroupId);
        }

        /// <summary>
        /// 纯 tunneling 响应：B 的当前盒与 A 盒不重叠，但扫掠路径相交（B 一步从 A 一侧穿到另一侧）。
        /// 用「B 中心射线 vs A 膨胀盒（+B 半宽）」求接触时刻，把 B 精确停在接触点（留 0.1m 余量防嵌入）。
        /// 若无法求交（无上一步记录）→ 退回上一步位置保守处理。
        /// </summary>
        private void ResolveTunneled(SpaceEntityGroup b, DVector3 prevB, DVector3 curBMin, DVector3 curBMax,
            DVector3 aMin, DVector3 aMax, int aGroupId)
        {
            DVector3 curCenter = (curBMin + curBMax) * 0.5;
            if (TryComputeTunneledStop(prevB, curCenter, curBMin, curBMax, aMin, aMax, out var stopPos))
            {
                ApplyTunneledStop(b, stopPos, aGroupId);
                return;
            }
            if (shipPrevPositions.TryGetValue(b.GroupId, out var pbPrev))
                ApplyTunneledStop(b, pbPrev, aGroupId);
        }

        /// <summary>
        /// 计算 tunneling 停点：B 中心从 prev 直线移动到 cur（一步位移 dir），
        /// 与「A 盒膨胀 B 半宽」（B 中心不可进入区）求交，停在其表面（留 0.1m 余量）。
        /// 纯静态，便于单元测试验证物理正确性。
        /// </summary>
        internal static bool TryComputeTunneledStop(DVector3 prevCenter, DVector3 curCenter,
            DVector3 curBMin, DVector3 curBMax, DVector3 aMin, DVector3 aMax, out DVector3 stopPos)
        {
            stopPos = default;
            DVector3 dir = curCenter - prevCenter; // 实际位移向量：t 即位移比例 [0,1]
            double dirLen = dir.magnitude;
            if (dirLen <= 0.01) return false; // 静止/无位移 → 无法求交
            DVector3 halfB = (curBMax - curBMin) * 0.5;
            DVector3 expandMin = aMin - halfB;
            DVector3 expandMax = aMax + halfB;
            if (!RayAabb(prevCenter, dir, expandMin, expandMax, out double tHit)) return false;
            double tStop = Math.Max(0.0, tHit - 0.1 / dirLen); // 留 0.1m 余量防嵌入
            stopPos = prevCenter + dir * tStop;
            return true;
        }

        private void ApplyTunneledStop(SpaceEntityGroup b, DVector3 stopPos, int aGroupId)
        {
            // 圆轨组的位置是解析的（GetCircularPositionAtTick），改 CurrentOrbit.position 无效；
            // 必须退出圆轨后停位才能生效（否则 tunneling 对圆轨目标无效，快速船照样穿过）
            if (b.IsCircularOrbit)
                b.ExitCircularOrbit();
            b.CurrentOrbit.position = stopPos;
            b.OrbitDirty = true;
            shipPrevPositions[b.GroupId] = stopPos;
            shipLastPositions[b.GroupId] = stopPos;
            Log.Message(string.Format("[ShipCollision] G{0} vs G{1} tunneling 捕获：G{1} 停于接触点 ({2:F0},{3:F0},{4:F0})m",
                aGroupId, b.GroupId, stopPos.x, stopPos.y, stopPos.z));
        }

        /// <summary>slab 法射线-AABB 求交：origin + t·dir（t∈[0,1]，dir 为实际位移向量 → t 即位移比例）。</summary>
        internal static bool RayAabb(DVector3 origin, DVector3 dir, DVector3 bmin, DVector3 bmax, out double tHit)
        {
            tHit = 0.0;
            double tmin = 0.0, tmax = 1.0; // 只关心当前物理步内的命中
            if (Math.Abs(dir.x) < 1e-12)
            {
                if (origin.x < bmin.x || origin.x > bmax.x) return false;
            }
            else
            {
                double t1 = (bmin.x - origin.x) / dir.x;
                double t2 = (bmax.x - origin.x) / dir.x;
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                tmin = Math.Max(tmin, t1);
                tmax = Math.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
            if (Math.Abs(dir.y) < 1e-12)
            {
                if (origin.y < bmin.y || origin.y > bmax.y) return false;
            }
            else
            {
                double t1 = (bmin.y - origin.y) / dir.y;
                double t2 = (bmax.y - origin.y) / dir.y;
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                tmin = Math.Max(tmin, t1);
                tmax = Math.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
            if (Math.Abs(dir.z) < 1e-12)
            {
                if (origin.z < bmin.z || origin.z > bmax.z) return false;
            }
            else
            {
                double t1 = (bmin.z - origin.z) / dir.z;
                double t2 = (bmax.z - origin.z) / dir.z;
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                tmin = Math.Max(tmin, t1);
                tmax = Math.Min(tmax, t2);
                if (tmin > tmax) return false;
            }
            tHit = tmin;
            return true;
        }

        /// <summary>轴对齐盒重叠：三轴重叠量均 > 0 即重叠。</summary>
        internal static bool AabbOverlap(DVector3 aMin, DVector3 aMax, DVector3 bMin, DVector3 bMax,
            out double ox, out double oy, out double oz)
        {
            ox = Math.Min(aMax.x, bMax.x) - Math.Max(aMin.x, bMin.x);
            oy = Math.Min(aMax.y, bMax.y) - Math.Max(aMin.y, bMin.y);
            oz = Math.Min(aMax.z, bMax.z) - Math.Max(aMin.z, bMin.z);
            return ox > 0 && oy > 0 && oz > 0;
        }

        private double GetGroupRadius(SpaceEntityGroup group)
        {
            var spaceGame = GetSpaceGameComp();
            double r = 1000.0;
            if (spaceGame == null) return r;
            foreach (int eid in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(eid);
                if (entity == null) continue;
                // 旋转后包围盒的球半径（覆盖倾斜姿态下的最远角点）
                entity.GetWorldAabb(out var eMin, out var eMax);
                double er = (eMax - eMin).magnitude * 0.5;
                if (er > r) r = er;
            }
            return r;
        }

        private void GetGroupWorldAabb(SpaceEntityGroup group, DVector3 groupPos, SpaceGameComp spaceGame,
            out DVector3 min, out DVector3 max)
        {
            min = groupPos;
            max = groupPos;
            bool has = false;
            foreach (int eid in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(eid);
                if (entity == null || !entity.IsCollidable) continue;
                // 旋转后的世界轴对齐包围盒（覆盖倾斜姿态）
                entity.GetWorldAabb(out var relMin, out var relMax);
                DVector3 eMin = groupPos + entity.Position + relMin;
                DVector3 eMax = groupPos + entity.Position + relMax;
                if (!has) { min = eMin; max = eMax; has = true; }
                else
                {
                    min = new DVector3(Math.Min(min.x, eMin.x), Math.Min(min.y, eMin.y), Math.Min(min.z, eMin.z));
                    max = new DVector3(Math.Max(max.x, eMax.x), Math.Max(max.y, eMax.y), Math.Max(max.z, eMax.z));
                }
            }
        }

        /// <summary>
        /// 获取 Group 当前世界系速度。圆轨返回解析切向速度（懒缓存）；绕卫星（月球）时补中心天体公转速度。
        /// </summary>
        internal DVector3 GetGroupVelocity(SpaceEntityGroup group)
        {
            if (group.IsCircularOrbit)
            {
                double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
                return group.GetCircularVelocityAtTick(physicsTick);
            }
            return group.CurrentOrbit.velocity;
        }

        private double GetGroupMass(SpaceEntityGroup group)
        {
            var spaceGame = GetSpaceGameComp();
            double m = 0.0;
            if (spaceGame != null)
            {
                foreach (int eid in group.GetEntityIds())
                {
                    var entity = spaceGame.GetEntityById(eid);
                    if (entity == null) continue;
                    m += entity.TotalMass;
                }
            }
            return Math.Max(m, 1.0);
        }

        /// <summary>
        /// 速度冲量（碰撞动量交换/爆炸径向）入合冲量累积器：物理步首由 SpaceGameComp
        /// 统一施加到速度。任何速度修正都需先退出圆轨（圆轨是解析位置，速度修正会被覆盖）；
        /// 位置分离（防穿透）保持即时，不在此处理。
        /// </summary>
        private void ApplyGroupVelocity(SpaceEntityGroup group, DVector3 newVel)
        {
            if (group.IsCircularOrbit)
                group.ExitCircularOrbit();
            DVector3 delta = newVel - group.CurrentOrbit.velocity;
            if (delta.sqrMagnitude <= 0.0) return;
            group.pendingImpulse += delta;
        }

        /// <summary>
        /// AABB 位置分离：三轴同时推开（各推 overlap/2），一次收敛。
        /// 只推最短轴会导致其他轴仍重叠、下一帧重复触发（刷屏 + 重复冲量）。
        /// </summary>
        private void SeparatePositions(SpaceEntityGroup a, SpaceEntityGroup b, double ox, double oy, double oz)
        {
            if (a.IsCircularOrbit) a.ExitCircularOrbit();
            if (b.IsCircularOrbit) b.ExitCircularOrbit();

            DVector3 push = new DVector3(ox * 0.5, oy * 0.5, oz * 0.5);
            a.CurrentOrbit.position -= push;
            b.CurrentOrbit.position += push;

            a.OrbitDirty = true;
            b.OrbitDirty = true;

            // 分离后立即刷新扫掠基准：否则下拍并集含"分离前位置"，会误报重叠并反复分离。
            // 必须同时更新 shipLastPositions，否则下个 SyncAllShipsToGrid 会用分离前的旧值
            // 覆盖 shipPrevPositions（扫掠并集又含分离前位置）。
            shipPrevPositions[a.GroupId] = a.CurrentOrbit.position;
            shipPrevPositions[b.GroupId] = b.CurrentOrbit.position;
            shipLastPositions[a.GroupId] = a.CurrentOrbit.position;
            shipLastPositions[b.GroupId] = b.CurrentOrbit.position;
        }

        /// <summary>低速撞击等效：e=0 完全非弹性动量交换 + 位置分离，无损伤。</summary>
        private void ResolveImpact(SpaceEntityGroup a, SpaceEntityGroup b, DVector3 pa, DVector3 pb,
            DVector3 va, DVector3 vb, DVector3 nDir, double vRel, double ox, double oy, double oz)
        {
            double ma = GetGroupMass(a);
            double mb = GetGroupMass(b);
            double e = CollisionRestitution;
            double j = -(1.0 + e) * vRel / (1.0 / ma + 1.0 / mb);

            ApplyGroupVelocity(a, va + nDir * (j / ma));
            ApplyGroupVelocity(b, vb - nDir * (j / mb));
            SeparatePositions(a, b, ox, oy, oz);

            Log.Message(string.Format("[ShipCollision] 撞击等效 G{0} vs G{1} 接近速度={2:F1}m/s 质量={3:F0}/{4:F0}kg 冲量j={5:F2} 已分离",
                a.GroupId, b.GroupId, vRel, ma, mb, j));
        }

        /// <summary>高速真爆炸：范围损伤 + 径向冲量 + 爆炸视觉。</summary>
        private void TriggerExplosion(SpaceEntityGroup a, SpaceEntityGroup b,
            DVector3 pa, DVector3 pb, DVector3 va, DVector3 vb, DVector3 nDir, double vRel,
            double ox, double oy, double oz)
        {
            double ma = GetGroupMass(a);
            double mb = GetGroupMass(b);
            double mEff = ma * mb / (ma + mb);
            double E = 0.5 * mEff * vRel * vRel;

            DVector3 hitPoint = (pa + pb) * 0.5;
            double R = CollisionExplosionBaseRadius + CollisionExplosionRadiusK * Math.Pow(E, 1.0 / 3.0);
            double D0 = CollisionExplosionDamageK * Math.Sqrt(E);

            int damaged = ApplyExplosionDamage(hitPoint, R, D0);
            int pushed = ApplyExplosionImpulse(hitPoint, R, E);

            // 视觉残留
            activeExplosions.Add(new ExplosionInstance
            {
                position = hitPoint,
                maxRadius = R,
                remainingTicks = 60,
                totalTicks = 60,
                systemBodyId = GetSpaceGameComp()?.ResolveReferenceFrameId(a.CelestialBodyId) ?? -1
            });

            SeparatePositions(a, b, ox, oy, oz);

            Log.Message(string.Format("[爆炸] G{0} vs G{1} 碰撞点=({2:F0},{3:F0},{4:F0}) 半径={5:F0}m 能量={6:F2}J vRel={7:F1}m/s 损伤Part={8} 冲量船={9}",
                a.GroupId, b.GroupId, hitPoint.x, hitPoint.y, hitPoint.z, R, E, vRel, damaged, pushed));
        }

        /// <summary>爆炸范围损伤：半径 R 内所有 Part 按 (1 - dist/R) 衰减扣 HP。</summary>
        private int ApplyExplosionDamage(DVector3 center, double R, double D0)
        {
            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null) return 0;

            int damaged = 0;
            double rangeCheck = R + 100000.0; // 球快筛余量

            foreach (var group in spaceGame.GetAllGroups())
            {
                if (!group.IsActive || !group.IsCollidable) continue;

                DVector3 gpos = GetGroupPos(group);
                if ((gpos - center).sqrMagnitude > rangeCheck * rangeCheck) continue;

                foreach (int eid in group.GetEntityIds())
                {
                    var entity = spaceGame.GetEntityById(eid);
                    if (entity == null) continue;

                    foreach (var part in entity.Parts)
                    {
                        if (part.isDestroyed) continue;
                        DVector3 pw = gpos + entity.Position + part.localPosition;
                        double dist = (pw - center).magnitude;
                        if (dist > R) continue;
                        double dmg = D0 * (1.0 - dist / R);
                        if (dmg <= 0) continue;

                        // 应用伤害（走组件回调：OnDamaged / OnDestroyed）
                        part.ApplyDamage((float)dmg);
                        damaged++;
                        if (part.isDestroyed)
                        {
                            entity.MarkPartDestroyed(part.partId);
                            Log.Message(string.Format("[爆炸损伤] Entity{0} Part[{1}]\"{2}\" HP≤0 被摧毁 dist={3:F0}m",
                                eid, part.partId, part.defName, dist));
                        }
                    }
                }
            }
            return damaged;
        }

        /// <summary>爆炸径向冲量：范围内船被炸飞，Δv = K·√E·(1-dist/R)/mass。</summary>
        private int ApplyExplosionImpulse(DVector3 center, double R, double E)
        {
            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null) return 0;

            int pushed = 0;
            double sqRtE = Math.Sqrt(E);

            foreach (var group in spaceGame.GetAllGroups())
            {
                if (!group.IsActive || !group.IsCollidable) continue;

                DVector3 gpos = GetGroupPos(group);
                DVector3 to = gpos - center;
                double dist = to.magnitude;
                if (dist > R || dist < 1e-6) continue;

                double falloff = 1.0 - dist / R;
                double dv = CollisionImpulseK * sqRtE * falloff / GetGroupMass(group);
                ApplyGroupVelocity(group, GetGroupVelocity(group) + (to / dist) * dv);
                pushed++;
            }
            return pushed;
        }

        private void UpdateWeapons(int currentTick)
        {
            // Lite 裁剪（§5.5）：实体级武器收集（WeaponMountComponentData 链路）已删除，
            // groupWeapons 暂为空容器，等待 M4 的 Map 炮塔桥接新接口填充。
            // 仅对既有武器实例做每 tick 冷却/过热更新。
            var spaceGame = GetSpaceGameComp();
            if (spaceGame != null)
            {
                foreach (var kv in groupWeapons)
                {
                    foreach (var weapon in kv.Value)
                    {
                        weapon.Update(currentTick);
                    }
                }
            }

            TargetingSystem?.Update(currentTick);
        }

        private void LogGridStats(int currentTick)
        {
#if DEBUG
            if (currentTick - lastLogTick < LOG_INTERVAL) return;
            lastLogTick = currentTick;

            StringBuilder sb = new StringBuilder();
            sb.Append("[网格状态] Tick=").Append(currentTick)
              .Append(" physicsTick=").Append(TimeScaleManager.GetPhysicsTickSafe().ToString("F0"));

            var combatGrid = GetCombatGrid();
            if (combatGrid != null)
                combatGrid.DebugPrintStats(sb);

            var spaceGame = GetSpaceGameComp();
            if (spaceGame != null)
            {
                var planetGrids = spaceGame.GetPlanetGrids();
                if (planetGrids != null)
                {
                    sb.Append(" 网格数=").Append(planetGrids.Count);
                    foreach (var kv in planetGrids)
                    {
                        sb.Append(" [体").Append(kv.Key).Append("]");
                        kv.Value.DebugPrintStats(sb);
                    }
                }
            }

            sb.Append(" 射弹=").Append(activeProjectiles.Count);
            if (activeProjectiles.Count > 0)
            {
                for (int i = 0; i < Mathf.Min(3, activeProjectiles.Count); i++)
                {
                    var proj = activeProjectiles[i];
                    sb.Append(" [G").Append(proj.sourceGroupId);
                    sb.Append(" 命=").Append(proj.remainingLife.ToString("F1"));
                    sb.Append(" 速=").Append(proj.velocity.magnitude.ToString("F0"));
                    sb.Append(" 格(").Append(proj.fineCx).Append(",")
                      .Append(proj.fineCy).Append(",").Append(proj.fineCz).Append(")");
                    sb.Append("]");
                }
                if (activeProjectiles.Count > 3)
                    sb.Append(" ...+").Append(activeProjectiles.Count - 3);
            }

            // 第一条船的格子坐标
            if (spaceGame != null)
            {
                int shown = 0;
                foreach (var g in spaceGame.GetAllGroups())
                {
                    if (!g.IsActive || !g.IsCollidable) continue;
                    if (shown >= 1) break;
                    DVector3 p = GetGroupPos(g);
                    var cell = CombatGrid.WorldToFineCell(p);
                    sb.Append(" 船G").Append(g.GroupId)
                      .Append(" 位(").Append((p.x/1000).ToString("F0")).Append(",")
                      .Append((p.y/1000).ToString("F0")).Append(",")
                      .Append((p.z/1000).ToString("F0")).Append(")km")
                      .Append(" 格(").Append(cell.cx).Append(",")
                      .Append(cell.cy).Append(",").Append(cell.cz).Append(")");
                    shown++;
                }
            }

            if (collisionSamples > 0)
            {
                sb.Append(" 阶段耗时[弹=").Append((projMsAccum / collisionSamples).ToString("F3"))
                  .Append(" 武=").Append((weaponsMsAccum / collisionSamples).ToString("F3"))
                  .Append(" 同步=").Append((syncMsAccum / collisionSamples).ToString("F3"))
                  .Append(" 碰=").Append((collisionMsAccum / collisionSamples).ToString("F3"))
                  .Append("ms/次]");
                projMsAccum = 0;
                weaponsMsAccum = 0;
                syncMsAccum = 0;
                collisionMsAccum = 0;
                collisionSamples = 0;
            }

            // 事件计数（增量：射弹生/灭、船跨格）
            long dSpawned = projSpawnedTotal - lastProjSpawned;
            long dDestroyed = projDestroyedTotal - lastProjDestroyed;
            long dCross = combatGrid != null ? combatGrid.debugCellCrossCount - lastCellCross : 0;
            lastProjSpawned = projSpawnedTotal;
            lastProjDestroyed = projDestroyedTotal;
            if (combatGrid != null) lastCellCross = combatGrid.debugCellCrossCount;
            sb.Append(" 弹生=").Append(dSpawned)
              .Append(" 弹灭=").Append(dDestroyed)
              .Append(" 跨格=").Append(dCross);

            // 圆轨解析缓存统计（增量：仅本次 LOG_INTERVAL 内的命中/未命中）
            long posHits = 0, posMisses = 0, velHits = 0, velMisses = 0;
            if (spaceGame != null)
            {
                foreach (var g in spaceGame.GetAllGroups())
                {
                    if (!g.IsCircularOrbit) continue;
                    posHits += g.debugPosCacheHits;
                    posMisses += g.debugPosCacheMisses;
                    velHits += g.debugVelCacheHits;
                    velMisses += g.debugVelCacheMisses;
                }
            }
            long dPosHits = posHits - lastPosCacheHits;
            long dPosMisses = posMisses - lastPosCacheMisses;
            long dVelHits = velHits - lastVelCacheHits;
            long dVelMisses = velMisses - lastVelCacheMisses;
            lastPosCacheHits = posHits;
            lastPosCacheMisses = posMisses;
            lastVelCacheHits = velHits;
            lastVelCacheMisses = velMisses;
            if (dPosHits + dPosMisses + dVelHits + dVelMisses > 0)
            {
                sb.Append(" 圆轨缓存: 位置命中=").Append(dPosHits).Append(" 未命中=").Append(dPosMisses)
                  .Append(" 速度命中=").Append(dVelHits).Append(" 未命中=").Append(dVelMisses);
            }

            Log.Message(sb.ToString());
#endif
        }

        private const double ENGAGEMENT_RANGE_METERS = 1000000.0;

        public bool CanEngage(int groupIdA, int groupIdB)
        {
            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null) return false;

            var a = spaceGame.GetGroupById(groupIdA);
            var b = spaceGame.GetGroupById(groupIdB);
            if (a == null || b == null) return false;

            double dist = (GetGroupPos(a) - GetGroupPos(b)).magnitude;
            return dist <= ENGAGEMENT_RANGE_METERS;
        }

        public void EngageGroups(int groupIdA, int groupIdB)
        {
            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null) return;

            var a = spaceGame.GetGroupById(groupIdA);
            var b = spaceGame.GetGroupById(groupIdB);
            if (a == null || b == null) return;

            if (!CanEngage(groupIdA, groupIdB))
            {
                Log.Warning("[战斗系统] 距离过远，无法发起战斗");
                return;
            }

            if (TargetingSystem == null)
                TargetingSystem = new TargetingSystem(this);

            Log.Message(string.Format("[战斗系统] Group {0} 与 Group {1} 进入战斗", groupIdA, groupIdB));
        }

        public void EndBattle()
        {
            var combatGrid = GetCombatGrid();
            combatGrid?.Clear();

            TargetingSystem = null;

            Log.Message("[战斗系统] 战斗结束");
        }

        public void InitializeGroupWeapons(int groupId, List<WeaponInstance> weapons)
        {
            groupWeapons[groupId] = weapons;
        }

        /// <summary>
        /// 按索引获取 Group 的第 N 把武器（供调试窗口使用；战斗逻辑请直接传 WeaponInstance 引用）。
        /// </summary>
        public WeaponInstance GetGroupWeapon(int groupId, int weaponIndex)
        {
            if (!groupWeapons.TryGetValue(groupId, out var weapons)) return null;
            if (weaponIndex < 0 || weaponIndex >= weapons.Count) return null;
            return weapons[weaponIndex];
        }

        /// <summary>获取 Group 的全部武器列表（供调试/UI 展示）。</summary>
        public List<WeaponInstance> GetGroupWeapons(int groupId)
        {
            if (groupWeapons.TryGetValue(groupId, out var weapons))
                return weapons;

            // Lite 裁剪（§5.5）：实体级武器收集已删除；M4 前返回空列表（Map 炮塔桥接填充后立即可用）
            return new List<WeaponInstance>();
        }

        /// <param name="targetVelocity">目标当前世界系速度（用于提前量计算）。null = 瞄准当前位置。</param>
        public void FireWeapon(int groupId, WeaponInstance weapon, DVector3 targetPosition, DVector3? spawnPosition = null, DVector3? targetVelocity = null)
        {
            if (weapon == null)
            {
                Log.Warning(string.Format("[FireWeapon] Group {0} 武器为空", groupId));
                return;
            }
            if (!weapon.CanFire())
            {
                Log.Warning(string.Format("[FireWeapon] 武器无法开火: overheated={0} cooldown={1} ammo={2}",
                    weapon.isOverheated, weapon.cooldownRemaining, weapon.ammo));
                return;
            }

            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null)
            {
                Log.Warning("[FireWeapon] SpaceGameComp 未找到");
                return;
            }
            var group = spaceGame.GetGroupById(groupId);
            if (group == null)
            {
                Log.Warning(string.Format("[FireWeapon] Group {0} 未找到", groupId));
                return;
            }

            DVector3 spawnPos;
            if (spawnPosition.HasValue)
            {
                spawnPos = spawnPosition.Value;
            }
            else
            {
                spawnPos = GetGroupPos(group);
            }

            // 发射者当前世界系速度（射弹继承 + 提前量解算共用）
            DVector3 groupVelocity = GetGroupVelocity(group);

            // 瞄准：射线瞬时命中瞄准当前位置；射弹带简单提前量（相对运动二次方程）
            DVector3 aimPos = targetPosition;
            if (weapon.def.weaponType != WeaponType.Ray && targetVelocity.HasValue && weapon.def.projectileSpeed > 0f)
                aimPos = ComputeLeadAimPoint(spawnPos, groupVelocity, targetPosition, targetVelocity.Value, weapon.def.projectileSpeed);

            // 出膛方向有效性检查：目标与炮口重合（<1m）时 aimDelta 为零向量，
            // direction.normalized 为 0 → muzzle=0 → 生成"卡膛弹"（射弹无出膛速度，只有船速）。
            // 跳过本次开火（不消耗冷却），避免物理荒谬的卡膛弹。
            DVector3 aimDelta = aimPos - spawnPos;
            if (aimDelta.sqrMagnitude < 1.0)
            {
                Log.Message(string.Format("[FireWeapon] Group {0} 目标与炮口重合（<1m），跳过开火避免卡膛弹", groupId));
                return;
            }
            DVector3 direction = aimDelta.normalized;

            weapon.Fire();

            if (weapon.def.weaponType == WeaponType.Ray)
            {
                FireRayWeapon(group, weapon, groupId, spawnPos, direction);
                return;
            }

            DVector3 muzzleVelocity = direction * weapon.def.projectileSpeed;

            var proj = new ProjectileInstance
            {
                sourceGroupId = groupId,
                factionId = group.FactionId,
                weaponDef = weapon.def,
                position = spawnPos,
                velocity = groupVelocity + muzzleVelocity,
                remainingLife = weapon.def.projectileLifetime / 60f,
                damage = weapon.def.damagePerShot,
                armorPenetration = weapon.def.armorPenetration,
                affectedByGravity = weapon.def.affectedByGravity,
                prevPosition = spawnPos,
                spawnPosition = spawnPos,
                spawnPhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                lastUpdateRealTick = Find.TickManager.TicksGame,
                prevUpdateRealTick = Find.TickManager.TicksGame,
                // 上一步物理更新时刻必须初始化：默认 0 会让首步命中检测的 dtPhysics 变成
                // 巨大负数（physicsTick 为负）→ 目标参考系回退到轨道 t=0 → 误判。
                prevPhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                prevUpdatePhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                systemBodyId = spaceGame.ResolveReferenceFrameId(group.CelestialBodyId)
            };

            activeProjectiles.Add(proj);
#if DEBUG
            projSpawnedTotal++;
#endif

            // 射弹生成瞬间立即进入 Combat 档（子弹时间）：排队降速有 ≤2 tick 边界延迟，
            // Normal 档窗口射弹 6.5km/步 瞬移，观感"物理断裂"。立即切换让射弹全程平滑。
            Current.Game?.GetComponent<TimeScaleManager>()?.SetCombatModeImmediate();

            var combatGrid = GetCombatGrid();
            if (combatGrid != null)
                combatGrid.AddProjectile(proj);

            // 射弹入粗网格
            spaceGame.RegisterProjectileToGrid(proj);

            DVector3 totalVel = groupVelocity + muzzleVelocity;

            // ── 完整调试日志：生成位置 / 瞄准提前量 / 速度继承分解 ──
            double leadDist = (aimPos - targetPosition).magnitude;
            double physicsTickNow = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 centralVel = DVector3.zero;
            if (group.IsCircularOrbit && group.circularCentralBodyId >= 0)
            {
                var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
                if (mgr != null)
                {
                    var cd = mgr.GetBodyById(group.circularCentralBodyId);
                    if (cd != null && cd.bodyType != CelestialBodyType.Planet)
                        centralVel = mgr.GetBodyLocalVelocity(group.circularCentralBodyId, physicsTickNow);
                }
            }
            double vRelMag = targetVelocity.HasValue ? (targetVelocity.Value - groupVelocity).magnitude : 0.0;
            Log.Message(string.Format(
                "[射弹生成] proj={0} Group {1} TicksGame={2} physicsTick={3:F1} 位置=({4:F0},{5:F0},{6:F0}) 目标当前=({7:F0},{8:F0},{9:F0}) 瞄准点=({10:F0},{11:F0},{12:F0}) 提前量={13:F0}m",
                proj.id, groupId, Find.TickManager.TicksGame, physicsTickNow,
                spawnPos.x, spawnPos.y, spawnPos.z,
                targetPosition.x, targetPosition.y, targetPosition.z,
                aimPos.x, aimPos.y, aimPos.z, leadDist));
            Log.Message(string.Format(
                "  └ 继承速度: 圆轨={0} 中心天体速度=({1:F1},{2:F1},{3:F1}) 轨道速={4:F0} 炮口速={5:F0} 合速={6:F0}m/s 提前量时间≈{7:F1}s 相对速度={8:F0}m/s",
                group.IsCircularOrbit ? "是" : "否",
                centralVel.x, centralVel.y, centralVel.z,
                groupVelocity.magnitude, muzzleVelocity.magnitude, totalVel.magnitude,
                leadDist / Math.Max(weapon.def.projectileSpeed, 1f), vRelMag));

            // 开火详情（详细日志）：出膛点相对船心偏移 = 炮口在船体 AABB 表面的位置（验证出膛点生成）
            if (DebugDetailedCombat)
            {
                DVector3 groupPosNow = GetGroupPos(group);
                DVector3 offset = spawnPos - groupPosNow;
                Log.Message(string.Format(
                    "  └ [开火详情] 炮口相对船心偏移=({0:F0},{1:F0},{2:F0})m 出膛方向=({3:F3},{4:F3},{5:F3}) 出膛速度=({6:F0},{7:F0},{8:F0})m/s 继承船速=({9:F0},{10:F0},{11:F0})m/s",
                    offset.x, offset.y, offset.z,
                    direction.x, direction.y, direction.z,
                    muzzleVelocity.x, muzzleVelocity.y, muzzleVelocity.z,
                    groupVelocity.x, groupVelocity.y, groupVelocity.z));
            }
        }

        /// <summary>
        /// Map 炮塔开火入口（M4-F2 桥接）。复用 FireWeapon 全链路（提前量 / 卡膛检查 / 射弹生成 / 入格）。
        /// spawnPos 为炮塔在世界系的炮口位置（由 MapCoordMapper 换算），targetVelocity 供提前量解算。
        /// </summary>
        public void FireFromMapBuilding(int groupId, WeaponInstance weapon, DVector3 spawnPos, DVector3 targetPos, DVector3? targetVelocity = null)
        {
            FireWeapon(groupId, weapon, targetPos, spawnPos, targetVelocity);
        }

        /// <summary>
        /// 3D 命中 → Map 落点视觉弹。
        /// hitLocalMeters 为**船体局部系**命中点（CheckSegmentGroupHit / FireRayWeapon 已含 entity.Rotation 逆旋转），
        /// part 局部 = 地图 1:1，直接映射到地图（落点反映"击中倾斜船体的真实部位"）。
        /// entryLocalMeters 为可选贯通入口（船体局部系）：传入时从入口贯穿到落点（1~2 tick 极速，
        /// 定向能"光束切进船体"）；不传时瞬爆（F3 射弹命中，现状不动）。
        /// </summary>
        private void SpawnPenetrationMote(SpaceEntity entity, Map map, DVector3 hitLocalMeters, DVector3? entryLocalMeters = null)
        {
            if (entity == null || map == null) return;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Projectile_Shell");
            if (def == null) return;

            Vector3 pos = MapCoordMapper.ShipLocalToMapWorld(map,
                new Vector3((float)hitLocalMeters.x, (float)hitLocalMeters.y, (float)hitLocalMeters.z));
            pos.y = 0f; // 落点恒在地面（爆炸层）
            pos.x = Mathf.Clamp(pos.x, 0f, map.Size.x - 1f);
            pos.z = Mathf.Clamp(pos.z, 0f, map.Size.z - 1f);

            var proj = (PFProjectile3D)ThingMaker.MakeThing(def);
            if (proj == null) return;
            IntVec3 cell = pos.ToIntVec3();
            if (!cell.InBounds(map)) cell = map.Center; // 防御

            // 定向能贯通：入口 = 命中点沿入射方向反方向延长到船体 AABB 六面（Map 边缘），
            // 从入口贯穿到落点（1~2 tick 极速，视觉是"一条光束切进船体"）。
            if (entryLocalMeters.HasValue)
            {
                Vector3 entry = MapCoordMapper.ShipLocalToMapWorld(map,
                    new Vector3((float)entryLocalMeters.Value.x, (float)entryLocalMeters.Value.y, (float)entryLocalMeters.Value.z));
                entry.y = Mathf.Max(entry.y, 0f); // 入口在地图下层时夹到地面（与出膛弹同规则）
                entry.x = Mathf.Clamp(entry.x, 0f, map.Size.x - 1f);
                entry.z = Mathf.Clamp(entry.z, 0f, map.Size.z - 1f);

                float dist = (entry - pos).magnitude;
                float speed = Mathf.Max(2f, dist / 2f); // 1~2 tick 贯穿

                // 贯穿生成详情（详细日志）：入口/命中局部 → Map 场景 → 落点格 全链路映射
                if (DebugDetailedCombat)
                {
                    IntVec3 c0 = map.Center;
                    Log.Message(string.Format(
                        "[贯穿生成] Entity{0} 入口局部=({1:F1},{2:F1},{3:F1})m 命中局部=({4:F1},{5:F1},{6:F1})m → Map场景 入口=({7:F1},{8:F1},{9:F1}) 落点=({10:F1},{11:F1},{12:F1}) → 落点格=({13},{14}) 地图中心=({15},{16}) 地图尺寸={17}×{18}",
                        entity.EntityId,
                        entryLocalMeters.Value.x, entryLocalMeters.Value.y, entryLocalMeters.Value.z,
                        hitLocalMeters.x, hitLocalMeters.y, hitLocalMeters.z,
                        entry.x, entry.y, entry.z,
                        pos.x, pos.y, pos.z,
                        cell.x, cell.z, c0.x, c0.z, map.Size.x, map.Size.z));
                }

                GenSpawn.Spawn(proj, cell, map);
                proj.Launch3D(entry, pos, cell, speedOverride: speed);
                return;
            }

            // 瞬爆（F3 射弹命中）：入口 = 落点 = 命中点（船体局部映射格），距离 0 → 1 tick 后 ImpactSomething
            // → Impact 造成落点建筑伤害 + 爆闪视觉。
            // 贯穿生成详情（详细日志）：命中局部 → Map 场景 → 落点格 全链路映射
            if (DebugDetailedCombat)
            {
                IntVec3 c0 = map.Center;
                Log.Message(string.Format(
                    "[贯穿生成] Entity{0} 命中局部=({1:F1},{2:F1},{3:F1})m → Map场景=({4:F1},{5:F1},{6:F1}) → 落点格=({7},{8}) 地图中心=({9},{10}) 地图尺寸={11}×{12}",
                    entity.EntityId,
                    hitLocalMeters.x, hitLocalMeters.y, hitLocalMeters.z,
                    pos.x, pos.y, pos.z,
                    cell.x, cell.z, c0.x, c0.z, map.Size.x, map.Size.z));
            }

            GenSpawn.Spawn(proj, cell, map);
            proj.Launch3D(pos, pos, cell);
        }

        /// <summary>
        /// 简单提前量：在相对坐标系（射手静止）中解 |r0 + vRel·t| = muzzleSpeed·t 的二次方程，
        /// 返回目标提前量瞄准点（世界系）。无解（目标逃脱/弹速不足）时回退瞄准当前位置。
        /// </summary>
        private DVector3 ComputeLeadAimPoint(DVector3 shooterPos, DVector3 shooterVel, DVector3 targetPos, DVector3 targetVel, double muzzleSpeed)
        {
            DVector3 r0 = targetPos - shooterPos;
            DVector3 vRel = targetVel - shooterVel;
            double r0Sq = r0.sqrMagnitude;
            if (r0Sq < 1e-6) return targetPos;

            double a = vRel.sqrMagnitude - muzzleSpeed * muzzleSpeed;
            double b = 2.0 * DVector3.Dot(r0, vRel);
            double c = r0Sq;

            double t = 0.0;
            if (System.Math.Abs(a) < 1e-9)
            {
                if (System.Math.Abs(b) < 1e-9) return targetPos;
                t = -c / b;
            }
            else
            {
                double disc = b * b - 4.0 * a * c;
                if (disc < 0.0) return targetPos; // 目标逃脱，无法命中
                double sqrtDisc = System.Math.Sqrt(disc);
                double t1 = (-b - sqrtDisc) / (2.0 * a);
                double t2 = (-b + sqrtDisc) / (2.0 * a);
                if (t1 > 0.0) t = t1;
                else if (t2 > 0.0) t = t2;
                else return targetPos;
            }

            if (t <= 0.0) return targetPos;
            // 相对坐标系正确外推：射弹从射手当前位置沿 (r0 + vRel·t) 方向出膛，
            // 射手自身速度已在 vRel 中扣除（射弹飞行中射手也在移动）。
            // 原实现 targetPos + targetVel·t 用绝对速度外推，共轨场景（船速≈弹速量级）
            // 会偏出 目标AABB（如 2.4km/s×0.27s≈650m >> 30m AABB）导致必然脱靶。
            return targetPos + vRel * t;
        }

        /// <summary>
        /// 激光武器：立即射线命中检测，不生成射弹（定向能，即时命中无需提前量）。
        /// 候选（A）= 战斗区块正方体粗筛 + 射程球内过滤（B 走格预留）；精确判定 = 当前时刻
        /// 世界系 slab → 最小 tEntry（遮挡语义：只命中最近一艘）→ 旋转正确版 part 检测。
        /// </summary>
        private void FireRayWeapon(SpaceEntityGroup group, WeaponInstance weapon, int groupId, DVector3 spawnPos, DVector3 direction)
        {
            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null) return;

            DVector3 startPos = spawnPos;
            DVector3 dir = direction;
            double beamLength = weapon.def.maxRange; // Ray 语义：光束长度（Def 与 orbitalEngageRange 对齐）
            DVector3 endPos = startPos + dir * beamLength;
            int systemBodyId = spaceGame.ResolveReferenceFrameId(group.CelestialBodyId);

            Log.Message(string.Format("[激光发射] Group {0} 起点=({1:F0},{2:F0},{3:F0}) 方向=({4:F3},{5:F3},{6:F3}) 射程={7:F0}m",
                groupId, startPos.x, startPos.y, startPos.z,
                dir.x, dir.y, dir.z, beamLength));

            // 候选收集：A 正方体粗筛（当前）；B 线段走格（预留切换点，见 CollectRayCandidates）
            CollectRayCandidates(groupId, systemBodyId, startPos, endPos, beamLength, rayCandidateResults);

            // 候选收集详情（详细日志）：粗筛后候选船数 + 每艘距离（验证正方体粗筛 + 射程球过滤）
            if (DebugDetailedCombat)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[激光候选] G").Append(groupId).Append(" 射程=").Append(beamLength.ToString("F0"))
                  .Append("m 候选船数=").Append(rayCandidateResults.Count);
                if (rayCandidateResults.Count > 0)
                {
                    sb.Append(" 列表:");
                    foreach (var cg in rayCandidateResults)
                    {
                        if (cg == null) continue;
                        double cd = (GetGroupPos(cg) - startPos).magnitude;
                        sb.Append(" G").Append(cg.GroupId).Append('@').Append((cd / 1000.0).ToString("F1")).Append("km");
                    }
                }
                Log.Message(sb.ToString());
            }

            // 立即射线命中检测：候选集 slab 求交 → 取最近 tEntry（遮挡语义）
            bool hit = false;
            double closestT = double.MaxValue;
            SpaceEntity hitEntity = null;
            SpaceEntityGroup hitGroup = null;
            int hitPartIndex = -1;
            DVector3 hitLocal = DVector3.zero;     // 命中点（船体局部系，含旋转）
            DVector3 entryLocal = DVector3.zero;   // 船壳入口（船体局部系，贯通视觉弹入口）

            foreach (var targetGroup in rayCandidateResults)
            {
                if (!targetGroup.IsActive || !targetGroup.IsCollidable) continue;
                if (targetGroup.GroupId == groupId) continue;

                DVector3 targetGroupPos = GetGroupPos(targetGroup);

                foreach (int entityId in targetGroup.GetEntityIds())
                {
                    var entity = spaceGame.GetEntityById(entityId);
                    if (entity == null || !entity.IsCollidable) continue;

                    DVector3 entityWorldPos = targetGroupPos + entity.Position;
                    DVector3 halfSize = entity.AabbHalfSize;

                    // 旋转正确版：世界线段 → 船体局部系（含逆旋转）→ 局部轴对齐盒 slab。
                    // 旧版"世界方向在局部系延伸"（localOrigin + dir*maxDist）在船无旋转时恰好成立，
                    // 旋转时失效；且旧版起点漏乘 maxRange，part 检测从炮口扫 0~1 米必然错位。
                    // 照 CheckSegmentGroupHit 的完整旋转处理写。
                    DQuaternion rotInv = DQuaternion.Inverse(DQuaternion.FromQuaternion(entity.Rotation));
                    DVector3 localStart = DVector3.Rotate(startPos - entityWorldPos, rotInv);
                    DVector3 localEnd = DVector3.Rotate(endPos - entityWorldPos, rotInv);

                    DVector3 min = -halfSize;
                    DVector3 max = halfSize;
                    bool slabHit = CombatGrid.RaySlabIntersection(localStart, localEnd, min, max,
                        out double tEntry, out double tExit, out _);
                    // slab 详情（详细日志）：局部系线段 + 盒半长 + tEntry/tExit，逐候选逐实体打印
                    // （命中距离 = tEntry × beamLength，验证"局部系 t = 世界系 t"的旋转保持长度前提）
                    if (DebugDetailedCombat)
                    {
                        Log.Message(string.Format("[激光slab] G{0}→G{1} E{2} 局部线段=({3:F0},{4:F0},{5:F0})→({6:F0},{7:F0},{8:F0}) 盒半长=({9:F0},{10:F0},{11:F0}) {12} tEntry={13:F3} tExit={14:F3} 命中距离={15:F1}km",
                            groupId, targetGroup.GroupId, entityId,
                            localStart.x, localStart.y, localStart.z,
                            localEnd.x, localEnd.y, localEnd.z,
                            halfSize.x, halfSize.y, halfSize.z,
                            slabHit ? "HIT" : "MISS", tEntry, tExit,
                            slabHit ? (tEntry * beamLength) / 1000.0 : 0.0));
                    }
                    if (!slabHit)
                        continue;

                    if (tEntry < closestT)
                    {
                        closestT = tEntry;
                        hit = true;
                        hitGroup = targetGroup;
                        hitEntity = entity;
                        hitPartIndex = -1;

                        // Part 级精确检测（单 part 化：唯一 part slab 直判，§5.3）。
                        // 线段基准 = 盒进入点（局部系）：part 检测从进入点扫盒内区间。
                        // 旋转保持长度，局部系 t 与世界系 t 一致 → 距离 = t × beamLength。
                        if (entity.Parts.Count > 0)
                        {
                            DVector3 localDir = (localEnd - localStart).normalized;
                            DVector3 boxEntry = localStart + localDir * (tEntry * beamLength);
                            double localMaxDist = (tExit - tEntry) * beamLength;
                            var hitPart = entity.Parts[0];
                            DVector3 partMin = hitPart.localPosition - hitPart.localBoundsSize * 0.5;
                            DVector3 partMax = hitPart.localPosition + hitPart.localBoundsSize * 0.5;
                            bool partHit = CombatGrid.RaySlabIntersection(boxEntry, boxEntry + localDir * localMaxDist,
                                partMin, partMax, out double partTEntry, out _, out _);
                            // partTEntry 可为微负（盒入口恰在 part 盒表面时浮点抖动/起点已在盒内）：
                            // 命中参数取 max(0, ·)，否则"起点在盒内"被误判 miss（直射 AABB 表面时交替命中/未命中）。
                            // RaySlabIntersection 已保证 tFar>=0 且区间与 [0,1] 有交集，去掉 >=0 检查安全。
                            partHit = partHit && partTEntry <= localMaxDist;
                            if (partHit)
                            {
                                double partEnterT = partTEntry < 0 ? 0 : partTEntry;
                                hitPartIndex = 0;
                                // 注意：partTEntry 是归一化参数（0..1 对应 part 线段），命中距离 = partEnterT × localMaxDist
                                hitLocal = boxEntry + localDir * (partEnterT * localMaxDist);
                                // 船壳入口（贯通视觉弹）：命中点沿入射方向反方向延长到 AABB 六面
                                // （RayExitAabb 反向复用，与出膛点几何对称；命中点必在盒内 → 恒出射）
                                entryLocal = (DVector3)MapCoordMapper.RayExitAabb(
                                    new Vector3((float)hitLocal.x, (float)hitLocal.y, (float)hitLocal.z),
                                    new Vector3((float)(-localDir.x), (float)(-localDir.y), (float)(-localDir.z)),
                                    (Vector3)(-halfSize), (Vector3)halfSize, out _);
                                // part 详情（详细日志）：盒入口 / part 命中参数 / 命中局部点 / 船壳入口
                                if (DebugDetailedCombat)
                                {
                                    Log.Message(string.Format("[激光part] G{0}→G{1} E{2} 盒入口=({3:F1},{4:F1},{5:F1})m partT={6:F3} 命中局部=({7:F1},{8:F1},{9:F1})m 船壳入口=({10:F1},{11:F1},{12:F1})m 入射局部方向=({13:F3},{14:F3},{15:F3})",
                                        groupId, targetGroup.GroupId, entityId,
                                        boxEntry.x, boxEntry.y, boxEntry.z,
                                        partTEntry,
                                        hitLocal.x, hitLocal.y, hitLocal.z,
                                        entryLocal.x, entryLocal.y, entryLocal.z,
                                        localDir.x, localDir.y, localDir.z));
                                }
                            }
                        }
                    }
                }
            }

            if (hit && hitEntity != null)
            {
                DVector3 hitPoint = startPos + dir * (closestT * beamLength);
                lastHitPosition = hitPoint;
                lastHitGroupId = hitGroup.GroupId;
                lastHitTick = Find.TickManager.TicksGame;
                lastHitWeaponType = weapon.def.weaponType;

                if (hitPartIndex >= 0 && hitPartIndex < hitEntity.Parts.Count)
                {
                    var hitPart = hitEntity.Parts[hitPartIndex];
                    float preHP = hitPart.hp;
                    // 应用伤害（走组件回调：OnDamaged / OnDestroyed）
                    hitPart.ApplyDamage(weapon.def.damagePerShot);
                    Log.Message(string.Format("[激光命中!] Tick={0} Group{1}→Group{2} Part[{3}]\"{4}\" HP={5:F0}→{6:F0} 命中点=({7:F0},{8:F0},{9:F0})",
                        lastHitTick, groupId, hitGroup.GroupId, hitPartIndex, hitPart.defName,
                        preHP, hitPart.hp,
                        hitPoint.x, hitPoint.y, hitPoint.z));
                    if (hitPart.isDestroyed)
                    {
                        hitEntity.MarkPartDestroyed(hitPart.partId);
                        Log.Message(string.Format("[Part摧毁] Entity{0} Part[{1}]\"{2}\" 已被摧毁",
                            hitEntity.EntityId, hitPartIndex, hitPart.defName));
                    }

                    // 定向能贯通视觉弹：命中目标有 Map → 从船壳入口贯穿到落点（光束切进船体）
                    if (hitEntity.MapId >= 0)
                    {
                        Map targetMap = SpaceMapGenerator.FindPocketMap(hitEntity.MapId);
                        if (targetMap != null)
                            SpawnPenetrationMote(hitEntity, targetMap, hitLocal, entryLocal);
                    }
                }
                else
                {
                    Log.Message(string.Format("[激光命中!] Tick={0} Group{1}→Group{2} Entity{3} AABB 命中点=({4:F0},{5:F0},{6:F0})",
                        lastHitTick, groupId, hitGroup.GroupId, hitEntity.EntityId,
                        hitPoint.x, hitPoint.y, hitPoint.z));
                }

                activeLaserBeams.Add(new LaserBeamInstance
                {
                    startPos = startPos,
                    endPos = hitPoint,
                    remainingTicks = 60,
                    totalTicks = 60,
                    systemBodyId = systemBodyId
                });

                // 命中判定汇总（详细日志）：候选船数 + 命中距离（遮挡语义下 = 最近一艘）
                if (DebugDetailedCombat)
                {
                    Log.Message(string.Format("[激光命中判定] G{0}→G{1} 命中距离={2:F1}km 候选={3}艘 最近tEntry={4:F3} 命中点=({5:F0},{6:F0},{7:F0})",
                        groupId, hitGroup.GroupId, (hitPoint - startPos).magnitude / 1000.0,
                        rayCandidateResults.Count, closestT, hitPoint.x, hitPoint.y, hitPoint.z));
                }
            }
            else
            {
                // 未命中详情：候选船数（0 = 粗筛/射程过滤后无目标；>0 = 全部 slab 未中）
                Log.Message(string.Format("[激光未命中] Group {0} 射线无碰撞（候选船数={1}）", groupId, rayCandidateResults.Count));
                activeLaserBeams.Add(new LaserBeamInstance
                {
                    startPos = startPos,
                    endPos = endPos,
                    remainingTicks = 60,
                    totalTicks = 60,
                    systemBodyId = systemBodyId
                });
            }
        }

        /// <summary>
        /// 定向能射线候选收集：方案 A = 战斗区块正方体粗筛 + 射程球内过滤（当前启用）；
        /// 方案 B = 线段走格（射程 >~100km 正方体格数爆炸、或需要更紧候选时切换，只改下面一行）。
        /// 公共过滤：活性 + 可碰撞 + 排除自己。阵营不过滤（水火无情：光束路径上任何船都遮挡）。
        /// </summary>
        private void CollectRayCandidates(int shooterGroupId, int systemBodyId, DVector3 startPos, DVector3 endPos,
            double beamLength, HashSet<SpaceEntityGroup> results)
        {
            var combatGrid = GetCombatGrid();
            results.Clear();
            if (combatGrid == null) return;

            // 方案 A（当前）：正方体粗筛（覆盖格数 ≈ (2R/20)³，50km → 125 格，多数 miss）
            combatGrid.CollectShipsInCube(systemBodyId, startPos, beamLength, results);

            // 方案 B（预留切换点）：线段走格，候选更精确（GetShipsNearSegment 已实现，待需要时启用）
            // combatGrid.GetShipsNearSegment(systemBodyId, startPos, endPos, results);

            double maxRangeSq = beamLength * beamLength;
            results.RemoveWhere(g =>
            {
                if (g == null || !g.IsActive || !g.IsCollidable || g.GroupId == shooterGroupId) return true;
                return (GetGroupPos(g) - startPos).sqrMagnitude > maxRangeSq; // 射程球内
            });
        }

        /// <summary>
        /// 群组销毁时清理相关战斗缓存。
        /// 由 SpaceGameComp.DestroyGroup 在级联销毁中调用。
        /// </summary>
        public void OnGroupDestroyed(int groupId)
        {
            groupWeapons.Remove(groupId);
            shipLastPositions.Remove(groupId);
            // 射弹继续飞行，不清理
        }

        public override void ExposeData()
        {
            base.ExposeData();
            ProjectileInstance.ExposeIdCounter();
        }
    }

    /// <summary>
    /// 爆炸视觉残留数据，物理效果（范围损伤/径向冲量）即时结算，此对象仅用于绘制扩散闪光。
    /// </summary>
    public class ExplosionInstance
    {
        public DVector3 position;
        public double maxRadius;
        public int remainingTicks;
        public int totalTicks;
        public int systemBodyId = -1;   // 所属参考系（渲染时转绝对坐标）
    }

    /// <summary>
    /// 激光射线视觉残留数据，不参与物理模拟。
    /// </summary>
    public class LaserBeamInstance
    {
        public DVector3 startPos;
        public DVector3 endPos;
        public int remainingTicks;
        public int totalTicks;
        public int systemBodyId = -1;   // 所属参考系（渲染时转绝对坐标）
    }
}
