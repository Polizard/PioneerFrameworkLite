using System;

namespace PioneerFramework.RingWorld
{
    public sealed class RingGeometry
    {
        private static readonly int[] CornerX = { 1, 0, -1, -1, 0, 1 };
        private static readonly int[] CornerY = { 1, 2, 1, -1, -2, -1 };
        public RingTopology Topology { get; }
        public double Radius { get; }
        public double Spacing => 2 * Math.PI * Radius / Topology.Columns;
        public double RowSpacing => Math.Sqrt(3) * Spacing / 2;

        /// <summary>环带轴向半宽（含六边形外伸角，与 RingIndustrialGeometry 的 halfWidth 同口径）。</summary>
        public double HalfWidth => RowSpacing * (Topology.Rows - 1) / 2 + Spacing / Math.Sqrt(3);

        public RingGeometry(RingTopology topology, double radius)
        {
            if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
            Topology = topology; Radius = radius;
        }
        private DVector3 Position(int halfColumn, double thirdRow)
        {
            int period = 2 * Topology.Columns;
            double theta = Math.PI * (((halfColumn % period) + period) % period) / Topology.Columns;
            double y = (thirdRow - 1.5 * (Topology.Rows - 1)) * Spacing / (2 * Math.Sqrt(3));
            return new DVector3(Radius * Math.Cos(theta), y, Radius * Math.Sin(theta));
        }
        public DVector3 Center(int tile)
        {
            Topology.Check(tile); int q = tile % Topology.Columns, r = tile / Topology.Columns;
            return Position(2 * q + r, 3 * r);
        }
        public DVector3[] Vertices(int tile)
        {
            Topology.Check(tile); int q = tile % Topology.Columns, r = tile / Topology.Columns;
            var result = new DVector3[6];
            for (int i = 0; i < 6; i++) result[i] = Position(2 * q + r + CornerX[i], 3 * r + CornerY[i]);
            return result;
        }
        public DVector3 Normal(int tile) { DVector3 p = Center(tile); return new DVector3(-p.x / Radius, 0, -p.z / Radius); }
        public DVector3 Tangent(int tile) { DVector3 p = Center(tile); return new DVector3(-p.z / Radius, 0, p.x / Radius); }
        public DVector3 ProjectDirectionToSurface(DVector3 direction)
        {
            double radial = Math.Sqrt(direction.x * direction.x + direction.z * direction.z);
            if (radial < 1e-4)
            {
                direction = new DVector3(0, 0, 1);
                radial = 1;
            }
            double halfHeight = RowSpacing * (Topology.Rows - 1) / 2;
            return new DVector3(direction.x / radial * Radius,
                Math.Clamp(direction.y / radial * Radius, -halfHeight, halfHeight),
                direction.z / radial * Radius);
        }
        public int ClosestTile(DVector3 position)
        {
            if (!double.IsFinite(position.x) || !double.IsFinite(position.y) || !double.IsFinite(position.z))
                throw new ArgumentOutOfRangeException(nameof(position));
            double angle = Math.Atan2(position.z, position.x);
            double s = angle / (2 * Math.PI) * Topology.Columns;
            int row = Math.Clamp((int)Math.Round(position.y / RowSpacing + (Topology.Rows - 1) * 0.5), 0, Topology.Rows - 1);
            int best = -1; double bestDistance = double.PositiveInfinity;
            for (int r = Math.Max(0, row - 1); r <= Math.Min(Topology.Rows - 1, row + 1); r++)
            {
                int q = (int)Math.Round(s - r * 0.5);
                for (int dq = -1; dq <= 1; dq++)
                {
                    int tile = Topology.TileId(q + dq, r);
                    double distance = (Center(tile) - position).sqrMagnitude;
                    if (distance < bestDistance) { bestDistance = distance; best = tile; }
                }
            }
            return best;
        }
        public double Heading(int from, int to)
        {
            Topology.Check(from); Topology.Check(to);
            double ds = (to % Topology.Columns - from % Topology.Columns) + (to / Topology.Columns - from / Topology.Columns) * 0.5;
            ds -= Math.Round(ds / Topology.Columns) * Topology.Columns;
            double dz = (to / Topology.Columns - from / Topology.Columns) * Math.Sqrt(3) / 2;
            return (Math.Atan2(ds, dz) * 180 / Math.PI + 360) % 360;
        }
        public bool IntersectsSegment(DVector3 from, DVector3 to, double radiusFactor = 1)
        {
            DVector3 delta=to-from;
            double a=delta.x*delta.x+delta.z*delta.z;
            if(a<1e-15) return false;
            double b=2*(from.x*delta.x+from.z*delta.z);
            double radius=Radius*radiusFactor;
            double c=from.x*from.x+from.z*from.z-radius*radius;
            double discriminant=b*b-4*a*c;
            if(discriminant<0) return false;
            double halfHeight=RowSpacing*(Topology.Rows-1)/2+Spacing/Math.Sqrt(3);
            double root=Math.Sqrt(discriminant);
            foreach(double t in new[]{(-b-root)/(2*a),(-b+root)/(2*a)})
                if(t>1e-5&&t<1-1e-5&&Math.Abs(from.y+delta.y*t)<=halfHeight) return true;
            return false;
        }
    }
}
