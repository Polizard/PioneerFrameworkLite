using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Landing
{
    public static class LandingPlacementTargeter
    {
        public static void Begin(string taskId)
        {
            var task = Current.Game.GetComponent<LandingTransitManager>().FindTask(taskId);
            if (task == null || task.Status != LandingTransitStatus.AwaitingPlacement) return;
            var reconciliation = LandingMapTransferService.Reconcile(task);
            if (!reconciliation.Success) { LandingTransitService.Fail(task, reconciliation, task.Status, true); return; }
            var target = LandingSourceMapService.FindMap(task.DestinationMapId);
            var entry = task.SourceMaps.FirstOrDefault(s => s.Status == LandingMapStatus.Pending);
            if (entry == null) { task.Status = LandingTransitStatus.Completing; LandingWorkflow.Arrive(task); return; }
            if (target == null) return;
            var source = LandingSourceMapService.FindMap(entry.MapId); if (source == null) return;
            if (!LandingFootprintService.TryCollect(source, out var footprint, out var initial))
            { LandingTransitService.Fail(task, initial, LandingTransitStatus.AwaitingPlacement); return; }
            entry.SourceCells = footprint!.SourceCells.ToList();
            CameraJumper.TryJump(target.Center, target); Find.TickManager.Pause();
            foreach (var old in Find.WindowStack.Windows.OfType<Window_LandingPlacement>().ToList()) old.Close();
            var window = new Window_LandingPlacement(task); Find.WindowStack.Add(window);
            var parameters = new TargetingParameters { canTargetLocations = true, canTargetBuildings = false, canTargetPawns = false,
                validator = t => t.Cell.InBounds(target) && Find.CurrentMap == target && !Mouse.IsOver(window.windowRect) };
            Find.Targeter.BeginTargeting(parameters, location =>
            {
                if (Find.CurrentMap != target) return;
                if (LandingMapTransferService.TryPlace(task, entry.SourceKey, location.Cell, out var report))
                {
                    window.Close();
                    if (task.Status == LandingTransitStatus.AwaitingPlacement) QueueNext(taskId);
                    else LandingWorkflow.Arrive(task);
                }
                else
                {
                    window.Reason = report.Text;
                    if (task.Status == LandingTransitStatus.AwaitingPlacement) QueueNext(taskId);
                }
            }, highlightAction: _ =>
            {
                if (Find.CurrentMap != target) return;
                var cell = UI.MouseCell();
                var report = LandingFootprintService.Validate(source, target, footprint, cell, System.Array.Empty<IntVec3>());
                window.Reason = report.Success ? "PFL_LandingClickToPlace".Translate() : report.Text;
                GenDraw.DrawFieldEdges(footprint.TargetCells(cell).Where(c => c.InBounds(target)).ToList(), report.Success ? Color.green : Color.red);
            }, targetValidator: _ => Find.CurrentMap == target && !Mouse.IsOver(window.windowRect));
        }
        private static void QueueNext(string taskId) => LongEventHandler.QueueLongEvent(() => Begin(taskId), "PFL_LandingPreparing", false, null);
    }
}
