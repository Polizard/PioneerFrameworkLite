using Verse;

namespace PioneerFramework.RingWorld
{
    public sealed class RingWorldSaveData : IExposable
    {
        public int formatVersion;
        public string ringId = "";
        public int parentBodyId;
        public int seed;
        public int columns;
        public int rows;
        public double radiusMeters;
        public static RingWorldSaveData From(RingParameters p) => new RingWorldSaveData
        {
            formatVersion = p.FormatVersion, ringId = p.RingId, parentBodyId = p.ParentBodyId,
            seed = p.Seed, columns = p.Columns, rows = p.Rows, radiusMeters = p.RadiusMeters
        };
        public RingParameters ToParameters() => new RingParameters(ringId, parentBodyId, seed, columns, rows, radiusMeters, formatVersion);
        public void ExposeData()
        {
            Scribe_Values.Look(ref formatVersion, "formatVersion");
            Scribe_Values.Look(ref ringId, "ringId");
            Scribe_Values.Look(ref parentBodyId, "parentBodyId", -1);
            Scribe_Values.Look(ref seed, "seed");
            Scribe_Values.Look(ref columns, "columns");
            Scribe_Values.Look(ref rows, "rows");
            Scribe_Values.Look(ref radiusMeters, "radiusMeters");
        }
    }
}
