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
        private bool stellarShadersAreS3A;
        private PFViewStellarHeatCache? stellarHeatCache;
        private bool debugUseStellarHeatCache = PFViewStellarProbe.DefaultHeatCacheEnabled;
        private readonly Dictionary<int, Material> stellarHaloMaterials = new();
        private readonly List<(int Handle, Mesh Mesh, Matrix4x4 Matrix, Material Material)> stellarHaloDraws = new();

        internal bool DebugUseStellarHeatCache
        {
            get => debugUseStellarHeatCache;
            set
            {
                debugUseStellarHeatCache = value;
                if (!value && stellarHeatCache != null && stellarHeatCache.Texture == null)
                {
                    stellarHeatCache.Dispose();
                    stellarHeatCache = null;
                }
            }
        }

        internal string DebugStellarHeatCacheStatus
        {
            get
            {
                if (stellarShadersAreFast) return "PFV_StellarHeatCacheFast";
                if (stellarHeatCache?.Texture != null) return "PFV_StellarHeatCacheReady";
                if (stellarHeatCache?.Failed == true || (stellarShadersChecked && (stellarSurfaceShader == null || stellarHaloShader == null)))
                    return "PFV_StellarHeatCacheFailed";
                return "PFV_StellarHeatCachePreparing";
            }
        }

        /// <summary>
        /// 选取本窗口使用的恒星 shader，优先级：**Fast（玩家显式勾选）> S3-a（默认）> legacy**。
        /// S3-a = 区域跳过积分（保留 legacy 密集区采样网格，仅外层相位对齐跳过/粗积分，含 S1 剪枝）。
        /// 任一候选缺失时自动回退 legacy（不让恒星变普通材质）。
        /// 设置切换在重开 PFView 后生效（材质按 body 缓存，见 CleanupResources）。
        /// </summary>
        private void EnsureStellarShaders()
        {
            if (stellarShadersChecked) return;
            stellarShadersChecked = true;
            stellarShadersAreFast = PFViewStellarProbe.FastRendererEnabled;
            stellarShadersAreS3A = false;
            if (stellarShadersAreFast)
            {
                stellarSurfaceShader = Shader.Find("PF_StellarSurfaceFast");
                stellarHaloShader = Shader.Find("PF_StellarHaloFast");
                if (stellarSurfaceShader == null || stellarHaloShader == null)
                {
                    Log.Warning("[PFView·恒星材质] 新版恒星 shader 未加载，回退。");
                    stellarShadersAreFast = false;
                }
            }
            if (!stellarShadersAreFast)
            {
                stellarShadersAreS3A = PFViewStellarProbe.S3AEnabled;
                if (stellarShadersAreS3A)
                {
                    stellarSurfaceShader = Shader.Find("PF_StellarSurfaceS3A");
                    stellarHaloShader = Shader.Find("PF_StellarHaloS3A");
                    if (stellarSurfaceShader == null || stellarHaloShader == null)
                    {
                        Log.Warning("[PFView·恒星材质] S3-a 恒星 shader 未加载，回退 legacy。");
                        stellarShadersAreS3A = false;
                    }
                }
                if (!stellarShadersAreS3A)
                {
                    stellarSurfaceShader = Shader.Find("PF_StellarSurface");
                    stellarHaloShader = Shader.Find("PF_StellarHalo");
                }
            }
            if (stellarSurfaceShader == null || stellarHaloShader == null)
                Log.Warning("[PFView·恒星材质] 等离子体着色器未加载，本次窗口使用普通自发光材质。");
            else
                Log.Message($"[PFView·恒星材质] 使用{(stellarShadersAreFast ? "新版（性能优先）" : stellarShadersAreS3A ? "S3-a（区域跳过积分）" : "旧版体积")}恒星渲染。");
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
            var frame = PFViewStellarVisualCore.Frame(tick);
            Texture2D? heat = DebugUseStellarHeatCache && !stellarShadersAreFast
                ? (stellarHeatCache ??= new PFViewStellarHeatCache()).GetTexture()
                : null;
            Vector4 eyeValue = new(eye.x, eye.y, eye.z, heat != null ? 1f : 0f);
            float framePosition = heat != null ? PFViewStellarHeatCacheCore.FramePosition(frame.Phase) : PFViewStellarVisualCore.FlowSpeed;
            surface.SetVector("_StellarEye", eyeValue);
            ApplyStellarFrame(surface, in frame, heat, framePosition);
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
            ApplyStellarFrame(halo, in frame, heat, framePosition);
            halo.SetVector("_StellarParams", new Vector4(steps, 1f, .19f, PFViewStellarVisualCore.Extent));
            stellarHaloDraws.Add((body.bodyId, mesh, Matrix4x4.TRS(center, rotation, Vector3.one * proxyRadius * 2f), halo));
            return proxyRadius;
        }

        private static void ApplyStellarFrame(Material material, in PFViewStellarFrame frame, Texture2D? heat, float framePosition)
        {
            if (heat != null && material.GetTexture("_StellarHeatCache") != heat)
                material.SetTexture("_StellarHeatCache", heat);
            material.SetVector("_StellarTime", new Vector4(frame.Phase, framePosition, 2f, 2f));
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

        private void DestroyStellarVisualResourcesForBody(int handle)
        {
            stellarHaloDraws.RemoveAll(draw => draw.Handle == handle);
            DestroyOwnedResource(stellarHaloMaterials, handle);
        }

        private void DestroyStellarVisualResources()
        {
            stellarHaloDraws.Clear();
            foreach (var material in stellarHaloMaterials.Values)
                if (material != null) UnityEngine.Object.Destroy(material);
            stellarHaloMaterials.Clear();
            stellarSurfaceShader = null;
            stellarHaloShader = null;
            stellarShadersChecked = false;
            stellarShadersAreFast = false;
            stellarShadersAreS3A = false;
        }
    }
}
