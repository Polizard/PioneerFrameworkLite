using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Landing
{
    public sealed class CaravanArrivalAction_RecoverLandedGroup : CaravanArrivalAction
    {
        private int siteId;
        public CaravanArrivalAction_RecoverLandedGroup() { }
        public CaravanArrivalAction_RecoverLandedGroup(int id) { siteId = id; }
        public override string Label => "PFL_LandingRecoverAction".Translate();
        public override string ReportString => "PFL_LandingRecoverAction".Translate();
        public override void Arrived(Caravan caravan)
        {
            var site = Find.WorldObjects.AllWorldObjects.OfType<LandedGroupWorldObject>().FirstOrDefault(s => s.ID == siteId);
            if (site != null) LandingRecoveryService.TryRecover(site, caravan, out _);
        }
        public override void ExposeData() { base.ExposeData(); Scribe_Values.Look(ref siteId, "siteId"); }
    }
}
