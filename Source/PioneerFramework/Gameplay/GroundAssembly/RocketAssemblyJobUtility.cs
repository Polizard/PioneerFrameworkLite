using RimWorld;
using Verse;
using Verse.AI;

namespace PioneerFramework.GroundAssembly;

[DefOf]
internal static class RocketAssemblyDefOf
{
    public static ThingDef PF_Building_RocketAssembler = null!;
    public static JobDef PF_AssembleRocket = null!;
    public static JobDef PF_DeliverRocketMaterials = null!;

    static RocketAssemblyDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RocketAssemblyDefOf));
}

internal static class RocketAssemblyJobUtility
{
    internal const PathEndMode PadReachMode = PathEndMode.Touch;

    internal static bool CanWorkAt(Pawn pawn, Thing pad, WorkTypeDef workType)
        => pawn.Spawned && pad.Spawned && pawn.Map == pad.Map && pad.Faction == Faction.OfPlayer
            && pawn.IsFreeColonist && !pawn.Downed && !pawn.Drafted && !pawn.Dead
            && pawn.workSettings?.WorkIsActive(workType) == true
            && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && !pad.IsForbidden(pawn);

    internal static bool CanAssign(Pawn pawn, Thing pad, CompRocketAssembler comp, WorkTypeDef workType, bool forced)
    {
        if (comp.State != AssemblerState.Building) return false;
        if (comp.IsBuildPaused || comp.IsRefundPending || comp.MaterialOrderError != null)
        {
            if (forced) JobFailReason.Is(comp.AssemblyStatusText);
            return false;
        }
        if (!CanWorkAt(pawn, pad, workType))
        {
            if (forced) JobFailReason.Is("PF_AssemblyWorkerUnavailable".Translate());
            return false;
        }
        if (!pawn.CanReserve(pad))
        {
            if (forced) JobFailReason.Is("PF_AssemblyReserved".Translate());
            return false;
        }
        if (!pawn.CanReach(pad, PadReachMode, forced ? Danger.Deadly : pawn.NormalMaxDanger()))
        {
            if (forced) JobFailReason.Is("NoPath".Translate());
            return false;
        }
        return true;
    }

    internal static bool HoldsPad(Pawn pawn, Job job, Thing pad)
        => pad.Map?.reservationManager.ReservedBy(pad, pawn, job) == true;

    internal static bool CanTakeMaterial(Pawn pawn, Thing item)
        => item.Spawned && item.Map == pawn.Map && item.stackCount > 0 && !item.IsForbidden(pawn)
            && HaulAIUtility.PawnCanAutomaticallyHaulFast_NewTemp(pawn, item, forced: false, checkReachability: false);
}
