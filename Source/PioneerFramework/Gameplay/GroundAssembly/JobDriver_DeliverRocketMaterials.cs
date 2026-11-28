using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PioneerFramework.GroundAssembly;

public sealed class JobDriver_DeliverRocketMaterials : JobDriver
{
    private const TargetIndex Pad = TargetIndex.A;
    private const TargetIndex Source = TargetIndex.B;
    private CompRocketAssembler? Assembler => job.targetA.Thing?.TryGetComp<CompRocketAssembler>();
    internal bool IsDelivering => CurToilIndex == 3;

    public override bool? IsSameJobAs(Job other) => job.count != other.count ? false : null;

    public override bool TryMakePreToilReservations(bool errorOnFailed)
    {
        Thing? source = job.targetB.Thing;
        var comp = Assembler;
        if (comp == null || source is not { Spawned: true } || comp.MissingMaterial(source.def) == 0
            || !comp.IsOpenBuildOrder(job.count) || !RocketAssemblyJobUtility.CanTakeMaterial(pawn, source)
            || !RocketAssemblyJobUtility.CanWorkAt(pawn, job.targetA.Thing, WorkTypeDefOf.Hauling)
            || !pawn.CanReserve(job.targetA) || !pawn.CanReserve(source)) return false;
        if (!pawn.Reserve(job.targetA, job, errorOnFailed: errorOnFailed)) return false;
        if (pawn.Reserve(source, job, errorOnFailed: errorOnFailed)) return true;
        pawn.Map.reservationManager.Release(job.targetA, pawn, job);
        return false;
    }

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(Pad);
        this.FailOn(() => Assembler?.IsOpenBuildOrder(job.count) != true
            || !RocketAssemblyJobUtility.CanWorkAt(pawn, job.targetA.Thing, WorkTypeDefOf.Hauling)
            || !RocketAssemblyJobUtility.HoldsPad(pawn, job, job.targetA.Thing));

        yield return Toils_Goto.GotoThing(Source, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(Source);
        yield return Toils_General.Do(PickUpMaterials);
        yield return Toils_Goto.GotoThing(Pad, RocketAssemblyJobUtility.PadReachMode);
        yield return Toils_General.Do(() =>
        {
            if ((Assembler?.TryReceiveAssemblyMaterials(pawn, job) ?? 0) == 0)
                EndJobWith(JobCondition.Incompletable);
        });
    }

    private void PickUpMaterials()
    {
        Thing? source = job.targetB.Thing;
        var comp = Assembler;
        if (source == null || comp == null || !RocketAssemblyJobUtility.CanTakeMaterial(pawn, source)
            || !pawn.CanReachImmediate(source, PathEndMode.ClosestTouch)
            || !pawn.Map.reservationManager.ReservedBy(source, pawn, job)
            || pawn.carryTracker.CarriedThing != null)
        {
            EndJobWith(JobCondition.Incompletable);
            return;
        }
        int wanted = Math.Min(comp.MissingMaterial(source.def), source.stackCount);
        int count = Math.Min(wanted, pawn.carryTracker.AvailableStackSpace(source.def));
        if (count <= 0 || pawn.carryTracker.TryStartCarry(source, count, reserve: false) <= 0)
        {
            EndJobWith(JobCondition.Incompletable);
            return;
        }
        job.SetTarget(Source, pawn.carryTracker.CarriedThing);
    }
}
