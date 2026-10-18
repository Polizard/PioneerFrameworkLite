using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Megastructures
{
    public static class SurfaceQueryService
    {
        public static IEnumerable<int> SelectByDistance(IArtificialSurfaceGraph graph,int origin,double min,double max)
        {
            if(origin<0||origin>=graph.CellCount)throw new ArgumentOutOfRangeException(nameof(origin));
            return Enumerable.Range(0,graph.CellCount).Select(cell=>(cell,distance:graph.Distance(origin,cell)))
                .Where(x=>x.distance>=min&&x.distance<=max).OrderBy(x=>x.distance).ThenBy(x=>x.cell).Select(x=>x.cell);
        }
    }
}
