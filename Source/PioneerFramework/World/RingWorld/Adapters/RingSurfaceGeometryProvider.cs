using System.Collections.Generic;
using PioneerFramework.Megastructures;

namespace PioneerFramework.RingWorld
{
    /// <summary>Ring-only shape. All coordinates supplied to shared services are meters.</summary>
    public sealed class RingSurfaceGeometryProvider:IArtificialSurfaceGeometry
    {
        private readonly RingGeometry geometry;
        public RingSurfaceGeometryProvider(RingParameters parameters)=>geometry=new(new(parameters.Columns,parameters.Rows),parameters.RadiusMeters);
        public int CellCount=>geometry.Topology.TileCount;
        public double AverageCellSizeMeters=>geometry.Spacing;
        public void GetNeighbors(int cell,List<int> result)=>geometry.Topology.GetNeighbors(cell,result);
        public IReadOnlyList<DVector3> GetPolygon(int cell)=>geometry.Vertices(cell);
        public SurfaceLocalFrame EvaluateLocal(int cell)=>new(geometry.Center(cell),geometry.Normal(cell),geometry.Tangent(cell));
        public double Distance(int from,int to)=>geometry.Topology.Distance(from,to);
    }
}
