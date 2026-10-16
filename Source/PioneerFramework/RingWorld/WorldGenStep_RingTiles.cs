using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.RingWorld
{
    public sealed class WorldGenStep_RingTiles : WorldGenStep
    {
        public override int SeedPart => 194910151;
        public override void GenerateFresh(string seed, PlanetLayer layer)
        {
            if (layer is RingSurfaceLayer ring) ring.GenerateTiles(BiomeDefOf.TemperateForest);
        }
    }
}
