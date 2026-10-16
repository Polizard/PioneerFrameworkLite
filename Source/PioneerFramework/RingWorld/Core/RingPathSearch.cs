using System;
using System.Collections.Generic;

namespace PioneerFramework.RingWorld
{
    public sealed class RingPathResult
    {
        public IReadOnlyList<int> Nodes { get; }
        public double Cost { get; }
        public bool Found => Nodes.Count > 0;
        internal RingPathResult(List<int> nodes, double cost) { Nodes = nodes; Cost = cost; }
    }

    public static class RingPathSearch
    {
        public static RingPathResult Find(RingTopology graph, int start, int goal,
            Func<int, int, double>? edgeCost = null, Func<int, bool>? passable = null, Func<double, bool>? terminator = null)
        {
            graph.Check(start); graph.Check(goal);
            var notFound = new RingPathResult(new List<int>(), double.PositiveInfinity);
            if (passable != null && (!passable(start) || !passable(goal))) return notFound;
            var costs = new double[graph.TileCount]; Array.Fill(costs, double.PositiveInfinity);
            var previous = new int[graph.TileCount]; Array.Fill(previous, -1);
            var frontier = new PriorityQueue<int, (double, int)>(); var neighbors = new List<int>(6);
            costs[start] = 0; frontier.Enqueue(start, (0, start));
            while (frontier.TryDequeue(out int tile, out var priority))
            {
                double cost = priority.Item1;
                if (cost != costs[tile]) continue;
                if (terminator?.Invoke(cost) == true) return notFound;
                if (tile == goal)
                {
                    var nodes = new List<int>();
                    for (int t = tile; t >= 0; t = previous[t]) nodes.Add(t);
                    nodes.Reverse(); return new RingPathResult(nodes, cost);
                }
                graph.GetNeighbors(tile, neighbors);
                foreach (int other in neighbors)
                {
                    if (passable?.Invoke(other) == false) continue;
                    double step = edgeCost?.Invoke(tile, other) ?? 1;
                    if (double.IsNaN(step) || step < 0) throw new ArgumentOutOfRangeException(nameof(edgeCost), "Edge cost must be nonnegative.");
                    double candidate = cost + step;
                    if (candidate >= costs[other]) continue;
                    costs[other] = candidate; previous[other] = tile;
                    frontier.Enqueue(other, (candidate, other));
                }
            }
            return notFound;
        }
    }
}
