using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PioneerFramework.GroundAssembly;

public sealed class JobDriver_AssembleRocket : JobDriver
{
    private const TargetIndex Pad = TargetIndex.A;
    private const int WorkTicksBeforeReschedule = 5000;
    private CompRocketAssembler? Assembler => job.targetA.Thing?.TryGetComp<CompRocketAssembler>();
    internal bool IsAssembling => CurToilIndex == 1;

    public override bool? IsSameJobAs(Job other) => job.count != other.count ? false : null;

    public override bool TryMakePreToilReservations(bool errorOnFailed)
        => Assembler is { HasAssemblyMaterials: true } comp && comp.IsOpenBuildOrder(job.count)
            && RocketAssemblyJobUtility.CanWorkAt(pawn, job.targetA.Thing, WorkTypeDefOf.Construction)
            && pawn.CanReserve(job.targetA) && pawn.Reserve(job.targetA, job, errorOnFailed: errorOnFailed);

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(Pad);
        this.FailOn(() => Assembler is not { HasAssemblyMaterials: true } comp || !comp.IsOpenBuildOrder(job.count)
            || !RocketAssemblyJobUtility.CanWorkAt(pawn, job.targetA.Thing, WorkTypeDefOf.Construction)
            || !RocketAssemblyJobUtility.HoldsPad(pawn, job, job.targetA.Thing));

        yield return Toils_Goto.GotoThing(Pad, RocketAssemblyJobUtility.PadReachMode);

        Toil work = ToilMaker.MakeToil("AssembleRocket");
        work.defaultCompleteMode = ToilCompleteMode.Delay;
        work.defaultDuration = WorkTicksBeforeReschedule;
        work.activeSkill = () => SkillDefOf.Construction;
        work.handlingFacing = true;
        work.tickAction = () =>
        {
            pawn.rotationTracker.FaceTarget(job.targetA);
            switch (Assembler?.TryApplyAssemblyWork(pawn, job, 1))
            {
                case AssemblyWorkResult.InProgress: break;
                case AssemblyWorkResult.Completed: EndJobWith(JobCondition.Succeeded); break;
                default: EndJobWith(JobCondition.Incompletable); break;
            }
        };
        work.WithEffect(EffecterDefOf.ConstructMetal, Pad);
        work.WithProgressBar(Pad, () => Assembler?.Progress01 ?? 0f);
        yield return work;
    }
}
