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

        // S3-b（2026-11-20）：星体半分辨率离屏。composite 材质把星体 RT 合成回主 RT；
        // 星体自身 draw 排 StarLayer（stellarCamera 收），composite draw 排 SceneLayer（主相机收）。
        private Shader? stellarCompositeSurfaceShader;
        private Shader? stellarCompositeHaloShader;
        private bool stellarOffscreenActive;
        private readonly Dictionary<int, Material> stellarCompositeSurfaceMaterials = new();
        private readonly Dictionary<int, Material> stellarCompositeHaloMaterials = new();
        private readonly List<(int Handle, Mesh Mesh, Matrix4x4 Matrix, Material Material)> stellarStarHaloDraws = new();
        private readonly List<(int Handle, Mesh Mesh, Matrix4x4 Matrix, Material Material)> stellarCompositeHaloDraws = new();

        /// <summary>S3-b 是否真正启用（开关 + composite shader + 星体 RT 都就绪）；绘制/渲染两侧共用。</summary>
        internal bool StellarOffscreenActive => stellarOffscreenActive && stellarRenderTexture != null;

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
            SelectStellarShaders(out stellarSurfaceShader, out stellarHaloShader, out stellarShadersAreFast, out stellarShadersAreS3A);
            stellarOffscreenActive = false;
            if (PFViewStellarProbe.S3BEnabled)
            {
                stellarCompositeSurfaceShader = Shader.Find("PF_StellarSurfaceComposite");
                stellarCompositeHaloShader = Shader.Find("PF_StellarHaloComposite");
                if (stellarCompositeSurfaceShader == null || stellarCompositeHaloShader == null)
                    Log.Warning("[PFView·恒星材质] S3-b composite shader 未加载，回退普通恒星路径。");
                else if (stellarRenderTexture == null)
                    Log.Warning("[PFView·恒星材质] S3-b 已开但星体 RT 未就绪，回退普通恒星路径。");
                else
                    stellarOffscreenActive = true;
            }
            if (stellarSurfaceShader == null || stellarHaloShader == null)
                return;
            Log.Message($"[PFView·恒星材质] 使用{(stellarShadersAreFast ? "新版（性能优先）" : stellarShadersAreS3A ? "S3-a（区域跳过积分）" : "旧版体积")}恒星渲染{(stellarOffscreenActive ? "；S3-b 半分辨率离屏" : "")}。");
        }

        internal static void PrewarmStellarPipelines() => PFViewStellarPipelineWarmup.Run(sphereMeshLOD0, SceneLayer, StarLayer);

        internal static void SelectStellarShaders(out Shader? stellarSurfaceShader, out Shader? stellarHaloShader,
            out bool stellarShadersAreFast, out bool stellarShadersAreS3A)
        {
            stellarSurfaceShader = stellarHaloShader = null;
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

            // P0 探针：halo pass 可单独关掉做 A/B（surface pass 的绘制在 Scene.cs 侧由
            // `PFViewStellarProbe.SurfacePass` 控制，两者独立）。默认开 = 现状。
            Material? halo = null;
            if (PFViewStellarProbe.HaloPass && !stellarHaloMaterials.TryGetValue(body.bodyId, out halo))
            {
                halo = new Material(stellarHaloShader) { name = "PF_StellarHalo_" + body.bodyId,
                    renderQueue = PFViewLayers.StellarHalo };
                stellarHaloMaterials.Add(body.bodyId, halo);
            }
            float proxyRadius = ApplyStellarVisual(surface, halo, GetCameraViewPos(), center, radius, rotation, tick,
                stellarShadersAreFast, DebugUseStellarHeatCache, ref stellarHeatCache);
            Matrix4x4 matrix = Matrix4x4.TRS(center, rotation, Vector3.one * proxyRadius * 2f);

            if (stellarOffscreenActive && stellarRenderTexture != null
                && stellarCompositeSurfaceShader != null && stellarCompositeHaloShader != null)
            {
                // 星体自身进 StarLayer（stellarCamera 渲染到半分辨率 RT）；composite 进 SceneLayer。
                var frame = PFViewStellarVisualCore.Frame(tick);
                Vector3 eye = PFViewStellarVisualCore.Eye(GetCameraViewPos(), center, rotation, radius);
                var eyeValue = new Vector4(eye.x, eye.y, eye.z, 0f);
                int steps = PFViewStellarProbe.ResolveStepBudget();
                // P0 probe: SurfacePass=false 时连星体离屏 surface 也不提交（halo 独立）。
                if (PFViewStellarProbe.SurfacePass)
                {
                    Graphics.DrawMesh(mesh, matrix, surface, StarLayer, null, 0);

                    Material compSurface = GetOrCreateCompositeSurfaceMaterial(body.bodyId);
                    ApplyStellarFrame(compSurface, in frame, null, PFViewStellarVisualCore.FlowSpeed, eyeValue, steps, 0f);
                    Graphics.DrawMesh(mesh, matrix, compSurface, SceneLayer, null, 0);
                }

                if (halo != null)
                {
                    stellarStarHaloDraws.Add((body.bodyId, mesh, matrix, halo));
                    Material compHalo = GetOrCreateCompositeHaloMaterial(body.bodyId);
                    ApplyStellarFrame(compHalo, in frame, null, PFViewStellarVisualCore.FlowSpeed, eyeValue, steps, 1f);
                    stellarCompositeHaloDraws.Add((body.bodyId, mesh, matrix, compHalo));
                }
                return proxyRadius;
            }

            if (halo != null)
                stellarHaloDraws.Add((body.bodyId, mesh, matrix, halo));
            return proxyRadius;
        }

        internal static float ApplyStellarVisual(Material surface, Material? halo, Vector3 cameraPosition,
            Vector3 center, float radius, Quaternion rotation, double tick, bool fast, bool useHeatCache,
            ref PFViewStellarHeatCache? heatCache)
        {
            int steps = PFViewStellarProbe.ResolveStepBudget();
            Vector3 eye = PFViewStellarVisualCore.Eye(cameraPosition, center, rotation, radius);
            var frame = PFViewStellarVisualCore.Frame(tick);
            Texture2D? heat = useHeatCache && !fast ? (heatCache ??= new PFViewStellarHeatCache()).GetTexture() : null;
            Vector4 eyeValue = new(eye.x, eye.y, eye.z, heat != null ? 1f : 0f);
            float framePosition = heat != null ? PFViewStellarHeatCacheCore.FramePosition(frame.Phase) : PFViewStellarVisualCore.FlowSpeed;
            ApplyStellarFrame(surface, in frame, heat, framePosition, eyeValue, steps, 0f);
            if (halo != null) ApplyStellarFrame(halo, in frame, heat, framePosition, eyeValue, steps, 1f);
            return radius * PFViewStellarVisualCore.Extent;
        }

        private static void ApplyStellarFrame(Material material, in PFViewStellarFrame frame, Texture2D? heat,
            float framePosition, Vector4 eye, int steps, float halo)
        {
            if (heat != null && material.GetTexture("_StellarHeatCache") != heat)
                material.SetTexture("_StellarHeatCache", heat);
            material.SetVector("_StellarEye", eye);
            material.SetVector("_StellarParams", new Vector4(steps, halo, .19f, PFViewStellarVisualCore.Extent));
            material.SetVector("_StellarTime", new Vector4(frame.Phase, framePosition, 2f, 2f));
            material.SetVector("_StellarDrift", frame.Drift);
            material.SetVector("_StellarArchHeight0", frame.ArchHeight0);
            material.SetVector("_StellarArchHeight1", frame.ArchHeight1);
        }

        private void DrawStellarHalos()
        {
            foreach (var draw in stellarHaloDraws)
                Graphics.DrawMesh(draw.Mesh, draw.Matrix, draw.Material, SceneLayer, renderCamera);
            // S3-b：星体 halo 进 StarLayer（stellarCamera 半分辨率 RT）……
            foreach (var draw in stellarStarHaloDraws)
                Graphics.DrawMesh(draw.Mesh, draw.Matrix, draw.Material, StarLayer, null, 0);
            // ……再把星体 RT 的 halo 以 composite 混合回主 RT（主相机，queue 2900）。
            foreach (var draw in stellarCompositeHaloDraws)
                Graphics.DrawMesh(draw.Mesh, draw.Matrix, draw.Material, SceneLayer, null, 0);
            stellarHaloDraws.Clear();
            stellarStarHaloDraws.Clear();
            stellarCompositeHaloDraws.Clear();
        }

        private Material GetOrCreateCompositeSurfaceMaterial(int bodyId)
        {
            if (stellarCompositeSurfaceMaterials.TryGetValue(bodyId, out var existing)) return existing;
            var material = new Material(stellarCompositeSurfaceShader) { name = "PF_StellarCompositeSurface_" + bodyId,
                renderQueue = PFViewLayers.PlanetAndShips };
            if (stellarRenderTexture != null) material.SetTexture("_StellarStarTex", stellarRenderTexture);
            stellarCompositeSurfaceMaterials.Add(bodyId, material);
            return material;
        }

        private Material GetOrCreateCompositeHaloMaterial(int bodyId)
        {
            if (stellarCompositeHaloMaterials.TryGetValue(bodyId, out var existing)) return existing;
            var material = new Material(stellarCompositeHaloShader) { name = "PF_StellarCompositeHalo_" + bodyId,
                renderQueue = PFViewLayers.StellarHalo };
            if (stellarRenderTexture != null) material.SetTexture("_StellarStarTex", stellarRenderTexture);
            stellarCompositeHaloMaterials.Add(bodyId, material);
            return material;
        }

        /// <summary>星体 RT 重建后把新纹理绑到已缓存的 composite 材质（窗口尺寸变化时）。</summary>
        private void UpdateStellarCompositeTextures()
        {
            if (stellarRenderTexture == null) return;
            foreach (var material in stellarCompositeSurfaceMaterials.Values)
                if (material != null) material.SetTexture("_StellarStarTex", stellarRenderTexture);
            foreach (var material in stellarCompositeHaloMaterials.Values)
                if (material != null) material.SetTexture("_StellarStarTex", stellarRenderTexture);
        }

        private void DestroyStellarVisualResourcesForBody(int handle)
        {
            stellarHaloDraws.RemoveAll(draw => draw.Handle == handle);
            stellarStarHaloDraws.RemoveAll(draw => draw.Handle == handle);
            stellarCompositeHaloDraws.RemoveAll(draw => draw.Handle == handle);
            DestroyOwnedResource(stellarHaloMaterials, handle);
            DestroyOwnedResource(stellarCompositeSurfaceMaterials, handle);
            DestroyOwnedResource(stellarCompositeHaloMaterials, handle);
        }

        private void DestroyStellarVisualResources()
        {
            stellarHaloDraws.Clear();
            stellarStarHaloDraws.Clear();
            stellarCompositeHaloDraws.Clear();
            foreach (var material in stellarHaloMaterials.Values)
                if (material != null) UnityEngine.Object.Destroy(material);
            stellarHaloMaterials.Clear();
            foreach (var material in stellarCompositeSurfaceMaterials.Values)
                if (material != null) UnityEngine.Object.Destroy(material);
            stellarCompositeSurfaceMaterials.Clear();
            foreach (var material in stellarCompositeHaloMaterials.Values)
                if (material != null) UnityEngine.Object.Destroy(material);
            stellarCompositeHaloMaterials.Clear();
            stellarSurfaceShader = null;
            stellarHaloShader = null;
            stellarCompositeSurfaceShader = null;
            stellarCompositeHaloShader = null;
            stellarOffscreenActive = false;
            stellarShadersChecked = false;
            stellarShadersAreFast = false;
            stellarShadersAreS3A = false;
        }
    }
}
