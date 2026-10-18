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
            // 2026-10-17 电影感贴图：按世界尺度（贴图 texel 纵横比）决定环向重复数，
            // 让 256×3 与 1024×12 的贴图密度一致；贴图横向严格无缝 + Repeat，任意整数都安全。
            // 仍取偶数：保留历史"镜像接缝"时期的既有 UV 连续性测试判据（只加强不软化）。
            int repeats = SurfaceRepeats(geometry);
            // V 使用整个带宽（旧口径只采样贴图中段 36%，新贴图按整条带设计）。
            double halfWidth = geometry.HalfWidth;
            double v = (position.y + halfWidth) / (2 * halfWidth);
            return new RingVisualPoint(position, turn * repeats, v);
        }

        /// <summary>居住面贴图（4096×1024）对应的环向重复数（世界尺度、偶数）。</summary>
        public static int SurfaceRepeats(RingGeometry geometry) => EvenWorldRepeats(geometry, 4096, 1024);

        /// <summary>外壳贴图（2048×512）对应的环向重复数（世界尺度、偶数）。</summary>
        public static int StructureRepeats(RingGeometry geometry) => EvenWorldRepeats(geometry, 2048, 512);

        /// <summary>
        /// 给定贴图宽高，按"环周长 / 带宽"算出让 texel 接近正方形的环向重复数。
        /// 结果强制为偶数：历史 UV 测试按"偶数重复 + Mirror 接缝"设计（新贴图已无缝，
        /// 但仍保留该约束以免软化既有判据）。
        /// </summary>
        public static int EvenWorldRepeats(RingGeometry geometry, int textureWidth, int textureHeight)
        {
            if (textureWidth <= 0) throw new ArgumentOutOfRangeException(nameof(textureWidth));
            if (textureHeight <= 0) throw new ArgumentOutOfRangeException(nameof(textureHeight));
            double circumference = Math.Tau * geometry.Radius;
            double bandWidth = Math.Max(1e-6, 2 * geometry.HalfWidth);
            double desired = (double)textureHeight * circumference / (bandWidth * textureWidth);
            return 2 * Math.Max(1, (int)Math.Round(desired / 2.0));
        }

        /// <summary>
        /// 四边形可见侧法线：A-B-C / A-C-D 绕序的 cross。Hull 朝外、内衬朝内已在
        /// `RingWorldVisualTests.BackHullClosesExactlyAtSeamAndFacesOutward` /
        /// `GapAboveZigzagHabitatEdgeHasInwardFacingBacking` 中锚定。
        /// </summary>
        public static DVector3 FaceNormal(RingVisualQuad quad)
        {
            DVector3 n = DVector3.Cross(quad.B.Position - quad.A.Position, quad.C.Position - quad.A.Position);
            return n.magnitude > 1e-12 ? n.normalized : new DVector3(0, 1, 0);
        }

        /// <summary>
        /// 居住面（圆柱内表面）逐顶点朝内法线。与 hex 三角绕序数值复算一致（见 IMPL §2.3）。
        /// </summary>
        public static DVector3 HabitatNormal(DVector3 position)
        {
            double length = Math.Sqrt(position.x * position.x + position.z * position.z);
            return length > 1e-12 ? new DVector3(-position.x / length, 0, -position.z / length) : new DVector3(0, 0, 1);
        }

        public static IEnumerable<RingVisualQuad> Build(RingGeometry geometry)
        {
            double radius = geometry.Radius, spacing = geometry.Spacing;
            double halfWidth = geometry.HalfWidth;
            double back = radius + spacing * .08;
            int segments = Math.Clamp(geometry.Topology.Columns, 128, 512);
            int bays = Math.Clamp(geometry.Topology.Columns / 4, 16, 128);
            int repeats = StructureRepeats(geometry);

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

            // 两道沿周向的纵向脊梁（在带面 ±42% 处凸起），与每个 bay 的横向肋骨形成"井"字骨架，
            // 受光后产生连续的高光/阴影线，增强外壳体积感（2026-10-17 凹凸增强）。
            int keelSegments = Math.Clamp(segments / 2, 128, 256);
            foreach (int side in new[] { -1, 1 })
            {
                double keelY = side * halfWidth * .42;
                foreach (var q in BoxBand(0, 1, keelY - spacing * .035, keelY + spacing * .035,
                    back + spacing * .018, back + spacing * .15, keelSegments, capped: false)) yield return q;
            }

            // 2026-10-17 用户反馈"X 形钢架不美观"：移除原来的两道交叉斜撑（Brace），
            // 改为**凹凸起伏的装甲凸块 + 每 4 bay 一座 L 形检修吊臂**（无交叉线框）。
            for (int i = 0; i < bays; i++)
            {
                double a = (double)i / bays, b = (double)(i + 1) / bays;
                double ribHalfTurn = spacing * .03 / (radius * Math.Tau);
                foreach (var q in BoxBand(a - ribHalfTurn, a + ribHalfTurn, -halfWidth, halfWidth,
                    back + spacing * .008, back + spacing * .11, 1, capped: true)) yield return q;

                // 3 个不同高度/位置的凸起装甲块：从侧面看形成明确的前后层次（受光后就是凹凸感）。
                int seed = 0x2B9F13 + i * 211;
                for (int k = 0; k < 3; k++)
                {
                    int s = seed + k * 29;
                    double center = a + (b - a) * (0.18 + 0.28 * k + Range(s + 1, -.04, .04));
                    double halfTurn = (b - a) * Range(s + 2, .055, .105);
                    double yCenter = Range(s + 3, -.58, .58) * halfWidth;
                    double yHalf = Math.Max(.12, Range(s + 4, .10, .22) * halfWidth);
                    double height = spacing * Range(s + 5, .03, .16);
                    foreach (var q in BoxBand(center - halfTurn, center + halfTurn, yCenter - yHalf, yCenter + yHalf,
                        back + spacing * .012, back + height, 1, capped: true)) yield return q;
                }

                // 每 4 个 bay 一座 L 形吊臂：径向伸出 + 沿环带边缘向上竖起，强调上下结构。
                if ((i & 3) == 0)
                {
                    double g = a + (b - a) * .5;
                    double half = (b - a) * .055;
                    double armRadius = back + spacing * .62;
                    foreach (var q in BoxBand(g - half, g + half, -spacing * .07, spacing * .07,
                        back + spacing * .02, armRadius, 1, capped: true)) yield return q;
                    foreach (var q in BoxBand(g - half, g + half, halfWidth + spacing * .02,
                        halfWidth + spacing * .55, armRadius - spacing * .05, armRadius + spacing * .05,
                        1, capped: true)) yield return q;
                }

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

        // ── 确定性哈希（凸块布局用；不使用 Rand，纯核可测/跨平台逐位一致） ─────

        private static double Hash01(int seed)
        {
            unchecked
            {
                uint x = (uint)seed * 0x9E3779B1u;
                x ^= x >> 16;
                x *= 0x85EBCA6Bu;
                x ^= x >> 13;
                x *= 0xC2B2AE35u;
                x ^= x >> 16;
                return x / 4294967296.0;
            }
        }

        private static double Range(int seed, double min, double max) => min + (max - min) * Hash01(seed);
    }
}
