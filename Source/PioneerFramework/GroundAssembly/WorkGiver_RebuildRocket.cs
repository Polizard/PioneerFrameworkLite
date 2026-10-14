using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// WorkGiver：告诉殖民者可以执行火箭重建工作。
    /// </summary>
    public class WorkGiver_RebuildRocket : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial);

        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!(t is Building building))
                return false;

            if (!LaunchPadUtility.IsReturnedBooster(building.def))
                return false;

            Building? launchpad = FindLaunchpad(building);
            if (launchpad == null || !LaunchPadUtility.IsAssemblyPad(launchpad.def))
                return false;

            if (!LaunchPadUtility.CanRebuildOnPad(building.def, launchpad.def))
                return false;

            if (!LaunchPadUtility.IsWithinBuildableArea(launchpad, building.Position, building.def, building.Rotation))
                return false;

            if (building.Map.designationManager.DesignationOn(building, PFLaunchDefOf.PF_DesignateRebuildRocket) == null)
                return false;

            if (building.Map.reservationManager.IsReservedByAnyoneOf(building, pawn.Faction))
                return false;

            if (!pawn.CanReserve(building, 1, -1, null, forced))
                return false;

            return true;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!(t is Building returnedBuilding) || returnedBuilding.Map == null)
                return null!;

            if (returnedBuilding.Map.designationManager.DesignationOn(returnedBuilding, PFLaunchDefOf.PF_DesignateRebuildRocket) == null)
                return null!;

            Building? launchpad = FindLaunchpad(returnedBuilding);
            if (launchpad == null)
                return null!;

            JobDef jobDef = PFLaunchDefOf.PF_RebuildRocket;
            if (jobDef == null)
                return null!;

            return JobMaker.MakeJob(jobDef, returnedBuilding, launchpad);
        }

        private static Building? FindLaunchpad(Building returnedBooster)
        {
            if (returnedBooster?.Map == null)
                return null;

            return returnedBooster.Map.thingGrid.ThingsListAt(returnedBooster.Position)
                .OfType<Building>()
                .FirstOrDefault(b => LaunchPadUtility.IsAssemblyPad(b.def));
        }
    }
}
