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
        private bool stellarShadersAreFast;
        private readonly Dictionary<int, Material> stellarHaloMaterials = new();
        private readonly List<(Mesh Mesh, Matrix4x4 Matrix, Material Material)> stellarHaloDraws = new();

        /// <summary>
        /// 选取本窗口使用的恒星 shader：**默认 legacy（旧视觉）**；只有玩家在 Mod 设置勾选
        /// "使用新版恒星渲染" 时才用 Fast 版。Fast 版缺失时自动回退 legacy（不让恒星变普通材质）。
        /// 设置切换在重开 PFView 后生效（材质按 body 缓存，见 CleanupResources）。
        /// </summary>
        private void EnsureStellarShaders()
        {
            if (stellarShadersChecked) return;
            stellarShadersChecked = true;
            stellarShadersAreFast = PFViewStellarProbe.FastRendererEnabled;
            if (stellarShadersAreFast)
            {
                stellarSurfaceShader = Shader.Find("PF_StellarSurfaceFast");
                stellarHaloShader = Shader.Find("PF_StellarHaloFast");
                if (stellarSurfaceShader == null || stellarHaloShader == null)
                {
                    Log.Warning("[PFView·恒星材质] 新版恒星 shader 未加载，回退旧版视觉。");
                    stellarShadersAreFast = false;
                }
            }
            if (!stellarShadersAreFast)
            {
                stellarSurfaceShader = Shader.Find("PF_StellarSurface");
                stellarHaloShader = Shader.Find("PF_StellarHalo");
            }
            if (stellarSurfaceShader == null || stellarHaloShader == null)
                Log.Warning("[PFView·恒星材质] 等离子体着色器未加载，本次窗口使用普通自发光材质。");
            else
                Log.Message($"[PFView·恒星材质] 使用{(stellarShadersAreFast ? "新版（性能优先）" : "旧版（默认视觉）")}恒星渲染。");
        }

        private Material CreateStellarSurfaceMaterial(CelestialBodyInfo body)
        {
            EnsureStellarShaders();
            if (stellarSurfaceShader != null && stellarHaloShader != null)
                return new Material(stellarSurfaceShader) { name = "PF_StellarSurface_" + body.bodyId,
                    renderQueue = PFViewLayers.PlanetAndShips };

            var fallback = new Material(renderMat) { mainTexture = Texture2D.whiteTexture,
                renderQueue = PFViewLayers.PlanetAndShips };
            fallback.SetColor("_Emission", body.baseSphereColor.a > 0 ? body.baseSphereColor : CelestialBodyManager.GetBodyColor(body));
            return fallback;
        }

        /// <summary>只更新材质并返回视觉包围球半径；中心、姿态和真实半径均来自普通天体流程。</summary>
        private float PrepareStellarVisual(CelestialBodyInfo body, Material surface, Vector3 center, float radius,
            Quaternion rotation, Mesh mesh, double tick)
        {
            if (stellarSurfaceShader == null || stellarHaloShader == null || surface.shader != stellarSurfaceShader)
                return radius;

            int steps = PFViewStellarProbe.ResolveStepBudget();
            Vector3 eye = PFViewStellarVisualCore.Eye(GetCameraViewPos(), center, rotation, radius);
            Vector4 eyeValue = new(eye.x, eye.y, eye.z, 0f);
            var frame = PFViewStellarVisualCore.Frame(tick);
            surface.SetVector("_StellarEye", eyeValue);
            ApplyStellarFrame(surface, in frame);
            surface.SetVector("_StellarParams", new Vector4(steps, 0f, .19f, PFViewStellarVisualCore.Extent));

            float proxyRadius = radius * PFViewStellarVisualCore.Extent;
            // P0 探针：halo pass 可单独关掉做 A/B（surface pass 的绘制在 Scene.cs 侧由
            // `PFViewStellarProbe.SurfacePass` 控制，两者独立）。默认开 = 现状。
            if (!PFViewStellarProbe.HaloPass) return proxyRadius;

            if (!stellarHaloMaterials.TryGetValue(body.bodyId, out var halo))
            {
                halo = new Material(stellarHaloShader) { name = "PF_StellarHalo_" + body.bodyId,
                    renderQueue = PFViewLayers.StellarHalo };
                stellarHaloMaterials.Add(body.bodyId, halo);
            }
            halo.SetVector("_StellarEye", eyeValue);
            ApplyStellarFrame(halo, in frame);
            halo.SetVector("_StellarParams", new Vector4(steps, 1f, .19f, PFViewStellarVisualCore.Extent));
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
            stellarShadersAreFast = false;
        }
    }
}
