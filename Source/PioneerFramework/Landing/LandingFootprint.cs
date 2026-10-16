using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework.Landing
{
    public sealed class LandingFootprint
    {
        public IReadOnlyList<IntVec3> SourceCells { get; }
        public CellRect SourceBounds { get; }
        public LandingFootprint(IEnumerable<IntVec3> cells)
        {
            var sorted = cells.Distinct().OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            if (sorted.Count == 0) throw new ArgumentException("Empty ship footprint.", nameof(cells));
            SourceCells = sorted.AsReadOnly();
            int x = sorted.Min(c => c.x), z = sorted.Min(c => c.z);
            SourceBounds = new CellRect(x, z, sorted.Max(c => c.x) - x + 1, sorted.Max(c => c.z) - z + 1);
        }
        public IntVec3 TargetOrigin(IntVec3 center) => new IntVec3(center.x - (SourceBounds.Width - 1) / 2, 0, center.z - (SourceBounds.Height - 1) / 2);
        public IReadOnlyList<IntVec3> TargetCells(IntVec3 center)
        {
            var origin = TargetOrigin(center);
            return SourceCells.Select(c => new IntVec3(origin.x + c.x - SourceBounds.minX, 0, origin.z + c.z - SourceBounds.minZ)).ToList();
        }
    }
}
