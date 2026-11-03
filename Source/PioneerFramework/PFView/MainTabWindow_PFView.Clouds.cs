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

            float radius = renderR * (float)PFViewClouds.RadiusFactor(body.radius, altitude);
            // 低空看向云层时，仅按地表余量算 near 会切掉云壳近侧；取实际渲染尺度的云壳余量。
            float shellGapView = cameraFromBody.magnitude - radius;
            renderCamera.nearClipPlane = (float)PFViewClouds.LimitNearClip(renderCamera.nearClipPlane, shellGapView, NearClip);
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

        private void DestroyCloudResourcesForBody(CelestialViewCacheKey owner)
        {
            foreach (var key in celestialCloudMaterials.Keys.Where(key => key.WorldSessionId == owner.WorldSessionId && key.InstanceId == owner.InstanceId).ToArray())
                DestroyOwnedResource(celestialCloudMaterials, key);
        }

        private void DestroyCloudResources()
        {
            foreach (var material in celestialCloudMaterials.Values)
                if (material != null) UnityEngine.Object.Destroy(material);
            celestialCloudMaterials.Clear();
            if (cloudMaterial != null) UnityEngine.Object.Destroy(cloudMaterial);
            cloudMaterial = null;
            cloudResourcesUnavailable = false;
            // 网格和原版贴图均为借用资源，生命周期由各自的缓存持有。
        }
    }
}
