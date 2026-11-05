using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Landing
{
    public sealed class LandingVesselRecord : IExposable
    {
        public string TaskId = "", OperationId = "", SourceKey = "";
        public IntVec3 Origin;
        public List<int> EntityIds = new List<int>();
        public List<string> ThingIds = new List<string>();
        public LandingVirtualPropulsionData VirtualPropulsion = new LandingVirtualPropulsionData();
        public void ExposeData()
        {
            Scribe_Values.Look(ref TaskId, "task"); Scribe_Values.Look(ref OperationId, "operation"); Scribe_Values.Look(ref SourceKey, "source");
            Scribe_Values.Look(ref Origin, "origin"); Scribe_Collections.Look(ref EntityIds, "entities", LookMode.Value);
            Scribe_Collections.Look(ref ThingIds, "things", LookMode.Value); Scribe_Deep.Look(ref VirtualPropulsion, "virtualPropulsion");
        }
    }
    public sealed class LandingMapRecords : MapComponent
    {
        public List<LandingVesselRecord> Records = new List<LandingVesselRecord>();
        public LandingMapRecords(Map map) : base(map) { }
        public bool TryGet(string id, out LandingVesselRecord? record) { record = Records.FirstOrDefault(r => r.OperationId == id); return record != null; }
        public void Record(LandingVesselRecord record) { if (!TryGet(record.OperationId, out _)) Records.Add(record); }
        public override void ExposeData() { base.ExposeData(); Scribe_Collections.Look(ref Records, "pflLandedVessels", LookMode.Deep); }
    }
}
