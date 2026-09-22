using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using PioneerFramework.Combat;
using PioneerFramework.Components;
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

        // ── 碰撞测试距离探针（自证物理是否推进；部署时设置，CompTick 每 2 秒打一条）──
        // 暂停时 CompTick 不跑 → 无探针日志 = 时间未推进；距离递减 = 物理正常等 [ShipCollision]。
        private static int probeGroupA = -1;
        private static int probeGroupB = -1;
        private static int probeGroupC = -1;
        private static int probeNextLogTick = 0;
        private static int probeLogCount = 0;
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

        /// <summary>每 tick 驱动（原 CompTick；现由 SpaceGameComp.GameComponentTick 调用）：
        /// 场景自动下令（Following→Attack）+ 碰撞测试距离探针（暂停时不跑 ⇒ 无探针日志即时间未推进）。</summary>
        public static void Tick()
        {
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
            Log.Message(string.Format(
                "[碰撞探针] realTick={0} physicsTick={1:F1} A=({2:F1},{3:F1},{4:F1})km " +
                "B=({5:F1},{6:F1},{7:F1})km C=({8:F1},{9:F1},{10:F1})km " +
                "|B-A|={11:F1}m B-Ax={12:F1}m |C-A|={13:F1}m C-Ax={14:F1}m",
                now, tsm != null ? tsm.GetPhysicsTick() : 0.0,
                pa.x / 1000.0, pa.y / 1000.0, pa.z / 1000.0,
                pb.x / 1000.0, pb.y / 1000.0, pb.z / 1000.0,
                pc.x / 1000.0, pc.y / 1000.0, pc.z / 1000.0,
                dB, relB.x, dC, relC.x));
        }

        /// <summary>与 CombatManager.GetGroupPos 相同的基准：圆轨解析位置 / 非圆轨 CurrentOrbit.position。</summary>
        private static DVector3 GetGroupPosDebug(SpaceEntityGroup g)
        {
            if (g.IsCircularOrbit)
                return g.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return g.CurrentOrbit.position;
        }

        /// <summary>与 CombatManager.GetGroupVelocity 相同的基准：圆轨解析切向速度 / 非圆轨 CurrentOrbit.velocity。</summary>
        private static DVector3 GetGroupVelDebug(SpaceEntityGroup g)
        {
            if (g.IsCircularOrbit)
                return g.GetCircularVelocityAtTick(TimeScaleManager.GetPhysicsTickSafe());
            return g.CurrentOrbit.velocity;
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
        private static (SpaceEntity entity, SpaceEntityGroup group, Map map) CreateTestStation(double altitudeMeters = 500000.0)
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

            Log.Message(string.Format("[StationTest] Created station: entityId={0}, groupId={1}, MapId={2}",
                entityId, groupId, entity.MapId));

            return (entity, group, map);
        }

        /// <summary>
        /// 创建无 Map 的纯物理航天器（linkedMapId=-1，单 part），用于船-船碰撞测试。
        /// 支持指定尺寸 / 姿态（验证旋转感知碰撞盒 GetWorldAabb）/ 初始速度。
        /// </summary>
        private static SpaceEntityGroup CreateShipNoMap(SpaceGameComp spaceGame, int factionId, DVector3 pos,
            DVector3 size, Quaternion rotation, string name, DVector3? velocity = null)
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
                position = pos,
                velocity = velocity ?? DVector3.zero,
                rotation = Quaternion.identity
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
                localPosition = DVector3.zero,
                localBoundsSize = size,
                hp = 100f,
                isIndestructible = false // 纯物理航天器可被摧毁
            };
            entity.SetParts(new List<SpacePart> { part });

            spaceGame.RegisterEntity(entity);
            group.AddEntity(entityId);

            Log.Message(string.Format(
                "[碰撞测试] 创建无Map航天器 \"{5}\" Entity {0} Group {1} size=({2:F0}x{3:F0}x{4:F0}) " +
                "旋转=({6:F0},{7:F0},{8:F0}) pos=({9:F0},{10:F0},{11:F0})km vel=({12:F0},{13:F0},{14:F0})m/s",
                entityId, groupId, size.x, size.y, size.z, name,
                rotation.eulerAngles.x, rotation.eulerAngles.y, rotation.eulerAngles.z,
                pos.x / 1000.0, pos.y / 1000.0, pos.z / 1000.0,
                group.CurrentOrbit.velocity.x, group.CurrentOrbit.velocity.y, group.CurrentOrbit.velocity.z));
            return group;
        }

        /// <summary>
        /// 无 Map 航天器碰撞测试（验证 #9 旋转感知碰撞盒）：
        /// ① 基准船 A（无 Map，不旋转）置于同步轨道圆轨；
        /// ② 船 B（40×30×40，绕 Z 旋转 45°）在 A 旁 200m，给朝向 A 的接近速度 → 斜向靠近；
        /// ③ 船 C（20×20×20，绕 Y 旋转 30°）从另一方向接近。
        /// 部署后自动打开 3D 视角，并挂上「距离探针」：CompTick 每 2 秒打印一次 B/C 相对 A 的距离，
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
            DVector3 velB = groupA.CurrentOrbit.velocity - new DVector3(30, 0, 0);
            var groupB = CreateShipNoMap(spaceGame, playerFaction + 1, basePos + new DVector3(200, 0, 0),
                new DVector3(40, 30, 40), Quaternion.Euler(0f, 0f, 45f), "斜向船B(旋转45°)", velB);

            // C：敌 faction，旋转 30°，另一方向 25m/s 靠近
            DVector3 velC = groupA.CurrentOrbit.velocity + new DVector3(25, 0, -25);
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
            Log.Message(string.Format("[碰撞测试] 场景就绪: realTick={0} physicsTick={1:F1} 时间档位={2}；B/C 将相对 A 靠近，等待 [ShipCollision]",
                Find.TickManager.TicksGame,
                tsm != null ? tsm.GetPhysicsTick() : 0.0,
                tsm != null ? tsm.CurrentLevel.ToString() : "N/A"));

            // 打开 3D 视角（默认地图视图看不到太空中的船，容易误以为"没反应"）
            Find.WindowStack.Add(new Window_SpaceBattleView3D(groupA));
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
                position = pos,
                velocity = DVector3.zero,
                rotation = Quaternion.identity
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
                localPosition = DVector3.zero,
                localBoundsSize = new DVector3(2, 2, 8),
                hp = 200f,
                isIndestructible = false
            };
            part.AddComponent(new HullMassComponent { mass = 300f }); // 干质量 300kg
            entity.SetParts(new List<SpacePart> { part });

            // D15 主推方向型：主推轴 body 局部 -Z
            entity.mainAxisLocal = new DVector3(0, 0, -1);

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

            Log.Message(string.Format(
                "[AxialThrustTest] 就绪: G{0} E{1} 主推轴局部=(0,0,-1) 姿态identity→burnDir=+X(夹角90°) dv=500m/s " +
                "主网=H2(ṁ6kg/s F12kN) RCS=肼(ṁ2kg/s F1.8kN) 双罐=H2 2000/肼 300kg；开 [Thrust] 日志观察对准+RCS→主网切换",
                groupId, entityId));

            // 打开 3D 视角
            Find.WindowStack.Add(new Window_SpaceBattleView3D(group));
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

            // 打印双向映射验证
            if (map != null)
            {
                var mapComp = map.GetComponent<SpaceMapComp>();
                Log.Message(string.Format("[StationTest] SpaceMapComp: ownerEntityId={0}, linkedPartId={1}",
                    mapComp?.ownerEntityId ?? -1, mapComp?.linkedPartId ?? -1));
            }
            foreach (var p in entity.Parts)
                Log.Message(string.Format("[StationTest] Part {0} linkedMapId={1}", p.partId, p.linkedMapId));

            // 打开 3D 视角
            var view3D = new Window_SpaceBattleView3D(group);
            Find.WindowStack.Add(view3D);

            Messages.Message(string.Format("空间站已创建 (Group {0})，Map {1}，3D视角已打开。",
                group.GroupId, entity.MapId), MessageTypeDefOf.TaskCompletion);
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
            Log.Message(string.Format("[漏靶弹头测试] 弹头已生成={0}：r={1:F0}km 圆轨速度={2:F0}m/s｜"
                + "预期 ㉒|a|≈{3:F3} m/s²（=μ/r²）、㉔弯曲比≈1×、寿命 {4:F0}s",
                ok, r / 1000.0, vCirc,
                earth.gravitationalParameter / (r * r), 120.0));
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
        /// <summary>
        /// ★★ **反推着陆验收场景**（2026-09-22）：在**赤道**上把船摆到地表上方 8 km、**纯径向下降 150 m/s**，
        /// 然后交给 <see cref="LandingController" /> 做垂直反推。
        ///
        /// ## 为什么这么摆
        /// · **纯径向下降**（无切向速度）= 反推制导纯核的**唯一假设**（它只解径向一维）⇒
        ///   这个场景测的是**制导与执行**，不带"消水平速度"那一层（那是后续的段）；
        /// · 起点 8 km / 150 m/s：按闭式点火高度 `v²/(2·(aMax−g))`，本船的推力余量下有
        ///   一倍以上裕度（"能不能刹住"应当判**能**）⇒ 跑起来就该落地；
        /// · 用**过推档**的调试船（560kN 主推）⇒ 推重比 ≫1，不会被"推重比不足"那条拦住。
        ///
        /// 观察点（日志）：`[着陆·诊断]`（h / 下降率 / aMax / 动作）→ 最终 `[着陆] **着陆完成**`。
        /// </summary>
        public static void SpawnLandingTest()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var celestialMgr = Current.Game?.GetComponent<CelestialBodyManager>();
            if (spaceGame == null || celestialMgr == null) return;

            var body = celestialMgr.GetBodyById(EARTH_BODY_ID);
            if (body == null)
            {
                Log.Error("[着陆测试] 天体 Def 缺失（bodyId=1）");
                return;
            }

            double physicsTick = TimeScaleManager.GetPhysicsTickSafe();
            double altStart = 8_000.0;
            double descent = 150.0;
            double r = body.radius + altStart;

            // 赤道方向：从自转相位取一个固定的径向（与 `EquatorialRadial` 同口径）
            DVector3 up = EquatorialRadial(physicsTick);
            DVector3 pos = up * r;
            DVector3 vel = -up * descent;          // **纯径向下降**（无切向 ⇒ 反推核的适用域）

            var group = CreateShipNoMap(spaceGame, Faction.OfPlayer.loadID, pos,
                new DVector3(6f, 6f, 12f), Quaternion.identity, "着陆测试", vel);
            if (group == null) return;

            group.CelestialBodyId = EARTH_BODY_ID;
            group.exemptPlanetCollision = true;     // 着陆段允许贴地（由制导判到达，不靠碰撞豁免）

            LandingController.BeginLanding(group.GroupId, EARTH_BODY_ID);

            Log.Message(string.Format(
                "[着陆测试] G{0} 已创建：高度 {1:F0} m、纯径向下降 {2:F0} m/s、地表半径 {3:F0} km｜" +
                "看 [着陆·诊断] → [着陆] 着陆完成",
                group.GroupId, altStart, descent, body.radius / 1000.0));
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
            // 月球相对**其父天体**（= 根行星地球）的位置/速度 → 与群组系（CurrentOrbit 所在系）同系
            DVector3 moonPos = celestialMgr.GetBodyLocalPosition(MOON_BODY_ID, physicsTick);
            DVector3 moonVel = celestialMgr.GetBodyLocalVelocity(MOON_BODY_ID, physicsTick);

            double r = moon.radius + 1_800_000.0;
            double vCirc = System.Math.Sqrt(moon.gravitationalParameter / r);
            var radial = new DVector3(1.0, 0.0, 0.15).normalized;
            var tangent = new DVector3(radial.z, 0.0, -radial.x); // XZ 平面内正交

            DVector3 pos = moonPos + radial * r;
            DVector3 vel = moonVel + tangent * (vCirc * 0.92);

            var group = CreateShipNoMap(spaceGame, Faction.OfPlayer.loadID, pos,
                new DVector3(6f, 6f, 12f), Quaternion.identity, "月球引力测试", vel);
            if (group == null) return;

            // 中心天体 = 月球（引力场按根系解析；坐标仍在系统系 → 无需改 CurrentOrbit）
            group.CelestialBodyId = MOON_BODY_ID;

            Log.Message(string.Format(
                "[月球引力测试] G{0} 已创建：r={1:F0}km 圆轨速度={2:F0}m/s 实给={3:F0}m/s（椭圆 e≈0.154）" +
                "｜开「性能优化验收探针」后看汇总行 ㉑ 的子天体域差分比值",
                group.GroupId, r / 1000.0, vCirc, vCirc * 0.92));
        }


        #region 无 Map 编队放置（预设圆轨）

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
        /// 控制：放置后在世界视图选中该编队 →「机动规划 / 转移到圆轨 / 交会规划」即可消耗 dV。
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
                    presetLabel = string.Format("同步轨道（周期 {0:F0}s = 天体自转周期 ⇒ 与地表相对静止）",
                        2.0 * System.Math.PI / omega);
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
                    // 群组系 = 根行星（地球）为原点 ⇒ 月心相对状态要加月球的位置/速度
                    DVector3 moonPos = mgr.GetBodyLocalPosition(MOON_BODY_ID, physicsTick);
                    DVector3 moonVel = mgr.GetBodyLocalVelocity(MOON_BODY_ID, physicsTick);
                    // 初始径向 = 背地侧（月心 → 远离地球），落在月球公转面内；切向取该面内的顺行方向
                    DVector3 radial = moonPos.normalized;
                    DVector3 planeNormal = DVector3.Cross(moonPos, moonVel).normalized;
                    if (planeNormal.sqrMagnitude < 0.01) planeNormal = DVector3.up;
                    DVector3 tangent = DVector3.Cross(planeNormal, radial).normalized;
                    if (tangent.sqrMagnitude < 0.01) tangent = ProgradeEquatorialTangent(radial);
                    pos = moonPos + radial * radius;
                    vel = moonVel + tangent * System.Math.Sqrt(spaceGame.GetBodyGM(bodyId) / radius);
                    presetLabel = string.Format("绕月轨道（月面上方 {0:F0}km，背地侧起点）", 1_800_000.0 / 1000.0);
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
                    presetLabel = string.Format("近地轨道（地表上方 {0:F0}km）", 500_000.0 / 1000.0);
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
            Log.Message(string.Format(
                "[放置] G{0} E{1} 无Map编队 → {2}｜中心={3}(ID={4}) r={5:F0}km（高度 {6:F0}km）圆轨周期={7:F0}s " +
                "v={8:F0}m/s dV={9:F0}/{10:F0} m/s 工质={11:F0}/{12:F0}kg｜{13}｜轨道保持=开（维持圆轨会持续耗料，设计如此）",
                groupId, entityId, presetLabel,
                centralDef != null ? centralDef.label : "?", bodyId,
                radius / 1000.0, (radius - (centralDef != null ? centralDef.radius : 0.0)) / 1000.0, period,
                group.IsCircularOrbit ? group.GetCircularVelocityAtTick(physicsTick).magnitude : vel.magnitude,
                remainingDv, maxDv, propMass, propCap, dirLabel));

            Messages.Message(string.Format(
                "已放置无Map编队 G{0}：{1}｜dV {2:F0} m/s（世界视图选中它 →「机动规划/转移到圆轨」消耗 dV）",
                groupId, presetLabel, remainingDv), MessageTypeDefOf.TaskCompletion, false);
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
            Log.Message(string.Format(
                "[放置] G{0} E{1} 无Map编队 → **GTO 椭圆轨道**｜rp={2:F0}km ra={3:F0}km a={4:F0}km e={5:F3} " +
                "周期={6:F0}s vp={7:F0}m/s｜dV={8:F0} m/s｜**非圆轨**（走引力积分，不进圆轨解析模式）",
                group.GroupId, entityId, rp / 1000.0, ra / 1000.0, a / 1000.0, e, period, vp,
                spaceGame.GetGroupDeltaV(group).remainingDv));
            Messages.Message(string.Format(
                "已放置 GTO 椭圆目标 G{0}（近地点 {1:F0}km / 远地点 {2:F0}km，e={3:F2}）。" +
                "再放一个 LEO 编队去「交会」它 —— 这就是 LEO→GTO 的原始工况。",
                group.GroupId, rp / 1000.0, ra / 1000.0, e), MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>放置：绕月轨道圆轨（月面 1800km）。</summary>
        public static void PlaceMoonOrbitNoMapGroup() => PlaceNoMapOrbitGroup(NoMapOrbitPreset.MoonOrbit);

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
            group.CurrentOrbit = new OrbitState { position = pos, velocity = vel, rotation = Quaternion.identity };
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
            entity.mainAxisLocal = new DVector3(0, 0, -1);   // D15：无 Map 船的主推轴来源

            var part = new SpacePart
            {
                partId = 1,
                defName = "PF_ShipPart_Cube",
                localPosition = DVector3.zero,
                localBoundsSize = new DVector3(40.0, 20.0, 80.0),   // 长轴 = Z = 主推轴向
                hp = 1000f,
                isIndestructible = false
            };
            part.AddComponent(new HullMassComponent { mass = 5000f });   // 结构干质量（碰撞动量交换也用它）
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
        /// **新远场律的游戏内验收场景**（2026-09-17）：同轨两船、相位差 120°，追击方用
        /// <see cref="RendezvousGuidanceLaw"/> 自主收相位（**没有出发段机动** —— 律本身就是整条转移）。
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
        /// <param name="legSeconds">远场每段的时间预算（0 = 游戏默认 60000s）。
        /// ⚠ **时长由它定，不由相位差定**：律的 S 形式目标是"在本段末恰好闭合"
        /// ⇒ 只要没撞上物理上界（缩轨到大气层边缘给出的 δn 上限），**段多长就等多久**。
        /// 代价同时由它定：相位守恒律 Δv·t = (2/3)·r·Δφ ⇒ 段短则快且贵。这是"时间当自由变量"的旋钮。</param>
        /// <param name="targetAltKm">目标的高度（km）相对追击方的差值：0 = 同轨（只考相位段）；
        /// >0 = 目标在更高的圆轨（**考形状段**：先变轨、再收相位）。</param>
        /// <param name="targetIncDeg">目标轨道的**倾角**（度）：把目标的半径方向绕**当地切向**转过去实现
        /// —— 同半径 + 异面时速度仍切向（`dot(r′,v′)=0` 且 `|v′|=v_c(R)`）⇒ 仍是圆轨、升交点在目标当前位置。
        /// ⚠ **异面是阶梯「匹配平面」那一级唯一会上场的几何**（同面时那一级永远不会被选中）
        /// ⇒ 不设它，那一级就只能靠台架/纯测试覆盖。</param>
        public static void SpawnRendezvousLawTest(bool overThrust, double phaseDegAhead, double legSeconds,
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

            // 登记**新律**交会：无出发段 ⇒ 登记即生效（旧路径要等出发机动点火才 armed，
            // 而新律本身就是整条转移、不需要出发段）。
            chaser.RegisterRendezvousGuidance(target.GroupId, physicsTick, prograde: true, useNetworks: null);
            chaser.ArmRendezvousGuidance();
            // 腿长写在登记**之后**（Register 会 ResetRendezvousLawState 把它清零）
            chaser.rendezvousLawLegSeconds = legSeconds;
            // 期望估计用**游戏在跑的那条政策**（RendezvousGuidanceLaw.PlanLeg）——不另写一套估计，
            // 否则"场景说的"和"律做的"又成两份（这正是本轮教训）。
            bool shapeProbe = false;
            double legEndProbe = double.NegativeInfinity;   // -∞ ⇒ PlanLeg 必然重排一段，用它要估计值
            double aMaxEst = spaceGame.EstimateMaxAcceleration(chaser);
            double legUse = legSeconds > 0.0 ? legSeconds : 60000.0;
            RendezvousGuidanceLaw.PlanLeg(chaser.localPosition, chaser.localVelocity,
                target.localPosition, target.localVelocity, mu, aMaxEst, physicsTick, legUse, 0.0,
                ref shapeProbe, ref legEndProbe, out double legPlan, out double dvShapePlan);
            double dvPhasePlan = (2.0 / 3.0) * chaser.localPosition.magnitude * System.Math.Abs(phaseRad)
                                 / System.Math.Max(legPlan, 1.0);
            double dvTotalEst = dvShapePlan * RendezvousGuidanceLaw.ShapeCostFactor + dvPhasePlan;
            double dvBudget = spaceGame.GetGroupDeltaV(chaser).remainingDv;
            Log.Message(string.Format(
                "[交会验收] G{0}(追击) → G{1}(目标)｜追击 r={2:F0}km 目标 r={3:F0}km 相位差 {4:F0}° 目标倾角 {12:F1}°｜" +
                "政策预估：模式={5} 腿长={6:F0}s Δv≈{7:F0} m/s（变轨 {8:F0} + 相位 {9:F0}）×1.5余量 vs 预算 {10:F0} m/s{11}｜" +
                "新律=开 · 无出发段（律即转移）· 期望：先变轨 → 再收相位 → 5% 半径内交班 CW 终端 → 交会完成",
                chaser.GroupId, target.GroupId, radius / 1000.0, radiusTgt / 1000.0, phaseDegAhead,
                shapeProbe ? "形状/变轨" : "相位", legPlan, dvTotalEst, dvShapePlan, dvPhasePlan, dvBudget,
                dvTotalEst > dvBudget ? " ⚠ 超预算（此场景绕过玩家路径的预算闸门，仍会跑）" : "", targetIncDeg));
            Messages.Message(string.Format(
                "交会验收已布置：G{0} 追击 G{1}（相位 {2:F0}°，目标高 {3:F0}km）。政策预估 Δv≈{4:F0} m/s、" +
                "耗时 ≈{5:F0} 模拟秒；中途读数见日志 [NewLaw]（需先开「详细操舵日志」）。",
                chaser.GroupId, target.GroupId, phaseDegAhead, targetAltKm, dvTotalEst, legPlan),
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


        #endregion

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

            Log.Message(string.Format("[DualEngineTest] 场景就绪: 氢罐 {0}kg / 氙罐 {1}kg，机动1(核热+50径向) T+600，机动2(离子+30切向) T+1200",
                hydrogenTank?.StoredMass ?? 0f, xenonTank?.StoredMass ?? 0f));

            // 打开机动队列窗口（查看/调整每条机动的网络选择）与 3D 视角
            Find.WindowStack.Add(new ManeuverQueueWindow(group));
            var view3D = new Window_SpaceBattleView3D(group);
            Find.WindowStack.Add(view3D);

            Messages.Message("双引擎测试场景已部署：核热+离子，两条机动已各自绑定网络（见机动队列）",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>在地图指定格子放置一个建筑，返回其 CompPropellantTank（若无则 null）。</summary>
        private static CompPropellantTank PlaceBuilding(Map map, string defName, IntVec3 cell)
        {
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
            Log.Message(string.Format(
                "[SteerTest] 场景就绪 Group{0}：主推进轴={1} 六向推力(kN) F={2:F1} B={3:F1} L={4:F1} R={5:F1} U={6:F1} D={7:F1} RCS={8:F1}kN 氢={9:F0}kg",
                group.GroupId, mapComp.MainThrustAxis,
                mapComp.GetAxisTotalThrust((int)ThrustAxis.Forward) / 1000f,
                mapComp.GetAxisTotalThrust((int)ThrustAxis.Back) / 1000f,
                mapComp.GetAxisTotalThrust((int)ThrustAxis.Left) / 1000f,
                mapComp.GetAxisTotalThrust((int)ThrustAxis.Right) / 1000f,
                mapComp.GetAxisTotalThrust((int)ThrustAxis.Up) / 1000f,
                mapComp.GetAxisTotalThrust((int)ThrustAxis.Down) / 1000f,
                mapComp.RcsTotalThrust / 1000f,
                mapComp.TotalPropellantMass));

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
                : playerGroup.CurrentOrbit.position;


            DVector3 enemyPos = playerPos + new DVector3(20000, 0, 0);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
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
                enemyGroup.CurrentOrbit.rotation = Quaternion.Euler(0f, 0f, 45f);
                if (enemyEntity != null)
                    enemyEntity.SetRotation(enemyGroup.CurrentOrbit.rotation);
            }

            // 4. 打开 3D 视角观察对射，并跳转到玩家空间站地图看炮塔/出膛弹/开火烟雾
            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[TurretTest] 场景就绪: 玩家 Group {0} 整体式炮塔已绑定位机，敌对 Group {1} 位于 20km 外（赤道面），Auto 索敌启动",
                playerGroup.GroupId, enemyGroupId));
            Messages.Message(string.Format("炮塔对射测试场景就绪：玩家 Group {0}，敌对 Group {1} 位于 20km 外（赤道面，验证 360° 射界）。",
                playerGroup.GroupId, enemyGroupId), MessageTypeDefOf.TaskCompletion);
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
                : playerGroup.CurrentOrbit.position;
            // 异轨靶（2026-09-07：同轨正后方追尾=纯切向，任何推进都抬轨逃逸，物理打不中；
            // 真实交战目标异轨，视线含径向分量，推力直扑可打——19:53 异轨轮次命中实证）。
            // 位置 = 沿母站轨道切向 20km + 径向抬轨 30km → 入轨后不同半径不同周期 = 轨道不同步
            DVector3 sameOrbitPoint = SameOrbitPointOffset(playerPos, GetGroupVelDebug(playerGroup), 20000);
            DVector3 enemyPos = sameOrbitPoint + (sameOrbitPoint / sameOrbitPoint.magnitude) * 30000.0;
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（导弹靶）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 3. Manual 指定目标 + 立即齐射（不等 scanInterval 自动索敌；齐射沿物理步对齐，奇 tick 调用会被跳拍后由 autoFire 补发）
            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();

            // 4. 打开 3D 视角观察领弹出生，并跳转到玩家空间站地图
            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[MissileTest] 场景就绪: 玩家 Group {0}（导弹发射器炮塔已绑定）→ 敌对 Group {1} 位于 20km 外，ManualFire 已触发",
                playerGroup.GroupId, enemyGroupId));
            Messages.Message(string.Format("导弹发射测试场景就绪：玩家 Group {0}，敌对 Group {1} 位于 20km 外（观察 [导弹发射] 日志与 3D 视图领弹出生）。",
                playerGroup.GroupId, enemyGroupId), MessageTypeDefOf.TaskCompletion);
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
                : playerGroup.CurrentOrbit.position;
            // 同轨靶（严格同半径共轨，相对静止）：沿母站轨道切向偏移——世界系偏移在任意轨道相位
            // 会引入径向分量导致异轨（半径差 km 级），靶漂移+瞄准错乱（22:43 会话实证）
            DVector3 enemyPos = SameOrbitPointOffset(playerPos, GetGroupVelDebug(playerGroup), 1500);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（导弹靶 1.5km）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 3. Manual 指定目标 + 立即齐射（齐射沿物理步对齐，奇 tick 调用会被跳拍后由 autoFire 补发）
            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();
            autoCmdLauncherGroupId = playerGroup.GroupId; // 导弹出生即自动下令撞击（模拟玩家出膛下令）

            // 4. 打开 3D 视角观察撞击爆炸，并跳转到玩家空间站地图
            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[MissileImpactTest] 场景就绪: 玩家 Group {0} → 敌对 Group {1} 位于 1.5km 外，ManualFire 已触发，预计 ~5s 撞击",
                playerGroup.GroupId, enemyGroupId));
            Messages.Message(string.Format("导弹撞击测试场景就绪：玩家 Group {0}，敌对 Group {1} 位于 1.5km 外（观察 [导弹撞击] → [弹头命中] 日志与 3D 爆炸）。",
                playerGroup.GroupId, enemyGroupId), MessageTypeDefOf.TaskCompletion);
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
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（巡航测试靶）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();
            autoCmdLauncherGroupId = playerGroup.GroupId; // 导弹出生即自动下令撞击（模拟玩家出膛下令）

            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[MissileCruiseTest] 场景就绪: 玩家 Group {0} → 敌对 Group {1} 位于 1.5km 外（巡航边界 500m），预期日志链：巡航 → 终末接管 → 撞击",
                playerGroup.GroupId, enemyGroupId));
            Messages.Message(string.Format("巡航→终末转移测试就绪（1.5km 靶，巡航边界 500m）：观察 [导弹巡航] → [导弹制导 终末接管] → [导弹撞击]。",
                playerGroup.GroupId, enemyGroupId), MessageTypeDefOf.TaskCompletion);
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
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（弹群靶 ~25km）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();
            autoCmdLauncherGroupId = playerGroup.GroupId; // 出膛即自动下 Attack（进终末 → 解离）

            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[VolleyImpactTest] 场景就绪: 玩家 G{0} 弹群 N=8 → 敌对 G{1} ~25.3km。预期日志链：[导弹弹群] N=8 队列 → [弹群解离]（含错峰时隙）→ 8×[导弹制导 终末接管]（距离递增）→ 8×[导弹撞击]/[弹头命中]（错峰点火保证到达间隔 ≥ 间距/巡航，无兄弟爆炸误杀）",
                playerGroup.GroupId, enemyGroupId));
            Messages.Message(string.Format("弹群齐射测试就绪：N=8 深度队列 → 敌对 G{1} ~25.3km（观察 [导弹弹群]/[弹群解离]/8× 错峰撞击日志）。",
                playerGroup.GroupId, enemyGroupId), MessageTypeDefOf.TaskCompletion);
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
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
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

            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[VolleyMergedHitTest] 场景就绪: 玩家 G{0} 合并弹群 G{1}(N=4 Following) ← 敌站 G{2} 探针敌弹（Attack 目标=弹群）。预期：敌弹终末撞入编队 → 两条单枚 [导弹撞击] + [弹群] E.. 离群 N 4→3（兄弟继续合并）",
                playerGroup.GroupId, volleyGroupId, enemyGroupId));
            Messages.Message(string.Format("合并弹群撞击测试就绪：探针敌弹即将撞入 G{1} 弹群（观察单枚 [导弹撞击]/[弹群] 离群日志；之后用面板/「命令全体导弹」下 Attack 看解离）。",
                playerGroup.GroupId, volleyGroupId), MessageTypeDefOf.TaskCompletion);
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
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站（长距靶 ~60km）");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            launchComp.SetManualTarget(enemyGroupId);
            launchComp.ManualFire();
            autoCmdLauncherGroupId = playerGroup.GroupId; // 出膛即自动下 Attack

            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[MissileLongRangeTest] 场景就绪: 玩家 G{0} 弹群 N=4 → 敌对 G{1} ~60km。预期：[导弹制导 终末接管]（表面距 ~60km）→ 全程收敛冲刺 → 4× 错峰 [导弹撞击]/[弹头命中]（间距 2km > 爆炸半径 1.5km，零冲量扰动）。~133 游戏秒后到达",
                playerGroup.GroupId, enemyGroupId));
            Messages.Message(string.Format("长距制导验证就绪：N=4 弹群 → 敌对 G{1} ~60km（观察制导收敛与 4× 错峰命中；约 133 游戏秒）。",
                playerGroup.GroupId, enemyGroupId), MessageTypeDefOf.TaskCompletion);
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
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
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

            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[MissileInterceptTest] 场景就绪: 玩家 G{0} 反导炮塔已绑定 → 敌对 G{1} 位于 20km 外（3 波敌弹 2s/9s/16s 来袭），敌弹接近后反导自动锁定。预期：异 volley 对撞双 [导弹撞击] + 迟发反导弹 [导弹中止]（K6）",
                playerGroup.GroupId, enemyGroupId));
            Messages.Message(string.Format("拦截对撞测试就绪：敌对 G{1} 位于 20km 外，3 波敌弹即将来袭（观察对撞双爆与 [导弹中止]）。",
                playerGroup.GroupId, enemyGroupId), MessageTypeDefOf.TaskCompletion);
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
                Log.Message(string.Format("[拦截测试] 敌弹波次：G{0} 敌站G{1}→玩家G{2} 距离={3:F1}km", group.GroupId,
                    interceptEnemyGroupId, interceptPlayerGroupId, dist / 1000.0));
        }

        /// <summary>
        /// 测试弹工厂（场景用）：CombatManager.CreateMissileGroup 建实体 + AutonomousVehicleManager 登记，
        /// 与发射链同参数语义（volleyId 独立、launcherGroupId=-1 表示非炮塔来源）。
        /// </summary>
        private static SpaceEntityGroup CreateTestMissile(string missileDefName, string weaponDefName, int factionId,
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
                Log.Message(string.Format("[拦截测试] 手动销毁来袭导弹 G{0}", gid));
            }
            Messages.Message(string.Format("已销毁 {0} 枚来袭导弹——正在追击它们的反导弹应中止回 Standby（[导弹中止]）。", targets.Count), MessageTypeDefOf.TaskCompletion);
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
                : playerGroup.CurrentOrbit.position;

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

            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
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
                mapping += string.Format("G{0}={1}(期望面{2}) ", gid, d.name, d.expectFace);
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
                Log.Message(string.Format("[OmniTurretTest] G{0} 已设精确炮击点 (40,30)：瞄准偏移船心 +10m（看 [精确炮击]/[炮塔出膛]/[贯穿生成] 日志）",
                    enemyGroupIds[0]));
            }

            // 打开 3D 视角 + 跳转玩家地图看 6 发齐射出膛弹
            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[OmniTurretTest] 场景就绪: 玩家 G{0}，6 炮塔各指一个敌站：{1}",
                playerGroup.GroupId, mapping));
            Messages.Message("全方位炮塔对射场景就绪：6 敌站分布六方位，6 炮塔齐射验证 360° 射界与六面出膛（看日志 [炮塔出膛]）。",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>在地图指定格子放置一个建筑，返回 Thing（无 faction，测试用）。</summary>
        private static Thing PlaceBuildingRaw(Map map, string defName, IntVec3 cell)
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
                : playerGroup.CurrentOrbit.position;

            // 敌舰：玩家船正下方（世界 -y）20km → 船体局部 -y → 出膛面 y-（面2）
            DVector3 enemyPos = playerPos + new DVector3(0, -20000, 0);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
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

            // 打开 3D 视角 + 跳转敌舰地图看红色精确炮击标记
            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            var enemyEntity = spaceGame.GetEntityById(enemyEntityId);
            if (enemyEntity != null && enemyEntity.MapId >= 0)
            {
                Map enemyMap = SpaceMapGenerator.FindPocketMap(enemyEntity.MapId);
                if (enemyMap != null)
                    CameraJumper.TryJump(enemyMap.Center, enemyMap);
            }

            Log.Message(string.Format(
                "[PrecisionTest] 场景就绪: 玩家 G{0}，敌舰 G{1} 位于正下方 20km，精确炮击点 (40,30)（船心偏移 +10m），Auto 索敌启动",
                playerGroup.GroupId, enemyGroupId));
            Messages.Message("精确炮击测试场景就绪：目标在正下方（期望出膛面 y-），精确炮击点 (40,30)，敌舰地图上有红色标记。",
                MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>
        /// 定向能对射测试场景（2026-08-29）：玩家站定位机 Auto + 激光炮塔，敌对站 20km 赤道面（vRel≈0）。
        /// 验证点（日志链）：[齐射详情] → [炮塔出膛] → [激光发射]（射程 50km）→ [激光命中!]（Part HP 下降）
        /// → [贯穿生成]（贯通入口→落点格）→ 光束 60 tick 残留（3D 视角）。
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
                : playerGroup.CurrentOrbit.position;
            DVector3 enemyPos = playerPos + new DVector3(20000, 0, 0);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 3. 打开 3D 视角 + 跳转玩家地图看激光炮塔
            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[RayTurretTest] 场景就绪: 玩家 Group {0} 激光炮塔已绑定位机，敌对 Group {1} 位于 20km 外（赤道面），Auto 索敌启动",
                playerGroup.GroupId, enemyGroupId));
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
                : playerGroup.CurrentOrbit.position;
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;

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

            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[RayOcclusionTest] 场景就绪: 玩家 G{0} 激光炮塔 Manual→G{1}(20km)，近站 G{2}(10km) 挡路，期望只命中近站",
                playerGroup.GroupId, farGroupId, nearGroupId));
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
                : playerGroup.CurrentOrbit.position;


            DVector3 enemyPos = playerPos + new DVector3(20000, 0, 0);
            int enemyGroupId = spaceGame.AllocGroupId();
            int enemyEntityId = spaceGame.AllocEntityId();
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "旋转敌站");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);
            var enemyEntity = spaceGame.GetEntityById(enemyEntityId);
            if (enemyGroup != null)
            {
                // 姿态改走 group（entity.Rotation 由 UpdateGroupOrbit 同步），同时设 entity 保证立即生效
                enemyGroup.CurrentOrbit.rotation = Quaternion.Euler(0f, 0f, 45f);
                if (enemyEntity != null)
                    enemyEntity.SetRotation(enemyGroup.CurrentOrbit.rotation);
            }

            // 3. 打开 3D 视角 + 跳转玩家地图
            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[RayRotatedTest] 场景就绪: 玩家 G{0} 激光炮塔，敌对 G{1} 旋转45°（Z轴）20km 外，验证旋转正确 part 检测",
                playerGroup.GroupId, enemyGroupId));
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
                : playerGroup.CurrentOrbit.position;

            // 1. 四方位敌站（20km 赤道面，共轨 vRel≈0）
            var directions = new (string name, DVector3 offset)[]
            {
                ("敌站+x", new DVector3(20000, 0, 0)),
                ("敌站-x", new DVector3(-20000, 0, 0)),
                ("敌站+z", new DVector3(0, 0, 20000)),
                ("敌站-z", new DVector3(0, 0, -20000)),
            };
            int enemyFactionId = (Faction.OfPlayer.loadID) + 1;
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
                mapping += string.Format("G{0}={1} ", gid, d.name);
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

            // 3. 打开 3D 视角 + 跳转玩家地图
            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[RayMultiTest] 场景就绪: 玩家 G{0}，4 激光炮塔各指一个敌站：{1}",
                playerGroup.GroupId, mapping));
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
