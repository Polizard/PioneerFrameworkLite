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
