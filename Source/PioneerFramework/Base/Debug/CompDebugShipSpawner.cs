using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    public class CompProperties_DebugShipSpawner : CompProperties
    {
        public CompProperties_DebugShipSpawner()
        {
            compClass = typeof(CompDebugShipSpawner);
        }
    }

    /// <summary>
    /// 调试用 ThingComp：提供一个 Gizmo，创建绕地球圆轨道的测试空间站（含地图 + 单 part）。
    /// Lite 裁剪：Ship3D 蓝本部署已删除（装配系统移除），仅保留空间站部署入口。
    /// 额外提供「双引擎测试场景」：核热 + 离子引擎、对应储罐加注，并规划两条
    /// 各自绑定网络的机动，用于验证工质网络独立选择与执行。
    /// </summary>
    public class CompDebugShipSpawner : ThingComp
    {
        private const int EARTH_BODY_ID = 1;

        // ── 碰撞测试距离探针（自证物理是否推进；部署时设置，CompTick 每 2 秒打一条）──
        // 暂停时 CompTick 不跑 → 无探针日志 = 时间未推进；距离递减 = 物理正常等 [ShipCollision]。
        private static int probeGroupA = -1;
        private static int probeGroupB = -1;
        private static int probeGroupC = -1;
        private static int probeNextLogTick = 0;
        private static int probeLogCount = 0;
        private const int PROBE_LOG_INTERVAL = 120;
        private const int PROBE_LOG_MAX = 300;

        public override void CompTick()
        {
            base.CompTick();
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

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (var g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            yield return new Command_Action
            {
                defaultLabel = "部署空间站 (测试)",
                defaultDesc = "创建一个测试空间站（含地图+Part），并打开3D视角。",
                icon = TexCommand.DesirePower,
                action = delegate
                {
                    SpawnTestStation();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "部署双引擎测试场景",
                defaultDesc = "创建空间站并自动布置核热(液氢)+离子(氙)引擎与满罐储罐，规划两条各自绑定网络的机动。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnDualEngineTest();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "运行架构验证测试",
                defaultDesc = "自动部署测试空间站并验证：储罐差值同步 / 合冲量 burn / 推进器燃烧状态。结果打印到游戏日志。",
                icon = TexButton.Info,
                action = delegate
                {
                    BurnArchitectureTest.Run();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "部署炮塔对射测试场景",
                defaultDesc = "创建玩家空间站（定位机+上下双侧炮塔，自动绑定）+ 敌对空间站（20km 外），触发 Auto 索敌齐射。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnTurretTest();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "部署无Map航天器碰撞测试",
                defaultDesc = "创建 3 艘无 Map 纯物理航天器：基准船 A（同步轨道圆轨）+ 旋转 45° 的 B（200m 外 30m/s 接近）+ 旋转 30° 的 C（25m/s 另一方向接近），验证旋转感知碰撞盒 GetWorldAabb。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnCollisionTest();
                }
            };
        }

        private static int GetNextGroupId(SpaceGameComp spaceGame)
        {
            int maxId = 0;
            foreach (var g in spaceGame.GetAllGroups())
                if (g.GroupId > maxId) maxId = g.GroupId;
            return maxId + 1;
        }

        private static int GetNextEntityId(SpaceGameComp spaceGame)
        {
            int maxId = 0;
            foreach (var g in spaceGame.GetAllGroups())
            {
                foreach (int eid in g.GetEntityIds())
                    if (eid > maxId) maxId = eid;
            }
            return maxId + 1;
        }

        /// <summary>创建指定高度圆轨道的测试空间站（含地图 + 单 part），返回实体/群组/地图。</summary>
        private (SpaceEntity entity, SpaceEntityGroup group, Map map) CreateTestStation(double altitudeMeters = 500000.0)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Messages.Message("SpaceGameComp 未找到！", MessageTypeDefOf.RejectInput);
                return (null, null, null);
            }

            int groupId = GetNextGroupId(spaceGame);
            int entityId = GetNextEntityId(spaceGame);

            // 默认 500km LEO；同步轨道高度约 28,560km（半径 33,560km，周期 24h）
            double earthRadiusMeters = SpaceConstants.PlanetRadiusMeters;
            double orbitRadiusMeters = earthRadiusMeters + altitudeMeters;
            double angle = System.DateTime.Now.Millisecond * 0.001 * System.Math.PI * 2;
            double x = System.Math.Cos(angle) * orbitRadiusMeters;
            double z = System.Math.Sin(angle) * orbitRadiusMeters;
            var pos = new DVector3(x, 0, z);

            var entity = spaceGame.CreateStation(entityId, groupId, parent.Faction?.loadID ?? 0,
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
        private SpaceEntityGroup CreateShipNoMap(SpaceGameComp spaceGame, int factionId, DVector3 pos,
            DVector3 size, Quaternion rotation, string name, DVector3? velocity = null)
        {
            int groupId = GetNextGroupId(spaceGame);
            int entityId = GetNextEntityId(spaceGame);

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
        private void SpawnCollisionTest()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;

            // 碰撞检测挂在 GameComponentTick 上，暂停时永不执行 → 部署时强制解暂停回 1x
            bool wasPaused = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            if (wasPaused)
                Log.Message("[碰撞测试] 检测到暂停，已自动解除（TimeSpeed.Normal）");

            int playerFaction = parent.Faction?.loadID ?? 0;

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

        private void SpawnTestStation()
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
        /// 双引擎测试场景：
        /// 核热引擎(液氢) + 氢储罐(满 5000kg)、离子引擎(氙) + 氙储罐(满 1000kg)，
        /// 规划两条各自绑定网络的机动（①核热径向 ②离子切向），并打开机动队列窗口。
        /// </summary>
        private void SpawnDualEngineTest()
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
        private CompPropellantTank PlaceBuilding(Map map, string defName, IntVec3 cell)
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

        /// <summary>
        /// 炮塔对射测试场景（M4-F2 验收）：
        /// ① 玩家空间站（圆轨）内布置定位机 + 上下双侧炮塔，自动完成绑定；
        /// ② 敌对空间站（不同 factionId）置于玩家旁 80km，近似共轨速度；
        /// ③ 定位机 Auto 模式自动索敌 → 半球射界判定 → 齐射 → 3D 射弹命中。
        /// </summary>
        private void SpawnTurretTest()
        {
            // 同步轨道（高度约 28,560km，周期 24h）：相对角速度极小，敌对近似共轨漂移极慢，适合长时观察
            var (playerEntity, playerGroup, playerMap) = CreateTestStation(altitudeMeters: 28560000.0);
            if (playerEntity == null || playerGroup == null || playerMap == null)
                return;

            // 侧倾 30°（绕 Z 轴）：两船同在轨道面（世界 y≈0），若不倾斜，目标在船体局部 y≈0，
            // 上锚点(需 y>0)/下锚点(需 y<0) 全被半球射界过滤 → 无炮可开。侧倾后目标落入上炮半球。
            playerEntity.SetRotation(Quaternion.Euler(0f, 0f, 30f));

            // 1. 布置定位机 + 上下双侧炮塔（Both = 2 锚点）
            var designator = PlaceBuildingRaw(playerMap, "PF_Building_TargetDesignator", new IntVec3(28, 0, 30));
            var turret = PlaceBuildingRaw(playerMap, "PF_Building_Turret_Cannon_Both", new IntVec3(30, 0, 30));
            if (designator == null || turret == null)
                return;

            // 2. 自动绑定（模拟玩家 Gizmo 绑定结果）：上下锚点都绑到定位机
            var desComp = designator.TryGetComp<CompMapDesignator>();
            var turretComp = turret.TryGetComp<CompMapTurret>();
            if (desComp == null || turretComp == null)
            {
                Log.Error("[TurretTest] 定位机/炮塔 Comp 缺失");
                return;
            }
            desComp.RegisterTurret(turret, TurretMountSide.Top);
            turretComp.GetMount(TurretMountSide.Top).boundDesignator = designator;
            desComp.RegisterTurret(turret, TurretMountSide.Bottom);
            turretComp.GetMount(TurretMountSide.Bottom).boundDesignator = designator;

            // 3. 敌对空间站：玩家旁 20km，真正共轨（同高度圆轨 → 相对静止 vRel≈0，提前量 0 精确命中）。
            //    Part 尺寸按地图格子对应（1 格 = 1 米），不取巧放大目标。
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null) return;
            DVector3 playerPos = playerGroup.IsCircularOrbit
                ? playerGroup.GetCircularPositionAtTick(TimeScaleManager.GetPhysicsTickSafe())
                : playerGroup.CurrentOrbit.position;
            DVector3 enemyPos = playerPos + new DVector3(20000, 0, 0);
            int enemyGroupId = GetNextGroupId(spaceGame);
            int enemyEntityId = GetNextEntityId(spaceGame);
            int enemyFactionId = (parent.Faction?.loadID ?? 0) + 1;
            spaceGame.CreateStation(enemyEntityId, enemyGroupId, enemyFactionId, enemyPos, 60, 60, "敌对空间站");
            var enemyGroup = spaceGame.GetGroupById(enemyGroupId);
            if (enemyGroup != null)
                enemyGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 敌对空间站绕 Z 轴旋转 45°：射弹（世界水平 +x 来向）在船体局部系变成 45° 俯冲，
            // 命中船体上部缓冲区（y≈21m）→ 贯穿入口高度 ≈26m，炮弹从上方斜降地面（"上方 45 度挨打"）
            var enemyEntity = spaceGame.GetEntityById(enemyEntityId);
            if (enemyEntity != null)
                enemyEntity.SetRotation(Quaternion.Euler(0f, 0f, 45f));

            // 4. 打开 3D 视角观察对射，并跳转到玩家空间站地图看炮塔/开火闪光
            Find.WindowStack.Add(new Window_SpaceBattleView3D(playerGroup));
            if (playerMap != null)
                CameraJumper.TryJump(playerMap.Center, playerMap);

            Log.Message(string.Format("[TurretTest] 场景就绪: 玩家 Group {0} 炮塔已绑定位机(上/下)，敌对 Group {1} 位于 20km 外，Auto 索敌启动",
                playerGroup.GroupId, enemyGroupId));
            Messages.Message(string.Format("炮塔对射测试场景就绪：玩家 Group {0}，敌对 Group {1} 位于 20km 外。",
                playerGroup.GroupId, enemyGroupId), MessageTypeDefOf.TaskCompletion);
        }

        /// <summary>在地图指定格子放置一个建筑，返回 Thing（无 faction，测试用）。</summary>
        private Thing PlaceBuildingRaw(Map map, string defName, IntVec3 cell)
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
    }
}
