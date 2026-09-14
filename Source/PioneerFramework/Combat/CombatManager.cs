using System.Collections.Generic;
using UnityEngine;
using Verse;
using System.Text;
using System;
using RimWorld;
using PioneerFramework.Components;

namespace PioneerFramework.Combat
{
    public class CombatManager : GameComponent
    {
        private readonly List<ProjectileInstance> activeProjectiles = new List<ProjectileInstance>();
        // 群组武器列表（Lite 裁剪后：实体级武器收集已删除，此容器由 M4 Map 炮塔桥接填充）
        private readonly Dictionary<int, List<WeaponInstance>> groupWeapons = new Dictionary<int, List<WeaponInstance>>();
        private readonly HashSet<SpaceEntityGroup> projectileQueryResults = new HashSet<SpaceEntityGroup>();
        /// <summary>齐射弹群实体数上限（§5，2026-09-08 深度队列制：同深度仅 1 枚、深度间距 = min(2km, 10km/(N−1))，
        /// 致死带 ~1km 余量下 10km 内安全容量 = 8；方形同排成员会被兄弟弹头 AoE 击杀，已废弃）。</summary>
        /// <summary>单次齐射枚数上限：**单一来源**——深度队列安全容量（N 枚沿航向排开、
        /// 间距受弹头爆炸冲量作用半径约束；N=8 时 16km/(8−1) ≈ 2.29km &gt; 爆炸半径 1.5km）。
        /// 发射器 UI（`CompMissileLauncher` / `Window_VolleyCountSlider`）与齐射工厂都引用它。</summary>
        public const int MaxVolleyCount = 8;
        // 定向能射线候选集（每炮一次收集复用，方案 A 正方体粗筛 / B 走格预留共用）
        private readonly HashSet<SpaceEntityGroup> rayCandidateResults = new HashSet<SpaceEntityGroup>();
        // 命中诊断：已记录过的射弹 id（每个射弹只打印一次）
        private readonly HashSet<int> hitDiagnosticLogged = new HashSet<int>();
        private readonly Dictionary<int, DVector3> shipLastPositions = new Dictionary<int, DVector3>();
        // #4：SyncAllShipsToGrid 的本 tick 存活船缓冲（复用，避免每 2 tick 分配 HashSet）
        private readonly HashSet<int> aliveScratch = new HashSet<int>();
        // 性能第 4 条：CheckShipCollisions 本步待销毁导弹组缓冲（复用，避免场上无导弹时每物理步也分配）
        private readonly HashSet<int> destroyedMissilesScratch = new HashSet<int>();

        // 性能第 15 条：爆炸 AoE 候选组缓冲（走细网格；每次爆炸复用，CollectShipsInCube 内部会 Clear）。
        // 复用安全：ApplyExplosionDamage 的循环体不会重入自身（部件伤害回调无重写实现），
        // 冲量函数在其返回后才再次收集。
        private readonly HashSet<SpaceEntityGroup> explosionCandidateResults = new HashSet<SpaceEntityGroup>();

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

        }

        /// <summary>
        /// 每物理步执行（2026-09-07 起由 SpaceGameComp 在物理推进完成后统一驱动）：
        /// （2026-09-14 更正）本方法**不依赖组件 tick 顺序**：原先它按 `%2` 自行 tick，会读物理推进
        /// **前**的位置，与制导/其他每物理步消费者错位 2s ≈ 5km（09-07 日志实证：同 tick 制导报
        /// 0.7km 即将命中、碰撞实测 2.6km，导弹真实航线从没穿过目标盒）。改为挂在物理推进之后，
        /// 所有每物理步消费者同 tick 同位，顺序依赖消除。
        /// 注：旧注释把组件顺序写成"A V M → SpaceGameComp → CombatManager"，与类型全名字母序不符
        /// （本组件 C 在前、S 在后）——正因为顺序不可指望，才改成由 SpaceGameComp 显式驱动。
        /// </summary>
        internal void OnPhysicsStepCompleted()
        {
            int currentTick = Find.TickManager.TicksGame;

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
        /// 射弹在飞 / 导弹处于 Attack 终末自动段时自动将时间流速降到 1/60（Combat 档），
        /// 实现"子弹时间"（导弹终末与射弹同级 flag，2026-09-07 用户定稿——否则 60× 现实流速下
        /// 玩家看不清导弹冲刺与撞击）。只降不升：恢复由 SpaceGameComp 的流速检查（每 240 tick）
        /// 处理，避免清空/再生反复切换。限频（每 60 tick 最多请求一次）：覆盖用户手动切走的
        /// 高流速，同时避免 QueueCommand 刷屏。
        /// </summary>
        private void ManageProjectileTimeScale()
        {
            var tsm = GetTimeScaleManager();
            if (tsm == null) return;

            bool combatActive = activeProjectiles.Count > 0;
            if (!combatActive)
            {
                var avm = GetAvm();
                combatActive = avm != null && avm.HasActiveAttack;
            }

            if (combatActive &&
                tsm.CurrentLevel != TimeScaleLevel.Combat &&
                Find.TickManager.TicksGame - lastProjectileSlowdownTick >= PROJECTILE_SLOWDOWN_MIN_INTERVAL)
            {
                lastProjectileSlowdownTick = Find.TickManager.TicksGame;
                tsm.QueueCommand(TimeScaleCommand.ToCombat());
            }
        }

        // ── 组件引用缓存（2026-09-10 性能第 5 条）：下列组件在每物理步 / 每弹 / 每对 上报，
        //    每次 Current.Game.GetComponent 都是组件表线性扫描，故按 Game 缓存（见 GameComponentRef）。──
        private readonly GameComponentRef<CombatGrid> combatGridRef = new GameComponentRef<CombatGrid>();
        private readonly GameComponentRef<SpaceGameComp> spaceGameRef = new GameComponentRef<SpaceGameComp>();
        private readonly GameComponentRef<TimeScaleManager> timeScaleRef = new GameComponentRef<TimeScaleManager>();
        private readonly GameComponentRef<AutonomousVehicleManager> avmRef = new GameComponentRef<AutonomousVehicleManager>();
        private readonly GameComponentRef<BoardingManager> boardingRef = new GameComponentRef<BoardingManager>();
        private readonly GameComponentRef<CelestialBodyManager> celestialRef = new GameComponentRef<CelestialBodyManager>();

        private CombatGrid GetCombatGrid() => combatGridRef.Get();
        private SpaceGameComp GetSpaceGameComp() => spaceGameRef.Get();
        private TimeScaleManager GetTimeScaleManager() => timeScaleRef.Get();
        private AutonomousVehicleManager GetAvm() => avmRef.Get();
        private BoardingManager GetBoardingManager() => boardingRef.Get();
        private CelestialBodyManager GetCelestialBodyManager() => celestialRef.Get();

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

                proj.prevUpdateRealTick = proj.lastUpdateRealTick;
                proj.lastUpdateRealTick = Find.TickManager.TicksGame;

                // A2：引力射弹走"2 物理秒一段的三阶泰勒传播 + 段内插值"——
                // 引力（含递归取天体位置）每段只求一次，段内位置由多项式给出，
                // 仍是"每物理步一个精确位置"，扫掠碰撞检测的精度不受影响。
                if (proj.affectedByGravity)
                {
                    bool expired = proj.gravitySegment.Expired(physicsTickNow);
                    if (expired)
                    {
                        // 段起点时刻必须是**当前状态所在的时刻**（= 上一步更新时间），不能是 physicsTickNow：
                        // 否则求值时 t = physicsTickNow − baseTick = 0 → 这一步位置不推进，每段丢一步运动。
                        // 实测：Combat 档（2s 段 = 120 步）偏 0.83%，Normal 档（2 步）偏 33% —— 2026-09-14 ㉔ 探针抓到。
                        BuildGravitySegment(proj, proj.prevUpdatePhysicsTick);
                    }

                    proj.BeginStep();
                    proj.ApplyGravitySegment(physicsTickNow);
                    proj.AdvanceLife(deltaTime);

                    if (PerfProbe.Enabled)
                    {
                        proj.gravitySteps++;
                        if (expired) proj.gravityRebuilds++;
                        PerfProbe.NoteProjectileStep(proj.id, proj.gravityRebuilds,
                            proj.totalFlightSeconds, ProjectileGravitySegmentSeconds, expired);
                        // 定量值判据：与"生成时的直线"相比的实际弯曲量 vs ½·|a|·t²
                        PerfProbe.NoteFreeFlightBend(proj.id, proj.isWarhead, proj.totalFlightSeconds,
                            proj.spawnPosition, proj.spawnVelocity, proj.position,
                            proj.gravitySegment.acc.magnitude);
                    }
                }
                else
                {
                    proj.UpdatePosition((float)deltaTime);
                }

                if (!proj.IsAlive())
                {
                    // ── 调试日志：超时脱靶汇总（验证时间基准 B1 与提前量 A1）──
                    double flightDist = (proj.position - proj.spawnPosition).magnitude;
                    if (DebugDetailedCombat)
                    {
                        Log.Message(string.Format(
                            "[射弹脱靶] proj={0} 超时销毁 | 生成=({1:F0},{2:F0},{3:F0}) 终点=({4:F0},{5:F0},{6:F0}) 飞行距离={7:F0}m 累计游戏秒={8:F2}s 平均速度={9:F0}m/s 末速={10:F0}m/s physicsTick={11:F1}",
                            proj.id,
                            proj.spawnPosition.x, proj.spawnPosition.y, proj.spawnPosition.z,
                            proj.position.x, proj.position.y, proj.position.z,
                            flightDist, proj.totalFlightSeconds,
                            proj.totalFlightSeconds > 0.001 ? flightDist / proj.totalFlightSeconds : 0.0,
                            proj.velocity.magnitude,
                            TimeScaleManager.GetPhysicsTickSafe()));
                    }
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

        /// <summary>引力射弹的传播段长（物理秒）。用户 2026-09-14 定调：不要算得太勤。</summary>
        private const double ProjectileGravitySegmentSeconds = 2.0;

        private readonly List<GravitySource> projectileGravityScratch = new List<GravitySource>();

        /// <summary>
        /// 以当前位置/速度为段起点重开一段引力传播（A2）。
        /// 位置/速度取自当前位置 → 段间连续无跳变；引力取**该行星系全部天体**（A0 的场，
        /// 与飞船/导弹同源），因此弹头与弹群吃同一套引力、不存在"弹头绕地球弯而弹群不弯"的几何错位。
        /// </summary>
        private void BuildGravitySegment(ProjectileInstance proj, double physicsTick)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr == null)
            {
                proj.gravitySegment.Rebuild(proj.position, proj.velocity,
                    DVector3.zero, DVector3.zero, physicsTick, ProjectileGravitySegmentSeconds);
                return;
            }

            // systemBodyId = 参考帧天体（根行星/恒星）= 系统标识 → 取该系全部天体
            mgr.CollectSystemSources(proj.systemBodyId, physicsTick, projectileGravityScratch);
            NBodyIntegrator.FieldAccelJerk(proj.position, proj.velocity, projectileGravityScratch,
                out var acc, out var jerk);

            if (PerfProbe.Enabled && acc.magnitude > 1e-9)
            {
                // 探针（A2）：|a| / 主导项（μ/r² 最大值）+ "指向行星中心"余弦 —— 旧实现此处是 ~100× 主导项的伪值，
                // 修正后应 ≈1× 主导项（近地即行星真实引力 ~8 m/s² 量级）。
                double dominant = 0.0;
                for (int i = 0; i < projectileGravityScratch.Count; i++)
                {
                    var s = projectileGravityScratch[i];
                    double dSq = (s.position - proj.position).sqrMagnitude;
                    if (dSq < 0.01) continue;
                    double term = s.mu / dSq;
                    if (term > dominant) dominant = term;
                }

                DVector3 toCenter = -proj.position;
                double cos = toCenter.magnitude > 1.0
                    ? DVector3.Dot(acc.normalized, toCenter.normalized)
                    : 0.0;
                double ratio = dominant > 1e-12 ? acc.magnitude / dominant : 0.0;

                PerfProbe.NoteWarheadSegment(acc.magnitude, cos, ratio);
            }

            proj.gravitySegment.Rebuild(proj.position, proj.velocity, acc, jerk,
                physicsTick, ProjectileGravitySegmentSeconds);
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
                        if (DebugDetailedCombat)
                        {
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

            // B1：命中拦截时**不在 foreach 内改 group.entityIds**——只记录，摘除/引爆延后到循环外。
            // 原实现靠"改完立即 return"侥幸安全（HashSet 枚举器只在 MoveNext 校验 version），
            // 后续谁在 return 前补一句逻辑就会抛 InvalidOperationException。
            AutonomousVehicleManager.VehicleRecord? interceptedRec = null;
            int interceptedEntityId = -1;
            DVector3 interceptedHitCenter = DVector3.zero;

            foreach (int entityId in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(entityId);
                if (entity == null || !entity.IsCollidable) continue;

                DVector3 entityLocal = entity.Position;   // 目标参考系中实体局部位置（不随时间变）
                DVector3 halfSize = entity.AabbHalfSize;

                // 射弹线段 → 船体局部系（含旋转）：part 局部 = 固定轴对齐盒（地图 1:1 基准）。
                // entity.Rotation 参与命中几何——倾斜/翻滚的船，命中点反映真实姿态。
                // 第 16 条：逆四元数走实体 per-tick 缓存（每弹每实体的旋转开销 → 每实体每 tick 一次）
                DQuaternion rotInv = entity.RotationInverse();
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

                // 拦截引爆（§10）：目标实体所属 group 是导弹（manager 反查，K9）→ 命中即就地引爆弹头
                // AoE（无阵营过滤，误伤玩法成立）。统一单枚路径（2026-09-08 双路径统一）：只引爆被
                // slab 命中的这枚实体（爆心 = 该实体世界位 = 单实体时即组原位，语义与原整群引爆一致），
                // 实体离群走 AVM.RemoveMissileEntity 出口（燃料随实体、无记账）；组空 → 注销+销毁。
                // 射弹路径内 DestroyGroup 安全（此处不在 EnumerateShipPairs 迭代中）。
                var interceptMgr = GetAvm();
                var missileRec = interceptMgr?.GetRecord(group.GroupId);
                if (missileRec != null)
                {
                    Log.Message(string.Format("[导弹拦截] proj={0} 击毁导弹G{1} 单枚E{2} → 就地引爆", proj.id, group.GroupId, entityId));
                    interceptedRec = missileRec;
                    interceptedEntityId = entityId;
                    interceptedHitCenter = groupPos + entity.Position; // 命中实体世界位（爆心）
                    break;   // 摘除/引爆在循环外执行（见方法尾部）
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
                        lastHitWeaponType = proj.weaponDef?.weaponType ?? WeaponType.Projectile;
                        if (DebugDetailedCombat)
                        {
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
                        }
                        // 应用伤害（走组件回调：OnDamaged / OnDestroyed）
                        hitPart.ApplyDamage(proj.damage);
                        if (hitPart.isDestroyed)
                        {
                            entity.MarkPartDestroyed(hitPart.partId);
                            if (DebugDetailedCombat)
                            {
                                Log.Message(string.Format("[Part摧毁] Entity{0} Part[{1}]\"{2}\" 已被摧毁",
                                    entityId, firstHitPartIndex, hitPart.defName));
                            }
                        }

                        // F3 命中 → Map 落点瞬爆：命中点已在船体局部系（含旋转），
                        // part 局部 = 地图 1:1，直接映射即可。
                        DVector3 worldHitPoint = proj.position; // 兜底：步末点
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
                                // 弹头 Map 范围爆炸（§10 收尾，2026-09-07）：以命中映射格为心在目标地图上
                                // 做范围爆炸（条件：命中处地板不是"太空"，SpawnMapWarheadBlast 内部判定）——
                                // 3D AoE（HandleWarheadImpact）的 Map 对应物；太空 Pocket Map GenExplosion NRE → 自实现。
                                if (proj.isWarhead)
                                    SpawnMapWarheadBlast(targetMap, hitLocal, proj.mapBlastRadiusCells, proj.blastCenterDamage);
                                // 世界命中点（爆炸中心用）：船体局部 hitLocal → 世界（含目标姿态/群位置）
                                worldHitPoint = groupPos + entity.Position + DVector3.Rotate(hitLocal, entity.Rotation);
                            }
                        }

                        // 弹头命中结算（§10）：part 大额直扣（上方 ApplyDamage）+ F3 Map 落点（上方 mote）
                        // 已走现链；弹头额外在**精确命中点**触发 3D AoE 爆炸（2026-09-07 修：
                        // 原用 proj.position（步末点）作爆炸中心——高速弹头一步穿 ~3.4km，
                        // 偏深超出 AoE 半径导致 damaged=0）。（命中即移除，外层处理。）
                        if (proj.isWarhead)
                            HandleWarheadImpact(proj, group, worldHitPoint);
                        return true;
                    }
                }
                else
                {
                    // 无 Part 数据，回退为 Entity AABB 命中即算
                    lastHitPosition = proj.position;
                    lastHitGroupId = group.GroupId;
                    lastHitTick = Find.TickManager.TicksGame;
                    lastHitWeaponType = proj.weaponDef?.weaponType ?? WeaponType.Projectile;
                    if (DebugDetailedCombat)
                    {
                        Log.Message(string.Format("[命中!] 射弹击中 Entity {0} (Group {1}) 位置({2:F0},{3:F0},{4:F0})",
                            entityId, group.GroupId, groupPos.x, groupPos.y, groupPos.z));
                    }
                    if (proj.isWarhead)
                        HandleWarheadImpact(proj, group, proj.position);
                    return true;
                }
            }

            // B1：拦截引爆延后到循环外（A 2026-09-09 定稿的"先摘除后引爆"顺序在此保持；
            // B：引爆带 excludeGroupId=本组，成员死亡爆炸不扰动残组）
            if (interceptedRec != null)
            {
                var interceptMgr = GetAvm();
                bool groupEmpty = interceptMgr.RemoveMissileEntity(interceptedRec, interceptedEntityId, "拦截");
                ExplodeMissileEntityAt(group, interceptedRec, interceptedHitCenter, spaceGame, group.GroupId);
                if (groupEmpty)
                {
                    interceptMgr.UnregisterGroup(group.GroupId);
                    spaceGame.DestroyGroup(group.GroupId);
                }
                return true;
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

            // 转化交接待销毁导弹群：统一在 pair 循环外执行（EnumerateShipPairs 是字典懒迭代器，
            // 迭代体内 DestroyGroup 会并发修改抛异常）。HashSet 防同一导弹本 tick 与多个目标接触时重复转化。
            var destroyedMissiles = destroyedMissilesScratch;
            if (PerfProbe.Enabled) PerfProbe.DestroyedLeftoverReport(destroyedMissiles.Count); // 探针：上一步遗留项数（>0 即复用生效）
            destroyedMissiles.Clear(); // 第 4 条：字段复用，步首清空（早退分支不消费，无需清理）
            // 导弹识别管理器：pair 循环外取一次（GetComponent 线性查组件表，不能每对被拒 pair 都调）
            var missileMgr = GetAvm();

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

                // 球包围快筛（导弹参与的对豁免——09-07 日志实证根因）：步末距离判据在
                // "相对步长 > 球区跨度"时失效：导弹整步从目标一侧穿到另一侧（0.7km/接近率479，
                // 单步相对位移 958m > 目标球区 ±57m），步末已在另一侧 209m → 误拒 → 扫掠/段级
                // 复核全部没机会执行（收敛直撞线零碰撞）。导弹对改由下方段级时间对齐复核权威判定。
                double ra = ca.radius;
                double rb = cb.radius;
                double rr = ra + rb;
                if ((pb - pa).sqrMagnitude > rr * rr)
                {
                    bool anyMissile = missileMgr != null
                        && (missileMgr.GetRecord(a.GroupId) != null || missileMgr.GetRecord(b.GroupId) != null);
                    if (!anyMissile) continue;
                }

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
                // 注意：本日志全局 30 tick 门控——09-07 诊断期曾临时无门控（穿越窗口 2-4 tick），
                // Combat 档（1/60）下现实 30Hz × 每对刷屏 8725 行/次测试拖垮内存，使命完成后恢复门控。
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
                if (!sweptHit)
                {
                    // 第 10 条：绝大多数对在此早退，故导弹记录查询移到判定之后——每对每步省 2 次
                    // GetRecord 字典查找（此前在判定前取，扫掠未命中的对也白查两次）。
                    if (PerfProbe.Enabled) PerfProbe.NoteRecordLookupsAvoided(2);
                    continue;
                }

                // 导弹对判定（导弹分支复用；GetRecord 字典 O(1)）。放在早退之后：见第 10 条。
                var recA = missileMgr?.GetRecord(a.GroupId);
                var recB = missileMgr?.GetRecord(b.GroupId);
                bool pairHasMissile = recA != null || recB != null;

                // 导弹转化交接分支（§10）：导弹接触任何碰撞体（普通船 / 异 volley 导弹 / 友军）→
                // 一律「先生成弹头射弹（继承方向/速度/位置）、后销毁导弹」——打谁都转为射弹。
                // 例外只有两条：① 同 volley 兄弟互碰永久排除（K1，防齐射同点/编队自爆）；
                // ② 拦截（导弹被射弹击中）= 就地引爆，在 CheckSegmentGroupHit 处理（§10），不在此。
                // 本分支位于 tunneling / 跳帮分支之前：导弹参与的重叠一律先被消费，不落通用船-船
                // 语义（动量交换/弹开/动能爆炸/ResolveTunneled 全错）。销毁收集到 destroyedMissiles
                // 延后到 pair 循环外统一执行（EnumerateShipPairs 是字典懒迭代器，迭代内 DestroyGroup 抛异常）。
                if (missileMgr != null && pairHasMissile)
                {
                    bool sameVolley = recA != null && recB != null && recA.volleyId == recB.volleyId;
                    if (!sameVolley)
                    {
                        // 逐实体接触解析（唯一路径，2026-09-08 双路径统一）：导弹对一律走 ResolveEntityContacts
                        // ——单实体组 = 1×1 退化情形，与旧组级复核几何完全一致（组盒=实体盒、组原点=实体
                        // 原点），游戏内单发命中链（撞击→弹头命中）等价；并集盒扫掠只作粗筛：多实体
                        // 10km 阵列"空当"不触发转化；真接触的单枚独立转化离群（组空才整体销毁，延后
                        // pair 循环外统一执行）。假阳性防御（出生/伴飞时间对齐复核）内建于逐实体解析。
                        ResolveEntityContacts(a, b, recA, ca, cb, missileMgr, spaceGame, combatGrid, destroyedMissiles);
                        ResolveEntityContacts(b, a, recB, cb, ca, missileMgr, spaceGame, combatGrid, destroyedMissiles);
                    }
                    continue; // 导弹对一律在本分支消费完（含同 volley 排除），不落通用船-船语义
                }
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
                // （导弹 vs 跳帮单位不会到达此处：导弹分支已在上方优先消费。）
                var boarding = GetBoardingManager();
                if (boarding != null && boarding.TryResolveBoardingContact(a, b, vRel))
                {
                    InvalidateCollisionCache(a, b); // 跳帮单位位置可能改变
                    continue;
                }

                if (vRel >= CollisionExplosionSpeedThreshold)
                {
                    TriggerExplosion(a, b, pa, pb, va, vb, nDir, vRel, ox, oy, oz, destroyedMissiles);
                }
                else if (vRel > 0)
                {
                    ResolveImpact(a, b, pa, pb, va, vb, nDir, vRel, ox, oy, oz);
                }
                else
                {
                    // 正在分离但 AABB 仍重叠：仅位置分离防卡死
                    SeparatePositions(a, b, ox, oy, oz);
                    if (DebugDetailedCollision)
                    {
                        Log.Message(string.Format("[ShipCollision] G{0} vs G{1} 分离中重叠，仅位置分离 重叠=({2:F0},{3:F0},{4:F0})m",
                            a.GroupId, b.GroupId, ox, oy, oz));
                    }
                }
                // 任何碰撞解决都可能改变 a/b 位置 → 失效缓存，后续对该组的对重建
                InvalidateCollisionCache(a, b);
            }

            // 转化交接统一销毁（pair 循环外：字典懒迭代器内 DestroyGroup 抛异常）
            if (destroyedMissiles.Count > 0)
            {
                var avmDestroy = GetAvm();
                foreach (int gid in destroyedMissiles)
                {
                    if (PerfProbe.Enabled) PerfProbe.DestroyedConsume(gid, Find.TickManager.TicksGame); // 探针：同一步重复销毁检测
                    avmDestroy?.UnregisterGroup(gid); // 弹群登记注销（§11）
                    spaceGame.DestroyGroup(gid);
                }
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
            if (DebugDetailedCollision)
            {
                Log.Message(string.Format("[ShipCollision] G{0} vs G{1} tunneling 捕获：G{1} 停于接触点 ({2:F0},{3:F0},{4:F0})m",
                    aGroupId, b.GroupId, stopPos.x, stopPos.y, stopPos.z));
            }
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
            if (PerfProbe.Enabled) PerfProbe.NoteContact(); // 探针：接触解算唯一收口（验收器碰撞阶段判据）
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

            if (DebugDetailedCollision)
            {
                Log.Message(string.Format("[ShipCollision] 撞击等效 G{0} vs G{1} 接近速度={2:F1}m/s 质量={3:F0}/{4:F0}kg 冲量j={5:F2} 已分离",
                    a.GroupId, b.GroupId, vRel, ma, mb, j));
            }
        }

        /// <summary>高速真爆炸：范围损伤 + 径向冲量 + 爆炸视觉。
        /// deferredEmptyGroups：本调用处于 pair 迭代内，AoE 击毁导弹成员后变空的组延后销毁（C）。</summary>
        private void TriggerExplosion(SpaceEntityGroup a, SpaceEntityGroup b,
            DVector3 pa, DVector3 pb, DVector3 va, DVector3 vb, DVector3 nDir, double vRel,
            double ox, double oy, double oz, HashSet<int> deferredEmptyGroups = null)
        {
            double ma = GetGroupMass(a);
            double mb = GetGroupMass(b);
            double mEff = ma * mb / (ma + mb);
            double E = 0.5 * mEff * vRel * vRel;

            DVector3 hitPoint = (pa + pb) * 0.5;
            double R = CollisionExplosionBaseRadius + CollisionExplosionRadiusK * Math.Pow(E, 1.0 / 3.0);
            double D0 = CollisionExplosionDamageK * Math.Sqrt(E);

            int frameId = GetCombatGrid()?.ResolveFrameId(a.CelestialBodyId) ?? -1;
            int damaged = ApplyExplosionDamage(frameId, hitPoint, R, D0, -1, deferredEmptyGroups);
            int pushed = ApplyExplosionImpulse(frameId, hitPoint, R, E);

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

            if (DebugDetailedCollision)
            {
                Log.Message(string.Format("[爆炸] G{0} vs G{1} 碰撞点=({2:F0},{3:F0},{4:F0}) 半径={5:F0}m 能量={6:F2}J vRel={7:F1}m/s 损伤Part={8} 冲量船={9}",
                    a.GroupId, b.GroupId, hitPoint.x, hitPoint.y, hitPoint.z, R, E, vRel, damaged, pushed));
            }
        }

        /// <summary>
        /// 爆炸范围损伤：半径 R 内所有 Part 按 (1 - dist/R) 衰减扣 HP。
        /// excludeGroupId：豁免组（B，2026-09-09 用户定稿——成员死亡爆炸不作用于自家残组）。
        /// emptiedMissileGroups：C（2026-09-09）——导弹实体被打到部件全毁**当场脱队**
        /// （RemoveMissileEntity，只移实体不销毁组，安全），组空则把组 id 交给调用方销毁
        /// （射弹路径当场销毁；pair 迭代内延后到循环外）。此前滞后 120 tick 由 AVM 周期清理兜底。
        /// </summary>
        /// <param name="frameId">爆炸所在参考系（CombatGrid.ResolveFrameId），候选组由该帧细网格给出。</param>
        private int ApplyExplosionDamage(int frameId, DVector3 center, double R, double D0,
            int excludeGroupId = -1, HashSet<int> emptiedMissileGroups = null)
        {
            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null) return 0;
            var combatGrid = GetCombatGrid();
            if (combatGrid == null) return 0;
            var avm = GetAvm();
            var newlyDeadMissiles = new List<(int groupId, int entityId)>();

            int damaged = 0;

            // 性能第 15 条：候选改走细网格（半径 R 的立方体即保守超集——部件的世界位必落在本船
            // 注册 AABB 内，而注册覆盖 AABB 的全部格）。成本 O(全场组数) → O(R 内格数，R≈1.5km 即 1~8 格)，
            // 与船队总量解耦。网格每物理步由 SyncAllShipsToGrid 保鲜（含深空帧 -1）。
            var candidates = explosionCandidateResults;
            combatGrid.CollectShipsInCube(frameId, center, R, candidates);

            if (PerfProbe.Enabled)
            {
                PerfProbe.ExplosionQuery("AoE损伤", frameId, R, candidates.Count);
                if (PerfProbe.ShadowChecks && PerfProbe.ShouldShadowExplosion())
                    ProbeExplosionShadow("AoE损伤", frameId, center, R, candidates, excludeGroupId);
            }

            foreach (var group in candidates)
            {
                if (!group.IsActive || !group.IsCollidable) continue;
                if (excludeGroupId >= 0 && group.GroupId == excludeGroupId) continue;

                DVector3 gpos = GetGroupPos(group);

                // C：导弹群才做"击毁即脱队"收集（普通船部件全毁维持现语义）
                var missileRec = avm != null ? avm.GetRecord(group.GroupId) : null;

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
                            if (DebugDetailedCollision)
                            {
                                Log.Message(string.Format("[爆炸损伤] Entity{0} Part[{1}]\"{2}\" HP≤0 被摧毁 dist={3:F0}m",
                                    eid, part.partId, part.defName, dist));
                            }
                        }
                    }

                    // C：本实体部件全毁（含此前遗留的空壳）→ 当场脱队（组迭代结束后统一执行）
                    if (missileRec != null && entity.Parts.Count > 0)
                    {
                        bool allDead = true;
                        foreach (var pt in entity.Parts)
                            if (!pt.isDestroyed) { allDead = false; break; }
                        if (allDead) newlyDeadMissiles.Add((group.GroupId, eid));
                    }
                }
            }

            // C：组迭代结束后执行离群（不在 GetAllGroups/GetEntityIds 迭代中改集合）
            foreach (var (gid, eid) in newlyDeadMissiles)
            {
                var rec = avm?.GetRecord(gid);
                if (rec == null) continue;
                if (avm.RemoveMissileEntity(rec, eid, "AoE 击毁") && emptiedMissileGroups != null)
                    emptiedMissileGroups.Add(gid);
            }
            return damaged;
        }

        /// <summary>
        /// 爆炸径向冲量：范围内船被炸飞，Δv = K·√E·(1-dist/R)/mass。
        /// excludeGroupId：豁免组（B）。空实体组跳过（冲量模型除以组质量，空组会除零）。
        /// </summary>
        /// <summary>
        /// 探针（第 15 条影子校验）：按优化前的旧算法（全场 GetAllGroups + R+100km 球筛 + 逐部件距离）
        /// 重算一遍候选，与网格候选集比对，专门回答"网格会不会漏伤"：
        ///   · 旧扫有、网格没有、且**确有部件在 R 内** → 真实漏伤（红字，判据必须 0）
        ///   · 旧扫有、网格没有、但无部件在 R 内 → 只是旧球筛 100km 余量的保守差异（正常，仅计数）
        /// 逐部件仍用实时位置（与两版实现同口径），故差异只来自候选枚举本身。
        /// </summary>
        private void ProbeExplosionShadow(string tag, int frameId, DVector3 center, double R,
            HashSet<SpaceEntityGroup> gridCandidates, int excludeGroupId)
        {
            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null) return;
            double rangeCheck = R + 100000.0; // 旧实现的球快筛余量（原样保留以复现旧候选集）
            int oldScan = 0, oldOnly = 0, missed = 0;
            string firstMiss = "";
            foreach (var group in spaceGame.GetAllGroups())
            {
                if (!group.IsActive || !group.IsCollidable) continue;
                if (excludeGroupId >= 0 && group.GroupId == excludeGroupId) continue;
                DVector3 gpos = GetGroupPos(group);
                if ((gpos - center).sqrMagnitude > rangeCheck * rangeCheck) continue;
                oldScan++;
                if (gridCandidates.Contains(group)) continue;
                oldOnly++;

                bool partInRange = false;
                foreach (int eid in group.GetEntityIds())
                {
                    var entity = spaceGame.GetEntityById(eid);
                    if (entity == null) continue;
                    foreach (var part in entity.Parts)
                    {
                        if (part.isDestroyed) continue;
                        if ((gpos + entity.Position + part.localPosition - center).magnitude <= R) { partInRange = true; break; }
                    }
                    if (partInRange) break;
                }
                if (partInRange)
                {
                    missed++;
                    if (firstMiss.Length == 0)
                        firstMiss = $"G{group.GroupId} 组位距心={(gpos - center).magnitude:F0}m";
                }
            }
            PerfProbe.ExplosionShadowResult(tag, oldScan, oldOnly, missed, firstMiss);
        }

        /// <param name="frameId">爆炸所在参考系（CombatGrid.ResolveFrameId）。</param>
        private int ApplyExplosionImpulse(int frameId, DVector3 center, double R, double E, int excludeGroupId = -1)
        {
            var spaceGame = GetSpaceGameComp();
            if (spaceGame == null) return 0;
            var combatGrid = GetCombatGrid();
            if (combatGrid == null) return 0;

            int pushed = 0;
            double sqRtE = Math.Sqrt(E);

            // 第 15 条：同 ApplyExplosionDamage 走细网格。组位置必在注册 AABB 内
            // （GetGroupWorldBounds 以 groupPos 为初值做并集），故 cube(R) 仍是本判定的保守超集。
            var candidates = explosionCandidateResults;
            combatGrid.CollectShipsInCube(frameId, center, R, candidates);

            if (PerfProbe.Enabled)
            {
                PerfProbe.ExplosionQuery("爆炸冲量", frameId, R, candidates.Count);
                if (PerfProbe.ShadowChecks && PerfProbe.ShouldShadowExplosion())
                    ProbeExplosionShadow("爆炸冲量", frameId, center, R, candidates, excludeGroupId);
            }

            foreach (var group in candidates)
            {
                if (!group.IsActive || !group.IsCollidable) continue;
                if (excludeGroupId >= 0 && group.GroupId == excludeGroupId) continue;
                if (group.GetEntityIds().Count == 0) continue; // 空组（待延后销毁）：防除零

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
        /// <summary>未注册组的共享空容器（第 7 条）：调用方只读（TargetingSystem 索引 / PFView 读 Count），
        /// 不要往里写——它是共享实例。</summary>
        private static readonly List<WeaponInstance> EmptyWeaponList = new List<WeaponInstance>();

        public List<WeaponInstance> GetGroupWeapons(int groupId)
        {
            if (groupWeapons.TryGetValue(groupId, out var weapons))
                return weapons;

            // Lite 裁剪（§5.5）：实体级武器收集已删除；M4 前恒空（Map 炮塔桥接填充后立即可用）。
            // 第 7 条：返回共享空实例——此前每步每组都 new 一个空 List 再扔掉（战斗期每组每步一次分配）。
            if (PerfProbe.Enabled) PerfProbe.NoteEmptyWeaponListHit();
            return EmptyWeaponList;
        }

        /// <param name="targetVelocity">目标当前世界系速度（用于提前量计算）。null = 瞄准当前位置。</param>
        /// <param name="targetGroupId">目标 group id（导弹默认 Mission=Attack 用；null = 导弹出生即 Standby）。射弹/射线忽略。</param>
        /// <param name="missileFollowMode">导弹出生 Mission = Following（发射器建筑释放：目标跟随发射器、
        /// 不立即进终末，2026-09-06）。false = 出生即 Attack（fire-and-forget）。射弹/射线忽略。</param>
        /// <param name="aimOffsetLocal">导弹弹着偏置（目标局部米，y=0，§10 精确打击；零 = 瞄目标质心）。射弹/射线忽略。</param>
        /// <param name="volleyCount">导弹齐射枚数（弹群实体数；null = 武器 Def.missileCount）。射弹/射线忽略。</param>
        public void FireWeapon(int groupId, WeaponInstance weapon, DVector3 targetPosition, DVector3? spawnPosition = null, DVector3? targetVelocity = null, int? targetGroupId = null, bool missileFollowMode = false, DVector3 aimOffsetLocal = default, int? volleyCount = null)
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

            // 瞄准：射线瞬时命中瞄准当前位置；射弹带简单提前量（相对运动二次方程）。
            // Missile 无提前量（硬性分叉 §6）：出膛速度量级远低于炮弹且靠制导收敛，收敛归制导循环。
            DVector3 aimPos = targetPosition;
            if (weapon.def.weaponType != WeaponType.Ray && weapon.def.weaponType != WeaponType.Missile
                && targetVelocity.HasValue && weapon.def.projectileSpeed > 0f)
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

            // 导弹（自主武器平台）：创建弹群 + 注册 AutonomousVehicleManager。
            // 不切 Combat 档（分支内无 SetCombatModeImmediate）、不做弹道提前量（上方 aimPos 已排除）。
            if (weapon.def.weaponType == WeaponType.Missile)
            {
                FireMissileWeapon(group, spaceGame, groupId, weapon, spawnPos, direction, groupVelocity, targetGroupId, missileFollowMode, aimOffsetLocal, volleyCount);
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
                spawnVelocity = groupVelocity + muzzleVelocity,
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
            GetTimeScaleManager()?.SetCombatModeImmediate();

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
                var mgr = GetCelestialBodyManager();
                if (mgr != null)
                {
                    var cd = mgr.GetBodyById(group.circularCentralBodyId);
                    if (cd != null && cd.bodyType != CelestialBodyType.Planet)
                        centralVel = mgr.GetBodyLocalVelocity(group.circularCentralBodyId, physicsTickNow);
                }
            }
            double vRelMag = targetVelocity.HasValue ? (targetVelocity.Value - groupVelocity).magnitude : 0.0;
            if (DebugDetailedCombat)
            {
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
        public void FireFromMapBuilding(int groupId, WeaponInstance weapon, DVector3 spawnPos, DVector3 targetPos, DVector3? targetVelocity = null, int? targetGroupId = null, DVector3 aimOffsetLocal = default, int? volleyCount = null)
        {
            // 建筑释放的导弹 = Following：目标跟随发射器（定位机换目标自动同步），不立即进终末
            FireWeapon(groupId, weapon, targetPos, spawnPos, targetVelocity, targetGroupId, missileFollowMode: true, aimOffsetLocal: aimOffsetLocal, volleyCount: volleyCount);
        }

        // ════════════════════════════════════════════
        //  导弹发射（FireWeapon Missile 分支，《导弹系统设计》§6 / §0.2 K1-K3 / K7 / K10）
        // ════════════════════════════════════════════

        /// <summary>
        /// Missile 分支：创建导弹（N=1 单实体 = 原单发路径；N&gt;1 弹群 = 1 group + N 实体，§5 2026-09-08），
        /// 出生点沿出膛方向外推到母船 AABB 外（K1）、继承母船速度 + 出膛速度（不做提前量），
        /// 默认 Mission = Attack(目标 group) 或 Standby（targetGroupId 为空），注册 AutonomousVehicleManager。
        /// 成本 = 扣 WeaponInstance.ammo：公共路径 weapon.Fire() 已扣 1，齐射 N 在此补扣至 N（K3）。
        /// 弹体/推进/D15 主推/弹头参数全部来自 MissileDef（K2/K10）。
        /// aimOffsetLocal = 弹着偏置（§10 精确打击：目标局部米 y=0，随记录进制导瞄准）。
        /// volleyCount = 齐射枚数（null = 武器 Def.missileCount）。
        /// </summary>
        private void FireMissileWeapon(SpaceEntityGroup launcherGroup, SpaceGameComp spaceGame, int launcherGroupId,
            WeaponInstance weapon, DVector3 spawnPos, DVector3 direction, DVector3 groupVelocity, int? targetGroupId, bool followMode = false, DVector3 aimOffsetLocal = default, int? volleyCount = null)
        {
            var wdef = weapon.def;
            var missileDef = DefDatabase<MissileDef>.GetNamedSilentFail(wdef.missileDefName);
            if (missileDef == null)
            {
                Log.Warning(string.Format("[导弹发射] 武器 {0} 的 missileDefName='{1}' 未找到，中止发射（冷却已消耗，请检查 Def 配置）",
                    wdef.defName, wdef.missileDefName));
                return;
            }

            var manager = GetAvm();
            if (manager == null)
            {
                Log.Warning("[导弹发射] AutonomousVehicleManager 未找到，中止发射");
                return;
            }

            int logicalCount = Mathf.Clamp(volleyCount ?? wdef.missileCount, 1, MaxVolleyCount);

            // ── 出生点：出膛点沿出膛方向外推到母船 AABB 出口之外（K1：发射器侧职责，防出生与母船重叠）──
            DVector3 birthPos = ExtrapolateOutsideMothership(spawnPos, direction, missileDef, launcherGroup, spaceGame);

            // 初速 = 母船当前速度 + 出膛速度（朝目标当前位置方向；不做目标速度提前量，收敛归制导循环）
            double muzzleSpeed = Math.Max(wdef.missileSpeed, 1.0);
            DVector3 birthVelocity = groupVelocity + direction * muzzleSpeed;

            // ── 弹群创建（工厂供调试场景复用；N=1 单实体路径保持原语义零回归）──
            SpaceEntityGroup group;
            int missileEntityId;
            bool volleySqueezed = false;
            if (logicalCount > 1)
            {
                group = CreateMissileVolley(missileDef, launcherGroup.FactionId, birthPos, birthVelocity,
                    launcherGroup.CelestialBodyId, direction, logicalCount, out missileEntityId, out volleySqueezed);
                if (volleySqueezed)
                {
                    Log.Warning(string.Format("[导弹发射] 弹群 N={0} 超阵型边长上限（{1:F0}m），已压间距强行创建",
                        logicalCount, missileDef.arraySideLimitMeters));
                    Messages.Message(string.Format("齐射 {0} 枚超出安全阵列（边长上限 {1:F0}m），已压缩间距强行发射——兄弟弹可能处于弹头 AoE 致死带内",
                        logicalCount, missileDef.arraySideLimitMeters), MessageTypeDefOf.RejectInput, false);
                }
            }
            else
            {
                group = CreateMissileGroup(missileDef, launcherGroup.FactionId, birthPos, birthVelocity,
                    launcherGroup.CelestialBodyId, direction, out missileEntityId);
            }
            int missileGroupId = group.GroupId;

            // 齐射 N 按弹药计数（K3）：公共路径已扣 1，补扣余下 N-1（ammo<0 = 无限不动）
            if (weapon.ammo > 0 && logicalCount > 1)
                weapon.ammo = Math.Max(0, weapon.ammo - (logicalCount - 1));

            // 队首收敛余量警告（2026-09-08 三轮实测：队列长 14km 打 15.2km 靶 → 队首出生即在靶
            // 1.2km 处，终末收敛时间不足擦过）。前沿余量 ≈ 目标距离 − 队列长 < 5km 提示。
            if (logicalCount > 1 && targetGroupId.HasValue)
            {
                double queueLen = (logicalCount - 1) * MissileVolleyMath.ComputeSpacing(
                    missileDef.safetySpacingMeters, missileDef.arraySideLimitMeters, logicalCount,
                    missileDef.blastRadius * 1.2f);
                var tg = spaceGame.GetGroupById(targetGroupId.Value);
                double targetDist = tg != null ? (GetGroupPos(tg) - birthPos).magnitude : 0.0;
                double frontRunUp = targetDist - queueLen;
                if (tg != null && frontRunUp < 5000.0)
                    Log.Warning(string.Format(
                        "[导弹发射] 队首收敛余量不足：目标 {0:F1}km − 队列长 {1:F1}km = 前沿余量 {2:F1}km（&lt;5km，队首终末收敛时间受限，建议拉远目标或减小齐射数）",
                        targetDist / 1000.0, queueLen / 1000.0, frontRunUp / 1000.0));
            }

            // 默认 Mission：建筑释放（followMode）→ Following（目标跟随发射器，不立即进终末，
            // 玩家在合适时机从面板下 Attack）；其他来源有目标 → Attack（fire-and-forget 基线）；
            // 无目标 → Standby（K7，等待玩家/AI 指令）
            int volleyId = manager.AllocVolleyId();
            var mission = !targetGroupId.HasValue
                ? AutonomousVehicleManager.MissionType.Standby
                : followMode
                    ? AutonomousVehicleManager.MissionType.Following
                    : AutonomousVehicleManager.MissionType.Attack;
            manager.RegisterMissileLeader(missileGroupId, missileEntityId, volleyId, launcherGroupId,
                logicalCount, missileDef.defName, wdef.defName, mission, targetGroupId ?? -1, aimOffsetLocal);

            // ── 发射摘要（无条件，一次齐射一条）：出生点 / 速度继承分解 / Mission ──
            Log.Message(string.Format(
                "[导弹发射] G{0} volley={1} N={2} 领弹G{3}/E{4} 目标G{5} Mission={6} 出生=({7:F0},{8:F0},{9:F0})m 出膛方向=({10:F3},{11:F3},{12:F3}) 继承船速={13:F0}m/s 出膛速={14:F0}m/s 合速={15:F0}m/s 主工质={16:F0}kg RCS={17:F0}kg",
                launcherGroupId, volleyId, logicalCount, missileGroupId, missileEntityId,
                targetGroupId ?? -1, mission,
                birthPos.x, birthPos.y, birthPos.z,
                direction.x, direction.y, direction.z,
                groupVelocity.magnitude, muzzleSpeed, birthVelocity.magnitude,
                missileDef.fuelMass, missileDef.rcsFuelMass));

            // 出生点相对母船偏移（详细日志门控）：验证 K1 外推量
            if (DebugDetailedCombat)
            {
                DVector3 motherPos = GetGroupPos(launcherGroup);
                DVector3 offset = birthPos - motherPos;
                Log.Message(string.Format(
                    "  └ [导弹细节] 母船G{0} 船心=({1:F0},{2:F0},{3:F0})m 出生相对船心偏移=({4:F0},{5:F0},{6:F0})m 外推后离开盒距离={7:F0}m 出膛点=({8:F0},{9:F0},{10:F0})m",
                    launcherGroupId, motherPos.x, motherPos.y, motherPos.z,
                    offset.x, offset.y, offset.z,
                    (birthPos - spawnPos).magnitude,
                    spawnPos.x, spawnPos.y, spawnPos.z));
            }
        }

        /// <summary>
        /// 导弹弹群创建工厂（FireMissileWeapon 发射链与调试场景——敌弹/拦截/巡航测试——共用）：
        /// 1 group + 1 领弹实体（巡航单等效体），参数全来自 MissileDef（K2/K10/K11）：
        /// body Part（尺寸/干质量/HP）、主网满罐注入 + RCS 副网（异工质）、D15 主推轴、
        /// 敏捷转向率覆盖、出生姿态 = 局部主推轴（-Z）对准 birthDir（省初始大转向等待）。
        /// 调用方职责：出生点语义（避母船外推是发射侧）、速度继承、AutonomousVehicleManager 登记。
        /// </summary>
        internal SpaceEntityGroup CreateMissileGroup(MissileDef missileDef, int factionId,
            DVector3 birthPos, DVector3 birthVel, int celestialBodyId, DVector3 birthDir, out int entityId)
        {
            var spaceGame = GetSpaceGameComp();
            int missileGroupId = spaceGame.AllocGroupId();
            entityId = spaceGame.AllocEntityId();

            var group = new SpaceEntityGroup(missileGroupId, factionId)
            {
                IsActive = true,
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = celestialBodyId
            };
            Quaternion birthRotation = Quaternion.FromToRotation(Vector3.back,
                new Vector3((float)birthDir.x, (float)birthDir.y, (float)birthDir.z));
            group.CurrentOrbit = new OrbitState
            {
                position = birthPos,
                velocity = birthVel,
                rotation = birthRotation
            };
            spaceGame.RegisterGroup(group);
            // D15 转向率覆盖：默认 5°/s 对追导太慢（转 90° 要 18s），导弹用 MissileDef 敏捷值（20°/s）
            group.maxAngularRate = missileDef.maxAngularRateDegrees * Mathf.Deg2Rad;

            // 弹体实体（弹体 Part + 满罐主网/RCS 推进）——单发/弹群共用构建器（2026-09-08 思路 A 去重）
            BuildMissileEntity(group, spaceGame, missileDef, factionId, entityId, birthRotation, DVector3.zero);
            return group;
        }

        /// <summary>
        /// 导弹实体构建器（单发 CreateMissileGroup 与弹群 CreateMissileVolley 共用，2026-09-08 思路 A）：
        /// body Part（尺寸定 AABB / HullMassComponent 定干质量 / hp = part HP，K10）+ D15 主推轴 +
        /// **每枚标准满罐**主网/RCS（工质归实体自己——多实体弹群由物理核心 GroupPropulsionAggregator
        /// 按 N 源聚合燃烧，无需任何 ×N 记账）。
        /// localOffset = 组内局部偏移（世界轴向；单发为零）。注册实体并入组。
        /// </summary>
        private void BuildMissileEntity(SpaceEntityGroup group, SpaceGameComp spaceGame,
            MissileDef missileDef, int factionId, int entityId, Quaternion rotation, DVector3 localOffset)
        {
            var entity = new SpaceEntity(entityId, factionId)
            {
                HasMap = false,
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true
            };
            entity.GroupId = group.GroupId;
            entity.SetPositionVelocity(localOffset, DVector3.zero);
            entity.SetRotation(rotation); // 与 group 姿态一致（命中几何/渲染立即正确）

            // body Part（弹体：尺寸定 AABB / HullMassComponent 定干质量 / hp 为 part HP，K10）
            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Missile",
                localPosition = DVector3.zero,
                localBoundsSize = new DVector3(missileDef.bodySize.x, missileDef.bodySize.y, missileDef.bodySize.z),
                hp = missileDef.hp,
                isIndestructible = false
            };
            part.AddComponent(new HullMassComponent { mass = missileDef.dryMass });
            entity.SetParts(new List<SpacePart> { part });

            // D15 主推方向型：主推轴 body 局部（K11）
            if (missileDef.mainThrustDirectional && missileDef.mainAxisLocal.sqrMagnitude > 1e-9f)
                entity.mainAxisLocal = new DVector3(missileDef.mainAxisLocal.x, missileDef.mainAxisLocal.y, missileDef.mainAxisLocal.z);

            // 主网（满罐注入 = 作战半径）+ 弱 RCS 副网（与主网异工质，K11）
            var mainProp = missileDef.Propellant;
            if (mainProp == null)
                Log.Warning(string.Format("[导弹] MissileDef {0} 主网工质 '{1}' 未找到", missileDef.defName, missileDef.propellantDefName));
            else
                entity.AddPropulsion(new PropulsionData
                {
                    propellant = mainProp,
                    ve = missileDef.ve,
                    massFlow = missileDef.massFlow,
                    mass = missileDef.fuelMass,
                    capacity = missileDef.fuelMass
                });
            if (missileDef.hasRcs)
            {
                var rcsProp = missileDef.RcsPropellant;
                if (rcsProp == null)
                    Log.Warning(string.Format("[导弹] MissileDef {0} RCS 工质 '{1}' 未找到", missileDef.defName, missileDef.rcsPropellantDefName));
                else
                    entity.AddPropulsion(new PropulsionData
                    {
                        propellant = rcsProp,
                        ve = missileDef.rcsVe,
                        massFlow = missileDef.rcsMassFlow,
                        mass = missileDef.rcsFuelMass,
                        capacity = missileDef.rcsFuelMass,
                        isVectoredRcs = true
                    });
            }

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);
        }

        /// <summary>
        /// 齐射弹群工厂（§5，2026-09-08 思路 A）：1 group + count 个导弹实体，**每枚自带标准满罐**——
        /// 群级燃烧由物理核心 GroupPropulsionAggregator 按 N 源同步（数学精确 ≡ N 台同型引擎）。
        /// 散布 = **深度队列**（2026-09-08 用户定稿：方形阵列同深度横排成员到达差离散于物理步
        /// （33ms），先转化者弹头 AoE（致死带 ~1km）必杀后到兄弟 → 方形废弃）：每枚沿出膛方向排开、
        /// 同深度仅 1 枚，深度间距 = min(安全格距, 边长上限/(N−1))（N=8 → 10km/7 ≈ 1.43km > 致死带
        /// 加裕量）——解离后沿航向队列自然错峰到达，前弹 AoE 打不到后到兄弟；横向仅装饰性微偏
        /// （±~0.2·间距，到达差可忽略）。深度自出膛点向前排开（不穿母船）；N ≤ MaxVolleyCount 恒可排布。
        /// 领弹（记录实体）置队列最前方、优先创建。
        /// </summary>
        private SpaceEntityGroup CreateMissileVolley(MissileDef missileDef, int factionId,
            DVector3 birthPos, DVector3 birthVel, int celestialBodyId, DVector3 birthDir, int count,
            out int leaderEntityId, out bool squeezed)
        {
            var spaceGame = GetSpaceGameComp();
            int missileGroupId = spaceGame.AllocGroupId();
            count = Mathf.Clamp(count, 1, MaxVolleyCount);
            squeezed = false;

            DVector3 axisDepth = birthDir.normalized;
            // 深度间距：自适应取「安全格距」与「总长上限/(N−1)」较小者，并以**弹头爆炸冲量作用半径**
            // 为下限（口径统一在 MissileVolleyMath，纯测试固化不变量：间距不足时后弹会被前弹爆炸
            // 侧推数百 m/s、错峰队形被打乱——2026-09-08 弹群仿真复现）
            float minSpacing = missileDef.blastRadius * 1.2f;
            float spacing = count > 1
                ? (float)MissileVolleyMath.ComputeSpacing(missileDef.safetySpacingMeters, missileDef.arraySideLimitMeters, count, minSpacing)
                : 0f;
            // 因爆炸半径下限被迫超出总长上限时提示（原本的压间距分支已由下限保护取代）
            squeezed = count > 1 && (count - 1) * spacing > missileDef.arraySideLimitMeters;
            DVector3 axisCross = DVector3.Cross(axisDepth, DVector3.up);
            if (axisCross.sqrMagnitude < 1e-12)
                axisCross = DVector3.Cross(axisDepth, DVector3.right); // 出膛方向与天顶平行 → 换基准
            axisCross = axisCross.normalized;

            var group = new SpaceEntityGroup(missileGroupId, factionId)
            {
                IsActive = true,
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = celestialBodyId
            };
            Quaternion birthRotation = Quaternion.FromToRotation(Vector3.back,
                new Vector3((float)birthDir.x, (float)birthDir.y, (float)birthDir.z));
            // 群原点 = 队列中点：出膛点沿深度前移半队长（后排实体仍在出膛点之前，不穿母船）
            float mid = (count - 1) * 0.5f * spacing;
            group.CurrentOrbit = new OrbitState
            {
                position = birthPos + axisDepth * mid,
                velocity = birthVel,
                rotation = birthRotation
            };
            spaceGame.RegisterGroup(group);
            group.maxAngularRate = missileDef.maxAngularRateDegrees * Mathf.Deg2Rad;

            // 填列：k=0 = 领弹（记录实体）置队列最前方（先到先转化），其余依次靠后
            leaderEntityId = -1;
            for (int k = 0; k < count; k++)
            {
                int depthIndex = count - 1 - k; // k=0 → 最前（depthIndex = count−1）
                float along = (depthIndex - (count - 1) * 0.5f) * spacing; // 相对群原点
                // 横向装饰性微偏（±0.05·间距，交替两侧）：只用于避免同深度完全共线，**必须小**——
                // 队首距靶可能只有 ~1km，0.2·间距（400m）的横偏在 1km 处是 22° 视线偏角，
                // 转向+修正来不及（2026-09-08 游戏内队首擦过实证）→ 收到 0.05（100m ≈ 6.5°）。
                float jitter = (k % 2 == 0 ? -0.05f : 0.05f) * spacing;
                DVector3 offset = axisDepth * along + axisCross * jitter;
                int eid = spaceGame.AllocEntityId();
                BuildMissileEntity(group, spaceGame, missileDef, factionId, eid, birthRotation, offset);
                if (k == 0) leaderEntityId = eid;
            }
            if (leaderEntityId < 0)
            {
                foreach (int eid in group.GetEntityIds()) { leaderEntityId = eid; break; }
            }

            Log.Message(string.Format(
                "[导弹弹群] G{0} N={1} 深度队列 间距={2:F0}m 队长={3:F0}m 领弹E{4}（最前） 深度轴=({5:F3},{6:F3},{7:F3}) 群原点=({8:F0},{9:F0},{10:F0})m｜间距依据：弹头爆炸半径={11:F0}m 下限={12:F0}m 超长上限={13}",
                group.GroupId, count, spacing, (count - 1) * spacing, leaderEntityId,
                axisDepth.x, axisDepth.y, axisDepth.z,
                group.CurrentOrbit.position.x, group.CurrentOrbit.position.y, group.CurrentOrbit.position.z,
                missileDef.blastRadius, minSpacing, squeezed ? "是（间距被爆炸半径下限抬高）" : "否"));

            return group;
        }

        /// <summary>
        /// K1 出生点外推：出膛点沿出膛方向推进到母船 group 世界 AABB（旋转后并集）出口之外，
        /// 再外推「弹体最大半轴 + 5m」缓冲（弹体质心完全离开母船盒，防出生重叠误判碰撞）。
        /// slab 法统一处理出膛点盒内/盒外：盒内向外出 = 正向出口距离；盒外背向/不穿盒 → 原出膛点不动。
        /// </summary>
        private DVector3 ExtrapolateOutsideMothership(DVector3 spawnPos, DVector3 direction, MissileDef missileDef,
            SpaceEntityGroup launcherGroup, SpaceGameComp spaceGame)
        {
            GetGroupWorldAabb(launcherGroup, GetGroupPos(launcherGroup), spaceGame, out DVector3 gMin, out DVector3 gMax);
            if (gMax.x < gMin.x)
                return spawnPos; // group 无碰撞实体 → 无需外推

            const double eps = 1e-9;
            double tNear = double.MinValue;
            double tFar = double.MaxValue;
            // x 轴 slab
            if (Math.Abs(direction.x) < eps)
            {
                if (spawnPos.x < gMin.x || spawnPos.x > gMax.x) return spawnPos; // 平行且已出该轴范围 → 不穿盒
            }
            else
            {
                double t1 = (gMin.x - spawnPos.x) / direction.x;
                double t2 = (gMax.x - spawnPos.x) / direction.x;
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                tNear = Math.Max(tNear, t1);
                tFar = Math.Min(tFar, t2);
                if (tNear > tFar) return spawnPos;
            }
            // y 轴 slab
            if (Math.Abs(direction.y) < eps)
            {
                if (spawnPos.y < gMin.y || spawnPos.y > gMax.y) return spawnPos;
            }
            else
            {
                double t1 = (gMin.y - spawnPos.y) / direction.y;
                double t2 = (gMax.y - spawnPos.y) / direction.y;
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                tNear = Math.Max(tNear, t1);
                tFar = Math.Min(tFar, t2);
                if (tNear > tFar) return spawnPos;
            }
            // z 轴 slab
            if (Math.Abs(direction.z) < eps)
            {
                if (spawnPos.z < gMin.z || spawnPos.z > gMax.z) return spawnPos;
            }
            else
            {
                double t1 = (gMin.z - spawnPos.z) / direction.z;
                double t2 = (gMax.z - spawnPos.z) / direction.z;
                if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
                tNear = Math.Max(tNear, t1);
                tFar = Math.Min(tFar, t2);
                if (tNear > tFar) return spawnPos;
            }

            if (tFar < 0.0)
                return spawnPos; // 盒整体在射线反方向 → 出膛点已在盒外

            double bodyHalf = Math.Max(Math.Max(missileDef.bodySize.x, missileDef.bodySize.y), missileDef.bodySize.z) * 0.5;
            return spawnPos + direction * (Math.Max(tFar, 0.0) + bodyHalf + 5.0);
        }

        // ════════════════════════════════════════════
        //  撞击式命中语义（《导弹系统设计》§10：转化交接 / 拦截引爆 / 弹头 AoE）
        //  2026-09-08 双路径统一：整组转化（HandleMissileContact）已删除——导弹对一律走
        //  ResolveEntityContacts 逐实体解析（单实体 = 1×1 退化情形，几何等价）。
        // ════════════════════════════════════════════

        /// <summary>
        /// 导弹接触时间对齐复核（防扫掠假阳性）：把导弹本步运动段（missilePrev→missileCur）转到
        /// 目标随动参考系（prev 时刻减 prev 中心、当前减 cur 中心——与 CheckSegmentGroupHit 同构，
        /// 相对运动时间对齐），与目标当前盒做 slab。
        /// 通过 = 真实接触（out contactWorld = 参考系首接触点转回世界系，作为弹头出生点）；
        /// 不过 = 假阳性（扫掠并集的"上一步位置带"与刚出生/伴飞导弹时间不重合）。
        /// </summary>
        private static bool MissileSegmentHitsTarget(DVector3 missilePrev, DVector3 missileCur,
            DVector3 targetPrevCenter, DVector3 targetCurMin, DVector3 targetCurMax,
            out DVector3 contactWorld)
        {
            DVector3 curCenter = (targetCurMin + targetCurMax) * 0.5;
            DVector3 half = (targetCurMax - targetCurMin) * 0.5;
            DVector3 start = missilePrev - targetPrevCenter;
            DVector3 end = missileCur - curCenter;
            contactWorld = missileCur;
            if (!CombatGrid.RaySlabIntersection(start, end, -half, half, out double tEntry, out _, out _))
                return false;
            // 首接触点（目标系）→ 世界系：目标系点 + 目标当前中心
            double t = Math.Max(0.0, Math.Min(1.0, tEntry));
            contactWorld = start + (end - start) * t + curCenter;
            return true;
        }

        /// <summary>组内实体数（弹群多实体判定）。</summary>
        private static int CountGroupEntities(SpaceEntityGroup g)
        {
            int n = 0;
            foreach (int _ in g.GetEntityIds()) n++;
            return n;
        }

        /// <summary>本物理步时长（s，与 SpaceGameComp.UpdateAllMovements 同口径：UPDATE_INTERVAL_TICKS × 流速）。</summary>
        private double PhysicsStepSeconds()
        {
            var tsm = GetTimeScaleManager();
            return SpaceGameComp.UPDATE_INTERVAL_TICKS * (tsm != null ? tsm.PhysicsSpeedMultiplier : 1f);
        }

        /// <summary>
        /// 弹群逐实体接触解析（§5，2026-09-08）：x 侧（导弹，recX 非空）每个实体本步运动段 vs
        /// y 侧**每个实体**的世界盒做时间对齐复核——并集盒扫掠只作粗筛，10km 阵列空当不触发转化；
        /// 真接触 → 该实体独立转化弹头（接触点 = 弹头出生点）并走 AVM.RemoveMissileEntity 离群出口
        /// （多实体组只移实体；组空 → destroyedMissileIds 延后统一销毁）。
        /// 两阶段（先全部探测后执行）：y 侧同为导弹时保持"双方各自转化"的对称语义。
        /// 调用约束：pair 循环内（不销毁 group、不改 AVM 登记结构——空组销毁延后到 pair 循环外）。
        /// </summary>
        private void ResolveEntityContacts(SpaceEntityGroup xGroup, SpaceEntityGroup yGroup,
            AutonomousVehicleManager.VehicleRecord recX, GroupCollisionCache cx, GroupCollisionCache cy,
            AutonomousVehicleManager avm, SpaceGameComp spaceGame, CombatGrid combatGrid,
            HashSet<int> destroyedMissileIds)
        {
            if (recX == null || xGroup == null || yGroup == null) return;
            var missileDef = recX.missileDefName == null
                ? null
                : DefDatabase<MissileDef>.GetNamedSilentFail(recX.missileDefName);

            // 阶段 1：探测（几何快照，全部命中后统一执行——保持导弹-导弹互撞的对称转化）
            var contacts = new List<(int entityId, DVector3 point)>();
            foreach (int exId in new List<int>(xGroup.GetEntityIds()))
            {
                var ex = spaceGame.GetEntityById(exId);
                if (ex == null || !ex.IsCollidable) continue;
                // 首拍（组刚创建，无上一步位置）：按当前速度回推一个物理步作为段起点——
                // 既避免零长段退化判定（RaySlabIntersection 旧版对 dir=0 恒真 → 2026-09-08
                // 弹群场景首步 8 枚全转化），也防"首拍整步穿过目标"漏检（首拍步长可达 5.2km）。
                DVector3 xStart = cx.hasPrev
                    ? cx.prevPos + ex.Position
                    : cx.pos + ex.Position - GetGroupVelocity(xGroup) * PhysicsStepSeconds();
                DVector3 xEnd = cx.pos + ex.Position;
                foreach (int eyId in yGroup.GetEntityIds())
                {
                    var ey = spaceGame.GetEntityById(eyId);
                    if (ey == null || !ey.IsCollidable) continue;
                    ey.GetWorldAabb(out var relMin, out var relMax);
                    DVector3 prevCenter = (cy.hasPrev ? cy.prevPos : cy.pos) + ey.Position + (relMin + relMax) * 0.5;
                    DVector3 curMin = cy.pos + ey.Position + relMin;
                    DVector3 curMax = cy.pos + ey.Position + relMax;
                    if (MissileSegmentHitsTarget(xStart, xEnd, prevCenter, curMin, curMax, out var contact))
                    {
                        contacts.Add((exId, contact));
                        break; // ex 只转化一次
                    }
                }
            }
            // 详细门控（§5 场景验证：并集盒扫掠通过但逐实体复核空当放行的对照证据）
            if (DebugDetailedCollision && contacts.Count == 0)
            {
                Log.Message(string.Format(
                    "[弹群接触] G{0}(E{1}) vs G{2}(E{3}) 并集盒重叠但逐实体复核无真接触（阵列空当/擦边放行，不转化）",
                    xGroup.GroupId, CountGroupEntities(xGroup), yGroup.GroupId, CountGroupEntities(yGroup)));
            }

            // 阶段 2：执行转化 + 离群
            for (int i = 0; i < contacts.Count; i++)
            {
                int exId = contacts[i].entityId;
                if (!xGroup.ContainsEntity(exId)) continue; // 已被同 tick 前序对/前序命中转化
                if (missileDef == null)
                {
                    Log.Warning(string.Format("[导弹撞击] G{0} E{1} 弹体 Def 缺失，仅移除该实体", xGroup.GroupId, exId));
                    if (avm.RemoveMissileEntity(recX, exId, "撞击转化(Def缺失)"))
                        destroyedMissileIds.Add(xGroup.GroupId);
                    continue;
                }
                CreateWarheadProjectile(xGroup, recX, missileDef, contacts[i].point,
                    GetGroupVelocity(xGroup), spaceGame, combatGrid);
                Log.Message(string.Format(
                    "[导弹撞击] G{0} E{1}→G{2} 接触=({3:F0},{4:F0},{5:F0})m 单枚转化弹头（弹群剩余继续）",
                    xGroup.GroupId, exId, yGroup.GroupId,
                    contacts[i].point.x, contacts[i].point.y, contacts[i].point.z));
                if (avm.RemoveMissileEntity(recX, exId, "撞击转化"))
                    destroyedMissileIds.Add(xGroup.GroupId);
            }
        }

        /// <summary>
        /// 弹头射弹工厂：继承导弹当前位置/速度（方向随速度），照 FireWeapon 射弹生成样板
        /// （prevPhysicsTick 系列初始化防首步参考系回退误判；三处注册）。
        /// 弹头 = 普通射弹 + isWarhead/AoE 参数；命中结算由 CheckSegmentGroupHit 现有链完成。
        /// </summary>
        private void CreateWarheadProjectile(SpaceEntityGroup missileGroup,
            AutonomousVehicleManager.VehicleRecord rec, MissileDef missileDef,
            DVector3 pos, DVector3 vel, SpaceGameComp spaceGame, CombatGrid combatGrid)
        {
            SpawnWarheadProjectileCore(missileGroup.GroupId, missileGroup.FactionId, missileGroup.CelestialBodyId,
                rec.weaponDefName, missileDef, pos, vel, spaceGame, combatGrid);
        }

        /// <summary>
        /// 【调试】生成一枚**必定漏靶**的弹头，用于验 A2 的"2 物理秒一段"节奏与长期引力弯曲
        /// （《审查修复计划2026-09-14》§9 第 2 项）。
        /// 实战弹头在命中点出生、通常一个物理步内就结算 —— 段传播只走到"建段"，走不到"段内插值飞行"，
        /// 也验不到"漏靶弹头被引力弯成轨道垃圾"这条设计意图。走**同一条生成核心**，不另起实现。
        /// </summary>
        public bool SpawnDebugMissedWarhead(string missileDefName, int celestialBodyId, DVector3 pos, DVector3 vel)
        {
            var spaceGame = GetSpaceGameComp();
            var def = DefDatabase<MissileDef>.GetNamedSilentFail(missileDefName);
            if (spaceGame == null || def == null)
            {
                Log.Error(string.Format("[漏靶弹头测试] 前置缺失：spaceGame={0} def={1}", spaceGame != null, def != null));
                return false;
            }

            SpawnWarheadProjectileCore(-1, Faction.OfPlayer.loadID, celestialBodyId,
                null, def, pos, vel, spaceGame, GetCombatGrid());
            return true;
        }

        /// <summary>弹头生成的**唯一实现**（实战与调试共用，避免两条路各自演化）。</summary>
        private void SpawnWarheadProjectileCore(int sourceGroupId, int factionId, int celestialBodyId,
            string weaponDefName, MissileDef missileDef,
            DVector3 pos, DVector3 vel, SpaceGameComp spaceGame, CombatGrid combatGrid)
        {
            var warhead = new ProjectileInstance
            {
                sourceGroupId = sourceGroupId,
                factionId = factionId,
                weaponDef = weaponDefName == null ? null : DefDatabase<WeaponDef>.GetNamedSilentFail(weaponDefName),
                position = pos,
                velocity = vel,
                remainingLife = missileDef.warheadLifetimeSeconds,
                damage = missileDef.warheadDamage,
                armorPenetration = 0f,
                affectedByGravity = true, // 轨道环境：弹头随引力弯曲，脱靶自然衰减为轨道垃圾
                flybyDistance = 0f,
                trailEffect = false,
                prevPosition = pos,
                spawnPosition = pos,
                spawnVelocity = vel,
                spawnPhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                lastUpdateRealTick = Find.TickManager.TicksGame,
                prevUpdateRealTick = Find.TickManager.TicksGame,
                prevPhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                prevUpdatePhysicsTick = TimeScaleManager.GetPhysicsTickSafe(),
                systemBodyId = spaceGame.ResolveReferenceFrameId(celestialBodyId),
                isWarhead = true,
                blastRadius = missileDef.blastRadius,
                mapBlastRadiusCells = missileDef.mapBlastRadiusCells,
                blastCenterDamage = missileDef.blastCenterDamage,
                blastImpulseEnergy = missileDef.blastImpulseEnergy
            };
            activeProjectiles.Add(warhead);
            combatGrid?.AddProjectile(warhead);
            spaceGame.RegisterProjectileToGrid(warhead);
        }

        /// <summary>
        /// 弹头命中结算（§10）：part 大额直扣与 F3 Map 落点已由命中现链完成，
        /// 此处补 3D AoE（伤害线性衰减 + 径向冲量 + 扩散视觉），以精确命中点 worldHitPoint 为心。
        /// </summary>
        private void HandleWarheadImpact(ProjectileInstance proj, SpaceEntityGroup target, DVector3 worldHitPoint)
        {
            // B：弹头源自某弹群成员（sourceGroupId=来源组）——其死亡爆炸豁免自家残组；
            // C：AoE 击毁的导弹成员当场脱队，变空的组当场销毁（射弹路径不在 pair 迭代中，安全）
            var spaceGameHi = GetSpaceGameComp();
            var avmHi = GetAvm();
            var emptiedGroups = new HashSet<int>();
            // 帧号用射弹自带的 systemBodyId：它与网格注册同源（射弹碰撞链一直以它查网格）
            int damaged = ApplyExplosionDamage(proj.systemBodyId, worldHitPoint, proj.blastRadius, proj.blastCenterDamage,
                proj.sourceGroupId, emptiedGroups);
            ApplyExplosionImpulse(proj.systemBodyId, worldHitPoint, proj.blastRadius, proj.blastImpulseEnergy, proj.sourceGroupId);
            foreach (int gid in emptiedGroups)
            {
                avmHi?.UnregisterGroup(gid);
                spaceGameHi?.DestroyGroup(gid);
            }
            activeExplosions.Add(new ExplosionInstance
            {
                position = worldHitPoint,
                maxRadius = proj.blastRadius,
                remainingTicks = 60,
                totalTicks = 60,
                systemBodyId = proj.systemBodyId
            });
            Log.Message(string.Format(
                "[弹头命中] proj={0}→G{1} 爆炸中心=({2:F0},{3:F0},{4:F0})m 直扣={5:F0} AoE R={6:F0}m D0={7:F0} damaged={8}",
                proj.id, target.GroupId,
                worldHitPoint.x, worldHitPoint.y, worldHitPoint.z,
                proj.damage, proj.blastRadius, proj.blastCenterDamage, damaged));
        }

        /// <summary>
        /// 导弹就地引爆（拦截路径与自毁指令共用，§10：导弹被射弹击中 / 玩家下令自毁 =
        /// 在该位置引爆弹头 AoE 后销毁）。internal：AutonomousVehicleManager.IssueCommand(SelfDestruct) 调用。
        /// 多实体弹群 = 逐枚在其世界位引爆（整群自毁语义），单实体 = 原位引爆。
        /// ⚠️ 调用约束：ApplyExplosionDamage 内部遍历 groups 字典，调用点不得处于
        /// EnumerateShipPairs/GetAllGroups 的修改迭代中（射弹命中路径/UI 事件满足）。
        /// </summary>
        internal void ExplodeMissileAt(SpaceEntityGroup missileGroup,
            AutonomousVehicleManager.VehicleRecord rec, SpaceGameComp spaceGame)
        {
            // 整群自毁：逐枚各自爆心（2026-09-08 统一：单实体 = 单次循环，等价原位引爆；多实体覆盖散布）
            DVector3 gpos = GetGroupPos(missileGroup);
            int n = 0;
            foreach (int eid in missileGroup.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e == null) continue;
                ExplodeAtWorldPoint(missileGroup, rec, spaceGame, gpos + e.Position,
                    n == 0 ? "[导弹引爆]" : "[导弹群自毁]", missileGroup.GroupId);
                n++;
            }
            if (n == 0)
                ExplodeAtWorldPoint(missileGroup, rec, spaceGame, gpos, "[导弹引爆]", missileGroup.GroupId);

            var avm = GetAvm();
            avm?.UnregisterGroup(missileGroup.GroupId);
            spaceGame.DestroyGroup(missileGroup.GroupId);
        }

        /// <summary>
        /// 多实体弹群单枚引爆（拦截用，2026-09-08 §5）：爆心 = 被命中那枚实体的世界位；
        /// 只引爆不销毁群——实体离群由调用方走 AVM.RemoveMissileEntity 出口。
        /// </summary>
        internal void ExplodeMissileEntityAt(SpaceEntityGroup missileGroup,
            AutonomousVehicleManager.VehicleRecord rec, DVector3 center, SpaceGameComp spaceGame,
            int excludeGroupId = -1)
        {
            ExplodeAtWorldPoint(missileGroup, rec, spaceGame, center, "[导弹单枚引爆]", excludeGroupId);
        }

        /// <summary>弹头 AoE 核心（伤害/冲量/视觉/日志）。excludeGroupId：豁免自家残组（B）；
        /// AoE 击毁变空的导弹组当场销毁（C）——调用点均在射弹路径/UI 事件，不在 pair 迭代中。</summary>
        private void ExplodeAtWorldPoint(SpaceEntityGroup missileGroup,
            AutonomousVehicleManager.VehicleRecord rec, SpaceGameComp spaceGame,
            DVector3 center, string tag, int excludeGroupId = -1)
        {
            var missileDef = rec.missileDefName == null
                ? null
                : DefDatabase<MissileDef>.GetNamedSilentFail(rec.missileDefName);
            // B3：Def 缺失时仍继续（爆炸必须发生，不能因配置错误让弹头变成哑弹），
            // 但**不再静默**——否则"武器 Def 名写错"这类错误会表现为"弹头能炸但威力不对"，无从察觉。
            if (missileDef == null)
                Log.Error(string.Format("[弹头AOE] MissileDef '{0}' 未找到 → 使用兜底口径 R=1000 D0=500 E=1e9（请检查 Def 配置）",
                    rec.missileDefName ?? "<null>"));
            double R = missileDef != null ? missileDef.blastRadius : 1000.0;   // 兜底口径（已报错）
            double D0 = missileDef != null ? missileDef.blastCenterDamage : 500.0;
            double E = missileDef != null ? missileDef.blastImpulseEnergy : 1e9;

            var emptiedGroups = new HashSet<int>();
            int frameId = GetCombatGrid()?.ResolveFrameId(missileGroup.CelestialBodyId) ?? -1;
            int damaged = ApplyExplosionDamage(frameId, center, R, D0, excludeGroupId, emptiedGroups);
            foreach (int gid in emptiedGroups)
            {
                var avm = GetAvm();
                avm?.UnregisterGroup(gid);
                spaceGame.DestroyGroup(gid);
            }
            ApplyExplosionImpulse(frameId, center, R, E, excludeGroupId);
            activeExplosions.Add(new ExplosionInstance
            {
                position = center,
                maxRadius = R,
                remainingTicks = 60,
                totalTicks = 60,
                systemBodyId = spaceGame.ResolveReferenceFrameId(missileGroup.CelestialBodyId)
            });
            Log.Message(string.Format(
                "{0} G{1} 位置=({2:F0},{3:F0},{4:F0})m AoE R={5:F0}m D0={6:F0} damaged={7}",
                tag, missileGroup.GroupId, center.x, center.y, center.z, R, D0, damaged));
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

        // ════════════════════════════════════════════
        //  弹头 Map 范围爆炸（§10 结算的 Map 侧 AoE，2026-09-07）
        // ════════════════════════════════════════════

        /// <summary>
        /// 弹头命中带 Map 目标后的地图范围爆炸（3D AoE HandleWarheadImpact 的 Map 对应物）。
        /// 太空 Pocket Map 上 GenExplosion 会 NRE（terrain/roof 缺失，PFProjectile3D 既有注释）→ 自实现：
        /// 命中点（船体局部，已含姿态旋转）映射为地图中心格，半径 = mapBlastRadiusCells（**格**，1 格 = 1 m；
        /// 2026-09-09 用户定稿 5 格——与 3D AoE 1500m 分口径，Map 侧外科手术式拆建筑），
        /// 伤害沿距离线性衰减到 0，对格上建筑（墙/门/家具）与 Pawn 造成 Bomb 伤害——
        /// **拆站伤害语义（2026-09-09 用户定稿）：Map 无血条，进图拆建筑即细化伤害；站体 part 不可摧毁，
        /// "关键建筑被毁 → 空间站毁灭"链后续设计。**
        /// 爆炸条件（2026-09-07 用户定稿）：中心格地板不是"太空"——奥德赛 DLC TerrainDef Space
        /// （真空暴露、无实心甲板）；中心是太空 = 命中点无结构可炸 → 整个 Map 爆炸跳过；
        /// 半径内太空格同样跳过（不伤害不视觉）。
        /// </summary>
        private void SpawnMapWarheadBlast(Map map, DVector3 hitLocalMeters, float blastRadiusCells, float blastCenterDamage)
        {
            if (map == null || blastRadiusCells <= 0f || blastCenterDamage <= 0f) return;
            IntVec3 center = MapCoordMapper.LocalToMapCell(map,
                new Vector3((float)hitLocalMeters.x, 0f, (float)hitLocalMeters.z));
            if (!center.InBounds(map)) return;

            TerrainDef ground = map.terrainGrid.TerrainAt(center);
            if (IsSpaceFloor(ground))
            {
                Log.Message(string.Format(
                    "[弹头Map爆炸] 中心格=({0},{1}) 地板='{2}' 是太空 → 命中点无实心甲板，跳过 Map 范围爆炸（仅 3D AoE）",
                    center.x, center.z, ground?.defName ?? "null"));
                return;
            }

            // 先收集后结算：遍历中摧毁建筑会改动 thingGrid，爆炸结算统一在收集后执行
            var targets = new List<KeyValuePair<Thing, float>>(64);
            int radiusCells = Mathf.CeilToInt(blastRadiusCells);
            int buildings = 0, pawns = 0, spaceCells = 0;
            for (int x = Math.Max(center.x - radiusCells, 0); x <= Math.Min(center.x + radiusCells, map.Size.x - 1); x++)
            {
                for (int z = Math.Max(center.z - radiusCells, 0); z <= Math.Min(center.z + radiusCells, map.Size.z - 1); z++)
                {
                    var c = new IntVec3(x, 0, z);
                    if (IsSpaceFloor(map.terrainGrid.TerrainAt(c)))
                    {
                        spaceCells++;
                        continue;
                    }
                    float dist = (c - center).LengthHorizontal;
                    float dmg = blastCenterDamage * (1f - dist / blastRadiusCells);
                    if (dmg <= 0.5f) continue;
                    var things = map.thingGrid.ThingsListAt(c);
                    for (int j = 0; j < things.Count; j++)
                    {
                        Thing th = things[j];
                        if (th is Pawn)
                        {
                            targets.Add(new KeyValuePair<Thing, float>(th, dmg));
                            pawns++;
                        }
                        else if (th.def.category == ThingCategory.Building)
                        {
                            targets.Add(new KeyValuePair<Thing, float>(th, dmg));
                            buildings++;
                        }
                    }
                }
            }
            for (int i = 0; i < targets.Count; i++)
                targets[i].Key.TakeDamage(new DamageInfo(DamageDefOf.Bomb, targets[i].Value));

            SpawnMapBlastFlecks(map, center, radiusCells);

            Log.Message(string.Format(
                "[弹头Map爆炸] Map={0} 中心格=({1},{2}) 地板={3} R={4:F0}格 D0={5:F0} 受击建筑={6} Pawn={7} 跳过太空格={8}",
                map.uniqueID, center.x, center.z, ground?.defName ?? "null",
                blastRadiusCells, blastCenterDamage, buildings, pawns, spaceCells));
        }

        /// <summary>"太空地板"判定（奥德赛 DLC TerrainDef Space：exposesToVacuum、不可站、无实心甲板；
        /// 兼容 defName 与 tag 两种标注）。</summary>
        private static bool IsSpaceFloor(TerrainDef terrain)
        {
            return terrain != null && (terrain.defName == "Space" || terrain.HasTag("Space"));
        }

        /// <summary>Map 爆炸视觉（纯 Fleck，GenExplosion NRE 规避）：一次爆炸从命中点炸开的观感——
        /// 中心主爆闪/火球 + 同心冲击波环（确定性角度抽样，中心密、外沿疏）+ 近场火光亮与烟柱。
        /// ⚠️ 09-07 实测修正：不要用"半径内随机撒点"（观感 = 全图散一地）；环形/密集结构才读作一次爆炸。</summary>
        private static void SpawnMapBlastFlecks(Map map, IntVec3 center, int radiusCells)
        {
            Vector3 cv = center.ToVector3Shifted();
            // 中心主爆闪（规模随爆炸半径增长，封顶避免盖满整图）+ 中心火球
            float flashScale = Mathf.Min(5f, Mathf.Max(2.5f, radiusCells * 0.3f));
            FleckMaker.Static(cv, map, FleckDefOf.ExplosionFlash, flashScale);
            for (int e = 0; e < 12; e++)
                FleckMaker.ThrowExplosionInterior(cv + Gen.RandomHorizontalVector(1.6f), map, FleckDefOf.ExplosionFlash);
            // 冲击波环：巨型 AoE 的可见环收敛到核心（观感清晰半径 ~10 格），随实际半径缩小
            float visR = Mathf.Min(radiusCells, 10f);
            float[] shells = { 0.25f, 0.5f, 0.75f, 1.0f }; // 环半径（相对 visR）
            int[] counts = { 6, 10, 14, 18 };              // 中心环少而亮，外环密而淡（读作波前）
            for (int s = 0; s < shells.Length; s++)
            {
                float ringR = shells[s] * visR;
                for (int k = 0; k < counts[s]; k++)
                {
                    float ang = (k / (float)counts[s]) * 2f * Mathf.PI + s * 0.35f; // 环间错相位，不叠放射线
                    Vector3 p = cv + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * ringR;
                    IntVec3 c2 = p.ToIntVec3();
                    if (c2.InBounds(map))
                        FleckMaker.ThrowHeatGlow(c2, map, shells[s] < 0.6f ? Rand.Range(0.85f, 1.2f) : Rand.Range(0.45f, 0.8f));
                }
            }
            // 火光亮（中心附近）+ 烟柱
            for (int f = 0; f < 6; f++)
                FleckMaker.ThrowFireGlow(cv + Gen.RandomHorizontalVector(2.2f), map, Rand.Range(0.6f, 0.95f));
            for (int m = 0; m < 5; m++)
                FleckMaker.ThrowSmoke(cv + Gen.RandomHorizontalVector(1.4f), map, Rand.Range(2.0f, 2.8f));
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

            if (DebugDetailedCombat)
            {
                Log.Message(string.Format("[激光发射] Group {0} 起点=({1:F0},{2:F0},{3:F0}) 方向=({4:F3},{5:F3},{6:F3}) 射程={7:F0}m",
                    groupId, startPos.x, startPos.y, startPos.z,
                    dir.x, dir.y, dir.z, beamLength));
            }

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
                    // 照 CheckSegmentGroupHit 的完整旋转处理写（第 16 条：逆四元数同走实体 per-tick 缓存）。
                    DQuaternion rotInv = entity.RotationInverse();
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
                    if (DebugDetailedCombat)
                    {
                        Log.Message(string.Format("[激光命中!] Tick={0} Group{1}→Group{2} Part[{3}]\"{4}\" HP={5:F0}→{6:F0} 命中点=({7:F0},{8:F0},{9:F0})",
                            lastHitTick, groupId, hitGroup.GroupId, hitPartIndex, hitPart.defName,
                            preHP, hitPart.hp,
                            hitPoint.x, hitPoint.y, hitPoint.z));
                    }
                    if (hitPart.isDestroyed)
                    {
                        hitEntity.MarkPartDestroyed(hitPart.partId);
                        if (DebugDetailedCombat)
                        {
                            Log.Message(string.Format("[Part摧毁] Entity{0} Part[{1}]\"{2}\" 已被摧毁",
                                hitEntity.EntityId, hitPartIndex, hitPart.defName));
                        }
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
                    if (DebugDetailedCombat)
                    {
                        Log.Message(string.Format("[激光命中!] Tick={0} Group{1}→Group{2} Entity{3} AABB 命中点=({4:F0},{5:F0},{6:F0})",
                            lastHitTick, groupId, hitGroup.GroupId, hitEntity.EntityId,
                            hitPoint.x, hitPoint.y, hitPoint.z));
                    }
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
                if (DebugDetailedCombat)
                {
                    Log.Message(string.Format("[激光未命中] Group {0} 射线无碰撞（候选船数={1}）", groupId, rayCandidateResults.Count));
                }
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
