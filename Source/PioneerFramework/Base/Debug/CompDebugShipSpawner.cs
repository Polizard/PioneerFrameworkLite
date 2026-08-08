using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Components;
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
    /// 调试用 ThingComp：提供一个 Gizmo，用于创建绕地球圆轨道的
    /// SpaceEntityGroup，选择已保存的飞船设计，并加满工质罐。
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
                defaultLabel = "部署飞船设计",
                defaultDesc = "选择一个已保存的飞船设计，创建绕地球圆轨道的飞船。",
                icon = TexCommand.DesirePower,
                action = delegate
                {
                    var designs = Ship3D.GetSavedBlueprints();
                    if (designs.Count == 0)
                    {
                        Messages.Message("没有已保存的飞船设计！请先在3D飞船搭建器中储存设计。",
                            MessageTypeDefOf.RejectInput);
                        return;
                    }

                    var options = new List<FloatMenuOption>();
                    foreach (var name in designs)
                    {
                        string capturedName = name;
                        options.Add(new FloatMenuOption(capturedName, () =>
                        {
                            SpawnShip(capturedName);
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };

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
        }

        private void SpawnShip(string designName)
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Messages.Message("SpaceGameComp 未找到！", MessageTypeDefOf.RejectInput);
                return;
            }

            var ship = Ship3D.LoadFromFile(designName);
            if (ship == null)
            {
                Messages.Message("无法加载设计 '" + designName + "'！", MessageTypeDefOf.RejectInput);
                return;
            }

            // 获取新的唯一 ID
            int groupId = GetNextGroupId(spaceGame);
            int entityId = GetNextEntityId(spaceGame);

            // 创建轨道：默认 500km LEO
            double altitudeMeters = 500000.0;
            double earthRadiusMeters = SpaceConstants.PlanetRadiusMeters;
            double orbitRadiusMeters = earthRadiusMeters + altitudeMeters;

            double angle = System.DateTime.Now.Millisecond * 0.001 * System.Math.PI * 2;
            double x = System.Math.Cos(angle) * orbitRadiusMeters;
            double z = System.Math.Sin(angle) * orbitRadiusMeters;
            double mu = spaceGame.GetBodyGM(EARTH_BODY_ID);
            double orbitalSpeed = System.Math.Sqrt(mu / orbitRadiusMeters);

            var group = new SpaceEntityGroup(groupId, Faction.OfPlayer?.loadID ?? 0)
            {
                IsMovable = true,
                IsCollidable = true,
                CelestialBodyId = EARTH_BODY_ID
            };
            group.CurrentOrbit = new OrbitState
            {
                position = new DVector3(x, 0, z),
                velocity = new DVector3(-System.Math.Sin(angle) * orbitalSpeed, 0, System.Math.Cos(angle) * orbitalSpeed),
                rotation = Quaternion.identity
            };

            // 在注册前先创建 Entity，设置 GroupId
            var entity = new SpaceEntity(entityId, group.FactionId)
            {
                GroupId = groupId,
                IsCollidable = true,
                IsMovable = true,
                IsAffectedByGravity = true
            };
            entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
            entity.SetRotation(Quaternion.identity);
            entity.CanInteract = true;

            // 从设计生成 SpacePart
            var spaceParts = ship.ToSpaceParts();
            entity.SetParts(spaceParts);

            // 存储设计引用，供 3D 视角使用
            entity.shipDesign = ship;

            // 注册 Group 和 Entity
            spaceGame.RegisterGroup(group);
            group.EnterCircularOrbit(Find.TickManager.TicksGame, EARTH_BODY_ID);
            spaceGame.RegisterEntity(entity);

            // 分配 dV 以使自动交会机动队列正常工作
            entity.RemainingDeltaV = 10000000f;
            entity.MaxDeltaV = 10000000f;

            // 计算工质罐信息（组件驱动）
            int tankCount = 0;
            float totalPropellant = 0;
            foreach (var p in ship.parts)
            {
                var tank = p.GetComponent<PropellantTankComponentData>();
                if (tank != null)
                {
                    tankCount++;
                    totalPropellant += tank.wetMass - tank.dryMass;
                }
            }

            Messages.Message(
                string.Format("飞船 '{0}' (Group {1}) 已部署到 {2}km LEO。\n部件数: {3}，工质罐: {4}个 (总工质{5:F0}kg)",
                    designName, groupId,
                    altitudeMeters / 1000.0,
                    ship.parts.Count, tankCount, totalPropellant),
                MessageTypeDefOf.TaskCompletion);

            Log.Message(string.Format("[ShipSpawner] Created Group {0} with Ship3D '{1}', {2} parts, {3} tanks",
                groupId, designName, ship.parts.Count, tankCount));
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

        private void SpawnTestStation()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (spaceGame == null)
            {
                Messages.Message("SpaceGameComp 未找到！", MessageTypeDefOf.RejectInput);
                return;
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

            Log.Message(string.Format("[StationTest] Created station: entityId={0}, groupId={1}, entity.Parts.Count={2}, MapId={3}",
                entityId, groupId, entity.Parts.Count, entity.MapId));

            // 打印双向映射验证
            var map = SpaceMapGenerator.FindPocketMap(entity.MapId);
            if (map != null)
            {
                var mapComp = map.GetComponent<SpaceMapComp>();
                Log.Message(string.Format("[StationTest] SpaceMapComp: ownerEntityId={0}, linkedPartId={1}",
                    mapComp?.ownerEntityId ?? -1, mapComp?.linkedPartId ?? -1));
            }
            foreach (var p in entity.Parts)
                Log.Message(string.Format("[StationTest] Part {0} linkedMapId={1}", p.partId, p.linkedMapId));

            // 打开 3D 视角
            if (group != null)
            {
                var view3D = new Window_SpaceBattleView3D(group);
                Find.WindowStack.Add(view3D);
            }

            Messages.Message(string.Format("空间站已创建 (Group {0})，Map {1}，3D视角已打开。",
                groupId, entity.MapId), MessageTypeDefOf.TaskCompletion);
        }
    }
}
