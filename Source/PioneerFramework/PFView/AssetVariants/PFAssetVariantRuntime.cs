using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// Tracks the active minimal-asset mode and owns the registered PFView
    /// model variants. Refresh() is called once per rendered scene. When the
    /// setting or prefix changes, all registered models release their owned
    /// materials so no base asset is retained after switching to minimal mode.
    /// </summary>
    internal static class PFAssetVariantRuntime
    {
        private static readonly List<PFModelAsset> Assets = new();
        private static bool initialized;
        private static bool lastMinimalMode;
        private static string lastPrefix = string.Empty;

        internal static int Revision { get; private set; }

        internal static void Register(PFModelAsset asset)
        {
            if (asset != null && !Assets.Contains(asset)) Assets.Add(asset);
        }

        internal static void Refresh()
        {
            bool minimalMode = PFAssetVariantRules.MinimalMode;
            string configuredPrefix = PFAssetVariantRules.Prefix;
            string prefix = minimalMode ? configuredPrefix : string.Empty;

            if (!initialized)
            {
                initialized = true;
                lastMinimalMode = minimalMode;
                lastPrefix = prefix;
                return;
            }

            if (minimalMode == lastMinimalMode
                && string.Equals(prefix, lastPrefix, StringComparison.Ordinal))
            {
                return;
            }

            lastMinimalMode = minimalMode;
            lastPrefix = prefix;
            Revision++;

            for (int i = 0; i < Assets.Count; i++) Assets[i].Invalidate();

            Log.Message("[PFView·极简] 资产模式已切换为"
                + (minimalMode ? "开启" : "关闭")
                + "，前缀=\"" + configuredPrefix + "\"；旧材质缓存已释放。");
        }
    }

    /// <summary>
    /// One runtime model family (base + optional prefixed minimal variant).
    /// The instance is shared by every entity of the same partDefName.
    /// </summary>
    internal sealed class PFModelAsset
    {
        private const float MinimalBoundsTolerance = 0.02f;
        private static readonly Vector4 NoNormalParams = new(0f, 0f, 0f, 0f);

        private readonly string displayName;
        private readonly string baseMeshRelativePath;
        private readonly string baseAlbedoPath;
        private readonly string baseNormalPath;

        private Mesh? mesh;
        private Material? material;
        private Texture2D? linearNormal;
        private bool resolved;
        private bool available;
        private bool packedUvTransportReady;
        private int loadedRevision = -1;
        private string? failure;

        internal PFModelAsset(
            string displayName,
            string baseMeshRelativePath,
            string baseAlbedoPath,
            string baseNormalPath)
        {
            this.displayName = displayName;
            this.baseMeshRelativePath = baseMeshRelativePath;
            this.baseAlbedoPath = baseAlbedoPath;
            this.baseNormalPath = baseNormalPath;
            PFAssetVariantRuntime.Register(this);
        }

        internal bool Available => available;
        internal string? Failure => failure;

        internal bool TryDraw(
            string partDefName,
            SpaceEntity entity,
            Vector3 viewPos,
            Quaternion groupRotation,
            float scale,
            int layer,
            MaterialPropertyBlock block,
            float metersToView)
        {
            if (entity == null || entity.Parts.Count != 1) return false;
            SpacePart part = entity.Parts[0];
            if (part == null || part.defName != partDefName) return false;

            if (!TryGetResources(out Mesh? assetMesh, out Material? assetMaterial)
                || assetMesh == null || assetMaterial == null)
            {
                return false;
            }

            block.SetColor("_Color", Color.white);
            Matrix4x4 matrix = ShipModelRenderer.PartMatrix(
                viewPos, groupRotation, part.rotationShip, part.positionShip,
                part.boundsSizeShip, metersToView, scale);
            Graphics.DrawMesh(assetMesh, matrix, assetMaterial, layer, null, 0, block);
            return true;
        }

        internal void ApplyLighting(Vector3 toSun, Color sunColor, Vector3 cameraViewPos)
        {
            Material? mat = material;
            if (mat == null) return;
            mat.SetVector("_SunDir", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            mat.SetVector("_SunColor", new Vector4(sunColor.r, sunColor.g, sunColor.b, 1f));
            mat.SetVector("_CamPos", new Vector4(cameraViewPos.x, cameraViewPos.y, cameraViewPos.z, 1f));
        }

        internal void Destroy()
        {
            ResetResources();
            failure = null;
        }

        internal void Invalidate()
        {
            ResetResources();
            failure = null;
        }

        private void ResetResources()
        {
            if (material != null)
            {
                UnityEngine.Object.Destroy(material);
                material = null;
            }
            if (linearNormal != null)
            {
                UnityEngine.Object.Destroy(linearNormal);
                linearNormal = null;
            }

            mesh = null;
            resolved = false;
            available = false;
            packedUvTransportReady = false;
            loadedRevision = -1;
        }

        private bool TryGetResources(out Mesh? assetMesh, out Material? assetMaterial)
        {
            if (loadedRevision != PFAssetVariantRuntime.Revision)
            {
                ResetResources();
            }

            if (resolved)
            {
                assetMesh = mesh;
                assetMaterial = material;
                return available;
            }

            resolved = true;
            loadedRevision = PFAssetVariantRuntime.Revision;

            bool minimal = PFAssetVariantRules.MinimalMode;
            string prefix = PFAssetVariantRules.Prefix;
            string meshPath = minimal
                ? PFAssetVariantRules.ApplyPrefix(baseMeshRelativePath, prefix)
                : baseMeshRelativePath;

            Mesh? loadedMesh = LoadMesh(meshPath, minimal);
            if (loadedMesh == null)
            {
                assetMesh = null;
                assetMaterial = null;
                return false;
            }

            packedUvTransportReady = PreparePackedUvTransport(loadedMesh, minimal);
            Material? loadedMaterial = BuildMaterial(minimal, prefix);
            if (loadedMaterial == null)
            {
                assetMesh = null;
                assetMaterial = null;
                return false;
            }

            mesh = loadedMesh;
            material = loadedMaterial;
            available = true;
            assetMesh = mesh;
            assetMaterial = material;
            return true;
        }

        private Mesh? LoadMesh(string relativePath, bool minimal)
        {
            string asmDir = Path.GetDirectoryName(typeof(PFModelAsset).Assembly.Location) ?? ".";
            string path = Path.GetFullPath(Path.Combine(asmDir, "..", relativePath));

            if (!File.Exists(path))
            {
                failure = (minimal ? "极简资产缺失: " : "OBJ 缺失: ") + relativePath;
                if (minimal)
                {
                    Log.Warning("[PFView·极简] " + displayName + ": " + failure
                        + "；使用 Box/Cylinder/Sphere 占位。");
                }
                else
                {
                    Log.Error("[PFView·" + displayName + "] " + failure);
                }
                return null;
            }

            Mesh? loaded;
            try
            {
                loaded = Resources.Load<Mesh>(path);
            }
            catch (Exception exception)
            {
                failure = "OBJ 加载异常: " + exception.Message;
                Log.Error("[PFView·" + displayName + "] " + failure);
                return null;
            }

            if (loaded == null)
            {
                failure = "OBJ 加载失败: " + path;
                Log.Error("[PFView·" + displayName + "] " + failure);
                return null;
            }

            if (minimal && !IsValidMinimalMesh(loaded))
            {
                failure = "极简 OBJ 不是有效的单位网格: " + relativePath;
                Log.Warning("[PFView·极简] " + displayName + ": " + failure
                    + "；使用 Box/Cylinder/Sphere 占位。");
                return null;
            }

            Log.Message("[PFView·" + displayName + "] mesh loaded: verts=" + loaded.vertexCount
                + ", submeshes=" + loaded.subMeshCount
                + ", bounds=" + loaded.bounds.size
                + ", variant=" + (minimal ? "minimal" : "base"));
            return loaded;
        }

        private static bool IsValidMinimalMesh(Mesh candidate)
        {
            if (candidate.vertexCount <= 0 || candidate.subMeshCount != 1) return false;

            int[] triangles;
            try
            {
                triangles = candidate.triangles;
            }
            catch
            {
                return false;
            }

            if (triangles == null || triangles.Length < 3) return false;

            Vector3 size = candidate.bounds.size;
            Vector3 center = candidate.bounds.center;
            const float epsilon = MinimalBoundsTolerance;
            return Mathf.Abs(size.x - 1f) <= epsilon
                && Mathf.Abs(size.y - 1f) <= epsilon
                && Mathf.Abs(size.z - 1f) <= epsilon
                && center.sqrMagnitude <= epsilon * epsilon;
        }

        private bool PreparePackedUvTransport(Mesh assetMesh, bool minimal)
        {
            try
            {
                Vector2[] uvs = assetMesh.uv;
                if (uvs == null || uvs.Length == 0 || uvs.Length != assetMesh.vertexCount)
                {
                    Log.Warning("[PFView·" + displayName + "] UV transport unavailable: uv="
                        + (uvs?.Length ?? 0) + ", verts=" + assetMesh.vertexCount);
                    return false;
                }

                var packed = new Vector4[uvs.Length];
                float minU = float.MaxValue, maxU = float.MinValue;
                float minV = float.MaxValue, maxV = float.MinValue;
                for (int i = 0; i < uvs.Length; i++)
                {
                    Vector2 uv = uvs[i];
                    packed[i] = new Vector4(uv.x, uv.y, 1f, 1f);
                    if (uv.x < minU) minU = uv.x;
                    if (uv.x > maxU) maxU = uv.x;
                    if (uv.y < minV) minV = uv.y;
                    if (uv.y > maxV) maxV = uv.y;
                }

                assetMesh.tangents = packed;
                Vector4[] verify = assetMesh.tangents;
                if (verify == null || verify.Length != uvs.Length)
                {
                    Log.Warning("[PFView·" + displayName + "] UV transport rebuild failed: tangent="
                        + (verify?.Length ?? 0) + ", verts=" + assetMesh.vertexCount);
                    return false;
                }

                Log.Message("[PFView·" + displayName + "] UV transport ready: count=" + uvs.Length
                    + ", U=" + minU + ".." + maxU
                    + ", V=" + minV + ".." + maxV
                    + ", variant=" + (minimal ? "minimal" : "base"));
                return true;
            }
            catch (Exception exception)
            {
                Log.Warning("[PFView·" + displayName + "] UV transport exception: " + exception.Message);
                return false;
            }
        }

        private Material? BuildMaterial(bool minimal, string prefix)
        {
            Shader? shader = Shader.Find("PFLitNormal");
            bool hasNormalMapShader = shader != null;
            if (shader == null) shader = Shader.Find("PFLit");
            if (shader == null)
            {
                failure = "Neither PFLitNormal nor PFLit was found";
                Log.Error("[PFView·" + displayName + "] " + failure);
                return null;
            }

            var mat = new Material(shader) { color = Color.white };
            string albedoPath = minimal
                ? PFAssetVariantRules.ApplyPrefix(baseAlbedoPath, prefix)
                : baseAlbedoPath;
            Texture2D? albedo = ContentFinder<Texture2D>.Get(albedoPath, reportFailure: false);
            if (albedo != null)
            {
                albedo.wrapMode = TextureWrapMode.Repeat;
                mat.mainTexture = albedo;
                mat.SetTexture("_MainTex", albedo);
            }
            else
            {
                mat.mainTexture = Texture2D.whiteTexture;
                mat.SetTexture("_MainTex", Texture2D.whiteTexture);
                failure = "albedo 缺失: " + albedoPath;
                Log.Warning("[PFView·" + displayName + "] " + failure
                    + "；使用共享白材质。");
            }

            if (hasNormalMapShader && !minimal)
            {
                Texture2D? normal = ContentFinder<Texture2D>.Get(baseNormalPath, reportFailure: false);
                if (normal != null)
                {
                    normal.wrapMode = TextureWrapMode.Repeat;
                    linearNormal = new Texture2D(
                        normal.width, normal.height, TextureFormat.RGBA32, false, true);
                    linearNormal.name = normal.name + "_Linear";
                    linearNormal.SetPixels32(normal.GetPixels32());
                    linearNormal.Apply(false, false);
                    linearNormal.wrapMode = TextureWrapMode.Repeat;
                    mat.SetTexture("_NormalTex", linearNormal);
                    mat.SetVector("_NormalParams", new Vector4(0.8f, 0f, 0f, 0f));
                }
                else
                {
                    mat.SetVector("_NormalParams", NoNormalParams);
                    failure = "normal map 缺失: " + baseNormalPath;
                    Log.Warning("[PFView·" + displayName + "] " + failure);
                }
            }
            else
            {
                // Minimal variants deliberately avoid the full-resolution
                // RGBA32 normal copy. They use albedo only by default.
                mat.SetVector("_NormalParams", NoNormalParams);
            }

            mat.SetVector("_MainTex_ST", new Vector4(0f, 0f, 1f, 1f));
            mat.SetVector("_Color", new Vector4(1f, 1f, 1f, 1f));
            mat.SetVector("_SunDir", new Vector4(1f, 0f, 0f, 0f));
            mat.SetVector("_SunColor", new Vector4(1f, 0.98f, 0.9f, 1f));
            mat.SetVector("_CamPos", new Vector4(0f, 0f, 0f, 1f));

            Log.Message("[PFView·" + displayName + "] asset material ready, shader=" + shader.name
                + ", albedo=" + (albedo != null ? "OK" : "white")
                + ", normalMap=" + (minimal ? "off" : (hasNormalMapShader && linearNormal != null ? "OK" : "off"))
                + ", variant=" + (minimal ? "minimal" : "base")
                + ", uvTransport=" + (packedUvTransportReady ? "tangent" : "fallback"));
            return mat;
        }
    }
}
