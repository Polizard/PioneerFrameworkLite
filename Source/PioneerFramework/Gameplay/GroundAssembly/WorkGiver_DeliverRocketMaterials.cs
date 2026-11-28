using RimWorld;
using Verse;
using Verse.AI;

namespace PioneerFramework.GroundAssembly;

public sealed class WorkGiver_DeliverRocketMaterials : WorkGiver_Scanner
{
    public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(RocketAssemblyDefOf.PF_Building_RocketAssembler);
    public override PathEndMode PathEndMode => RocketAssemblyJobUtility.PadReachMode;

    public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        => TryFindSource(pawn, t, forced, out _, out _);

    public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        if (!TryFindSource(pawn, t, forced, out CompRocketAssembler? comp, out Thing? source)) return null!;
        var job = JobMaker.MakeJob(RocketAssemblyDefOf.PF_DeliverRocketMaterials, t, source);
        job.count = comp!.BuildOrderVersion;
        return job;
    }

    private static bool TryFindSource(Pawn pawn, Thing pad, bool forced, out CompRocketAssembler? comp, out Thing? source)
    {
        comp = pad.TryGetComp<CompRocketAssembler>();
        source = null;
        if (comp == null || comp.HasAssemblyMaterials
            || !RocketAssemblyJobUtility.CanAssign(pawn, pad, comp, WorkTypeDefOf.Hauling, forced)) return false;

        Danger danger = forced ? Danger.Deadly : pawn.NormalMaxDanger();
        foreach (AssemblyMaterialRow row in comp.MaterialRows())
        {
            if (row.Def == null || row.Missing == 0 || pawn.carryTracker.MaxStackSpaceEver(row.Def) <= 0) continue;
            source = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(row.Def),
                PathEndMode.ClosestTouch, TraverseParms.For(pawn, danger), 9999f,
                thing => RocketAssemblyJobUtility.CanTakeMaterial(pawn, thing));
            if (source != null) return true;
        }
        if (forced) JobFailReason.Is("PF_AssemblyNoMaterialsToHaul".Translate());
        return false;
    }
}
