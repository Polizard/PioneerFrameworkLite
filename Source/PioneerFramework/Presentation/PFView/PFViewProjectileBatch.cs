using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework.ProjectileVisuals
{
    internal sealed class PFViewProjectileBatch
    {
        private const int Capacity = 1024;
        private readonly record struct Streak(ProjectileStreakQuad Quad, Color32 Color);
        private readonly PriorityQueue<Streak, (bool Impact, float Depth, long Id)> streaks = new(Capacity);
        private readonly List<Vector3> vertices = new(Capacity * 4);
        private readonly List<Vector4> shapeUvs = new(Capacity * 4);
        private readonly List<Color32> colors = new(Capacity * 4);
        private readonly List<int> indices = new(Capacity * 6);
        private readonly Mesh?[] meshes = new Mesh?[2];
        private Material? material;
        private bool shaderMissing;

        internal void BeginFrame() => streaks.Clear();

        internal void Add(in ProjectileStreakQuad quad, Color color, long id, bool impact = false)
        {
            var priority = (impact, -quad.HeadLeft.z, -id);
            var item = new Streak(quad, color);
            if (streaks.Count < Capacity) streaks.Enqueue(item, priority);
            else streaks.EnqueueDequeue(item, priority);
        }

        internal void Draw(Camera camera, Vector3 cameraPosition, Quaternion rotation, int layer)
        {
            if (streaks.Count == 0 || shaderMissing) return;
            if (material == null)
            {
                var shader = Shader.Find("PF_ProjectileGlow");
                if (shader == null)
                {
                    shaderMissing = true;
                    Log.Warning("[PFL] PFView projectile glow shader is unavailable.");
                    return;
                }
                material = new Material(shader);
            }

            vertices.Clear();
            shapeUvs.Clear();
            colors.Clear();
            indices.Clear();
            foreach (var entry in streaks.UnorderedItems)
            {
                var streak = entry.Element;
                var quad = streak.Quad;
                float cap = quad.HalfLengthInRadii + 1f;
                int index = vertices.Count;
                AddVertex(quad.TailLeft, -1f, -cap);
                AddVertex(quad.TailRight, 1f, -cap);
                AddVertex(quad.HeadLeft, -1f, cap);
                AddVertex(quad.HeadRight, 1f, cap);
                indices.Add(index);
                indices.Add(index + 1);
                indices.Add(index + 2);
                indices.Add(index + 1);
                indices.Add(index + 3);
                indices.Add(index + 2);

                void AddVertex(Vector3 position, float u, float v)
                {
                    vertices.Add(position);
                    shapeUvs.Add(new Vector4(u, v, quad.HalfLengthInRadii, 0f));
                    colors.Add(streak.Color);
                }
            }

            // As in WorldQuadBatch, alternate meshes across frames so a submitted frame keeps its geometry.
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
            foreach (var mesh in meshes)
                if (mesh != null) Object.Destroy(mesh);
            if (material != null) Object.Destroy(material);
        }
    }
}
