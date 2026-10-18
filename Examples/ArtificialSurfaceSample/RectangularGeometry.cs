using System;
using System.Collections.Generic;
using PioneerFramework;
using PioneerFramework.Megastructures;

namespace PFL.ArtificialSurfaceSample
{
    public sealed class RectangularGeometry:IArtificialSurfaceGeometry
    {
        public int Columns {get;}
        public int Rows {get;}
        public int CellCount=>checked(Columns*Rows);
        public double AverageCellSizeMeters {get;}
        public RectangularGeometry(int columns=3,int rows=2,double spacing=100000)
        {
            if(columns<1||rows<1||columns>256||rows>256||!double.IsFinite(spacing)||spacing<=0)throw new ArgumentOutOfRangeException(nameof(columns));
            Columns=columns;Rows=rows;AverageCellSizeMeters=spacing;
        }
        private void Check(int c){if(c<0||c>=CellCount)throw new ArgumentOutOfRangeException(nameof(c));}
        public void GetNeighbors(int cell,List<int> result)
        {
            Check(cell);result.Clear();int x=cell%Columns,y=cell/Columns;
            if(x>0)result.Add(cell-1);if(x<Columns-1)result.Add(cell+1);
            if(y>0)result.Add(cell-Columns);if(y<Rows-1)result.Add(cell+Columns);
        }
        public double Distance(int from,int to){Check(from);Check(to);return Math.Abs(from%Columns-to%Columns)+Math.Abs(from/Columns-to/Columns);}
        public SurfaceLocalFrame EvaluateLocal(int cell)
        {Check(cell);return new(new((cell%Columns-(Columns-1)*.5)*AverageCellSizeMeters,0,(cell/Columns-(Rows-1)*.5)*AverageCellSizeMeters),new(0,1,0),new(1,0,0));}
        public IReadOnlyList<DVector3> GetPolygon(int cell)
        {
            var p=EvaluateLocal(cell).PositionMeters;double h=AverageCellSizeMeters*.5;
            return new[]{p+new DVector3(-h,0,-h),p+new DVector3(h,0,-h),p+new DVector3(h,0,h),p+new DVector3(-h,0,h)};
        }
    }
}
