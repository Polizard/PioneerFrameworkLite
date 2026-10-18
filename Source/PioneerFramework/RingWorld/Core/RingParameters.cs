using System;

namespace PioneerFramework.RingWorld
{
    public sealed record RingParameters
    {
        public const int CurrentFormat = 1;
        public string RingId { get; }
        public int ParentBodyId { get; }
        public int Seed { get; }
        public int Columns { get; }
        public int Rows { get; }
        public double RadiusMeters { get; }
        public int FormatVersion { get; }

        public RingParameters(string ringId, int parentBodyId, int seed, int columns, int rows,
            double radiusMeters, int formatVersion = CurrentFormat)
        {
            if (string.IsNullOrWhiteSpace(ringId)) throw new ArgumentException("RingId is required.", nameof(ringId));
            if (parentBodyId < 0) throw new ArgumentOutOfRangeException(nameof(parentBodyId));
            if (!double.IsFinite(radiusMeters) || radiusMeters <= 0 || radiusMeters > 1e12)
                throw new ArgumentOutOfRangeException(nameof(radiusMeters));
            if (formatVersion != CurrentFormat) throw new NotSupportedException($"Unsupported ring format {formatVersion}.");
            _ = new RingTopology(columns, rows);
            RingId = ringId; ParentBodyId = parentBodyId; Seed = seed;
            Columns = columns; Rows = rows; RadiusMeters = radiusMeters; FormatVersion = formatVersion;
        }
    }
}
