using System;
using System.Collections.Generic;

namespace PioneerFramework.RingWorld
{
    public sealed class RingPathResult
    {
        public IReadOnlyList<int> Nodes { get; }
        public double Cost { get; }
        public bool Found => Nodes.Count > 0;
        internal RingPathResult(IReadOnlyList<int> nodes, double cost) { Nodes = nodes; Cost = cost; }
    }

    public static class RingPathSearch
    {
        public static RingPathResult Find(RingTopology graph, int start, int goal,
            Func<int, int, double>? edgeCost = null, Func<int, bool>? passable = null, Func<double, bool>? terminator = null)
        {
            return Megastructures.SurfacePathSearch.Find(graph,start,goal,static(nodes,cost)=>new RingPathResult(nodes,cost),edgeCost,passable,terminator);
        }
    }
}
