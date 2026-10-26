using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private Shader? stellarSurfaceShader;
        private Shader? stellarHaloShader;
        private bool stellarShadersChecked;
        private readonly Dictionary<int, Material> stellarHaloMaterials = new();
        private readonly List<(Mesh Mesh, Matrix4x4 Matrix, Material Material)> stellarHaloDraws = new();

        private Material CreateStellarSurfaceMaterial(CelestialBodyDef body)
        {
            if (!stellarShadersChecked)
            {
                stellarShadersChecked = true;
                stellarSurfaceShader = Shader.Find("PF_StellarSurface");
                stellarHaloShader = Shader.Find("PF_StellarHalo");
                if (stellarSurfaceShader == null || stellarHaloShader == null)
                    Log.Warning("[PFView·恒星材质] 等离子体着色器未加载，本次窗口使用普通自发光材质。");
            }
            if (stellarSurfaceShader != null && stellarHaloShader != null)
                return new Material(stellarSurfaceShader) { name = "PF_StellarSurface_" + body.bodyId,
                    renderQueue = PFViewLayers.PlanetAndShips };

            var fallback = new Material(renderMat) { mainTexture = Texture2D.whiteTexture,
                renderQueue = PFViewLayers.PlanetAndShips };
            fallback.SetColor("_Emission", CelestialBodyManager.GetBodyColor(body));
            return fallback;
        }

        /// <summary>只更新材质并返回视觉包围球半径；中心、姿态和真实半径均来自普通天体流程。</summary>
        private float PrepareStellarVisual(CelestialBodyDef body, Material surface, Vector3 center, float radius,
            Quaternion rotation, Mesh mesh, double tick)
        {
            if (stellarSurfaceShader == null || stellarHaloShader == null || surface.shader != stellarSurfaceShader)
                return radius;

            Vector3 eye = PFViewStellarVisualCore.Eye(GetCameraViewPos(), center, rotation, radius);
            Vector4 eyeValue = new(eye.x, eye.y, eye.z, 0f);
            var frame = PFViewStellarVisualCore.Frame(tick);
            surface.SetVector("_StellarEye", eyeValue);
            ApplyStellarFrame(surface, in frame);
            surface.SetVector("_StellarParams", new Vector4(PFViewStellarVisualCore.SurfaceSteps, 0f, .19f, PFViewStellarVisualCore.Extent));

            if (!stellarHaloMaterials.TryGetValue(body.bodyId, out var halo))
            {
                halo = new Material(stellarHaloShader) { name = "PF_StellarHalo_" + body.bodyId,
                    renderQueue = PFViewLayers.StellarHalo };
                stellarHaloMaterials.Add(body.bodyId, halo);
            }
            halo.SetVector("_StellarEye", eyeValue);
            ApplyStellarFrame(halo, in frame);
            halo.SetVector("_StellarParams", new Vector4(PFViewStellarVisualCore.HaloSteps, 1f, .19f, PFViewStellarVisualCore.Extent));
            float proxyRadius = radius * PFViewStellarVisualCore.Extent;
            stellarHaloDraws.Add((mesh, Matrix4x4.TRS(center, rotation, Vector3.one * proxyRadius * 2f), halo));
            return proxyRadius;
        }

        private static void ApplyStellarFrame(Material material, in PFViewStellarFrame frame)
        {
            material.SetVector("_StellarTime", new Vector4(frame.Phase, PFViewStellarVisualCore.FlowSpeed, 2f, 2f));
            material.SetVector("_StellarDrift", frame.Drift);
            material.SetVector("_StellarArchHeight0", frame.ArchHeight0);
            material.SetVector("_StellarArchHeight1", frame.ArchHeight1);
        }

        private void DrawStellarHalos()
        {
            foreach (var draw in stellarHaloDraws)
                Graphics.DrawMesh(draw.Mesh, draw.Matrix, draw.Material, SceneLayer, renderCamera);
            stellarHaloDraws.Clear();
        }

        private void DestroyStellarVisualResources()
        {
            stellarHaloDraws.Clear();
            foreach (var material in stellarHaloMaterials.Values) UnityEngine.Object.Destroy(material);
            stellarHaloMaterials.Clear();
            stellarSurfaceShader = null;
            stellarHaloShader = null;
            stellarShadersChecked = false;
        }
    }
}
