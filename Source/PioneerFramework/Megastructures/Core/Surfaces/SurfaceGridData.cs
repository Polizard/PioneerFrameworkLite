using System;
using System.Collections.Generic;

namespace PioneerFramework.Megastructures
{
    public sealed record SurfaceGridData(DVector3[] Vertices,int[] VertexOffsets,int[] NeighborOffsets,int[] Neighbors)
    {
        public static SurfaceGridData Build(IArtificialSurfaceGeometry geometry)
        {
            if(geometry.CellCount<=0||!double.IsFinite(geometry.AverageCellSizeMeters)||geometry.AverageCellSizeMeters<=0)throw new ArgumentException("Invalid surface size.");
            var vertices=new List<DVector3>();var vertexOffsets=new int[geometry.CellCount];
            var offsets=new int[geometry.CellCount];var values=new List<int>();var neighbors=new List<int>();
            for(int cell=0;cell<geometry.CellCount;cell++)
            {
                vertexOffsets[cell]=vertices.Count;var polygon=geometry.GetPolygon(cell);
                if(polygon.Count<3)throw new ArgumentException("A surface cell requires at least three vertices.");
                _=checked(vertices.Count+polygon.Count);
                foreach(var point in polygon){if(!point.IsFinite())throw new ArgumentException("Invalid polygon vertex.");vertices.Add(point);}
                offsets[cell]=values.Count;geometry.GetNeighbors(cell,neighbors);_=checked(values.Count+neighbors.Count);
                foreach(int neighbor in neighbors){if(neighbor<0||neighbor>=geometry.CellCount)throw new ArgumentException("Invalid neighbor.");values.Add(neighbor);}
            }
            return new(vertices.ToArray(),vertexOffsets,offsets,values.ToArray());
        }
    }
}
