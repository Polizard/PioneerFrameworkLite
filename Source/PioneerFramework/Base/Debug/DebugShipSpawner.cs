using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;   // PlanetTile（降落探针：tile 自带 layer ⇒ 跨层错配从类型上就挡得住）
using UnityEngine;
using Verse;
using PioneerFramework.Combat;
using PioneerFramework.Components;
using PioneerFramework.GroundAssembly;   // M1：RocketDesignSpec/DesignPartAssembler（多part 地图站样本）
using PioneerFramework.Launch;   // GetSurfaceState.RotationOmega（自转角速度唯一出处）

namespace PioneerFramework
{
    /// <summary>
    /// 调试场景库（2026-09-10 规范化：从「挂在调试建筑上的 ThingComp」迁到游戏自带 Debug 菜单）。
    /// 入口：dev mode 的 **Debug 菜单 →「PFL 测试」页签**（<see cref="DebugTabMenu_PFL"/>，Def `PF_DebugTab`）；
    /// （2026-09-14 更正）旧建筑 `PF_ShipDeployer` 仅保留 ThingDef、**没有 Comp 也没有 Gizmo**——
    /// 本类与它已无任何关系；此前注释声称"保留一个 Gizmo 作兼容入口"与代码/XML 均不符。
    /// 各场景由 CreateTestStation 自建口袋地图，**不依赖任何建筑**；一键验收（PerfAcceptanceRunner）
    /// 也直接调本类。转静态：原 ThingComp 的实例状态本就全是 static 字段，无状态迁移。
    /// </summary>
    public static class DebugShipSpawner
    {
        private const int EARTH_BODY_ID = 1;
        private const int MOON_BODY_ID = 2;

        /// <summary>B8 表现验收按钮生成过的组（清场只清这些，不碰玩家船）。</summary>
        private static readonly List<int> b8DemoGroupIds = new();

        /// <summary>256 件飞行/对撞、多part 交会按钮生成过的组（独立清场）。</summary>
        private static readonly List<int> stressDemoGroupIds = new();

        // ── 碰撞测试距离探针（自证物理是否推进；部署时设置，CompTick 每 2 秒打一条）──
        // 暂停时 CompTick 不跑 → 无探针日志 = 时间未推进；距离递减 = 物理正常等 [ShipCollision]。
        private static int probeGroupA = -1;
        private static int probeGroupB = -1;
        private static int probeGroupC = -1;
        private static int probeNextLogTick ;
        private static int probeLogCount ;
        private const int PROBE_LOG_INTERVAL = 120;
        private const int PROBE_LOG_MAX = 300;

        // ── 导弹拦截测试状态（SpawnMissileInterceptTest 部署；CompTick 驱动敌弹波次；销毁按钮复用）──
        private static bool interceptTestActive;
        /// <summary>敌 faction id（「销毁来袭弹」按钮按此过滤敌导弹）。</summary>
        private static int interceptEnemyFactionId = -1;
        private static int interceptPlayerGroupId = -1;
        private static int interceptEnemyGroupId = -1;
        private static int interceptNextWaveTick;
        private static int interceptWavesLeft;
        private const int interceptWaveIntervalTicks = 420; // ~7s：敌弹 3 波，与反导冷却（300tick）错开

        /// <summary>
        /// 场景自动下令（撞击/巡航/拦截用）：该发射器释放的 Following 导弹出生后立即下 Attack——
        /// 模拟"玩家在导弹出膛时下令撞击"，保持场景验证内容（20km 发射测试不设置 = 验证 Following
        /// 巡航待命不乱飞，由玩家在面板手动下令）。
        /// </summary>
        private static int autoCmdLauncherGroupId = -1;

        /// <summary>碰撞演示的详细日志自动关闭时刻（演示后 30s；0/负 = 无计划）。
        /// 目的：演示把 DebugDetailedCollision 打开后不再永久污染日志/性能（用户实测 30s 后
        /// 两船相距 6.6km 仍每 30 tick 刷 [碰撞枚举]；手动「关闭碰撞详细日志」按钮仍在）。</summary>
        private static int collisionDemoLogOffTick = -1;

        /// <summary>每 tick 驱动（原 CompTick；现由 SpaceGameComp.GameComponentTick 调用）：
        /// 场景自动下令（Following→Attack）+ 碰撞测试距离探针（暂停时不跑 ⇒ 无探针日志即时间未推进）。</summary>
        public static void Tick()
        {
            DzhanibekovDemo.Tick(); // 中间轴自旋演示：每 tick 手写 RK4 姿态（空闲零成本）

            if (collisionDemoLogOffTick >= 0 && Find.TickManager.TicksGame >= collisionDemoLogOffTick)
            {
                collisionDemoLogOffTick = -1;
                if (CombatManager.DebugDetailedCollision)
                {
                    CombatManager.DebugDetailedCollision = false;
                    Log.Message("[碰撞] 演示详细日志已自动关闭（30s 窗口结束；生产性能口径）");
                }
            }

            // ── 场景自动下令：Following 导弹 → Attack（模拟玩家在出膛时下令；幂等——下令后不再 Following）──
            if (autoCmdLauncherGroupId >= 0)
            {
                var avmNow = Current.Game?.GetComponent<AutonomousVehicleManager>();
                if (avmNow != null)
                {
                    var recs = avmNow.Vehicles;
                    for (int i = 0; i < recs.Count; i++)
                    {
                        var rec = recs[i];
                        if (rec.mission != AutonomousVehicleManager.MissionType.Following) continue;
                        if (rec.launcherGroupId != autoCmdLauncherGroupId || rec.targetGroupId < 0) continue;
                        avmNow.IssueCommand(rec.groupId, AutonomousVehicleManager.MissionType.Attack, rec.targetGroupId);
                    }
                }
            }

            // ── 导弹拦截测试：敌弹波次自动发射（不依赖敌站地图 tick，由本 Comp 在玩家地图驱动）──
            if (interceptTestActive)
            {
                int waveNow = Find.TickManager.TicksGame;
                if (waveNow >= interceptNextWaveTick)
                {
                    if (interceptWavesLeft > 0)
                    {
                        interceptWavesLeft--;
                        interceptNextWaveTick = waveNow + interceptWaveIntervalTicks;
                        FireEnemyMissileWave();
                    }
                    else
                    {
                        interceptTestActive = false;
                        Log.Message("[拦截测试] 敌弹波次发射完毕（3 波），观察反导拦截日志");
                    }
                }
            }

            if (probeGroupA < 0) return;
            if (probeLogCount >= PROBE_LOG_MAX)
            {
                probeGroupA = -1; // 探针超时，停止
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (now < probeNextLogTick) return;
            probeNextLogTick = now + PROBE_LOG_INTERVAL;
            probeLogCount++;

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            var ga = spaceGame.GetGroupById(probeGroupA);
            var gb = spaceGame.GetGroupById(probeGroupB);
            var gc = spaceGame.GetGroupById(probeGroupC);
            if (ga == null || gb == null || gc == null)
            {
                probeGroupA = -1; // 测试 group 已销毁
                return;
            }

            DVector3 pa = GetGroupPosDebug(ga);
            DVector3 pb = GetGroupPosDebug(gb);
            DVector3 pc = GetGroupPosDebug(gc);
            DVector3 relB = pb - pa;
            DVector3 relC = pc - pa;
            double dB = relB.magnitude;
            double dC = relC.magnitude;
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            Log.Message($"[碰撞探针] realTick={now} physicsTick={(tsm != null ? tsm.GetPhysicsTick() : 0.0):F1} A=({pa.x / 1000.0:F1},{pa.y / 1000.0:F1},{pa.z / 1000.0:F1})km B=({pb.x / 1000.0:F1},{pb.y / 1000.0:F1},{pb.z / 1000.0:F1})km C=({pc.x / 1000.0:F1},{pc.y / 1000.0:F1},{pc.z / 1000.0:F1})km |B-A|={dB:F1}m B-Ax={relB.x:F1}m |C-A|={dC:F1}m C-Ax={relC.x:F1}m");
        }

        /// <summary>与 CombatManager.GetGroupPos 相同的基准：圆轨解析位置 / 非圆轨 CurrentOrbit.positionSystem。</summary>
        private static DVector3 GetGroupPosDebug(SpaceEntityGroup? g)
        {
            if (g.IsCircularOrbit)
                return g.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return g.CurrentOrbit.positionSystem.v;
        }

        /// <summary>与 CombatManager.GetGroupVelocity 相同的基准：圆轨解析切向速度 / 非圆轨 CurrentOrbit.velocitySystem。</summary>
        private static DVector3 GetGroupVelDebug(SpaceEntityGroup? g)
        {
            if (g.IsCircularOrbit)
                return g.GetCircularVelocityAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return g.CurrentOrbit.velocitySystem.v;
        }

        /// <summary>
        /// 同轨靶点（2026-09-06 补）：把 pos 绕中心天体沿轨道切向旋转弧长 arcMeters，得到与母站
        /// 严格同半径同轨的点（相对静止）。此前用世界系偏移（pos + 世界轴×距离）——轨道任意相位下
        /// 偏移含径向分量 → 靶进入异轨（半径差 km 级，22:43 会话 33571 vs 33560km 实证），
        /// 靶相对母站持续漂移、导弹瞄准错乱。旋转方向 = 沿速度方向（右手定则：绕 pos×vel 正转）。
        /// </summary>
        private static DVector3 SameOrbitPointOffset(DVector3 pos, DVector3 vel, double arcMeters)
        {
            double r = pos.magnitude;
            if (r < 1e-6) return pos + vel.normalized * arcMeters; // 退化兜底（不应发生）
            DVector3 axis = DVector3.Cross(pos, vel);
            double aLen = axis.magnitude;
            if (aLen < 1e-9) return pos + vel.normalized * arcMeters;
            axis /= aLen;
            double angRad = arcMeters / r;
            var q = Quaternion.AngleAxis((float)(angRad * Mathf.Rad2Deg),
                new Vector3((float)axis.x, (float)axis.y, (float)axis.z));
            return DVector3.Rotate(pos, q);
        }



        /// <summary>创建指定高度圆轨道的测试空间站（含地图 + 单 part），返回实体/群组/地图。</summary>
        private static (SpaceEntity? entity, SpaceEntityGroup? group, Map? map) CreateTestStation(double altitudeMeters = 500000.0)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Messages.Message("SpaceGameComp 未找到！", MessageTypeDefOf.RejectInput);
                return (null, null, null);
            }

            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();

            // 默认 500km LEO；同步轨道高度约 28,560km（半径 33,560km，周期 24h）
            double earthRadiusMeters = SpaceConstants.PlanetRadiusMeters;
            double orbitRadiusMeters = earthRadiusMeters + altitudeMeters;
            // 2026-09-14：相位改由物理 tick 推导（原 DateTime.Now.Millisecond 每次运行不同 →
            // 同一场景几何不可复现，"这次没覆盖/超时"无法与上次对照）
            double angle = (TimeScaleManager.GetPhysicsTickSafe() % 60000.0) / 60000.0 * System.Math.PI * 2.0;
            double x = System.Math.Cos(angle) * orbitRadiusMeters;
            double z = System.Math.Sin(angle) * orbitRadiusMeters;
            var pos = new DVector3(x, 0, z);

            var entity = spaceGame.CreateStation(entityId, groupId, Faction.OfPlayer.loadID,
                pos, mapWidth: 60, mapHeight: 60, stationName: "测试空间站");

            // 进入圆轨道
            var group = spaceGame.GetGroupById(groupId);
            if (group != null)
                group.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            var map = SpaceMapGenerator.FindPocketMap(entity.MapId);

            Log.Message($"[StationTest] Created station: entityId={entityId}, groupId={groupId}, MapId={entity.MapId}");

            return (entity, group, map);
        }

        /// <summary>
        /// 创建无 Map 的纯物理航天器（linkedMapId=-1，单 part），用于船-船碰撞测试。
        /// 支持指定尺寸 / 姿态（验证旋转感知碰撞盒 GetAabbSystem）/ 初始速度。
        /// </summary>
        internal static SpaceEntityGroup CreateShipNoMap(SpaceGameComp spaceGame, int factionId, DVector3 pos,
            DVector3 size, Quaternion rotation, string name, DVector3? velocity = null, float dryMass = 0f)
        {
            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = EARTH_BODY_ID
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)(velocity ?? DVector3.zero),
                rotation = DQuaternion.identity
            };
            spaceGame.RegisterGroup(group);

            var entity = new SpaceEntity(entityId, factionId)
            {
                HasMap = false,        // 无 Map（纯物理）
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true
            };
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(rotation); // 姿态（验证旋转感知盒）

            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                positionShip = DVector3.zero,
                boundsSizeShip = size,
                hp = 100f,
                isIndestructible = false // 纯物理航天器可被摧毁
            };
            // 可选干质量（[补4]，2026-10-06）：**没有组件 = 质量 1 kg**（SpacePart 的兜底），
            // 而 1 kg 对几十 m² 的迎风面在 ρ₀=1.2 的大气里等于纸片 —— 撞击探针要"能撞出坠毁"就得给质量。
            if (dryMass > 0f)
                part.AddComponent(new HullMassComponent { mass = dryMass });
            entity.SetParts(new List<SpacePart> { part });

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);

            Log.Message($"[碰撞测试] 创建无Map航天器 \"{name}\" Entity {entityId} Group {groupId} size=({size.x:F0}x{size.y:F0}x{size.z:F0}) 旋转=({rotation.eulerAngles.x:F0},{rotation.eulerAngles.y:F0},{rotation.eulerAngles.z:F0}) pos=({pos.x / 1000.0:F0},{pos.y / 1000.0:F0},{pos.z / 1000.0:F0})km vel=({group.CurrentOrbit.velocitySystem.x:F0},{group.CurrentOrbit.velocitySystem.y:F0},{group.CurrentOrbit.velocitySystem.z:F0})m/s");
            return group;
        }

        // ── 多part 碰撞/语义 演示（2026-10-17：给新响应加可点的按钮，不用手动铺场）──
        private static int prealignDemoGroupId = -1;
        private static int guidanceCutDemoGroupId = -1;

        /// <summary>低速对撞（10 m/s）：看最小穿透轴 / 质量反比 / e=0.2 / 角冲量。</summary>
        public static void SpawnLowSpeedCollisionDemo() => SpawnCollisionDemo(4f);

        /// <summary>高速对撞（80 m/s）：仍走爆炸分支。</summary>
        public static void SpawnHighSpeedCollisionDemo() => SpawnCollisionDemo(80f);

        /// <summary>极端高速：只要求组级扫掠命中（撞了就算），不追 part 精确 TOI。</summary>
        public static void SpawnExtremeSpeedCollisionDemo() => SpawnCollisionDemo(10000f);

        /// <summary>
        /// 相邻格对穿（相对 10 km/s，dt=2s ⇒ 一步相对位移 20 km）：
        /// 两船起点分处相邻 20 km 格，步末仍分处相邻格（左右互换），路径中途相撞。
        /// 只按"当前覆盖格"配对的实现会整对漏掉 —— 本按钮是 EnumerateShipPairs 完备性的游戏内回归。
        /// </summary>
        public static void SpawnAdjacentCellCrossingDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            ForceNormalTime();

            // 场地隔离：高速场在 +Y 1e8、低速场在 0，本场景放 -Y 1e8。
            // x 基准 1e9 正好是 20 km 格边界：+15 km 在格 50000、+25 km 在格 50001。
            DVector3 basePos = new DVector3(1.0e9, -1.0e8, 0);
            const float speed = 5000f; // 各 5 km/s，相对 10 km/s

            var a = CreateShipNoMap(spaceGame, Faction.OfPlayer?.loadID ?? 0,
                basePos + new DVector3(15000, 0, 0), new DVector3(10, 10, 10),
                Quaternion.identity, "相邻格A", new DVector3(speed, 0, 0), dryMass: 1000f);
            var b = CreateShipNoMap(spaceGame, (Faction.OfPlayer?.loadID ?? 0) + 1,
                basePos + new DVector3(25000, 0, 0), new DVector3(6, 6, 6),
                Quaternion.identity, "相邻格B", new DVector3(-speed, 0, 0), dryMass: 1000f);

            CombatManager.DebugDetailedCollision = true;
            collisionDemoLogOffTick = Find.TickManager.TicksGame + 1800; // 30s（1×）后自动关
            Log.Message($"[碰撞演示·相邻格] A=G{a.GroupId}@格50000 B=G{b.GroupId}@格50001｜各 {speed / 1000f:F0} km/s 对开（相对 10 km/s）｜一步 20 km 对穿 ⇒ 判据：[爆炸]/[TOI诊断]");
            Messages.Message("已布置相邻格对穿（相对 10 km/s）：两船一步互换格位，应命中爆炸。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        private static void SpawnCollisionDemo(float relSpeed)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            ForceNormalTime();

            // ⚠ 关键：**零共速**（不做圆轨解析）。共轨 4.5 km/s 在一步里就把"扫掠并集盒"拉成几 km
            //   ⇒ 相邻船会假 tunneling（2026-10-17 实测：低速演示刚放出来就 tunneling 捕获）。
            //   这里两船静止在 1e9 m（重力 ~2e-4 m/s²，演示期间几乎不落），只留相对速度 ⇒ 扫掠盒只有相对位移那么大。
            // ⚠ 场地隔离（2026-10-17）：高速演示爆炸半径 ~8km，若与低速演示同场会把它炸飞
            //   （实测低场 G2 被爆风推 270m/s、连撞 4 船）⇒ 高、低两场沿 +Y 隔开 1e8 m。
            DVector3 basePos = relSpeed >= 50f
                ? new DVector3(1.0e9, 1.0e8, 0)
                : new DVector3(1.0e9, 0, 0);
            float separation = relSpeed >= 50f ? 800f : 36f;
            float massB = relSpeed >= 50f ? 1000f : 100f;

            var a = CreateShipNoMap(spaceGame, Faction.OfPlayer?.loadID ?? 0, basePos,
                new DVector3(10, 10, 10), Quaternion.identity, "对撞A", DVector3.zero, dryMass: 1000f);

            var b = CreateShipNoMap(spaceGame, (Faction.OfPlayer?.loadID ?? 0) + 1,
                basePos + new DVector3(separation, 0, 0), new DVector3(6, 6, 6), Quaternion.identity,
                "对撞B", new DVector3(-relSpeed, 0, 0), dryMass: massB);

            CombatManager.DebugDetailedCollision = true;
            collisionDemoLogOffTick = Find.TickManager.TicksGame + 1800; // 30s（1×）后自动关，防永久刷屏
            Log.Message($"[碰撞演示·{(relSpeed >= 50f ? "高速" : "低速")}] A=G{a.GroupId}(1000kg) B=G{b.GroupId}({massB}kg)｜相对速度={relSpeed:F0}m/s｜间距={separation:F0}m ⇒ 约 {separation / relSpeed:F0}s（1×）｜看 [ShipCollision] 撞击等效（e/接触点/角冲量）或 [爆炸]");
            Messages.Message($"已布置{(relSpeed >= 50f ? "高速" : "低速")}对撞（1×）：等约 {separation / relSpeed:F0} 秒，看日志 [ShipCollision]/[爆炸]。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>把时间档压回 1×（并锁 DesiredTimeScale，防止 SpaceGameComp 自动切回 Fast）。</summary>
        private static void ForceNormalTime()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame != null) spaceGame.DesiredTimeScale = TimeScaleLevel.Normal;
            Current.Game?.GetComponent<TimeScaleManager>()?.SetNormalMode();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
        }

        /// <summary>预对准消旋演示：排一条 600s 后的手动切向机动 ⇒ 船开始转向；再点「取消」看 ω 归零。</summary>
        public static void SpawnPrealignCancelDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            ForceNormalTime();

            DVector3 pos = new DVector3(1.0e9, 0, 0);
            double mu = spaceGame.GetBodyGM(EARTH_BODY_ID);
            double vc = System.Math.Sqrt(mu / pos.magnitude);
            var group = CreateShipNoMap(spaceGame, Faction.OfPlayer?.loadID ?? 0, pos,
                new DVector3(10, 10, 10), Quaternion.identity, "消旋演示船",
                new DVector3(0, vc, 0), dryMass: 1000f);
            // 慢转（0.5°/s）：否则 5°/s 下 180° 只要 36 物理秒，人还没点就转完了（2026-10-17 实测 |ω|=0）
            group.maxAngularRate = 0.5f * Mathf.Deg2Rad;
            var entity = spaceGame.GetEntityById(FirstEntityId(group));
            if (entity != null) entity.mainAxisShip = new DVector3(0, 0, -1);

            group.QueueManeuver(0f, 10f, 0f, 600, PlanOwner.Manual);
            prealignDemoGroupId = group.GroupId;
            Log.Message($"[消旋演示] G{group.GroupId} 已排 600s 后的手动切向机动（限速压到 0.5°/s）⇒ 预对准开始慢慢转向；点「预对准消旋演示·取消」看 |ω| 归零。");
            Messages.Message("消旋演示船已布置：它会以 0.5°/s 慢慢转；再点「预对准消旋演示·取消」验证停转。", MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>取消预对准：日志给出取消前/后的 |ω|（判据：后 ≈ 0）。</summary>
        public static void CancelPrealignDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var group = spaceGame != null && prealignDemoGroupId >= 0
                ? spaceGame.GetGroupById(prealignDemoGroupId) : null;
            if (group == null)
            {
                Messages.Message("消旋演示船不在了：先点「预对准消旋演示·布置」。", MessageTypeDefOf.RejectInput, false);
                return;
            }
            float before = (float)group.angularVelocity.magnitude * Mathf.Rad2Deg;
            group.ClearManeuvers();
            float after = (float)group.angularVelocity.magnitude * Mathf.Rad2Deg;
            Log.Message($"[消旋演示] G{group.GroupId} 取消前 |ω|={before:F2}°/s ⇒ 取消后 {after:F2}°/s（判据：≈0）");
            Messages.Message($"消旋演示：取消前 {before:F2}°/s ⇒ 取消后 {after:F2}°/s。", MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>交会引导切断演示：登记引导 + 2 条旧阶梯节点 + 1 条手动机动；再点「断开」看旧节点消失、手动保留。</summary>
        public static void SpawnGuidanceCutDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            ForceNormalTime();

            DVector3 pos = new DVector3(1.0e9, 0, 0);
            double mu = spaceGame.GetBodyGM(EARTH_BODY_ID);
            double vc = System.Math.Sqrt(mu / pos.magnitude);
            var group = CreateShipNoMap(spaceGame, Faction.OfPlayer?.loadID ?? 0, pos,
                new DVector3(10, 10, 10), Quaternion.identity, "引导切断演示船",
                new DVector3(0, vc, 0), dryMass: 1000f);
            // 目标必须真实存在：否则引导更新会因"target gone"自己清掉（演示还没点就没了）
            var target = CreateShipNoMap(spaceGame, Faction.OfPlayer?.loadID ?? 0,
                pos + new DVector3(1000, 0, 0), new DVector3(6, 6, 6), Quaternion.identity,
                "引导切断演示目标", new DVector3(0, vc, 0), dryMass: 100f);

            group.RegisterRendezvousGuidance(target.GroupId, TimeScaleManager.GetPhysicsTickSafe() + 1000.0, true, null);
            int execTick = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe()) + 600;
            group.plannedManeuvers.Add(new SpaceEntityGroup.PlannedManeuver
            { active = true, executeTick = execTick, owner = PlanOwner.Guidance, ladderEpoch = 0 });
            group.plannedManeuvers.Add(new SpaceEntityGroup.PlannedManeuver
            { active = true, executeTick = execTick + 100, owner = PlanOwner.Guidance, ladderEpoch = 0 });
            group.plannedManeuvers.Add(new SpaceEntityGroup.PlannedManeuver
            { active = true, executeTick = execTick + 200, owner = PlanOwner.Manual, ladderEpoch = 0 });
            guidanceCutDemoGroupId = group.GroupId;
            Log.Message($"[引导切断演示] G{group.GroupId} 已登记引导 + 3 条节点（2 引导 + 1 手动）；点「交会引导切断演示·断开」看旧节点消失、手动保留。");
            Messages.Message("引导切断演示船已布置：再点「交会引导切断演示·断开」验证节点撤回。", MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>断开引导：日志给出断前/后的队列条数（判据：3 → 1，剩下的必须是 Manual）。</summary>
        public static void CutGuidanceDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var group = spaceGame != null && guidanceCutDemoGroupId >= 0
                ? spaceGame.GetGroupById(guidanceCutDemoGroupId) : null;
            if (group == null)
            {
                Messages.Message("引导切断演示船不在了：先点「交会引导切断演示·布置」。", MessageTypeDefOf.RejectInput, false);
                return;
            }
            int before = group.plannedManeuvers.Count;
            group.ClearRendezvousGuidance();
            int after = group.plannedManeuvers.Count;
            bool onlyManual = after == 1 && group.plannedManeuvers[0].owner == PlanOwner.Manual;
            Log.Message($"[引导切断演示] G{group.GroupId} 断开前 {before} 条 ⇒ 断开后 {after} 条（判据：3→1 且剩余为手动={onlyManual}）");
            Messages.Message($"引导切断演示：{before} 条 ⇒ {after} 条（剩余手动={onlyManual}）。", MessageTypeDefOf.TaskCompletion, false);
        }

        private static int FirstEntityId(SpaceEntityGroup group)
        {
            foreach (int eid in group.GetEntityIds()) return eid;
            return -1;
        }

        /// <summary>
        /// 无 Map 航天器碰撞测试（验证 #9 旋转感知碰撞盒）：
        /// ① 基准船 A（无 Map，不旋转）置于同步轨道圆轨；
        /// ② 船 B（40×30×40，绕 Z 旋转 45°）在 A 旁 200m，给朝向 A 的接近速度 → 斜向靠近；
        /// ③ 船 C（20×20×20，绕 Y 旋转 30°）从另一方向接近。
        /// 部署后挂上「距离探针」：CompTick 每 2 秒打印一次 B/C 相对 A 的距离，
        /// 用于自证物理是否在推进（距离不变=时间未推进；递减=物理正常，等 [ShipCollision]）。
        /// </summary>
        public static void SpawnCollisionTest()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            // 碰撞检测挂在 GameComponentTick 上，暂停时永不执行 → 部署时强制解暂停回 1x
            bool wasPaused = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            if (wasPaused)
                Log.Message("[碰撞测试] 检测到暂停，已自动解除（TimeSpeed.Normal）");

            int playerFaction = Faction.OfPlayer.loadID;

            // A：基准无 Map 船（玩家），同步轨道圆轨，无旋转
            DVector3 basePos = new DVector3(33560000.0, 0, 0);
            var groupA = CreateShipNoMap(spaceGame, playerFaction, basePos,
                new DVector3(60, 50, 60), Quaternion.identity, "基准船A");
            groupA.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // B：敌 faction，旋转 45°，A 旁 200m，30m/s 朝 A 靠近（相对 A 的轨道速度）
            DVector3 velB = groupA.CurrentOrbit.velocitySystem.v - new DVector3(30, 0, 0);
            var groupB = CreateShipNoMap(spaceGame, playerFaction + 1, basePos + new DVector3(200, 0, 0),
                new DVector3(40, 30, 40), Quaternion.Euler(0f, 0f, 45f), "斜向船B(旋转45°)", velB);

            // C：敌 faction，旋转 30°，另一方向 25m/s 靠近
            DVector3 velC = groupA.CurrentOrbit.velocitySystem.v + new DVector3(25, 0, -25);
            var groupC = CreateShipNoMap(spaceGame, playerFaction + 1, basePos + new DVector3(-200, 0, 150),
                new DVector3(20, 20, 20), Quaternion.Euler(0f, 30f, 0f), "侧向船C(旋转30°)", velC);

            // 挂上距离探针（自证物理是否推进）
            probeGroupA = groupA.GroupId;
            probeGroupB = groupB.GroupId;
            probeGroupC = groupC.GroupId;
            probeNextLogTick = Find.TickManager.TicksGame + 60;
            probeLogCount = 0;

            // 开启详细碰撞日志（扫掠盒重叠过程），验证 tunneling 修复
            CombatManager.DebugDetailedCollision = true;
            Log.Message("[碰撞测试] 已开启 CombatManager.DebugDetailedCollision（扫掠盒重叠日志）");

            // 打印部署时刻状态（真实 tick / 物理 tick / 时间档位）
            var tsm = Current.Game?.GetComponent<TimeScaleManager>();
            Log.Message($"[碰撞测试] 场景就绪: realTick={Find.TickManager.TicksGame} physicsTick={(tsm != null ? tsm.GetPhysicsTick() : 0.0):F1} 时间档位={(tsm != null ? tsm.CurrentLevel.ToString() : "N/A")}；B/C 将相对 A 靠近，等待 [ShipCollision]");
        }

        /// <summary>
        /// 主推轴向 + 弱 RCS 燃烧测试场景（D15 改动验证，2026-09-06）：
        /// 创建 1 枚无 Map 主推方向型测试弹（body 局部主推轴 -Z + 主网液氢 + RCS 副网肼），
        /// 初始姿态 identity（轴 -Z），向世界 +X 点火 StartBurn(dv=500)。
        /// 预期 [Thrust] 日志序列（开「详细推进日志」）：
        ///   ① 对准中 + 用网=RCS：夹角 90°→收敛（魔动量轮 ω，RCS 小推力修正、主罐不动）；
        ///   ② 夹角收心至 ≤~1.5°（axis·burn≈0.9997）→ 已对准 + 用网=主：主网沿 +X 正中直烧、RCS 保留；
        ///   ③ [Burn] burn completed（主料烧到余 dv≈0 结算）。
        /// </summary>
        public static void SpawnAxialThrustTest()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Messages.Message("SpaceGameComp 未找到！", MessageTypeDefOf.RejectInput);
                return;
            }

            // 碰撞/物理依赖时间推进 → 解除暂停回 1x
            bool wasPaused = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            if (wasPaused)
                Log.Message("[AxialThrustTest] 检测到暂停，已自动解除（TimeSpeed.Normal）");

            SpaceGameComp.DebugDetailedThrust = true; // 自动开 [Thrust] 详细日志

            int factionId = Faction.OfPlayer.loadID;
            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();

            // 出生：近地圆轨 500km（半径 6.4Mm → 半径+500km），姿态 identity
            double radius = SpaceConstants.PlanetRadiusMeters + 500000.0;
            var pos = new DVector3(radius, 0, 0);

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsActive = true,
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = EARTH_BODY_ID
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = SystemVec3.zero,
                rotation = DQuaternion.identity
            };
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
            entity.SetRotation(Quaternion.identity);

            // body Part（长条，沿主推轴 -Z；AABB = 2×2×8m）
            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_TestMissile",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(2, 2, 8),
                hp = 200f,
                isIndestructible = false
            };
            part.AddComponent(new HullMassComponent { mass = 300f }); // 干质量 300kg
            entity.SetParts(new List<SpacePart> { part });

            // D15 主推方向型：主推轴 body 局部 -Z
            entity.mainAxisShip = new DVector3(0, 0, -1);

            // 主网（液氢，大推力）+ RCS 副网（肼，全矢量小推力；与主网异工质）
            var hydrogen = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrogen");
            var hydrazine = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrazine");
            entity.AddPropulsion(new PropulsionData
            {
                propellant = hydrogen, ve = 2000f, massFlow = 6f, mass = 2000f, capacity = 2000f
            });
            entity.AddPropulsion(new PropulsionData
            {
                propellant = hydrazine, ve = 900f, massFlow = 2f, mass = 300f, capacity = 300f, isVectoredRcs = true
            });

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);

            // 稳定圆轨（速度由 EnterCircularOrbit 解析生成）
            group.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 点火：世界 +X，dv=500 m/s（初始轴 -Z → 夹角 90°，先转向+RCS 修正，再主网直烧）
            group.StartBurn(new DVector3(500, 0, 0));

            Log.Message($"[AxialThrustTest] 就绪: G{groupId} E{entityId} 主推轴局部=(0,0,-1) 姿态identity→burnDir=+X(夹角90°) dv=500m/s 主网=H2(ṁ6kg/s F12kN) RCS=肼(ṁ2kg/s F1.8kN) 双罐=H2 2000/肼 300kg；开 [Thrust] 日志观察对准+RCS→主网切换");

        }

        /// <summary>
        /// 跳帮（登舰突击）测试场景（M3 验收）：
        /// ① 玩家空间站（圆轨，母船）内置突击兵；
        /// ② 敌对空间站（共轨 10km 外）内置守军；
        /// ③ 自动触发单兵 EVA 跳帮：母船 pawn DeSpawn → 临时 3D 实体 + 接近引导（瞄准移动落点）
        ///    → 降落判定通过 → pawn Spawn 进目标站 Map。
        /// 实现委托 BoardingTestScenarios（与测试面板共用同一场景逻辑）。
        /// </summary>
        public static void SpawnBoardingTest()
        {
            BoardingTestScenarios.DeployEvaTest(1);
        }

        public static void SpawnTestStation()
        {
            var (entity, group, map) = CreateTestStation();
            if (entity == null || group == null)
                return;

            // 打印双向映射验证（`linkedPartId` 已于 2026-09-26 删除：它只写不读，唯一读者就是这行日志）
            if (map != null)
            {
                var mapComp = map.GetComponent<SpaceMapComp>();
                Log.Message($"[StationTest] SpaceMapComp: ownerEntityId={(mapComp?.ownerEntityId ?? -1)}");
            }
            foreach (var p in entity.Parts)
                Log.Message($"[StationTest] Part {p.partId} linkedMapId={p.linkedMapId}");

            Messages.Message($"空间站已创建 (Group {group.GroupId})，Map {entity.MapId}。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// **漏靶弹头**场景（A2，《审查修复计划2026-09-14》§9 第 2 项）：让弹头活满寿命。
        ///
        /// 为什么需要它：实战弹头在命中点出生、通常**一个物理步内就结算**，于是 A2 的
        /// "2 物理秒一段 + 段内插值飞行"只走到"建段"，走不到"段内插值"，也验不到
        /// "漏靶弹头被引力弯成轨道垃圾"这条设计意图。
        ///
        /// 做法：在同步轨道半径处放一枚**圆轨**弹头（切向速度 = √(μ/r)），四周无任何目标
        /// —— 它既不会撞地球（33,560 km 之外）、也不会撞到任何船，活满
        /// `warheadLifetimeSeconds`（120 s）后自然销毁。
        ///
        /// 判据（探针）：㉔ 自由飞行弯曲 实际偏移/½|a|t² 应 ≈1×（0.6–1.7）；
        /// ㉒ 段重建节奏按"飞行秒/段长"判定；两者都由探针直接记 Mismatch，进硬指标。
        /// </summary>
        public static void SpawnMissedWarheadTest()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var combatMgr = Current.Game?.GetComponent<CombatManager>();
            if (spaceGame == null || celestialMgr == null || combatMgr == null)
            {
                Log.Error("[漏靶弹头测试] 前置组件缺失");
                return;
            }

            var earth = celestialMgr.GetBodyById(EARTH_BODY_ID);
            if (earth == null)
            {
                Log.Error("[漏靶弹头测试] 地球 Def 缺失");
                return;
            }

            // 同步轨道半径（与空间站场景同高，便于对照 ㉒ 的 |a|=μ/r²）
            double r = SpaceConstants.PlanetRadiusMeters + 28_560_000.0;
            double vCirc = System.Math.Sqrt(earth.gravitationalParameter / r);

            var pos = new DVector3(r, 0.0, 0.0);
            var vel = new DVector3(0.0, 0.0, vCirc); // 切向（⊥ 半径）→ 圆轨

            bool ok = combatMgr.SpawnDebugMissedWarhead("PF_Missile_Standard", EARTH_BODY_ID, pos, vel);
            Log.Message($"[漏靶弹头测试] 弹头已生成={ok}：r={r / 1000.0:F0}km 圆轨速度={vCirc:F0}m/s｜预期 ㉒|a|≈{earth.gravitationalParameter / (r * r):F3} m/s²（=μ/r²）、㉔弯曲比≈1×、寿命 {120.0:F0}s");
        }

        /// <summary>
        /// ★ **撞击事件探针**（2026-09-22）：一次跑出**两种结果**，验证"着陆 / 坠毁"判定与落点经纬度。
        ///
        /// 摆法：两颗船都放在**地表上方 200 m**、**纯径向下降**（无切向 ⇒ 接地速度只由"初速 + 重力"决定；
        /// 地表 g = μ/R² = 8 m/s² ⇒ 自由落体 200 m 只加 √(2gh) ≈ 57 m/s），只是初速不同：
        /// · **着陆**：初速 20 m/s ⇒ 撞击 ≈ √(20² + 2·8·200) ≈ **60 m/s ≤ 100**；
        /// · **坠毁**：初速 300 m/s ⇒ 撞击 ≈ **305 m/s > 100**。
        /// 两颗都**不带碰撞豁免**（豁免会让事件根本不触发），也**不挂着陆控制器** —— 本探针只验事件本身。
        ///
        /// 落点对照：着陆那颗在**赤道 0°/0°**，坠毁那颗在**北纬 30° / 东经 120°**
        /// ⇒ 用来证明经纬度**不是恒等于 (0,0)**（纬度那一路真的被算进去了）。
        ///
        /// 判据（消息 + `[Collision]` 日志）：应出现一条"着陆于地球（北纬 0.0° 东经 0.0°）"与
        /// 一条"坠毁于地球（北纬 30.0° 东经 120.0°）"，接地速度分别 ≈60 与 ≈305。
        /// </summary>
        public static void SpawnImpactEventTest()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || celestialMgr == null) return;

            var body = celestialMgr.GetBodyById(EARTH_BODY_ID);
            if (body == null)
            {
                Log.Error("[撞击事件] 天体 Def 缺失（bodyId=1）");
                return;
            }

            LastImpactProbeGroups.Clear();
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            double omega = GetSurfaceState.RotationOmega(celestialMgr, EARTH_BODY_ID);

            SpawnImpactProbeShip(spaceGame, body.radius, omega, physicsTick,
                0.0, 0.0, 20.0);                                // 赤道 0°/0° ⇒ 预期"着陆"
            SpawnImpactProbeShip(spaceGame, body.radius, omega, physicsTick,
                30.0 * System.Math.PI / 180.0, 120.0 * System.Math.PI / 180.0, 300.0);  // 北纬 30°/东经 120° ⇒ 预期"坠毁"
        }

        /// <summary>
        /// 最近一次 <see cref="SpawnImpactEventTest"/> 摆出的两颗探针组 id（**顺序 = 着陆颗、坠毁颗**）。
        /// 给 `LandingAcceptanceRunner` 跟踪它们的结局用（判据不许靠"看日志里有没有消息"当唯一证据）。
        /// </summary>
        internal static readonly System.Collections.Generic.List<int> LastImpactProbeGroups =
            new();

        /// <summary>
        /// 撞击事件探针的单颗摆法（<see cref="SpawnImpactEventTest"/>）：地表上方 200 m、相对地表的纯径向下降。
        ///
        /// **[补4]（2026-10-06，用户裁定"探针判据只是临时的、可以改"）——探针重裁**：
        /// 原探针 = 无组件（⇒ 质量 1 kg）+ 6×6×12 外廓（⇒ Cd·A ≈ 36–72 m²）。ρ₀=1.2 的大气一开，
        /// 它在下落中会被瞬间刹停（终端速度 ~0.4 m/s）⇒ "300 m/s ⇒ 坠毁"这条**物理上不再可能**
        /// （不是判据松了，是那枚探针在新世界里成了一片纸）。重裁为 **20 t + 3×3×6**（Cd·A ≈ 9–18 m²）：
        ///   · 坠毁探针：终端速度 ≈ 121–171 m/s &gt; 100 阈值；从 300 m/s 掉 200 m 只损失 ~20–30 m/s
        ///     ⇒ 撞地仍有 ~270 m/s ⇒ **坠毁**（对姿态不敏感：面积 9↔18 都落在阈值的正确一侧）；
        ///   · 着陆探针：20 m/s 起落 200 m ⇒ 撞地 ≈ 60 m/s（纯重力上限 60.3）⇒ **着陆**
        ///     （即使零阻力也在阈值之下 ⇒ 对气动数据不敏感）。
        /// 判据形状（着陆/坠毁各一次 + 经纬度不同）**未动**。
        /// </summary>
        private static void SpawnImpactProbeShip(SpaceGameComp spaceGame, double radius, double omega,
            double physicsTick, double latRad, double lonRad0, double descentMps)
        {
            const double altitude = 200.0;
            (DVector3 surfPos, DVector3 surfVel) = SurfaceFrame.SurfaceState(radius, latRad, lonRad0, omega, physicsTick);
            DVector3 up = surfPos.normalized;
            DVector3 pos = surfPos + up * altitude;
            // **相对地表**的纯径向下降：ECI 速度 = 共转速度 − 下降率×径向
            DVector3 vel = surfVel - up * descentMps;

            var g = CreateShipNoMap(spaceGame, Faction.OfPlayer.loadID, pos,
                new DVector3(3f, 3f, 6f), Quaternion.identity, "撞击事件", vel, dryMass: 20_000f);
            if (g == null) return;
            g.CelestialBodyId = EARTH_BODY_ID;      // ⚠ 不设 exemptPlanetCollision：要让撞击事件真的触发
            LastImpactProbeGroups.Add(g.GroupId);

            Log.Message($"[撞击事件] G{g.GroupId} 已创建：高度 {altitude:F0} m、相对地表纯径向下降 {descentMps:F0} m/s、落点预期 ({latRad * 180.0 / System.Math.PI:F1}°, {lonRad0 * 180.0 / System.Math.PI:F1}°)｜无碰撞豁免 ⇒ 撞到地表时应出现『着陆 / 坠毁』消息");
        }

        /// <summary>
        /// 月球系引力回归场景（A0 §9 第 3 项）：在月球附近放一个**非圆轨**群组。
        ///
        /// 为什么必须非圆轨：圆轨群组走 `GetCircularPositionAtTick` 解析推进，**不经过引力场积分**，
        /// 验不到 A0 的改动；非圆轨才走 `ComputeGroupPhysics` 的场积分路径。
        ///
        /// 起手数据：月球表面上方 1800km、速度 = 0.92×月球圆轨速度（切向）→ 稳定椭圆
        /// （a≈0.866r，e≈0.154，近月点 ≈2.4e6 m > 月半径 1.5e6 m，不会立刻撞月），
        /// 半径在 2.4–3.3e6 m 间变化，全程都在积分。
        ///
        /// 判据（配合 PerfProbe 的汇总行 ㉑）：子天体域「差分/局部引力」应落在 **0.1%–2%**（地球潮汐）；
        /// 若为 0 → 月球周围仍未带上根天体的贡献（A0 未生效）；若 ≈7% → 走了"全量求和"的错路。
        /// </summary>
        // ═══ 选 tile 主动降落（2026-09-22）：报价探针 + 执行探针（地月各一）═══

        /// <summary>
        /// ★ **选 tile 降落的报价探针**：近地圆轨 + 绕月圆轨各摆一颗（**顺行赤道** ⇒ 报价取该天体的最小值档），
        /// 打印它们**星下点 tile** 的报价 + 一个"远处对照"落点（用来同时看到**距离门**与"Δv 随落点变"）。
        ///
        /// 预期量级（口径见 <see cref="LandingQuote"/>）：
        /// · 近地圆轨（500 km）落赤道 ≈ **5.5 km/s**（= 圆轨 6030 − 地表共转 523.6）；
        /// · 绕月圆轨（1800 km）落月球赤道 ≈ **1.17 km/s**（= 1231 − 58.9）；
        /// · 远处对照落点应当被距离门拒（门：地球 1000 km / 月球 525 km）。
        /// </summary>
        public static void SpawnLandingQuoteProbe()
        {
            if (!TrySpawnLandingProbeShips(out var sg, out var mgr, out var tick, out var eShip, out var mShip)) return;
            if (sg == null || mgr == null || eShip == null || mShip == null) return;   // 可空标注所需；上面已保证非空
            ProbeLandingQuotes(sg, mgr, eShip, EARTH_BODY_ID, tick, "近地圆轨", LastEarthProbeTile);
            ProbeLandingQuotes(sg, mgr, mShip, MOON_BODY_ID, tick, "绕月圆轨", LastMoonProbeTile);
        }

        /// <summary>
        /// ★ **选 tile 降落的执行探针**：对上面两颗船的**星下点 tile** 真的执行一次
        /// <see cref="SpaceGameComp.TryLandAtTile"/> ⇒ 验证三件事：**消息 + 扣料 + 销毁**
        /// （执行前后各打一行剩余 Δv，扣料看这一对数）。
        /// </summary>
        public static void RunLandingExecutionProbe()
        {
            if (!TrySpawnLandingProbeShips(out var sg, out var mgr, out var tick, out var eShip, out var mShip)) return;
            if (sg == null || mgr == null || eShip == null || mShip == null) return;   // 同上
            RunOneLanding(sg, mgr, eShip, EARTH_BODY_ID, tick, "近地圆轨", LastEarthProbeTile);
            RunOneLanding(sg, mgr, mShip, MOON_BODY_ID, tick, "绕月圆轨", LastMoonProbeTile);
        }

        /// <summary>两颗探针船：近地圆轨（500 km）+ 绕月圆轨（1800 km），都是**顺行赤道**（按 `SurfaceFrame` 的东向约定）。
        /// 返回 false（并保证 sg == null）＝ 场景没搭起来 ⇒ 调用方直接返回。
        /// `internal`（2026-09-23）：`LandingAcceptanceRunner` 的判据要**复用**这份摆场（两处各写一份 = 两套权威）。</summary>
        internal static bool TrySpawnLandingProbeShips(out SpaceGameComp? sg, out CelestialBodyManager? mgr,
            out double physicsTick, out SpaceEntityGroup? eShip, out SpaceEntityGroup? mShip)
        {
            sg = Current.Game?.GetComponent<SpaceGameComp>();
            mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            physicsTick = 0.0;
            eShip = null;
            mShip = null;
            if (sg == null || mgr == null) return false;

            var earth = mgr.GetBodyById(EARTH_BODY_ID);
            var moon = mgr.GetBodyById(MOON_BODY_ID);
            if (earth == null || moon == null)
            {
                Log.Error("[降落探针] 天体 Def 缺失（need bodyId 1/2）");
                return false;
            }
            physicsTick = TimeScaleManager.GetPhysicsTickSafe();

            // ── 两颗探针都锚在**已知 tile 的正上方**（2026-09-24 改）────────────────────────
            // 为什么不再"随便找个位置 + 事后用 `EciToTile` 反查星下点"：反查走的是 layer 网格**顶点**，
            // 而判据要问的是"这艘船正对着哪个 tile" ⇒ 由构造保证最省事，也不必先相信反查。
            // 纬度改用 **RimWorld 自己的 `LongLatOf`**（`Asin(c.y / layer.Radius)`）挑赤道附近的 tile ——
            // 那是游戏自己的纬度定义，与"网格是否以原点为心"无关（后者是另一条判据在查的事）。
            double rE = earth.radius + 500_000.0;
            int eTile = PickEquatorialTile(GetSurfaceState.GetLayerForBody(mgr, EARTH_BODY_ID));
            DVector3 eUp = GetSurfaceState.TileToEci(eTile, mgr, EARTH_BODY_ID, physicsTick).normalized;
            DVector3 eEast = SurfaceFrame.SurfaceVelocity(eUp * earth.radius,
                GetSurfaceState.RotationOmega(mgr, EARTH_BODY_ID)).normalized;
            eShip = CreateShipNoMap(sg, Faction.OfPlayer.loadID, eUp * rE,
                new DVector3(6f, 6f, 12f), Quaternion.identity, "降落探针·近地",
                eEast * System.Math.Sqrt(earth.gravitationalParameter / rE));
            if (eShip != null) eShip.CelestialBodyId = EARTH_BODY_ID;

            // ── 绕月圆轨：月球某赤道 tile 正上方 1800 km，速度 = 月球绕地平动 + 月球相对圆轨速度（东向）──
            double omegaM = GetSurfaceState.RotationOmega(mgr, MOON_BODY_ID);
            DVector3 moonPos = mgr.GetBodyPositionParent(MOON_BODY_ID, physicsTick);   // 月球的父系（= 系统系）偏移
            DVector3 moonVel = mgr.GetBodyVelocityParent(MOON_BODY_ID, physicsTick);
            double rM = moon.radius + 1_800_000.0;
            int mTile = PickEquatorialTile(GetSurfaceState.GetLayerForBody(mgr, MOON_BODY_ID));
            DVector3 mUp = GetSurfaceState.TileToEci(mTile, mgr, MOON_BODY_ID, physicsTick).normalized;
            DVector3 mEast = SurfaceFrame.SurfaceVelocity(mUp * moon.radius, omegaM).normalized;
            mShip = CreateShipNoMap(sg, Faction.OfPlayer.loadID, moonPos + mUp * rM,
                new DVector3(6f, 6f, 12f), Quaternion.identity, "降落探针·绕月",
                moonVel + mEast * System.Math.Sqrt(moon.gravitationalParameter / rM));
            if (mShip != null) mShip.CelestialBodyId = MOON_BODY_ID;

            LastEarthProbeTile = eTile;
            LastMoonProbeTile = mTile;

            // ⚠ **无 Map 船必须显式加油**：`CreateShipNoMap` 只造壳（一个 `PF_ShipPart_Cube`），
            //   推进与工质全靠 `AddPropulsion` ⇒ 不加的话报价会被"工质不足"拒掉，探针就成了废的。
            GiveLandingProbePropulsion(sg, eShip);
            GiveLandingProbePropulsion(sg, mShip);

            return eShip != null && mShip != null;
        }

        /// <summary>
        /// 挑一个**赤道附近**的 tile（判据要"船正对哪个 tile"这个事实由构造给出）。纬度用 RimWorld 自己的
        /// `LongLatOf`（`Asin(c.y / layer.Radius)`）×57.3 转度 ⇒ 与"网格以原点为心"那个假设无关。
        /// 无 layer 或挑不出来 ⇒ 返回 0（调用方按 tile 0 处理；赤道性只是为了让 Δv 落在登记区间，不是正确性前提）。
        /// </summary>
        internal static int PickEquatorialTile(PlanetLayer? layer)
        {
            if (layer == null || layer.TilesCount <= 0) return 0;
            int n = layer.TilesCount;
            for (int i = 0; i < n; i++)
            {
                float lat = layer.LongLatOf(i).y;   // 度
                if (lat > -5f && lat < 5f) return i;
            }
            return 0;
        }

        /// <summary>最近一次 <see cref="TrySpawnLandingProbeShips"/> 两颗探针**正对着的 tile**（判据/探针共用）。</summary>
        internal static int LastEarthProbeTile ;
        internal static int LastMoonProbeTile ;

        /// <summary>
        /// 探针船的推进档（照抄既有的"过推档"）：主网 ve 4000 / ṁ140 / 66000 kg＋RCS 网 ve 1500 / ṁ20 / 4000 kg。
        /// ve_eff = ΣF/Σṁ = (4000·140 + 1500·20)/160 = **3687.5 m/s**；干重 ≈ 船体几百 kg
        /// ⇒ Δv ≈ 3687.5·ln(wet/dry) ≈ **10 km/s 量级**，够付近地圆轨那笔 5.5 km/s，也远超绕月那笔 1.17 km/s。
        /// </summary>
        private static void GiveLandingProbePropulsion(SpaceGameComp sg, SpaceEntityGroup? group)
        {
            if (group == null) return;
            var hydrogen = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrogen");
            var hydrazine = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrazine");
            if (hydrogen == null || hydrazine == null)
            {
                Log.Error("[降落探针] 工质 Def 缺失（PF_Propellant_Hydrogen / PF_Propellant_Hydrazine）");
                return;
            }
            foreach (int eid in group.GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e == null) continue;
                e.AddPropulsion(new PropulsionData
                {
                    propellant = hydrogen, ve = 4000f, massFlow = 140f, mass = 66000f, capacity = 66000f
                });
                e.AddPropulsion(new PropulsionData
                {
                    propellant = hydrazine, ve = 1500f, massFlow = 20f, mass = 4000f, capacity = 4000f, isVectoredRcs = true
                });
            }
        }

        /// <summary>打印某颗船在目标天体上的报价：**船正对的 tile**（由摆场给出）+ 一个"远处对照"落点。</summary>
        private static void ProbeLandingQuotes(SpaceGameComp sg, CelestialBodyManager mgr,
            SpaceEntityGroup g, int bodyId, double physicsTick, string tag, int shipTile)
        {
            var layer = GetSurfaceState.GetLayerForBody(mgr, bodyId);
            if (layer == null || layer.TilesCount <= 0)
            {
                Log.Error($"[降落探针] {tag}：天体 {bodyId} 没有地表网格");
                return;
            }
            int tile = shipTile;
            Log.Message($"[降落探针] {tag} G{g.GroupId}：船正对的 tile={tile}（layer 共 {layer.TilesCount} 格；摆场时由构造给出）");

            PrintLandingQuote(sg, g, bodyId, new PlanetTile(tile, layer), tag + "·正对落点");
            int far = (tile + System.Math.Max(1, layer.TilesCount / 3)) % layer.TilesCount;
            PrintLandingQuote(sg, g, bodyId, new PlanetTile(far, layer), tag + "·远处对照");
        }

        /// <summary>报价读数（一行）；被拒时打印拒绝原因（含"距离/上界"两个数）。</summary>
        private static void PrintLandingQuote(SpaceGameComp sg, SpaceEntityGroup g, int bodyId, PlanetTile tile, string tag)
        {
            if (sg.TryQuoteLanding(g, bodyId, tile, out var q, out var est, out var bodyName, out var spot, out var reason))
                Log.Message($"[降落探针] {tag} {bodyName} tile={tile.tileId}（{spot}）⇒ **可落** Δv={q.deltaV:F1} m/s（工质 {est.RequiredPropellant:F1} kg）｜地表 {q.surfaceDistance / 1000.0:F0} km / 门 {q.maxDistance / 1000.0:F0} km");
            else
                Log.Message($"[降落探针] {tag} tile={tile.tileId} ⇒ **拒绝**：{reason}");
        }

        /// <summary>对星下点 tile 真的执行一次降落（消息 + 扣料 + 销毁三件事的探针）。</summary>
        private static void RunOneLanding(SpaceGameComp sg, CelestialBodyManager mgr,
            SpaceEntityGroup g, int bodyId, double physicsTick, string tag, int shipTile)
        {
            var layer = GetSurfaceState.GetLayerForBody(mgr, bodyId);
            if (layer == null) { Log.Error($"[降落探针] {tag}：没有地表网格"); return; }
            int tile = shipTile;   // 船**正对**的 tile（摆场时由构造给出；不再用 `EciToTile` 反查）
            var before = sg.GetGroupDeltaV(g);
            bool ok = sg.TryLandAtTile(g, bodyId, new PlanetTile(tile, layer), out var reason);
            Log.Message($"[降落探针] {tag} G{g.GroupId} 执行 tile={tile} ⇒ {(ok ? "**成功**" : "**被拒**")}｜{reason}｜执行前剩余 Δv={before.remainingDv:F0} m/s，执行后编队{(sg.GetGroupById(g.GroupId) == null ? "已销毁 ✓" : "**仍在（应当已销毁）**")}");
        }

        public static void SpawnMoonGravityTest()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || celestialMgr == null) return;

            var moon = celestialMgr.GetBodyById(MOON_BODY_ID);
            if (moon == null)
            {
                Log.Error("[月球引力测试] 天体 Def 缺失（bodyId=2）");
                return;
            }

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            // 月球相对**其父天体**（= 根行星地球）的位置/速度 → 与系统系（CurrentOrbit 所在系）同系
            DVector3 moonPos = celestialMgr.GetBodyPositionParent(MOON_BODY_ID, physicsTick);
            DVector3 moonVel = celestialMgr.GetBodyVelocityParent(MOON_BODY_ID, physicsTick);

            double r = moon.radius + 1_800_000.0;
            double vCirc = System.Math.Sqrt(moon.gravitationalParameter / r);
            var radial = new DVector3(1.0, 0.0, 0.15).normalized;
            var tangent = new DVector3(radial.z, 0.0, -radial.x); // XZ 平面内正交

            DVector3 pos = moonPos + radial * r;
            DVector3 vel = moonVel + tangent * (vCirc * 0.92);

            var group = CreateShipNoMap(spaceGame, Faction.OfPlayer.loadID, pos,
                new DVector3(6f, 6f, 12f), Quaternion.identity, "月球引力测试", vel);
            if (group == null) return;

            // 中心天体 = 月球（引力场按系统系解析；坐标仍在系统系 → 无需改 CurrentOrbit）
            group.CelestialBodyId = MOON_BODY_ID;

            Log.Message($"[月球引力测试] G{group.GroupId} 已创建：r={r / 1000.0:F0}km 圆轨速度={vCirc:F0}m/s 实给={vCirc * 0.92:F0}m/s（椭圆 e≈0.154）｜开「性能优化验收探针」后看汇总行 ㉑ 的子天体域差分比值");
        }


        // ── 无 Map 编队放置（预设圆轨） ──

        /// <summary>无 Map 编队的预设圆轨（<see cref="PlaceNoMapOrbitGroup"/>）。</summary>
        public enum NoMapOrbitPreset
        {
            /// <summary>近地轨道：地表上方 500km，赤道面**顺行**圆轨（与发射入轨同向）。</summary>
            Leo,
            /// <summary>同步轨道：周期 = 天体自转周期（dayLengthTicks）⇒ 与地表相对静止。</summary>
            GeoSynchronous,
            /// <summary>绕月轨道：月面上方 1800km 圆轨（与 <see cref="SpawnMoonGravityTest"/> 同口径）。</summary>
            MoonOrbit,
        }

        /// <summary>落点相位（赤道面内的单位径向）：由 physicsTick 推得，可复现（不用 DateTime.Now）。</summary>
        private static DVector3 EquatorialRadial(double physicsTick)
        {
            double angle = (physicsTick % 60000.0) / 60000.0 * System.Math.PI * 2.0;
            return new DVector3(System.Math.Cos(angle), 0, System.Math.Sin(angle));
        }

        /// <summary>
        /// 赤道面**顺行**单位切向 = cross(radial, up)，与 `GetSurfaceState` 的地表自转同向
        /// （那里冻结了 v = ω·(p×up)，并注明"与 prograde up×radial 反向"）。
        /// ⚠ 方向必须显式给：零速度会让 EnterCircularOrbit 退化到 cross(up, radial) 的解 —— 那是**逆行**，
        /// 与地面发射入轨的船相对角速度翻倍，同步轨道也"静止"不了。
        /// </summary>
        private static DVector3 ProgradeEquatorialTangent(DVector3 radial)
            => DVector3.Cross(radial, DVector3.up);

        /// <summary>
        /// 放置一个**无 Map**编队到预设圆轨（调试用）。
        ///
        /// 船：单 part 长条盒 40×20×80m（长轴 = 主推轴 -Z，视图里朝向一眼可读）+ 结构干质量 5t
        ///     + 主网（液氢 ve4000 ṁ4）12t + RCS（肼 ve1500 ṁ1，全矢量）0.5t，满罐
        ///     ⇒ **有 dV 可机动**（口径 = `SpaceGameComp.GetGroupDeltaV`，与世界视图面板「剩余 dV」同一算式）。
        ///
        /// 圆轨：一律走 `group.EnterCircularOrbit`（解析位置 + 自动轨道保持 + 按中心天体 GM 复核速度）；
        ///     同步轨道半径按 **μ/ω² 开立方**现算（ω 取 `GetSurfaceState.RotationOmega`，不用"24h 假设"的常数）。
        /// 控制：放置后在世界视图选中该编队 →「机动规划 / 转移到圆轨 / 交会（选目标）」即可消耗 dV。
        /// </summary>
        public static void PlaceNoMapOrbitGroup(NoMapOrbitPreset preset)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null)
            {
                Messages.Message("SpaceGameComp / CelestialBodyManager 未找到！", MessageTypeDefOf.RejectInput);
                return;
            }

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            int bodyId;
            double radius;
            DVector3 pos, vel;
            string presetLabel;
            string dirLabel;

            switch (preset)
            {
                case NoMapOrbitPreset.GeoSynchronous:
                {
                    bodyId = EARTH_BODY_ID;
                    double mu = spaceGame.GetBodyGM(bodyId);
                    double omega = GetSurfaceState.RotationOmega(mgr, bodyId);       // 2π / dayLengthTicks
                    radius = System.Math.Pow(mu / (omega * omega), 1.0 / 3.0);       // 同步半径 (μ/ω²)^(1/3)
                    DVector3 radial = EquatorialRadial(physicsTick);
                    pos = radial * radius;
                    vel = ProgradeEquatorialTangent(radial) * System.Math.Sqrt(mu / radius);
                    presetLabel = $"同步轨道（周期 {2.0 * System.Math.PI / omega:F0}s = 天体自转周期 ⇒ 与地表相对静止）";
                    dirLabel = "顺行（与地表自转同向 = 地面发射入轨方向）";
                    break;
                }
                case NoMapOrbitPreset.MoonOrbit:
                {
                    bodyId = MOON_BODY_ID;
                    var moon = mgr.GetBodyById(MOON_BODY_ID);
                    if (moon == null)
                    {
                        Log.Error("[放置] 月球天体 Def 缺失（bodyId=2）");
                        return;
                    }
                    radius = moon.radius + 1_800_000.0;                              // 同 SpawnMoonGravityTest
                    // 系统系 = 根行星（地球）为原点 ⇒ 月心相对状态要加月球的位置/速度
                    DVector3 moonPos = mgr.GetBodyPositionParent(MOON_BODY_ID, physicsTick);
                    DVector3 moonVel = mgr.GetBodyVelocityParent(MOON_BODY_ID, physicsTick);
                    // 初始径向 = 背地侧（月心 → 远离地球），落在月球公转面内；切向取该面内的顺行方向
                    DVector3 radial = moonPos.normalized;
                    DVector3 planeNormal = DVector3.Cross(moonPos, moonVel).normalized;
                    if (planeNormal.sqrMagnitude < 0.01) planeNormal = DVector3.up;
                    DVector3 tangent = DVector3.Cross(planeNormal, radial).normalized;
                    if (tangent.sqrMagnitude < 0.01) tangent = ProgradeEquatorialTangent(radial);
                    pos = moonPos + radial * radius;
                    vel = moonVel + tangent * System.Math.Sqrt(spaceGame.GetBodyGM(bodyId) / radius);
                    presetLabel = $"绕月轨道（月面上方 {1_800_000.0 / 1000.0:F0}km，背地侧起点）";
                    dirLabel = "顺行（与月球公转同向；月球公转相对地球自转是反向，故不要拿它跟地球轨道比方向）";
                    break;
                }
                default: // Leo
                {
                    bodyId = EARTH_BODY_ID;
                    radius = SpaceConstants.PlanetRadiusMeters + 500_000.0;          // 同 SpawnAxialThrustTest
                    DVector3 radial = EquatorialRadial(physicsTick);
                    pos = radial * radius;
                    vel = ProgradeEquatorialTangent(radial) * System.Math.Sqrt(spaceGame.GetBodyGM(bodyId) / radius);
                    presetLabel = $"近地轨道（地表上方 {500_000.0 / 1000.0:F0}km）";
                    dirLabel = "顺行（与地表自转同向 = 地面发射入轨方向）";
                    break;
                }
            }

            var group = CreateNoMapShipGroup(spaceGame, bodyId, pos, vel, out int entityId);
            if (group == null) return;
            int groupId = group.GroupId;

            var centralDef = mgr.GetBodyById(bodyId);
            var (remainingDv, maxDv, propMass, propCap) = spaceGame.GetGroupDeltaV(group);
            double period = 2.0 * System.Math.PI * System.Math.Sqrt(radius * radius * radius / spaceGame.GetBodyGM(bodyId));
            Log.Message($"[放置] G{groupId} E{entityId} 无Map编队 → {presetLabel}｜中心={(centralDef != null ? centralDef.label : "?")}(ID={bodyId}) r={radius / 1000.0:F0}km（高度 {(radius - (centralDef != null ? centralDef.radius : 0.0)) / 1000.0:F0}km）圆轨周期={period:F0}s v={(group.IsCircularOrbit ? group.GetCircularVelocityAtTick(physicsTick).magnitude : vel.magnitude):F0}m/s dV={remainingDv:F0}/{maxDv:F0} m/s 工质={propMass:F0}/{propCap:F0}kg｜{dirLabel}｜轨道保持=开（维持圆轨会持续耗料，设计如此）");

            Messages.Message($"已放置无Map编队 G{groupId}：{presetLabel}｜dV {remainingDv:F0} m/s（世界视图选中它 →「机动规划/转移到圆轨」消耗 dV）", MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>放置：近地轨道圆轨（地表 500km，顺行）。</summary>
        public static void PlaceLeoNoMapGroup() => PlaceNoMapOrbitGroup(NoMapOrbitPreset.Leo);

        /// <summary>放置：同步轨道圆轨（周期 = 天体自转周期 ⇒ 与地表相对静止）。</summary>
        public static void PlaceGeoSyncNoMapGroup() => PlaceNoMapOrbitGroup(NoMapOrbitPreset.GeoSynchronous);

        /// <summary>
        /// **放置：GTO 椭圆轨道目标**（近地点 LEO 500km、远地点 = 同步半径）——**用户原始工况的载体**。
        ///
        /// 为什么补这个（2026-09-17 用户质问"你怎么一直弃置异轨情况"）：放置预设此前**只有圆轨**
        /// ⇒ **游戏里根本没法摆一个椭圆轨道目标** ⇒ 用户最初的 LEO→GTO 交会在游戏内**无法复现**，
        /// 而我一直在测同轨/圆-圆异轨。台架侧已补 `EllipticTarget`（T16：LEO→GTO 收敛 0.95 个目标周期、
        /// Δv 2182、从未侵地 ✓），游戏侧补这个预设后两边才对齐。
        /// </summary>
        public static void PlaceGtoNoMapGroup()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Messages.Message("SpaceGameComp 未找到！", MessageTypeDefOf.RejectInput);
                return;
            }
            int bodyId = EARTH_BODY_ID;
            double mu = spaceGame.GetBodyGM(bodyId);
            double rp = SpaceConstants.PlanetRadiusMeters + 500_000.0;      // 近地点：LEO 500km
            double ra = 26_322_000.0;                                       // 远地点：同步半径
            double a = 0.5 * (rp + ra);
            double vp = System.Math.Sqrt(mu * (2.0 / rp - 1.0 / a));        // 近地点速度（vis-viva）
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 radial = EquatorialRadial(physicsTick);
            DVector3 pos = radial * rp;
            DVector3 vel = ProgradeEquatorialTangent(radial) * vp;

            var group = CreateNoMapShipGroup(spaceGame, bodyId, pos, vel, out int entityId,
                overThrust: true, circularOrbit: false);
            if (group == null) return;
            double e = (ra - rp) / (ra + rp);
            double period = 2.0 * System.Math.PI * System.Math.Sqrt(a * a * a / mu);
            Log.Message($"[放置] G{group.GroupId} E{entityId} 无Map编队 → **GTO 椭圆轨道**｜rp={rp / 1000.0:F0}km ra={ra / 1000.0:F0}km a={a / 1000.0:F0}km e={e:F3} 周期={period:F0}s vp={vp:F0}m/s｜dV={spaceGame.GetGroupDeltaV(group).remainingDv:F0} m/s｜**非圆轨**（走引力积分，不进圆轨解析模式）");
            Messages.Message($"已放置 GTO 椭圆目标 G{group.GroupId}（近地点 {rp / 1000.0:F0}km / 远地点 {ra / 1000.0:F0}km，e={e:F2}）。再放一个 LEO 编队去「交会（选目标）」它 —— 这就是 LEO→GTO 的原始工况。", MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>放置：绕月轨道圆轨（月面 1800km）。</summary>
        public static void PlaceMoonOrbitNoMapGroup() => PlaceNoMapOrbitGroup(NoMapOrbitPreset.MoonOrbit);

        /// <summary>
        /// **绕月交会场景**（2026-09-29，用户报障后加："我们也许需要一个月球交会测试场景"）。
        ///
        /// 两艘**绕月圆轨**（月面 1800km，与「45 放置」那条同一套几何），目标在追击方**顺行前方**
        /// <paramref name="phaseDegAheadDeg"/> 度（**负值 = 后方**），并**自动登记交会引导**（登记即生效）。
        ///
        /// ## ⏱ 选参数 = 选"要等多久"（同轨起步的硬约束，2026-09-29 定案）
        /// 同轨（Δa=0）时霍曼**没有窗口** ⇒ 阶梯必落到调相级，而调相只能**抬轨**
        /// （`PlanPhasingOrbit` 的 `useLow` 要求 `a &lt; aTgt`）⇒ 只能等目标从后面绕上来。
        /// ⇒ **要补的相位 = 360°−φ（φ&gt;0）或 |φ|（φ&lt;0）**，而调相率约 **0.0043 °/s**
        /// （判据 = `axisRatio=1.16`、船周期 16,845 s）：
        ///   · **φ = −10°**（目标在后方）⇒ 补 10° ⇒ **≈2,300 s** ⇒ 整条链 1 分钟内可见 ⇒ **诊断用默认档**
        ///   · **φ = +30°**（目标在前方）⇒ 补 330° ⇒ **≈77,000 s** ⇒ 需 ≥5 分钟真实时间（硬例，留作回归）
        /// ⇒ **要快就看 −10°；+30° 是"最坏几何"那一档，慢是几何决定的、不是缺陷。**
        ///
        /// ## 为什么必须有这条场景
        /// "根行星 / 体心系 / 解析圆轨模式 / 天体偏移"这套机器**只在环绕卫星时才被执行**
        /// （地球/太阳场景里那些项恒为 0）⇒ 这条路径此前**零覆盖**（纯台架是二体的，够不到）。
        /// 用户实测过一次：规划与第一发点火都对（3300→2844km = 46.5 m/s，实测 46.58、对账 0.00%），
        /// 之后**无任何点火**却撞月 ⇒ 问题在"非点火"的状态路径上。
        ///
        /// ## 判据
        /// 见 `MoonRendezvousAcceptanceRunner`（三条：两船都活着 / 游戏自己的到达判据 / 阶梯在位）、
        /// 以及本库新增的探针 `SpaceGameComp.LogNonRootBurnState`（点火前/收工后各一行：圆轨模式 + 两套状态 + 要素）。
        /// </summary>
        public static void SpawnMoonRendezvousTest(double phaseDegAheadDeg = 30.0)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;
            var moon = mgr.GetBodyById(MOON_BODY_ID);
            if (moon == null)
            {
                Log.Error("[绕月交会] 月球天体 Def 缺失（bodyId=2）");
                return;
            }

            SpaceGameComp.UseNewRendezvousLaw = true;   // 调试场景的职责就是验收阶梯（与既有一族同）

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            double mu = spaceGame.GetBodyGM(MOON_BODY_ID);
            double radius = moon.radius + 1_800_000.0;
            double vCirc = System.Math.Sqrt(mu / radius);
            DVector3 moonPos = mgr.GetBodyPositionParent(MOON_BODY_ID, physicsTick);
            DVector3 moonVel = mgr.GetBodyVelocityParent(MOON_BODY_ID, physicsTick);

            // 与 MoonOrbit 预设同一套约定：径向取背地侧、切向取月球公转面内的顺行方向
            DVector3 radial0 = moonPos.normalized;
            DVector3 planeNormal = DVector3.Cross(moonPos, moonVel).normalized;
            if (planeNormal.sqrMagnitude < 0.01) planeNormal = DVector3.up;
            DVector3 tangent0 = DVector3.Cross(planeNormal, radial0).normalized;
            if (tangent0.sqrMagnitude < 0.01) tangent0 = ProgradeEquatorialTangent(radial0);

            // ⚠ **批5.C 修正（2026-09-29）——参数必须等于真实相位差。**
            //   旧写法把追击方放在 −φ、目标放在 +φ ⇒ **实际夹角是 2φ**（实测：传 30 ⇒ 真差 60
            //   ⇒ 阶梯要"绕过来 300°"⇒ 等 3.4 圈；而日志照着参数报 30°）⇒ **读数与几何差一倍**，
            //   正是本库反复吃过的"读数定名错"。现在：追击方在 radial0、目标在 +φ（顺行方向）。
            //   φ>0 = 目标在**前方**（阶梯只能抬轨让目标绕过来 ⇒ 需补 360°−φ）；φ<0 = 目标在**后方**
            //   （只需补 |φ| ⇒ **快档：−10° 只要 ~2300 s，是 30° 那档的 1/30**）。
            double phi = phaseDegAheadDeg * System.Math.PI / 180.0;
            DVector3 radialChaser = radial0;
            DVector3 radialTgt = (radial0 * System.Math.Cos(phi) + tangent0 * System.Math.Sin(phi)).normalized;
            DVector3 tgtTangent = DVector3.Cross(planeNormal, radialTgt).normalized;
            DVector3 chaserTangent = DVector3.Cross(planeNormal, radialChaser).normalized;
            // **实测**相位差（独立于参数、按构造基给出；+ = 目标在前，与 `RendezvousGoal.SignedPhaseDeg` 同号约定）
            // ⇒ 参数与几何若对不上（旧版差一倍那种），这一行当场露馅。
            double sepDeg = System.Math.Atan2(
                DVector3.Dot(planeNormal, DVector3.Cross(radialChaser, radialTgt)),
                DVector3.Dot(radialChaser, radialTgt)) * 180.0 / System.Math.PI;

            // ⚠ 系统系 = 根行星（地球）为原点 ⇒ 传入的状态要**加回月球的位置/速度**（与 MoonOrbit 预设同）
            var target = CreateNoMapShipGroup(spaceGame, MOON_BODY_ID,
                moonPos + radialTgt * radius, moonVel + tgtTangent * vCirc, out _);
            if (target == null) return;
            var chaser = CreateNoMapShipGroup(spaceGame, MOON_BODY_ID,
                moonPos + radialChaser * radius, moonVel + chaserTangent * vCirc, out _);
            if (chaser == null) return;

            chaser.RegisterRendezvousGuidance(target.GroupId, physicsTick, prograde: true, useNetworks: null);
            chaser.ArmRendezvousGuidance();     // 无出发段 ⇒ 登记即生效

            // **自动判定**（用户 2026-09-29："我希望自动判定，因为我是人类，跑得可能不及时"）：
            //   截止给 4 个**月球绕地周期** ⇒ 从月球自己的 Def 取（`orbitPeriod`，**唯一权威**）。
            //   ⚠ 首版我错用了"绕月**轨道**周期"（=2π√(r³/μ月)≈16,845s）⇒ 4 个只有 0.21 个真周期
            //     ⇒ 自动判定**提前叫停**并报"未达"（假红旗：那会儿阶梯还在远场走调相/霍曼、
            //     双方都在、Collision=0）。⇒ 期限必须按"月球绕地"的尺度给。
            //   ⚠ 二改（2026-09-29）：**进度行与结论行的周期口径改回"船的绕月周期"** —— 期限用
            //     月球绕地周期是对的（那是"给多久"），但拿它当分母报进度会**把读数缩小 19 倍**
            //     （实测"0.12 个月球周期"看着像刚开始，其实船已经绕了 2.19 圈）⇒ **判据与读数分开口径**。
            double moonOrbitPeriod = moon.orbitPeriod > 0.0 ? moon.orbitPeriod : 317900.0;
            double period = 2.0 * System.Math.PI * System.Math.Sqrt(radius * radius * radius / mu);
            // 墙钟闸显式写回 600 s：它是**静态字段**，近场档会把它改成 180 s ⇒ 两档之间必须各自声明，
            //   否则"先点慢档再点快档"会互相串（本库登记过"跨档串状态"这一族）。
            MoonRendezvousAcceptanceRunner.WallDeadlineSeconds = 600f;
            MoonRendezvousAcceptanceRunner.Arm(chaser.GroupId, target.GroupId, physicsTick,
                physicsTick + 4.0 * moonOrbitPeriod, period);

            double dvBudget = spaceGame.GetGroupDeltaV(chaser).remainingDv;
            // 阶梯在同轨（Δa=0）时拿不到霍曼窗口 ⇒ 必落到调相级，而调相的**方向只能是"抬轨"**
            // （`PlanPhasingOrbit` 的 `useLow` 要求 `a < aTgt`，同轨时为假）⇒ 只能等目标从后面绕上来。
            // ⇒ **要补的相位 = 360°−φ（φ>0 前方）或 |φ|（φ<0 后方）** —— 这条几何事实决定要等多久。
            double phaseToClose = phaseDegAheadDeg >= 0.0 ? 360.0 - phaseDegAheadDeg : -phaseDegAheadDeg;
            Log.Message($"[绕月交会] G{chaser.GroupId}(追击) → G{target.GroupId}(目标)｜中心=月球(ID={MOON_BODY_ID}) r={radius / 1000.0:F0}km（月面 {1_800_000.0 / 1000.0:F0}km）周期={period:F0}s｜**实测相位差 {sepDeg:F0}°**（参数给 {phaseDegAheadDeg:F0}°；+ 目标在前）｜μ={mu:E2}｜追击预算 {dvBudget:F0} m/s｜引导=阶梯（登记即生效）｜**同轨起步 ⇒ 只能抬轨等目标绕过来：要补 {phaseToClose:F0}° 相位**· 期望：匹配平面 → 变轨/调相 → 画拦截 → 收尾降档 → 到达判据收口；**不许任何一艘撞月**");
            Messages.Message($"绕月交会：G{chaser.GroupId} → G{target.GroupId}（相位差 {sepDeg:F0}°，已自动接合）",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>
        /// **绕月交会 · 异轨档**（2026-09-29 三改）—— 追击方与目标在**不同半径**的圆轨道上，
        /// 相位**拿阶梯自己的 `MakePlan` 当 oracle 扫出来**（取"直接霍曼且计划完成最早"的那个相位）。
        ///
        /// ## 为什么是异轨（用户 2026-09-29）
        /// "**近距离跑霍曼是非常弱智的行为**" + "**我需要的是异轨交会测试，你他妈怎么这么喜欢同轨**"。
        /// 同轨（Δa=0）时霍曼**无解** ⇒ 阶梯必落调相级 ⇒ 只能抬轨等目标从后面绕过来 ⇒ 最多等约一个
        /// 会合周期（≈8.5e4 s ≈ 4.5 分钟真实）—— 那是我先前两个夹具**都做成同轨**才有的病，
        /// **不是交会本身的病**。异轨是另一回事：Δa≠0 ⇒ 窗口存在，而且能扫到"**等 0.00 圈**"。
        ///
        /// 离线读数（台架 `G6_MoonDiffOrbit...`，与本函数同一套构造）：
        ///   · **升轨**（追击 2900km → 目标 3300km）：φ=+16.25° ⇒ 直接霍曼，**计划用时 7,698 s**，Δv 82.1 m/s
        ///   · **降轨**（追击 3700km → 目标 3300km）：φ=−16.75° ⇒ 直接霍曼，**计划用时 9,241 s**，Δv 68.4 m/s
        ///   · 同轨：**扫遍 0..360° 一个都扫不到** ⇒ 那是病理档（用 `SpawnMoonRendezvousTest` 留作对照）
        ///
        /// ## 构造（**唯一权威 = 阶梯自己**）
        /// 扫目标相位 φ ∈ 0..360°，每个 φ 构造两条 `OrbitView` 调 `RendezvousLadder.MakePlan`，
        /// 只认 `Rung.Hohmann`，取"末节点时刻最早"者。**不写夹角公式** —— 我推过两次夹角都是错的
        /// （把"补相位"误解成"把相位差收成 0"，见交接 §⑭）⇒ 用被测物当 oracle，夹具就不会自己错。
        /// ⇒ 本档**不含调相段**，考的是"远场 → 霍曼 → 终端"这条链（用户原报障的几何正在这里）。
        /// ⚠ 判据与读数仍全部走 `MoonRendezvousAcceptanceRunner`（同三条判据、同一套进度行）。
        /// </summary>
        /// <param name="chaserRadiusKm">追击方轨道半径（km）。**2900 = 升轨**（= 用户原报障"压轨到 2844"那一族）、
        /// **3700 = 降轨**。目标固定 3300km（月面 1800km 档，与其余月球场景同）。</param>
        public static void SpawnMoonRendezvousDiffOrbitTest(double chaserRadiusKm = 2900.0)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;
            var moon = mgr.GetBodyById(MOON_BODY_ID);
            if (moon == null)
            {
                Log.Error("[绕月交会·异轨] 月球天体 Def 缺失（bodyId=2）");
                return;
            }

            SpaceGameComp.UseNewRendezvousLaw = true;

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            double mu = spaceGame.GetBodyGM(MOON_BODY_ID);
            double rTgt = moon.radius + 1_800_000.0;
            double rChaser = chaserRadiusKm * 1000.0;
            double vTgtCirc = System.Math.Sqrt(mu / rTgt);
            double vChaserCirc = System.Math.Sqrt(mu / rChaser);
            DVector3 moonPos = mgr.GetBodyPositionParent(MOON_BODY_ID, physicsTick);
            DVector3 moonVel = mgr.GetBodyVelocityParent(MOON_BODY_ID, physicsTick);

            DVector3 radial0 = moonPos.normalized;
            DVector3 planeNormal = DVector3.Cross(moonPos, moonVel).normalized;
            if (planeNormal.sqrMagnitude < 0.01) planeNormal = DVector3.up;
            DVector3 tangent0 = DVector3.Cross(planeNormal, radial0).normalized;
            if (tangent0.sqrMagnitude < 0.01) tangent0 = ProgradeEquatorialTangent(radial0);

            // ── 扫相位：**oracle = 阶梯自己的 `MakePlan`**（粗扫 5° + 精扫 0.5°）──
            //    只认"直接霍曼"；目标函数 = 计划末节点的时刻（= 计划完成用时，now 归零）。
            var ladderParams = new RendezvousLadder.Params
            {
                DesiredDistance = 283.0,
                MaxClosingSpeed = 100.0,
                MaxPhasingOrbits = 5.0,
                // ⚠ `LadderPilot` 那套默认参数是**地球尺度**（MinRadius=5.1e6）⇒ 月球档必须显式给
                MinRadius = moon.radius + 100_000.0,
                // 调试扫相位要看"计划到达偏差"（独立 Shepperd 复算）⇒ 保留诊断
                IncludeDiagnostics = true,
            };
            double bestPhiDeg = 0.0, bestPlanSpan = double.PositiveInfinity, bestDvSum = 0.0;
            string bestStatus = "（扫遍 0..360° 没有直接霍曼 ⇒ 本几何是病理档）";
            int probes = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                double center = pass == 0 ? 0.0 : bestPhiDeg;
                double half = pass == 0 ? 180.0 : 5.0;
                double step = pass == 0 ? 5.0 : 0.5;
                for (double deg = center - half; deg <= center + half; deg += step)
                {
                    probes++;
                    DVector3 rC = radial0 * rChaser;
                    DVector3 vC = tangent0 * vChaserCirc;
                    double ph = deg * System.Math.PI / 180.0;
                    DVector3 radialT = (radial0 * System.Math.Cos(ph) + tangent0 * System.Math.Sin(ph)).normalized;
                    DVector3 rT = radialT * rTgt;
                    DVector3 vT = DVector3.Cross(planeNormal, radialT).normalized * vTgtCirc;
                    var ovC = OrbitView.FromStateVectors(rC, vC, mu, physicsTick, planeNormal, moon.radius);
                    var ovT = OrbitView.FromStateVectors(rT, vT, mu, physicsTick, planeNormal, moon.radius);
                    var rung = RendezvousLadder.MakePlan(ovC, ovT, physicsTick, ladderParams,
                        out List<PlanNode> nodes, out string status);
                    if (rung != RendezvousLadder.Rung.Hohmann || nodes.Count == 0) continue;
                    double span = nodes[nodes.Count - 1].ut - physicsTick;
                    if (!(span > 0.0) || double.IsNaN(span)) continue;
                    if (span < bestPlanSpan)
                    {
                        bestPlanSpan = span; bestPhiDeg = deg; bestStatus = status;
                        bestDvSum = 0.0;
                        foreach (var n in nodes) bestDvSum += n.dV.magnitude;
                    }
                }
            }

            double phiBest = bestPhiDeg * System.Math.PI / 180.0;
            DVector3 radialTgt = (radial0 * System.Math.Cos(phiBest) + tangent0 * System.Math.Sin(phiBest)).normalized;
            DVector3 tgtTangent = DVector3.Cross(planeNormal, radialTgt).normalized;

            var target = CreateNoMapShipGroup(spaceGame, MOON_BODY_ID,
                moonPos + radialTgt * rTgt, moonVel + tgtTangent * vTgtCirc, out _);
            if (target == null) return;
            var chaser = CreateNoMapShipGroup(spaceGame, MOON_BODY_ID,
                moonPos + radial0 * rChaser, moonVel + tangent0 * vChaserCirc, out _);
            if (chaser == null) return;

            chaser.RegisterRendezvousGuidance(target.GroupId, physicsTick, prograde: true, useNetworks: null);
            chaser.ArmRendezvousGuidance();

            // 期限：本档预期 ~1.5e4 s ⇒ 墙钟给 180 s（实测 300:1 ⇒ 54,000 s）留 3.5× 余量，
            //   **失败也能在 3 分钟内出结论**（同轨慢档那边显式写回 600 s —— 静态字段，两边都要写，别串档）。
            MoonRendezvousAcceptanceRunner.WallDeadlineSeconds = 180f;
            double moonOrbitPeriod = moon.orbitPeriod > 0.0 ? moon.orbitPeriod : 317900.0;
            double chaserPeriod = 2.0 * System.Math.PI * System.Math.Sqrt(rChaser * rChaser * rChaser / mu);
            MoonRendezvousAcceptanceRunner.Arm(chaser.GroupId, target.GroupId, physicsTick,
                physicsTick + 4.0 * moonOrbitPeriod, chaserPeriod);

            double sepDeg = System.Math.Atan2(
                DVector3.Dot(planeNormal, DVector3.Cross(radial0, radialTgt)),
                DVector3.Dot(radial0, radialTgt)) * 180.0 / System.Math.PI;
            Log.Message($"[绕月交会·异轨] G{chaser.GroupId}(追击 r={rChaser / 1000.0:F0}km) → G{target.GroupId}(目标 r={rTgt / 1000.0:F0}km)｜**实测相位差 {sepDeg:F2}°**（+ 目标在前；{probes} 次 `MakePlan` 试探扫出）｜μ={mu:E2}｜**阶梯第一次规划 = 直接霍曼，计划用时 {bestPlanSpan:F0}s**（Δv {bestDvSum:F1} m/s）｜{bestStatus}｜**本档不含调相段** ⇒ 考「远场 → 霍曼 → 终端」· 墙钟闸 {MoonRendezvousAcceptanceRunner.WallDeadlineSeconds:F0}s");
            Messages.Message($"绕月交会·异轨：G{chaser.GroupId} → G{target.GroupId}（直接霍曼，计划 {bestPlanSpan:F0}s ⇒ 约 1 分钟出结论）",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>
        /// **无 Map 船编队的工厂**（放置类场景共用）：注册群组 + 单 part 长条件 + 主网/ RCS 两副推进，
        /// 并调用 <see cref="SpaceEntityGroup.EnterCircularOrbit"/> 让位置由解析圆轨给出。
        /// 失败（工质 Def 缺失）返回 null。
        /// </summary>
        // 公开：供 P0 适配层的一键验收铺"受控轨道"的场景组（吃显式 pos/vel）。
        public static SpaceEntityGroup? CreateNoMapShipGroup(SpaceGameComp spaceGame, int bodyId,
            DVector3 pos, DVector3 vel, out int entityId, bool overThrust = true, bool circularOrbit = true)
        {
            entityId = -1;
            var hydrogen = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrogen");
            var hydrazine = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrazine");
            if (hydrogen == null || hydrazine == null)
            {
                Log.Error("[放置] 工质 Def 缺失（PF_Propellant_Hydrogen / PF_Propellant_Hydrazine）→ 放置的船不会有 dV");
                return null;
            }

            int factionId = Faction.OfPlayer?.loadID ?? 0;
            int groupId = spaceGame.AllocGroupId();
            entityId = spaceGame.AllocEntityId();

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = bodyId
            };
            group.CurrentOrbit = new OrbitState { positionSystem = (SystemVec3)pos, velocitySystem = (SystemVec3)vel, rotation = DQuaternion.identity };
            spaceGame.RegisterGroup(group);

            var entity = new SpaceEntity(entityId, factionId)
            {
                HasMap = false,          // 无 Map（纯物理）
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true
            };
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.mainAxisShip = new DVector3(0, 0, -1);   // D15：无 Map 船的主推轴来源

            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(40.0, 20.0, 80.0),   // 长轴 = Z = 主推轴向
                hp = 1000f,
                isIndestructible = false
            };
            part.AddComponent(new HullMassComponent { mass = 5000f });   // 结构干质量（碰撞动量交换也用它）
            // [V7] 船载传感器阵列（1000 km；抗性取站的一半 ⇒ 面板上"站 > 船"看得出来）
            part.AddComponent(new SensorArrayComponentData
            {
                passiveRangeMeters = 1_000_000f, ecmResist = 0.5f, eccmResist = 0.5f
            });
            entity.SetParts(new List<SpacePart> { part });

            // ── 推进（两个档）────────────────────────────────────────────────
            // 默认档（overThrust=true）：**推力给足 + 油量给到 dV≈10000 m/s**。
            //   · 推力：机动规划器是脉冲模型（一次点火等效瞬时机动脉冲，燃烧居中只补偿"中点对齐"）。
            //     ṁ 太小 ⇒ 一次 LEO→同步轨道注入要烧 28 分钟、横跨 104° 轨道，有限燃烧把轨道打偏，
            //     闭环引导只能用单脉冲 Lambert 追 → 近地点处"逆行修正"被判撞行星 → 死锁
            //     （2026-09-16 游戏内实测：99 次 skipped 后 dv 涨到 2733.7 m/s 而终止）。
            //   · 油量：**2026-09-17 扩到 dV≈10000**（原 4385）。理由：LEO→同步轨道的交会账单实测
            //     ≈4911 m/s（螺旋价 3274×1.5 + 相位 + 终端，纯台架 T11），4385 会被预算闸门**正确拒掉**
            //     ⇒ 那个工况在游戏内根本跑不起来。给足油才能验收。
            //     ⚠ Δv 口径 = ve_eff·ln(湿/干)，ve_eff = ΣF/Σṁ = (560k+30k)/(140+20) = 3687.5
            //     ⇒ 3687.5·ln(75000/5000) ≈ 9986 m/s。
            //   · **推力档不影响交会动力学**：新律的限权是 min(aMax, 0.05·g) = 0.05·g（LEO 0.33 m/s²），
            //     只要 aMax > 0.05g 就与船无关 ⇒ 实测应与纯台架 T11（同一限权）**同量级可比**。
            // 台架档（overThrust=false）：**纯台架 RendezvousShip 的原参数**（8kN 主/ṁ2、1.5kN RCS/ṁ1、
            //   工质 12000+500、干重 5000）——要跟台架读数逐位对照时必须用这一档（同一 plant 才有同一读数）。
            //   ⚠ 它的 dV 只有 ~3967 m/s，**故意不放大**（放大就不再是台架的 plant）；LEO→GEO 那种大账单
            //   在台架侧由 T11 覆盖，游戏内用默认档。
            entity.AddPropulsion(new PropulsionData
            {
                propellant = hydrogen,
                ve = 4000f,
                massFlow = overThrust ? 140f : 2f,
                mass = overThrust ? 66000f : 12000f,
                capacity = overThrust ? 66000f : 12000f
            });
            entity.AddPropulsion(new PropulsionData
            {
                propellant = hydrazine,
                ve = 1500f,
                massFlow = overThrust ? 20f : 1f,
                mass = overThrust ? 4000f : 500f,
                capacity = overThrust ? 4000f : 500f,
                isVectoredRcs = true
            });

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);

            // 圆轨（解析位置 + **自动轨道保持**；中心天体 = 预设天体 → 顺带置位 group.CelestialBodyId）
            // ⚠ 轨道保持**故意留着**（2026-09-16 用户澄清）：维持圆轨本来就该持续烧工质抵消非白名单
            // 天体的潮汐扰动（ComputeOrbitKeepingCorrection 的 ①②差分），并按质量流率分摊到各网络
            // （⇒ 20% 从 RCS 罐扣）。数量级：LEO ≈6 m/s/天、同步 ≈31、绕月 ≈155。**这是设计意图，别关**。
            // 圆轨走解析推进；**椭圆轨道不能进圆轨模式**（那会把它当圆轨解析推进 ⇒ 轨道被抹掉），
            // 只开轨道保持（抵消非中心天体引力，仍是设计意图）。
            if (circularOrbit) group.EnterCircularOrbit(Find.TickManager.TicksGame, bodyId);
            else group.EnterOrbitKeeping(new HashSet<int> { bodyId });
            return group;
        }

        /// <summary>
        /// **多part 调试船**（批 0 共同样本，2026-10-17）：**一个实体挂 3 个 part**（不同质量/偏移/尺寸，
        /// 其中一件用圆柱预设）——惯量张量合成、主轴分解与操舵窗读数从第一天就有真样本。
        /// 无 Map、圆轨（解析推进）、不带推进（只做惯量/几何样本，不参与燃烧）。
        ///
        /// 手算锚点（人眼对照）：质量 3000+800+200 = 4000 kg；质心 = (800·8 + 200·(−6))/4000 = **1.30**、
        /// 0.10、−1.40 m —— 操舵窗「惯量」行应显示 ≈ (1.30, 0.10, −1.40)。
        /// 判据：三个主惯量非零且量级合理；静止时「重算#」不增长（事件惰性自证）。
        /// </summary>
        /// <summary>
        /// **多part 地图站**（M1 第一刀，2026-10-17）：40×40 舱盒（Part[0]、挂图、浮雕）+ 目录追加件
        /// （thruster/tank/stage）沿 −Y 堆叠。用途 = 游戏内直观验收"舱盒挂图 + 非挂图件"与 PFView 盒体渲染、
        /// 惯量的多 part 合成。**不做推进/发射**（M2 甲：推进仍留地图/实体级）。
        /// 返回群组（一键验收复用；菜单调用忽略返回值）。
        /// </summary>
        public static SpaceEntityGroup? SpawnMultipartStationDebug()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return null;

            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 radial = EquatorialRadial(physicsTick);
            DVector3 pos = radial * (SpaceConstants.PlanetRadiusMeters + 800_000.0);

            // 40×40 舱盒（7,200 t）而不是 60×60（16,200 t）：追加件（2×4×2 等）相对可见，
            // 且不会把 PFView 的相机/浮雕尺度拉到看不见小零件。
            var entity = spaceGame.CreateStation(entityId, groupId, Faction.OfPlayer?.loadID ?? 0,
                pos, 40, 40, "多part测试站");
            if (entity == null) return null;
            var group = spaceGame.GetGroupById(groupId);
            group?.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "PF_ShipPart_Thruster_Debug", category = "thruster", quantity = 1 },
                    new() { defName = "PF_ShipPart_Tank_Debug", category = "tank", quantity = 2 },
                    new() { defName = "PF_ShipPart_Stage_Debug", category = "stage", quantity = 1 },
                }
            };
            var parts = DesignPartAssembler.BuildParts(spec, 40, 40, out _, includeUnknownDefs: true);
            entity.SetParts(parts);

            var map = SpaceMapGenerator.FindPocketMap(entity.MapId);
            if (map != null) SpaceMapGenerator.LinkEntityToMap(entity, map);

            double totalMass = 0.0;
            for (int i = 0; i < parts.Count; i++) totalMass += parts[i].Mass;
            Log.Message($"[多part地图站] G{groupId} E{entityId} 已放置：{parts.Count} 件" +
                $"（舱盒 40×40 挂图 + thruster×1/tank×2/stage×1）｜总质量={totalMass / 1000.0:F1} t" +
                $"｜舱盒 linkedMap={parts[0].linkedMapId}｜追加件 y={parts[1].positionShip.y:F1}…{parts[parts.Count - 1].positionShip.y:F1} m");
            Messages.Message("多part 地图站已放置：PFView 选中看舱盒浮雕 + 下方追加件盒体；「惯量」行应为多件合成。",
                MessageTypeDefOf.TaskCompletion, false);
            return group;
        }

        /// <summary>
        /// **B7 调试件：256 件基准船**——40×40 舱盒 + 255 件 stage，正好顶满 `MaxTotalParts=256`。
        /// 用于渲染/惯量/碰撞/存档/Map 质量的整船规模验证。
        /// </summary>
        public static SpaceEntityGroup? Spawn256PartDebugShip()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return null;

            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 radial = EquatorialRadial(physicsTick);
            DVector3 pos = radial * (SpaceConstants.PlanetRadiusMeters + 2_000_000.0);

            var entity = spaceGame.CreateStation(entityId, groupId, Faction.OfPlayer?.loadID ?? 0,
                pos, 40, 40, "256件基准船");
            if (entity == null) return null;
            var group = spaceGame.GetGroupById(groupId);
            group?.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "PF_ShipPart_Stage_Debug", category = "stage", quantity = 255 }
                }
            };
            var parts = DesignPartAssembler.BuildParts(spec, 40, 40, out _, includeUnknownDefs: true);
            entity.SetParts(parts);
            var map = SpaceMapGenerator.FindPocketMap(entity.MapId);
            if (map != null) SpaceMapGenerator.LinkEntityToMap(entity, map);

            Log.Message($"[256件基准船] G{groupId} E{entityId} 已放置：{parts.Count} 件｜" +
                $"总质量={entity.AggregatedStats.mass / 1000.0:F1} t｜包围盒={entity.BoundsSize.x:F0}×{entity.BoundsSize.y:F0}×{entity.BoundsSize.z:F0} m");
            Messages.Message("256 件基准船已放置：用于 B7 规模验收（装配/惯量/碰撞/存档/Map 质量）。",
                MessageTypeDefOf.TaskCompletion, false);
            return group;
        }

        /// <summary>
        /// **B6 调试件：双舱室船**——一个实体、两件挂图 part、两张独立口袋图（各 40×40）。
        /// 主图 = 第一件舱盒；第二张图挂在第二件舱盒上，用于验证多图指针/坐标/生命周期。
        /// </summary>
        public static SpaceEntityGroup? SpawnDualCabinDebugShip()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return null;

            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 radial = EquatorialRadial(physicsTick);
            // 比多part调试船（R+1200km）更高，避免同场叠放
            DVector3 pos = radial * (SpaceConstants.PlanetRadiusMeters + 1_600_000.0);

            var entity = spaceGame.CreateStation(entityId, groupId, Faction.OfPlayer?.loadID ?? 0,
                pos, 40, 40, "多舱室测试船");
            if (entity == null) return null;
            var group = spaceGame.GetGroupById(groupId);
            group?.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 第二件舱盒（右侧 60 m）——CreateStation 只造了第一件
            var cabin2 = new SpacePart
            {
                partId = 2,
                defName = "PF_ShipPart_Cube",
                positionShip = new DVector3(60, 0, 0),
                boundsSizeShip = new DVector3(40, 50, 40),
                hp = 100f,
                isIndestructible = true,
            };
            cabin2.AddComponent(new HullMassComponent { mass = 0f });
            // 调试件故意“一开一封”：主图舱开放（显示浮雕），第二舱封闭（不显示浮雕、画盒体）。
            // 常规习惯是多 part 船全封闭，但 isSealed 是逐件字段、不是由 part 数推导的机制。
            cabin2.isSealed = true;
            cabin2.InvalidateStats();
            entity.AddPart(cabin2);

            var map2 = SpaceMapGenerator.GeneratePocketMap(40, 40, "DualCabin_" + entityId);
            if (map2 != null)
                SpaceMapGenerator.LinkEntityToMap(entity, map2, cabin2.partId);

            Log.Message($"[多舱室调试船] G{groupId} E{entityId} 已放置：主图={entity.MapId}" +
                $" + 第二图={(map2 != null ? map2.uniqueID : -1)}｜parts={entity.Parts.Count}" +
                $"｜图数={entity.GetMapIds().Count}");
            Messages.Message("双舱室船已放置：两张口袋图各挂一件舱盒（B6 数据/坐标验证用）。",
                MessageTypeDefOf.TaskCompletion, false);
            return group;
        }

        public static SpaceEntityGroup? SpawnMultiPartDebugShip()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return null;
            int bodyId = EARTH_BODY_ID;
            double mu = spaceGame.GetBodyGM(bodyId);
            // 2026-10-17：比地图站（R+800km）高 400km，避免两件调试图在同一轨道点叠场
            // （叠场会让碰撞分离持续扰动圆轨，验收结束后调试图会掉轨/坠毁）。
            double radius = SpaceConstants.PlanetRadiusMeters + 1_200_000.0;
            double vCirc = System.Math.Sqrt(mu / radius);
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 radial = EquatorialRadial(physicsTick);
            DVector3 pos = radial * radius;
            DVector3 vel = ProgradeEquatorialTangent(radial) * vCirc;

            var group = CreateNoMapShipGroup(spaceGame, bodyId, pos, vel, out int entityId,
                overThrust: false, circularOrbit: true);
            if (group == null) return null;
            var entity = spaceGame.GetEntityById(entityId);
            if (entity == null) return null;

            var hull = new SpacePart
            {
                partId = 1, defName = "PF_ShipPart_Cube", positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(10, 6, 40), hp = 200f
            };
            hull.AddComponent(new HullMassComponent { mass = 3000f });

            var left = new SpacePart
            {
                partId = 2, defName = "PF_ShipPart_Cube", positionShip = new DVector3(8, 0, -10),
                boundsSizeShip = new DVector3(4, 4, 8), hp = 100f
            };
            left.AddComponent(new HullMassComponent { mass = 800f });

            var right = new SpacePart
            {
                partId = 3, defName = "PF_ShipPart_Cube", positionShip = new DVector3(-6, 2, 12),
                boundsSizeShip = new DVector3(3, 3, 6), hp = 100f,
                shape = InertiaTensor.Shape.Cylinder,   // B3b：真渲染样本（直径 3 / 高 6，轴 = Z）
            };
            right.AddComponent(new HullMassComponent { mass = 200f });
            // 第三件走**圆柱预设**（2026-10-29 起对称轴 = 体 Z；直径 3 / 高 6）——批 0 形状通道的真样本。
            right.AddComponent(new PartInertiaComponent
            {
                shape = InertiaTensor.Shape.Cylinder,
                dims = new DVector3(3, 0, 6)
            });

            entity.SetParts(new List<SpacePart> { hull, left, right });
            Log.Message($"[多part调试船] G{group.GroupId} E{entityId} 已放置：3 件 part（3000+800+200 kg）｜{group.InertiaDebugString()}");
            return group;
        }

        // ══════════════════════════════════════════════════════════════════
        //  256 件飞行/对撞 + 多part 交会（2026-10-18 用户要按钮）
        // ══════════════════════════════════════════════════════════════════

        private static void TrackStressDemo(int groupId)
        {
            if (groupId >= 0 && !stressDemoGroupIds.Contains(groupId))
                stressDemoGroupIds.Add(groupId);
        }

        /// <summary>装配器的 256 件标准件（舱盒 + 255 stage；与 B7 基准船同一套）。</summary>
        private static List<SpacePart> Build256Parts()
        {
            var spec = new RocketDesignSpec
            {
                parts = new List<DesignPartSpec>
                {
                    new() { defName = "PF_ShipPart_Stage_Debug", category = "stage", quantity = 255 }
                }
            };
            return DesignPartAssembler.BuildParts(spec, 40, 40, out _, includeUnknownDefs: true);
        }

        /// <summary>给无 Map 船换成 3 件多part（与「多part 调试船」同一套质量/形状；不带引擎组件）。</summary>
        private static void ReplaceWithMultiPart3(SpaceEntity entity)
        {
            var hull = new SpacePart
            {
                partId = 1, defName = "PF_ShipPart_Cube", positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(10, 6, 40), hp = 200f
            };
            hull.AddComponent(new HullMassComponent { mass = 3000f });

            var left = new SpacePart
            {
                partId = 2, defName = "PF_ShipPart_Cube", positionShip = new DVector3(8, 0, -10),
                boundsSizeShip = new DVector3(4, 4, 8), hp = 100f
            };
            left.AddComponent(new HullMassComponent { mass = 800f });

            var right = new SpacePart
            {
                partId = 3, defName = "PF_ShipPart_Cube", positionShip = new DVector3(-6, 2, 12),
                boundsSizeShip = new DVector3(3, 3, 6), hp = 100f,
                shape = InertiaTensor.Shape.Cylinder,
            };
            right.AddComponent(new HullMassComponent { mass = 200f });
            right.AddComponent(new PartInertiaComponent
            {
                shape = InertiaTensor.Shape.Cylinder,
                dims = new DVector3(3, 0, 6)
            });

            entity.SetParts(new List<SpacePart> { hull, left, right });
            entity.mainAxisShip = new DVector3(0, 0, -1);
        }

        /// <summary>
        /// 紧凑 256 件（8×8×4，2m 立方件，间距 2.2m）——专门触发 256×256 part 级 CCD。
        /// `oy/oz` 给第二艘一个 phase，使两船的 part 网格在 Y/Z 各错开 2.2m（互不重叠），
        /// 从而在组级扫掠相交时走满采样循环，而不是在组停点就 `PartsOverlap=true`。
        /// </summary>
        private static void ReplaceWithCompact256(SpaceEntity entity, double oy = 0.0, double oz = 0.0)
        {
            var parts = new List<SpacePart>(256);
            int id = 1;
            for (int iz = 0; iz < 4; iz++)
                for (int iy = 0; iy < 8; iy++)
                    for (int ix = 0; ix < 8; ix++)
                    {
                        var part = new SpacePart
                        {
                            partId = id++,
                            defName = "PF_ShipPart_Cube",
                            positionShip = new DVector3(
                                (ix - 3.5) * 6.0,
                                (iy - 3.5) * 6.0 + oy,
                                (iz - 1.5) * 6.0 + oz),
                            boundsSizeShip = new DVector3(2, 2, 2),
                            hp = 100f,
                            isIndestructible = false
                        };
                        part.AddComponent(new HullMassComponent { mass = 100f });
                        parts.Add(part);
                    }
            entity.SetParts(parts);
        }

        /// <summary>
        /// 256 件紧凑高速近失（part 级 CCD 验收入口）：
        /// 两艘 8×8×4=256 件（2m 件、minHalf=1m，间距 6m ⇒ 件间空隙 4m），
        /// 间距 50m、相对 49.5m/s（一步 99m）；起点/步末组 AABB 均不重叠、路径对穿。
        /// 第二艘 part 网格 Y/Z 各错开 3m（半个间距）⇒ 正好落在第一艘的 4m 空隙里，part 不接触。
        /// 期望：`nA*nB=65,536` 不被护栏挡回，`samples=ceil(99/2)=50` ⇒ 走满 256×256 三轴 SAP + 50 次采样；
        /// 日志看 `[碰撞子段] CCD=` 与新增的 `CCD细化=`。
        /// </summary>
        public static void SpawnCompact256CcdDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            ForceNormalTime();

            DVector3 basePos = new(1.0e9, -2.0e8, 0);
            const float speed = 24.75f;   // 相对 49.5 m/s，低于 50 m/s 爆炸阈值
            var a = CreateShipNoMap(spaceGame, Faction.OfPlayer?.loadID ?? 0,
                basePos + new DVector3(-25, 0, 0), new DVector3(2, 2, 2),
                Quaternion.identity, "紧凑256A", new DVector3(speed, 0, 0), dryMass: 0f);
            var b = CreateShipNoMap(spaceGame, (Faction.OfPlayer?.loadID ?? 0) + 1,
                basePos + new DVector3(25, 0, 0), new DVector3(2, 2, 2),
                Quaternion.identity, "紧凑256B", new DVector3(-speed, 0, 0), dryMass: 0f);
            if (a == null || b == null) return;

            var ea = spaceGame.GetEntityById(FirstEntityId(a));
            var eb = spaceGame.GetEntityById(FirstEntityId(b));
            if (ea == null || eb == null) return;
            ReplaceWithCompact256(ea);
            ReplaceWithCompact256(eb, oy: 3.0, oz: 3.0);
            TrackStressDemo(a.GroupId);
            TrackStressDemo(b.GroupId);

            CombatManager.DebugDetailedCollision = true;
            collisionDemoLogOffTick = Find.TickManager.TicksGame + 1800;
            Log.Message($"[紧凑256·近失] A=G{a.GroupId} B=G{b.GroupId}｜8×8×4=256 件/艘（2m 件、间距 6m）｜" +
                $"间距 50m、相对 49.5m/s（一步 99m）｜part 网格 Y/Z 各错开 3m ⇒ 落在 4m 空隙、part 不接触；" +
                $"期望 samples≈50；看 [碰撞子段] CCD= / CCD细化= 与 [TOI诊断]。");
            Messages.Message("紧凑 256 件高速近失已布置：预期触发 256×256 part 级 CCD（无爆炸）。看日志 CCD=/CCD细化=。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>256 件船飞行：无 Map、圆轨、自动顺行点火 15 m/s（看物理/渲染/推进全链）。</summary>
        public static void Spawn256PartFlightDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            ForceNormalTime();

            double mu = spaceGame.GetBodyGM(EARTH_BODY_ID);
            double radius = SpaceConstants.PlanetRadiusMeters + 2_400_000.0;
            double vCirc = System.Math.Sqrt(mu / radius);
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 radial = EquatorialRadial(physicsTick);
            var group = CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID, radial * radius,
                ProgradeEquatorialTangent(radial) * vCirc, out int entityId, overThrust: true, circularOrbit: true);
            if (group == null) return;

            var entity = spaceGame.GetEntityById(entityId);
            if (entity == null) return;
            entity.SetParts(Build256Parts());
            TrackStressDemo(group.GroupId);

            DVector3 burnDir = group.CurrentOrbit.velocitySystem.v.normalized;
            group.StartBurn(burnDir * 15.0);
            Log.Message($"[256件·飞行] G{group.GroupId} E{entityId} 已放置：{entity.Parts.Count} 件｜" +
                $"质量={entity.AggregatedStats.mass / 1000.0:F1} t｜圆轨 r={radius / 1000.0:F0}km｜" +
                $"已顺行点火 15 m/s；看轨道变化/PFView 渲染/物理步耗时。");
            Messages.Message($"256 件飞行船 G{group.GroupId} 已布置并点火 15 m/s；选中看 PFView 与轨道。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>
        /// 同格 256 艘**独立单 part 船**（P5 宽相验收）：全部落在同一个 20 km 格内、
        /// 互相不重叠（500 m 间距），专门压同格精确 AABB/SAP 过滤：
        /// 格内原始对 32,640，真实相交应为 0。
        /// 按钮打印原始对/SAP 候选/耗时；后续物理步可在 [碰撞链成本] 看配对循环。
        /// </summary>
        public static void SpawnDenseSameCellDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            // 先清掉之前 256/10km/s/多part 场景留下的跨格船；否则 AllShipsSingleCell()
            // 会因为别的船是扫掠多格而整体退回旧路径，这个场景就测不到快速路径。
            CleanupStressDemos();
            ForceNormalTime();
            CombatManager.DebugDetailedCollision = false;

            const int n = 256;
            const int cols = 16;
            const double spacing = 500.0;
            var basePos = new DVector3(7_010_000, 10_000, 10_000);   // x/y/z 全落同一格
            int added = 0;
            for (int i = 0; i < n; i++)
            {
                double ox = (i % cols) * spacing - (cols - 1) * spacing * 0.5;
                double oy = ((i / cols) % cols) * spacing - (cols - 1) * spacing * 0.5;
                double oz = ((i / (cols * cols)) % cols) * spacing - (cols - 1) * spacing * 0.5;
                var pos = basePos + new DVector3(ox, oy, oz);
                var group = CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID, pos, DVector3.zero,
                    out _, overThrust: false, circularOrbit: false);
                if (group == null) continue;
                TrackStressDemo(group.GroupId);
                added++;
            }

            int pairCount = 0;
            var grid = Current.Game?.GetComponent<CombatGrid>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (grid != null)
                foreach (var _ in grid.EnumerateShipPairs()) pairCount++;
            sw.Stop();
            Log.Message($"[同格演示] 独立单part船 {added}/{n} 艘已放置（500m 间距，同一 20km 格）｜" +
                $"多格船={grid?.DebugMultiCellShipCount() ?? -1}｜" +
                $"格内原始对={grid?.DebugRawCellPairCount() ?? -1}｜" +
                $"SAP 候选={pairCount} 对 / {sw.Elapsed.TotalMilliseconds * 1000.0:F1} µs；" +
                $"后续看 [碰撞链成本] 配对循环/船对。");
            Messages.Message("同格 256 艘已放置：同一 20km 格、互不重叠；看 [同格演示] 与 [碰撞链成本]。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>256 件对撞：两艘 256 件船、间距 60m、相对 6 m/s（约 3s；低速近距避免 tunneling 捕获）。</summary>
        public static void Spawn256PartCollisionDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            ForceNormalTime();

            DVector3 basePos = new(1.0e9, 0, 0);
            var a = CreateShipNoMap(spaceGame, Faction.OfPlayer?.loadID ?? 0, basePos,
                new DVector3(40, 560, 40), Quaternion.identity, "256对撞A", DVector3.zero, dryMass: 0f);
            var b = CreateShipNoMap(spaceGame, (Faction.OfPlayer?.loadID ?? 0) + 1,
                basePos + new DVector3(60, 0, 0), new DVector3(40, 560, 40), Quaternion.identity,
                "256对撞B", new DVector3(-6, 0, 0), dryMass: 0f);
            if (a == null || b == null) return;

            var ea = spaceGame.GetEntityById(FirstEntityId(a));
            var eb = spaceGame.GetEntityById(FirstEntityId(b));
            if (ea == null || eb == null) return;
            ea.SetParts(Build256Parts());
            eb.SetParts(Build256Parts());
            TrackStressDemo(a.GroupId);
            TrackStressDemo(b.GroupId);

            CombatManager.DebugDetailedCollision = true;
            collisionDemoLogOffTick = Find.TickManager.TicksGame + 1800;
            Log.Message($"[256件·对撞] A=G{a.GroupId}({ea.AggregatedStats.mass / 1000.0:F1}t) B=G{b.GroupId}({eb.AggregatedStats.mass / 1000.0:F1}t)｜" +
                $"间距=60m 相对速度=6m/s ⇒ 约 3s（1×）｜**保持 560m 长条 256 件设计**；" +
                $"看 part 窄相/冲量/一次撞击，不再用紧凑件回避几何。");
            Messages.Message("256 件对撞已布置（1×，约 3 秒；6 m/s 低速撞击）；看日志 [ShipCollision]。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>
        /// 多part 多点对撞：两艘 3 件船侧靠错位（12m 间距/4m/s），第一拍就有多个 part 对，
        /// 专门触发 `ResolveContacts` 的多接触求解路径（不是 256 长条场景）。
        /// </summary>
        public static void SpawnMultipartMultiContactDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            ForceNormalTime();

            DVector3 basePos = new(1.0e9, 2.0e8, 0);
            var a = CreateShipNoMap(spaceGame, Faction.OfPlayer?.loadID ?? 0, basePos,
                new DVector3(20, 20, 20), Quaternion.identity, "多点对撞A", DVector3.zero, dryMass: 0f);
            var b = CreateShipNoMap(spaceGame, (Faction.OfPlayer?.loadID ?? 0) + 1,
                basePos + new DVector3(12, 0, 0), new DVector3(20, 20, 20), Quaternion.identity,
                "多点对撞B", new DVector3(-4, 0, 0), dryMass: 0f);
            if (a == null || b == null) return;

            var ea = spaceGame.GetEntityById(FirstEntityId(a));
            var eb = spaceGame.GetEntityById(FirstEntityId(b));
            if (ea == null || eb == null) return;
            ReplaceWithMultiPart3(ea);
            ReplaceWithMultiPart3(eb);
            TrackStressDemo(a.GroupId);
            TrackStressDemo(b.GroupId);

            CombatManager.DebugDetailedCollision = true;
            collisionDemoLogOffTick = Find.TickManager.TicksGame + 1800;
            Log.Message($"[多part·多点对撞] A=G{a.GroupId} B=G{b.GroupId}｜两艘 3 件、间距 12m、-4m/s｜" +
                $"首拍应有多个 part 对 ⇒ 看 `多接触求解`（不是单接触 `撞击等效`）。");
            Messages.Message("多part 多点对撞已布置：两艘 3 件船侧靠，专门看多接触求解日志。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>形状接触测试用：hull Box + 一件 Sphere + 一件 Box。</summary>
        private static List<SpacePart> BuildShapeContactParts()
        {
            var hull = new SpacePart
            {
                partId = 1, defName = "PF_ShipPart_Cube", positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(10, 6, 40), hp = 200f
            };
            hull.AddComponent(new HullMassComponent { mass = 3000f });

            var sphere = new SpacePart
            {
                partId = 2, defName = "PF_ShipPart_Cube", positionShip = new DVector3(12, 0, 0),
                boundsSizeShip = new DVector3(4, 4, 4), hp = 100f,
                shape = InertiaTensor.Shape.Sphere,
            };
            sphere.AddComponent(new HullMassComponent { mass = 200f });

            var box = new SpacePart
            {
                partId = 3, defName = "PF_ShipPart_Cube", positionShip = new DVector3(-6, 0, 0),
                boundsSizeShip = new DVector3(4, 6, 4), hp = 100f,
                shape = InertiaTensor.Shape.Cylinder,
            };
            box.AddComponent(new HullMassComponent { mass = 200f });

            return new List<SpacePart> { hull, sphere, box };
        }

        /// <summary>形状接触：两艘 3 件船，Sphere/Box 混合，低速对撞，验证解析接触路径。</summary>
        public static void SpawnShapeContactDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            ForceNormalTime();

            DVector3 basePos = new(1.0e9, 3.0e8, 0);
            var a = CreateShipNoMap(spaceGame, Faction.OfPlayer?.loadID ?? 0, basePos,
                new DVector3(20, 20, 20), Quaternion.identity, "形状对撞A", DVector3.zero, dryMass: 0f);
            var b = CreateShipNoMap(spaceGame, (Faction.OfPlayer?.loadID ?? 0) + 1,
                basePos + new DVector3(14, 0, 0), new DVector3(20, 20, 20), Quaternion.identity,
                "形状对撞B", new DVector3(-4, 0, 0), dryMass: 0f);
            if (a == null || b == null) return;

            var ea = spaceGame.GetEntityById(FirstEntityId(a));
            var eb = spaceGame.GetEntityById(FirstEntityId(b));
            if (ea == null || eb == null) return;
            ea.SetParts(BuildShapeContactParts());
            eb.SetParts(BuildShapeContactParts());
            TrackStressDemo(a.GroupId);
            TrackStressDemo(b.GroupId);

            CombatManager.DebugDetailedCollision = true;
            collisionDemoLogOffTick = Find.TickManager.TicksGame + 1800;
            Log.Message($"[形状接触] A=G{a.GroupId} B=G{b.GroupId}｜两艘 3 件（hull Box + Sphere + Cylinder），间距 14m、-4m/s｜看解析 Sphere–Box/Cylinder–Cylinder 接触。");
            Messages.Message("形状接触已布置：Sphere/Box 混合，低速对撞；看 [ShipCollision]。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>多part 交会：3 件追击 → 3 件目标（同轨 30° 相位，走阶梯）。</summary>
        public static void SpawnMultipartRendezvousDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;
            ForceNormalTime();
            SpaceGameComp.UseNewRendezvousLaw = true;

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            double mu = spaceGame.GetBodyGM(EARTH_BODY_ID);
            double radius = SpaceConstants.PlanetRadiusMeters + 500_000.0;
            DVector3 radialChaser = EquatorialRadial(physicsTick);
            double vCirc = System.Math.Sqrt(mu / radius);
            DVector3 radialTarget = RotateAboutUp(radialChaser, 30.0 * System.Math.PI / 180.0);

            var target = CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID, radialTarget * radius,
                ProgradeEquatorialTangent(radialTarget) * vCirc, out int targetEid, overThrust: false);
            var chaser = CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID, radialChaser * radius,
                ProgradeEquatorialTangent(radialChaser) * vCirc, out int chaserEid, overThrust: false);
            if (target == null || chaser == null) return;

            var targetEntity = spaceGame.GetEntityById(targetEid);
            var chaserEntity = spaceGame.GetEntityById(chaserEid);
            if (targetEntity == null || chaserEntity == null) return;
            ReplaceWithMultiPart3(targetEntity);
            ReplaceWithMultiPart3(chaserEntity);
            TrackStressDemo(target.GroupId);
            TrackStressDemo(chaser.GroupId);

            chaser.RegisterRendezvousGuidance(target.GroupId, physicsTick, prograde: true, useNetworks: null);
            chaser.ArmRendezvousGuidance();
            double dvBudget = spaceGame.GetGroupDeltaV(chaser).remainingDv;
            Log.Message($"[多part·交会] 追击=G{chaser.GroupId}(3 件，{chaserEntity.AggregatedStats.mass:F0}kg) → " +
                $"目标=G{target.GroupId}(3 件，{targetEntity.AggregatedStats.mass:F0}kg)｜同轨 30° 相位｜" +
                $"预算 {dvBudget:F0} m/s｜看 [阶梯]/[交会·收尾] 与两者多part 惯量/姿态。");
            Messages.Message($"多part 交会 G{chaser.GroupId} → G{target.GroupId} 已登记（30° 相位）；看日志 [阶梯]。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>
        /// **20 组交会一次布置**（2026-10-21）：同一拍里 20 份阶梯规划 ⇒ 用来验"预热是否到位"。
        ///
        /// 为什么需要它：`[阶梯·耗时]` 的 `#序号` 曲线要**多次调用**才看得出首末差；单组交会每 120 s
        /// 才规划一次，一小时也攒不到 20 个点。这里一次布置 20 对（40 组 / 120 件），全部同拍到期 ⇒
        /// 一拍就产出 20 份计划，曲线当场可见。
        ///
        /// 读数怎么看：
        /// · 出现 `[阶梯·预取] … 20 份` ⇒ 这 20 份走的是**预取并行**（每份 ~0.3–1 ms 时总耗时 1–3 ms；
        ///   若仍是 2–3 ms/份 ⇒ 预热没生效，总耗时会到 20–60 ms 量级）；
        /// · 没出现 `[阶梯·预取]`、而是逐组 `[阶梯·耗时]` ⇒ 走的是内联路径，`#序号` 会连续递增，
        ///   直接看 #1…#20 是否收敛。
        /// · 结束后可用「256/多part 演示·清场」一键清掉（本演示全部登记在同一张表里）。
        /// </summary>
        public static void SpawnRendezvousBatch20()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null) return;
            ForceNormalTime();
            SpaceGameComp.UseNewRendezvousLaw = true;

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            double mu = spaceGame.GetBodyGM(EARTH_BODY_ID);
            DVector3 radial0 = EquatorialRadial(physicsTick);

            const int pairs = 20;
            int made = 0;
            double dvMin = double.MaxValue, dvMax = 0.0;
            for (int i = 0; i < pairs; i++)
            {
                // ★ 2026-10-21 改**异半径**：追击方在 r、目标在 r+200 km。
                //   上一版是严格同轨（同半径只差相位）⇒ `T_syn→∞` 让霍曼搜索立刻放弃，
                //   走的是最便宜那条路，**验不到解析预判门**。异半径才会出现"窗口在 5 圈外"的几何
                //   （台架：7500→7700 km、φ=90° ⇒ 窗口 6.21 圈），那才是要跳过的搜索。
                //   相位差逐对拉开（30°…315°）⇒ 20 组里同时有"可接受窗口"和"远窗口"两种，形成对照。
                double radius = SpaceConstants.PlanetRadiusMeters + 500_000.0 + i * 30_000.0;
                double targetRadius = radius + 200_000.0;
                double vCirc = System.Math.Sqrt(mu / radius);
                double vCircTarget = System.Math.Sqrt(mu / targetRadius);
                DVector3 radialChaser = RotateAboutUp(radial0, i * 12.0 * System.Math.PI / 180.0);
                double phaseGap = (30.0 + i * 15.0) * System.Math.PI / 180.0;
                DVector3 radialTarget = RotateAboutUp(radialChaser, phaseGap);

                var target = CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID, radialTarget * targetRadius,
                    ProgradeEquatorialTangent(radialTarget) * vCircTarget, out int targetEid, overThrust: false);
                var chaser = CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID, radialChaser * radius,
                    ProgradeEquatorialTangent(radialChaser) * vCirc, out int chaserEid, overThrust: false);
                if (target == null || chaser == null) continue;

                var targetEntity = spaceGame.GetEntityById(targetEid);
                var chaserEntity = spaceGame.GetEntityById(chaserEid);
                if (targetEntity == null || chaserEntity == null) continue;
                ReplaceWithMultiPart3(targetEntity);
                ReplaceWithMultiPart3(chaserEntity);
                TrackStressDemo(target.GroupId);
                TrackStressDemo(chaser.GroupId);

                chaser.RegisterRendezvousGuidance(target.GroupId, physicsTick, prograde: true, useNetworks: null);
                chaser.ArmRendezvousGuidance();
                double dv = spaceGame.GetGroupDeltaV(chaser).remainingDv;
                if (dv < dvMin) dvMin = dv;
                if (dv > dvMax) dvMax = dv;
                made++;
            }

            Log.Message($"[交会·批量20] 已登记 {made}/{pairs} 对（{made * 2} 组 / {made * 6} 件）｜**异半径**：" +
                $"追击 {SpaceConstants.PlanetRadiusMeters / 1000.0 + 500:F0}–{SpaceConstants.PlanetRadiusMeters / 1000.0 + 500 + (pairs - 1) * 30:F0} km、" +
                $"目标再高 200 km｜相位 30°–{30 + (pairs - 1) * 15}°｜预算 {dvMin:F0}–{dvMax:F0} m/s｜" +
                $"看 [阶梯] 里有没有「跳过霍曼搜索」与 [阶梯·耗时] #序号（20 份同拍到期）。");
            Messages.Message($"已布置 {made} 组异半径交会（20 份阶梯规划同拍到期）；看日志里「跳过霍曼搜索」。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>清掉本组 256/多part 演示（不碰玩家船）。</summary>
        public static void CleanupStressDemos()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            int removed = 0;
            for (int i = 0; i < stressDemoGroupIds.Count; i++)
            {
                int gid = stressDemoGroupIds[i];
                if (spaceGame.GetGroupById(gid) == null) continue;
                spaceGame.DestroyGroup(gid, "256/多part 演示清场");
                removed++;
            }
            int tracked = stressDemoGroupIds.Count;
            stressDemoGroupIds.Clear();
            Log.Message($"[256/多part 演示] 清场：移除 {removed}/{tracked} 组。");
        }

        // ══════════════════════════════════════════════════════════════════
        //  B8 表现验收一键场景（2026-10-18，用户要按钮）
        //  三个按钮各生成一组测试船；「清场」只清这里生成的组，不碰玩家船。
        // ══════════════════════════════════════════════════════════════════

        private static void TrackB8Demo(int groupId)
        {
            if (groupId >= 0 && !b8DemoGroupIds.Contains(groupId))
                b8DemoGroupIds.Add(groupId);
        }

        /// <summary>B8：生成 3 件多part船并损毁第 2 件，用于看暗红残骸色（其余件仍船体色）。</summary>
        public static void SpawnB8WreckColorDemo()
        {
            var group = SpawnMultiPartDebugShip();
            if (group == null) return;
            TrackB8Demo(group.GroupId);

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            SpaceEntity? entity = null;
            foreach (int eid in group.GetEntityIds())
            {
                entity = spaceGame?.GetEntityById(eid);
                if (entity != null) break;
            }
            if (entity == null || entity.Parts.Count < 2)
            {
                Log.Warning("[B8·残骸色] 没有可损毁的追加件");
                return;
            }

            // 第 2 件（左侧 800kg）不是关键件（无 Map 时关键件 = 首件）⇒ 只影响外观/质量，不会让船失能。
            var target = entity.Parts[1];
            target.ApplyDamage(999999f);
            entity.MarkPartDestroyed(target.partId);
            Log.Message($"[B8·残骸色] G{group.GroupId} E{entity.EntityId} 已放置并损毁 Part[{target.partId}]；" +
                $"选中该船看：**该件暗红**、其余件仍是原船体蓝；残骸仍占位、仍可命中。");
            Messages.Message($"B8 残骸色：G{group.GroupId} 第 2 件已损毁；选中看暗红残骸，其余件应为原色。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>
        /// B8：无 Map 多引擎主推轴——2 台 part 引擎（Back 30kN + Forward 12kN），
        /// **清空旧 mainAxisShip**，强制走 part 聚合；预期朝向棒指 −Z（Back 胜出）。
        /// </summary>
        public static void SpawnB8MainAxisDemo()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            double mu = spaceGame.GetBodyGM(EARTH_BODY_ID);
            double radius = SpaceConstants.PlanetRadiusMeters + 2_400_000.0;
            double vCirc = System.Math.Sqrt(mu / radius);
            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            DVector3 radial = EquatorialRadial(physicsTick);
            DVector3 pos = radial * radius;
            DVector3 vel = ProgradeEquatorialTangent(radial) * vCirc;

            var group = CreateNoMapShipGroup(spaceGame, EARTH_BODY_ID, pos, vel, out int entityId,
                overThrust: false, circularOrbit: true);
            if (group == null) return;
            TrackB8Demo(group.GroupId);
            var entity = spaceGame.GetEntityById(entityId);
            if (entity == null) return;

            var hull = new SpacePart
            {
                partId = 1, defName = "PF_ShipPart_Cube", positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(8, 4, 16), hp = 500f
            };
            hull.AddComponent(new HullMassComponent { mass = 1000f });

            var engineBack = new SpacePart
            {
                partId = 2, defName = "PF_ShipPart_Cube", positionShip = new DVector3(2, 0, -8),
                boundsSizeShip = new DVector3(2, 2, 2), hp = 100f
            };
            engineBack.AddComponent(new HullMassComponent { mass = 100f });
            engineBack.AddComponent(new EngineComponentData
            {
                thrustAxis = ThrustAxis.Back, exhaustVelocity = 3000f, massFlowRate = 10f,
                propellantDefName = "PF_Propellant_Hydrogen"
            });

            var engineForward = new SpacePart
            {
                partId = 3, defName = "PF_ShipPart_Cube", positionShip = new DVector3(-2, 0, 8),
                boundsSizeShip = new DVector3(2, 2, 2), hp = 100f
            };
            engineForward.AddComponent(new HullMassComponent { mass = 100f });
            engineForward.AddComponent(new EngineComponentData
            {
                thrustAxis = ThrustAxis.Forward, exhaustVelocity = 3000f, massFlowRate = 4f,
                propellantDefName = "PF_Propellant_Hydrogen"
            });

            entity.SetParts(new List<SpacePart> { hull, engineBack, engineForward });
            entity.mainAxisShip = null;   // 关键：不靠旧字段，强制 part 引擎聚合

            DVector3 axis = entity.GetMainThrustAxisShip();
            Log.Message($"[B8·主推轴] G{group.GroupId} E{entity.EntityId} 已放置：Back 30kN + Forward 12kN，" +
                $"清空旧 mainAxisShip｜聚合轴=({axis.x:F3},{axis.y:F3},{axis.z:F3})，预期 (0,0,−1)；" +
                $"选中看 PFView 朝向棒/操舵窗应指向船体 −Z。");
            Messages.Message($"B8 主推轴：G{group.GroupId} 聚合轴应为 −Z（Back 30kN > Forward 12kN）；选中看朝向棒。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>B8：双舱室多图入口——生成两张口袋图，旧面板应出现「选择地图入口」。</summary>
        public static void SpawnB8MultiMapDemo()
        {
            var group = SpawnDualCabinDebugShip();
            if (group == null) return;
            TrackB8Demo(group.GroupId);
            Log.Message($"[B8·多图入口] G{group.GroupId} 已放置双舱室船（两张图）；" +
                $"选中该船 → 旧选择面板应显示「选择地图入口」，菜单里可分别进入/取档。" +
                $"PFView 左栏也应有逐图入口；主图舱开放显示浮雕，第二舱封闭显示盒体。");
            Messages.Message($"B8 多图入口：G{group.GroupId} 双舱室船已放置；选中后旧面板点「选择地图入口」。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>B8：一次生成三组表现验收场景。</summary>
        public static void SpawnAllB8Demos()
        {
            SpawnB8WreckColorDemo();
            SpawnB8MainAxisDemo();
            SpawnB8MultiMapDemo();
            Messages.Message("B8 表现验收：残骸色 / 主推轴 / 双舱室三组已布置；测完按「B8 表现验收·清场」。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>B8：只清本组按钮生成的演示组（不碰玩家船）。</summary>
        public static void CleanupB8Demos()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            int removed = 0;
            for (int i = 0; i < b8DemoGroupIds.Count; i++)
            {
                int gid = b8DemoGroupIds[i];
                if (spaceGame.GetGroupById(gid) == null) continue;
                spaceGame.DestroyGroup(gid, "B8 表现验收清场");
                removed++;
            }
            int tracked = b8DemoGroupIds.Count;
            b8DemoGroupIds.Clear();
            Log.Message($"[B8·表现验收] 清场：移除 {removed}/{tracked} 组（只清 B8 按钮生成的组）。");
        }

        /// <summary>
        /// **旧远场律的游戏内验收场景**（2026-09-17）：同轨两船、相位差 120°，追击方用
        /// `RendezvousGuidanceLaw` 自主收相位（**没有出发段机动** —— 律本身就是整条转移）。
        /// ⚠ 批5.B（2026-09-28）：那套律已归档（`Source/已弃置/`）⇒ 本场景现在跑的是**阶梯**，
        ///   船队与几何仍然有用（它仍是"单弧 Lambert 无便宜解"的经典工况），但上面那句"用律收相位"已过期。
        ///
        /// 为什么选"同轨 120°"：这是"单弧 Lambert 无便宜解"的经典工况（纯测试 §14 实测最小单弧解
        /// ≈3,848 m/s，而相位规划只要几十~几百）⇒ 它同时检验"新律确实在走相位机动而不是硬追"。
        ///
        /// <param name="overThrust">false = 用**纯台架 <c>RendezvousShip</c> 的原参数**（8kN 主 / 1.5kN RCS）
        /// ⇒ 读数可与台架 T1/T2/T5 直接对照；true = 用调试放置船的 160kN 档（高推执行者的极端）。
        /// ⚠ 高推档每轮点火冲量 aMax×dt≈100 m/s 会撞上"该轨道能安全吸收的 Δv"上界（LEO ≈100 m/s）
        /// ⇒ 每轮被撞行星保护裁剪：**只会更慢、不会发散**（环增益被削小 = 更阻尼），但读数不可与台架对照。</param>
        ///
        /// 本场景会**打开** <see cref="SpaceGameComp.UseNewRendezvousLaw"/>（调试场景的职责就是验收它）。
        /// 观察点：日志 `[NewLaw] φ=… C0=… ecc=…`（需"详细操舵日志"）→ 最终应出现
        /// 「交会完成（距离 … 相对速度 …）」。
        /// </summary>
        /// <param name="phaseDegAhead">目标在追击方顺行**前方**的相位差（度）。
        /// ⚠ 它直接决定**时长**（不是可调的性能参数，是物理）：收相位的速度上限来自"轨道能缩多深"——
        /// LEO 500km 缩到大气层边缘（地表+100km）只给出 δn≈6.3e-5 rad/s ⇒ 收 120° 要 ≈3.4e4 模拟秒
        /// （≈6 个轨道周期；8 倍速下约 70 真实分钟）。故另给一个 30° 快例（≈9e3 秒/19 分钟）用于快速回归。
        /// 代价遵循相位守恒律 Δv·t = (2/3)·r·Δφ ⇒ 快例与硬例的 Δv **同量级**，只有时间不同。</param>
        /// <param name="phaseDegAhead">目标在追击方顺行**前方**的相位差（度）。</param>
        /// <param name="targetAltKm">目标的高度（km）相对追击方的差值：0 = 同轨（只考相位段）；
        /// >0 = 目标在更高的圆轨（**考形状段**：先变轨、再收相位）。</param>
        /// <param name="targetIncDeg">目标轨道的**倾角**（度）：把目标的半径方向绕**当地切向**转过去实现
        /// —— 同半径 + 异面时速度仍切向（`dot(r′,v′)=0` 且 `|v′|=v_c(R)`）⇒ 仍是圆轨、升交点在目标当前位置。
        /// ⚠ **异面是阶梯「匹配平面」那一级唯一会上场的几何**（同面时那一级永远不会被选中）
        /// ⇒ 不设它，那一级就只能靠台架/纯测试覆盖。</param>
        public static void SpawnRendezvousTest(bool overThrust, double phaseDegAhead,
            double targetAltKm = 0.0, double targetIncDeg = 0.0)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null)
            {
                Messages.Message("SpaceGameComp / CelestialBodyManager 未找到！", MessageTypeDefOf.RejectInput);
                return;
            }

            SpaceGameComp.UseNewRendezvousLaw = true;

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            int bodyId = EARTH_BODY_ID;
            double mu = spaceGame.GetBodyGM(bodyId);
            double radius = SpaceConstants.PlanetRadiusMeters + 500_000.0;
            DVector3 radialChaser = EquatorialRadial(physicsTick);
            double vCirc = System.Math.Sqrt(mu / radius);
            double phaseRad = phaseDegAhead * System.Math.PI / 180.0;
            // 目标在追击方**顺行前方**（沿用纯测试 §14 的工况：Lambert 无便宜解、必须收相位）
            DVector3 radialTgt = RotateAboutUp(radialChaser, phaseRad);
            double radiusTgt = radius + System.Math.Max(targetAltKm, 0.0) * 1000.0;
            double vCircTgt = System.Math.Sqrt(mu / radiusTgt);

            // 倾角：把半径方向绕**当地切向**转 i（切向自身不变 ⇒ 速度仍切向、轨道仍圆，升交点在本位置）
            DVector3 radialTgtUse = radialTgt;
            if (System.Math.Abs(targetIncDeg) > 1e-9)
            {
                double inc = targetIncDeg * System.Math.PI / 180.0;
                radialTgtUse = radialTgt * System.Math.Cos(inc) - DVector3.up * System.Math.Sin(inc);
            }
            var target = CreateNoMapShipGroup(spaceGame, bodyId, radialTgtUse * radiusTgt,
                ProgradeEquatorialTangent(radialTgt) * vCircTgt, out _, overThrust);
            if (target == null) return;
            var chaser = CreateNoMapShipGroup(spaceGame, bodyId, radialChaser * radius,
                ProgradeEquatorialTangent(radialChaser) * vCirc, out _, overThrust);
            if (chaser == null) return;

            // 登记精确交会：登记即生效（旧路径要等出发机动点火才 armed；阶梯引导不需要那个出发段）。
            chaser.RegisterRendezvousGuidance(target.GroupId, physicsTick, prograde: true, useNetworks: null);
            chaser.ArmRendezvousGuidance();
            // 批5.B（2026-09-28）：原先这里写 `rendezvousLawLegSeconds = legSeconds`（旧律的"腿长"旋钮）。
            // 旧律归档 + 该状态族退役后**它是死的**（阶梯没有腿长旋钮）⇒ 本方法也不再收 `legSeconds`
            // 形参（那 6 个调试按钮的实参一并删掉）。
            // ✅ 2026-09-28（用户裁定"删掉这个估计，或让阶梯自己出价目"）：**删掉借用作废律的价目估计**
            //   （原为 `RendezvousGuidanceLaw.PlanLeg` + `ShapeCostFactor`）。理由两层：
            //     ① 那套律已被裁定作废（不许接回/当兜底）⇒ 场景不该再有它的活依赖；
            //     ② 更要紧的是**它会撒谎**：它算的是旧律"形状+相位"两段账，与阶梯真正排的
            //        （MatchPlanes → Hohmann/Intercept…）不是同一笔 ⇒ 本轮日志实测估计 642 m/s
            //        vs 实际累计 ≈1207 m/s（1.9×）⇒ **一个可能撒谎的读数不如没有**。
            //   ⇒ 现在只报**预算**（客观事实）；真实价格由阶梯在运行时给出（日志 `[阶梯] … Δv 合计`）。
            double dvBudget = spaceGame.GetGroupDeltaV(chaser).remainingDv;
            Log.Message($"[交会验收] G{chaser.GroupId}(追击) → G{target.GroupId}(目标)｜追击 r={radius / 1000.0:F0}km 目标 r={radiusTgt / 1000.0:F0}km 相位差 {phaseDegAhead:F0}° 目标倾角 {targetIncDeg:F1}°｜预算 {dvBudget:F0} m/s（**不再预估 Δv**：旧估计借已作废律的价目函数、与实际 1.9× 不符；真实价格见运行时 [阶梯] 行）｜引导=阶梯（远场律与 5% CW 终端已于 2026-09-22 作废删除）· 期望：先匹配平面 → 变轨/调相 → 画拦截 → 收尾降档 → 到达判据收口");
            Messages.Message($"交会验收已布置：G{chaser.GroupId} 追击 G{target.GroupId}（相位 {phaseDegAhead:F0}°，目标高 {targetAltKm:F0}km）。预算 {dvBudget:F0} m/s；真实 Δv 由阶梯在运行时给出，读数见日志 [阶梯]/[交会·收尾]。",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>
        /// 绕 <c>up</c>（+Y）轴把矢量旋转 angle（**顺行方向为正**：radial(θ)=(cosθ,0,sinθ)，
        /// 而 <see cref="ProgradeEquatorialTangent"/> = d/dθ 同号 ⇒ 正角 = 顺行前方）。
        /// </summary>
        private static DVector3 RotateAboutUp(DVector3 v, double angle)
        {
            double c = System.Math.Cos(angle), s = System.Math.Sin(angle);
            return new DVector3(v.x * c - v.z * s, v.y, v.x * s + v.z * c);
        }



        /// 核热引擎(液氢) + 氢储罐(满 5000kg)、离子引擎(氙) + 氙储罐(满 1000kg)，
        /// 规划两条各自绑定网络的机动（①核热径向 ②离子切向），并打开机动队列窗口。
        /// </summary>
        public static void SpawnDualEngineTest()
        {
            var (entity, group, map) = CreateTestStation();
            if (entity == null || group == null || map == null)
                return;

            // 布置引擎与储罐（1x1 建筑，错开格子）
            PlaceBuilding(map, "PF_Building_Thruster_NuclearThermal", new IntVec3(28, 0, 30));
            var hydrogenTank = PlaceBuilding(map, "PF_Building_Tank_Hydrogen", new IntVec3(30, 0, 30));
            PlaceBuilding(map, "PF_Building_Thruster_Ion", new IntVec3(28, 0, 32));
            var xenonTank = PlaceBuilding(map, "PF_Building_Tank_Xenon", new IntVec3(30, 0, 32));

            // 加注至满罐（同步到 mapComp 工质向量）
            if (hydrogenTank != null)
                hydrogenTank.TryFill(hydrogenTank.Capacity);
            if (xenonTank != null)
                xenonTank.TryFill(xenonTank.Capacity);

            // 规划两条机动：先设「当前网络=核热(氢)」加入机动1，再切「当前网络=离子(氙)」加入机动2，
            // 验证新机动自动继承当前网络配置。executeTick 用 physicsTick 基准（与检查点一致）。
            int now = (int)System.Math.Round(TimeScaleManager.GetPhysicsTickSafe());
            var hydrogenDef = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrogen");
            var xenonDef = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Xenon");

            group.currentNetwork = hydrogenDef; // 当前网络：核热(氢)
            group.QueueManeuver(new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = 50f,
                tangentialDV = 0f,
                normalDV = 0f,
                executeTick = now + 600,   // 10 分钟后
                active = true
                // useNetworks 由 QueueManeuver 继承 currentNetwork
            });

            group.currentNetwork = xenonDef; // 当前网络：离子(氙)
            group.QueueManeuver(new SpaceEntityGroup.PlannedManeuver
            {
                radialDV = 0f,
                tangentialDV = 30f,
                normalDV = 0f,
                executeTick = now + 1200,  // 20 分钟后
                active = true
            });

            Log.Message($"[DualEngineTest] 场景就绪: 氢罐 {(hydrogenTank?.StoredMass ?? 0f)}kg / 氙罐 {(xenonTank?.StoredMass ?? 0f)}kg，机动1(核热+50径向) T+600，机动2(离子+30切向) T+1200");

            // 打开机动队列窗口（查看/调整每条机动的网络选择）
            Find.WindowStack.Add(new ManeuverQueueWindow(group));

            Messages.Message("双引擎测试场景已部署：核热+离子，两条机动已各自绑定网络（见机动队列）",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// **[A4] 吸气式试验机**（**看读数用**，不进自动化判据）。
        ///
        /// 场景：无 Map 细长棍（1×1×12 m ⇒ 正面 1 m²、侧面 12 m²，顺气流指向由 [A6+] 的制导纪律负责），
        /// 干重 8 t，出生在 **地表 30 km**、**水平顺行 400 m/s**，点火方向 = **当前速度方向**（水平加速）。
        /// 推进 = `StandardPropulsion` 的**标准吸气式档**（ve 30000 / ṁ 5 / a=1 / 空气 15:1）+ 空气罐 500 kg
        /// （**初始空罐** ⇒ 充填是"从零开始"，一眼可读）。
        ///
        /// 该看的三件事（都由物理路径自己打，日志各最多一条）：
        ///  ① **充填**：空气罐从 0 涨起来（30 km 处 ρ≈0.028、400 m/s ⇒ 进气道 12 m² 给 ≈135 kg/s，
        ///     超过全流量 75 kg/s ⇒ 罐会灌满；罐满后充填自然停）；
        ///  ② **包线**：推力随高度衰减（φ = ρ/ρ0）——爬升越高，加速越慢（本场景没有自动驾驶，
        ///     它飞着飞着就往下掉，ρ 回升、推力又涨回来：这正是"L1 无升力"的直接表现）；
        ///  ③ **熄火/不路由**：空气抽干 ⇒ `[大气·进气] …熄火`（该步燃料不扣）；飞进真空 ⇒
        ///     `[大气·包线] …包线因子 0 ⇒ 不路由`（D5 的网络可用性门控）。
        ///
        /// ⚠ 这不是"能不能飞"的判据 —— L1 无升力、无悬浮（[A7] 才做定点保持），它**注定会落**。
        /// </summary>
        public static void SpawnAirBreathingTest(bool announce = true)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Messages.Message("SpaceGameComp 未找到！", MessageTypeDefOf.RejectInput);
                return;
            }

            var airDef = StandardPropulsion.IntakeAirDef;
            var fuelDef = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrogen");
            if (airDef == null || fuelDef == null)
            {
                Log.Error("[吸气式试验机] 工质 Def 缺失（" + StandardPropulsion.IntakeAirDefName + " / PF_Propellant_Hydrogen）");
                return;
            }

            // 物理依赖时间推进
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;

            int factionId = Faction.OfPlayer.loadID;
            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();

            double altitude = 30000.0;
            double radius = SpaceConstants.PlanetRadiusMeters + altitude;
            var radial = new DVector3(1, 0, 0);
            var tangent = ProgradeEquatorialTangent(radial).normalized;
            var pos = radial * radius;
            var vel = tangent * 400.0;                      // 水平 400 m/s（顺行）

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsActive = true,
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = EARTH_BODY_ID
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)vel,
                rotation = DQuaternion.identity,
            };
            spaceGame.RegisterGroup(group);

            var entity = new SpaceEntity(entityId, factionId)
            {
                HasMap = false,
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true,
            };
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);

            // 细长棍 + 进气道（[补3] 的同款几何：就是要让气动看得出来）
            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_TestMissile",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(1, 1, 12),
                hp = 200f,
                isIndestructible = false,
            };
            part.AddComponent(new Components.HullMassComponent { mass = 8000f });
            part.AddComponent(StandardPropulsion.Intake(areaM2: 12f));
            entity.SetParts(new List<SpacePart> { part });

            // 主网（吸气式）+ 空气罐（空罐 ⇒ 充填从零可见）
            entity.AddPropulsion(StandardPropulsion.AirBreathingEngine(fuelDef, fuelKg: 500.0, airDef: airDef));
            entity.AddPropulsion(StandardPropulsion.AirTank(airDef, capacityKg: 500.0, fillKg: 0.0));

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);

            // 点火：沿**水平速度方向**加速（不爬升 ⇒ 本场景只看水平飞行下的充填与包线）
            group.StartBurn(tangent * 3000.0);
            SpaceGameComp.DebugDetailedThrust = true;

            if (announce)
                Messages.Message("吸气式试验机已部署（30 km，水平 400 m/s，空罐起飞）——看 [大气·进气] / [大气·包线] 读数",
                    MessageTypeDefOf.TaskCompletion);
            Log.Message($"[吸气式试验机] G{groupId} E{entityId} 高度 {altitude / 1000.0:F0} km｜水平 {400.0:F0} m/s｜干重 8 t｜吸气式 ve={StandardPropulsion.AirBreathingVe:F0} ṁ={StandardPropulsion.StandardMassFlow:F0} a={StandardPropulsion.AirBreathingDensityExponent:F1}｜空气罐 0/500 kg（进气道 12 m²）｜全推续航（满罐）= {AirBreathingCore.FullThrottleEnduranceSeconds(500.0, StandardPropulsion.StandardMassFlow):F0} s｜点火 Δv 3000 m/s（沿当前速度）");

        }

        /// <summary>
        /// **[A7] 悬浮试验机**（**看读数用**，进不了自动化判据 —— 它是闭环控制器，得看它稳不稳）。
        ///
        /// 场景：无 Map 棍（1×1×10 m）干重 3 t + 全矢量 RCS 工质 3 t（ve 3000 / ṁ 20 ⇒ 60 kN，
        /// 对 6 t 全重给 ≈10 m/s²，压得住本行星地表 8 m/s² 的重力并留余量）；
        /// 出生在**地表 30 km**、**带着 −20 m/s 的向下速度**（让控制器有事做：它得先刹住再保持），
        /// **立刻开悬浮**（定点 = 出生点的星下点 + 30 km）。
        ///
        /// 该看的：
        ///   · 高度**守在 30 km** 附近（台架给的抖幅量级 = 死区 `HoverDefaultDeadZone` ≈ 几米；
        ///     计划稿原话的"dt² 十米级"实测**不适用**于"每步刷新抵消"的实现，见 `HoverBenchTests`）；
        ///   · 燃料按 ≈ `|g|·m/ve` ≈ 16 kg/s 往下走（**悬浮是吃料的**：物理档 = 真实推力）；
        ///   · 姿态**上轴朝下不歪**（D10 锁铅垂；顺气流指向被悬浮让位）；
        ///   · 读数行（需 `DebugDetailedSteering` = 默认已在操舵详细日志里开）每 10 游戏秒一行 `[悬浮]`。
        ///
        /// ⚠ 本刀口径：悬浮**只走全矢量（RCS）**推力、**不转船体**（主引擎要对准会把船拧歪）；
        ///   RCS 给不出就报"推力不足"（如实报，不偷偷切主网）。玩家向入口（操舵台/PFView）属视觉/交互线，本刀不做。
        /// </summary>
        public static void SpawnHoverTest(bool announce = true, double fuelKg = 3000.0)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Messages.Message("SpaceGameComp 未找到！", MessageTypeDefOf.RejectInput);
                return;
            }
            var hydrazine = DefDatabase<PropellantDef>.GetNamedSilentFail("PF_Propellant_Hydrazine");
            if (hydrazine == null)
            {
                Log.Error("[悬浮试验机] 工质 Def 缺失（PF_Propellant_Hydrazine）");
                return;
            }

            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            SpaceGameComp.DebugDetailedSteering = true;   // 开 [悬浮] 周期读数（10 游戏秒一行）

            int factionId = Faction.OfPlayer.loadID;
            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();

            double altitude = 30000.0;
            double radius = SpaceConstants.PlanetRadiusMeters + altitude;
            var radial = new DVector3(1, 0, 0);
            var pos = radial * radius;
            // ⚠ **必须带共转速度**：悬浮的定点是"地面上的一个点"，它随行星以 ω·(R+alt) ≈ 523 m/s（赤道）转。
            //   以惯性系零速扔出去 = 一出生就背着 523 m/s 的速度差 ⇒ 控制器拿 10 m/s² 去追一个永远追不上的
            //   目标 ⇒ 烧干料、自由落体（**2026-10-06 首轮实测翻的就是这个**，日志见该轮交接）。
            double omega = Launch.GetSurfaceState.RotationOmega(
                Current.Game?.GetComponent<CelestialBodyManager>(), EARTH_BODY_ID);
            var vel = SurfaceFrame.SurfaceVelocity(pos, omega) + radial * -20.0;   // 共转 −20 m/s（向下）

            var group = new SpaceEntityGroup(groupId, factionId)
            {
                IsActive = true,
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = EARTH_BODY_ID,
            };
            group.CurrentOrbit = new OrbitState
            {
                positionSystem = (SystemVec3)pos,
                velocitySystem = (SystemVec3)vel,
                rotation = DQuaternion.identity,
            };
            spaceGame.RegisterGroup(group);

            var entity = new SpaceEntity(entityId, factionId)
            {
                HasMap = false,
                IsMapArchived = false,
                IsCollidable = true,
                IsMovable = true,
                CanInteract = false,
                IsAffectedByGravity = true,
            };
            entity.GroupId = groupId;
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);

            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_TestMissile",
                positionShip = DVector3.zero,
                boundsSizeShip = new DVector3(1, 1, 10),
                hp = 200f,
                isIndestructible = false,
            };
            part.AddComponent(new Components.HullMassComponent { mass = 3000f });
            entity.SetParts(new List<SpacePart> { part });

            // 全矢量网（isVectoredRcs）：悬浮唯一认的那一类。
            // ⚠ 罐量刻意**短**（2026-10-06 第六轮用户裁定："十几分钟不行，几乎永远"）：测试要的是
            //   **几秒内看到状态**，不是让试验机在天上吊一刻钟 ⇒ 3 t / ve 3000 ⇒ 6 t 全重下 a = 10 m/s²、
            //   等效耗料率 ≈16 kg/s、续航 ≈190 s（够按「悬浮·切档」；想看"料尽"，按「悬浮·抽空」几秒就到）。
            // `fuelKg` 默认 3000（**手动场景**：短才对，≈190 s）；**自动跑批器**传更长的一档 ——
            //   它要连着跑"物理 150 s + 解析 50 s + 回物理 25 s"再抽空，3000 kg 会在半路见底，
            //   把"料尽退出"提前到解析档（实测抓到：那条会被误报成功能红字）。
            // ⚠ **加燃料必须同时给推力**：6000 kg 时全重 9 t，若还按 ve=3000/ṁ=20（60 kN）⇒
            //   a = 6.7 m/s² **低于本地重力 7.9** ⇒ 悬不住（实测抓到：物理档一路掉到 −12.5 km）。
            //   ⇒ 长续航档把 ve 抬到 9000（180 kN ⇒ 9 t 时 a ≈ 20 m/s²），并按质量算 ṁ。
            bool longRange = fuelKg > 3500.0;
            float veUsed = longRange ? 9000f : 3000f;
            float mfUsed = longRange ? (float)((3000.0 + fuelKg) * 8.0 / 9000.0 * 2.5) : 20f;   // 留 2.5 倍余量
            entity.AddPropulsion(new PropulsionData
            {
                propellant = hydrazine, ve = veUsed, massFlow = mfUsed,
                mass = fuelKg, capacity = fuelKg, isVectoredRcs = true,
            });

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);

            bool ok = spaceGame.SetHover(group, true);
            if (announce)
            {
                Messages.Message(ok
                        ? "悬浮试验机已部署（30 km，−20 m/s，已开悬浮）——看 [悬浮] 读数与高度是否守住"
                        : "悬浮试验机已部署，但悬浮未能开启（缺天体/在地表下）",
                    MessageTypeDefOf.TaskCompletion);
            }
            Log.Message($"[悬浮试验机] G{groupId} E{entityId} 高度 {altitude / 1000.0:F0} km｜−20 m/s 起步｜全重 {(3000.0 + fuelKg) / 1000.0:F1} t｜RCS ve={veUsed:F0} ṁ={mfUsed:F1}（≈{veUsed * mfUsed / 1000f:F0} kN）｜料 {fuelKg:F0} kg（≈{AirBreathingCore.FullThrottleEnduranceSeconds(fuelKg, mfUsed):F0} s 满推续航）｜定点已锁 ⇒ 该看：高度守住 + 上轴朝铅垂；按「悬浮·切档」切解析档（**定点不变**，读数行变成 [悬浮·解析档]）；按「悬浮·抽空」几秒看料尽退出");

        }

        /// <summary>
        /// **赤道 × 本初子午线 悬停船（超级测试船：调试引擎 + 无限燃料）** —— 2026-10-07 用户点名。
        ///
        /// 位置 = **φ 0°、λ 0°**（RimWorld 约定 λ = Atan2(x, −z) ⇒ 本初子午线在 **−z 轴**），高度 30 km
        /// （大气层内，悬浮有意义），带共转速度（少给 20 m/s 向下）。
        ///
        /// **有 Map**（船上带**操舵台** ⇒ 可直接开「船上控制台」看地块图/读数/悬浮档位）。
        ///
        /// ⚠ 为什么配**调试超级引擎**：地图站质量 = `mapW×50×mapH×90 kg/m³`（**60×60 = 16,200 t**）
        /// ⇒ 悬停要 ≈128 MN，而常规 RCS 只有 4.5 kN/台 ⇒ 差 3 个数量级（实测那台"推力不足 ⇒ 会掉"的真因）。
        /// 本钮用 `PF_Building_Thruster_TestSuper`（单台 300 MN），**台数按质量当场推**（1.6× 余量，不是拍的）。
        ///
        /// **无限燃料** = `group.debugInfinitePropellant`（唯一扣减口报告"已扣完"但不动账）——
        /// **不是**把罐加大（那会把重量加上去、反而更吊不动）。
        /// </summary>
        public static void SpawnEquatorHoverTest()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || mgr == null)
            {
                Messages.Message("SpaceGameComp / CelestialBodyManager 未找到！", MessageTypeDefOf.RejectInput);
                return;
            }
            const string engineDefName = "PF_Building_Thruster_TestSuper";
            var engineDef = DefDatabase<ThingDef>.GetNamedSilentFail(engineDefName);
            CompProperties_Thruster? engineProps = null;
            if (engineDef != null && engineDef.comps != null)
            {
                for (int i = 0; i < engineDef.comps.Count; i++)
                    if (engineDef.comps[i] is CompProperties_Thruster p) { engineProps = p; break; }
            }
            if (engineProps == null)
            {
                Log.Error("[赤道测试船] 调试引擎 Def 缺失或没有 CompProperties_Thruster：" + engineDefName);
                return;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            SpaceGameComp.DebugDetailedSteering = true;   // [悬浮] 周期读数

            double tick = TimeScaleManager.GetPhysicsTickSafe();
            double omega = GetSurfaceState.RotationOmega(mgr, EARTH_BODY_ID);
            double radius = GetSurfaceState.BodyRadius(mgr, EARTH_BODY_ID);
            const double altitude = 30000.0;
            // 赤道 × 本初子午线：**由 SurfaceState 直接给**（不经过 tile —— 那是经纬度的定义点）
            var (pos, velSurf) = SurfaceFrame.SurfaceState(radius + altitude, 0.0, 0.0, omega, tick);
            var vel = velSurf + pos.normalized * (-20.0);      // 共转 −20 m/s（向下）

            int groupId = spaceGame.AllocGroupId();
            int entityId = spaceGame.AllocEntityId();
            var entity = spaceGame.CreateStation(entityId, groupId, Faction.OfPlayer.loadID,
                pos, mapWidth: 60, mapHeight: 60, stationName: "赤道原点 测试船");
            if (entity == null) return;
            var group = spaceGame.GetGroupById(groupId);
            if (group == null) return;
            group.CurrentOrbit.velocitySystem = (SystemVec3)vel;   // CreateStation 给的是零速 ⇒ 补共转
            group.OrbitDirty = true;

            // ── 台数**按质量当场推**（余量 1.6×）——不许拍（纪律：预算/选型要么由生产量推出、要么写明余量）──
            double massKg = entity.TotalMass;                       // 站体质量（HullMassComponent 已同步就绪）
            double gSurface = SpaceConstants.PrimaryGravitationalParameter / (radius * radius);
            double needN = massKg * gSurface * 1.6;
            double perEngineN = engineProps.exhaustVelocity * engineProps.massFlowRate;
            int engines = perEngineN > 0.0 ? (int)System.Math.Ceiling(needN / perEngineN) : 1;
            if (engines < 1) engines = 1;

            var map = SpaceMapGenerator.FindPocketMap(entity.MapId);
            if (map != null)
            {
                PlaceBuilding(map, "PF_Building_SteeringConsole", new IntVec3(30, 0, 30));   // 控制台入口
                var tank = PlaceBuilding(map, "PF_Building_Tank_Hydrogen", new IntVec3(28, 0, 30));
                if (tank != null) tank.TryFill(600f);       // 有料就行（**无限**：存量只影响质量口径）
                for (int i = 0; i < engines; i++)
                    PlaceBuilding(map, engineDefName, new IntVec3(18 + (i % 10), 0, 34 + (i / 10)));
            }

            group.debugInfinitePropellant = true;          // **无限燃料**（唯一扣减口不动账）
            bool ok = spaceGame.SetHover(group, true);     // 物理档悬浮（调试引擎真给推力）
            string spot = $"φ 0.0° / λ 0.0°（赤道 × 本初子午线）· 高度 {altitude / 1000.0:F0} km";
            Messages.Message(ok
                    ? "赤道原点测试船已部署（" + spot + "，无限燃料，已开悬浮）——带操舵台 ⇒ 可开「船上控制台」"
                    : "赤道原点测试船已部署（" + spot + "），但悬浮未能开启（详情见日志）",
                ok ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput);
            Log.Message($"[赤道测试船] G{groupId} E{entityId}：{spot}｜质量 {massKg / 1000.0:F0} t｜需 {needN / 1e6:F1} MN（含 1.6× 余量）｜调试引擎 {engines} 台 × {perEngineN / 1e6:F0} MN｜无限燃料=开｜悬浮={(ok ? "物理档" : "未开")}｜（有 Map；站体边长 60×60 m）");

        }

        /// <summary>**[A7] 悬浮·关**（把当前所有在悬浮的组都关掉；调试用 —— 玩家向入口在操舵台/PFView 那条线）。</summary>
        public static void StopAllHover()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            int n = 0;
            foreach (var g in spaceGame.GetAllGroups())
            {
                if (g != null && g.hoverActive && spaceGame.SetHover(g, false)) n++;
            }
            Messages.Message("悬浮已关：" + n + " 组", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// **[A8 测试用] 悬浮·抽空**：把在悬浮的组的**全矢量（RCS）料**抽到只剩 <paramref name="keepKg"/>，
        /// 让"**料尽 ⇒ 显式退出 ⇒ 自由落体**"这条路径**几秒内**可见（不必等它自己烧干 ——
        /// 用户 2026-10-06 裁定"十几分钟不行，几乎来不及看"）。
        /// </summary>
        public static void DrainHoverFuel(double keepKg = 60.0)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            int n = 0; double drained = 0.0;
            foreach (var g in spaceGame.GetAllGroups())
            {
                if (g == null || !g.hoverActive) continue;
                foreach (int eid in g.GetEntityIds())
                {
                    var e = spaceGame.GetEntityById(eid);
                    if (e == null) continue;
                    for (int i = 0; i < e.propulsion.Count; i++)
                    {
                        var d = e.propulsion[i];
                        if (d == null || !d.isVectoredRcs || d.propellant == null) continue;
                        double cut = d.mass - keepKg;
                        if (cut > 0.0) drained += e.TryConsumePropellant(d.propellant, cut);
                    }
                    n++;
                }
            }
            Messages.Message("悬浮料已抽空（剩 " + $"{keepKg:F0}" + " kg/实体）：抽掉 " + $"{drained:F0}" + " kg",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// **[A8] 悬浮·切档（解析 ⇄ 物理）**：把当前在悬浮的组整体切到另一档，**进出都写日志**（D7）。
        /// 该看的：① 日志里两条 `[档位]` 行（含档位 / 等效耗料率 / 续航）；② **解析档下位置被钉死在定点**
        /// （读数高度差应恒为 0 且**没有锯齿**——不再有 × 步的推力）；③ **两档耗料率应同量级**
        /// （解析档按 `m·g/ve` 计费，物理档真给推力 ⇒ 判据"账单一致"）。
        /// </summary>
        public static void ToggleHoverTier()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            int n = 0;
            foreach (var g in spaceGame.GetAllGroups())
            {
                if (g == null || !g.hoverActive) continue;
                bool toAnalytic = !g.hoverAnalytic;
                if (spaceGame.SwitchHoverTier(g, toAnalytic)) n++;   // ⚠ 只翻档位、**不重锁定点**
            }
            Messages.Message(n > 0 ? ("悬浮档位已切换：" + n + " 组（看 [档位] 日志）") : "没有在悬浮的组",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// **[A3]–[A5] 有 Map 侧大气三件套试验台**（2026-10-07，供跑批器与手动场景共用）：
        /// 在**殖民地地图**上立一座带地图的飞船（`CreateTestStation`），并装上：
        /// 空气罐 ×1 · 进气道 ×1 · 吸气式发动机 ×1（ve 30000 / ṁ 5 / a=1 / 空气 15:1）。
        ///
        /// **为什么要它**：有 Map 侧的进气/抽气/包线此前只钉了"数据一致性"这一层；要跑**飞行中**的行为，
        /// 需要的其实是**空气相对速度**（不用真发射）—— 跑批器直接把群速度改成"共转 + 500 m/s 顺行"即可。
        /// 手动看：地图上会多出三个建筑；停在地表时 v_air≈0 ⇒ **如实不进气**（这是负对照）。
        /// </summary>
        public static bool SpawnMapAeroTest(out int groupId, out Map? map, bool announce = true,
            double altitudeMeters = 60000.0)
        {
            groupId = -1;
            map = null;
            // 高度取 **60 km**（2026-10-07 两轮实测后定）：
            //   · 500 km（默认）：ρ=0 ⇒ 进气与包线**双双失真**（首轮红）
            //   · 30 km：ρ≈0.028，几百 m² 迎风面的站**被阻力拽下来撞掉**（第二轮"组不见了"）
            //   · 60 km：ρ≈6.7e-4 ⇒ 进气够用（A=6 m²、v_air=500 ⇒ ≈2 kg/s）、阻力撑得住这一两百秒
            var (entity, group, map0) = CreateTestStation(altitudeMeters);
            if (entity == null || group == null || map0 == null) return false;
            map = map0;

            var airTank = PlaceBuilding(map0, "PF_Building_Tank_IntakeAir", new IntVec3(24, 0, 30));
            PlaceBuilding(map0, "PF_Building_IntakeAir", new IntVec3(26, 0, 30));
            PlaceBuilding(map0, "PF_Building_Thruster_AirBreathing", new IntVec3(28, 0, 30));
            // [③ + A8] **操舵台 = 玩家向控制台的入口**（2026-10-07 用户裁定）⇒ 试验台必须带一台，
            // 否则"控制台"这条 UI 在场景里无处可点（挂点从储罐挪到操舵台之后就是这条依赖）。
            PlaceBuilding(map0, "PF_Building_SteeringConsole", new IntVec3(32, 0, 30));
            // ⚠ 空气罐**故意留空**（充填要看得见"从 0 涨起来"）⇒ 新立的罐本来就是空的，什么都不用做。
            _ = airTank;   // （保留引用：将来若要预装，用 `TryFill`）
            // 燃料罐**必须加满**（不然"燃料在扣"根本测不出来 —— 实测抓到的第二条红）
            var h2Tank = PlaceBuilding(map0, "PF_Building_Tank_Hydrogen", new IntVec3(30, 0, 30));
            if (h2Tank != null) h2Tank.TryFill(h2Tank.Capacity);

            // ⚠ **站台默认在圆轨上**（`CreateTestStation` 给的初速 ≈6.3 km/s ⇒ v_air≈5.8 km/s）
            //   ⇒ 那已经是"飞着"的状态（首轮实测：气罐被灌到 1896 kg）。要让**负对照**站得住，
            //   必须先把群速度改成**与地表共转**（v_air≈0）—— 负对照的前提是"真的没空速"，不是"环境正好没空气"。
            var mgr0 = Current.Game?.GetComponent<CelestialBodyManager>();
            if (mgr0 != null)
            {
                double omega0 = Launch.GetSurfaceState.RotationOmega(mgr0, group.CelestialBodyId);
                var pos0 = group.CurrentOrbit.positionSystem.v;
                group.CurrentOrbit.velocitySystem = (SystemVec3)SurfaceFrame.SurfaceVelocity(pos0, omega0);
                group.OrbitDirty = true;
            }

            groupId = group.GroupId;
            if (announce)
            {
                Messages.Message("有 Map 大气三件套已部署（进气道/空气罐/吸气式发动机）——停在地表 v_air≈0 ⇒ 不进气（负对照）"
                    + "｜**控制台**：选中**操舵台** → 底部 Gizmo「船上控制台」（地块图 + 读数 + 起搬 + 悬浮）",
                    MessageTypeDefOf.TaskCompletion);
            }
            return true;
        }

        /// <summary>在地图指定格子放置一个建筑，返回其 CompPropellantTank（若无则 null）。</summary>
        private static CompPropellantTank? PlaceBuilding(Map map, string defName, IntVec3 cell)        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Log.Error("[DualEngineTest] Def not found: " + defName);
                return null;
            }
            var thing = ThingMaker.MakeThing(def);
            GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish, false);
            return thing.TryGetComp<CompPropellantTank>();
        }

        /// <summary>放置建筑并指定朝向（推进器 Rot4 映射轴向用）。</summary>
        private static void PlaceBuildingRot(Map map, string defName, IntVec3 cell, Rot4 rot)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Log.Error("[SteerTest] Def not found: " + defName);
                return;
            }
            var thing = ThingMaker.MakeThing(def);
            GenSpawn.Spawn(thing, cell, map, rot, WipeMode.Vanish, false);
        }

        /// <summary>
        /// 操舵测试场景（M-c 验收）：玩家站布置操舵台 + 主推进（核热 Rot4.East → 船 +X）+
        /// 横向（Side 默认 Left）+ 垂直（Vertical Up）+ 氢罐满，自动打开操舵 HUD 与详细操舵日志。
        /// 验证点：① 按键平移沿船轴世界方向出 Δv（[Steer] 平移）；② 动量轮旋转（[Steer] 旋转）；
        /// ③ SAS 对准（[Steer] SAS→）；④ 动作组武装过滤（离子引擎 actionGroups=1）；⑤ 任意输入清空机动队列。
        /// </summary>
        public static void SpawnSteeringTest()
        {
            var (entity, group, map) = CreateTestStation();
            if (entity == null || group == null || map == null)
                return;

            // 详细操舵日志
            SpaceGameComp.DebugDetailedSteering = true;

            // 布置：操舵台 + 主推进（Rot4.East：def Back → 船 +X）+ 横向（Left）+ 垂直（Up）+ RCS + 氢罐满
            PlaceBuilding(map, "PF_Building_SteeringConsole", new IntVec3(32, 0, 30));
            PlaceBuildingRot(map, "PF_Building_Thruster_NuclearThermal", new IntVec3(28, 0, 30), Rot4.East);
            PlaceBuildingRot(map, "PF_Building_Thruster_Side", new IntVec3(28, 0, 32), Rot4.North);
            PlaceBuildingRot(map, "PF_Building_Thruster_Vertical", new IntVec3(28, 0, 34), Rot4.North);
            PlaceBuildingRot(map, "PF_Building_Thruster_Rcs", new IntVec3(28, 0, 36), Rot4.North);
            PlaceBuildingRot(map, "PF_Building_Thruster_Rcs", new IntVec3(28, 0, 38), Rot4.North);
            var tank = PlaceBuilding(map, "PF_Building_Tank_Hydrogen", new IntVec3(30, 0, 30));
            tank?.TryFill(tank.Capacity);

            var mapComp = map.GetComponent<SpaceMapComp>();
            if (mapComp == null) return;
            Log.Message($"[SteerTest] 场景就绪 Group{group.GroupId}：主推进轴={mapComp.MainThrustAxis} 六向推力(kN) F={mapComp.GetAxisTotalThrust((int)ThrustAxis.Forward) / 1000f:F1} B={mapComp.GetAxisTotalThrust((int)ThrustAxis.Back) / 1000f:F1} L={mapComp.GetAxisTotalThrust((int)ThrustAxis.Left) / 1000f:F1} R={mapComp.GetAxisTotalThrust((int)ThrustAxis.Right) / 1000f:F1} U={mapComp.GetAxisTotalThrust((int)ThrustAxis.Up) / 1000f:F1} D={mapComp.GetAxisTotalThrust((int)ThrustAxis.Down) / 1000f:F1} RCS={mapComp.RcsTotalThrust / 1000f:F1}kN 氢={mapComp.TotalPropellantMass:F0}kg");

            // 打开操舵 HUD + 跳转玩家站地图
            SteeringController.Open(group.GroupId);
            Find.WindowStack.Add(new Window_Steering());
            if (map != null)
                CameraJumper.TryJump(map.Center, map);

            Messages.Message("操舵测试场景就绪：操舵 HUD 已打开。W/S/A/D+R/F 平移、Q/E+方向键 旋转、SAS 按钮对准、1-9 动作组。日志看 [Steer]/[Align]。",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 炮塔对射测试场景（M4-F2 验收，整体式 360° 版）：
        /// ① 玩家空间站（圆轨）内布置定位机 + 整体式炮塔，自动完成绑定；
        /// ② 敌对空间站（不同 factionId）置于玩家旁 20km，真正共轨（vRel≈0）；
        /// ③ 定位机 Auto 模式自动索敌 → 360° 无射界过滤 → 齐射 → 3D 射弹命中。
        /// 验证点：目标在船体局部赤道面（y≈0）——旧半球射界下"无炮可开"，现应正常开火，
        /// 日志 [炮塔出膛] 显示六面出膛面与出膛点（本场景应命中 x+ 侧面，exitFace=1）。
        /// </summary>
        public static void SpawnTurretTest()
        {
            // 同步轨道（高度约 28,560km，周期 24h）：相对角速度极小，敌对近似共轨漂移极慢，适合长时观察
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            // 开启详细战斗日志 + 射弹探针（30 tick/条），观察出膛点/弹道/命中全链
            CombatManager.DebugDetailedCombat = true;
            CombatManager.DebugProjectileProbeInterval = 30;
            Log.Message("[TurretTest] 详细战斗日志已开启（DebugDetailedCombat + 射弹探针 30 tick/条），可随时用 Debug 组件 Gizmo 关闭");

            // 不倾斜：目标在船体局部 y≈0（赤道面），360° 射界下仍可开火（旧半球射界会全过滤）

            // 1. 布置定位机 + 整体式炮塔（单锚点）
            var designator = PlaceBuildingRaw(playerMap, "PF_Building_TargetDesignator", new IntVec3(28, 0, 30));
            var turret = PlaceBuildingRaw(playerMap, "PF_Building_Turret_Cannon", new IntVec3(30, 0, 30));
            if (designator == null || turret == null)
                return;

            // 2. 自动绑定（模拟玩家 Gizmo 绑定结果）
            var desComp = designator.TryGetComp<CompMapDesignator>();
            var turretComp = turret.TryGetComp<CompMapTurret>();
            if (desComp == null || turretComp == null)
            {
                Log.Error("[TurretTest] 定位机/炮塔 Comp 缺失");
                return;
            }
            desComp.RegisterTurret(turret);
            turretComp.Mount.boundDesignator = designator;

            // 3. 敌对空间站：玩家旁 20km，真正共轨（同高度圆轨 → 相对静止 vRel≈0，提前量 0 精确命中）。
            //    Part 尺寸按地图格子对应（1 格 = 1 米），不取巧放大目标。
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = playerGroup.IsCircularOrbit
                ? playerGroup.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe())
                : playerGroup.CurrentOrbit.positionSystem.v;


            DVector3 enemyPos = playerPos + new DVector3(20000, 0, 0);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 敌对空间站绕 Z 轴旋转 45°：射弹（世界水平 +x 来向）在船体局部系变成 45° 俯冲，
            // 命中船体上部缓冲区（y≈21m）→ 贯穿入口高度 ≈26m，炮弹从上方斜降地面（"上方 45 度挨打"）
            var enemyEntity = spaceGame.GetEntityById(enemyEntityId);
            if (enemyGroup != null)
            {
                // 姿态改走 group（entity.Rotation 由 UpdateGroupOrbit 同步），同时设 entity 保证立即生效
                enemyGroup.CurrentOrbit.rotation = (DQuaternion)Quaternion.Euler(0f, 0f, 45f);
                if (enemyEntity != null)
                    enemyEntity.SetRotation(enemyGroup.CurrentOrbit.rotation);
            }

            // 4. 跳转到玩家空间站地图看炮塔/出膛弹/开火烟雾
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[TurretTest] 场景就绪: 玩家 Group {playerGroup.GroupId} 整体式炮塔已绑定位机，敌对 Group {enemyGroupId} 位于 20km 外（赤道面），Auto 索敌启动");
            Messages.Message($"炮塔对射测试场景就绪：玩家 Group {playerGroup.GroupId}，敌对 Group {enemyGroupId} 位于 20km 外（赤道面，验证 360° 射界）。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 导弹发射测试场景（《导弹系统设计-20260830》§14 验收第 1 条）：
        /// ① 玩家空间站（圆轨）布置定位机 + 整体式炮塔，武器现场换为 PF_Weapon_MissileLauncher；
        /// ② 敌对空间站（不同 factionId）置于玩家旁 20km 共轨；
        /// ③ Manual 指定目标 + ManualFire 立即齐射 → FireWeapon Missile 分支创建领弹弹群
        ///    （出生点外推避母船 AABB / 继承母船速度 / Mission=Attack 登记 AutonomousVehicleManager）。
        /// 验证点：[导弹发射] 日志（出生点偏移/速度分解/volley/N/Mission）+ 3D 视图目视领弹出生。
        /// 注：导弹当前为巡航惯性体（制导/解离/转化交接后置任务），发射后沿轨道惯性飞行。
        /// </summary>
        public static void SpawnMissileLaunchTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            CombatManager.DebugDetailedCombat = true;
            AutonomousVehicleManager.DebugDetailedGuidance = true; // 制导详情（距离/dv/burn）10tick 限频
            SpaceGameComp.DebugDetailedThrust = true; // 物理推进日志（对准状态/用网/耗料）——点火链诊断用
            CombatManager.DebugDetailedCollision = true; // 碰撞枚举/扫掠盒重叠详情（2026-09-07 直撞线漏检诊断）
            Log.Message("[MissileTest] 详细战斗/制导/推进/碰撞日志已开启，可随时用 Debug 组件 Gizmo 关闭");

            // 1. 布置定位机 + 整体式炮塔，武器现场换成导弹发射器
            //    （正式「导弹发射器炮塔」Building Def 属后续 XML 待办，场景用 Cannon 壳替换武器）
            // 一体式导弹发射器建筑（自带定位机+炮塔，自动绑定；Auto 索敌敌对，或玩家手动指定目标）
            var launcher = PlaceBuildingRaw(playerMap, "PF_Building_MissileLauncher", new IntVec3(30, 0, 30));
            if (launcher == null)
                return;
            var launchComp = launcher.TryGetComp<CompMissileLauncher>();
            if (launchComp == null)
            {
                Log.Error("[MissileTest] 导弹发射器 Comp 缺失");
                return;
            }

            // 2. 敌对空间站：玩家旁 20km 共轨（索敌射程取武器 orbitalEngageRange 默认 100km）
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = playerGroup.IsCircularOrbit
                ? playerGroup.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe())
                : playerGroup.CurrentOrbit.positionSystem.v;
            // 异轨靶（2026-09-07：同轨正后方追尾=纯切向，任何推进都抬轨逃逸，物理打不中；
            // 真实交战目标异轨，视线含径向分量，推力直扑可打——19:53 异轨轮次命中实证）。
            // 位置 = 沿母站轨道切向 20km + 径向抬轨 30km → 入轨后不同半径不同周期 = 轨道不同步
            DVector3 sameOrbitPoint = SameOrbitPointOffset(playerPos, GetGroupVelDebug(playerGroup), 20000);
            DVector3 enemyPos = sameOrbitPoint + (sameOrbitPoint / sameOrbitPoint.magnitude) * 30000.0;
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（导弹靶）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 3. Manual 指定目标 + 立即齐射（不等 scanInterval 自动索敌；齐射沿物理步对齐，奇 tick 调用会被跳拍后由 autoFire 补发）
            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();

            // 4. 跳转到玩家空间站地图观察领弹出生
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[MissileTest] 场景就绪: 玩家 Group {playerGroup.GroupId}（导弹发射器炮塔已绑定）→ 敌对 Group {enemyGroupId} 位于 20km 外，ManualFire 已触发");
            Messages.Message($"导弹发射测试场景就绪：玩家 Group {playerGroup.GroupId}，敌对 Group {enemyGroupId} 位于 20km 外（观察 [导弹发射] 日志与 3D 视图领弹出生）。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 导弹撞击测试场景（《导弹系统设计-20260830》§14 验收「命中语义」：撞上才爆）：
        /// 同 SpawnMissileLaunchTest，但敌对空间站置于 1.5km 近距——同轨近邻横向相对速度 ≈0，
        /// 领弹以 150m/s 出膛增量出膛（制导加速接管），数秒内必然撞上目标 → 转化交接：
        /// [导弹撞击]（先生成弹头射弹、后销毁导弹）→ [弹头命中]（part 直扣 + Map 落点贯穿 + 3D AoE）。
        /// 验证点：撞击才爆炸（无"地板/平面即命中"）、弹头射弹沿导弹速度方向命中不偏深、
        /// 导弹登记注销（[AutonomousVehicle] 注销）。可用炮塔对射场景打飞行导弹验证拦截引爆。
        /// </summary>
        public static void SpawnMissileImpactTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            CombatManager.DebugDetailedCombat = true;
            AutonomousVehicleManager.DebugDetailedGuidance = true; // 制导详情（距离/dv/burn）10tick 限频
            Log.Message("[MissileImpactTest] 详细战斗日志已开启（DebugDetailedCombat），可随时用 Debug 组件 Gizmo 关闭");

            // 1. 定位机 + 整体式炮塔（武器现场换成导弹发射器，同 SpawnMissileLaunchTest）
            // 一体式导弹发射器建筑（自带定位机+炮塔，自动绑定；Auto 索敌敌对，或玩家手动指定目标）
            var launcher = PlaceBuildingRaw(playerMap, "PF_Building_MissileLauncher", new IntVec3(30, 0, 30));
            if (launcher == null)
                return;
            var launchComp = launcher.TryGetComp<CompMissileLauncher>();
            if (launchComp == null)
            {
                Log.Error("[MissileImpactTest] 导弹发射器 Comp 缺失");
                return;
            }

            // 2. 敌对空间站：玩家旁 1.5km 共轨（撞击窗口 ~5s；20km 场景用于发射验证，本场景用于命中验证）
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = playerGroup.IsCircularOrbit
                ? playerGroup.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe())
                : playerGroup.CurrentOrbit.positionSystem.v;
            // 同轨靶（严格同半径共轨，相对静止）：沿母站轨道切向偏移——世界系偏移在任意轨道相位
            // 会引入径向分量导致异轨（半径差 km 级），靶漂移+瞄准错乱（22:43 会话实证）
            DVector3 enemyPos = SameOrbitPointOffset(playerPos, GetGroupVelDebug(playerGroup), 1500);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（导弹靶 1.5km）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 3. Manual 指定目标 + 立即齐射（齐射沿物理步对齐，奇 tick 调用会被跳拍后由 autoFire 补发）
            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();
            autoCmdLauncherGroupId = playerGroup.GroupId; // 导弹出生即自动下令撞击（模拟玩家出膛下令）

            // 4. 跳转到玩家空间站地图观察撞击爆炸
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[MissileImpactTest] 场景就绪: 玩家 Group {playerGroup.GroupId} → 敌对 Group {enemyGroupId} 位于 1.5km 外，ManualFire 已触发，预计 ~5s 撞击");
            Messages.Message($"导弹撞击测试场景就绪：玩家 Group {playerGroup.GroupId}，敌对 Group {enemyGroupId} 位于 1.5km 外（观察 [导弹撞击] → [弹头命中] 日志与 3D 爆炸）。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 导弹巡航→终末转移测试（制导巡航分支验证）：炮塔换装 PF_Weapon_MissileLauncher_CruiseTest
        /// （弹体 PF_Missile_TestCruise：homingEngageDistance=500m），敌对靶 1.5km——
        /// 导弹出生表面距 1.5km &gt; 500m 边界 → 巡航不点火（[导弹巡航]，惯性接近）→
        /// 惯性接近至 ~500m 表面距 → [导弹制导] 终末接管 → 火炬冲刺 → 撞击全链（~10s 内）。
        /// </summary>
        public static void SpawnMissileCruiseTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            CombatManager.DebugDetailedCombat = true;
            AutonomousVehicleManager.DebugDetailedGuidance = true; // 制导详情（距离/dv/burn）10tick 限频

            // 一体式导弹发射器建筑（自带定位机+炮塔，自动绑定；Auto 索敌敌对，或玩家手动指定目标）
            var launcher = PlaceBuildingRaw(playerMap, "PF_Building_MissileLauncher", new IntVec3(30, 0, 30));
            if (launcher == null)
                return;
            var launchComp = launcher.TryGetComp<CompMissileLauncher>();
            if (launchComp == null)
            {
                Log.Error("[MissileCruiseTest] 导弹发射器 Comp 缺失");
                return;
            }
            // 巡航测试换弹体（homing 500m）：正式建筑默认标准弹，本场景现场换装测试武器
            var cruiseWeaponDef = DefDatabase<WeaponDef>.GetNamedSilentFail("PF_Weapon_MissileLauncher_CruiseTest");
            if (cruiseWeaponDef == null)
            {
                Log.Error("[MissileCruiseTest] PF_Weapon_MissileLauncher_CruiseTest 武器 Def 缺失");
                return;
            }
            launchComp.Weapon.def = cruiseWeaponDef;

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = GetGroupPosDebug(playerGroup);
            // 同轨靶（严格同半径共轨，相对静止）：沿母站轨道切向偏移——世界系偏移在任意轨道相位
            // 会引入径向分量导致异轨（半径差 km 级），靶漂移+瞄准错乱（22:43 会话实证）
            DVector3 enemyPos = SameOrbitPointOffset(playerPos, GetGroupVelDebug(playerGroup), 1500);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（巡航测试靶）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();
            autoCmdLauncherGroupId = playerGroup.GroupId; // 导弹出生即自动下令撞击（模拟玩家出膛下令）
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[MissileCruiseTest] 场景就绪: 玩家 Group {playerGroup.GroupId} → 敌对 Group {enemyGroupId} 位于 1.5km 外（巡航边界 500m），预期日志链：巡航 → 终末接管 → 撞击");
            Messages.Message("巡航→终末转移测试就绪（1.5km 靶，巡航边界 500m）：观察 [导弹巡航] → [导弹制导 终末接管] → [导弹撞击]。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 弹群齐射·解离·错峰撞击测试（§5 思路 A，2026-09-08）：
        /// 玩家站导弹发射器 8 连发（深度队列：同深度 1 枚、间距 2km = 安全格距且 &gt; 弹头爆炸半径 1.5km、
        /// 队长 14km）→ 敌对站位于出膛方向 ~25.3km（队首距靶 ~11.3km 收敛余量充足、队尾 25.3km）→
        /// autoCmd 立即下 Attack → 合并群（1 group ×8，每枚满罐）表面距 ≤ homing → [弹群解离] 拆 8 个
        /// 单实体 group → 各枚按队列错峰终末（[导弹制导 终末接管] 距离递增）→ 逐枚 [导弹撞击]→[弹头命中]
        /// （前弹爆炸冲量作用半径 1.5km &lt; 队列间距 2km → 后到兄弟既不自伤也不被侧推打乱队形）。
        /// 验证点：[导弹弹群] N=8 队列日志 / 面板 ×8 单枚燃料 2000kg / [弹群解离] / 8× 错峰撞击无自伤 /
        /// 每枚独立制导均命中（聚合器分账正确性由面板单枚燃料 + 燃尽日志佐证）。
        /// </summary>
        public static void SpawnVolleyImpactTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            CombatManager.DebugDetailedCombat = true;
            CombatManager.DebugDetailedCollision = true; // [弹群接触] 空当放行对照
            AutonomousVehicleManager.DebugDetailedGuidance = true;
            // 注：不开 DebugDetailedThrust——Combat 慢放下多弹同时燃烧会按物理步刷屏拖垮内存（09-07 教训）
            Log.Message("[VolleyImpactTest] 详细战斗/碰撞/制导日志已开启（推进日志未开）");

            var launcher = PlaceBuildingRaw(playerMap, "PF_Building_MissileLauncher", new IntVec3(30, 0, 30));
            if (launcher == null)
                return;
            var launchComp = launcher.TryGetComp<CompMissileLauncher>();
            if (launchComp == null)
            {
                Log.Error("[VolleyImpactTest] 导弹发射器 Comp 缺失");
                return;
            }
            launchComp.SetVolleyCount(8); // 弹群 8 连发（深度队列安全容量）

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = GetGroupPosDebug(playerGroup);
            // 异轨靶（同轨正后方追尾物理打不中——径向分量是直扑几何前提，既有场景同口径）：
            // 切向 24km + 径向抬轨 8km → 斜距 ~25.3km；队列长 14km（间距 2km）→ 队首距靶 ~11.3km
            // （2026-09-08 三轮实测：靶 15.2km 时队首仅 1.2km 余量收敛不及擦过 → 拉远靶距）
            DVector3 sameOrbitPoint = SameOrbitPointOffset(playerPos, GetGroupVelDebug(playerGroup), 24000);
            DVector3 enemyPos = sameOrbitPoint + (sameOrbitPoint / sameOrbitPoint.magnitude) * 8000.0;
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（弹群靶 ~25km）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();
            autoCmdLauncherGroupId = playerGroup.GroupId; // 出膛即自动下 Attack（进终末 → 解离）
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[VolleyImpactTest] 场景就绪: 玩家 G{playerGroup.GroupId} 弹群 N=8 → 敌对 G{enemyGroupId} ~25.3km。预期日志链：[导弹弹群] N=8 队列 → [弹群解离]（含错峰时隙）→ 8×[导弹制导 终末接管]（距离递增）→ 8×[导弹撞击]/[弹头命中]（错峰点火保证到达间隔 ≥ 间距/巡航，无兄弟爆炸误杀）");
            Messages.Message($"弹群齐射测试就绪：N=8 深度队列 → 敌对 G{enemyGroupId} ~25.3km（观察 [导弹弹群]/[弹群解离]/8× 错峰撞击日志）。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 弹群合并期被撞击测试（新路径：多实体 group 逐实体接触解析，2026-09-08）：
        /// 玩家站发射弹群 N=4（Following 保持合并，不下 Attack——合并群停在发射器附近 coast）；
        /// 敌对站 6km 外发射 1 枚敌弹 Attack 目标 = 该合并弹群 → 敌弹终末冲刺撞入编队 →
        /// pair 双 pass 逐实体解析（ResolveEntityContacts）：敌弹单枚转化（自毁）+ 弹群被撞那枚
        /// 独立转化离群（[弹群] E.. 离群，N 4→3，燃料随实体消失无记账）→ 弹群其余兄弟继续合并巡航。
        /// 验证点：[导弹弹群] N=4 → 敌弹终末 → 两条单枚 [导弹撞击]（弹群侧含 E..）+ [弹群] 离群日志；
        /// 面板该弹群 ×3；随后玩家可用「命令全体导弹」/面板对剩余弹群下 Attack 观察解离+错峰撞击。
        /// </summary>
        public static void SpawnVolleyMergedHitTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            CombatManager.DebugDetailedCombat = true;
            CombatManager.DebugDetailedCollision = true;
            AutonomousVehicleManager.DebugDetailedGuidance = true;
            Log.Message("[VolleyMergedHitTest] 详细战斗/碰撞/制导日志已开启");

            var launcher = PlaceBuildingRaw(playerMap, "PF_Building_MissileLauncher", new IntVec3(30, 0, 30));
            if (launcher == null)
                return;
            var launchComp = launcher.TryGetComp<CompMissileLauncher>();
            if (launchComp == null)
            {
                Log.Error("[VolleyMergedHitTest] 导弹发射器 Comp 缺失");
                return;
            }
            launchComp.SetVolleyCount(4); // 合并弹群（不下 Attack = 保持合并巡航）

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = GetGroupPosDebug(playerGroup);
            // 敌站 6km（异轨小径向差）：探针敌弹从敌站侧撞入停驻在玩家站附近的合并弹群
            DVector3 sameOrbitPoint = SameOrbitPointOffset(playerPos, GetGroupVelDebug(playerGroup), 6000);
            DVector3 enemyPos = sameOrbitPoint + (sameOrbitPoint / sameOrbitPoint.magnitude) * 2000.0;
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（合并弹群撞击探针）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 发射弹群（Following：不 autoCmd）→ 合并停驻
            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();

            // 找到刚发射的合并弹群（本发射器释放、mission=Following、logicalCount=4）
            var avm = Current.Game?.GetComponent<AutonomousVehicleManager>();
            int volleyGroupId = -1;
            if (avm != null)
            {
                var recs = avm.Vehicles;
                for (int i = 0; i < recs.Count; i++)
                {
                    var rec = recs[i];
                    if (rec.launcherGroupId != playerGroup.GroupId) continue;
                    if (rec.mission != AutonomousVehicleManager.MissionType.Following) continue;
                    if (rec.logicalCount > 1) { volleyGroupId = rec.groupId; break; }
                }
            }
            if (volleyGroupId < 0)
            {
                Log.Error("[VolleyMergedHitTest] 未找到合并弹群记录（Following ×N>1）");
                return;
            }

            // 敌弹探针：敌站远侧 4km（背向弹群）出生，Attack 目标 = 合并弹群（从外撞入编队，
            // 出生点远离队列避免重叠——弹群 Following coast 停在玩家站前 ~3km 队列内）
            DVector3 volleyPos = GetGroupPosDebug(spaceGame.GetGroupById(volleyGroupId));
            DVector3 enemyPos2 = GetGroupPosDebug(enemyGroup);
            DVector3 dirToVolley = volleyPos - enemyPos2;
            double d = dirToVolley.magnitude;
            if (d < 1.0) return;
            dirToVolley /= d;
            DVector3 probePos = enemyPos2 - dirToVolley * 4000.0; // 敌站远侧（离弹群更远，追击路径清晰）
            DVector3 probeVel = GetGroupVelDebug(enemyGroup) + dirToVolley * 150.0;
            CreateTestMissile("PF_Missile_Standard", "PF_Weapon_MissileLauncher", enemyFactionId,
                probePos, probeVel, enemyGroup.CelestialBodyId, dirToVolley, volleyGroupId);
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[VolleyMergedHitTest] 场景就绪: 玩家 G{playerGroup.GroupId} 合并弹群 G{volleyGroupId}(N=4 Following) ← 敌站 G{enemyGroupId} 探针敌弹（Attack 目标=弹群）。预期：敌弹终末撞入编队 → 两条单枚 [导弹撞击] + [弹群] E.. 离群 N 4→3（兄弟继续合并）");
            Messages.Message($"合并弹群撞击测试就绪：探针敌弹即将撞入 G{volleyGroupId} 弹群（观察单枚 [导弹撞击]/[弹群] 离群日志；之后用面板/「命令全体导弹」下 Attack 看解离）。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 导弹长距制导验证场景（60km+ 靶，2026-09-08 制导改良验收）：
        /// 玩家站发射 N=4 弹群（间距 2km &gt; 弹头爆炸半径 1.5km）→ 敌对站位于切向 55km + 径向 25km
        /// （斜距 ~60km，仍在 homing 100km 内）→ 出膛即进终末段 → 收敛制导全程火炬（~450m/s）
        /// → 逐枚错峰撞击（前弹爆炸冲量作用不到 2km 外的后弹）。
        /// 验证点：① 长距 tGo 充裕时命中率应显著高于 11km 弹群场景（制导改良有效性）；
        /// ② [导弹制导] 日志中冲刺量随横差自适应（横差大时 ≈0，不再无脑 450）；③ 零冲量扰动。
        /// 注：60km @ ~450m/s ≈ 133 游戏秒（Combat 档 ≈133 现实秒）——长距验证的固有代价，比其他场景慢。
        /// </summary>
        public static void SpawnMissileLongRangeTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            CombatManager.DebugDetailedCombat = true;
            AutonomousVehicleManager.DebugDetailedGuidance = true;
            Log.Message("[MissileLongRangeTest] 详细战斗/制导日志已开启（推进日志未开，防刷屏）");

            var launcher = PlaceBuildingRaw(playerMap, "PF_Building_MissileLauncher", new IntVec3(30, 0, 30));
            if (launcher == null)
                return;
            var launchComp = launcher.TryGetComp<CompMissileLauncher>();
            if (launchComp == null)
            {
                Log.Error("[MissileLongRangeTest] 导弹发射器 Comp 缺失");
                return;
            }
            launchComp.SetVolleyCount(4);

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = GetGroupPosDebug(playerGroup);
            // 长距靶：切向 55km + 径向 25km（斜距 ~60km）——异轨（径向分量是直扑几何前提），
            // 仍在 homing 100km 内 → 出膛即终末段，全程火炬收敛
            DVector3 sameOrbitPoint = SameOrbitPointOffset(playerPos, GetGroupVelDebug(playerGroup), 55000);
            DVector3 enemyPos = sameOrbitPoint + (sameOrbitPoint / sameOrbitPoint.magnitude) * 25000.0;
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（长距靶 ~60km）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();
            autoCmdLauncherGroupId = playerGroup.GroupId; // 出膛即自动下 Attack
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[MissileLongRangeTest] 场景就绪: 玩家 G{playerGroup.GroupId} 弹群 N=4 → 敌对 G{enemyGroupId} ~60km。预期：[导弹制导 终末接管]（表面距 ~60km）→ 全程收敛冲刺 → 4× 错峰 [导弹撞击]/[弹头命中]（间距 2km > 爆炸半径 1.5km，零冲量扰动）。~133 游戏秒后到达");
            Messages.Message($"长距制导验证就绪：N=4 弹群 → 敌对 G{enemyGroupId} ~60km（观察制导收敛与 4× 错峰命中；约 133 游戏秒）。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 导弹拦截对撞测试（异 volley 互撞 + K6 目标消失自动验证）：
        /// ① 敌对空间站 20km（CreateStation，faction=玩家+1），由本 Comp 定时发射 3 波敌弹
        ///    （敌弹 = 完整导弹：CreateMissileGroup + manager 登记，Attack 目标 = 玩家站）；
        /// ② 玩家站部署导弹炮塔 + 定位机（Auto 索敌）：敌弹接近后比敌站更近 → 自动锁定敌弹 → 反导导弹；
        /// ③ 敌弹与反导导弹同线相向（相对 ~600m/s）→ 必然对撞：异 volley → 双方各自转化（双 [导弹撞击]）；
        /// ④ 反导冷却 5s &lt; 敌弹波次间隔 7s → 迟发反导弹飞行途中目标（敌弹）已被前弹撞毁 →
        ///    [导弹中止] 回 Standby（K6 自动出现）。
        /// 可用 DebugHub「销毁来袭弹」按钮手动销毁敌弹即时验证 K6。
        /// </summary>
        public static void SpawnMissileInterceptTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            CombatManager.DebugDetailedCombat = true;
            AutonomousVehicleManager.DebugDetailedGuidance = true;
            SpaceGameComp.DebugDetailedThrust = true;
            Log.Message("[MissileInterceptTest] 已开启详细战斗/制导/推进日志，可随时关闭");

            // 1. 玩家反导炮塔 + 定位机（Auto 索敌：敌弹接近后自动锁定）
            // 一体式导弹发射器建筑（自带定位机+炮塔，自动绑定；Auto 索敌敌对，或玩家手动指定目标）
            var launcher = PlaceBuildingRaw(playerMap, "PF_Building_MissileLauncher", new IntVec3(30, 0, 30));
            if (launcher == null)
                return;
            var launchComp = launcher.TryGetComp<CompMissileLauncher>();
            if (launchComp == null)
            {
                Log.Error("[MissileInterceptTest] 导弹发射器 Comp 缺失");
                return;
            }

            // 2. 敌对空间站 20km（faction = 玩家+1）
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = GetGroupPosDebug(playerGroup);
            // 异轨靶（2026-09-07：同轨正后方追尾=纯切向，任何推进都抬轨逃逸，物理打不中；
            // 真实交战目标异轨，视线含径向分量，推力直扑可打——19:53 异轨轮次命中实证）。
            // 位置 = 沿母站轨道切向 20km + 径向抬轨 30km → 入轨后不同半径不同周期 = 轨道不同步
            DVector3 sameOrbitPoint = SameOrbitPointOffset(playerPos, GetGroupVelDebug(playerGroup), 20000);
            DVector3 enemyPos = sameOrbitPoint + (sameOrbitPoint / sameOrbitPoint.magnitude) * 30000.0;
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（来袭导弹基地）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 3. 拦截测试状态：3 波敌弹由 CompTick 定时发射（2s 后首波）
            interceptEnemyFactionId = enemyFactionId;
            interceptPlayerGroupId = playerGroup.GroupId;
            interceptEnemyGroupId = enemyGroupId;
            interceptWavesLeft = 3;
            interceptNextWaveTick = Find.TickManager.TicksGame + 120;
            interceptTestActive = true;
            autoCmdLauncherGroupId = playerGroup.GroupId; // 反导弹出生即自动下令拦截（目标=发射器锁定的敌弹）
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[MissileInterceptTest] 场景就绪: 玩家 G{playerGroup.GroupId} 反导炮塔已绑定 → 敌对 G{enemyGroupId} 位于 20km 外（3 波敌弹 2s/9s/16s 来袭），敌弹接近后反导自动锁定。预期：异 volley 对撞双 [导弹撞击] + 迟发反导弹 [导弹中止]（K6）");
            Messages.Message($"拦截对撞测试就绪：敌对 G{enemyGroupId} 位于 20km 外，3 波敌弹即将来袭（观察对撞双爆与 [导弹中止]）。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>拦截测试敌弹波次：从敌站外侧朝玩家站发射一枚完整敌对导弹（不依赖敌站地图 tick）。</summary>
        public static void FireEnemyMissileWave()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var enemyGroup = spaceGame?.GetGroupById(interceptEnemyGroupId);
            var playerGroup = spaceGame?.GetGroupById(interceptPlayerGroupId);
            if (spaceGame == null || enemyGroup == null || playerGroup == null || interceptEnemyFactionId < 0)
            {
                Log.Warning("[拦截测试] 敌弹波次发射失败：站 group 缺失（可能已被销毁），终止波次");
                interceptTestActive = false;
                return;
            }

            DVector3 enemyPos = GetGroupPosDebug(enemyGroup);
            DVector3 playerPos = GetGroupPosDebug(playerGroup);
            DVector3 toPlayer = playerPos - enemyPos;
            double dist = toPlayer.magnitude;
            if (dist < 1.0) return;
            DVector3 dir = toPlayer / dist;

            // 出生点：敌站视线前方 1km（> 敌站 AABB 扫掠带；且距玩家站 19km < 敌站 20km，
            // 保证反导 Auto 索敌优先锁定来袭导弹而非敌站）；速度 = 敌站速度 + 150m/s 出膛增量（同正式武器口径）
            DVector3 birthPos = enemyPos + dir * 1000.0;
            DVector3 birthVel = GetGroupVelDebug(enemyGroup) + dir * 150.0;
            var group = CreateTestMissile("PF_Missile_Standard", "PF_Weapon_MissileLauncher", interceptEnemyFactionId,
                birthPos, birthVel, enemyGroup.CelestialBodyId, dir, interceptPlayerGroupId);
            if (group != null)
                Log.Message($"[拦截测试] 敌弹波次：G{group.GroupId} 敌站G{interceptEnemyGroupId}→玩家G{interceptPlayerGroupId} 距离={dist / 1000.0:F1}km");
        }

        /// <summary>
        /// 测试弹工厂（场景用）：CombatManager.CreateMissileGroup 建实体 + AutonomousVehicleManager 登记，
        /// 与发射链同参数语义（volleyId 独立、launcherGroupId=-1 表示非炮塔来源）。
        /// </summary>
        private static SpaceEntityGroup? CreateTestMissile(string missileDefName, string weaponDefName, int factionId,
            DVector3 birthPos, DVector3 birthVel, int celestialBodyId, DVector3 birthDir, int? targetGroupId)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var combatMgr = Current.Game?.GetComponent<CombatManager>();
            var avm = Current.Game?.GetComponent<AutonomousVehicleManager>();
            if (spaceGame == null || combatMgr == null || avm == null)
            {
                Log.Error("[导弹测试] SpaceGameComp/CombatManager/AutonomousVehicleManager 缺失");
                return null;
            }
            var missileDef = DefDatabase<MissileDef>.GetNamedSilentFail(missileDefName);
            if (missileDef == null)
            {
                Log.Error("[导弹测试] MissileDef '" + missileDefName + "' 未找到");
                return null;
            }

            var group = combatMgr.CreateMissileGroup(missileDef, factionId, birthPos, birthVel,
                celestialBodyId, birthDir, out int entityId);
            int volleyId = avm.AllocVolleyId();
            var mission = targetGroupId.HasValue
                ? AutonomousVehicleManager.MissionType.Attack
                : AutonomousVehicleManager.MissionType.Standby;
            avm.RegisterMissileLeader(group.GroupId, entityId, volleyId, -1, 1,
                missileDefName, weaponDefName, mission, targetGroupId ?? -1);
            return group;
        }

        /// <summary>销毁当前全部敌对来袭导弹（DebugHub 按钮：K6 目标消失即时验证——正在追敌弹的反导弹会 [导弹中止] 回 Standby）。</summary>
        internal static void DestroyIncomingMissiles()
        {
            if (interceptEnemyFactionId < 0)
            {
                Messages.Message("未部署拦截测试场景（先点「拦截对撞」）！", MessageTypeDefOf.RejectInput);
                return;
            }
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var avm = Current.Game?.GetComponent<AutonomousVehicleManager>();
            if (spaceGame == null || avm == null) return;

            var targets = new List<int>();
            var recs = avm.Vehicles;
            for (int i = 0; i < recs.Count; i++)
            {
                var rec = recs[i];
                if (rec.mission != AutonomousVehicleManager.MissionType.Attack) continue;
                var g = spaceGame.GetGroupById(rec.groupId);
                if (g != null && g.FactionId == interceptEnemyFactionId)
                    targets.Add(rec.groupId);
            }
            if (targets.Count == 0)
            {
                Messages.Message("当前没有在飞的敌对来袭导弹。", MessageTypeDefOf.RejectInput);
                return;
            }
            foreach (int gid in targets)
            {
                avm.UnregisterGroup(gid);
                spaceGame.DestroyGroup(gid);
                Log.Message($"[拦截测试] 手动销毁来袭导弹 G{gid}");
            }
            Messages.Message($"已销毁 {targets.Count} 枚来袭导弹——正在追击它们的反导弹应中止回 Standby（[导弹中止]）。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 全方位炮塔对射测试场景（2026-08-29，整体式 360° 验证）：
        /// 6 座整体式炮塔各配一台定位机（Manual 模式各指一个敌站），敌站分布在玩家船六个方位（20km）：
        ///   ±x / ±z（赤道面，共轨）→ 期望出膛面 1/0/5/4（四侧面）
        ///   ±y（轨道面法向）→ 期望出膛面 3/2（上/下表面）
        /// 一次齐射即可通过 [炮塔出膛] 日志验证：360° 射界解除（赤道面旧半球逻辑无炮可开）+
        /// 六面出膛碰撞点准确性。玩家船不旋转（船体局部 = 世界轴，方位对应直白）。
        /// </summary>
        public static void SpawnTurretOmniTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            // 详细战斗日志 + 射弹探针：追踪 6 发齐射的出膛/弹道/命中全链
            CombatManager.DebugDetailedCombat = true;
            CombatManager.DebugProjectileProbeInterval = 30;

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            DVector3 playerPos = playerGroup.IsCircularOrbit
                ? playerGroup.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe())
                : playerGroup.CurrentOrbit.positionSystem.v;

            // 六方位敌站（20km）→ 期望出膛面（0x- 1x+ 2y- 3y+ 4z- 5z+）
            var directions = new (string name, DVector3 offset, int expectFace)[]
            {
                ("敌站+x", new DVector3(20000, 0, 0), 1),
                ("敌站-x", new DVector3(-20000, 0, 0), 0),
                ("敌站+z", new DVector3(0, 0, 20000), 5),
                ("敌站-z", new DVector3(0, 0, -20000), 4),
                ("敌站+y", new DVector3(0, 20000, 0), 3),
                ("敌站-y", new DVector3(0, -20000, 0), 2),
            };

            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            var enemyGroupIds = new List<int>();
            string mapping = "";
            foreach (var d in directions)
            {
                int gid = spaceGame.AllocGroupId();
                int eid = spaceGame.AllocEntityId();
                spaceGame.CreateStation(eid, gid, enemyFactionId, playerPos + d.offset, 60, 60, d.name);
                var g = spaceGame.GetGroupById(gid);
                if (g != null)
                    g.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);
                enemyGroupIds.Add(gid);
                mapping += $"G{gid}={d.name}(期望面{d.expectFace}) ";
            }

            // 6 组：整体式炮塔 + 定位机（Manual 各指一个敌站），自动完成绑定
            var turretCells = new[]
            {
                new IntVec3(29, 0, 29), new IntVec3(30, 0, 29), new IntVec3(31, 0, 29),
                new IntVec3(29, 0, 30), new IntVec3(30, 0, 30), new IntVec3(31, 0, 30),
            };
            var desCells = new[]
            {
                new IntVec3(28, 0, 28), new IntVec3(29, 0, 28), new IntVec3(30, 0, 28),
                new IntVec3(28, 0, 31), new IntVec3(29, 0, 31), new IntVec3(30, 0, 31),
            };
            var designators = new List<CompMapDesignator>();
            for (int i = 0; i < directions.Length; i++)
            {
                var des = PlaceBuildingRaw(playerMap, "PF_Building_TargetDesignator", desCells[i]);
                var turret = PlaceBuildingRaw(playerMap, "PF_Building_Turret_Cannon", turretCells[i]);
                var desComp = des?.TryGetComp<CompMapDesignator>();
                var turretComp = turret?.TryGetComp<CompMapTurret>();
                if (desComp == null || turretComp == null || turretComp.Mount == null)
                {
                    Log.Error("[OmniTurretTest] 第 " + i + " 组定位机/炮塔 Comp 缺失");
                    continue;
                }
                desComp.DebugSetManualTarget(enemyGroupIds[i]);
                desComp.RegisterTurret(turret);
                turretComp.Mount.boundDesignator = des;
                designators.Add(desComp);
            }

            // 精确炮击验证：G2(+x 敌站) 的定位机瞄准船心偏移格 (40,30)（60×60 站中心(30,30) → 局部 +10m）
            // 该组炮塔出膛方向将朝 G2 的 +10m 偏移点，命中落点格应落在 (40,30) 附近。
            if (designators.Count > 0)
            {
                designators[0].DebugSetPreciseStrike(enemyGroupIds[0], new IntVec3(40, 0, 30));
                Log.Message($"[OmniTurretTest] G{enemyGroupIds[0]} 已设精确炮击点 (40,30)：瞄准偏移船心 +10m（看 [精确炮击]/[炮塔出膛]/[贯穿生成] 日志）");
            }

            // 跳转玩家地图看 6 发齐射出膛弹
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[OmniTurretTest] 场景就绪: 玩家 G{playerGroup.GroupId}，6 炮塔各指一个敌站：{mapping}");
            Messages.Message("全方位炮塔对射场景就绪：6 敌站分布六方位，6 炮塔齐射验证 360° 射界与六面出膛（看日志 [炮塔出膛]）。",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>在地图指定格子放置一个建筑，返回 Thing（无 faction，测试用）。</summary>
        private static Thing? PlaceBuildingRaw(Map map, string defName, IntVec3 cell)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Log.Error("[TurretTest] Def not found: " + defName);
                return null;
            }
            var thing = ThingMaker.MakeThing(def);
            GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish, false);
            return thing;
        }

        /// <summary>
        /// 精确炮击测试场景（2026-08-29）：目标敌舰位于玩家船正下方（世界 -y，20km）。
        /// 验证点：
        ///   ① 正下方目标 → 出膛面 y-（面2），出膛点局部 (≈0,-25,≈0)；
        ///   ② 精确炮击点 (40,30)（60×60 站中心(30,30) → 局部偏移 +10m x）→ [精确炮击] 日志显示偏移；
        ///   ③ 敌舰地图 (40,30) 上出现红色精确炮击标记；
        ///   ④ 命中落点格 ≈ (40,30)（[贯穿生成] 验证）。
        /// </summary>
        public static void SpawnTurretPrecisionTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            // 详细战斗日志 + 射弹探针
            CombatManager.DebugDetailedCombat = true;
            CombatManager.DebugProjectileProbeInterval = 30;

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            DVector3 playerPos = playerGroup.IsCircularOrbit
                ? playerGroup.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe())
                : playerGroup.CurrentOrbit.positionSystem.v;

            // 敌舰：玩家船正下方（世界 -y）20km → 船体局部 -y → 出膛面 y-（面2）
            DVector3 enemyPos = playerPos + new DVector3(0, -20000, 0);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "正下方敌站");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 定位机 + 整体式炮塔，自动绑定
            var designator = PlaceBuildingRaw(playerMap, "PF_Building_TargetDesignator", new IntVec3(28, 0, 30));
            var turret = PlaceBuildingRaw(playerMap, "PF_Building_Turret_Cannon", new IntVec3(30, 0, 30));
            var desComp = designator?.TryGetComp<CompMapDesignator>();
            var turretComp = turret?.TryGetComp<CompMapTurret>();
            if (desComp == null || turretComp == null || turretComp.Mount == null)
            {
                Log.Error("[PrecisionTest] 定位机/炮塔 Comp 缺失");
                return;
            }
            desComp.RegisterTurret(turret);
            turretComp.Mount.boundDesignator = designator;

            // 精确炮击点：敌舰地图格 (40,30)（60×60 中心(30,30) → 局部偏移 +10m x），自动切 Manual 锁定目标
            desComp.DebugSetPreciseStrike(enemyGroupId, new IntVec3(40, 0, 30));

            // 跳转敌舰地图看红色精确炮击标记
            var enemyEntity = spaceGame.GetEntityById(enemyEntityId);
            if (enemyEntity != null && enemyEntity.MapId >= 0)
            {
                Map enemyMap = SpaceMapGenerator.FindPocketMap(enemyEntity.MapId);
                if (enemyMap != null)
                    CameraJumper.TryJump(enemyMap.Center, enemyMap);
            }

            Log.Message($"[PrecisionTest] 场景就绪: 玩家 G{playerGroup.GroupId}，敌舰 G{enemyGroupId} 位于正下方 20km，精确炮击点 (40,30)（船心偏移 +10m），Auto 索敌启动");
            Messages.Message("精确炮击测试场景就绪：目标在正下方（期望出膛面 y-），精确炮击点 (40,30)，敌舰地图上有红色标记。",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 定向能对射测试场景（2026-08-29）：玩家站定位机 Auto + 激光炮塔，敌对站 20km 赤道面（vRel≈0）。
        /// 验证点（日志链）：[齐射详情] → [炮塔出膛] → [激光发射]（射程 50km）→ [激光命中!]（Part HP 下降）
        /// → [贯穿生成]（贯通入口→落点格）→ 光束 60 tick 残留。
        /// </summary>
        public static void SpawnRayTurretTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            // 详细战斗日志
            CombatManager.DebugDetailedCombat = true;
            CombatManager.DebugProjectileProbeInterval = 30;

            // 1. 定位机（Auto）+ 激光炮塔，自动绑定
            var designator = PlaceBuildingRaw(playerMap, "PF_Building_TargetDesignator", new IntVec3(28, 0, 30));
            var turret = PlaceBuildingRaw(playerMap, "PF_Building_Turret_Laser", new IntVec3(30, 0, 30));
            if (designator == null || turret == null)
            {
                Log.Error("[RayTurretTest] 定位机/激光炮塔 Def 缺失");
                return;
            }
            var desComp = designator.TryGetComp<CompMapDesignator>();
            var turretComp = turret.TryGetComp<CompMapTurret>();
            if (desComp == null || turretComp == null || turretComp.Mount == null)
            {
                Log.Error("[RayTurretTest] 定位机/炮塔 Comp 缺失");
                return;
            }
            desComp.RegisterTurret(turret);
            turretComp.Mount.boundDesignator = designator;

            // 2. 敌对空间站：玩家旁 20km，真正共轨（同高度圆轨 → 相对静止 vRel≈0，即时命中精准）
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = playerGroup.IsCircularOrbit
                ? playerGroup.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe())
                : playerGroup.CurrentOrbit.positionSystem.v;
            DVector3 enemyPos = playerPos + new DVector3(20000, 0, 0);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 3. 跳转玩家地图看激光炮塔
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[RayTurretTest] 场景就绪: 玩家 Group {playerGroup.GroupId} 激光炮塔已绑定位机，敌对 Group {enemyGroupId} 位于 20km 外（赤道面），Auto 索敌启动");
            Messages.Message("定向能对射场景就绪：玩家 Group " + playerGroup.GroupId + "，敌对 Group " + enemyGroupId
                + " 位于 20km 外（赤道面）。看日志 [激光发射]/[激光命中!]/[贯穿生成]。", MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 定向能遮挡验证场景（2026-08-29）：玩家站激光炮塔，定位机 Manual 指向 20km 远敌站，
        /// 近敌站 10km 正挡在玩家→远站连线上。期望：光束路径被近站遮挡 → 只命中最近一艘
        /// （[激光命中!] 指向近站 Group、近站 Part HP 下降），验证遮挡语义。
        /// 定位机必须 Manual 指远站——Auto 会选最近的近站，无法验证遮挡。
        /// </summary>
        public static void SpawnRayOcclusionTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            CombatManager.DebugDetailedCombat = true;
            CombatManager.DebugProjectileProbeInterval = 30;

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = playerGroup.IsCircularOrbit
                ? playerGroup.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe())
                : playerGroup.CurrentOrbit.positionSystem.v;
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();

            // 1. 近敌站（挡路）：玩家 +x 10km
            DVector3 nearPos = playerPos + new DVector3(10000, 0, 0);
            int nearGroupId = spaceGame.AllocGroupId();
            int nearEntityId = spaceGame.AllocEntityId();
            spaceGame.CreateStation(nearEntityId, nearGroupId, enemyFactionId, nearPos, 60, 60, "遮挡敌站(近10km)");
            var nearGroup = spaceGame.GetGroupById(nearGroupId);
            if (nearGroup != null)
                nearGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 2. 远敌站（瞄准目标）：玩家 +x 20km（近站正挡在光束路径上）
            DVector3 farPos = playerPos + new DVector3(20000, 0, 0);
            int farGroupId = spaceGame.AllocGroupId();
            int farEntityId = spaceGame.AllocEntityId();
            spaceGame.CreateStation(farEntityId, farGroupId, enemyFactionId, farPos, 60, 60, "瞄准敌站(远20km)");
            var farGroup = spaceGame.GetGroupById(farGroupId);
            if (farGroup != null)
                farGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 3. 激光炮塔 + 定位机（Manual 指向远站）
            var designator = PlaceBuildingRaw(playerMap, "PF_Building_TargetDesignator", new IntVec3(28, 0, 30));
            var turret = PlaceBuildingRaw(playerMap, "PF_Building_Turret_Laser", new IntVec3(30, 0, 30));
            var desComp = designator?.TryGetComp<CompMapDesignator>();
            var turretComp = turret?.TryGetComp<CompMapTurret>();
            if (desComp == null || turretComp == null || turretComp.Mount == null)
            {
                Log.Error("[RayOcclusionTest] 定位机/炮塔 Comp 缺失");
                return;
            }
            desComp.RegisterTurret(turret);
            turretComp.Mount.boundDesignator = designator;
            desComp.DebugSetManualTarget(farGroupId);
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[RayOcclusionTest] 场景就绪: 玩家 G{playerGroup.GroupId} 激光炮塔 Manual→G{farGroupId}(20km)，近站 G{nearGroupId}(10km) 挡路，期望只命中近站");
            Messages.Message("定向能遮挡验证场景就绪：定位机 Manual 指向 20km 远站，10km 近站挡在光束路径上。期望光束只命中近站（遮挡语义）。",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 定向能旋转命中验证场景（2026-08-29）：敌站绕 Z 轴旋转 45°，光束世界 +x 水平来向，
        /// 在船体局部系变 45° 俯冲。验证 T2 的旋转正确 part 检测：
        /// [激光slab] 局部系线段呈 45° 倾斜、[激光part] 命中局部点 y≠0（上方斜入）、船壳入口 y>0（从上斜降地面）。
        /// 旧版"世界方向在局部系延伸"在旋转时失效，此场景是核心正确性回归测试。
        /// </summary>
        public static void SpawnRayRotatedTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            CombatManager.DebugDetailedCombat = true;
            CombatManager.DebugProjectileProbeInterval = 30;

            // 1. 定位机（Auto）+ 激光炮塔，自动绑定
            var designator = PlaceBuildingRaw(playerMap, "PF_Building_TargetDesignator", new IntVec3(28, 0, 30));
            var turret = PlaceBuildingRaw(playerMap, "PF_Building_Turret_Laser", new IntVec3(30, 0, 30));
            var desComp = designator?.TryGetComp<CompMapDesignator>();
            var turretComp = turret?.TryGetComp<CompMapTurret>();
            if (desComp == null || turretComp == null || turretComp.Mount == null)
            {
                Log.Error("[RayRotatedTest] 定位机/炮塔 Comp 缺失");
                return;
            }
            desComp.RegisterTurret(turret);
            turretComp.Mount.boundDesignator = designator;

            // 2. 敌对空间站 20km 赤道面（+x），绕 Z 轴旋转 45°：光束 +x 来向在局部系变 45° 俯冲
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = playerGroup.IsCircularOrbit
                ? playerGroup.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe())
                : playerGroup.CurrentOrbit.positionSystem.v;


            DVector3 enemyPos = playerPos + new DVector3(20000, 0, 0);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "旋转敌站");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);
            var enemyEntity = spaceGame.GetEntityById(enemyEntityId);
            if (enemyGroup != null)
            {
                // 姿态改走 group（entity.Rotation 由 UpdateGroupOrbit 同步），同时设 entity 保证立即生效
                enemyGroup.CurrentOrbit.rotation = (DQuaternion)Quaternion.Euler(0f, 0f, 45f);
                if (enemyEntity != null)
                    enemyEntity.SetRotation(enemyGroup.CurrentOrbit.rotation);
            }

            // 3. 跳转玩家地图
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[RayRotatedTest] 场景就绪: 玩家 G{playerGroup.GroupId} 激光炮塔，敌对 G{enemyGroupId} 旋转45°（Z轴）20km 外，验证旋转正确 part 检测");
            Messages.Message("定向能旋转命中验证场景就绪：敌站旋转 45°，看 [激光slab] 局部系线段倾斜 + [激光part] 命中局部点 y≠0（上方斜入）。",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 定向能多目标齐射场景（2026-08-29）：4 敌站四方位（±x/±z 20km，赤道面共轨），
        /// 4 组激光炮塔 + 定位机（Manual 各指一个敌站）。一次齐射 4 发，每发独立判定链
        /// [激光发射]→[激光slab]→[激光part]→[激光命中!]→[激光命中判定]，验证多目标同时命中 + 光束视觉。
        /// </summary>
        public static void SpawnRayMultiTest()
        {
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            CombatManager.DebugDetailedCombat = true;
            CombatManager.DebugProjectileProbeInterval = 30;

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = playerGroup.IsCircularOrbit
                ? playerGroup.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe())
                : playerGroup.CurrentOrbit.positionSystem.v;

            // 1. 四方位敌站（20km 赤道面，共轨 vRel≈0）
            var directions = new (string name, DVector3 offset)[]
            {
                ("敌站+x", new DVector3(20000, 0, 0)),
                ("敌站-x", new DVector3(-20000, 0, 0)),
                ("敌站+z", new DVector3(0, 0, 20000)),
                ("敌站-z", new DVector3(0, 0, -20000)),
            };
            int enemyFactionId = FactionResolver.HostileFactionIdForPlayer();
            var enemyGroupIds = new List<int>();
            string mapping = "";
            foreach (var d in directions)
            {
                int gid = spaceGame.AllocGroupId();
                int eid = spaceGame.AllocEntityId();
                spaceGame.CreateStation(eid, gid, enemyFactionId, playerPos + d.offset, 60, 60, d.name);
                var g = spaceGame.GetGroupById(gid);
                if (g != null)
                    g.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);
                enemyGroupIds.Add(gid);
                mapping += $"G{gid}={d.name} ";
            }

            // 2. 4 组：激光炮塔 + 定位机（Manual 各指一个敌站），自动绑定
            var turretCells = new[]
            {
                new IntVec3(29, 0, 29), new IntVec3(30, 0, 29), new IntVec3(31, 0, 29),
                new IntVec3(30, 0, 30),
            };
            var desCells = new[]
            {
                new IntVec3(28, 0, 28), new IntVec3(29, 0, 28), new IntVec3(30, 0, 28),
                new IntVec3(28, 0, 30),
            };
            for (int i = 0; i < enemyGroupIds.Count; i++)
            {
                var des = PlaceBuildingRaw(playerMap, "PF_Building_TargetDesignator", desCells[i]);
                var turret = PlaceBuildingRaw(playerMap, "PF_Building_Turret_Laser", turretCells[i]);
                var desComp = des?.TryGetComp<CompMapDesignator>();
                var turretComp = turret?.TryGetComp<CompMapTurret>();
                if (desComp == null || turretComp == null || turretComp.Mount == null)
                {
                    Log.Error("[RayMultiTest] 第 " + i + " 组定位机/炮塔 Comp 缺失");
                    continue;
                }
                desComp.RegisterTurret(turret);
                turretComp.Mount.boundDesignator = des;
                desComp.DebugSetManualTarget(enemyGroupIds[i]);
            }

            // 3. 跳转玩家地图
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message($"[RayMultiTest] 场景就绪: 玩家 G{playerGroup.GroupId}，4 激光炮塔各指一个敌站：{mapping}");
            Messages.Message("定向能多目标齐射场景就绪：4 敌站四方位，4 激光炮塔齐射（看 [激光命中!] 四条独立命中链）。",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>开关详细战斗日志 + 射弹探针（详细日志开启时探针固定 30 tick/条）。</summary>
        private static void ToggleDetailedCombat()
        {
            CombatManager.DebugDetailedCombat = !CombatManager.DebugDetailedCombat;
            CombatManager.DebugProjectileProbeInterval = CombatManager.DebugDetailedCombat ? 30 : 0;
            Log.Message("[调试] 详细战斗日志 " + (CombatManager.DebugDetailedCombat ? "已开启" : "已关闭")
                + "（射弹探针 " + CombatManager.DebugProjectileProbeInterval + " tick/条）");
        }
    }
}
