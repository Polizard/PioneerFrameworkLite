using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Landing
{
    /// <summary>
    /// 物理接地只登记落点并保留船图。首次地表地图请求必须来自显式操作；
    /// 失败时维持接地暂停状态，不能恢复地下轨道或回退到 DestroyGroup。
    /// </summary>
    public static class SurfaceTouchdownService
    {
        private static bool HasSourceMap(SpaceGameComp sg, SpaceEntityGroup group) => group.GetEntityIds().Any(id =>
                sg.GetEntityById(id) is SpaceEntity e && (e.HasMap || e.IsMapArchived || e.GetMapIds().Count > 0));

        internal static bool CanHandle(SpaceGameComp sg, SpaceEntityGroup group, int playerFactionId) =>
            group.FactionId == playerFactionId && HasSourceMap(sg, group);

        internal static LandingTaskData Capture(SpaceGameComp sg, LandingTransitManager manager,
            SpaceEntityGroup group, SurfaceTouchdownData snapshot)
        {
            if (!double.IsFinite(snapshot.GroundSpeed) || snapshot.GroundSpeed < 0 || !SpaceConstants.IsSafeLandingSpeed(snapshot.GroundSpeed) ||
                !double.IsFinite(snapshot.PhysicsTick) || !double.IsFinite(snapshot.LatitudeRad) || !double.IsFinite(snapshot.LongitudeRad) ||
                !Finite(snapshot.PositionBody) || !Finite(snapshot.VelocityBody))
                throw new InvalidOperationException("Invalid safe touchdown snapshot.");
            var existing = manager.FindForGroup(group.GroupId);
            if (existing != null) return existing;
            if (sg.GetGroupById(group.GroupId) != group) throw new InvalidOperationException("Missing touchdown group.");
            if (!string.IsNullOrEmpty(group.LandingTaskId)) throw new InvalidOperationException("Group already belongs to a landing task.");
            var task = new LandingTaskData
            {
                GroupId = group.GroupId, TargetBodyId = group.CelestialBodyId,
                EntityIds = group.GetEntityIds().OrderBy(id => id).ToList(),
                Status = LandingTransitStatus.AwaitingMapRequest, Touchdown = snapshot,
                StartPosition = group.CurrentOrbit.positionSystem,
                DepartureCenter = (SystemVec3)(group.CurrentOrbit.positionSystem.v - snapshot.PositionBody)
            };
            // 在复杂预检前保存所有已知地图身份，失败任务仍保护源图。
            var byMap = new SortedDictionary<int, LandingSourceMapData>();
            foreach (int id in task.EntityIds)
            {
                var entity = sg.GetEntityById(id);
                if (entity == null) continue;
                foreach (int mapId in entity.GetMapIds().Append(entity.MapId).Where(id => id >= 0).Distinct())
                {
                    if (!byMap.TryGetValue(mapId, out var source)) byMap[mapId] = source = new LandingSourceMapData { MapId = mapId };
                    source.EntityIds.Add(id);
                }
            }
            task.SourceMaps = byMap.Values.ToList();
            manager.Register(task);
            task.Suspension = LandingGroupLifecycle.SuspendState(group, task.TaskId);
            // 索引清理失败时 task 和暂停状态均已留存，由调用方记为 Blocked。
            sg.RemoveLandingGroupFromIndexes(group, task.Suspension);
            if (SteeringController.IsOpen && SteeringController.GroupId == group.GroupId) SteeringController.Close();
            return task;
        }

        private static bool Finite(DVector3 value) => double.IsFinite(value.x) && double.IsFinite(value.y) && double.IsFinite(value.z);

        public static bool TryHandle(SpaceGameComp sg, SpaceEntityGroup group, DVector3 positionBody,
            DVector3 velocityBody, double physicsTick, double groundSpeed, double latitudeRad, double longitudeRad)
        {
            // 无图探针及导弹的旧结算不需要访问玩家阵营或降落管理器。
            if (!HasSourceMap(sg, group)) return false;
            if (Faction.OfPlayer == null || group.FactionId != Faction.OfPlayer.loadID) return false;
            var manager = Current.Game.GetComponent<LandingTransitManager>();
            // 已登记的接地通知幂等：不重算落点、改写阶段或重复显示消息。
            if (manager.FindForGroup(group.GroupId) != null) return true;
            LandingTaskData? task = null;
            try
            {
                task = Capture(sg, manager, group, new SurfaceTouchdownData { PositionBody = positionBody,
                    VelocityBody = velocityBody, PhysicsTick = physicsTick, GroundSpeed = groundSpeed,
                    LatitudeRad = latitudeRad, LongitudeRad = longitudeRad });
                var bodies = Current.Game.GetComponent<CelestialBodyManager>();
                var layer = Launch.GetSurfaceState.GetLayerForBody(bodies, task.TargetBodyId);
                int tileId = layer == null ? -1 : Launch.GetSurfaceState.EciToTile(positionBody, bodies, task.TargetBodyId, physicsTick);
                if (tileId >= 0) task.TargetTile = new PlanetTile(tileId, layer!);
                var report = CheckSupported(sg, task);
                if (!report.Success) { LandingTransitService.Fail(task, report, LandingTransitStatus.AwaitingMapRequest, true); }
                LandingTransitService.EnsureMarker(task);
                if (report.Success)
                    Messages.Message("PFL_TouchdownRegistered".Translate(group.GroupId), MessageTypeDefOf.TaskCompletion, false);
                else
                    Messages.Message(report.Text, MessageTypeDefOf.RejectInput, false);
            }
            catch (Exception e)
            {
                task ??= manager.FindForGroup(group.GroupId);
                if (task != null) LandingTransitService.Fail(task, LandingReport.Reject("PFL_LandingExecutionError", e.Message), LandingTransitStatus.AwaitingMapRequest, true);
                else
                {
                    group.IsActive = group.IsMovable = group.IsCollidable = false;
                    Log.Error("[PFL Touchdown] Capture failed; group retained: " + e);
                }
            }
            return true;
        }

        private static LandingReport CheckSupported(SpaceGameComp sg, LandingTaskData task)
        {
            if (task.TargetBodyId != 1 || !task.TargetTile.Valid || task.SourceMaps.Count != 1 || task.EntityIds.Count != 1)
                return LandingReport.Reject("PFL_TouchdownUnsupported");
            foreach (int id in task.EntityIds)
            {
                var entity = sg.GetEntityById(id);
                if (entity == null || entity.IsMapArchived || LandingSourceMapService.FindMap(task.SourceMaps[0].MapId) == null)
                    return LandingReport.Reject("PFL_LandingSourceUnavailable", "Touchdown requires one loaded ship map.");
            }
            return DescribeDestinationFailure(task, LandingDestinationService.Check(task.TargetTile, task.TargetBodyId, new IntVec2(250, 250)));
        }

        // 已发生的物理接地使用事故表述；主动降落预检和源数据故障保留原诊断。
        internal static LandingReport DescribeDestinationFailure(LandingTaskData task, LandingReport report) =>
            task.Touchdown != null && !report.Success && report.ReasonKey == "PFL_LandingUnsupportedDestination"
                ? LandingReport.Reject("PFL_TouchdownCrashLanding", task.GroupId)
                : report;

        internal static bool BeginMapRequest(LandingTaskData task, Func<LandingReport> preflight,
            Action<LandingTaskData> prepare, out LandingReport report)
        {
            if (task.Touchdown == null || task.Status != LandingTransitStatus.AwaitingMapRequest)
            { report = LandingReport.Reject("PFL_LandingInvalidState"); return false; }
            report = preflight();
            if (!report.Success) { task.FailureReason = report.Text; return false; }
            if (!task.TryTransition(LandingTransitStatus.PreparingDestination, out var reason))
            { report = LandingReport.Reject(reason); return false; }
            task.FailureReason = "";
            prepare(task);
            return true;
        }

        public static bool TryRequestDestinationMap(Game game, string taskId, out LandingReport report)
        {
            if (!ReferenceEquals(game, Current.Game)) { report = LandingReport.Reject("PFL_LandingInvalidState"); return false; }
            var task = game.GetComponent<LandingTransitManager>().FindTask(taskId);
            var sg = game.GetComponent<SpaceGameComp>();
            var group = task == null ? null : sg.GetGroupById(task.GroupId);
            if (task == null || group == null || group.LandingTaskId != task.TaskId || group.FactionId != Faction.OfPlayer.loadID)
            { report = LandingReport.Reject("PFL_LandingGroupUnavailable"); return false; }
            try
            {
                return BeginMapRequest(task, () => PrepareSources(sg, group, task), LandingWorkflow.Arrive, out report);
            }
            catch (Exception e)
            {
                report = LandingReport.Reject("PFL_LandingExecutionError", e.Message);
                LandingTransitService.Fail(task, report, task.Status); return false;
            }
        }

        private static LandingReport PrepareSources(SpaceGameComp sg, SpaceEntityGroup group, LandingTaskData task)
        {
            var report = CheckSupported(sg, task); if (!report.Success) return report;
            if (!LandingSourceMapService.TryCollect(sg, group, out var maps, out var mapless, out report)) return report;
            if (maps.Count != 1 || mapless.Count != 0 || maps[0].MapId != task.SourceMaps[0].MapId || !maps[0].EntityIds.SequenceEqual(task.SourceMaps[0].EntityIds))
                return LandingReport.Reject("PFL_LandingSourceUnavailable", "Source map identity changed.");
            var map = LandingSourceMapService.FindMap(maps[0].MapId)!;
            if (!LandingFootprintService.TryCollect(map, out var footprint, out report)) return report;
            report = DescribeDestinationFailure(task, LandingDestinationService.Check(task.TargetTile, task.TargetBodyId, LandingDestinationService.RequiredMapSize(
                new IntVec2(footprint!.SourceBounds.Width, footprint.SourceBounds.Height))));
            if (!report.Success) return report;
            task.SourceMaps[0].SourceCells = footprint.SourceCells.ToList();
            return LandingReport.Ok();
        }
    }
}
