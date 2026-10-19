using System.Collections.Generic;
using PioneerFramework.SpaceAI;
using Verse;

namespace PioneerFramework
{
    public class CompProperties_DecoyEmitter : CompProperties
    {
        public int charges = 6;
        public float cooldownSeconds = 20;
        public float lifetimeSeconds = 45;
        public float signalStrength = 10;
        public float apparentRadius = 20;
        public CompProperties_DecoyEmitter() { compClass = typeof(CompDecoyEmitter); }
    }

    /// <summary>Actual finite-charge DRFM equipment. Player and AI call the same emission method.</summary>
    public class CompDecoyEmitter : ThingComp
    {
        private readonly DecoyBudget budget = new();
        public CompProperties_DecoyEmitter Props => (CompProperties_DecoyEmitter)props;
        public int Charges => budget.Charges;
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!respawningAfterLoad) budget.Charges = Props.charges;
        }
        public bool TryEmit()
        {
            if (!AiShipAdapter.Operational(parent)) return false;
            var group = ShipBuildingUI.GetOwnerGroup(parent);
            var game = Current.Game?.GetComponent<SpaceGameComp>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (group == null || !group.IsActive || game == null || sensor == null) return false;
            double now = TimeScaleManager.GetPhysicsTickSafe();
            if (!budget.Ready(now)) return false;
            var combat = Current.Game?.GetComponent<Combat.CombatManager>();
            if (combat == null) return false;
            DVector3 right = group.GetThrustAxisSystem(ThrustAxis.Right);
            sensor.InjectDecoy(group.FactionId, combat.GetGroupPos(group) + right * 5000,
                combat.GetGroupVelocity(group) + right * 30, Props.signalStrength, Props.lifetimeSeconds, Props.apparentRadius);
            return budget.TrySpend(now, Props.cooldownSeconds);
        }
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref budget.Charges, "decoyCharges");
            Scribe_Values.Look(ref budget.NextEmission, "decoyNextEmission");
        }
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            var group = ShipBuildingUI.GetOwnerGroup(parent);
            if (group == null || !ShipBuildingUI.IsPlayerOwned(parent, group)) yield break;
            var command = new Command_Action
            {
                defaultLabel = "PF_AI_DecoyEmit".Translate(),
                defaultDesc = "PF_AI_DecoyDescription".Translate(),
                action = () => TryEmit()
            };
            if (Charges <= 0) command.Disable("PF_AI_DecoyCharges".Translate(0));
            yield return command;
        }
        public override string CompInspectStringExtra() => "PF_AI_DecoyCharges".Translate(Charges);
    }
}
