using Verse;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Saveable intent and control ownership. Derived equipment and target keys are rebuilt.</summary>
    public sealed class AiShipRecord : IExposable
    {
        public int groupId, factionId, fleetId;
        public int knownTargetGroupId = -1;
        public AiRole role;
        public AiAction action;
        public double nextDecision, nextPlan, retreatStarted = -1;
        public double reserveDeltaV = 100;
        public bool ownsBurn, withdrawing;
        public int targetGroupId = -1, targetDecoyId = -1;
        public double burnExpires;
        public double activateAt;
        public long ownedBurnVersion;
        public DVector3 retreatDirection;
        internal double nextFleetUpdate, actionUntil;
        internal int preferredTrack;
        internal readonly AiShipAdapter adapter = new();
        internal readonly System.Collections.Generic.List<AiContact> contacts = new();
        internal readonly System.Collections.Generic.List<AiContact> candidates = new(4);
        internal AiDecision decision;
        internal AiOrbitPlanningJob? planning;
        internal double planningEpoch;
        internal int planningTrack;
        internal int planningBody;
        internal Celestial.CelestialTargetToken? planningCelestialTarget;
        internal AiShipState planningState;
        public void ExposeData()
        {
            Scribe_Values.Look(ref groupId, "groupId"); Scribe_Values.Look(ref factionId, "factionId");
            Scribe_Values.Look(ref fleetId, "fleetId"); Scribe_Values.Look(ref knownTargetGroupId, "knownTarget", -1);
            Scribe_Values.Look(ref role, "role"); Scribe_Values.Look(ref action, "action");
            Scribe_Values.Look(ref reserveDeltaV, "reserveDeltaV", 100);
            Scribe_Values.Look(ref ownsBurn, "ownsBurn"); Scribe_Values.Look(ref withdrawing, "withdrawing");
            Scribe_Values.Look(ref targetGroupId, "targetGroupId", -1); Scribe_Values.Look(ref targetDecoyId, "targetDecoyId", -1);
            Scribe_Values.Look(ref burnExpires, "burnExpires"); Scribe_Values.Look(ref retreatStarted, "retreatStarted", -1);
            Scribe_Values.Look(ref activateAt, "activateAt");
            Scribe_Values.Look(ref ownedBurnVersion, "ownedBurnVersion");
            DVector3.Scribe("retreatDirection",ref retreatDirection);
        }
    }
}
