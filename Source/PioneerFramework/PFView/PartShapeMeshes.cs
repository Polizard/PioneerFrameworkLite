using System.Collections.Generic;
using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// **B3b：按形状取渲染源网格**（2026-10-17）。单位形状都落在 [-0.5,0.5]³，
    /// 由 `PartMatrix` 的 bounds 缩放成实际尺寸；Box 直接用调用方给的单位立方体，
    /// Cylinder（轴 = Y，X/Z = 直径）与 Sphere（X/Y/Z = 直径）在首次使用时生成并缓存。
    ///
    /// 只被游戏内渲染调用（`Mesh` 是引擎对象）；headless 不走这里。
    /// </summary>
    public static class PartShapeMeshes
    {
        private static readonly Dictionary<InertiaTensor.Shape, Mesh> cache = new();

        public static Mesh? For(InertiaTensor.Shape shape, Mesh? boxMesh)
        {
            if (shape == InertiaTensor.Shape.Box) return boxMesh;
            if (cache.TryGetValue(shape, out Mesh? cached) && cached != null) return cached;

            Mesh mesh = shape == InertiaTensor.Shape.Cylinder ? BuildCylinder() : BuildSphere();
            mesh.name = "PF_PartShape_" + shape;
            cache[shape] = mesh;
            return mesh;
        }

        private static Mesh BuildCylinder(int segments = 16)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            // 侧面：上下两圈（半径 0.5，高 1）
            for (int i = 0; i <= segments; i++)
            {
                float u = (float)i / segments;
                float a = u * Mathf.PI * 2f;
                float x = Mathf.Cos(a) * 0.5f;
                float z = Mathf.Sin(a) * 0.5f;
                var n = new Vector3(x, 0f, z).normalized;
                verts.Add(new Vector3(x, -0.5f, z)); normals.Add(n); uvs.Add(new Vector2(u, 0f));
                verts.Add(new Vector3(x, 0.5f, z)); normals.Add(n); uvs.Add(new Vector2(u, 1f));
            }
            for (int i = 0; i < segments; i++)
            {
                int b = i * 2;
                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b + 2); tris.Add(b + 1); tris.Add(b + 3);
            }

            // 端盖（两个圆心 + 各自一圈；法线 ±Y）
            AddCap(verts, normals, uvs, tris, -0.5f, segments, -1f);
            AddCap(verts, normals, uvs, tris, 0.5f, segments, 1f);

            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddCap(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
            List<int> tris, float y, int segments, float ny)
        {
            int center = verts.Count;
            verts.Add(new Vector3(0f, y, 0f)); normals.Add(new Vector3(0f, ny, 0f)); uvs.Add(new Vector2(0.5f, 0.5f));
            int ringStart = verts.Count;
            for (int i = 0; i <= segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                float x = Mathf.Cos(a) * 0.5f;
                float z = Mathf.Sin(a) * 0.5f;
                verts.Add(new Vector3(x, y, z)); normals.Add(new Vector3(0f, ny, 0f));
                uvs.Add(new Vector2(x + 0.5f, z + 0.5f));
            }
            for (int i = 0; i < segments; i++)
            {
                int a = ringStart + i;
                int b = ringStart + i + 1;
                if (ny < 0f) { tris.Add(center); tris.Add(b); tris.Add(a); }
                else { tris.Add(center); tris.Add(a); tris.Add(b); }
            }
        }

        private static Mesh BuildSphere(int lat = 12, int lon = 24)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            for (int y = 0; y <= lat; y++)
            {
                float v = (float)y / lat;
                float phi = v * Mathf.PI;             // 0..π
                float sy = Mathf.Cos(phi) * 0.5f;
                float ring = Mathf.Sin(phi) * 0.5f;
                for (int x = 0; x <= lon; x++)
                {
                    float u = (float)x / lon;
                    float theta = u * Mathf.PI * 2f;
                    var p = new Vector3(Mathf.Cos(theta) * ring, sy, Mathf.Sin(theta) * ring);
                    verts.Add(p);
                    normals.Add(p.normalized);
                    uvs.Add(new Vector2(u, 1f - v));
                }
            }
            for (int y = 0; y < lat; y++)
            {
                for (int x = 0; x < lon; x++)
                {
                    int a = y * (lon + 1) + x;
                    int b = a + 1;
                    int c = a + (lon + 1);
                    int d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }

            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
