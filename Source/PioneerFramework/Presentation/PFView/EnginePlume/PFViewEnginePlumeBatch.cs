// ⛔【NOSTEPANYMORE】表现层文件（PFView 引擎喷焰批）。只读后端状态，不写任何业务字段。
//    若下一步要改 Base/** 等后端文件，按 BACKEND-LOCK.md 停机请示，不得顺手改。
// ─────────────────────────────────────────────────────────────────────────────
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **PFView 引擎喷焰批**（2026-11-08）：相机空间 CPU 拼 mesh，一次 DrawMesh 画完全部喷焰。
    ///
    /// 结构照抄 PFViewProjectileBatch（同一套"顶点在相机空间、矩阵 = TRS(相机位姿, 1)"的做法）：
    /// · 为什么用相机空间：所有片都正对相机，不需要逐片矩阵；一次提交、一个材质。
    /// · 为什么加性：满足交换律 ⇒ 不需要按深度排序；depthWrite=false 不污染深度。
    /// · 材质直接用现成的 PF_ProjectileGlow（Additive / depthTest / 不写深度 / cull None），
    ///   喷焰 = 长胶囊、喷口辉光 = 半长为 0 的胶囊（frag 里"中心线缩到 0 就变圆点"）。
    ///
    /// 纪律：本类不碰 WorldQuadBatch 的全局批状态，也不改任何后端文件。
    /// </summary>
    internal sealed class PFViewEnginePlumeBatch
    {
        private const int MaxQuads = PFViewEnginePlumeProbe.MaxQuads;
        private readonly List<Vector3> vertices = new(MaxQuads * 4);
        private readonly List<Vector4> shapeUvs = new(MaxQuads * 4);
        private readonly List<Color32> colors = new(MaxQuads * 4);
        private readonly List<int> indices = new(MaxQuads * 6);
        private readonly Mesh?[] meshes = new Mesh?[2];
        private Material? material;
        private bool shaderMissing;
        private int quadCount;

        internal int QuadCount => quadCount;

        internal bool ShaderMissing => shaderMissing;

        internal void BeginFrame()
        {
            vertices.Clear();
            shapeUvs.Clear();
            colors.Clear();
            indices.Clear();
            quadCount = 0;
        }

        /// <summary>
        /// 加一片喷焰（眼空间）：headEye = 喷口，dirEye = 排气方向，长度/半宽都是视图单位。
        /// 头部亮、尾部暗由顶点色给出；尾部形状衰减由 PF_ProjectileGlow 的 tailFade 给出。
        /// </summary>
        internal void AddPlume(Vector3 headEye, Vector3 dirEye, float lengthEye,
            float headHalfWidthEye, float tailHalfWidthEye, Color headColor, Color tailColor, float near)
        {
            if (quadCount >= MaxQuads) return;
            if (headEye.z <= near || lengthEye <= 1e-5f) return;
            if (dirEye.sqrMagnitude < 1e-12f) return;

            Vector3 dir = dirEye.normalized;
            Vector3 tailEye = headEye + dir * lengthEye;
            if (tailEye.z <= near)
            {
                // 尾端穿过近裁面：沿轴线裁到 near 之后，避免整条消失或糊屏。
                float denom = tailEye.z - headEye.z;
                float t = denom > 1e-6f ? (near + 0.01f - headEye.z) / denom : 0f;
                tailEye = Vector3.Lerp(headEye, tailEye, Mathf.Clamp01(t));
            }

            // 胶囊两端各外扩一个半宽：shader 的 u/v 都以"半宽"为单位，外扩后端点才是圆帽，
            // 与 PFViewProjectileGeometry 的胶囊口径一致（只是这里在世界空间做）。
            Vector3 headEnd = headEye + dir * headHalfWidthEye;
            Vector3 tailEnd = tailEye - dir * tailHalfWidthEye;
            if (headEnd.z <= near) headEnd = headEye;
            if (tailEnd.z <= near) tailEnd = tailEye;
            float shapedLength = Vector3.Distance(headEnd, tailEnd);
            if (shapedLength <= 1e-5f) return;

            Vector3 view = headEye.sqrMagnitude > 1e-8f ? headEye.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(dir, view);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.Cross(dir, Vector3.up);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();

            float maxHalfWidth = Mathf.Max(headHalfWidthEye, tailHalfWidthEye);
            float halfLengthRadii = Mathf.Max(0.001f, shapedLength / Mathf.Max(0.001f, maxHalfWidth * 2f));
            AddQuad(tailEnd - right * tailHalfWidthEye, tailEnd + right * tailHalfWidthEye,
                headEnd - right * headHalfWidthEye, headEnd + right * headHalfWidthEye,
                halfLengthRadii, tailColor, headColor);
        }

        /// <summary>加一个正对相机的软圆点（喷口辉光；眼空间 XY 平面）。</summary>
        internal void AddDot(Vector3 centerEye, float radiusEye, Color color, float near)
        {
            if (quadCount >= MaxQuads) return;
            if (centerEye.z <= near || radiusEye <= 1e-5f) return;
            Vector3 r = new(radiusEye, 0f, 0f);
            Vector3 u = new(0f, radiusEye, 0f);
            AddQuad(centerEye - r - u, centerEye + r - u, centerEye - r + u, centerEye + r + u,
                0f, color, color);
        }

        private void AddQuad(Vector3 tailLeft, Vector3 tailRight, Vector3 headLeft, Vector3 headRight,
            float halfLengthRadii, Color tailColor, Color headColor)
        {
            if (quadCount >= MaxQuads) return;
            float cap = halfLengthRadii + 1f;
            int index = vertices.Count;

            AddVertex(tailLeft, -1f, -cap, tailColor);
            AddVertex(tailRight, 1f, -cap, tailColor);
            AddVertex(headLeft, -1f, cap, headColor);
            AddVertex(headRight, 1f, cap, headColor);

            indices.Add(index);
            indices.Add(index + 1);
            indices.Add(index + 2);
            indices.Add(index + 1);
            indices.Add(index + 3);
            indices.Add(index + 2);
            quadCount++;

            void AddVertex(Vector3 position, float u, float v, Color color)
            {
                vertices.Add(position);
                shapeUvs.Add(new Vector4(u, v, halfLengthRadii, 0f));
                colors.Add(color);
            }
        }

        internal void Draw(Camera camera, Vector3 cameraPosition, Quaternion rotation, int layer)
        {
            if (quadCount == 0) return;
            if (shaderMissing) return;
            if (material == null)
            {
                Shader? shader = Shader.Find("PF_ProjectileGlow");
                if (shader == null)
                {
                    shaderMissing = true;
                    Log.Warning("[PFL] PFView engine plume shader PF_ProjectileGlow is unavailable; engine plumes are skipped.");
                    return;
                }
                material = new Material(shader);
            }

            // 与 PFViewProjectileBatch 相同的双 mesh 交替：提交帧的几何不会被下一帧改写。
            Mesh mesh = meshes[RealTime.frameCount & 1] ??= new Mesh();
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, shapeUvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            Graphics.DrawMesh(mesh, Matrix4x4.TRS(cameraPosition, rotation, Vector3.one), material, layer, camera);
        }

        internal void Release()
        {
            foreach (Mesh? mesh in meshes)
                if (mesh != null) Object.Destroy(mesh);
            meshes[0] = null;
            meshes[1] = null;
            if (material != null) Object.Destroy(material);
            material = null;
            shaderMissing = false;
            quadCount = 0;
            BeginFrame();
        }
    }
}