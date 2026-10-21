using System.IO;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// Low-poly Soyuz test asset for PFView.
    ///
    /// The OBJ is a unit-cube normalized prism mesh:
    ///   X/Z = 12/16-sided radial sections, Y = forward length.
    /// The runtime PartMatrix restores the real dimensions from boundsSizeShip.
    ///
    /// PFLitNormal uses a derivative-rebuilt TBN, so this path works through the
    /// normal WorldQuadBatch attributes (position/normal/UV) without tangents.
    /// </summary>
    internal static class SoyuzModelAssets
    {
        internal const string PartDefName = "PF_ShipPart_Soyuz";

        private const string MeshRelativePath = @"Models\Soyuz\PFL_Soyuz_Prism.obj";
        private const string AlbedoPath = "PFView/Ships/Soyuz/SoyuzAlbedo";
        private const string NormalPath = "PFView/Ships/Soyuz/SoyuzNormal";

        private static Mesh? mesh;
        private static Material? material;
        private static Texture2D? linearNormal;
        private static bool tried;
        private static string? failure;

        internal static bool Available => material != null;

        internal static string? Failure => failure;

        internal static Mesh? Mesh
        {
            get
            {
                if (mesh != null) return mesh;

                try
                {
                    string asmDir = Path.GetDirectoryName(typeof(SoyuzModelAssets).Assembly.Location) ?? ".";
                    string path = Path.GetFullPath(
                        Path.Combine(asmDir, "..", MeshRelativePath));
                    mesh = UnityEngine.Resources.Load<Mesh>(path);
                    if (mesh == null)
                    {
                        failure = "OBJ load failed: " + path;
                        Log.Error("[PFView·联盟号] " + failure);
                    }
                    else
                    {
                        Log.Message("[PFView·联盟号] mesh loaded: verts=" + mesh.vertexCount
                            + ", submeshes=" + mesh.subMeshCount
                            + ", bounds=" + mesh.bounds.size);
                    }
                }
                catch (System.Exception exception)
                {
                    failure = "OBJ load exception: " + exception.Message;
                    Log.Error("[PFView·联盟号] " + failure);
                }

                return mesh;
            }
        }

        internal static Material? Material
        {
            get
            {
                if (tried) return material;
                tried = true;

                Shader? shader = Shader.Find("PFLitNormal");
                bool hasNormalMapShader = shader != null;
                if (shader == null) shader = Shader.Find("PFLit");
                if (shader == null)
                {
                    failure = "Neither PFLitNormal nor PFLit was found";
                    Log.Error("[PFView·联盟号] " + failure);
                    return null;
                }

                var mat = new Material(shader) { color = Color.white };
                Texture2D? albedo = ContentFinder<Texture2D>.Get(AlbedoPath, reportFailure: false);
                Texture2D? normal = ContentFinder<Texture2D>.Get(NormalPath, reportFailure: false);
                if (albedo != null)
                {
                    albedo.wrapMode = TextureWrapMode.Repeat;
                    mat.mainTexture = albedo;
                }
                else
                {
                    mat.mainTexture = Texture2D.whiteTexture;
                    failure = "albedo missing: " + AlbedoPath;
                    Log.Warning("[PFView·联盟号] " + failure);
                }

                if (hasNormalMapShader && normal != null)
                {
                    // The normal PNG is authored as raw data (Blender Non-Color),
                    // while ContentFinder returns an sRGB texture. Copy the raw
                    // pixels into a linear texture so the shader sees the intended
                    // tangent-space values rather than gamma-decoded ones.
                    normal.wrapMode = TextureWrapMode.Repeat;
                    linearNormal = new Texture2D(
                        normal.width, normal.height, TextureFormat.RGBA32, false, true);
                    linearNormal.name = normal.name + "_Linear";
                    linearNormal.SetPixels32(normal.GetPixels32());
                    linearNormal.Apply(false, false);
                    linearNormal.wrapMode = TextureWrapMode.Repeat;
                    mat.SetTexture("_NormalTex", linearNormal);
                    mat.SetVector("_NormalParams", new Vector4(0.9f, 0f, 0f, 0f));
                }
                else
                {
                    mat.SetVector("_NormalParams", Vector4.zero);
                    if (hasNormalMapShader)
                    {
                        failure = "normal map missing: " + NormalPath;
                        Log.Warning("[PFView·联盟号] " + failure);
                    }
                }

                mat.SetVector("_Color", new Vector4(1f, 1f, 1f, 1f));
                mat.SetVector("_SunDir", new Vector4(1f, 0f, 0f, 0f));
                mat.SetVector("_SunColor", new Vector4(1f, 0.98f, 0.9f, 1f));
                mat.SetVector("_CamPos", new Vector4(0f, 0f, 0f, 1f));
                material = mat;
                Log.Message("[PFView·联盟号] asset material ready, shader=" + shader.name
                    + ", normalMap=" + (hasNormalMapShader && normal != null ? "OK" : "off"));
                return material;
            }
        }

        internal static void ApplyLighting(Vector3 toSun, Color sunColor, Vector3 cameraViewPos)
        {
            Material? mat = material;
            if (mat == null) return;
            mat.SetVector("_SunDir", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            mat.SetVector("_SunColor", new Vector4(sunColor.r, sunColor.g, sunColor.b, 1f));
            mat.SetVector("_CamPos", new Vector4(cameraViewPos.x, cameraViewPos.y, cameraViewPos.z, 1f));
        }

        internal static bool TryDraw(
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
            if (part == null || part.defName != PartDefName) return false;

            Mesh? assetMesh = Mesh;
            Material? assetMaterial = Material;
            if (assetMesh == null || assetMaterial == null) return false;

            block.SetColor("_Color", Color.white);
            Matrix4x4 matrix = ShipModelRenderer.PartMatrix(
                viewPos, groupRotation, part.rotationShip, part.positionShip,
                part.boundsSizeShip, metersToView, scale);
            Graphics.DrawMesh(assetMesh, matrix, assetMaterial, layer, null, 0, block);
            return true;
        }

        internal static void Destroy()
        {
            if (material != null)
            {
                Object.Destroy(material);
                material = null;
            }
            if (linearNormal != null)
            {
                Object.Destroy(linearNormal);
                linearNormal = null;
            }
            tried = false;
            failure = null;
            // mesh is owned by the engine's file-backed resource cache.
            mesh = null;
        }
    }
}
