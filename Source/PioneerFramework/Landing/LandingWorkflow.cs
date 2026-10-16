using System.Linq;
using RimWorld;
using Verse;
namespace PioneerFramework.Landing
{
    [StaticConstructorOnStartup]
    public static class LandingWorkflow
    {
        static LandingWorkflow()
        {
            var harmony = new HarmonyLib.Harmony("PioneerFramework.LandingMapProtection");
            harmony.CreateClassProcessor(typeof(LandingMapProtection)).Patch();
            harmony.CreateClassProcessor(typeof(LandingWorldObjectPresentation)).Patch();
            LandingTransitService.Arrived = Arrive;
            LandingTransitService.ContinuePlacement = t => LandingPlacementTargeter.Begin(t.TaskId);
            StatDefOf.Mass.parts ??= new System.Collections.Generic.List<StatPart>();
            if (!StatDefOf.Mass.parts.Any(p => p is StatPart_RecoveredSpacecraftMass))
                StatDefOf.Mass.parts.Add(new StatPart_RecoveredSpacecraftMass { parentStat = StatDefOf.Mass });
        }
        public static void Arrive(LandingTaskData task)
        {
            if (task.Status == LandingTransitStatus.PreparingDestination)
            {
                LandingDestinationService.BeginResolve(task, map =>
                {
                    task.Status = LandingTransitStatus.AwaitingPlacement; LandingPlacementTargeter.Begin(task.TaskId);
                }, report => LandingTransitService.Fail(task, report, LandingTransitStatus.PreparingDestination));
                return;
            }
            if (task.Status != LandingTransitStatus.Completing) return;
            if (task.SourceMaps.Count == 0 && task.RecoverySiteId < 0)
            {
                var admission = LandingDestinationService.Check(task.TargetTile, task.TargetBodyId, new IntVec2(250, 250));
                if (!admission.Success) { LandingTransitService.Fail(task, admission, LandingTransitStatus.Completing); return; }
            }
            if (!LandingRecoveryService.TryCreateSite(task, out _, out var recovery))
            { LandingTransitService.Fail(task, recovery, LandingTransitStatus.Completing); return; }
            if (!LandingMapTransferService.TryComplete(task, out var result))
            { LandingTransitService.Fail(task, result, LandingTransitStatus.Completing); return; }
            task.Status = LandingTransitStatus.Completed;
            foreach (var obj in Find.WorldObjects.AllWorldObjects.OfType<LandingTransitWorldObject>().Where(w => w.Task == task).ToArray()) obj.Destroy();
            Messages.Message("PFL_LandingCompleted".Translate(task.GroupId), MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
