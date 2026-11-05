using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingDestinationService
    {
        public static IntVec2 RequiredMapSize(IntVec2 size) => new IntVec2(Math.Max(250, size.x + 20), Math.Max(250, size.z + 20));
        public static bool IsAllowed(bool friendly, bool quest, bool supported, bool landable) => friendly && !quest && supported && landable;
        public static T ResolveExisting<T>(Func<T?> find, Func<T> create) where T : class => find() ?? create();
        public static Map? FindExisting(PlanetTile tile) => Current.Game == null ? null : Find.Maps.FirstOrDefault(m => m.Tile == tile && m.Parent is not SpacecraftMapParent);

        public static LandingReport Check(PlanetTile target, int bodyId, IntVec2 requiredSize)
        {
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            var layer = mgr == null ? null : Launch.GetSurfaceState.GetLayerForBody(mgr, bodyId);
            if (!target.Valid || layer == null || target.Layer != layer || target.tileId >= layer.TilesCount)
                return LandingReport.Reject("PFL_LandingWrongLayer");
            if ((requiredSize.x > 400 || requiredSize.z > 400) && FindExisting(target) == null) return LandingReport.Reject("PFL_LandingTooLarge");
            if (target.Tile.PrimaryBiome == null || target.Tile.PrimaryBiome.impassable || target.Tile.hilliness == Hilliness.Impassable)
                return LandingReport.Reject("PFL_LandingUnsupportedDestination");
            foreach (var obj in Find.WorldObjects.ObjectsAt(target))
            {
                if (obj is not MapParent parent) continue;
                bool supported = parent is LandedShipMapParent || parent is Settlement || parent.GetType() == typeof(MapParent);
                if (!IsAllowed(parent.Faction == null || parent.Faction == Faction.OfPlayer, !parent.questTags.NullOrEmpty(), supported, true))
                    return LandingReport.Reject("PFL_LandingUnsupportedDestination");
                if (!parent.HasMap && (parent is not LandedShipMapParent own || own.EverGenerated))
                    return LandingReport.Reject("PFL_LandingDestinationUnavailable");
                if (parent.GetType() == typeof(MapParent)) return LandingReport.Reject("PFL_LandingUnsupportedDestination");
            }
            return LandingReport.Ok();
        }

        public static void BeginResolve(LandingTaskData task, Action<Map> ready, Action<LandingReport> failed)
        {
            Game session = Current.Game;
            string taskId = task.TaskId;
            int targetHandle = task.TargetBodyId;
            var targetTile = task.TargetTile;
            var bodies = session.GetComponent<CelestialBodyManager>();
            Celestial.CelestialTargetToken? target = null;
            if (bodies?.TryGetCelestialRuntime(out _) == true)
            {
                var captured = bodies.CaptureTarget(targetHandle);
                if (!captured.Success) { failed(LandingReport.Reject("PFL_LandingDestinationUnavailable")); return; }
                target = captured.Value;
            }
            bool Pending() => ReferenceEquals(session, Current.Game) &&
                ReferenceEquals(session.GetComponent<LandingTransitManager>()?.FindTask(taskId), task) &&
                task.Status == LandingTransitStatus.PreparingDestination;
            void Reject(LandingReport report) { if (Pending()) failed(SurfaceTouchdownService.DescribeDestinationFailure(task, report)); }
            LongEventHandler.QueueLongEvent(() =>
            {
                if (!Pending()) return;
                try
                {
                    if (task.TargetBodyId != targetHandle || task.TargetTile != targetTile || target != null && !bodies!.ValidateTarget(target).Success)
                    { Reject(LandingReport.Reject("PFL_LandingDestinationUnavailable")); return; }
                    var sourceCheck = CheckSourceIdentity(task);
                    if (!sourceCheck.Success) { Reject(sourceCheck); return; }
                    var largest = new IntVec2(0, 0);
                    foreach (var source in task.SourceMaps)
                    {
                        if (source.SourceCells.Count == 0) continue;
                        largest.x = Math.Max(largest.x, source.SourceCells.Max(c => c.x) - source.SourceCells.Min(c => c.x) + 1);
                        largest.z = Math.Max(largest.z, source.SourceCells.Max(c => c.z) - source.SourceCells.Min(c => c.z) + 1);
                    }
                    var size = RequiredMapSize(largest);
                    var check = Check(task.TargetTile, task.TargetBodyId, size);
                    if (!check.Success) { Reject(check); return; }
                    Map map = task.TargetTile.Layer is Celestial.CelestialSurfaceLayer ? LandingDestinationRuntime.Generate(task, size) :
                        ResolveExisting(() => FindExisting(task.TargetTile), () => LandingDestinationRuntime.Generate(task, size));
                    if (!Pending()) return;
                    if (target != null && !bodies!.ValidateTarget(target).Success)
                    { Reject(LandingReport.Reject("PFL_LandingDestinationUnavailable")); return; }
                    task.DestinationMapId = map.uniqueID; task.DestinationParentId = map.Parent.ID;
                    ready(map);
                }
                catch (Exception e) { Reject(LandingReport.Reject("PFL_LandingDestinationError", e.Message)); }
            }, "PFL_LandingPreparing", false, e => Reject(LandingReport.Reject("PFL_LandingDestinationError", e.Message)));
        }

        private static LandingReport CheckSourceIdentity(LandingTaskData task)
        {
            var sg = Current.Game.GetComponent<SpaceGameComp>();
            var group = sg.GetGroupById(task.GroupId);
            if (group == null || group.LandingTaskId != task.TaskId ||
                !group.GetEntityIds().OrderBy(id => id).SequenceEqual(task.EntityIds.OrderBy(id => id)))
                return LandingReport.Reject("PFL_LandingSourceUnavailable", "Source group identity changed.");
            if (!LandingSourceMapService.TryCollect(sg, group, out var maps, out var mapless, out var report)) return report;
            if (maps.Count != task.SourceMaps.Count || !mapless.OrderBy(id => id).SequenceEqual(task.MaplessEntityIds.OrderBy(id => id)))
                return LandingReport.Reject("PFL_LandingSourceUnavailable", "Source map identity changed.");
            foreach (var source in task.SourceMaps)
            {
                var live = maps.FirstOrDefault(m => m.MapId == source.MapId);
                if (live == null || !live.EntityIds.OrderBy(id => id).SequenceEqual(source.EntityIds.OrderBy(id => id)))
                    return LandingReport.Reject("PFL_LandingSourceUnavailable", "Source map owner changed.");
            }
            return LandingReport.Ok();
        }
    }
}
