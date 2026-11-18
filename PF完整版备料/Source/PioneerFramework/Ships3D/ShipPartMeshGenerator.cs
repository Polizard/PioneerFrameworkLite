using System.Collections.Generic;
using UnityEngine;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 程序化生成飞船部件的 3D Mesh（纯几何）。
    /// 当前支持：正方体 (Cube)、角球体 (SphericalCorner)。
    /// </summary>
    public static class ShipPartMeshGenerator
    {
        private static Mesh _cubeMesh;
        private static Mesh _cornerMesh;

        public static Mesh GetMesh(ShipPartShape shape)
        {
            switch (shape)
            {
                case ShipPartShape.SphericalCorner: return GetCornerMesh();
                default: return GetCubeMesh();
            }
        }

        private static Mesh GetCubeMesh()
        {
            if (_cubeMesh != null) return _cubeMesh;
            _cubeMesh = BuildCube();
            return _cubeMesh;
        }

        private static Mesh GetCornerMesh()
        {
            if (_cornerMesh != null) return _cornerMesh;
            _cornerMesh = BuildSphericalCorner(16);
            return _cornerMesh;
        }

        private static Mesh BuildCube()
        {
            var verts = new Vector3[24];
            var tris = new int[36];
            var norms = new Vector3[24];

            // 标准单位立方体 [-0.5, 0.5]，每个面 4 个独立顶点
            Vector3[] corners = {
                new(-0.5f, -0.5f, -0.5f), // 0
                new( 0.5f, -0.5f, -0.5f), // 1
                new( 0.5f,  0.5f, -0.5f), // 2
                new(-0.5f,  0.5f, -0.5f), // 3
                new(-0.5f, -0.5f,  0.5f), // 4
                new( 0.5f, -0.5f,  0.5f), // 5
                new( 0.5f,  0.5f,  0.5f), // 6
                new(-0.5f,  0.5f,  0.5f), // 7
            };

            // 6 个面: 前面(-Z), 右面(+X), 后面(+Z), 左面(-X), 上面(+Y), 下面(-Y)
            int[][] faceVerts = {
                new[] { 0, 1, 2, 3 }, // front
                new[] { 1, 5, 6, 2 }, // right
                new[] { 5, 4, 7, 6 }, // back
                new[] { 4, 0, 3, 7 }, // left
                new[] { 3, 2, 6, 7 }, // top
                new[] { 4, 5, 1, 0 }, // bottom
            };
            Vector3[] faceNorms = {
                new( 0,  0, -1),
                new( 1,  0,  0),
                new( 0,  0,  1),
                new(-1,  0,  0),
                new( 0,  1,  0),
                new( 0, -1,  0),
            };

            int vi = 0, ti = 0;
            for (int f = 0; f < 6; f++)
            {
                for (int c = 0; c < 4; c++)
                {
                    verts[vi] = corners[faceVerts[f][c]];
                    norms[vi] = faceNorms[f];
                    vi++;
                }
                // 两个三角形: 0-1-2, 0-2-3
                int baseIdx = f * 4;
                tris[ti++] = baseIdx;
                tris[ti++] = baseIdx + 1;
                tris[ti++] = baseIdx + 2;
                tris[ti++] = baseIdx;
                tris[ti++] = baseIdx + 2;
                tris[ti++] = baseIdx + 3;
            }

            var mesh = new Mesh { name = "ShipPart_Cube" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.normals = norms;
            return mesh;
        }

        /// <summary>
        /// 构建 1/8 角球体（球面八分之一）。
        /// 位于第一卦限 (x≥0, y≥0, z≥0)，半径 0.5。
        /// </summary>
        private static Mesh BuildSphericalCorner(int segments)
        {
            int res = segments; // 经线/纬线分段数
            int vertCount = (res + 1) * (res + 1);
            int triCount = res * res * 2;

            var verts = new Vector3[vertCount];
            var tris = new int[triCount * 3];
            var norms = new Vector3[vertCount];

            float radius = 0.5f;
            int idx = 0;

            for (int i = 0; i <= res; i++)
            {
                float theta = (float)i / res * (Mathf.PI * 0.5f); // 0 到 π/2 (纬度)
                for (int j = 0; j <= res; j++)
                {
                    float phi = (float)j / res * (Mathf.PI * 0.5f); // 0 到 π/2 (经度)
                    float x = radius * Mathf.Sin(theta) * Mathf.Cos(phi);
                    float y = radius * Mathf.Cos(theta);
                    float z = radius * Mathf.Sin(theta) * Mathf.Sin(phi);
                    verts[idx] = new Vector3(x, y, z);
                    norms[idx] = new Vector3(x, y, z).normalized;
                    idx++;
                }
            }

            idx = 0;
            for (int i = 0; i < res; i++)
            {
                for (int j = 0; j < res; j++)
                {
                    int a = i * (res + 1) + j;
                    int b = i * (res + 1) + j + 1;
                    int c = (i + 1) * (res + 1) + j;
                    int d = (i + 1) * (res + 1) + j + 1;

                    tris[idx++] = a;
                    tris[idx++] = c;
                    tris[idx++] = b;

                    tris[idx++] = b;
                    tris[idx++] = c;
                    tris[idx++] = d;
                }
            }

            var mesh = new Mesh { name = "ShipPart_SphericalCorner" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.normals = norms;
            return mesh;
        }

    }
}
