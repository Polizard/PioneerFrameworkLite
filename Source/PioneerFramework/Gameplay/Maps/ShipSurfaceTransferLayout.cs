using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace PioneerFramework
{
    /// <summary>Maps a caller-selected ship region onto an existing surface map.</summary>
    public sealed class ShipSurfaceTransferLayout
    {
        private readonly HashSet<IntVec3> sourceSet;
        private readonly IntVec3 sourceOrigin;
        private readonly IntVec3 targetOrigin;

        public IReadOnlyList<IntVec3> SourceCells { get; }

        private ShipSurfaceTransferLayout(List<IntVec3> cells, IntVec3 sourceOrigin, IntVec3 targetOrigin)
        {
            SourceCells = cells.AsReadOnly();
            sourceSet = new HashSet<IntVec3>(cells);
            this.sourceOrigin = sourceOrigin;
            this.targetOrigin = targetOrigin;
        }

        public IntVec3 MapCell(IntVec3 sourceCell)
        {
            if (!sourceSet.Contains(sourceCell))
                throw new ArgumentOutOfRangeException(nameof(sourceCell), "Cell is outside the selected region.");
            return new IntVec3(targetOrigin.x + sourceCell.x - sourceOrigin.x, 0,
                targetOrigin.z + sourceCell.z - sourceOrigin.z);
        }

        public bool ContainsFootprint(CellRect footprint)
        {
            foreach (IntVec3 cell in footprint.Cells)
                if (!sourceSet.Contains(cell)) return false;
            return true;
        }

        public static bool TryCreate(IEnumerable<IntVec3> sourceCells, IntVec2 sourceSize,
            IntVec2 targetSize, IntVec3 targetOrigin,
            out ShipSurfaceTransferLayout layout, out string error)
        {
            layout = null!;
            error = string.Empty;
            if (sourceCells == null)
            {
                error = "Source cells are missing.";
                return false;
            }

            List<IntVec3> cells = sourceCells.Distinct().OrderBy(c => c.z).ThenBy(c => c.x).ToList();
            if (cells.Count == 0)
            {
                error = "The selected ship region is empty.";
                return false;
            }

            int minX = cells.Min(c => c.x);
            int minZ = cells.Min(c => c.z);
            foreach (IntVec3 cell in cells)
            {
                if (cell.y != 0 || cell.x < 0 || cell.z < 0 ||
                    cell.x >= sourceSize.x || cell.z >= sourceSize.z)
                {
                    error = $"Source cell {cell} is outside the source map.";
                    return false;
                }
                long x = (long)targetOrigin.x + cell.x - minX;
                long z = (long)targetOrigin.z + cell.z - minZ;
                if (targetOrigin.y != 0 || x < 0 || z < 0 || x >= targetSize.x || z >= targetSize.z)
                {
                    error = $"Mapped target for {cell} is outside the target map.";
                    return false;
                }
            }

            layout = new ShipSurfaceTransferLayout(cells, new IntVec3(minX, 0, minZ), targetOrigin);
            return true;
        }
    }
}
