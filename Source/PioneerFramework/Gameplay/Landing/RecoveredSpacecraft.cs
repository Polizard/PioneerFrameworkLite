using RimWorld;
using Verse;

namespace PioneerFramework.Landing
{
    public sealed class RecoveredSpacecraft : ThingWithComps
    {
        public LandingEntityPayload? Payload;
        public override void ExposeData() { base.ExposeData(); Scribe_Deep.Look(ref Payload, "landedEntityPayload"); }
        public override string GetInspectString() => LandingWorldObjectPresentation.JoinInspectLines(base.GetInspectString(), "PFL_LandingRecoveredMass".Translate((Payload?.Mass ?? 0).ToString("F1")));
    }
    public sealed class StatPart_RecoveredSpacecraftMass : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        { if (req.Thing is RecoveredSpacecraft ship && ship.Payload != null) val = (float)ship.Payload.Mass; }
        public override string ExplanationPart(StatRequest req) => req.Thing is RecoveredSpacecraft ship ? "PFL_LandingRecoveredMass".Translate((ship.Payload?.Mass ?? 0).ToString("F1")) : "";
    }
}
