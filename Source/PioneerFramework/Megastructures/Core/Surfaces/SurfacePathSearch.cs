using System;
using System.Collections.Generic;

namespace PioneerFramework.Megastructures
{
    public sealed class SurfacePathResult
    {
        public IReadOnlyList<int> Nodes { get; }
        public double Cost { get; }
        public bool Found => Nodes.Count > 0;
        internal SurfacePathResult(List<int> nodes, double cost) { Nodes = nodes; Cost = cost; }
    }

    public static class SurfacePathSearch
    {
        public static SurfacePathResult Find(IArtificialSurfaceGraph graph, int start, int goal,
            Func<int, int, double>? edgeCost = null, Func<int, bool>? passable = null, Func<double, bool>? terminator = null)
            =>Find(graph,start,goal,static(nodes,cost)=>new SurfacePathResult(nodes,cost),edgeCost,passable,terminator);
        public static TResult Find<TResult>(IArtificialSurfaceGraph graph,int start,int goal,
            Func<List<int>,double,TResult> createResult,Func<int,int,double>? edgeCost=null,Func<int,bool>? passable=null,Func<double,bool>? terminator=null)
        {
            var result=Search(graph,start,goal,edgeCost,passable,terminator);
            return createResult(result.Nodes,result.Cost);
        }
        private readonly record struct PathData(List<int> Nodes,double Cost);
        private static PathData NotFound()=>new(new List<int>(),double.PositiveInfinity);
        // Keep the shared search loop independent of result projection and generic context.
        private static PathData Search(IArtificialSurfaceGraph graph,int start,int goal,
            Func<int,int,double>? edgeCost,Func<int,bool>? passable,Func<double,bool>? terminator)
        {
            if(start<0||start>=graph.CellCount||goal<0||goal>=graph.CellCount)throw new ArgumentOutOfRangeException(nameof(start));
            if (passable != null && (!passable(start) || !passable(goal))) return NotFound();
            var costs = new double[graph.CellCount]; Array.Fill(costs, double.PositiveInfinity);
            var previous = new int[graph.CellCount]; Array.Fill(previous, -1);
            var frontier = new PriorityQueue<int, (double, int)>(); var neighbors = new List<int>(6);
            costs[start] = 0; frontier.Enqueue(start, (0, start));
            while (frontier.TryDequeue(out int tile, out var priority))
            {
                double cost = priority.Item1;
                if (cost != costs[tile]) continue;
                if (terminator?.Invoke(cost) == true) return NotFound();
                if (tile == goal)
                {
                    var nodes = new List<int>();
                    for (int t = tile; t >= 0; t = previous[t]) nodes.Add(t);
                    nodes.Reverse(); return new(nodes, cost);
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
            return NotFound();
        }
    }
}
