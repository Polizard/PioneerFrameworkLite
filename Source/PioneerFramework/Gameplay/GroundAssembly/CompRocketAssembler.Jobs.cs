using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace PioneerFramework.GroundAssembly;

internal enum AssemblyWorkResult { Rejected, InProgress, Completed }

public partial class CompRocketAssembler
{
    internal string AssemblyStatusText
    {
        get
        {
            if (refundPending) return "PF_AssemblyRefunding".Translate();
            if (buildPaused) return "PF_AssemblyPaused".Translate();
            string? error = MaterialOrderError;
            if (error != null) return error;

            Pawn? worker = null;
            if (parent?.Map != null && parent.Faction != null)
                parent.Map.reservationManager.TryGetReserver(parent, parent.Faction, out worker);
            Job? job = worker?.CurJob;
            bool currentJob = job != null && job.count == buildOrderVersion && job.targetA.Thing == parent
                && RocketAssemblyJobUtility.HoldsPad(worker!, job, parent);

            if (!HasAssemblyMaterials)
            {
                if (currentJob && worker!.jobs.curDriver is JobDriver_DeliverRocketMaterials)
                    return "PF_AssemblyBringing".Translate(worker.LabelShortCap);
                return "PF_AssemblyWaitMaterials".Translate();
            }
            if (currentJob && worker!.jobs.curDriver is JobDriver_AssembleRocket driver)
                return driver.IsAssembling ? "PF_AssemblyWorking".Translate(worker.LabelShortCap)
                    : "PF_AssemblyWalking".Translate(worker.LabelShortCap);
            return "PF_AssemblyWaitWorker".Translate();
        }
    }

    private bool IsAtAssignedPad(Pawn pawn, Job job, WorkTypeDef workType)
        => parent != null && pawn.CurJob == job && job.targetA.Thing == parent && IsOpenBuildOrder(job.count)
            && RocketAssemblyJobUtility.CanWorkAt(pawn, parent, workType)
            && RocketAssemblyJobUtility.HoldsPad(pawn, job, parent)
            && pawn.CanReachImmediate(parent, RocketAssemblyJobUtility.PadReachMode);

    internal int TryReceiveAssemblyMaterials(Pawn pawn, Job job)
    {
        if (pawn.jobs.curDriver is not JobDriver_DeliverRocketMaterials { IsDelivering: true }
            || !IsAtAssignedPad(pawn, job, WorkTypeDefOf.Hauling)) return 0;
        Thing? item = pawn.carryTracker.CarriedThing;
        if (item == null || item != job.targetB.Thing) return 0;
        int count = Math.Min(item.stackCount, MissingMaterial(item.def));
        if (count <= 0) return 0;
        // In 1.7 a fully merged incoming stack can report zero transferred after it is absorbed.
        return pawn.carryTracker.GetDirectlyHeldThings().TryTransferToContainer(item, GetDirectlyHeldThings(), count,
            canMergeWithExistingStacks: false);
    }

    internal AssemblyWorkResult TryApplyAssemblyWork(Pawn pawn, Job job, int elapsedTicks)
    {
        if (pawn.jobs.curDriver is not JobDriver_AssembleRocket { IsAssembling: true }
            || !IsAtAssignedPad(pawn, job, WorkTypeDefOf.Construction) || !HasAssemblyMaterials)
            return AssemblyWorkResult.Rejected;

        double delta = AssemblyWorkForTicks(elapsedTicks, Props.workPerSecond, pawn.GetStatValue(StatDefOf.ConstructionSpeed));
        if (delta <= 0) return AssemblyWorkResult.Rejected;
        workDone = Math.Min(workRequired, workDone + delta);
        if (workRequired - workDone > 1e-9) return AssemblyWorkResult.InProgress;
        FinishBuild();
        return AssemblyWorkResult.Completed;
    }

    internal static double AssemblyWorkForTicks(int ticks, float baseRate, float constructionSpeed)
    {
        double work = ticks * Math.Max(0.1, (double)baseRate) * constructionSpeed / 60d;
        return ticks > 0 && double.IsFinite(work) && work > 0 ? work : 0;
    }
}
