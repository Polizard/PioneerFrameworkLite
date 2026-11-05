using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Landing
{
    public sealed class LandedShipMapParent : MapParent
    {
        public bool EverGenerated;
        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject) { alsoRemoveWorldObject = false; return false; }
        public override void PostMapGenerate() { base.PostMapGenerate(); EverGenerated = true; }
        public override void ExposeData() { base.ExposeData(); Scribe_Values.Look(ref EverGenerated, "everGenerated"); }
    }
}
