using System;
using System.Linq;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingMapTransferService
    {
        public static bool ReconcileEntry(LandingSourceMapData source, bool ledgerCompleted, bool targetEvidence)
        {
            if (ledgerCompleted != targetEvidence || (source.Status != LandingMapStatus.Pending && !ledgerCompleted)) return false;
            if (ledgerCompleted) source.Status = LandingMapStatus.Transferred;
            return true;
        }
        public static LandingReport Reconcile(LandingTaskData task)
        {
            var ledger = Current.Game.GetComponent<ShipSurfaceTransferLedger>();
            var target = LandingSourceMapService.FindMap(task.DestinationMapId);
            var records = target?.GetComponent<LandingMapRecords>();
            return Reconcile(task, source =>
            {
                string op = source.OperationId(task.TaskId);
                return (ledger.IsCompleted(op), ledger.IsBlocked(op), records != null && records.TryGet(op, out _));
            });
        }
        public static LandingReport Reconcile(LandingTaskData task, Func<LandingSourceMapData, (bool completed, bool blocked, bool evidence)> lookup)
        {
            foreach (var source in task.SourceMaps.Where(s => s.Status != LandingMapStatus.Cleaned))
            {
                var state = lookup(source);
                if (state.blocked || !ReconcileEntry(source, state.completed, state.evidence))
                    return LandingReport.Reject("PFL_LandingTransferBlocked");
            }
            if (task.Status == LandingTransitStatus.Transferring)
                task.Status = task.SourceMaps.Any(s => s.Status == LandingMapStatus.Pending)
                    ? LandingTransitStatus.AwaitingPlacement : LandingTransitStatus.Completing;
            return LandingReport.Ok();
        }
        public static bool TryPlace(LandingTaskData task, string sourceKey, IntVec3 center, out LandingReport report)
        {
            report = Reconcile(task);
            if (!report.Success) { LandingTransitService.Fail(task, report, LandingTransitStatus.AwaitingPlacement, true); return false; }
            var entry = task.SourceMaps.FirstOrDefault(s => s.SourceKey == sourceKey);
            if (task.Status != LandingTransitStatus.AwaitingPlacement || entry == null || entry.Status != LandingMapStatus.Pending)
            { report = LandingReport.Reject("PFL_LandingInvalidState"); return false; }
            var source = LandingSourceMapService.FindMap(entry.MapId); var target = LandingSourceMapService.FindMap(task.DestinationMapId);
            if (source == null || target == null) { report = LandingReport.Reject("PFL_LandingSourceUnavailable", "Map missing"); return false; }
            // Source contents may change during flight: recompute and show a fresh layout before moving anything.
            if (!LandingFootprintService.TryCollect(source, out var footprint, out report)) return false;
            if (!footprint!.SourceCells.SequenceEqual(entry.SourceCells)) { entry.SourceCells = footprint.SourceCells.ToList(); report = LandingReport.Reject("PFL_LandingFootprintChanged"); return false; }
            report = LandingFootprintService.Validate(source, target, footprint, center, Array.Empty<IntVec3>());
            if (!report.Success) return false;
            if (target.GetComponent<SpaceMapComp>() == null) target.components.Add(new SpaceMapComp(target));
            if (target.GetComponent<LandingMapRecords>() == null) target.components.Add(new LandingMapRecords(target));
            CompPropellantTank.TickDifferenceSync(source);
            var origin = footprint.TargetOrigin(center);
            ShipSurfaceTransferLayout.TryCreate(entry.SourceCells, new IntVec2(source.Size.x, source.Size.z), new IntVec2(target.Size.x, target.Size.z), origin, out var layout, out _);
            var record = new LandingVesselRecord { TaskId = task.TaskId, SourceKey = entry.SourceKey, OperationId = entry.OperationId(task.TaskId), Origin = origin, EntityIds = entry.EntityIds.ToList(), VirtualPropulsion = source.GetComponent<SpaceMapComp>()?.ExportLandingVirtualPropulsion() ?? new LandingVirtualPropulsionData() };
            task.Status = LandingTransitStatus.Transferring;
            var request = new ShipSurfaceTransferRequest(source, target, entry.SourceCells, origin, record.OperationId);
            if (!ShipSurfaceTransfer.TryTransfer(request, new LandingMapStateTransfer(source, target, layout, record), out var result))
            {
                report = LandingReport.Reject("PFL_LandingPlacementBlocked", result.Message);
                task.Status = LandingTransitStatus.AwaitingPlacement;
                if (result.Status == ShipSurfaceTransferStatus.FailedUnrecovered) LandingTransitService.Fail(task, report, LandingTransitStatus.AwaitingPlacement, true);
                return false;
            }
            entry.TargetOrigin = origin; entry.Status = LandingMapStatus.Transferred;
            task.Status = task.SourceMaps.Any(s => s.Status == LandingMapStatus.Pending) ? LandingTransitStatus.AwaitingPlacement : LandingTransitStatus.Completing;
            report = LandingReport.Ok(); return true;
        }
        public static bool TryComplete(LandingTaskData task, out LandingReport report)
        {
            report = Reconcile(task); if (!report.Success) return false;
            if (task.SourceMaps.Any(s => s.Status == LandingMapStatus.Pending)) { report = LandingReport.Reject("PFL_LandingInvalidState"); return false; }
            var sg = Current.Game.GetComponent<SpaceGameComp>();
            try
            {
                foreach (var entry in task.SourceMaps.Where(s => s.Status == LandingMapStatus.Transferred))
                {
                    var map = LandingSourceMapService.FindMap(entry.MapId);
                    if (map != null && map.listerThings.AllThings.Any(t => t.def.category != ThingCategory.Mote && t.def.category != ThingCategory.Projectile))
                        throw new InvalidOperationException("Source map still has persistent content.");
                    foreach (int id in entry.EntityIds)
                    {
                        var e = sg.GetEntityById(id); if (e == null) continue;
                        if (e.MapId == entry.MapId) { e.MapId = -1; e.HasMap = false; e.IsMapArchived = false; }
                        foreach (var part in e.Parts.Where(p => p.linkedMapId == entry.MapId)) part.linkedMapId = -1;
                        e.NotifyMapLinksChanged();   // P0-1：图链缓存失效
                    }
                    if (map != null)
                    {
                        if (map.GetComponent<SpaceMapComp>() is SpaceMapComp mc) mc.ownerEntityId = -1;
                        if (map.Parent is SpacecraftMapParent ship) ship.ClearGroundBridge();
                        sg.UnregisterMapComponent(entry.MapId);
                        var parent = map.Parent; Current.Game.DeinitAndRemoveMap(map, false);
                        if (parent is SpacecraftMapParent && !parent.Destroyed) parent.Destroy();
                    }
                    entry.Status = LandingMapStatus.Cleaned;
                }
                report = LandingGroupLifecycle.Finish(sg, task); return report.Success;
            }
            catch (Exception e) { report = LandingReport.Reject("PFL_LandingExecutionError", e.Message); return false; }
        }
    }
}
