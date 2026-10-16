using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingTransitService
    {
        public static Action<LandingTaskData>? Arrived;
        public static Action<LandingTaskData>? ContinuePlacement;
        private static LandingTransitManager Manager => Current.Game.GetComponent<LandingTransitManager>();
        public static bool TryFindByGroupId(int groupId, out LandingTransitWorldObject? transit)
        {
            var task = Manager.FindForGroup(groupId);
            transit = task == null ? null : EnsureMarker(task); return transit != null;
        }
        public static bool TryStart(SpaceGameComp sg, SpaceEntityGroup group, int bodyId, PlanetTile target, int durationTicks,
            out LandingTransitWorldObject? transit, out LandingReport report)
        {
            transit = null;
            if (group.FactionId != Faction.OfPlayer.loadID || Manager.FindForGroup(group.GroupId) != null || !string.IsNullOrEmpty(group.LandingTaskId))
            { report = LandingReport.Reject("PFL_LandingGroupUnavailable"); return false; }
            if (!sg.TryQuoteLanding(group, bodyId, target, out var quote, out _, out _, out _, out string? reason))
            { report = LandingReport.Reject("PFL_LandingQuoteRejected", reason ?? ""); return false; }
            if (!LandingSourceMapService.TryCollect(sg, group, out var sources, out var mapless, out report)) return false;
            var largest = new IntVec2(0, 0);
            foreach (var source in sources)
            {
                if (!LandingFootprintService.TryCollect(LandingSourceMapService.FindMap(source.MapId)!, out var footprint, out report)) return false;
                source.SourceCells = footprint!.SourceCells.ToList();
                largest.x = Math.Max(largest.x, footprint.SourceBounds.Width); largest.z = Math.Max(largest.z, footprint.SourceBounds.Height);
            }
            report = LandingDestinationService.Check(target, bodyId, LandingDestinationService.RequiredMapSize(largest));
            if (!report.Success) return false;
            var task = new LandingTaskData
            {
                GroupId = group.GroupId, EntityIds = group.GetEntityIds().OrderBy(x => x).ToList(), MaplessEntityIds = mapless,
                TargetBodyId = bodyId, TargetTile = target, StartPosition = group.CurrentOrbit.positionSystem,
                DepartureCenter = (SystemVec3)ViewFrame.BodySystemPosition(bodyId, TimeScaleManager.GetPhysicsTickSafe()),
                StartTick = Find.TickManager.TicksGame, DurationTicks = durationTicks > 0 ? durationTicks : 180, SourceMaps = sources
            };
            LandingTransitWorldObject? marker = null;
            bool success = LandingFuelTransaction.TryRun(() =>
            {
                Manager.Register(task); marker = EnsureMarker(task);
                bool debited = LandingFuelTransaction.TryDebit(sg, group, quote.deltaV, out var receipt, out var debitReport);
                task.FuelReceipt = receipt;
                if (!debited) throw new InvalidOperationException(debitReport.Text);
                if (!LandingGroupLifecycle.TrySuspend(sg, group, task.TaskId, out var suspension, out var suspendReport))
                { task.Suspension = suspension; throw new InvalidOperationException(suspendReport.Text); }
                task.Suspension = suspension; task.FuelReceipt.Committed = true;
                sg.lastLandingConsumedKg = receipt.ConsumedKg; sg.lastLandingQuotedDeltaV = quote.deltaV;
            }, () =>
            {
                if (group.LandingTaskId == task.TaskId)
                {
                    var restoredGroup = LandingGroupLifecycle.Restore(sg, task);
                    if (!restoredGroup.Success) { task.Status = LandingTransitStatus.Blocked; task.FailureReason = restoredGroup.Text; throw new InvalidOperationException(restoredGroup.Text); }
                }
                if (task.FuelReceipt.Debits.Count != 0)
                {
                    var restored = LandingFuelTransaction.Rollback(task.FuelReceipt);
                    if (!restored.Success)
                    {
                        if (string.IsNullOrEmpty(group.LandingTaskId)) LandingGroupLifecycle.TrySuspend(sg, group, task.TaskId, out task.Suspension, out _);
                        task.Status = LandingTransitStatus.Blocked; task.FailureReason = restored.Text;
                        throw new InvalidOperationException(restored.Text);
                    }
                }
                Manager.Tasks.Remove(task);
                foreach (var obj in Find.WorldObjects.AllWorldObjects.OfType<LandingTransitWorldObject>().Where(w => w.Task == task).ToArray()) obj.Destroy();
            }, out string error);
            transit = success ? marker : null;
            report = success ? LandingReport.Ok() : LandingReport.Reject("PFL_LandingExecutionError", error); return success;
        }
        public static LandingTransitWorldObject EnsureMarker(LandingTaskData task)
        {
            var marker = Find.WorldObjects.AllWorldObjects.OfType<LandingTransitWorldObject>().FirstOrDefault(w => w.Task == task);
            if (marker != null) return marker;
            marker = (LandingTransitWorldObject)WorldObjectMaker.MakeWorldObject(DefDatabase<WorldObjectDef>.GetNamed("PF_LandingTransit"));
            marker.Task = task; marker.Tile = task.TargetTile; marker.SetFaction(Faction.OfPlayer); Find.WorldObjects.Add(marker); return marker;
        }
        public static void Advance(LandingTaskData task)
        {
            if (task.Status != LandingTransitStatus.InFlight || LandingTransitPath.Progress(task.StartTick, task.DurationTicks, Find.TickManager.TicksGame) < 1) return;
            task.Status = task.SourceMaps.Count == 0 ? LandingTransitStatus.Completing : LandingTransitStatus.PreparingDestination;
            if (Arrived == null) { Fail(task, LandingReport.Reject("PFL_LandingExecutionError", "Arrival handler unavailable"), task.Status); return; }
            Arrived(task);
        }
        public static void Fail(LandingTaskData task, LandingReport report, LandingTransitStatus resume, bool blocked = false)
        {
            task.ResumeStage = resume; task.FailureReason = report.Text;
            task.Status = blocked ? LandingTransitStatus.Blocked : LandingTransitStatus.FailedRecoverable;
            Log.Error("[PFL Landing " + task.TaskId + "] " + task.FailureReason);
        }
        public static bool TryResume(string id, out LandingReport report)
        {
            var task = Manager.FindTask(id);
            if (task == null || task.Status == LandingTransitStatus.Blocked || task.Status == LandingTransitStatus.Completed)
            { report = LandingReport.Reject("PFL_LandingInvalidState"); return false; }
            if (task.Status == LandingTransitStatus.FailedRecoverable) task.Status = task.ResumeStage;
            if (task.Status == LandingTransitStatus.AwaitingPlacement) ContinuePlacement?.Invoke(task); else Arrived?.Invoke(task);
            report = LandingReport.Ok(); return true;
        }
        public static void Reconcile(LandingTransitManager manager)
        {
            var sg = Current.Game.GetComponent<SpaceGameComp>();
            foreach (var site in Find.WorldObjects.AllWorldObjects.OfType<LandedGroupWorldObject>())
            {
                var recovery = LandingRecoveryService.Reconcile(site);
                if (!recovery.Success) Log.Warning("[PFL Landing " + site.TaskId + "] " + recovery.Text);
            }
            foreach (var task in manager.Tasks.Where(t => t.Status != LandingTransitStatus.Completed))
            {
                var group = sg.GetGroupById(task.GroupId);
                if (group == null && !task.Cleanup.SpaceRemoved)
                { Fail(task, LandingReport.Reject("PFL_LandingMissingGroup"), task.Status, true); continue; }
                if (group != null)
                {
                    group.LandingTaskId = task.TaskId; group.IsActive = group.IsMovable = group.IsCollidable = false;
                    sg.RemoveLandingGroupFromIndexes(group);
                }
                if (task.Status == LandingTransitStatus.PreparingDestination) task.Status = LandingTransitStatus.InFlight;
                var transfer = LandingMapTransferService.Reconcile(task);
                if (!transfer.Success) { Fail(task, transfer, task.Status, true); continue; }
                EnsureMarker(task);
            }
        }
    }
}
