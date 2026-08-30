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
                defaultDesc = "创建玩家空间站（定位机+整体式炮塔，自动绑定）+ 敌对空间站（20km 外赤道面），触发 Auto 索敌齐射，并自动开启详细战斗日志。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnTurretTest();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "部署全方位炮塔对射场景",
                defaultDesc = "6 座整体式炮塔各配一台定位机（Manual 各指一个敌站），敌站分布在玩家船六方位（±x/±z 赤道面 + ±y 法向 20km）。一次齐射验证 360° 射界解除 + 六面出膛碰撞点，日志看 [炮塔出膛] 出膛面 0~5。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnTurretOmniTest();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "部署精确炮击测试场景",
                defaultDesc = "目标敌舰位于玩家船正下方（世界 -y 20km），定位机预设精确炮击点 (40,30)（船心偏移 +10m）。验证：出膛面 y-（面2）、瞄准偏移、红色标记、落点格≈炮击点。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnTurretPrecisionTest();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "部署定向能对射场景",
                defaultDesc = "玩家空间站（定位机 Auto + 激光炮塔，自动绑定）+ 敌对空间站 20km 外赤道面（vRel≈0）。激光即时命中无提前量，验证：[激光发射]（射程 50km）→[激光命中!]（Part HP 下降）→[贯穿生成]（贯通入口→落点弹道）→ 光束 60 tick 残留。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnRayTurretTest();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "部署定向能遮挡验证",
                defaultDesc = "玩家站 + 激光炮塔，定位机 Manual 指向 20km 远敌站，近敌站 10km 挡在光束路径上。验证遮挡语义：光束只命中最近一艘（近站掉血，[激光命中!] 指向近站）。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnRayOcclusionTest();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "部署定向能旋转命中验证",
                defaultDesc = "敌站绕 Z 轴旋转 45°，光束从 +x 水平来向在船体局部系变 45° 俯冲。验证旋转正确 part 检测：[激光slab] 局部系线段倾斜、[激光part] 命中局部点 y≠0、船壳入口 y>0（从上斜降地面）。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnRayRotatedTest();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "部署定向能多目标齐射",
                defaultDesc = "4 敌站四方位（±x/±z 20km 赤道面共轨）+ 4 组激光炮塔定位机（Manual 各指一个）。一次齐射 4 发，每发独立判定链 [激光发射]→[激光slab]→[激光part]→[激光命中!]→[激光命中判定]，验证多目标同时命中。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnRayMultiTest();
                }
            };

            yield return new Command_Toggle
            {
                defaultLabel = "详细战斗日志",
                defaultDesc = "开关炮塔战斗全链详细日志（齐射/炮塔几何/出膛/视觉弹/命中/贯穿）+ 射弹飞行探针（30 tick/条）。",
                icon = TexButton.Info,
                isActive = () => CombatManager.DebugDetailedCombat,
                toggleAction = ToggleDetailedCombat
            };

            yield return new Command_Toggle
            {
                defaultLabel = "详细操舵日志",
                defaultDesc = "开关操舵/自动对准详细日志：手动平移/旋转/SAS（[Steer] 10 tick/条）+ 自动对准开始/完成/进度（[Align] 30 tick/条）。",
                icon = TexButton.Info,
                isActive = () => SpaceGameComp.DebugDetailedSteering,
                toggleAction = () => SpaceGameComp.DebugDetailedSteering = !SpaceGameComp.DebugDetailedSteering
            };

            yield return new Command_Action
            {
                defaultLabel = "部署操舵测试场景",
                defaultDesc = "玩家站（操舵台 + 核热引擎 Rot4.East 主推进 + 横向/垂直推进器 + 氢罐满）→ 自动打开操舵 HUD 并开启详细操舵日志。验证：按键平移（沿船轴方向出 Δv）、旋转（动量轮）、SAS 对准、动作组过滤、输入打断清队列。",
                icon = TexButton.Info,
                action = delegate
                {
                    SpawnSteeringTest();
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

            yield return new Command_Action
            {
                defaultLabel = "部署跳帮测试场景",
                defaultDesc = "创建玩家空间站（母船，含突击兵）+ 敌对空间站（10km 外，含守军），自动触发单兵 EVA 跳帮：DeSpawn → 飞行 → 降落 Spawn pawn 进目标站 Map。",
                icon = TexCommand.FireAtWill,
                action = delegate
                {
                    SpawnBoardingTest();
                }
            };

            yield return new Command_Action
            {
                defaultLabel = "打开跳帮测试面板",
                defaultDesc = "跳帮测试面板：EVA 单兵/多兵/穿梭机/高速撞击场景一键部署 + 详细日志/探针开关 + 单位实时状态 + 结算历史与着陆验证。",
                icon = TexButton.Info,
                action = delegate
                {
                    Find.WindowStack.Add(new Window_BoardingTest());
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

        /// <summary>
        /// 跳帮（登舰突击）测试场景（M3 验收）：
        /// ① 玩家空间站（圆轨，母船）内置突击兵；
        /// ② 敌对空间站（共轨 10km 外）内置守军；
        /// ③ 自动触发单兵 EVA 跳帮：母船 pawn DeSpawn → 临时 3D 实体 + 接近引导（瞄准移动落点）
        ///    → 降落判定通过 → pawn Spawn 进目标站 Map。
        /// 实现委托 BoardingTestScenarios（与测试面板共用同一场景逻辑）。
        /// </summary>
        private void SpawnBoardingTest()
        {
            BoardingTestScenarios.DeployEvaTest(1);
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
        private void SpawnSteeringTest()
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
        private void SpawnTurretTest()
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
        /// 全方位炮塔对射测试场景（2026-08-29，整体式 360° 验证）：
        /// 6 座整体式炮塔各配一台定位机（Manual 模式各指一个敌站），敌站分布在玩家船六个方位（20km）：
        ///   ±x / ±z（赤道面，共轨）→ 期望出膛面 1/0/5/4（四侧面）
        ///   ±y（轨道面法向）→ 期望出膛面 3/2（上/下表面）
        /// 一次齐射即可通过 [炮塔出膛] 日志验证：360° 射界解除（赤道面旧半球逻辑无炮可开）+
        /// 六面出膛碰撞点准确性。玩家船不旋转（船体局部 = 世界轴，方位对应直白）。
        /// </summary>
        private void SpawnTurretOmniTest()
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

            int enemyFactionId = (parent.Faction?.loadID ?? 0) + 1;
            var enemyGroupIds = new List<int>();
            string mapping = "";
            foreach (var d in directions)
            {
                int gid = GetNextGroupId(spaceGame);
                int eid = GetNextEntityId(spaceGame);
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

        /// <summary>
        /// 精确炮击测试场景（2026-08-29）：目标敌舰位于玩家船正下方（世界 -y，20km）。
        /// 验证点：
        ///   ① 正下方目标 → 出膛面 y-（面2），出膛点局部 (≈0,-25,≈0)；
        ///   ② 精确炮击点 (40,30)（60×60 站中心(30,30) → 局部偏移 +10m x）→ [精确炮击] 日志显示偏移；
        ///   ③ 敌舰地图 (40,30) 上出现红色精确炮击标记；
        ///   ④ 命中落点格 ≈ (40,30)（[贯穿生成] 验证）。
        /// </summary>
        private void SpawnTurretPrecisionTest()
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
            int enemyGroupId = GetNextGroupId(spaceGame);
            int enemyEntityId = GetNextEntityId(spaceGame);
            int enemyFactionId = (parent.Faction?.loadID ?? 0) + 1;
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
        private void SpawnRayTurretTest()
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
            int enemyGroupId = GetNextGroupId(spaceGame);
            int enemyEntityId = GetNextEntityId(spaceGame);
            int enemyFactionId = (parent.Faction?.loadID ?? 0) + 1;
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
        private void SpawnRayOcclusionTest()
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
            int enemyFactionId = (parent.Faction?.loadID ?? 0) + 1;

            // 1. 近敌站（挡路）：玩家 +x 10km
            DVector3 nearPos = playerPos + new DVector3(10000, 0, 0);
            int nearGroupId = GetNextGroupId(spaceGame);
            int nearEntityId = GetNextEntityId(spaceGame);
            spaceGame.CreateStation(nearEntityId, nearGroupId, enemyFactionId, nearPos, 60, 60, "遮挡敌站(近10km)");
            var nearGroup = spaceGame.GetGroupById(nearGroupId);
            if (nearGroup != null)
                nearGroup.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);

            // 2. 远敌站（瞄准目标）：玩家 +x 20km（近站正挡在光束路径上）
            DVector3 farPos = playerPos + new DVector3(20000, 0, 0);
            int farGroupId = GetNextGroupId(spaceGame);
            int farEntityId = GetNextEntityId(spaceGame);
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
        private void SpawnRayRotatedTest()
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
            int enemyGroupId = GetNextGroupId(spaceGame);
            int enemyEntityId = GetNextEntityId(spaceGame);
            int enemyFactionId = (parent.Faction?.loadID ?? 0) + 1;
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
        private void SpawnRayMultiTest()
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
            int enemyFactionId = (parent.Faction?.loadID ?? 0) + 1;
            var enemyGroupIds = new List<int>();
            string mapping = "";
            foreach (var d in directions)
            {
                int gid = GetNextGroupId(spaceGame);
                int eid = GetNextEntityId(spaceGame);
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
