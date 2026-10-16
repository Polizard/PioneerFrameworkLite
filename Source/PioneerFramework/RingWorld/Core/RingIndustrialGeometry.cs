using System;
using System.Collections.Generic;

namespace PioneerFramework.RingWorld
{
    public enum RingVisualMaterial { Hull, Frame, Pipe, WarningLight }

    public readonly record struct RingVisualPoint(DVector3 Position, double U, double V);

    /// <summary>Winding is A-B-C / A-C-D. These faces never enter the tile picking mesh.</summary>
    public readonly record struct RingVisualQuad(RingVisualMaterial Material,
        RingVisualPoint A, RingVisualPoint B, RingVisualPoint C, RingVisualPoint D);

    public static class RingIndustrialGeometry
    {
        public static RingVisualPoint SurfaceUV(RingGeometry geometry, int tile, DVector3 position)
        {
            geometry.Topology.Check(tile);
            int columns = geometry.Topology.Columns;
            double reference = (tile % columns + (tile / columns) * .5) / columns;
            double turn = Math.Atan2(position.z, position.x) / Math.Tau;
            turn += Math.Round(reference - turn);
            // Even repeat counts close both the geometry and the mirrored texture at the seam.
            int repeats = 2 * Math.Max(1, (int)Math.Round(columns / 16.0));
            return new RingVisualPoint(position, turn * repeats, position.y / (geometry.Spacing * 8) + .5);
        }

        public static IEnumerable<RingVisualQuad> Build(RingGeometry geometry)
        {
            double radius = geometry.Radius, spacing = geometry.Spacing;
            double halfWidth = geometry.RowSpacing * (geometry.Topology.Rows - 1) / 2 + spacing / Math.Sqrt(3);
            double back = radius + spacing * .08;
            int segments = Math.Clamp(geometry.Topology.Columns, 128, 512);
            int bays = Math.Clamp(geometry.Topology.Columns / 4, 16, 128);
            int repeats = 2 * Math.Max(1, segments / 16);

            for (int i = 0; i < segments; i++)
            {
                double a = (double)i / segments, b = (double)(i + 1) / segments;
                yield return Cylinder(RingVisualMaterial.Hull, a, b, -halfWidth, halfWidth, back, repeats);
                // Close the pockets between the sawtooth hex boundary and the straight edge girders.
                // This inward face stays behind every playable tile and has no picking collider.
                yield return Reverse(Cylinder(RingVisualMaterial.Frame, a, b, -halfWidth, halfWidth,
                    radius + spacing * .02, repeats));
                yield return Cap(a, b, halfWidth, radius, back, repeats, upper: true);
                yield return Cap(a, b, -halfWidth, radius, back, repeats, upper: false);
            }

            // Raised edge girders stay outside the bounding width of every playable hexagon.
            foreach (int side in new[] { -1, 1 })
            {
                double y0 = side > 0 ? halfWidth : -halfWidth - spacing * .18;
                double y1 = y0 + spacing * .18;
                foreach (var q in BoxBand(0, 1, y0, y1, radius - spacing * .08,
                    back + spacing * .12, segments, capped: false)) yield return q;
                foreach (var q in Pipe(back + spacing * .17, side * (halfWidth + spacing * .09),
                    spacing * .035, segments)) yield return q;
            }
            foreach (var q in Pipe(back + spacing * .055, 0, spacing * .03, segments)) yield return q;

            for (int i = 0; i < bays; i++)
            {
                double a = (double)i / bays, b = (double)(i + 1) / bays;
                double ribHalfTurn = spacing * .03 / (radius * Math.Tau);
                foreach (var q in BoxBand(a - ribHalfTurn, a + ribHalfTurn, -halfWidth, halfWidth,
                    back + spacing * .008, back + spacing * .11, 1, capped: true)) yield return q;

                // Curved cross-braces follow the cylinder rather than cutting a chord through the habitat.
                foreach (var q in Brace(a, b, -halfWidth, halfWidth, back + spacing * .015, spacing)) yield return q;
                foreach (var q in Brace(a, b, halfWidth, -halfWidth, back + spacing * .015, spacing)) yield return q;
                foreach (int side in new[] { -1, 1 })
                {
                    double y0 = side > 0 ? halfWidth + spacing * .035 : -halfWidth - spacing * .145;
                    double y1 = y0 + spacing * .11;
                    double light0 = a + (b - a) * .39, light1 = a + (b - a) * .57;
                    yield return Cylinder(RingVisualMaterial.WarningLight, light0, light1,
                        y0, y1, back + spacing * .121, 1);
                    yield return Reverse(Cylinder(RingVisualMaterial.WarningLight, light0, light1,
                        y0, y1, radius - spacing * .081, 1));
                }
            }
        }

        private static DVector3 At(double turn, double y, double radius)
        {
            turn -= Math.Floor(turn);
            double angle = turn * Math.Tau;
            return new DVector3(Math.Cos(angle) * radius, y, Math.Sin(angle) * radius);
        }

        private static RingVisualQuad Quad(RingVisualMaterial kind, DVector3 a, DVector3 b,
            DVector3 c, DVector3 d, double u0 = 0, double v0 = 0, double u1 = 1, double v1 = 1) =>
            new RingVisualQuad(kind, new(a,u0,v0), new(b,u0,v1), new(c,u1,v1), new(d,u1,v0));

        private static RingVisualQuad Reverse(RingVisualQuad q) => new(q.Material,q.D,q.C,q.B,q.A);

        private static RingVisualQuad Cylinder(RingVisualMaterial kind, double a, double b,
            double y0, double y1, double radius, int repeats) => Quad(kind,
                At(a,y0,radius), At(a,y1,radius), At(b,y1,radius), At(b,y0,radius),
                a*repeats, 0, b*repeats, 1);

        private static RingVisualQuad Cap(double a, double b, double y, double r0, double r1,
            int repeats, bool upper)
        {
            var q = new RingVisualQuad(RingVisualMaterial.Frame,
                new(At(a,y,r0),a*repeats,0), new(At(b,y,r0),b*repeats,0),
                new(At(b,y,r1),b*repeats,1), new(At(a,y,r1),a*repeats,1));
            return upper ? q : Reverse(q);
        }

        private static IEnumerable<RingVisualQuad> BoxBand(double start, double end, double y0,
            double y1, double r0, double r1, int segments, bool capped)
        {
            for (int i = 0; i < segments; i++)
            {
                double a = start + (end-start)*i/segments, b = start + (end-start)*(i+1)/segments;
                yield return Cylinder(RingVisualMaterial.Frame,a,b,y0,y1,r1,16);
                yield return Reverse(Cylinder(RingVisualMaterial.Frame,a,b,y0,y1,r0,16));
                yield return Cap(a,b,y1,r0,r1,16,upper:true);
                yield return Cap(a,b,y0,r0,r1,16,upper:false);
            }
            if (!capped) yield break;
            yield return Quad(RingVisualMaterial.Frame,At(start,y0,r0),At(start,y1,r0),At(start,y1,r1),At(start,y0,r1));
            yield return Quad(RingVisualMaterial.Frame,At(end,y0,r1),At(end,y1,r1),At(end,y1,r0),At(end,y0,r0));
        }

        private static IEnumerable<RingVisualQuad> Brace(double a, double b, double y0,
            double y1, double radius, double spacing)
        {
            double dx = (b-a)*Math.Tau*radius, dy = y1-y0;
            double length = Math.Sqrt(dx*dx+dy*dy);
            double du = -dy/length * spacing*.035 / (radius*Math.Tau);
            double dv = dx/length * spacing*.035;
            DVector3[] Section(double t)
            {
                double u = a+(b-a)*t, y = y0+(y1-y0)*t;
                return new[] { At(u-du,y-dv,radius), At(u-du,y-dv,radius+spacing*.055),
                    At(u+du,y+dv,radius+spacing*.055), At(u+du,y+dv,radius) };
            }
            var previous = Section(0);
            yield return Quad(RingVisualMaterial.Frame,previous[3],previous[2],previous[1],previous[0]);
            for (int step = 1; step <= 4; step++)
            {
                var next = Section(step/4.0);
                for (int i = 0; i < 4; i++)
                    yield return Quad(RingVisualMaterial.Frame,previous[i],previous[(i+1)%4],next[(i+1)%4],next[i]);
                previous = next;
            }
            yield return Quad(RingVisualMaterial.Frame,previous[0],previous[1],previous[2],previous[3]);
        }

        private static IEnumerable<RingVisualQuad> Pipe(double radius, double y, double size, int segments)
        {
            DVector3 Point(double turn, int side)
            {
                double angle = (side%6)*Math.Tau/6;
                return At(turn,y+Math.Sin(angle)*size,radius+Math.Cos(angle)*size);
            }
            for (int i = 0; i < segments; i++)
            for (int side = 0; side < 6; side++)
            {
                double a = (double)i/segments, b = (double)(i+1)/segments;
                yield return Quad(RingVisualMaterial.Pipe,Point(a,side),Point(a,side+1),
                    Point(b,side+1),Point(b,side),a*16,(double)side/6,b*16,(double)(side+1)/6);
            }
        }
    }
}
