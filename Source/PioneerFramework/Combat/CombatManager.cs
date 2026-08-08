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

                if (!shipLastPositions.TryGetValue(gid, out var lastPos))
                {
                    combatGrid.AddShip(group);
                }
                else if (!lastPos.Equals(currentPos))
                {
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
                DVector3 min = entityLocal - halfSize;
                DVector3 max = entityLocal + halfSize;

#if DEBUG
                if (verboseHit)
                    Log.Message(string.Format(
                        "[命中调试] entity={0} local=({1:F0},{2:F0},{3:F0}) half=({4:F0},{5:F0},{6:F0}) parts={7}",
                        entityId, entityLocal.x, entityLocal.y, entityLocal.z,
                        halfSize.x, halfSize.y, halfSize.z, entity.Parts.Count));
#endif

                // 相对坐标系中目标静止：slab 线段检测是连续几何检测（与线段长度无关），
                // 射弹无论多快，只要相对路径穿过 AABB 就会被捕捉，无需子段化。
                if (!CombatGrid.RaySlabIntersection(startPos, endPos, min, max,
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

                // Entity AABB 被命中，检查 Part 级碰撞（单 part 化：唯一 part slab 直判，§5.3）
                if (entity.Parts.Count > 0 && segmentLength > 0.001)
                {
                    DVector3 worldDirNorm = worldDir / segmentLength;
                    DVector3 hitEntryPoint = startPos + worldDir * tEntry;
                    DVector3 localOrigin = hitEntryPoint - entityLocal;
                    DVector3 localDir = worldDirNorm;
                    double localMaxDist = (tExit - tEntry) * segmentLength;

                    var hitPart = entity.Parts[0];
                    DVector3 partMin = hitPart.localPosition - hitPart.localBoundsSize * 0.5;
                    DVector3 partMax = hitPart.localPosition + hitPart.localBoundsSize * 0.5;
                    bool partHit = CombatGrid.RaySlabIntersection(localOrigin, localOrigin + localDir * localMaxDist,
                        partMin, partMax, out double partTEntry, out _, out _);
                    partHit = partHit && partTEntry >= 0 && partTEntry <= localMaxDist;
#if DEBUG
                    if (verboseHit)
                        Log.Message(string.Format(
                            "[命中调试] PartSlab origin=({0:F0},{1:F0},{2:F0}) dir=({3:F3},{4:F3},{5:F3}) maxDist={6:F0} partHit={7} t={8:F3}",
                            localOrigin.x, localOrigin.y, localOrigin.z,
                            localDir.x, localDir.y, localDir.z, localMaxDist, partHit, partTEntry));
#endif
                    if (partHit)
                    {
                        var firstHitPartIndex = 0;
                        var firstHitT = partTEntry;
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

            foreach (var (a, b) in combatGrid.EnumerateShipPairs())
            {
                if (!a.IsActive || !b.IsActive || !a.IsCollidable || !b.IsCollidable) continue;
                if (a.GroupId == b.GroupId) continue;

                DVector3 pa = GetGroupPos(a);
                DVector3 pb = GetGroupPos(b);

                // 球包围快筛
                double ra = GetGroupRadius(a);
                double rb = GetGroupRadius(b);
                double rr = ra + rb;
                if ((pb - pa).sqrMagnitude > rr * rr) continue;

                // AABB 重叠检测
                if (!TryGetOverlap(a, b, spaceGame, pa, pb, out double ox, out double oy, out double oz)) continue;

                DVector3 nDir = pb - pa;
                double nLen = nDir.magnitude;
                if (nLen < 1e-6)
                    nDir = new DVector3(1, 0, 0);
                else
                    nDir /= nLen;

                DVector3 va = GetGroupVelocity(a);
                DVector3 vb = GetGroupVelocity(b);
                double vRel = DVector3.Dot(va - vb, nDir); // >0 正在接近

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
            }
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
                double er = entity.AabbHalfSize.magnitude;
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
                DVector3 half = entity.AabbHalfSize;
                DVector3 eMin = groupPos + entity.Position - half;
                DVector3 eMax = groupPos + entity.Position + half;
                if (!has) { min = eMin; max = eMax; has = true; }
                else
                {
                    min = new DVector3(Math.Min(min.x, eMin.x), Math.Min(min.y, eMin.y), Math.Min(min.z, eMin.z));
                    max = new DVector3(Math.Max(max.x, eMax.x), Math.Max(max.y, eMax.y), Math.Max(max.z, eMax.z));
                }
            }
        }

        private bool TryGetOverlap(SpaceEntityGroup a, SpaceEntityGroup b, SpaceGameComp spaceGame,
            DVector3 pa, DVector3 pb,
            out double ox, out double oy, out double oz)
        {
            GetGroupWorldAabb(a, pa, spaceGame, out var aMin, out var aMax);
            GetGroupWorldAabb(b, pb, spaceGame, out var bMin, out var bMax);
            ox = Math.Min(aMax.x, bMax.x) - Math.Max(aMin.x, bMin.x);
            oy = Math.Min(aMax.y, bMax.y) - Math.Max(aMin.y, bMin.y);
            oz = Math.Min(aMax.z, bMax.z) - Math.Max(aMin.z, bMin.z);
            return ox > 0 && oy > 0 && oz > 0;
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

        /// <summary>任何速度修正都需先退出圆轨（圆轨是解析位置，速度修正会被覆盖）。</summary>
        private void ApplyGroupVelocity(SpaceEntityGroup group, DVector3 newVel)
        {
            if (group.IsCircularOrbit)
                group.ExitCircularOrbit();
            group.CurrentOrbit.velocity = newVel;
            group.OrbitDirty = true;
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
                lastUpdateRealTick = Find.TickManager.TicksGame,
                prevUpdateRealTick = Find.TickManager.TicksGame,
                prevPhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                systemBodyId = spaceGame.ResolveReferenceFrameId(group.CelestialBodyId)
            };

            activeProjectiles.Add(proj);
#if DEBUG
            projSpawnedTotal++;
#endif

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
                        centralVel = mgr.GetBodyLocalVelocity(group.circularCentralBodyId, (int)System.Math.Round(physicsTickNow));
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
            return targetPos + targetVel * t;
        }

        /// <summary>
        /// 激光武器：立即射线命中检测，不生成射弹。
        /// </summary>
        private void FireRayWeapon(SpaceEntityGroup group, WeaponInstance weapon, int groupId, DVector3 spawnPos, DVector3 direction)
        {
            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null) return;

            DVector3 startPos = spawnPos;
            DVector3 dir = direction;
            DVector3 endPos = startPos + dir * weapon.def.maxRange;

            Log.Message(string.Format("[激光发射] Group {0} 起点=({1:F0},{2:F0},{3:F0}) 方向=({4:F3},{5:F3},{6:F3}) 射程={7:F0}m",
                groupId, startPos.x, startPos.y, startPos.z,
                dir.x, dir.y, dir.z, weapon.def.maxRange));

            // 立即射线命中检测：遍历所有敌对 Group 的 Entity AABB
            bool hit = false;
            double closestT = double.MaxValue;
            SpaceEntity hitEntity = null;
            int hitPartIndex = -1;
            int hitGroupId = -1;

            foreach (var targetGroup in spaceGame.GetAllGroups())
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
                    DVector3 min = entityWorldPos - halfSize;
                    DVector3 max = entityWorldPos + halfSize;

                    if (!CombatGrid.RaySlabIntersection(startPos, endPos, min, max,
                            out double tEntry, out double tExit, out _))
                        continue;

                    if (tEntry < closestT)
                    {
                        closestT = tEntry;
                        hit = true;
                        hitGroupId = targetGroup.GroupId;
                        hitEntity = entity;

                        // Part 级精确检测（单 part 化：唯一 part slab 直判，§5.3）
                        if (entity.Parts.Count > 0)
                        {
                            DVector3 hitEntryPoint = startPos + dir * tEntry;
                            DVector3 localOrigin = hitEntryPoint - entityWorldPos;
                            double localMaxDist = (tExit - tEntry) * weapon.def.maxRange;
                            var part = entity.Parts[0];
                            DVector3 partMin = part.localPosition - part.localBoundsSize * 0.5;
                            DVector3 partMax = part.localPosition + part.localBoundsSize * 0.5;
                            if (CombatGrid.RaySlabIntersection(localOrigin, localOrigin + dir * localMaxDist,
                                    partMin, partMax, out double _, out _, out _))
                                hitPartIndex = 0;
                        }
                    }
                }
            }

            if (hit && hitEntity != null)
            {
                DVector3 hitPoint = startPos + dir * closestT;
                lastHitPosition = hitPoint;
                lastHitGroupId = hitGroupId;
                lastHitTick = Find.TickManager.TicksGame;
                lastHitWeaponType = weapon.def.weaponType;

                if (hitPartIndex >= 0 && hitPartIndex < hitEntity.Parts.Count)
                {
                    var hitPart = hitEntity.Parts[hitPartIndex];
                    float preHP = hitPart.hp;
                    // 应用伤害（走组件回调：OnDamaged / OnDestroyed）
                    hitPart.ApplyDamage(weapon.def.damagePerShot);
                    Log.Message(string.Format("[激光命中!] Tick={0} Group{1}→Group{2} Part[{3}]\"{4}\" HP={5:F0}→{6:F0} 命中点=({7:F0},{8:F0},{9:F0})",
                        lastHitTick, groupId, hitGroupId, hitPartIndex, hitPart.defName,
                        preHP, hitPart.hp,
                        hitPoint.x, hitPoint.y, hitPoint.z));
                    if (hitPart.isDestroyed)
                    {
                        hitEntity.MarkPartDestroyed(hitPart.partId);
                        Log.Message(string.Format("[Part摧毁] Entity{0} Part[{1}]\"{2}\" 已被摧毁",
                            hitEntity.EntityId, hitPartIndex, hitPart.defName));
                    }
                }
                else
                {
                    Log.Message(string.Format("[激光命中!] Tick={0} Group{1}→Group{2} Entity{3} AABB 命中点=({4:F0},{5:F0},{6:F0})",
                        lastHitTick, groupId, hitGroupId, hitEntity.EntityId,
                        hitPoint.x, hitPoint.y, hitPoint.z));
                }

                activeLaserBeams.Add(new LaserBeamInstance
                {
                    startPos = startPos,
                    endPos = hitPoint,
                    remainingTicks = 60,
                    totalTicks = 60,
                    systemBodyId = spaceGame != null ? spaceGame.ResolveReferenceFrameId(group.CelestialBodyId) : -1
                });
            }
            else
            {
                Log.Message(string.Format("[激光未命中] Group {0} 射线无碰撞", groupId));
                activeLaserBeams.Add(new LaserBeamInstance
                {
                    startPos = startPos,
                    endPos = endPos,
                    remainingTicks = 60,
                    totalTicks = 60,
                    systemBodyId = spaceGame != null ? spaceGame.ResolveReferenceFrameId(group.CelestialBodyId) : -1
                });
            }
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
