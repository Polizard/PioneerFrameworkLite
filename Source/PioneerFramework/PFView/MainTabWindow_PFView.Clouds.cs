using UnityEngine;
using Verse;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private Material? cloudMaterial;
        private bool cloudResourcesUnavailable;
        private static Mesh? cloudSphereMesh;
        private readonly Dictionary<CelestialViewCacheKey, Material> celestialCloudMaterials = new();
        /// <summary>低层云材质（与主层同 shader/贴图，参数在绘制时按层覆盖）。</summary>
        private readonly Dictionary<CelestialViewCacheKey, Material> celestialCloudDetailMaterials = new();
        private Material? cloudDetailMaterial;

        private static Mesh GetCloudSphereMesh()
        {
            if (cloudSphereMesh != null) return cloudSphereMesh;
            PFViewClouds.BuildShellGeometry(out var directions, out var triangles);
            var vertices = new Vector3[directions.Length];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = (Vector3)directions[i];
            cloudSphereMesh = new Mesh { name = "PFView_CloudSphere", vertices = vertices, triangles = triangles };
            cloudSphereMesh.RecalculateBounds();
            return cloudSphereMesh;
        }

        /// <summary>
        /// 地球独立云层（2026-10-19）：复用原版云图/噪声/晨昏渐变，跟随地表姿态与远景等比挪位。
        /// 相机低于 10 km 时隐藏；材质只覆盖云，现有大气边缘和低空天空照旧承担大气表现。
        /// </summary>
        private void DrawCloudLayer(CelestialBodyInfo body, Vector3 renderPos, float renderR,
            Quaternion selfRot, double tick, double cameraAltitudeMeters, Vector3 toSun)
        {
            var visual = celestialRenderRuntime != null ? GetCelestialRenderVisual(body.bodyId, CelestialVisualKind.Clouds) : null;
            // 旧路径（无 Celestial runtime）只有地球云层：门槛是**物理大气存在性**（`atmosphereThickness` = 大气顶），
            // 与视觉壳厚度无关 —— 云层高度/尺寸走 `PFViewClouds` 的 binding，不读壳厚度。
            if (renderCamera == null || (celestialRenderRuntime != null ? visual == null : body.defName != "Earth" || body.atmosphereThickness <= 0.0)) return;
            var profile = visual?.Profile;
            var binding = PFViewVisualBindings.Clouds(profile, tick, cameraAltitudeMeters);
            double altitude = binding.AltitudeMeters;
            float opacity = binding.Parameters.x * binding.Parameters.w * binding.Tint.w;
            if (opacity <= 0.001f) return;
            Material? mat = profile == null ? GetCloudMaterial() : GetCelestialCloudMaterial(celestialRenderSnapshots[body.bodyId].CacheKey, profile);
            if (mat == null) return;

            Vector3 localSun = Quaternion.Inverse(selfRot) * toSun;
            Vector3 cameraFromBody = renderCamera.transform.position - renderPos;
            mat.SetVector("_CloudToSun", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            mat.SetVector("_CloudSunLocal", new Vector4(localSun.x, localSun.y, localSun.z, 0f));
            mat.SetVector("_CloudCameraFromBody", new Vector4(cameraFromBody.x, cameraFromBody.y, cameraFromBody.z, 0f));
            mat.SetVector("_CloudParams", binding.Parameters);
            mat.SetVector("_CloudTint", binding.Tint);

            // ── 双层云（A 方案 P1-5，参考「天体运行」的双云带）──
            // 先画**低层**（离相机更远），再画主层：两层同为透明队列，提交顺序即叠放顺序。
            // 低层用同一张图、不同相位与更低的覆盖率，只表达"云有厚度"，不引入第二份资产/字段。
            Material? detail = celestialRenderRuntime != null
                ? GetCelestialCloudDetailMaterial(celestialRenderSnapshots[body.bodyId].CacheKey, profile)
                : GetCloudDetailMaterial();
            DrawCloudShell(detail, renderPos, renderR, selfRot, body.radius, altitude * CloudDetailAltitudeFactor,
                toSun, cameraFromBody, binding, detailLayer: true);
            DrawCloudShell(mat, renderPos, renderR, selfRot, body.radius, altitude,
                toSun, cameraFromBody, binding, detailLayer: false);
        }

        /// <summary>低层云高度 = 主层 × 此系数（0.62 ⇒ 10 km 主云 + 6.2 km 低云）。</summary>
        private const double CloudDetailAltitudeFactor = 0.62;

        /// <summary>画一层云壳（主层/低层共用）：半径、near 收紧、参数都按层算。</summary>
        private void DrawCloudShell(Material? mat, Vector3 renderPos, float renderR, Quaternion selfRot, double bodyRadius,
            double altitudeMeters, Vector3 toSun, Vector3 cameraFromBody, PFViewVisualBindings.CloudPlan binding, bool detailLayer)
        {
            if (mat == null) return;
            Vector3 localSun = Quaternion.Inverse(selfRot) * toSun;
            mat.SetVector("_CloudToSun", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            mat.SetVector("_CloudSunLocal", new Vector4(localSun.x, localSun.y, localSun.z, 0f));
            mat.SetVector("_CloudCameraFromBody", new Vector4(cameraFromBody.x, cameraFromBody.y, cameraFromBody.z, 0f));
            Vector4 parameters = binding.Parameters;
            Vector4 tint = binding.Tint;
            if (detailLayer)
            {
                // 低层：透明度 ×0.55、漂移相位偏移 0.37 圈、噪声相位 ×1.7、覆盖率 ×0.8、色调压暗一档。
                parameters = new Vector4(parameters.x * 0.55f,
                    (parameters.y + 0.37f) % 1f, (parameters.z * 1.7f) % 1f, parameters.w * 0.8f);
                tint = new Vector4(tint.x * 0.85f, tint.y * 0.85f, tint.z * 0.9f, tint.w);
            }
            mat.SetVector("_CloudParams", parameters);
            mat.SetVector("_CloudTint", tint);

            float radius = renderR * (float)PFViewClouds.RadiusFactor(bodyRadius, altitudeMeters);
            // 低空看向云层时，仅按地表余量算 near 会切掉云壳近侧；取实际渲染尺度的云壳余量。
            float shellGapView = cameraFromBody.magnitude - radius;
            renderCamera!.nearClipPlane = (float)PFViewClouds.LimitNearClip(renderCamera.nearClipPlane, shellGapView, NearClip);
            var matrix = Matrix4x4.TRS(renderPos, selfRot, Vector3.one * (radius * 2f));
            Graphics.DrawMesh(GetCloudSphereMesh(), matrix, mat, SceneLayer);
        }

        private Material? GetCloudMaterial()
        {
            if (cloudMaterial != null) return cloudMaterial;
            if (cloudResourcesUnavailable) return null;

            Shader shader = Shader.Find("PF_PlanetClouds");
            Texture2D clouds = ContentFinder<Texture2D>.Get("World/CloudMap", false);
            Texture2D noise = ContentFinder<Texture2D>.Get("Other/Perlin", false);
            Texture2D sunset = ContentFinder<Texture2D>.Get("World/SunsetGradient", false);
            if (shader == null || clouds == null || noise == null || sunset == null)
            {
                cloudResourcesUnavailable = true;
                Log.Warning("[PFView·云层] 着色器或原版云层贴图缺失，本次窗口会话停用云层。");
                return null;
            }

            cloudMaterial = new Material(shader) { renderQueue = PFViewLayers.Clouds };
            cloudMaterial.SetTexture("_CloudTex", clouds);
            cloudMaterial.SetTexture("_NoiseTex", noise);
            cloudMaterial.SetTexture("_SunsetColorRamp", sunset);
            Log.Message($"[PFView·云层] shader={shader.name} · 地球云高 10 km · 10–15 km 相机高度淡出 · 原版云图/噪声/晨昏渐变");
            return cloudMaterial;
        }
        private Material? GetCelestialCloudMaterial(CelestialViewCacheKey key, CelestialVisualProfile profile)
        {
            if (celestialCloudMaterials.TryGetValue(key, out var material)) return material;
            var template = GetCloudMaterial();
            if (template == null) return null;
            var texture = profile.TexturePath == null ? null : ContentFinder<Texture2D>.Get(profile.TexturePath, false);
            if (profile.TexturePath != null && texture == null) return null;
            material = new Material(template);
            if (texture != null) material.SetTexture("_CloudTex", texture);
            celestialCloudMaterials.Add(key, material);
            return material;
        }

        /// <summary>旧路径（无 Celestial runtime）的地球低层云材质。</summary>
        private Material? GetCloudDetailMaterial()
        {
            if (cloudDetailMaterial != null) return cloudDetailMaterial;
            var template = GetCloudMaterial();
            if (template == null) return null;
            cloudDetailMaterial = new Material(template) { renderQueue = PFViewLayers.Clouds };
            return cloudDetailMaterial;
        }

        /// <summary>实例路径的低层云材质：克隆本实例的主层材质（贴图同源，参数按层覆盖）。</summary>
        private Material? GetCelestialCloudDetailMaterial(CelestialViewCacheKey key, CelestialVisualProfile? profile)
        {
            if (celestialCloudDetailMaterials.TryGetValue(key, out var material)) return material;
            var baseMaterial = GetCelestialCloudMaterial(key, profile!);
            if (baseMaterial == null) return null;
            material = new Material(baseMaterial) { renderQueue = PFViewLayers.Clouds };
            celestialCloudDetailMaterials.Add(key, material);
            return material;
        }

        private void DestroyCloudResourcesForBody(CelestialViewCacheKey owner)
        {
            foreach (var key in celestialCloudMaterials.Keys.Where(key => key.WorldSessionId == owner.WorldSessionId && key.InstanceId == owner.InstanceId).ToArray())
                DestroyOwnedResource(celestialCloudMaterials, key);
            foreach (var key in celestialCloudDetailMaterials.Keys.Where(key => key.WorldSessionId == owner.WorldSessionId && key.InstanceId == owner.InstanceId).ToArray())
                DestroyOwnedResource(celestialCloudDetailMaterials, key);
        }

        private void DestroyCloudResources()
        {
            foreach (var material in celestialCloudMaterials.Values)
                if (material != null) UnityEngine.Object.Destroy(material);
            celestialCloudMaterials.Clear();
            foreach (var material in celestialCloudDetailMaterials.Values)
                if (material != null) UnityEngine.Object.Destroy(material);
            celestialCloudDetailMaterials.Clear();
            if (cloudMaterial != null) UnityEngine.Object.Destroy(cloudMaterial);
            cloudMaterial = null;
            if (cloudDetailMaterial != null) UnityEngine.Object.Destroy(cloudDetailMaterial);
            cloudDetailMaterial = null;
            cloudResourcesUnavailable = false;
            // 网格和原版贴图均为借用资源，生命周期由各自的缓存持有。
        }
    }
}
