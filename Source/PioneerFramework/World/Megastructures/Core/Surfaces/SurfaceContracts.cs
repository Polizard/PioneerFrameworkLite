using System.Collections.Generic;

namespace PioneerFramework.Megastructures
{
    public readonly record struct SurfaceAddress(NodeAddress Node,string SurfaceId);
    public readonly record struct SurfaceCellAddress(SurfaceAddress Surface,int CellId);
    public readonly record struct SurfaceLocalFrame(DVector3 PositionMeters,DVector3 Normal,DVector3 Tangent);
    public readonly record struct SurfaceCellState(SpatialState Pose,DVector3 Normal,DVector3 Tangent);
    public interface IArtificialSurfaceGraph
    {
        int CellCount { get; }
        void GetNeighbors(int cell,List<int> result);
        double Distance(int from,int to);
    }
    public interface IArtificialSurfaceGeometry:IArtificialSurfaceGraph
    {
        double AverageCellSizeMeters { get; }
        IReadOnlyList<DVector3> GetPolygon(int cell);
        SurfaceLocalFrame EvaluateLocal(int cell);
    }
    public sealed class SurfaceBindingState
    {
        public SurfaceAddress Address { get; set; }
        public StructureBackendRef Backend { get; set; }
        public int Version { get; set; }=1;
        public BindingOwnership Ownership { get; set; }
        public int AssociationGeneration { get; set; }=1;
        public string BlockedReason { get; set; }="";
    }
}
