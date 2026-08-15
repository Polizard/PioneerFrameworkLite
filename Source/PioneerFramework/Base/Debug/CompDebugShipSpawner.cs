using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

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

        /// <summary>创建 500km LEO 圆轨道的测试空间站（含地图 + 单 part），返回实体/群组/地图。</summary>
        private (SpaceEntity entity, SpaceEntityGroup group, Map map) CreateTestStation()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Messages.Message("SpaceGameComp 未找到！", MessageTypeDefOf.RejectInput);
                return (null, null, null);
            }

            int groupId = GetNextGroupId(spaceGame);
            int entityId = GetNextEntityId(spaceGame);

            // 500km LEO 轨道
            double altitudeMeters = 500000.0;
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
    }
}
