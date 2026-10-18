using System.Buffers;
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
        private readonly List<ProjectileInstance> activeProjectiles = new();
        // 群组武器列表（Lite 裁剪后：实体级武器收集已删除，此容器由 M4 Map 炮塔桥接填充）
        private readonly Dictionary<int, List<WeaponInstance>> groupWeapons = new();
        private readonly HashSet<SpaceEntityGroup> projectileQueryResults = new();
        /// <summary>齐射弹群实体数上限（§5，2026-09-08 深度队列制：同深度仅 1 枚、深度间距 = min(2km, 10km/(N−1))，
        /// 致死带 ~1km 余量下 10km 内安全容量 = 8；方形同排成员会被兄弟弹头 AoE 击杀，已废弃）。</summary>
        /// <summary>单次齐射枚数上限：**单一来源**——深度队列安全容量（N 枚沿航向排开、
        /// 间距受弹头爆炸冲量作用半径约束；N=8 时 16km/(8−1) ≈ 2.29km &gt; 爆炸半径 1.5km）。
        /// 发射器 UI（`CompMissileLauncher` / `Window_VolleyCountSlider`）与齐射工厂都引用它。</summary>
        public const int MaxVolleyCount = 8;
        // 定向能射线候选集（每炮一次收集复用，方案 A 正方体粗筛 / B 走格预留共用）
        private readonly HashSet<SpaceEntityGroup> rayCandidateResults = new();
        // 命中诊断：已记录过的射弹 id（每个射弹只打印一次）
        private readonly HashSet<int> hitDiagnosticLogged = new();
        private readonly Dictionary<int, DVector3> shipLastPositions = new();
        // #4：SyncAllShipsToGrid 的本 tick 存活船缓冲（复用，避免每 2 tick 分配 HashSet）
        private readonly HashSet<int> aliveScratch = new();
        // 性能第 4 条：CheckShipCollisions 本步待销毁导弹组缓冲（复用，避免场上无导弹时每物理步也分配）
        private readonly HashSet<int> destroyedMissilesScratch = new();

        // 性能第 15 条：爆炸 AoE 候选组缓冲（走细网格；每次爆炸复用，CollectShipsInCube 内部会 Clear）。
        // 复用安全：ApplyExplosionDamage 的循环体不会重入自身（部件伤害回调无重写实现），
        // 冲量函数在其返回后才再次收集。
        private readonly HashSet<SpaceEntityGroup> explosionCandidateResults = new();

        // 激光射线（仅视觉残留，不参与物理模拟）
        private readonly List<LaserBeamInstance> activeLaserBeams = new();
        internal IReadOnlyList<LaserBeamInstance> ActiveLaserBeams => activeLaserBeams;

        // 爆炸视觉残留（仅视觉，物理效果即时结算）
        private readonly List<ExplosionInstance> activeExplosions = new();
        internal IReadOnlyList<ExplosionInstance> ActiveExplosions => activeExplosions;

        // 船-船碰撞参数（可配置）──
        public const float CollisionExplosionSpeedThreshold = 50f;   // m/s：低于此值按撞击等效（动量交换），高于触发真爆炸
        public const float CollisionRestitution = 0.2f;              // 弹性系数（2026-10-17 批 3 碰7：汽车撞汽车 ≈0.2；低于阈值时按完全非弹性）
        /// <summary>碰7：接近速度低于此值视为完全非弹性（避免接触瞬间抖动）。</summary>
        public const float CollisionRestitutionMinSpeed = 1.0f;
        public const double CollisionExplosionBaseRadius = 8000.0;   // 爆炸基础半径 m
        public const double CollisionExplosionRadiusK = 2.0;         // R = base + K·E^(1/3)
        public const double CollisionExplosionDamageK = 0.005;       // D0 = K·√E（中心损伤，随距离衰减）
        public const double CollisionImpulseK = 500.0;               // 爆炸径向冲量 Δv = K·√E·falloff/mass

        // 碰撞性能统计（仅 DEBUG 日志使用）
        private readonly System.Diagnostics.Stopwatch collisionStopwatch = new();
        private double collisionMsAccum ;
        private int collisionSamples ;

        /// <summary>是否处于交战状态。原为 `activeProjectiles.Count > 0 || TargetingSystem != null`，
        /// 而 `TargetingSystem` 恒为 null（死代码，2026-09-16 随 [V9] 删除）⇒ 语义等价退化为弹量判断。</summary>
        public bool IsBattleActive => activeProjectiles.Count > 0;

        internal IReadOnlyList<ProjectileInstance> ActiveProjectiles => activeProjectiles;

        private int lastLogTick = -1;
        private const int LOG_INTERVAL = 120;

        // 船-船碰撞扫掠膨胀盒：保存"上一次 SyncAllShipsToGrid 读到的 group 位置"（上一步）。
        // 检测时用「当前 AABB ∪ 上一步 AABB」并集，覆盖两步间运动路径，消除大步长 tunneling
        // （离散采样点全部落在重叠窗口外导致穿模）。组件 tick 顺序不保证 → 用上次 sync 位置，
        // 两种顺序下每个物理步运动段都会被并集覆盖一次，仅响应延迟不同（0 或 2 tick）。
        private readonly Dictionary<int, DVector3> shipPrevPositions = new();

        // #2 性能优化：CheckShipCollisions 本 tick 组级缓存（位置/半径/当前盒/上一步盒每组只算一次，
        // 消除每对重复的 GetAabbSystem 旋转计算——原先每对最多 6 次）。
        // 2026-10-17：缓存改为**跨 tick 复用对象 + epoch 失效**（不再每 tick Clear 重建），
        // 并把逐 part AABB 列表一起缓存 ⇒ 同一组与多个对手窄相时只收集一次。
        private readonly Dictionary<int, GroupCollisionCache> groupCollisionCache = new();
        private int collisionCacheEpoch;
        private int collisionCachePruneAt;
        private readonly List<int> collisionPruneScratch = new();

        /// <summary>窄相用：part 的世界系轴对齐盒（相对量已并入）。Order = 收集顺序，用于并列深度时的确定性。
        /// B3b：带自身姿态的 part 额外保存 OBB（Center/Half/Rotation），窄相走精确 SAT；
        /// 旧构造器（无姿态）保持 AABB 路径逐位不变。</summary>
        internal readonly struct PartAabb
        {
            public readonly DVector3 Min;
            public readonly DVector3 Max;
            public readonly int Order;

            public readonly DVector3 Center;
            public readonly DVector3 Half;
            public readonly DQuaternion Rotation;
            public readonly bool UseObb;

            public PartAabb(DVector3 min, DVector3 max, int order = 0)
            {
                Min = min; Max = max; Order = order;
                Center = (min + max) * 0.5;
                Half = (max - min) * 0.5;
                Rotation = DQuaternion.identity;
                UseObb = false;
            }

            /// <summary>B3b：世界 OBB；AABB 由投影得到（广播/裁剪仍用它）。</summary>
            public PartAabb(DVector3 center, DVector3 half, DQuaternion rotation, int order = 0)
            {
                Center = center;
                Half = half;
                Rotation = PartGeometry.EffectiveRotation(rotation);
                Order = order;
                DVector3 rh = PartGeometry.ProjectedHalfExtents(Rotation, half);
                Min = center - rh;
                Max = center + rh;
                UseObb = true;
            }
        }

        /// <summary>船对碰撞检测的组级缓存条目（跨 tick 复用；epoch 标记本步是否已刷新）。</summary>
        private sealed class GroupCollisionCache
        {
            public int epoch;
            public DVector3 pos;
            public double radius;
            public DVector3 curMin;
            public DVector3 curMax;
            public bool hasPrev;
            public DVector3 prevPos;
            public DVector3 prevMin;
            public DVector3 prevMax;

            /// <summary>本组逐 part 世界 AABB（收集顺序；按需构建，位置变化后由 Invalidate 清空）。</summary>
            public readonly List<PartAabb> parts = new();
            public bool partsBuilt;
            /// <summary>B1：构建缓存时的部件集指纹（entitySetVersion ⊕ 各实体 partSetVersion）。
            /// 原地改 part（不移动组）不会走 InvalidateCollisionCache，靠它发现并重建。</summary>
            public int partsVersion = -1;
        }

        /// <summary>详细碰撞日志开关（调试测试场景置 true；正式玩法保持 false）。</summary>
        public static bool DebugDetailedCollision ;
        private int detailedLogNextTick ;
        private int detailPairNextTick ;

        /// <summary>碰撞记录采集（仅验收场景打开；生产 false，零采集开销）。</summary>
        internal static bool CaptureCollisionRecords;
        private readonly List<CollisionDebugRecord> debugCollisionRecords = new();
        private const int MaxDebugCollisionRecords = 64;

        /// <summary>一次低速撞击的完整输入/输出（验收用：冲量公式、角冲量、角动量守恒）。</summary>
        internal struct CollisionDebugRecord
        {
            public int tick;
            public int aGroup, bGroup;
            public DVector3 pa, pb;          // 撞击时组原点（世界系）
            public Quaternion rotA, rotB;     // 撞击时姿态（角动量/惯量换算用）
            public DVector3 va, vb;          // 撞击前速度
            public DVector3 vaAfter, vbAfter; // 冲量后应有速度（pendingImpulse 应用后的目标）
            public DVector3 n;                // 冲击法线（A→B）
            public DVector3 contact;          // 接触点（世界系）
            public double j, vRel;
            public DVector3 wA0, wB0;        // 撞击前角速度（世界系）
            public DVector3 dwA, dwB;         // 角冲量带来的 Δω（世界系）
            public bool usedPartPair;         // true = 走的是 part 窄相；false = 组级回退
        }

        internal void ClearDebugCollisionRecords() => debugCollisionRecords.Clear();
        internal IReadOnlyList<CollisionDebugRecord> DebugCollisionRecords => debugCollisionRecords;

        /// <summary>炮塔战斗全链详细日志开关（开火/出膛/视觉弹/命中/贯穿，调试测试场景自动开启）。</summary>
        public static bool DebugDetailedCombat ;
        /// <summary>射弹飞行状态探针间隔（tick，0=关）：每 N tick 打印全部在飞射弹位置/速度/寿命。</summary>
        public static int DebugProjectileProbeInterval ;
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
            sb.Append(System.Globalization.CultureInfo.CurrentCulture, $"[射弹探针] physicsTick={TimeScaleManager.GetPhysicsTickSafe():F1} 在飞={activeProjectiles.Count}");
            foreach (var p in activeProjectiles)
            {
                double flown = (p.position - p.spawnPosition).magnitude;
                sb.Append(System.Globalization.CultureInfo.CurrentCulture, $" | proj{p.id}(G{p.sourceGroupId}): pos=({p.position.x:F0},{p.position.y:F0},{p.position.z:F0}) v=({p.velocity.x:F0},{p.velocity.y:F0},{p.velocity.z:F0}) 速率={p.velocity.magnitude:F0}m/s 距生成点={flown:F0}m 飞行={p.totalFlightSeconds:F1}s 剩余={p.remainingLife:F1}s");
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
        private readonly GameComponentRef<CombatGrid> combatGridRef = new();
        private readonly GameComponentRef<SpaceGameComp> spaceGameRef = new();
        private readonly GameComponentRef<TimeScaleManager> timeScaleRef = new();
        private readonly GameComponentRef<AutonomousVehicleManager> avmRef = new();
        private readonly GameComponentRef<BoardingManager> boardingRef = new();
        private readonly GameComponentRef<CelestialBodyManager> celestialRef = new();

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
            return group.CurrentOrbit.positionSystem.v;   // 批6 S3-3c：读带系字段
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

            List<int>? toRemove = null;
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
                            proj.spawnPosition.v, proj.spawnVelocity.v, proj.position.v,
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
                        Log.Message($"[射弹脱靶] proj={proj.id} 超时销毁 | 生成=({proj.spawnPosition.x:F0},{proj.spawnPosition.y:F0},{proj.spawnPosition.z:F0}) 终点=({proj.position.x:F0},{proj.position.y:F0},{proj.position.z:F0}) 飞行距离={flightDist:F0}m 累计游戏秒={proj.totalFlightSeconds:F2}s 平均速度={(proj.totalFlightSeconds > 0.001 ? flightDist / proj.totalFlightSeconds : 0.0):F0}m/s 末速={proj.velocity.magnitude:F0}m/s physicsTick={TimeScaleManager.GetPhysicsTickSafe():F1}");
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
                    combatGrid.UpdateProjectileCell(proj, proj.prevPosition.v, proj.position.v);
                }

                CheckProjectileCollision(proj, spaceGame);
            }
        }

        /// <summary>引力射弹的传播段长（物理秒）。用户 2026-09-14 定调：不要算得太勤。</summary>
        private const double ProjectileGravitySegmentSeconds = 2.0;

        private readonly List<GravitySource> projectileGravityScratch = new();

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
                proj.gravitySegment.Rebuild(proj.position.v, proj.velocity.v,
                    DVector3.zero, DVector3.zero, physicsTick, ProjectileGravitySegmentSeconds);
                return;
            }

            // systemBodyId = 参考帧天体（根行星/恒星）= 系统标识 → 取该系全部天体
            mgr.CollectSystemSources(proj.systemBodyId, physicsTick, projectileGravityScratch);
            NBodyIntegrator.FieldAccelJerk(proj.position.v, proj.velocity.v, projectileGravityScratch,
                out var acc, out var jerk);

            if (PerfProbe.Enabled && acc.magnitude > 1e-9)
            {
                // 探针（A2）：|a| / 主导项（μ/r² 最大值）+ "指向行星中心"余弦 —— 旧实现此处是 ~100× 主导项的伪值，
                // 修正后应 ≈1× 主导项（近地即行星真实引力 ~8 m/s² 量级）。
                double dominant = 0.0;
                for (int i = 0; i < projectileGravityScratch.Count; i++)
                {
                    var s = projectileGravityScratch[i];
                    double dSq = (s.position - proj.position.v).sqrMagnitude;
                    if (dSq < 0.01) continue;
                    double term = s.mu / dSq;
                    if (term > dominant) dominant = term;
                }

                DVector3 toCenter = -proj.position.v;
                double cos = toCenter.magnitude > 1.0
                    ? DVector3.Dot(acc.normalized, toCenter.normalized)
                    : 0.0;
                double ratio = dominant > 1e-12 ? acc.magnitude / dominant : 0.0;

                PerfProbe.NoteWarheadSegment(acc.magnitude, cos, ratio);
            }

            proj.gravitySegment.Rebuild(proj.position.v, proj.velocity.v, acc, jerk,
                physicsTick, ProjectileGravitySegmentSeconds);
        }

        private void CheckProjectileCollision(ProjectileInstance proj, SpaceGameComp spaceGame)
        {
            var combatGrid = GetCombatGrid();
            if (combatGrid == null) return;

            DVector3 startPos = proj.prevPosition.v;
            DVector3 endPos = proj.position.v;

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
                    double dist = (gPos - proj.position.v).magnitude;
                    if (dist < 800.0)
                    {
                        hitDiagnosticLogged.Add(proj.id);
                        if (DebugDetailedCombat)
                        {
                            Log.Message($"[命中诊断] proj={proj.id} group={group.GroupId} 查询数={projectileQueryResults.Count} TicksGame={Find.TickManager.TicksGame} physicsTick={TimeScaleManager.GetPhysicsTickSafe():F0} 距离={dist:F0}m | 线段({proj.prevPosition.x:F0},{proj.prevPosition.z:F0})->({proj.position.x:F0},{proj.position.z:F0}) | 目标=({gPos.x:F0},{gPos.z:F0}) | 射弹速=({proj.velocity.x:F0},{proj.velocity.y:F0},{proj.velocity.z:F0})");
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

        /// <summary>
        /// **在实体 AABB 内选最近命中的 part**（多 part 命中链，批 3；局部系 slab）。
        /// 线段 = 盒内区间 `boxEntry → boxEntry + dir*maxDist`；`t` 为归一化参数（0..1），微负取 0。
        /// 多件重叠时取最小进入参数（先撞到的那件）；全部未命中 ⇒ false（AABB 命中但零件缝里穿过）。
        /// 调用方负责把 `partEnterT * maxDist` 换成命中点；图内映射只对 `linkedMapId >= 0` 的挂图件做。
        /// </summary>
        internal static bool TryPickHitPart(SpaceEntity entity, DVector3 boxEntry, DVector3 dirShip, double maxDistShip,
            out int partIndex, out SpacePart hitPart, out double partEnterT)
        {
            partIndex = -1;
            hitPart = null!;                 // null!：调用方只在返回 true 时使用（N2 可空族家规）
            partEnterT = double.MaxValue;
            if (!(maxDistShip > 0.0)) return false;

            DVector3 end = boxEntry + dirShip * maxDistShip;
            for (int i = 0; i < entity.Parts.Count; i++)
            {
                var p = entity.Parts[i];
                // B5 / D-P7：损毁残骸仍可被命中（子弹打在残骸上，不再“穿过空气”）。
                if (p == null) continue;
                if (!PartGeometry.RayObb(boxEntry, end, p.positionShip, p.rotationShip, p.boundsSizeShip,
                        out double tEntry))
                    continue;
                double enter = tEntry < 0.0 ? 0.0 : tEntry;
                if (enter < partEnterT)
                {
                    partEnterT = enter;
                    partIndex = i;
                    hitPart = p;
                }
            }
            return partIndex >= 0;
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
                : group.CurrentOrbit.positionSystem.v - group.CurrentOrbit.velocitySystem.v * dtPhysics;
            DVector3 startPos = proj.prevPosition.v - groupPosPrev;
            DVector3 endPos = proj.position.v - groupPos;
            DVector3 dirSystem = endPos - startPos;
            double segmentLength = dirSystem.magnitude;

            // 防御：相对参考系中无有效位移（射弹生成与首次检测同 tick、或与目标相对静止）时，
            // 零方向线段会让 slab 检测恒命中（tEntry=-Inf tExit=Inf）→ 射弹误判命中任意 group。
            // 跳过本次检测，射弹继续飞行，下一物理步相对位移恢复后再正常判定。
            if (segmentLength < 0.5)
                return false;

#if DEBUG
            // 命中逐步诊断：仅在射弹已触发[命中诊断]（接近目标）后打印，定位检测在哪一步失败
            bool verboseHit = hitDiagnosticLogged.Contains(proj.id);
            if (verboseHit)
                Log.Message($"[命中调试] proj={proj.id} group={group.GroupId} | groupPos=({groupPos.x:F0},{groupPos.y:F0},{groupPos.z:F0}) groupPosPrev=({groupPosPrev.x:F0},{groupPosPrev.y:F0},{groupPosPrev.z:F0}) | start=({startPos.x:F0},{startPos.y:F0},{startPos.z:F0}) end=({endPos.x:F0},{endPos.y:F0},{endPos.z:F0}) | segLen={segmentLength:F0}m 射弹速=({proj.velocity.x:F0},{proj.velocity.y:F0},{proj.velocity.z:F0})");
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

                DVector3 entityShip = entity.Position;   // 目标参考系中实体局部位置（不随时间变）
                DVector3 halfSize = entity.AabbHalfSize;

                // 射弹线段 → 船体局部系（含旋转）：part 局部 = 固定轴对齐盒（地图 1:1 基准）。
                // entity.Rotation 参与命中几何——倾斜/翻滚的船，命中点反映真实姿态。
                // 第 16 条：逆四元数走实体 per-tick 缓存（每弹每实体的旋转开销 → 每实体每 tick 一次）
                DQuaternion rotInv = entity.RotationInverse();
                DVector3 startShip = DVector3.Rotate(startPos - entityShip, rotInv);
                DVector3 endShip = DVector3.Rotate(endPos - entityShip, rotInv);

                // 粗检测（shipLocal 系，盒中心 = 实体原点）：相对坐标系中目标静止，
                // slab 线段检测是连续几何检测（与线段长度无关），射弹无论多快，
                // 只要相对路径穿过 AABB 就会被捕捉，无需子段化。
                DVector3 min = -halfSize;
                DVector3 max = halfSize;
                if (!CombatGrid.RaySlabIntersection(startShip, endShip, min, max,
                        out double tEntry, out double tExit, out _))
                {
#if DEBUG
                    if (verboseHit)
                        Log.Message($"[命中调试] slab MISS tEntry={tEntry:F3} tExit={tExit:F3}");
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
                    Log.Message($"[导弹拦截] proj={proj.id} 击毁导弹G{group.GroupId} 单枚E{entityId} → 就地引爆");
                    interceptedRec = missileRec;
                    interceptedEntityId = entityId;
                    interceptedHitCenter = groupPos + entity.Position; // 命中实体世界位（爆心）
                    break;   // 摘除/引爆在循环外执行（见方法尾部）
                }

#if DEBUG
                if (verboseHit)
                    Log.Message($"[命中调试] slab HIT tEntry={tEntry:F3} tExit={tExit:F3}");
#endif

                // 命中详情（详细日志，Release 下亦生效）：slab 进入/出射参数 + 射弹-目标几何
                if (DebugDetailedCombat)
                {
                    Log.Message($"[命中详情] proj={proj.id}(G{proj.sourceGroupId})→G{group.GroupId} slab HIT tEntry={tEntry:F3} tExit={tExit:F3} 线段长={segmentLength:F0}m 射弹速=({proj.velocity.x:F0},{proj.velocity.y:F0},{proj.velocity.z:F0})m/s 目标pos=({groupPos.x:F0},{groupPos.y:F0},{groupPos.z:F0})");
                }

                // Entity AABB 被命中，检查 Part 级碰撞（多 part：**逐 part slab 取最近 tEntry**，§5.3/批 3）。
                // 线段基准 = 粗检测进入点（shipLocal）：part 检测从盒进入点扫盒内区间，
                // 而不是从射弹线段起点扫（起点在盒外时永远 miss）。
                if (entity.Parts.Count > 0 && segmentLength > 0.001)
                {
                    DVector3 dirShip = (endShip - startShip) / segmentLength; // 归一化（旋转保持长度）
                    DVector3 entryPoint = startShip + dirShip * (tEntry * segmentLength);
                    double maxDistShip = (tExit - tEntry) * segmentLength;

                    bool partHit = TryPickHitPart(entity, entryPoint, dirShip, maxDistShip,
                        out int firstHitPartIndex, out var hitPart, out double partEnterT);
#if DEBUG
                    if (verboseHit)
                        Log.Message($"[命中调试] PartSlab origin=({entryPoint.x:F0},{entryPoint.y:F0},{entryPoint.z:F0}) dir=({dirShip.x:F3},{dirShip.y:F3},{dirShip.z:F3}) maxDist={maxDistShip:F0} partHit={partHit} t={partEnterT:F3}");
#endif
                    if (partHit)
                    {
                        var firstHitT = partEnterT;
                        DVector3 entityPosSystem = groupPos + entity.Position;
                        lastHitPosition = proj.position.v;
                        lastHitGroupId = group.GroupId;
                        lastHitTick = Find.TickManager.TicksGame;
                        lastHitWeaponType = proj.weaponDef?.weaponType ?? WeaponType.Projectile;
                        if (DebugDetailedCombat)
                        {
                            Log.Message($"[命中Part!] Tick={lastHitTick} 射弹{proj.id}→Group{group.GroupId} Entity{entityId} Part[{firstHitPartIndex}]\"{hitPart.defName}\" hitT={firstHitT:F3} PartHP={hitPart.hp:F0}→{hitPart.hp - proj.damage:F0} 飞行秒={proj.totalFlightSeconds:F2} EntityPos=({entityPosSystem.x:F0},{entityPosSystem.y:F0},{entityPosSystem.z:F0}) PartPosShip=({hitPart.positionShip.x:F0},{hitPart.positionShip.y:F0},{hitPart.positionShip.z:F0}) PartSize=({hitPart.boundsSizeShip.x:F0},{hitPart.boundsSizeShip.y:F0},{hitPart.boundsSizeShip.z:F0})");
                        }
                        // 应用伤害（走组件回调：OnDamaged / OnDestroyed）
                        hitPart.ApplyDamage(proj.damage);
                        if (hitPart.isDestroyed)
                        {
                            entity.MarkPartDestroyed(hitPart.partId);
                            if (DebugDetailedCombat)
                            {
                                Log.Message($"[Part摧毁] Entity{entityId} Part[{firstHitPartIndex}]\"{hitPart.defName}\" 已被摧毁");
                            }
                        }

                        // F3 命中 → Map 落点瞬爆：命中点已在船体局部系（含旋转），
                        // part 局部 = 地图 1:1，直接映射即可。
                        // 多 part：只有命中**挂图舱盒**才做图内映射；打到引擎/储罐等非挂图件时跳过（§6M 边缘小项）。
                        DVector3 hitPointSystem = proj.position.v; // 兜底：步末点
                        // B6：按**命中 part 的 linkedMapId** 选图（多舱室船可能挂多图；不再用实体主图）。
                        if (hitPart.linkedMapId >= 0)
                        {
                            Map targetMap = SpaceMapGenerator.FindPocketMap(hitPart.linkedMapId);
                            if (targetMap != null)
                            {
                                DVector3 hitShip = entryPoint + dirShip * (partEnterT * maxDistShip);
                                if (DebugDetailedCombat)
                                {
                                    DVector3 penDir = hitShip - entryPoint;
                                    Log.Message($"[命中详情] proj={proj.id}→G{group.GroupId} Entity{entityId} partT={partEnterT:F3} 船壳入口局部=({entryPoint.x:F1},{entryPoint.y:F1},{entryPoint.z:F1}) 命中局部=({hitShip.x:F1},{hitShip.y:F1},{hitShip.z:F1})m 贯穿长度={penDir.magnitude:F1}m 贯穿方向=({penDir.x / Math.Max(penDir.magnitude, 0.001):F3},{penDir.y / Math.Max(penDir.magnitude, 0.001):F3},{penDir.z / Math.Max(penDir.magnitude, 0.001):F3})");
                                }
                                SpawnPenetrationMote(entity, targetMap, hitShip);
                                // 弹头 Map 范围爆炸（§10 收尾，2026-09-07）：以命中映射格为心在目标地图上
                                // 做范围爆炸（条件：命中处地板不是"太空"，SpawnMapWarheadBlast 内部判定）——
                                // 3D AoE（HandleWarheadImpact）的 Map 对应物；太空 Pocket Map GenExplosion NRE → 自实现。
                                if (proj.isWarhead)
                                    SpawnMapWarheadBlast(targetMap, hitShip, proj.mapBlastRadiusCells, proj.blastCenterDamage);
                                // 世界命中点（爆炸中心用）：船体局部 hitShip → 世界（含目标姿态/群位置）
                                hitPointSystem = groupPos + entity.Position + DVector3.Rotate(hitShip, entity.Rotation);
                            }
                        }

                        // 弹头命中结算（§10）：part 大额直扣（上方 ApplyDamage）+ F3 Map 落点（上方 mote）
                        // 已走现链；弹头额外在**精确命中点**触发 3D AoE 爆炸（2026-09-07 修：
                        // 原用 proj.position（步末点）作爆炸中心——高速弹头一步穿 ~3.4km，
                        // 偏深超出 AoE 半径导致 damaged=0）。（命中即移除，外层处理。）
                        if (proj.isWarhead)
                            HandleWarheadImpact(proj, group, hitPointSystem);
                        return true;
                    }
                }
                else
                {
                    // 无 Part 数据，回退为 Entity AABB 命中即算
                    lastHitPosition = proj.position.v;
                    lastHitGroupId = group.GroupId;
                    lastHitTick = Find.TickManager.TicksGame;
                    lastHitWeaponType = proj.weaponDef?.weaponType ?? WeaponType.Projectile;
                    if (DebugDetailedCombat)
                    {
                        Log.Message($"[命中!] 射弹击中 Entity {entityId} (Group {group.GroupId}) 位置({groupPos.x:F0},{groupPos.y:F0},{groupPos.z:F0})");
                    }
                    if (proj.isWarhead)
                        HandleWarheadImpact(proj, group, proj.position.v);
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
                    spaceGame.DestroyGroup(group.GroupId, "战斗中损毁（射弹/接触）");
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

            // #2：epoch 推进（缓存对象跨 tick 复用；每 600 步清一次已死/久未用的组，防字典无界增长）
            collisionCacheEpoch++;
            if (collisionCacheEpoch >= collisionCachePruneAt)
            {
                PruneCollisionCache(spaceGame);
                collisionCachePruneAt = collisionCacheEpoch + 600;
            }

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

                // P49 读数（只量不改）：到这里才算"候选对"（前面两个 continue 是枚举器层面的粗筛）
                if (PerfProbe.Enabled) PerfProbe.PairCandidates++;

                // 枚举对诊断：确认 G1/G2/G3 是否被枚举到（限频）
                if (DebugDetailedCollision && Find.TickManager.TicksGame >= detailPairNextTick)
                {
                    detailPairNextTick = Find.TickManager.TicksGame + 30;
                    DVector3 pa0 = GetGroupPos(a);
                    DVector3 pb0 = GetGroupPos(b);
                    Log.Message($"[碰撞枚举] t={Find.TickManager.TicksGame} 对 G{a.GroupId}-G{b.GroupId} 距离={(pb0 - pa0).magnitude:F1}m");
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

                // **相对**扫掠盒（2026-10-17 关键修）：只把 B 的盒沿**相对位移**扩展（A 固定在当前），
                // 而不是"当前 ∪ 上一步"的绝对并集。绝对并集会把两船的共同轨道/自由落体运动
                // （一步几 km）算成重叠 ⇒ 共轨/同落体船假 tunneling（日志实测：G1/G2 每一步
                // 扫掠盒都重叠、当前盒不重叠 ⇒ 只停位置不反速度 ⇒ 用户看到的"一段段靠近又远离"）。
                DVector3 prevPa = ca.hasPrev ? ca.prevPos : pa;
                DVector3 prevPb = cb.hasPrev ? cb.prevPos : pb;
                DVector3 relDelta = (pb - pa) - (prevPb - prevPa);
                DVector3 aMin = curAMin, aMax = curAMax;
                DVector3 bMin = new DVector3(
                    Math.Min(curBMin.x, curBMin.x - relDelta.x),
                    Math.Min(curBMin.y, curBMin.y - relDelta.y),
                    Math.Min(curBMin.z, curBMin.z - relDelta.z));
                DVector3 bMax = new DVector3(
                    Math.Max(curBMax.x, curBMax.x - relDelta.x),
                    Math.Max(curBMax.y, curBMax.y - relDelta.y),
                    Math.Max(curBMax.z, curBMax.z - relDelta.z));

                bool sweptHit = AabbOverlap(aMin, aMax, bMin, bMax, out double ox, out double oy, out double oz);
                // 注意：本日志全局 30 tick 门控——09-07 诊断期曾临时无门控（穿越窗口 2-4 tick），
                // Combat 档（1/60）下现实 30Hz × 每对刷屏 8725 行/次测试拖垮内存，使命完成后恢复门控。
                if (DebugDetailedCollision && Find.TickManager.TicksGame >= detailedLogNextTick)
                {
                    detailedLogNextTick = Find.TickManager.TicksGame + 30;
                    Log.Message($"[碰撞详细] t={Find.TickManager.TicksGame} G{a.GroupId}扫掠盒=({aMin.x:F0},{aMin.y:F0},{aMin.z:F0})-({aMax.x:F0},{aMax.y:F0},{aMax.z:F0}) G{b.GroupId}扫掠盒=({bMin.x:F0},{bMin.y:F0},{bMin.z:F0})-({bMax.x:F0},{bMax.y:F0},{bMax.z:F0}) 距离={(pb - pa).magnitude:F1}m prevG{a.GroupId}=({prevPa.x:F0},{prevPa.y:F0},{prevPa.z:F0}) prevG{b.GroupId}=({prevPb.x:F0},{prevPb.y:F0},{prevPb.z:F0}) 重叠={(sweptHit ? $"{ox:F1},{oy:F1},{oz:F1}m" : "无")}");
                }
                if (!sweptHit)
                {
                    // 第 10 条：绝大多数对在此早退，故导弹记录查询移到判定之后——每对每步省 2 次
                    // GetRecord 字典查找（此前在判定前取，扫掠未命中的对也白查两次）。
                    if (PerfProbe.Enabled) PerfProbe.NoteRecordLookupsAvoided(2);
                    continue;
                }
                // P49 读数：过扫掠盒 = 真的要往下走的那一串判定（"同 volley 排除"就在后面）
                if (PerfProbe.Enabled) PerfProbe.PairSweptHit++;

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
                    // P49 读数：同 volley 兄弟对 = **必然被排除、纯白跑**的那部分（占比决定改法）
                    if (sameVolley && PerfProbe.Enabled) PerfProbe.PairSameVolley++;
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
                    ResolveTunneled(a, b, prevPa, prevPb, curAMin, curAMax, curBMin, curBMax, destroyedMissiles);
                    InvalidateCollisionCache(a, b); // B 位置已改变
                    continue;
                }

                // 批 3（2026-10-17）：窄相取**最深 part 对** ⇒ 最小穿透轴 / 深度 / 接触点。
                // 组级并集盒的重叠只是广播相：找不到 part 对时按碰 3 修订（用户裁 A）处理。
                DVector3 nDir;
                double depth;
                DVector3 contactPoint;
                bool usedPartPair = TryFindDeepestPartPair(a, b, ca, cb, spaceGame,
                    out nDir, out depth, out contactPoint);
                if (!usedPartPair)
                {
                    // 空角 = 并集盒重叠但没有任何零件接触。两支都有可用 part ⇒ 物理上尚未接触，
                    // 不解析、继续接近；只有一方完全没有可用 part（无实体/全毁/noCollide）才回退组级盒。
                    if (!ShouldFallbackToGroupAabb(ca.parts.Count, cb.parts.Count))
                    {
                        if (DebugDetailedCollision && Find.TickManager.TicksGame >= detailedLogNextTick)
                        {
                            detailedLogNextTick = Find.TickManager.TicksGame + 30;
                            Log.Message($"[ShipCollision] G{a.GroupId} vs G{b.GroupId} 组盒重叠但无 part 对（空角），跳过等待零件接触");
                        }
                        continue;
                    }

                    nDir = MinPenetrationAxis(ox, oy, oz, pa, pb, out depth);
                    DVector3 oMin = DVector3.Max(curAMin, curBMin);
                    DVector3 oMax = DVector3.Min(curAMax, curBMax);
                    contactPoint = (oMin + oMax) * 0.5;
                }

                DVector3 va = GetGroupVelocity(a);
                DVector3 vb = GetGroupVelocity(b);

                // 组级法线符号归一（2026-10-17 S1 间歇性回归）：part 窄相法线是"最深 part 对 A→B"，
                // 两船零件交错、或接触盒中心完全重合（SAT 的 delta≈0）时，轴符号会退化成与**组心连线**
                // 相反（S1 对称侧件恰好对心：delta=0 ⇒ 取轴序 +X，而枚举顺序可能让 A 是右船）。
                // 后果：① vRel<0 ⇒ 正在接近被当成分离，只位置分离不给冲量（继续深穿透）；
                //       ② 沿反方向分离 = 把两船推进彼此怀里 ⇒ 下一次深穿透补撞（实测记录 3 条、
                //          撞击后速度差 0.15 m/s）。组级冲量/分离必须按组心连线定向。
                nDir = AlignImpactNormal(nDir, pb - pa);

                // 分隔方向 = **flip 之前**的当前 A→B（位置分离必须沿"现在谁在谁哪边"，
                // 不能用翻回入射侧的冲击法线：一步跨过时入射法线与当前位形相反，
                // 用它分离会把两船往彼此怀里推 ⇒ 下一步再炸一次）。
                DVector3 nSepDir = nDir;

                // 一步跨过检测（2026-10-17）：高速时 B 可能在**一步内**从 A 的 +X 侧穿到 -X 侧，
                // 当前盒仍重叠、但"当前中心连线"法线指向已穿过去的一侧 ⇒ vRel 为负、被当成"分离中"，
                // 结果不爆炸、下一步继续假 tunneling。用 prev→cur 在法线上的侧别翻转把法线翻回入射侧。
                DVector3 relPrev = prevPb - prevPa;
                DVector3 relCur = pb - pa;
                double sidePrev = DVector3.Dot(relPrev, nDir);
                double sideCur = DVector3.Dot(relCur, nDir);
                if (sidePrev < 0.0 && sideCur >= 0.0)
                    nDir = -nDir;

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
                    TriggerExplosion(a, b, pa, pb, va, vb, nDir, vRel, nSepDir, depth, contactPoint, destroyedMissiles);
                }
                else if (vRel > 0)
                {
                    ResolveImpact(a, b, pa, pb, va, vb, nDir, vRel, nSepDir, depth, contactPoint, usedPartPair);
                }
                else
                {
                    // 正在分离但 AABB 仍重叠：仅位置分离防卡死
                    SeparatePositions(a, b, nSepDir, depth);
                    if (DebugDetailedCollision)
                    {
                        Log.Message($"[ShipCollision] G{a.GroupId} vs G{b.GroupId} 分离中重叠，仅位置分离 重叠=({ox:F0},{oy:F0},{oz:F0})m");
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
                    spaceGame.DestroyGroup(gid, "战斗中损毁（导弹接触）");
                }
            }
        }

        /// <summary>本 tick 组级碰撞缓存：每组只算一次位置/球半径/当前盒/上一步盒，每对重复时直接复用。</summary>
        private GroupCollisionCache GetOrBuildCollisionCache(SpaceEntityGroup group, SpaceGameComp spaceGame)
        {
            if (groupCollisionCache.TryGetValue(group.GroupId, out var c) && c.epoch == collisionCacheEpoch)
                return c;

            if (c == null)
            {
                c = new GroupCollisionCache();
                groupCollisionCache[group.GroupId] = c;
            }

            c.epoch = collisionCacheEpoch;
            c.pos = GetGroupPos(group);
            c.radius = GetGroupRadius(group);
            GetGroupAabbSystem(group, c.pos, spaceGame, out c.curMin, out c.curMax);
            c.hasPrev = false;
            c.partsBuilt = false;
            c.parts.Clear();
            if (shipPrevPositions.TryGetValue(group.GroupId, out var prevPos))
            {
                c.hasPrev = true;
                c.prevPos = prevPos;
                GetGroupAabbSystem(group, prevPos, spaceGame, out c.prevMin, out c.prevMax);
            }
            return c;
        }

        /// <summary>碰撞解决（改变位置/销毁）后使 a/b 的组缓存失效，下次访问重建（对象保留复用）。</summary>
        private void InvalidateCollisionCache(SpaceEntityGroup a, SpaceEntityGroup b)
        {
            InvalidateCollisionCacheEntry(a.GroupId);
            InvalidateCollisionCacheEntry(b.GroupId);
        }

        private void InvalidateCollisionCacheEntry(int groupId)
        {
            if (groupCollisionCache.TryGetValue(groupId, out var c))
            {
                c.epoch = -1;
                c.partsBuilt = false;
                c.parts.Clear();
            }
        }

        /// <summary>每 600 步清理已死/久未访问的组缓存条目（对象跨 tick 复用的代价：字典不会无界增长）。</summary>
        private void PruneCollisionCache(SpaceGameComp spaceGame)
        {
            collisionPruneScratch.Clear();
            int cutoff = collisionCacheEpoch - 600;
            foreach (var kv in groupCollisionCache)
            {
                var g = spaceGame.GetGroupById(kv.Key);
                if (g == null || !g.IsActive || kv.Value.epoch < cutoff)
                    collisionPruneScratch.Add(kv.Key);
            }
            for (int i = 0; i < collisionPruneScratch.Count; i++)
                groupCollisionCache.Remove(collisionPruneScratch[i]);
        }

        /// <summary>
        /// 纯 tunneling 响应：B 的当前盒与 A 盒不重叠，但扫掠路径相交（B 一步从 A 一侧穿到另一侧）。
        /// 用「B 中心射线 vs A 膨胀盒（+B 半宽）」求接触时刻，把 B 精确停在接触点（留 0.1m 余量防嵌入）。
        /// 求交失败（无上一步记录 / 起点已在膨胀盒内）⇒ **不做任何停位**——当前盒已不重叠，
        /// 没有可解的穿透；旧回退 `ApplyTunneledStop(b, pbPrev)` 会把 B 钉回上一位置，
        /// 与残余 0.1m 叠成每 tick 一次的黏着回环（2026-10-17 实测）。
        /// </summary>
        private void ResolveTunneled(SpaceEntityGroup a, SpaceEntityGroup b, DVector3 prevA, DVector3 prevB,
            DVector3 curAMin, DVector3 curAMax, DVector3 curBMin, DVector3 curBMax,
            HashSet<int>? destroyedMissiles = null)
        {
            // **相对** TOI（与相对扫掠同一口径）：B 相对 A 的位移，而不是 B 的绝对位移。
            DVector3 curA = GetGroupPos(a);
            DVector3 curB = GetGroupPos(b);
            DVector3 prevCenterRel = prevB - prevA;
            DVector3 curCenterRel = curB - curA;
            DVector3 bMinRel = curBMin - curB;
            DVector3 bMaxRel = curBMax - curB;
            DVector3 aMinRel = curAMin - curA;
            DVector3 aMaxRel = curAMax - curA;

            if (TryComputeTunneledStopWithNormal(prevCenterRel, curCenterRel, bMinRel, bMaxRel,
                    aMinRel, aMaxRel, out var stopRel, out var normal))
            {
                // 碰6（2026-10-17）：TOI 命中点**也要走碰撞响应**——只停位置不反速度会让 B 每步重新撞进来，
                // 形成"靠近→被停→再靠近"的锯齿（用户看到的"一段一段地远离"即此）。
                DVector3 va = GetGroupVelocity(a);
                DVector3 vb = GetGroupVelocity(b);
                double vRel = DVector3.Dot(va - vb, normal);
                if (vRel <= 0.0)
                {
                    // 已在分离：绝不能再 ApplyTunneledStop（会把 B 拖到 A 的新表面，形成黏着）。
                    if (DebugDetailedCollision)
                    {
                        Log.Message($"[TOI诊断] G{a.GroupId}→G{b.GroupId} prevRel=({prevCenterRel.x:F1},{prevCenterRel.y:F1},{prevCenterRel.z:F1}) curRel=({curCenterRel.x:F1},{curCenterRel.y:F1},{curCenterRel.z:F1}) stopRel=({stopRel.x:F1},{stopRel.y:F1},{stopRel.z:F1}) n=({normal.x:F2},{normal.y:F2},{normal.z:F2}) vRel={vRel:F2} ⇒ 分离中，跳过停位");
                    }
                    return;
                }

                ApplyTunneledStop(b, curA + stopRel, a.GroupId);

                if (DebugDetailedCollision)
                {
                    Log.Message($"[TOI诊断] G{a.GroupId}→G{b.GroupId} prevRel=({prevCenterRel.x:F1},{prevCenterRel.y:F1},{prevCenterRel.z:F1}) curRel=({curCenterRel.x:F1},{curCenterRel.y:F1},{curCenterRel.z:F1}) stopRel=({stopRel.x:F1},{stopRel.y:F1},{stopRel.z:F1}) n=({normal.x:F2},{normal.y:F2},{normal.z:F2}) vRel={vRel:F2} ⇒ {(vRel >= CollisionExplosionSpeedThreshold ? "爆炸" : "撞击")}");
                }

                DVector3 halfB = (curBMax - curBMin) * 0.5;
                double support = System.Math.Abs(normal.x) * halfB.x
                               + System.Math.Abs(normal.y) * halfB.y
                               + System.Math.Abs(normal.z) * halfB.z;
                DVector3 contact = curA + stopRel - normal * support;
                DVector3 pa = GetGroupPos(a);
                DVector3 pb = GetGroupPos(b);
                if (vRel >= CollisionExplosionSpeedThreshold)
                    TriggerExplosion(a, b, pa, pb, va, vb, normal, vRel, normal, 0.0, contact, destroyedMissiles);
                else
                    ResolveImpact(a, b, pa, pb, va, vb, normal, vRel, normal, 0.0, contact, false);
                return;
            }
        }

        /// <summary>
        /// 计算 tunneling 停点：B 中心从 prev 直线移动到 cur（一步位移 dir），
        /// 与「A 盒膨胀 B 半宽」（B 中心不可进入区）求交，停在其表面（留 0.1m 余量）。
        /// 纯静态，便于单元测试验证物理正确性。
        /// </summary>
        internal static bool TryComputeTunneledStop(DVector3 prevCenter, DVector3 curCenter,
            DVector3 curBMin, DVector3 curBMax, DVector3 aMin, DVector3 aMax, out DVector3 stopPos)
            => TryComputeTunneledStopWithNormal(prevCenter, curCenter, curBMin, curBMax, aMin, aMax,
                out stopPos, out _);

        /// <summary>
        /// 同 <see cref="TryComputeTunneledStop"/>，并给出**接触面外法线**（A→B，世界系；碰6 响应用）。
        /// 法线 = 射线进入膨胀盒时的那个面（与运动方向反向）；歧义时退化为"B 中心 − A 中心"。
        /// </summary>
        internal static bool TryComputeTunneledStopWithNormal(DVector3 prevCenter, DVector3 curCenter,
            DVector3 curBMin, DVector3 curBMax, DVector3 aMin, DVector3 aMax,
            out DVector3 stopPos, out DVector3 normal)
        {
            stopPos = default;
            normal = DVector3.zero;
            DVector3 dir = curCenter - prevCenter;
            double dirLen = dir.magnitude;
            if (dirLen <= 0.01) return false;
            DVector3 halfB = (curBMax - curBMin) * 0.5;
            DVector3 expandMin = aMin - halfB;
            DVector3 expandMax = aMax + halfB;

            // 起点已在膨胀盒内 ⇒ 没有"进入面"。这是上一步停在接触点（留 0.1m 余量）的接续帧，
            // 不是新的 tunneling；若继续算，RayAabb 会以 t=0 命中、把 B 反复钉回旧相对位置
            // （2026-10-17 实测：爆炸把 A 推走时，B 每步被拖行 ≈536m，表现为"黏着"）。
            if (prevCenter.x > expandMin.x && prevCenter.x < expandMax.x
                && prevCenter.y > expandMin.y && prevCenter.y < expandMax.y
                && prevCenter.z > expandMin.z && prevCenter.z < expandMax.z)
                return false;

            if (!RayAabb(prevCenter, dir, expandMin, expandMax, out double tHit)) return false;
            double tStop = Math.Max(0.0, tHit - 0.1 / dirLen);
            stopPos = prevCenter + dir * tStop;

            DVector3 entry = prevCenter + dir * tHit;
            double dx = Math.Min(System.Math.Abs(entry.x - expandMin.x), System.Math.Abs(entry.x - expandMax.x));
            double dy = Math.Min(System.Math.Abs(entry.y - expandMin.y), System.Math.Abs(entry.y - expandMax.y));
            double dz = Math.Min(System.Math.Abs(entry.z - expandMin.z), System.Math.Abs(entry.z - expandMax.z));
            if (dx <= dy && dx <= dz)
                normal = new DVector3(entry.x >= (expandMin.x + expandMax.x) * 0.5 ? 1.0 : -1.0, 0, 0);
            else if (dy <= dz)
                normal = new DVector3(0, entry.y >= (expandMin.y + expandMax.y) * 0.5 ? 1.0 : -1.0, 0);
            else
                normal = new DVector3(0, 0, entry.z >= (expandMin.z + expandMax.z) * 0.5 ? 1.0 : -1.0);

            if (normal.sqrMagnitude < 0.5)
            {
                DVector3 fallback = curCenter - (aMin + aMax) * 0.5;
                normal = fallback.sqrMagnitude > 1e-12 ? fallback.normalized : (-dir).normalized;
            }
            return true;
        }

        private void ApplyTunneledStop(SpaceEntityGroup b, DVector3 stopPos, int aGroupId)
        {
            // 圆轨组的位置是解析的（GetCircularPositionAtTick），改 CurrentOrbit.position 无效；
            // 必须退出圆轨后停位才能生效（否则 tunneling 对圆轨目标无效，快速船照样穿过）
            if (b.IsCircularOrbit)
                b.ExitCircularOrbit();
            b.CurrentOrbit.positionSystem = (SystemVec3)stopPos;
            b.OrbitDirty = true;
            shipPrevPositions[b.GroupId] = stopPos;
            shipLastPositions[b.GroupId] = stopPos;
            if (DebugDetailedCollision)
            {
                Log.Message($"[ShipCollision] G{aGroupId} vs G{b.GroupId} tunneling 捕获：G{b.GroupId} 停于接触点 ({stopPos.x:F0},{stopPos.y:F0},{stopPos.z:F0})m");
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
            // 窄相内层是 nA×nB 次调用，绝大多数 part 对在 X 轴就分开 ⇒ 逐轴早退（少 2/3 的
            // min/max 运算）。false 时 oy/oz 不保证有值；现有调用方都只在 true 分支读它们。
            ox = Math.Min(aMax.x, bMax.x) - Math.Max(aMin.x, bMin.x);
            if (ox <= 0) { oy = 0; oz = 0; return false; }
            oy = Math.Min(aMax.y, bMax.y) - Math.Max(aMin.y, bMin.y);
            if (oy <= 0) { oz = 0; return false; }
            oz = Math.Min(aMax.z, bMax.z) - Math.Max(aMin.z, bMin.z);
            return oz > 0;
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
                entity.GetAabbSystem(out var eMin, out var eMax);
                double er = (eMax - eMin).magnitude * 0.5;
                if (er > r) r = er;
            }
            return r;
        }

        private void GetGroupAabbSystem(SpaceEntityGroup group, DVector3 groupPos, SpaceGameComp spaceGame,
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
                entity.GetAabbSystem(out var relMin, out var relMax);
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
            return group.CurrentOrbit.velocitySystem.v;   // 批6 S3-3c：读带系字段
        }

        /// <summary>
        /// 碰撞动量交换用的质量（2026-10-17 用户裁定：**燃料参与碰撞动量交换**）：
        /// 干质量（部件结构） + 推进源当前工质质量；下限 1 kg。
        /// </summary>
        internal static double CollisionMassKg(double dryMassKg, double propellantMassKg)
            => Math.Max(dryMassKg + propellantMassKg, 1.0);

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
                // 燃料参与碰撞动量交换（2026-10-17 用户裁定）；有图/无图推进源都由聚合器统一给出。
                var src = spaceGame.GetGroupPropulsionSource(group);
                if (src != null)
                    m += src.TotalPropellantMass;
            }
            return CollisionMassKg(m, 0.0);
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
            DVector3 delta = newVel - group.CurrentOrbit.velocitySystem.v;
            if (delta.sqrMagnitude <= 0.0) return;
            group.pendingImpulse += delta;
        }

        /// <summary>
        /// AABB 位置分离：三轴同时推开（各推 overlap/2），一次收敛。
        /// 只推最短轴会导致其他轴仍重叠、下一帧重复触发（刷屏 + 重复冲量）。
        /// </summary>
        /// <summary>
        /// 组级 AABB 最小穿透轴（碰1；part 对找不到时回退）。返回单位轴（A→B 方向），`depth` = 该轴重叠量。
        /// 与"质心连线"不同：贴在斜面上时法线由**接触几何**决定，而不是两船中心连线。
        /// </summary>
        internal static DVector3 MinPenetrationAxis(double ox, double oy, double oz, DVector3 pa, DVector3 pb, out double depth)
        {
            if (ox <= oy && ox <= oz)
            {
                depth = ox;
                return new DVector3(pb.x >= pa.x ? 1.0 : -1.0, 0.0, 0.0);
            }
            if (oy <= oz)
            {
                depth = oy;
                return new DVector3(0.0, pb.y >= pa.y ? 1.0 : -1.0, 0.0);
            }
            depth = oz;
            return new DVector3(0.0, 0.0, pb.z >= pa.z ? 1.0 : -1.0);
        }

        /// <summary>
        /// 碰 3 修订（2026-10-17 用户裁 A）：找不到 part 对时是否必须回退组级并集盒。
        /// 两支都有可用 part ⇒ **不回退**（并集盒空角不算接触）；只有一方没有可用 part 才回退。
        /// </summary>
        internal static bool ShouldFallbackToGroupAabb(int usablePartsA, int usablePartsB)
            => usablePartsA <= 0 || usablePartsB <= 0;

        /// <summary>
        /// 组级冲击法线符号归一（2026-10-17 S1 间歇性回归的治本）：
        /// part 窄相返回的是"最深 part 对 A→B"法线，但当两侧零件**交错**、或接触盒中心
        /// **完全重合**（SAT 的 `delta≈0`，轴符号按轴序退化）时，这个法线可能与组心连线相反。
        /// 组级冲量与位置分离都以组心连线（`centerDelta = posB - posA`）为准；
        /// 组心重合（`centerDelta≈0`）时无方向可依，保留原法线交给"一步跨过"/相对速度判据。
        /// </summary>
        internal static DVector3 AlignImpactNormal(DVector3 normal, DVector3 centerDelta)
        {
            if (centerDelta.sqrMagnitude > 0.0 && DVector3.Dot(normal, centerDelta) < 0.0)
                return -normal;
            return normal;
        }

        /// <summary>
        /// **船-船窄相：找最深重叠的 part 对**（批 3；碰1/碰3/碰8）。
        /// 遍历两侧实体的全部部件（含损毁残骸，B5 / D-P7）、排除 `noCollide`，取 AABB 三轴重叠量的最小值（穿透深度）最大的一对；
        /// 法线 = 该对的最小穿透轴（A→B 符号），接触点 = 重叠盒中心。找不到 ⇒ false（组级并集盒的缝隙）。
        /// </summary>
        private bool TryFindDeepestPartPair(SpaceEntityGroup a, SpaceEntityGroup b,
            GroupCollisionCache ca, GroupCollisionCache cb, SpaceGameComp spaceGame,
            out DVector3 normal, out double depth, out DVector3 contact)
        {
            EnsurePartBoxes(ca, a, spaceGame);
            EnsurePartBoxes(cb, b, spaceGame);
            // 组 AABB 的交集是任何 part 对重叠点的**上界盒**：交集外的 part 不可能与对方任何 part 重叠。
            // 先按它裁剪候选 part（长船/斜靠只剩端部一小块时，能把 64×64 压到几×几）。
            DVector3 clipMin = DVector3.Max(ca.curMin, cb.curMin);
            DVector3 clipMax = DVector3.Min(ca.curMax, cb.curMax);
            return TryFindDeepestPartPair(ca.parts, cb.parts, clipMin, clipMax,
                out normal, out depth, out contact);
        }

        /// <summary>按需构建本组的 part AABB 缓存（同一 tick 内与多个对手窄相只构建一次）。</summary>
        private static void EnsurePartBoxes(GroupCollisionCache cache, SpaceEntityGroup group, SpaceGameComp spaceGame)
        {
            int version = GroupPartsVersion(group, spaceGame);
            if (cache.partsBuilt && cache.partsVersion == version) return;
            CollectPartAabbs(group, cache.pos, spaceGame, cache.parts);
            cache.partsBuilt = true;
            cache.partsVersion = version;
        }

        /// <summary>B1：部件集指纹（实体集版本 ⊕ 各实体 partSetVersion）。原地增删/改 part 都会变。</summary>
        private static int GroupPartsVersion(SpaceEntityGroup group, SpaceGameComp spaceGame)
        {
            int stamp = group.entitySetVersion * 397;
            foreach (int eid in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(eid);
                stamp = stamp * 31 + (entity?.partSetVersion ?? 0);
            }
            return stamp;
        }

        /// <summary>把一个组的可碰撞 part（含损毁残骸）的世界 AABB 收集进缓存（每件只做一次姿态变换）。</summary>
        private static void CollectPartAabbs(SpaceEntityGroup group, DVector3 groupPos,
            SpaceGameComp spaceGame, List<PartAabb> into)
        {
            into.Clear();
            foreach (int eid in group.GetEntityIds())
            {
                var entity = spaceGame.GetEntityById(eid);
                if (entity == null || !entity.IsCollidable) continue;

                DVector3 entityPos = groupPos + entity.Position;
                var parts = entity.Parts;
                for (int i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    // B5 / D-P7：损毁件仍参与碰撞（残骸可撞）；noCollide 仍豁免。
                    if (part == null || part.noCollide) continue;
                    if (PartGeometry.IsIdentityOrZero(part.rotationShip))
                    {
                        // 旧路径：无自身姿态的 part 保持轴对齐 AABB（逐位不变）
                        if (!entity.GetPartAabbSystem(i, out DVector3 relMin, out DVector3 relMax)) continue;
                        into.Add(new PartAabb(entityPos + relMin, entityPos + relMax, into.Count));
                    }
                    else
                    {
                        // B3b：带自身姿态的 part 存世界 OBB；AABB 由投影得到（广播/裁剪仍用它）
                        DQuaternion entityRot = PartGeometry.EffectiveRotation(entity.Rotation);
                        DVector3 center = entityPos + DVector3.Rotate(part.positionShip, entityRot);
                        DQuaternion worldRot = entityRot * PartGeometry.EffectiveRotation(part.rotationShip);
                        into.Add(new PartAabb(center, part.boundsSizeShip * 0.5, worldRot, into.Count));
                    }
                }
            }
        }

        /// <summary>nA×nB 超过此值改走 sweep-and-prune（32×32 起实测更快；再小排序开销不划算）。</summary>
        private const long SweepAndPruneThreshold = 512;

        /// <summary>窄相核心（纯函数，可单测/台架）：取最深重叠 part 对，返回最小穿透轴/深度/接触点。
        /// 小规模走两两比较；超阈值走 sweep-and-prune。此重载自行求两组 union 的交集作裁剪盒（测试/台架用）；
        /// 生产走带 clip 的重载（组级 AABB 已缓存，省一次 union 扫描）。</summary>
        internal static bool TryFindDeepestPartPair(List<PartAabb> boxesA, List<PartAabb> boxesB,
            out DVector3 normal, out double depth, out DVector3 contact,
            bool forceNested = false, bool forceSweep = false, bool forceBvh = false)
        {
            if (!TryGetUnion(boxesA, out DVector3 aMin, out DVector3 aMax)
                || !TryGetUnion(boxesB, out DVector3 bMin, out DVector3 bMax))
            {
                normal = DVector3.zero;
                depth = 0.0;
                contact = DVector3.zero;
                return false;
            }
            DVector3 clipMin = DVector3.Max(aMin, bMin);
            DVector3 clipMax = DVector3.Min(aMax, bMax);
            return TryFindDeepestPartPair(boxesA, boxesB, clipMin, clipMax,
                out normal, out depth, out contact, forceNested, forceSweep, forceBvh);
        }

        /// <summary>带裁剪盒的窄相核心：只考虑与 clip 盒有正重叠的 part（clip = 组 AABB 交集）。</summary>
        internal static bool TryFindDeepestPartPair(List<PartAabb> boxesA, List<PartAabb> boxesB,
            DVector3 clipMin, DVector3 clipMax,
            out DVector3 normal, out double depth, out DVector3 contact,
            bool forceNested = false, bool forceSweep = false, bool forceBvh = false)
        {
            normal = DVector3.zero;
            depth = 0.0;
            contact = DVector3.zero;
            if (boxesA.Count == 0 || boxesB.Count == 0) return false;
            if (clipMin.x >= clipMax.x || clipMin.y >= clipMax.y || clipMin.z >= clipMax.z) return false;

            long product = (long)boxesA.Count * boxesB.Count;
            if (forceNested)
                return DeepestNested(boxesA, boxesB, clipMin, clipMax, out normal, out depth, out contact);
            if (forceBvh)
                return DeepestByBvh(boxesA, boxesB, clipMin, clipMax, out normal, out depth, out contact);
            if (forceSweep || product > SweepAndPruneThreshold)
                return DeepestBySweepAndPrune(boxesA, boxesB, clipMin, clipMax,
                    out normal, out depth, out contact);
            return DeepestNested(boxesA, boxesB, clipMin, clipMax, out normal, out depth, out contact);
        }

        private static bool TryGetUnion(List<PartAabb> boxes, out DVector3 min, out DVector3 max)
        {
            if (boxes.Count == 0)
            {
                min = max = DVector3.zero;
                return false;
            }
            min = boxes[0].Min;
            max = boxes[0].Max;
            for (int i = 1; i < boxes.Count; i++)
            {
                min = DVector3.Min(min, boxes[i].Min);
                max = DVector3.Max(max, boxes[i].Max);
            }
            return true;
        }

        /// <summary>part 盒与裁剪盒是否有正重叠（交集外的 part 不可能参与任何 part 对重叠）。</summary>
        private static bool OverlapsClip(in PartAabb box, in DVector3 clipMin, in DVector3 clipMax)
            => box.Max.x > clipMin.x && box.Min.x < clipMax.x
            && box.Max.y > clipMin.y && box.Min.y < clipMax.y
            && box.Max.z > clipMin.z && box.Min.z < clipMax.z;

        private static bool DeepestNested(List<PartAabb> boxesA, List<PartAabb> boxesB,
            DVector3 clipMin, DVector3 clipMax,
            out DVector3 normal, out double depth, out DVector3 contact)
        {
            normal = DVector3.zero;
            depth = 0.0;
            contact = DVector3.zero;
            double best = 0.0;
            int bestA = int.MaxValue, bestB = int.MaxValue;

            for (int i = 0; i < boxesA.Count; i++)
            {
                PartAabb aBox = boxesA[i];
                if (!OverlapsClip(aBox, clipMin, clipMax)) continue;
                for (int j = 0; j < boxesB.Count; j++)
                {
                    PartAabb bBox = boxesB[j];
                    if (!OverlapsClip(bBox, clipMin, clipMax)) continue;
                    ConsiderPartPair(aBox, bBox, ref best, ref bestA, ref bestB,
                        ref normal, ref depth, ref contact);
                }
            }
            return best > 0.0;
        }

        /// <summary>X 轴 sweep-and-prune：按 Min.X 排序起止事件，活跃集内配对（只对 X 重叠的对验 Y/Z）。
        /// 事件/活跃下标数组走 ArrayPool ⇒ 每对零 GC。并列深度按 (A.Order,B.Order) 取最小，
        /// 与嵌套循环的 A 主序/B 次序结果逐位一致。</summary>
        private static bool DeepestBySweepAndPrune(List<PartAabb> boxesA, List<PartAabb> boxesB,
            DVector3 clipMin, DVector3 clipMax,
            out DVector3 normal, out double depth, out DVector3 contact)
        {
            normal = DVector3.zero;
            depth = 0.0;
            contact = DVector3.zero;
            double best = 0.0;
            int bestA = int.MaxValue, bestB = int.MaxValue;

            int nA = boxesA.Count;
            int nB = boxesB.Count;
            int axis = PickSweepAxis(boxesA, boxesB);
            int maxEvents = 2 * (nA + nB);
            SapEvent[] events = ArrayPool<SapEvent>.Shared.Rent(maxEvents);
            int[] activeA = ArrayPool<int>.Shared.Rent(nA);
            int[] activeB = ArrayPool<int>.Shared.Rent(nB);
            int[] posA = ArrayPool<int>.Shared.Rent(nA);
            int[] posB = ArrayPool<int>.Shared.Rent(nB);
            try
            {
                int e = 0;
                for (int i = 0; i < nA; i++)
                {
                    PartAabb aBox = boxesA[i];
                    if (!OverlapsClip(aBox, clipMin, clipMax)) continue;
                    events[e++] = new SapEvent(AxisMin(aBox, axis), true, false, i);
                    events[e++] = new SapEvent(AxisMax(aBox, axis), false, false, i);
                }
                for (int j = 0; j < nB; j++)
                {
                    PartAabb bBox = boxesB[j];
                    if (!OverlapsClip(bBox, clipMin, clipMax)) continue;
                    events[e++] = new SapEvent(AxisMin(bBox, axis), true, true, j);
                    events[e++] = new SapEvent(AxisMax(bBox, axis), false, true, j);
                }
                if (e == 0) return false;
                Array.Sort(events, 0, e, SapEventComparer.Instance);

                Array.Fill(posA, -1, 0, nA);
                Array.Fill(posB, -1, 0, nB);
                int countA = 0, countB = 0;

                for (int k = 0; k < e; k++)
                {
                    SapEvent ev = events[k];
                    if (ev.IsStart)
                    {
                        if (!ev.IsB)
                        {
                            PartAabb aBox = boxesA[ev.Index];
                            for (int q = 0; q < countB; q++)
                                ConsiderPartPair(aBox, boxesB[activeB[q]], ref best, ref bestA, ref bestB,
                                    ref normal, ref depth, ref contact);
                            posA[ev.Index] = countA;
                            activeA[countA++] = ev.Index;
                        }
                        else
                        {
                            PartAabb bBox = boxesB[ev.Index];
                            for (int q = 0; q < countA; q++)
                                ConsiderPartPair(boxesA[activeA[q]], bBox, ref best, ref bestA, ref bestB,
                                    ref normal, ref depth, ref contact);
                            posB[ev.Index] = countB;
                            activeB[countB++] = ev.Index;
                        }
                    }
                    else if (!ev.IsB)
                    {
                        int p = posA[ev.Index];
                        if (p < 0) continue;
                        int last = activeA[--countA];
                        activeA[p] = last;
                        posA[last] = p;
                        posA[ev.Index] = -1;
                    }
                    else
                    {
                        int p = posB[ev.Index];
                        if (p < 0) continue;
                        int last = activeB[--countB];
                        activeB[p] = last;
                        posB[last] = p;
                        posB[ev.Index] = -1;
                    }
                }
            }
            finally
            {
                ArrayPool<SapEvent>.Shared.Return(events);
                ArrayPool<int>.Shared.Return(activeA);
                ArrayPool<int>.Shared.Return(activeB);
                ArrayPool<int>.Shared.Return(posA);
                ArrayPool<int>.Shared.Return(posB);
            }
            return best > 0.0;
        }

        // ── BVH（大件数窄相）────────────────────────────────────────────
        // 判过不接（2026-10-17）：512×512 稀疏 SAP 49.7µs vs BVH 60.2µs；1024² SAP 118.6 vs 134.2；
        // 稠密 512² SAP 112.0 vs BVH 158.7 ⇒ BVH 建树/查询在本工作负载下始终慢于 SAP。
        // 保留实现仅供 `forceBvh` 台架复测；**生产默认不选它**。
        private const int BvhLeafSize = 4;

        private struct BvhNode
        {
            public DVector3 Min, Max;
            public int Left, Right;   // -1 = 叶
            public int Start, Count;  // 叶子在 idx 数组中的区间
            public bool IsLeaf => Left < 0;
        }

        private static bool DeepestByBvh(List<PartAabb> boxesA, List<PartAabb> boxesB,
            DVector3 clipMin, DVector3 clipMax,
            out DVector3 normal, out double depth, out DVector3 contact)
        {
            normal = DVector3.zero;
            depth = 0.0;
            contact = DVector3.zero;

            int[] idxA = ArrayPool<int>.Shared.Rent(boxesA.Count);
            int[] idxB = ArrayPool<int>.Shared.Rent(boxesB.Count);
            try
            {
                int nA = FillClipIndices(boxesA, clipMin, clipMax, idxA);
                int nB = FillClipIndices(boxesB, clipMin, clipMax, idxB);
                if (nA == 0 || nB == 0) return false;

                BvhNode[] nodesA = ArrayPool<BvhNode>.Shared.Rent(2 * nA);
                BvhNode[] nodesB = ArrayPool<BvhNode>.Shared.Rent(2 * nB);
                try
                {
                    int countA = 0, countB = 0;
                    int rootA = BuildBvh(boxesA, idxA, 0, nA, nodesA, ref countA, 0);
                    int rootB = BuildBvh(boxesB, idxB, 0, nB, nodesB, ref countB, 0);

                    double best = 0.0;
                    int bestA = int.MaxValue, bestB = int.MaxValue;
                    QueryBvh(rootA, rootB, nodesA, nodesB, idxA, idxB, boxesA, boxesB,
                        ref best, ref bestA, ref bestB, ref normal, ref depth, ref contact);
                    return best > 0.0;
                }
                finally
                {
                    ArrayPool<BvhNode>.Shared.Return(nodesA);
                    ArrayPool<BvhNode>.Shared.Return(nodesB);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(idxA);
                ArrayPool<int>.Shared.Return(idxB);
            }
        }

        private static int FillClipIndices(List<PartAabb> boxes, DVector3 clipMin, DVector3 clipMax, int[] idx)
        {
            int n = 0;
            for (int i = 0; i < boxes.Count; i++)
                if (OverlapsClip(boxes[i], clipMin, clipMax)) idx[n++] = i;
            return n;
        }

        private static int BuildBvh(List<PartAabb> boxes, int[] idx, int start, int count,
            BvhNode[] nodes, ref int nodeCount, int depth)
        {
            int node = nodeCount++;
            ComputeBounds(boxes, idx, start, count, out DVector3 min, out DVector3 max);
            nodes[node] = new BvhNode
            {
                Min = min, Max = max,
                Left = -1, Right = -1,
                Start = start, Count = count
            };
            if (count <= BvhLeafSize || depth >= 24) return node;

            DVector3 ext = max - min;
            int axis = ext.x >= ext.y && ext.x >= ext.z ? 0 : (ext.y >= ext.z ? 1 : 2);
            int mid = start + (count >> 1);
            NthElement(boxes, idx, start, start + count - 1, mid, axis);

            int left = BuildBvh(boxes, idx, start, mid - start, nodes, ref nodeCount, depth + 1);
            int right = BuildBvh(boxes, idx, mid, start + count - mid, nodes, ref nodeCount, depth + 1);
            BvhNode n = nodes[node];
            n.Left = left;
            n.Right = right;
            n.Start = 0;
            n.Count = 0;
            nodes[node] = n;
            return node;
        }

        private static void ComputeBounds(List<PartAabb> boxes, int[] idx, int start, int count,
            out DVector3 min, out DVector3 max)
        {
            min = boxes[idx[start]].Min;
            max = boxes[idx[start]].Max;
            for (int k = start + 1; k < start + count; k++)
            {
                PartAabb b = boxes[idx[k]];
                min = DVector3.Min(min, b.Min);
                max = DVector3.Max(max, b.Max);
            }
        }

        /// <summary>按 part 中心第 axis 轴的 quickselect（就地重排 idx[start..end]，使 nth 落位）。</summary>
        private static void NthElement(List<PartAabb> boxes, int[] idx, int left, int right, int nth, int axis)
        {
            while (left < right)
            {
                double pivot = Center(boxes[idx[(left + right) >> 1]], axis);
                int i = left, j = right;
                while (i <= j)
                {
                    while (Center(boxes[idx[i]], axis) < pivot) i++;
                    while (Center(boxes[idx[j]], axis) > pivot) j--;
                    if (i <= j)
                    {
                        int t = idx[i];
                        idx[i] = idx[j];
                        idx[j] = t;
                        i++;
                        j--;
                    }
                }
                if (nth <= j) right = j;
                else if (nth >= i) left = i;
                else break;
            }
        }

        private static double Center(in PartAabb box, int axis)
            => axis == 0 ? (box.Min.x + box.Max.x) * 0.5
             : axis == 1 ? (box.Min.y + box.Max.y) * 0.5
             : (box.Min.z + box.Max.z) * 0.5;

        private static void QueryBvh(int ai, int bi, BvhNode[] nodesA, BvhNode[] nodesB,
            int[] idxA, int[] idxB, List<PartAabb> boxesA, List<PartAabb> boxesB,
            ref double best, ref int bestA, ref int bestB,
            ref DVector3 normal, ref double depth, ref DVector3 contact)
        {
            BvhNode a = nodesA[ai];
            BvhNode b = nodesB[bi];
            if (!AabbOverlap(a.Min, a.Max, b.Min, b.Max, out _, out _, out _)) return;

            if (a.IsLeaf && b.IsLeaf)
            {
                int endA = a.Start + a.Count;
                int endB = b.Start + b.Count;
                for (int i = a.Start; i < endA; i++)
                    for (int j = b.Start; j < endB; j++)
                        ConsiderPartPair(boxesA[idxA[i]], boxesB[idxB[j]],
                            ref best, ref bestA, ref bestB, ref normal, ref depth, ref contact);
                return;
            }
            if (a.IsLeaf)
            {
                QueryBvh(ai, b.Left, nodesA, nodesB, idxA, idxB, boxesA, boxesB,
                    ref best, ref bestA, ref bestB, ref normal, ref depth, ref contact);
                QueryBvh(ai, b.Right, nodesA, nodesB, idxA, idxB, boxesA, boxesB,
                    ref best, ref bestA, ref bestB, ref normal, ref depth, ref contact);
                return;
            }
            if (b.IsLeaf)
            {
                QueryBvh(a.Left, bi, nodesA, nodesB, idxA, idxB, boxesA, boxesB,
                    ref best, ref bestA, ref bestB, ref normal, ref depth, ref contact);
                QueryBvh(a.Right, bi, nodesA, nodesB, idxA, idxB, boxesA, boxesB,
                    ref best, ref bestA, ref bestB, ref normal, ref depth, ref contact);
                return;
            }
            // 两边都是内部节点：下钻 A（树平衡，深度 O(log n)）
            QueryBvh(a.Left, bi, nodesA, nodesB, idxA, idxB, boxesA, boxesB,
                ref best, ref bestA, ref bestB, ref normal, ref depth, ref contact);
            QueryBvh(a.Right, bi, nodesA, nodesB, idxA, idxB, boxesA, boxesB,
                ref best, ref bestA, ref bestB, ref normal, ref depth, ref contact);
        }

        /// <summary>选扫描轴：取两侧 part 总包围盒**跨度最大**的轴，最小化活跃集（细长船沿长轴扫）。</summary>
        private static int PickSweepAxis(List<PartAabb> boxesA, List<PartAabb> boxesB)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;

            Accumulate(boxesA);
            Accumulate(boxesB);

            double spanX = maxX - minX;
            double spanY = maxY - minY;
            double spanZ = maxZ - minZ;
            if (spanX >= spanY && spanX >= spanZ) return 0;
            return spanY >= spanZ ? 1 : 2;

            void Accumulate(List<PartAabb> boxes)
            {
                for (int i = 0; i < boxes.Count; i++)
                {
                    PartAabb box = boxes[i];
                    if (box.Min.x < minX) minX = box.Min.x;
                    if (box.Max.x > maxX) maxX = box.Max.x;
                    if (box.Min.y < minY) minY = box.Min.y;
                    if (box.Max.y > maxY) maxY = box.Max.y;
                    if (box.Min.z < minZ) minZ = box.Min.z;
                    if (box.Max.z > maxZ) maxZ = box.Max.z;
                }
            }
        }

        private static double AxisMin(in PartAabb box, int axis)
            => axis == 0 ? box.Min.x : axis == 1 ? box.Min.y : box.Min.z;

        private static double AxisMax(in PartAabb box, int axis)
            => axis == 0 ? box.Max.x : axis == 1 ? box.Max.y : box.Max.z;

        /// <summary>比较一对 part：更新最深重叠（并列深度按 (A.Order,B.Order) 字典序取最小，复现嵌套顺序）。</summary>
        private static void ConsiderPartPair(in PartAabb aBox, in PartAabb bBox,
            ref double best, ref int bestA, ref int bestB,
            ref DVector3 normal, ref double depth, ref DVector3 contact)
        {
            // B3b：任一侧带自身姿态 ⇒ 精确 OBB-SAT；两侧都无姿态 ⇒ 旧 AABB 路径逐位不变。
            if (aBox.UseObb || bBox.UseObb)
            {
                if (!PartGeometry.ObbObbSat(aBox.Center, aBox.Half, aBox.Rotation,
                        bBox.Center, bBox.Half, bBox.Rotation,
                        out DVector3 obbNormal, out double obbDepth, out DVector3 obbContact))
                    return;
                if (obbDepth < best) return;
                if (obbDepth == best)
                {
                    bool earlierObb = aBox.Order < bestA || (aBox.Order == bestA && bBox.Order < bestB);
                    if (!earlierObb) return;
                }
                best = obbDepth;
                bestA = aBox.Order;
                bestB = bBox.Order;
                normal = obbNormal;
                depth = obbDepth;
                contact = obbContact;
                return;
            }

            if (!AabbOverlap(aBox.Min, aBox.Max, bBox.Min, bBox.Max,
                    out double ox, out double oy, out double oz))
                return;
            double d = Math.Min(ox, Math.Min(oy, oz));
            if (d < best) return;
            if (d == best)
            {
                bool earlier = aBox.Order < bestA || (aBox.Order == bestA && bBox.Order < bestB);
                if (!earlier) return;
            }

            DVector3 ca = (aBox.Min + aBox.Max) * 0.5;
            DVector3 cb = (bBox.Min + bBox.Max) * 0.5;
            DVector3 axis = MinPenetrationAxis(ox, oy, oz, ca, cb, out _);
            best = d;
            bestA = aBox.Order;
            bestB = bBox.Order;
            normal = axis;
            depth = d;
            DVector3 oMin = DVector3.Max(aBox.Min, bBox.Min);
            DVector3 oMax = DVector3.Min(aBox.Max, bBox.Max);
            contact = (oMin + oMax) * 0.5;
        }

        private readonly struct SapEvent
        {
            public readonly double X;
            public readonly bool IsStart;
            public readonly bool IsB;
            public readonly int Index;

            public SapEvent(double x, bool isStart, bool isB, int index)
            {
                X = x; IsStart = isStart; IsB = isB; Index = index;
            }
        }

        private sealed class SapEventComparer : IComparer<SapEvent>
        {
            public static readonly SapEventComparer Instance = new();
            public int Compare(SapEvent x, SapEvent y)
            {
                int c = x.X.CompareTo(y.X);
                if (c != 0) return c;
                // 同一 X：起点先于终点（零重叠会在 AabbOverlap 里被 ox>0 拒掉，顺序不影响正确性，只求确定）
                if (x.IsStart != y.IsStart) return x.IsStart ? -1 : 1;
                if (x.IsB != y.IsB) return x.IsB ? 1 : -1;
                return x.Index.CompareTo(y.Index);
            }
        }

        private void SeparatePositions(SpaceEntityGroup a, SpaceEntityGroup b, DVector3 n, double depth)
        {
            if (PerfProbe.Enabled) PerfProbe.NoteContact(); // 探针：接触解算唯一收口（验收器碰撞阶段判据）
            if (a.IsCircularOrbit) a.ExitCircularOrbit();
            if (b.IsCircularOrbit) b.ExitCircularOrbit();

            // 碰5：质量反比分离（轻的多走、重的少走；总质量退化时各半）
            double ma = GetGroupMass(a);
            double mb = GetGroupMass(b);
            double total = ma + mb;
            double wa = total > 0 ? mb / total : 0.5;
            double wb = total > 0 ? ma / total : 0.5;
            a.CurrentOrbit.positionSystem = a.CurrentOrbit.positionSystem - (SystemVec3)(n * (depth * wa));
            b.CurrentOrbit.positionSystem = b.CurrentOrbit.positionSystem + (SystemVec3)(n * (depth * wb));

            a.OrbitDirty = true;
            b.OrbitDirty = true;

            // 分离后立即刷新扫掠基准：否则下拍并集含"分离前位置"，会误报重叠并反复分离。
            // 必须同时更新 shipLastPositions，否则下个 SyncAllShipsToGrid 会用分离前的旧值
            // 覆盖 shipPrevPositions（扫掠并集又含分离前位置）。
            shipPrevPositions[a.GroupId] = a.CurrentOrbit.positionSystem.v;
            shipPrevPositions[b.GroupId] = b.CurrentOrbit.positionSystem.v;
            shipLastPositions[a.GroupId] = a.CurrentOrbit.positionSystem.v;
            shipLastPositions[b.GroupId] = b.CurrentOrbit.positionSystem.v;
        }

        /// <summary>低速撞击等效：碰1/5/7（最小穿透轴、质量反比、e=0.2 + 低速阈值）+ 角冲量（经惯量张量）。</summary>
        private void ResolveImpact(SpaceEntityGroup a, SpaceEntityGroup b, DVector3 pa, DVector3 pb,
            DVector3 va, DVector3 vb, DVector3 nDir, double vRel, DVector3 nSep, double depth,
            DVector3 contactPoint, bool usedPartPair)
        {
            double ma = GetGroupMass(a);
            double mb = GetGroupMass(b);
            double e = vRel >= CollisionRestitutionMinSpeed ? CollisionRestitution : 0.0;
            double j = -(1.0 + e) * vRel / (1.0 / ma + 1.0 / mb);

            DVector3 vaAfter = va + nDir * (j / ma);
            DVector3 vbAfter = vb - nDir * (j / mb);
            ApplyGroupVelocity(a, vaAfter);
            ApplyGroupVelocity(b, vbAfter);

            // 角冲量：J 作用在接触点（世界系），Δω = I⁻¹ (r × J)（r 相对质心）
            DVector3 impulse = nDir * j;
            DVector3 wA0 = a.angularVelocity;
            DVector3 wB0 = b.angularVelocity;
            DVector3 dwA = ApplyCollisionAngularImpulse(a, contactPoint, impulse);
            DVector3 dwB = ApplyCollisionAngularImpulse(b, contactPoint, -impulse);

            SeparatePositions(a, b, nSep, depth);

            if (CaptureCollisionRecords && debugCollisionRecords.Count < MaxDebugCollisionRecords)
            {
                debugCollisionRecords.Add(new CollisionDebugRecord
                {
                    tick = Find.TickManager?.TicksGame ?? 0,
                    aGroup = a.GroupId,
                    bGroup = b.GroupId,
                    pa = pa,
                    pb = pb,
                    rotA = a.CurrentOrbit.rotation.ToQuaternion(),
                    rotB = b.CurrentOrbit.rotation.ToQuaternion(),
                    va = va,
                    vb = vb,
                    vaAfter = vaAfter,
                    vbAfter = vbAfter,
                    n = nDir,
                    contact = contactPoint,
                    j = j,
                    vRel = vRel,
                    wA0 = wA0,
                    wB0 = wB0,
                    dwA = dwA,
                    dwB = dwB,
                    usedPartPair = usedPartPair
                });
            }

            if (DebugDetailedCollision)
            {
                Log.Message($"[ShipCollision] 撞击等效 G{a.GroupId} vs G{b.GroupId} 接近速度={vRel:F1}m/s 质量={ma:F0}/{mb:F0}kg e={e:F2} 冲量j={j:F2} 接触=({contactPoint.x:F0},{contactPoint.y:F0},{contactPoint.z:F0}) 已分离");
                // 2026-10-17 用户报「碰撞后双方同向转」⇒ 两侧 Δω 必须可读数：
                // 头对头（接触点/质心/法线共线）应 Δω≈0；点积 ≈0 = 无力矩，<0 = 反向。
                double dot = DVector3.Dot(dwA, dwB);
                Log.Message($"[碰撞角速度] G{a.GroupId}: ω前={FormatOmega(wA0)} Δω={FormatOmega(dwA)} | G{b.GroupId}: ω前={FormatOmega(wB0)} Δω={FormatOmega(dwB)} | ΔωA·ΔωB={dot:E2}（≈0 无力矩 / <0 反向） 接触=({contactPoint.x:F1},{contactPoint.y:F1},{contactPoint.z:F1}) 法线=({nDir.x:F2},{nDir.y:F2},{nDir.z:F2}) J=({impulse.x:F1},{impulse.y:F1},{impulse.z:F1}) 质心A=({a.InertiaResult.comX:F2},{a.InertiaResult.comY:F2},{a.InertiaResult.comZ:F2}) 质心B=({b.InertiaResult.comX:F2},{b.InertiaResult.comY:F2},{b.InertiaResult.comZ:F2})");
            }
        }

        /// <summary>
        /// 把世界系冲量在接触点处的角冲量加到组的角速度上（`Δω = I⁻¹·(r × J)`）。
        /// 惯量张量在**本体系**（主轴 + 方向矩阵）；r/J 先转进本体系，算完再转回世界系。
        /// 无惯量数据（headless/无部件）⇒ 静默跳过（位置/线速度响应仍然生效）。
        /// </summary>
        private DVector3 ApplyCollisionAngularImpulse(SpaceEntityGroup group, DVector3 worldContact, DVector3 worldImpulse)
        {
            DVector3 dw = ComputeCollisionDeltaOmega(group.InertiaResult, group.CurrentOrbit.rotation,
                GetGroupPos(group), worldContact, worldImpulse);
            group.angularVelocity += dw;
            return dw;
        }

        /// <summary>纯角冲量数学（静态、可单测）：`Δω = R·I_p⁻¹·(Rᵀ(r × J))`。</summary>
        internal static DVector3 ComputeCollisionDeltaOmega(in InertiaTensor.InertiaResult r, DQuaternion rotation,
            DVector3 groupPos, DVector3 worldContact, DVector3 worldImpulse)
        {
            if (!r.HasData || !(r.p1 > 0.0)) return DVector3.zero;

            DVector3 comWorld = DVector3.Rotate(new DVector3(r.comX, r.comY, r.comZ), rotation);
            DVector3 leverWorld = worldContact - groupPos - comWorld;
            DQuaternion qInv = DQuaternion.Inverse(rotation);
            DVector3 lever = DVector3.Rotate(leverWorld, qInv);
            DVector3 impulse = DVector3.Rotate(worldImpulse, qInv);

            DVector3 h = DVector3.Cross(lever, impulse);
            DVector3 dwBody = InertiaTensor.InverseInertiaMultiply(r, h.x, h.y, h.z);
            return DVector3.Rotate(dwBody, rotation);
        }

        private static string FormatOmega(DVector3 w)
            => $"({w.x:F4},{w.y:F4},{w.z:F4})rad/s|{w.magnitude * Mathf.Rad2Deg:F2}°/s";

        /// <summary>高速真爆炸：范围损伤 + 爆炸视觉（2026-10-17 用户裁定：撞击爆炸**不再施加径向冲量**）。
        /// deferredEmptyGroups：本调用处于 pair 迭代内，AoE 击毁导弹成员后变空的组延后销毁（C）。</summary>
        private void TriggerExplosion(SpaceEntityGroup a, SpaceEntityGroup b,
            DVector3 pa, DVector3 pb, DVector3 va, DVector3 vb, DVector3 nDir, double vRel,
            DVector3 nSep, double depth, DVector3 contactPoint, HashSet<int>? deferredEmptyGroups = null)
        {
            double ma = GetGroupMass(a);
            double mb = GetGroupMass(b);
            double mEff = ma * mb / (ma + mb);
            double E = 0.5 * mEff * vRel * vRel;

            // 碰 6 补（2026-10-17）：爆炸分支也必须**消费法向相对速度**。否则 80 m/s 对穿的两船
            // 在爆炸后仍按原速度互穿，下一 tick 再进重叠/扫掠 ⇒ 反复爆炸（实测 G1/G2 连续 100+ 次
            // "tunneling 捕获"后才靠爆风碰巧分开）。恢复系数与低速撞击同取 e=0.2：
            // e=0（完全非弹性）会让两船以同一速度粘在一起（2026-10-17 实测"粘住"）。
            double e = vRel >= CollisionRestitutionMinSpeed ? CollisionRestitution : 0.0;
            double jn = -(1.0 + e) * vRel / (1.0 / ma + 1.0 / mb);
            ApplyGroupVelocity(a, va + nDir * (jn / ma));
            ApplyGroupVelocity(b, vb - nDir * (jn / mb));

            DVector3 hitPoint = contactPoint.sqrMagnitude > 0.0 ? contactPoint : (pa + pb) * 0.5;
            double R = CollisionExplosionBaseRadius + CollisionExplosionRadiusK * Math.Pow(E, 1.0 / 3.0);
            double D0 = CollisionExplosionDamageK * Math.Sqrt(E);

            int frameId = GetCombatGrid()?.ResolveFrameId(a.CelestialBodyId) ?? -1;
            int damaged = ApplyExplosionDamage(frameId, hitPoint, R, D0, -1, deferredEmptyGroups);
            // 2026-10-17 用户裁定：**撞击爆炸只产生损伤，不再施加径向动量**。
            // 动量交换由上一段的碰撞冲量（jn）完成；弹头爆炸仍走 ApplyExplosionImpulse（另一条路径）。

            // 视觉残留
            activeExplosions.Add(new ExplosionInstance
            {
                position = hitPoint,
                maxRadius = R,
                remainingTicks = 60,
                totalTicks = 60,
                systemBodyId = GetSpaceGameComp()?.ResolveReferenceFrameId(a.CelestialBodyId) ?? -1
            });

            SeparatePositions(a, b, nSep, depth);

            if (DebugDetailedCollision)
            {
                Log.Message($"[爆炸] G{a.GroupId} vs G{b.GroupId} 碰撞点=({hitPoint.x:F0},{hitPoint.y:F0},{hitPoint.z:F0}) 半径={R:F0}m 能量={E:F2}J vRel={vRel:F1}m/s 损伤Part={damaged}（撞击爆炸只损伤，无径向冲量）");
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
            int excludeGroupId = -1, HashSet<int>? emptiedMissileGroups = null)
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
                        DVector3 pw = gpos + entity.Position + part.positionShip;
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
                                Log.Message($"[爆炸损伤] Entity{eid} Part[{part.partId}]\"{part.defName}\" HP≤0 被摧毁 dist={dist:F0}m");
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
                        if ((gpos + entity.Position + part.positionShip - center).magnitude <= R) { partInRange = true; break; }
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
            // （GetGroupBoundsSystem 以 groupPos 为初值做并集），故 cube(R) 仍是本判定的保守超集。
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

        }

        private void LogGridStats(int currentTick)
        {
#if DEBUG
            if (currentTick - lastLogTick < LOG_INTERVAL) return;
            lastLogTick = currentTick;

            StringBuilder sb = new StringBuilder();
            sb.Append("[网格状态] Tick=").Append(currentTick)
              .Append(" physicsTick=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{TimeScaleManager.GetPhysicsTickSafe():F0}");

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
                        sb.Append(" [体").Append(kv.Key).Append(']');
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
                    sb.Append(" 命=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{proj.remainingLife:F1}");
                    sb.Append(" 速=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{proj.velocity.magnitude:F0}");
                    sb.Append(" 格(").Append(proj.fineCx).Append(',')
                      .Append(proj.fineCy).Append(',').Append(proj.fineCz).Append(')');
                    sb.Append(']');
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
                      .Append(" 位(").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(p.x/1000):F0}").Append(',')
                      .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(p.y/1000):F0}").Append(',')
                      .Append(System.Globalization.CultureInfo.CurrentCulture, $"{(p.z/1000):F0}").Append(")km")
                      .Append(" 格(").Append(cell.cx).Append(',')
                      .Append(cell.cy).Append(',').Append(cell.cz).Append(')');
                    shown++;
                }
            }

            if (collisionSamples > 0)
            {
                sb.Append(" 阶段耗时[弹=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(projMsAccum / collisionSamples):F3}")
                  .Append(" 武=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(weaponsMsAccum / collisionSamples):F3}")
                  .Append(" 同步=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(syncMsAccum / collisionSamples):F3}")
                  .Append(" 碰=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{(collisionMsAccum / collisionSamples):F3}")
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

        /// <summary>
        /// 按索引获取 Group 的第 N 把武器（供调试窗口使用；战斗逻辑请直接传 WeaponInstance 引用）。
        /// </summary>
        public WeaponInstance? GetGroupWeapon(int groupId, int weaponIndex)
        {
            if (!groupWeapons.TryGetValue(groupId, out var weapons)) return null;
            if (weaponIndex < 0 || weaponIndex >= weapons.Count) return null;
            return weapons[weaponIndex];
        }

        /// <summary>获取 Group 的全部武器列表（供调试/UI 展示）。</summary>
        /// <summary>未注册组的共享空容器（第 7 条）：调用方只读（PFView 读 Count），
        /// 不要往里写——它是共享实例。</summary>
        private static readonly List<WeaponInstance> EmptyWeaponList = new();

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
        /// <param name="aimOffsetShip">导弹弹着偏置（目标局部米，y=0，§10 精确打击；零 = 瞄目标质心）。射弹/射线忽略。</param>
        /// <param name="volleyCount">导弹齐射枚数（弹群实体数；null = 武器 Def.missileCount）。射弹/射线忽略。</param>
        public void FireWeapon(int groupId, WeaponInstance weapon, DVector3 targetPosition, DVector3? spawnPosition = null, DVector3? targetVelocity = null, int? targetGroupId = null, bool missileFollowMode = false, DVector3 aimOffsetShip = default, int? volleyCount = null)
        {
            // [N6]/S4 **开火揭示**：非导弹开火 ⇒ 开火者自身信号抬升一个窗口（`SignalModel.RevealMultiplier`）。
            // 依据设计稿 A4/§4.3：**导弹发射器开火不算暴露**（冷发射；弹体点火才是暴露源）⇒ 按武器类型分流。
            var shooter = GetSpaceGameComp()?.GetGroupById(groupId);
            if (shooter != null && weapon?.def != null && weapon.def.weaponType != WeaponType.Missile)
                shooter.NotifyWeaponFire(TimeScaleManager.GetPhysicsTickSafe());

            if (weapon == null)
            {
                Log.Warning($"[FireWeapon] Group {groupId} 武器为空");
                return;
            }
            if (!weapon.CanFire())
            {
                Log.Warning($"[FireWeapon] 武器无法开火: overheated={weapon.isOverheated} cooldown={weapon.cooldownRemaining} ammo={weapon.ammo}");
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
                Log.Warning($"[FireWeapon] Group {groupId} 未找到");
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
                Log.Message($"[FireWeapon] Group {groupId} 目标与炮口重合（<1m），跳过开火避免卡膛弹");
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
                FireMissileWeapon(group, spaceGame, groupId, weapon, spawnPos, direction, groupVelocity, targetGroupId, missileFollowMode, aimOffsetShip, volleyCount);
                return;
            }

            DVector3 muzzleVelocity = direction * weapon.def.projectileSpeed;

            var proj = new ProjectileInstance
            {
                sourceGroupId = groupId,
                factionId = group.FactionId,
                weaponDef = weapon.def,
                // 批6 S3-3c：射弹的"出生点/出生速度"来自未标类型的算式 ⇒ 在此**显式跨界**（系统系）
                position = (SystemVec3)spawnPos,
                velocity = (SystemVec3)(groupVelocity + muzzleVelocity),
                remainingLife = weapon.def.projectileLifetime / 60f,
                damage = weapon.def.damagePerShot,
                armorPenetration = weapon.def.armorPenetration,
                affectedByGravity = weapon.def.affectedByGravity,
                prevPosition = (SystemVec3)spawnPos,
                spawnPosition = (SystemVec3)spawnPos,
                spawnVelocity = (SystemVec3)(groupVelocity + muzzleVelocity),
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
                        centralVel = mgr.GetBodyVelocityParent(group.circularCentralBodyId, physicsTickNow);
                }
            }
            double vRelMag = targetVelocity.HasValue ? (targetVelocity.Value - groupVelocity).magnitude : 0.0;
            if (DebugDetailedCombat)
            {
                Log.Message($"[射弹生成] proj={proj.id} Group {groupId} TicksGame={Find.TickManager.TicksGame} physicsTick={physicsTickNow:F1} 位置=({spawnPos.x:F0},{spawnPos.y:F0},{spawnPos.z:F0}) 目标当前=({targetPosition.x:F0},{targetPosition.y:F0},{targetPosition.z:F0}) 瞄准点=({aimPos.x:F0},{aimPos.y:F0},{aimPos.z:F0}) 提前量={leadDist:F0}m");
                Log.Message($"  └ 继承速度: 圆轨={(group.IsCircularOrbit ? "是" : "否")} 中心天体速度=({centralVel.x:F1},{centralVel.y:F1},{centralVel.z:F1}) 轨道速={groupVelocity.magnitude:F0} 炮口速={muzzleVelocity.magnitude:F0} 合速={totalVel.magnitude:F0}m/s 提前量时间≈{leadDist / Math.Max(weapon.def.projectileSpeed, 1f):F1}s 相对速度={vRelMag:F0}m/s");
            }

            // 开火详情（详细日志）：出膛点相对船心偏移 = 炮口在船体 AABB 表面的位置（验证出膛点生成）
            if (DebugDetailedCombat)
            {
                DVector3 groupPosNow = GetGroupPos(group);
                DVector3 offset = spawnPos - groupPosNow;
                Log.Message($"  └ [开火详情] 炮口相对船心偏移=({offset.x:F0},{offset.y:F0},{offset.z:F0})m 出膛方向=({direction.x:F3},{direction.y:F3},{direction.z:F3}) 出膛速度=({muzzleVelocity.x:F0},{muzzleVelocity.y:F0},{muzzleVelocity.z:F0})m/s 继承船速=({groupVelocity.x:F0},{groupVelocity.y:F0},{groupVelocity.z:F0})m/s");
            }
        }

        /// <summary>
        /// Map 炮塔开火入口（M4-F2 桥接）。复用 FireWeapon 全链路（提前量 / 卡膛检查 / 射弹生成 / 入格）。
        /// spawnPos 为炮塔在世界系的炮口位置（由 MapCoordMapper 换算），targetVelocity 供提前量解算。
        /// </summary>
        public void FireFromMapBuilding(int groupId, WeaponInstance weapon, DVector3 spawnPos, DVector3 targetPos, DVector3? targetVelocity = null, int? targetGroupId = null, DVector3 aimOffsetShip = default, int? volleyCount = null)
        {
            // 建筑释放的导弹 = Following：目标跟随发射器（定位机换目标自动同步），不立即进终末
            FireWeapon(groupId, weapon, targetPos, spawnPos, targetVelocity, targetGroupId, missileFollowMode: true, aimOffsetShip: aimOffsetShip, volleyCount: volleyCount);
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
        /// aimOffsetShip = 弹着偏置（§10 精确打击：目标局部米 y=0，随记录进制导瞄准）。
        /// volleyCount = 齐射枚数（null = 武器 Def.missileCount）。
        /// </summary>
        private void FireMissileWeapon(SpaceEntityGroup launcherGroup, SpaceGameComp spaceGame, int launcherGroupId,
            WeaponInstance weapon, DVector3 spawnPos, DVector3 direction, DVector3 groupVelocity, int? targetGroupId, bool followMode = false, DVector3 aimOffsetShip = default, int? volleyCount = null)
        {
            var wdef = weapon.def;
            var missileDef = DefDatabase<MissileDef>.GetNamedSilentFail(wdef.missileDefName);
            if (missileDef == null)
            {
                Log.Warning($"[导弹发射] 武器 {wdef.defName} 的 missileDefName='{wdef.missileDefName}' 未找到，中止发射（冷却已消耗，请检查 Def 配置）");
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
                    Log.Warning($"[导弹发射] 弹群 N={logicalCount} 超阵型边长上限（{missileDef.arraySideLimitMeters:F0}m），已压间距强行创建");
                    Messages.Message($"齐射 {logicalCount} 枚超出安全阵列（边长上限 {missileDef.arraySideLimitMeters:F0}m），已压缩间距强行发射——兄弟弹可能处于弹头 AoE 致死带内", MessageTypeDefOf.RejectInput, false);
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
                    Log.Warning($"[导弹发射] 队首收敛余量不足：目标 {targetDist / 1000.0:F1}km − 队列长 {queueLen / 1000.0:F1}km = 前沿余量 {frontRunUp / 1000.0:F1}km（&lt;5km，队首终末收敛时间受限，建议拉远目标或减小齐射数）");
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
                logicalCount, missileDef.defName, wdef.defName, mission, targetGroupId ?? -1, aimOffsetShip);

            // ── 发射摘要（无条件，一次齐射一条）：出生点 / 速度继承分解 / Mission ──
            Log.Message($"[导弹发射] G{launcherGroupId} volley={volleyId} N={logicalCount} 领弹G{missileGroupId}/E{missileEntityId} 目标G{(targetGroupId ?? -1)} Mission={mission} 出生=({birthPos.x:F0},{birthPos.y:F0},{birthPos.z:F0})m 出膛方向=({direction.x:F3},{direction.y:F3},{direction.z:F3}) 继承船速={groupVelocity.magnitude:F0}m/s 出膛速={muzzleSpeed:F0}m/s 合速={birthVelocity.magnitude:F0}m/s 主工质={missileDef.fuelMass:F0}kg RCS={missileDef.rcsFuelMass:F0}kg");

            // 出生点相对母船偏移（详细日志门控）：验证 K1 外推量
            if (DebugDetailedCombat)
            {
                DVector3 motherPos = GetGroupPos(launcherGroup);
                DVector3 offset = birthPos - motherPos;
                Log.Message($"  └ [导弹细节] 母船G{launcherGroupId} 船心=({motherPos.x:F0},{motherPos.y:F0},{motherPos.z:F0})m 出生相对船心偏移=({offset.x:F0},{offset.y:F0},{offset.z:F0})m 外推后离开盒距离={(birthPos - spawnPos).magnitude:F0}m 出膛点=({spawnPos.x:F0},{spawnPos.y:F0},{spawnPos.z:F0})m");
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
                // [N3]：弹体默认匿踪——导弹不广播自己的坐标，只能被探测到。
                // 巡航段不点火 ⇒ 信号只有弹体尺寸基数（很弱）；进入 homing 边界点火 ⇒ 信号跃升。
                // 冷发射语义（设计稿 §4.3）由此保住：信号源是弹不是船，发射器不因此暴露。
                IsStealth = true,
                CelestialBodyId = celestialBodyId
            };
            Quaternion birthRotation = Quaternion.FromToRotation(Vector3.back,
                new Vector3((float)birthDir.x, (float)birthDir.y, (float)birthDir.z));
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)birthPos,
                velocitySystem = (SystemVec3)birthVel,
                rotation = (DQuaternion)birthRotation
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
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(missileDef.bodySize.x, missileDef.bodySize.y, missileDef.bodySize.z),
                hp = missileDef.hp,
                isIndestructible = false
            };
            part.AddComponent(new HullMassComponent { mass = missileDef.dryMass });
            entity.SetParts(new List<SpacePart> { part });

            // D15 主推方向型：主推轴 body 局部（K11）
            if (missileDef.mainThrustDirectional && missileDef.mainAxisShip.sqrMagnitude > 1e-9f)
                entity.mainAxisShip = new DVector3(missileDef.mainAxisShip.x, missileDef.mainAxisShip.y, missileDef.mainAxisShip.z);

            // 主网（满罐注入 = 作战半径）+ 弱 RCS 副网（与主网异工质，K11）
            var mainProp = missileDef.Propellant;
            if (mainProp == null)
                Log.Warning($"[导弹] MissileDef {missileDef.defName} 主网工质 '{missileDef.propellantDefName}' 未找到");
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
                    Log.Warning($"[导弹] MissileDef {missileDef.defName} RCS 工质 '{missileDef.rcsPropellantDefName}' 未找到");
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
                // [N3]：弹群同样默认匿踪（同 CreateMissileGroup 的理由）
                IsStealth = true,
                CelestialBodyId = celestialBodyId
            };
            Quaternion birthRotation = Quaternion.FromToRotation(Vector3.back,
                new Vector3((float)birthDir.x, (float)birthDir.y, (float)birthDir.z));
            // 群原点 = 队列中点：出膛点沿深度前移半队长（后排实体仍在出膛点之前，不穿母船）
            float mid = (count - 1) * 0.5f * spacing;
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)(birthPos + axisDepth * mid),
                velocitySystem = (SystemVec3)birthVel,
                rotation = (DQuaternion)birthRotation
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

            Log.Message($"[导弹弹群] G{group.GroupId} N={count} 深度队列 间距={spacing:F0}m 队长={(count - 1) * spacing:F0}m 领弹E{leaderEntityId}（最前） 深度轴=({axisDepth.x:F3},{axisDepth.y:F3},{axisDepth.z:F3}) 群原点=({group.CurrentOrbit.positionSystem.x:F0},{group.CurrentOrbit.positionSystem.y:F0},{group.CurrentOrbit.positionSystem.z:F0})m｜间距依据：弹头爆炸半径={missileDef.blastRadius:F0}m 下限={minSpacing:F0}m 超长上限={(squeezed ? "是（间距被爆炸半径下限抬高）" : "否")}");

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
            GetGroupAabbSystem(launcherGroup, GetGroupPos(launcherGroup), spaceGame, out DVector3 gMin, out DVector3 gMax);
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
        /// 通过 = 真实接触（out contactSystem = 参考系首接触点转回世界系，作为弹头出生点）；
        /// 不过 = 假阳性（扫掠并集的"上一步位置带"与刚出生/伴飞导弹时间不重合）。
        /// </summary>
        private static bool MissileSegmentHitsTarget(DVector3 missilePrev, DVector3 missileCur,
            DVector3 targetPrevCenter, DVector3 targetCurMin, DVector3 targetCurMax,
            out DVector3 contactSystem)
        {
            DVector3 curCenter = (targetCurMin + targetCurMax) * 0.5;
            DVector3 half = (targetCurMax - targetCurMin) * 0.5;
            DVector3 start = missilePrev - targetPrevCenter;
            DVector3 end = missileCur - curCenter;
            contactSystem = missileCur;
            if (!CombatGrid.RaySlabIntersection(start, end, -half, half, out double tEntry, out _, out _))
                return false;
            // 首接触点（目标系）→ 世界系：目标系点 + 目标当前中心
            double t = Math.Max(0.0, Math.Min(1.0, tEntry));
            contactSystem = start + (end - start) * t + curCenter;
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
            AutonomousVehicleManager.VehicleRecord? recX, GroupCollisionCache cx, GroupCollisionCache cy,
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
                    ey.GetAabbSystem(out var relMin, out var relMax);
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
                Log.Message($"[弹群接触] G{xGroup.GroupId}(E{CountGroupEntities(xGroup)}) vs G{yGroup.GroupId}(E{CountGroupEntities(yGroup)}) 并集盒重叠但逐实体复核无真接触（阵列空当/擦边放行，不转化）");
            }

            // 阶段 2：执行转化 + 离群
            for (int i = 0; i < contacts.Count; i++)
            {
                int exId = contacts[i].entityId;
                if (!xGroup.ContainsEntity(exId)) continue; // 已被同 tick 前序对/前序命中转化
                if (missileDef == null)
                {
                    Log.Warning($"[导弹撞击] G{xGroup.GroupId} E{exId} 弹体 Def 缺失，仅移除该实体");
                    if (avm.RemoveMissileEntity(recX, exId, "撞击转化(Def缺失)"))
                        destroyedMissileIds.Add(xGroup.GroupId);
                    continue;
                }
                CreateWarheadProjectile(xGroup, recX, missileDef, contacts[i].point,
                    GetGroupVelocity(xGroup), spaceGame, combatGrid);
                Log.Message($"[导弹撞击] G{xGroup.GroupId} E{exId}→G{yGroup.GroupId} 接触=({contacts[i].point.x:F0},{contacts[i].point.y:F0},{contacts[i].point.z:F0})m 单枚转化弹头（弹群剩余继续）");
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
                Log.Error($"[漏靶弹头测试] 前置缺失：spaceGame={spaceGame != null} def={def != null}");
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
                // 批6 S3-3c：弹头的出生状态来自未标类型的算式 ⇒ 在此**显式跨界**（系统系）
                position = (SystemVec3)pos,
                velocity = (SystemVec3)vel,
                remainingLife = missileDef.warheadLifetimeSeconds,
                damage = missileDef.warheadDamage,
                armorPenetration = 0f,
                affectedByGravity = true, // 轨道环境：弹头随引力弯曲，脱靶自然衰减为轨道垃圾
                flybyDistance = 0f,
                trailEffect = false,
                prevPosition = (SystemVec3)pos,
                spawnPosition = (SystemVec3)pos,
                spawnVelocity = (SystemVec3)vel,
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
        /// 此处补 3D AoE（伤害线性衰减 + 径向冲量 + 扩散视觉），以精确命中点 hitPointSystem 为心。
        /// </summary>
        private void HandleWarheadImpact(ProjectileInstance proj, SpaceEntityGroup target, DVector3 hitPointSystem)
        {
            // B：弹头源自某弹群成员（sourceGroupId=来源组）——其死亡爆炸豁免自家残组；
            // C：AoE 击毁的导弹成员当场脱队，变空的组当场销毁（射弹路径不在 pair 迭代中，安全）
            var spaceGameHi = GetSpaceGameComp();
            var avmHi = GetAvm();
            var emptiedGroups = new HashSet<int>();
            // 帧号用射弹自带的 systemBodyId：它与网格注册同源（射弹碰撞链一直以它查网格）
            int damaged = ApplyExplosionDamage(proj.systemBodyId, hitPointSystem, proj.blastRadius, proj.blastCenterDamage,
                proj.sourceGroupId, emptiedGroups);
            ApplyExplosionImpulse(proj.systemBodyId, hitPointSystem, proj.blastRadius, proj.blastImpulseEnergy, proj.sourceGroupId);
            foreach (int gid in emptiedGroups)
            {
                avmHi?.UnregisterGroup(gid);
                spaceGameHi?.DestroyGroup(gid, "战斗中损毁（战斗部命中）");
            }
            activeExplosions.Add(new ExplosionInstance
            {
                position = hitPointSystem,
                maxRadius = proj.blastRadius,
                remainingTicks = 60,
                totalTicks = 60,
                systemBodyId = proj.systemBodyId
            });
            Log.Message($"[弹头命中] proj={proj.id}→G{target.GroupId} 爆炸中心=({hitPointSystem.x:F0},{hitPointSystem.y:F0},{hitPointSystem.z:F0})m 直扣={proj.damage:F0} AoE R={proj.blastRadius:F0}m D0={proj.blastCenterDamage:F0} damaged={damaged}");
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
                ExplodeAtPointSystem(missileGroup, rec, spaceGame, gpos + e.Position,
                    n == 0 ? "[导弹引爆]" : "[导弹群自毁]", missileGroup.GroupId);
                n++;
            }
            if (n == 0)
                ExplodeAtPointSystem(missileGroup, rec, spaceGame, gpos, "[导弹引爆]", missileGroup.GroupId);

            var avm = GetAvm();
            avm?.UnregisterGroup(missileGroup.GroupId);
            spaceGame.DestroyGroup(missileGroup.GroupId, "导弹自毁/耗尽");
        }

        /// <summary>
        /// 多实体弹群单枚引爆（拦截用，2026-09-08 §5）：爆心 = 被命中那枚实体的世界位；
        /// 只引爆不销毁群——实体离群由调用方走 AVM.RemoveMissileEntity 出口。
        /// </summary>
        internal void ExplodeMissileEntityAt(SpaceEntityGroup missileGroup,
            AutonomousVehicleManager.VehicleRecord rec, DVector3 center, SpaceGameComp spaceGame,
            int excludeGroupId = -1)
        {
            ExplodeAtPointSystem(missileGroup, rec, spaceGame, center, "[导弹单枚引爆]", excludeGroupId);
        }

        /// <summary>弹头 AoE 核心（伤害/冲量/视觉/日志）。excludeGroupId：豁免自家残组（B）；
        /// AoE 击毁变空的导弹组当场销毁（C）——调用点均在射弹路径/UI 事件，不在 pair 迭代中。</summary>
        private void ExplodeAtPointSystem(SpaceEntityGroup missileGroup,
            AutonomousVehicleManager.VehicleRecord rec, SpaceGameComp spaceGame,
            DVector3 center, string tag, int excludeGroupId = -1)
        {
            var missileDef = rec.missileDefName == null
                ? null
                : DefDatabase<MissileDef>.GetNamedSilentFail(rec.missileDefName);
            // B3：Def 缺失时仍继续（爆炸必须发生，不能因配置错误让弹头变成哑弹），
            // 但**不再静默**——否则"武器 Def 名写错"这类错误会表现为"弹头能炸但威力不对"，无从察觉。
            if (missileDef == null)
                Log.Error($"[弹头AOE] MissileDef '{(rec.missileDefName ?? "<null>")}' 未找到 → 使用兜底口径 R=1000 D0=500 E=1e9（请检查 Def 配置）");
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
                spaceGame.DestroyGroup(gid, "战斗中损毁（转化交接）");
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
            Log.Message($"{tag} G{missileGroup.GroupId} 位置=({center.x:F0},{center.y:F0},{center.z:F0})m AoE R={R:F0}m D0={D0:F0} damaged={damaged}");
        }

        /// <summary>
        /// 3D 命中 → Map 落点视觉弹。
        /// hitShipMeters 为**船体局部系**命中点（CheckSegmentGroupHit / FireRayWeapon 已含 entity.Rotation 逆旋转），
        /// part 局部 = 地图 1:1，直接映射到地图（落点反映"击中倾斜船体的真实部位"）。
        /// entryShipMeters 为可选贯通入口（船体局部系）：传入时从入口贯穿到落点（1~2 tick 极速，
        /// 定向能"光束切进船体"）；不传时瞬爆（F3 射弹命中，现状不动）。
        /// </summary>
        private void SpawnPenetrationMote(SpaceEntity entity, Map map, DVector3 hitShipMeters, DVector3? entryShipMeters = null)
        {
            if (entity == null || map == null) return;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Projectile_Shell");
            if (def == null) return;

            Vector3 pos = MapCoordMapper.ShipToMap(map,
                new Vector3((float)hitShipMeters.x, (float)hitShipMeters.y, (float)hitShipMeters.z));
            pos.y = 0f; // 落点恒在地面（爆炸层）
            pos.x = Mathf.Clamp(pos.x, 0f, map.Size.x - 1f);
            pos.z = Mathf.Clamp(pos.z, 0f, map.Size.z - 1f);

            var proj = (PFProjectile3D)ThingMaker.MakeThing(def);
            if (proj == null) return;
            IntVec3 cell = pos.ToIntVec3();
            if (!cell.InBounds(map)) cell = map.Center; // 防御

            // 定向能贯通：入口 = 命中点沿入射方向反方向延长到船体 AABB 六面（Map 边缘），
            // 从入口贯穿到落点（1~2 tick 极速，视觉是"一条光束切进船体"）。
            if (entryShipMeters.HasValue)
            {
                Vector3 entry = MapCoordMapper.ShipToMap(map,
                    new Vector3((float)entryShipMeters.Value.x, (float)entryShipMeters.Value.y, (float)entryShipMeters.Value.z));
                entry.y = Mathf.Max(entry.y, 0f); // 入口在地图下层时夹到地面（与出膛弹同规则）
                entry.x = Mathf.Clamp(entry.x, 0f, map.Size.x - 1f);
                entry.z = Mathf.Clamp(entry.z, 0f, map.Size.z - 1f);

                float dist = (entry - pos).magnitude;
                float speed = Mathf.Max(2f, dist / 2f); // 1~2 tick 贯穿

                // 贯穿生成详情（详细日志）：入口/命中局部 → Map 场景 → 落点格 全链路映射
                if (DebugDetailedCombat)
                {
                    IntVec3 c0 = map.Center;
                    Log.Message($"[贯穿生成] Entity{entity.EntityId} 入口局部=({entryShipMeters.Value.x:F1},{entryShipMeters.Value.y:F1},{entryShipMeters.Value.z:F1})m 命中局部=({hitShipMeters.x:F1},{hitShipMeters.y:F1},{hitShipMeters.z:F1})m → Map场景 入口=({entry.x:F1},{entry.y:F1},{entry.z:F1}) 落点=({pos.x:F1},{pos.y:F1},{pos.z:F1}) → 落点格=({cell.x},{cell.z}) 地图中心=({c0.x},{c0.z}) 地图尺寸={map.Size.x}×{map.Size.z}");
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
                Log.Message($"[贯穿生成] Entity{entity.EntityId} 命中局部=({hitShipMeters.x:F1},{hitShipMeters.y:F1},{hitShipMeters.z:F1})m → Map场景=({pos.x:F1},{pos.y:F1},{pos.z:F1}) → 落点格=({cell.x},{cell.z}) 地图中心=({c0.x},{c0.z}) 地图尺寸={map.Size.x}×{map.Size.z}");
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
        private void SpawnMapWarheadBlast(Map map, DVector3 hitShipMeters, float blastRadiusCells, float blastCenterDamage)
        {
            if (map == null || blastRadiusCells <= 0f || blastCenterDamage <= 0f) return;
            IntVec3 center = MapCoordMapper.ShipToMapCell(map,
                new Vector3((float)hitShipMeters.x, 0f, (float)hitShipMeters.z));
            if (!center.InBounds(map)) return;

            TerrainDef ground = map.terrainGrid.TerrainAt(center);
            if (IsSpaceFloor(ground))
            {
                Log.Message($"[弹头Map爆炸] 中心格=({center.x},{center.z}) 地板='{(ground?.defName ?? "null")}' 是太空 → 命中点无实心甲板，跳过 Map 范围爆炸（仅 3D AoE）");
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

            Log.Message($"[弹头Map爆炸] Map={map.uniqueID} 中心格=({center.x},{center.z}) 地板={(ground?.defName ?? "null")} R={blastRadiusCells:F0}格 D0={blastCenterDamage:F0} 受击建筑={buildings} Pawn={pawns} 跳过太空格={spaceCells}");
        }

        /// <summary>"太空地板"判定（奥德赛 DLC TerrainDef Space：exposesToVacuum、不可站、无实心甲板；
        /// 兼容 defName 与 tag 两种标注）。判据本体已收敛到 <see cref="SpaceTerrainUtility"/>——
        /// 地图浮雕（`SpaceMapComp`）用同一份，别在这里再写第二份。</summary>
        private static bool IsSpaceFloor(TerrainDef terrain)
        {
            return SpaceTerrainUtility.IsSpaceFloor(terrain);
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
            float[] shells = [0.25f, 0.5f, 0.75f, 1.0f]; // 环半径（相对 visR）
            int[] counts = [6, 10, 14, 18];              // 中心环少而亮，外环密而淡（读作波前）
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
                Log.Message($"[激光发射] Group {groupId} 起点=({startPos.x:F0},{startPos.y:F0},{startPos.z:F0}) 方向=({dir.x:F3},{dir.y:F3},{dir.z:F3}) 射程={beamLength:F0}m");
            }

            // 候选收集：A 正方体粗筛（当前）；B 线段走格（预留切换点，见 CollectRayCandidates）
            CollectRayCandidates(groupId, systemBodyId, startPos, endPos, beamLength, rayCandidateResults);

            // 候选收集详情（详细日志）：粗筛后候选船数 + 每艘距离（验证正方体粗筛 + 射程球过滤）
            if (DebugDetailedCombat)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[激光候选] G").Append(groupId).Append(" 射程=").Append(System.Globalization.CultureInfo.CurrentCulture, $"{beamLength:F0}")
                  .Append("m 候选船数=").Append(rayCandidateResults.Count);
                if (rayCandidateResults.Count > 0)
                {
                    sb.Append(" 列表:");
                    foreach (var cg in rayCandidateResults)
                    {
                        if (cg == null) continue;
                        double cd = (GetGroupPos(cg) - startPos).magnitude;
                        sb.Append(" G").Append(cg.GroupId).Append('@').Append(System.Globalization.CultureInfo.CurrentCulture, $"{(cd / 1000.0):F1}").Append("km");
                    }
                }
                Log.Message(sb.ToString());
            }

            // 立即射线命中检测：候选集 slab 求交 → 取最近 tEntry（遮挡语义）
            bool hit = false;
            double closestT = double.MaxValue;
            SpaceEntity? hitEntity = null;
            SpaceEntityGroup? hitGroup = null;
            int hitPartIndex = -1;
            DVector3 hitShip = DVector3.zero;     // 命中点（船体局部系，含旋转）
            DVector3 entryShip = DVector3.zero;   // 船壳入口（船体局部系，贯通视觉弹入口）

            foreach (var targetGroup in rayCandidateResults)
            {
                if (!targetGroup.IsActive || !targetGroup.IsCollidable) continue;
                if (targetGroup.GroupId == groupId) continue;

                DVector3 targetGroupPos = GetGroupPos(targetGroup);

                foreach (int entityId in targetGroup.GetEntityIds())
                {
                    var entity = spaceGame.GetEntityById(entityId);
                    if (entity == null || !entity.IsCollidable) continue;

                    DVector3 entityPosSystem = targetGroupPos + entity.Position;
                    DVector3 halfSize = entity.AabbHalfSize;

                    // 旋转正确版：世界线段 → 船体局部系（含逆旋转）→ 局部轴对齐盒 slab。
                    // 旧版"世界方向在局部系延伸"（localOrigin + dir*maxDist）在船无旋转时恰好成立，
                    // 旋转时失效；且旧版起点漏乘 maxRange，part 检测从炮口扫 0~1 米必然错位。
                    // 照 CheckSegmentGroupHit 的完整旋转处理写（第 16 条：逆四元数同走实体 per-tick 缓存）。
                    DQuaternion rotInv = entity.RotationInverse();
                    DVector3 startShip = DVector3.Rotate(startPos - entityPosSystem, rotInv);
                    DVector3 endShip = DVector3.Rotate(endPos - entityPosSystem, rotInv);

                    DVector3 min = -halfSize;
                    DVector3 max = halfSize;
                    bool slabHit = CombatGrid.RaySlabIntersection(startShip, endShip, min, max,
                        out double tEntry, out double tExit, out _);
                    // slab 详情（详细日志）：局部系线段 + 盒半长 + tEntry/tExit，逐候选逐实体打印
                    // （命中距离 = tEntry × beamLength，验证"局部系 t = 世界系 t"的旋转保持长度前提）
                    if (DebugDetailedCombat)
                    {
                        Log.Message($"[激光slab] G{groupId}→G{targetGroup.GroupId} E{entityId} 局部线段=({startShip.x:F0},{startShip.y:F0},{startShip.z:F0})→({endShip.x:F0},{endShip.y:F0},{endShip.z:F0}) 盒半长=({halfSize.x:F0},{halfSize.y:F0},{halfSize.z:F0}) {(slabHit ? "HIT" : "MISS")} tEntry={tEntry:F3} tExit={tExit:F3} 命中距离={(slabHit ? (tEntry * beamLength) / 1000.0 : 0.0):F1}km");
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

                        // Part 级精确检测（多 part：**逐 part slab 取最近 tEntry**，§5.3/批 3）。
                        // 线段基准 = 盒进入点（局部系）：part 检测从进入点扫盒内区间。
                        // 旋转保持长度，局部系 t 与世界系 t 一致 → 距离 = t × beamLength。
                        if (entity.Parts.Count > 0)
                        {
                            DVector3 dirShip = (endShip - startShip).normalized;
                            DVector3 boxEntry = startShip + dirShip * (tEntry * beamLength);
                            double maxDistShip = (tExit - tEntry) * beamLength;
                            if (TryPickHitPart(entity, boxEntry, dirShip, maxDistShip,
                                    out int pickedIndex, out var hitPart, out double partEnterT))
                            {
                                hitPartIndex = pickedIndex;
                                // 注意：partEnterT 是归一化参数（0..1 对应 part 线段），命中距离 = partEnterT × maxDistShip
                                hitShip = boxEntry + dirShip * (partEnterT * maxDistShip);
                                // 船壳入口（贯通视觉弹）：命中点沿入射方向反方向延长到 AABB 六面
                                // （RayExitAabb 反向复用，与出膛点几何对称；命中点必在盒内 → 恒出射）
                                entryShip = (DVector3)MapCoordMapper.RayExitAabb(
                                    new Vector3((float)hitShip.x, (float)hitShip.y, (float)hitShip.z),
                                    new Vector3((float)(-dirShip.x), (float)(-dirShip.y), (float)(-dirShip.z)),
                                    (Vector3)(-halfSize), (Vector3)halfSize, out _);
                                // part 详情（详细日志）：盒入口 / part 命中参数 / 命中局部点 / 船壳入口
                                if (DebugDetailedCombat)
                                {
                                    Log.Message($"[激光part] G{groupId}→G{targetGroup.GroupId} E{entityId} Part[{pickedIndex}] 盒入口=({boxEntry.x:F1},{boxEntry.y:F1},{boxEntry.z:F1})m partT={partEnterT:F3} 命中局部=({hitShip.x:F1},{hitShip.y:F1},{hitShip.z:F1})m 船壳入口=({entryShip.x:F1},{entryShip.y:F1},{entryShip.z:F1})m 入射局部方向=({dirShip.x:F3},{dirShip.y:F3},{dirShip.z:F3})");
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
                        Log.Message($"[激光命中!] Tick={lastHitTick} Group{groupId}→Group{hitGroup.GroupId} Part[{hitPartIndex}]\"{hitPart.defName}\" HP={preHP:F0}→{hitPart.hp:F0} 命中点=({hitPoint.x:F0},{hitPoint.y:F0},{hitPoint.z:F0})");
                    }
                    if (hitPart.isDestroyed)
                    {
                        hitEntity.MarkPartDestroyed(hitPart.partId);
                        if (DebugDetailedCombat)
                        {
                            Log.Message($"[Part摧毁] Entity{hitEntity.EntityId} Part[{hitPartIndex}]\"{hitPart.defName}\" 已被摧毁");
                        }
                    }

                    // 定向能贯通视觉弹：命中目标有 Map → 从船壳入口贯穿到落点（光束切进船体）
                    // 多 part：只有命中挂图舱盒才映射图内；非挂图件（引擎/储罐）不落图。
                    // B6：按命中 part 的 linkedMapId 选图（多图船不再只能落在主图）。
                    if (hitPart.linkedMapId >= 0)
                    {
                        Map targetMap = SpaceMapGenerator.FindPocketMap(hitPart.linkedMapId);
                        if (targetMap != null)
                            SpawnPenetrationMote(hitEntity, targetMap, hitShip, entryShip);
                    }
                }
                else
                {
                    if (DebugDetailedCombat)
                    {
                        Log.Message($"[激光命中!] Tick={lastHitTick} Group{groupId}→Group{hitGroup.GroupId} Entity{hitEntity.EntityId} AABB 命中点=({hitPoint.x:F0},{hitPoint.y:F0},{hitPoint.z:F0})");
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
                    Log.Message($"[激光命中判定] G{groupId}→G{hitGroup.GroupId} 命中距离={(hitPoint - startPos).magnitude / 1000.0:F1}km 候选={rayCandidateResults.Count}艘 最近tEntry={closestT:F3} 命中点=({hitPoint.x:F0},{hitPoint.y:F0},{hitPoint.z:F0})");
                }
            }
            else
            {
                // 未命中详情：候选船数（0 = 粗筛/射程过滤后无目标；>0 = 全部 slab 未中）
                if (DebugDetailedCombat)
                {
                    Log.Message($"[激光未命中] Group {groupId} 射线无碰撞（候选船数={rayCandidateResults.Count}）");
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
