using System;
using System.Linq;
using PioneerFramework.Components;
using PioneerFramework.Landing;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 有图接地测试：仅创建本次试验船舱；落点地表图必须由世界标记上的按钮请求。
    /// 场景日志是人工验收的核对数据，不把部署成功记作游戏验收通过。
    /// </summary>
    public static class SurfaceTouchdownAcceptanceRunner
    {
        public static void SpawnSafe() => Spawn(20);
        public static void SpawnCrash() => Spawn(300);

        private static void Spawn(double descentSpeed)
        {
            if (!Prefs.DevMode || Current.Game == null) return;
            var sg = Current.Game.GetComponent<SpaceGameComp>();
            var bodies = Current.Game.GetComponent<CelestialBodyManager>();
            var tile = Find.WorldSelector.SelectedTile;
            var layer = Launch.GetSurfaceState.GetLayerForBody(bodies, 1);
            if (!tile.Valid || tile.Layer != layer)
            {
                Messages.Message("请先在世界视图选择一个地球陆地地块，再布置带图接地测试。", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var check = LandingDestinationService.Check(tile, 1, new IntVec2(250, 250));
            if (!check.Success) { Messages.Message(check.Text, MessageTypeDefOf.RejectInput, false); return; }
            Game session = Current.Game;
            LongEventHandler.QueueLongEvent(() =>
            {
                if (!ReferenceEquals(session, Current.Game)) return;
                double tick = TimeScaleManager.GetPhysicsTickSafe();
                double omega = Launch.GetSurfaceState.RotationOmega(bodies, 1);
                var surface = Launch.GetSurfaceState.TileToEci(tile.tileId, bodies, 1, tick);
                var radial = surface.normalized;
                var pos = surface + radial * 200;
                var vel = SurfaceFrame.SurfaceVelocity(surface, omega) - radial * descentSpeed;
                int groupId = sg.AllocGroupId(), entityId = sg.AllocEntityId();
                var map = SpaceMapGenerator.GeneratePocketMap(32, 32, "TouchdownTest_" + groupId);
                if (map == null) throw new InvalidOperationException("Test cabin map generation failed.");
                var group = new SpaceEntityGroup(groupId, Faction.OfPlayer.loadID) { CelestialBodyId = 1 };
                group.CurrentOrbit = new OrbitState { positionSystem = (SystemVec3)pos, velocitySystem = (SystemVec3)vel, rotation = DQuaternion.identity };
                var entity = SpaceMapGenerator.CreateStationEntity(entityId, Faction.OfPlayer.loadID, pos, 32, 32);
                entity.GroupId = groupId; entity.SetPositionVelocity(DVector3.zero, DVector3.zero);
                for (int x = 10; x < 22; x++)
                    for (int z = 10; z < 22; z++) map.terrainGrid.SetTerrain(new IntVec3(x, 0, z), TerrainDefOf.Concrete);
                var bed = ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.Steel);
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel);
                bed.SetFaction(Faction.OfPlayer); wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(bed, new IntVec3(14, 0, 14), map);
                GenSpawn.Spawn(wall, new IntVec3(12, 0, 12), map);
                wall.HitPoints = Math.Max(1, wall.MaxHitPoints - 20);
                var steel = ThingMaker.MakeThing(ThingDefOf.Steel); steel.stackCount = 37;
                GenSpawn.Spawn(steel, new IntVec3(17, 0, 16), map);
                var pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(pawn, new IntVec3(16, 0, 16), map);
                SpaceMapGenerator.LinkEntityToMap(entity, map);
                sg.RegisterGroup(group); sg.RegisterEntity(entity); group.AddEntity(entityId);
                // 重型探针使高速场景在 200 m 内保持高速侧；仍以实际接地速度判定。
                var ballast = new SpacePart { partId = 2, defName = "PF_ShipPart_Cube", boundsSizeShip = new DVector3(1, 1, 1), hp = 100 };
                ballast.AddComponent(new HullMassComponent { mass = 20_000_000f });
                entity.SetParts(entity.Parts.Concat(new[] { ballast }).ToList());
                sg.SyncGroupToCoarseGrid(groupId, pos);
                Log.Message($"[PFL Touchdown test] DEPLOYED G{groupId} entity={entityId} sourceMap={map.uniqueID} target={tile} initialDescent={descentSpeed}m/s physicsTick={tick}");
                foreach (var thing in new Thing[] { pawn, bed, wall, steel })
                    Log.Message($"[PFL Touchdown test] CONTENT G{groupId} id={thing.ThingID} cell={thing.Position} hp={thing.HitPoints} count={thing.stackCount}");
                Messages.Message($"已布置带图接地探针 G{groupId}（{descentSpeed:F0} m/s，200 m）。恢复时间并观察 PFView；安全接地后应只出现世界标记，地图须手动请求。", MessageTypeDefOf.TaskCompletion, false);
            }, "PFL_LandingPreparing", false, e => Log.Error("[PFL Touchdown test] " + e));
        }

        public static void Report()
        {
            if (!Prefs.DevMode || Current.Game == null) return;
            var sg = Current.Game.GetComponent<SpaceGameComp>();
            foreach (var task in Current.Game.GetComponent<LandingTransitManager>().Tasks.Where(t => t.Touchdown != null))
            {
                var group = sg.GetGroupById(task.GroupId);
                Log.Message($"[PFL Touchdown test] task={task.TaskId} G{task.GroupId} state={task.Status} active={group?.IsActive} tile={task.TargetTile} speed={task.Touchdown!.GroundSpeed:F3} source=[{string.Join(",", task.SourceMaps.Select(s => s.MapId + ":" + s.Status))}] destination={task.DestinationMapId} maps={Find.Maps.Count} failure={task.FailureReason}");
                var destination = LandingSourceMapService.FindMap(task.DestinationMapId);
                if (destination == null) continue;
                var records = destination.GetComponent<LandingMapRecords>();
                if (records == null) continue;
                foreach (var record in records.Records.Where(r => r.TaskId == task.TaskId))
                    foreach (var id in record.ThingIds)
                    {
                        var thing = destination.listerThings.AllThings.FirstOrDefault(t => t.ThingID == id);
                        Log.Message($"[PFL Touchdown test] ARRIVED id={id} present={thing != null} cell={thing?.Position} hp={thing?.HitPoints} count={thing?.stackCount}");
                    }
            }
        }
    }
}
