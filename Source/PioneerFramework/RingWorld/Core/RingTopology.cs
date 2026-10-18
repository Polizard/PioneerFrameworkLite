using System;
using System.Collections.Generic;

namespace PioneerFramework.RingWorld
{
    public sealed class RingTopology : Megastructures.IArtificialSurfaceGraph
    {
        int Megastructures.IArtificialSurfaceGraph.CellCount=>TileCount;
        double Megastructures.IArtificialSurfaceGraph.Distance(int from,int to)=>Distance(from,to);

        private static readonly int[] Dq = { 1, 1, 0, -1, -1, 0 };
        private static readonly int[] Dr = { 0, -1, -1, 0, 1, 1 };
        public int Columns { get; }
        public int Rows { get; }
        public int TileCount => Columns * Rows;

        public RingTopology(int columns, int rows)
        {
            if (columns < 4 || columns > 4096 || rows < 2 || rows > 512 || (long)columns * rows > 131072)
                throw new ArgumentOutOfRangeException(nameof(columns), "Ring requires 4..4096 columns, 2..512 rows and at most 131072 tiles.");
            Columns = columns; Rows = rows;
        }

        public int Wrap(int q) => ((q % Columns) + Columns) % Columns;
        public int TileId(int q, int r) => r < 0 || r >= Rows ? -1 : r * Columns + Wrap(q);
        public bool Contains(int tile) => (uint)tile < (uint)TileCount;
        public void Check(int tile) { if (!Contains(tile)) throw new ArgumentOutOfRangeException(nameof(tile)); }
        public void GetNeighbors(int tile, List<int> result)
        {
            Check(tile); result.Clear();
            int q = tile % Columns, r = tile / Columns;
            for (int i = 0; i < 6; i++)
            {
                int other = TileId(q + Dq[i], r + Dr[i]);
                if (other >= 0) result.Add(other);
            }
        }
        public int[] Neighbors(int tile) { var result = new List<int>(6); GetNeighbors(tile, result); return result.ToArray(); }

        public int Distance(int from, int to)
        {
            Check(from); Check(to);
            int dq = to % Columns - from % Columns, dr = to / Columns - from / Columns;
            // Center the periodic image around the minimum of the axial hex metric.
            int image = (int)Math.Round((-dq - dr * 0.5) / Columns);
            int best = int.MaxValue;
            for (int k = image - 1; k <= image + 1; k++)
            {
                int x = dq + k * Columns;
                best = Math.Min(best, Math.Max(Math.Abs(x), Math.Max(Math.Abs(dr), Math.Abs(x + dr))));
            }
            return best;
        }
    }
}
