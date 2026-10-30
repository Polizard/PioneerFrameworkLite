using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework.Landing
{
    public sealed class LandingTransitWorldObject : WorldObject
    {
        public LandingTaskData Task = null!;
        public override string Label => Task?.Touchdown != null ? "PFL_TouchdownLabel".Translate(Task.GroupId).ToString() : base.Label;
        public double ProgressAt(int tick) => LandingTransitPath.Progress(Task.StartTick, Task.DurationTicks, tick);
        public DVector3 PositionAt(int tick)
        {
            double physics = TimeScaleManager.GetPhysicsTickSafe();
            var mgr = Current.Game.GetComponent<CelestialBodyManager>();
            var center = ViewFrame.BodySystemPosition(Task.TargetBodyId, physics);
            var target = center + Launch.GetSurfaceState.TileToEci(Task.TargetTile.tileId, mgr, Task.TargetBodyId, physics);
            double progress = Task.Status == LandingTransitStatus.InFlight ? ProgressAt(tick) : 1;
            return LandingTransitPath.Position(Task.DepartureCenter.v, center, Task.StartPosition.v, target,
                Launch.GetSurfaceState.BodyRadius(mgr, Task.TargetBodyId), progress);
        }
        public override Vector3 DrawPos => Task == null ? base.DrawPos : ViewFrame.ToViewUnity(PositionAt(Find.TickManager.TicksGame), TimeScaleManager.GetPhysicsTickSafe());
        public override string GetInspectString()
        {
            if (Task?.Touchdown != null)
            {
                string key = Task.Status switch
                {
                    LandingTransitStatus.AwaitingMapRequest => "PFL_TouchdownWaiting",
                    LandingTransitStatus.InFlight or LandingTransitStatus.PreparingDestination => "PFL_TouchdownPreparing",
                    LandingTransitStatus.AwaitingPlacement => "PFL_TouchdownPlacement",
                    LandingTransitStatus.Transferring or LandingTransitStatus.Completing => "PFL_TouchdownTransferring",
                    _ => "PFL_TouchdownBlocked"
                };
                return LandingWorldObjectPresentation.JoinInspectLines(
                    "PFL_TouchdownSpeed".Translate(Task.Touchdown.GroundSpeed.ToString("F1")), key.Translate(), Task.FailureReason);
            }
            return Task == null ? LandingWorldObjectPresentation.JoinInspectLines(base.GetInspectString()) :
                LandingWorldObjectPresentation.JoinInspectLines("PFL_LandingProgress".Translate(Task.Status.ToString(), (ProgressAt(Find.TickManager.TicksGame) * 100).ToString("F0")), Task.FailureReason);
        }
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            if (Task?.Touchdown != null && Task.Status == LandingTransitStatus.AwaitingMapRequest)
            {
                Game session = Current.Game;
                string taskId = Task.TaskId;
                yield return new Command_Action { defaultLabel = "PFL_TouchdownRequestMap".Translate(), action = () =>
                {
                    if (!SurfaceTouchdownService.TryRequestDestinationMap(session, taskId, out var report))
                        Messages.Message(report.Text, MessageTypeDefOf.RejectInput, false);
                } };
            }
            if (Task != null && (Task.Status == LandingTransitStatus.AwaitingPlacement || Task.Status == LandingTransitStatus.FailedRecoverable || Task.Status == LandingTransitStatus.Completing))
                yield return new Command_Action { defaultLabel = "PFL_LandingContinue".Translate(), action = () => LandingTransitService.TryResume(Task.TaskId, out _) };
        }
        public override void ExposeData() { base.ExposeData(); Scribe_References.Look(ref Task, "landingTask"); }
    }
}
