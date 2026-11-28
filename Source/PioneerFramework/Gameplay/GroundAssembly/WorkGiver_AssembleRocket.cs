using RimWorld;
using Verse;
using Verse.AI;

namespace PioneerFramework.GroundAssembly;

public sealed class WorkGiver_AssembleRocket : WorkGiver_Scanner
{
    public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(RocketAssemblyDefOf.PF_Building_RocketAssembler);
    public override PathEndMode PathEndMode => RocketAssemblyJobUtility.PadReachMode;

    public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        => t.TryGetComp<CompRocketAssembler>() is { HasAssemblyMaterials: true } comp
            && RocketAssemblyJobUtility.CanAssign(pawn, t, comp, WorkTypeDefOf.Construction, forced);

    public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        if (!HasJobOnThing(pawn, t, forced)) return null!;
        Job job = JobMaker.MakeJob(RocketAssemblyDefOf.PF_AssembleRocket, t);
        job.count = t.TryGetComp<CompRocketAssembler>().BuildOrderVersion;
        return job;
    }
}
