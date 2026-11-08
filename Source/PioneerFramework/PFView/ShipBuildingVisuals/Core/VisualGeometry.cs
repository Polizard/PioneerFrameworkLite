using System;
using System.Collections.Generic;
using System.Numerics;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

/// <summary>CPU geometry; providers own the arrays and must keep them immutable while registered.</summary>
public sealed record VisualMeshData(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, int[] Indices);

public static class VisualGeometry
{
    public static VisualMeshData Build(VisualPrimitive primitive)
    {
        var p = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            int first = p.Count;
            p.Add(a); p.Add(b); p.Add(c);
            n.Add(normal); n.Add(normal); n.Add(normal);
            uv.Add(new(0, 0)); uv.Add(new(1, 0)); uv.Add(new(0, 1));
            t.Add(first); t.Add(first + 1); t.Add(first + 2);
        }
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { Triangle(a, b, c); Triangle(a, c, d); }
        if (primitive == VisualPrimitive.Box)
        {
            Vector3[] v = [new(-.5f,-.5f,-.5f),new(.5f,-.5f,-.5f),new(.5f,.5f,-.5f),new(-.5f,.5f,-.5f),
                new(-.5f,-.5f,.5f),new(.5f,-.5f,.5f),new(.5f,.5f,.5f),new(-.5f,.5f,.5f)];
            int[][] faces = [[0,3,2,1],[4,5,6,7],[0,1,5,4],[3,7,6,2],[1,2,6,5],[0,4,7,3]];
            foreach (var f in faces) Quad(v[f[0]], v[f[1]], v[f[2]], v[f[3]]);
        }
        else if (primitive == VisualPrimitive.Cylinder)
        {
            for (int i = 0; i < 12; i++)
            {
                float a = i * MathF.Tau / 12, b = (i + 1) * MathF.Tau / 12;
                var a0 = new Vector3(MathF.Sin(a) * .5f, -.5f, MathF.Cos(a) * .5f);
                var b0 = new Vector3(MathF.Sin(b) * .5f, -.5f, MathF.Cos(b) * .5f);
                var a1 = a0 + Vector3.UnitY; var b1 = b0 + Vector3.UnitY;
                Quad(a0, b0, b1, a1);
                Triangle(new(0, -.5f, 0), b0, a0);
                Triangle(new(0, .5f, 0), a1, b1);
            }
        }
        else if (primitive == VisualPrimitive.Dish)
        {
            // Double-sided shallow dish, opening toward +Z, within the unit cube.
            for (int i = 0; i < 12; i++)
            {
                float a = i * MathF.Tau / 12, b = (i + 1) * MathF.Tau / 12;
                var rimA = new Vector3(MathF.Cos(a) * .5f, MathF.Sin(a) * .5f, .2f);
                var rimB = new Vector3(MathF.Cos(b) * .5f, MathF.Sin(b) * .5f, .2f);
                Triangle(new(0, 0, -.2f), rimA, rimB);
                Triangle(new(0, 0, -.2f), rimB, rimA);
            }
        }
        else throw new ArgumentOutOfRangeException(nameof(primitive));
        return new(p.ToArray(), n.ToArray(), uv.ToArray(), t.ToArray());
    }
}
