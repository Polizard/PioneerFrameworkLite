// Unity 2022.3.35f1 editor-only builder for the PFView outline shaders.
//
// Normal use goes through Tools/PFViewShaderBundle/
// Build-PFViewOutlineBundles.ps1, which creates a temporary Unity project and
// calls this interface:
//
//   Unity.exe -batchmode -quit -projectPath <temporary project>
//       -executeMethod PFViewOutlineBundleBuilder.BuildAll
//       -pfViewOutput <temporary project>/PublishedBundles
//
// BuildAll stages three extensionless bundles after every platform build and
// CRC check succeeds. Build-PFViewOutlineBundles.ps1 then uses Unity's own
// WebExtract and binary2text tools to verify the serialized Shader names before
// publishing them to the Mod. Runtime code should load the active-platform
// bundle, construct PFViewOutlineShaderSet from the three Shader assets, and
// provide it through IPFViewOutlineShaderProvider. The projection-alignment
// GPU check calls the staged PFViewOutlineProjection source so it exercises
// the same matrix rule as the runtime renderer; it does not call the renderer
// or modify PFView state.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using PioneerFramework.PFView;

public static class PFViewOutlineBundleBuilder
{
    private const string LogPrefix = "[PFViewOutlineBundle] ";
    private const string OutputArgument = "-pfViewOutput";

    private static readonly ShaderAsset[] ShaderAssets =
    {
        new ShaderAsset(
            "Assets/Data/NewFrontierTeam.PioneerFramework/Materials/" +
            "PFView/PFView_OutlineMask.shader",
            "Hidden/PioneerFramework/PFView/OutlineMask"),
        new ShaderAsset(
            "Assets/Data/NewFrontierTeam.PioneerFramework/Materials/" +
            "PFView/PFView_OutlineDilate.shader",
            "Hidden/PioneerFramework/PFView/OutlineDilate"),
        new ShaderAsset(
            "Assets/Data/NewFrontierTeam.PioneerFramework/Materials/" +
            "PFView/PFView_OutlineComposite.shader",
            "Hidden/PioneerFramework/PFView/OutlineComposite")
    };

    private static readonly BundleTarget[] BundleTargets =
    {
        new BundleTarget(
            BuildTarget.StandaloneWindows64,
            "pfview_outline_win"),
        new BundleTarget(
            BuildTarget.StandaloneLinux64,
            "pfview_outline_linux"),
        new BundleTarget(
            BuildTarget.StandaloneOSX,
            "pfview_outline_mac")
    };

    [MenuItem("PFView/Build Outline Shader Bundles")]
    public static void BuildAll()
    {
        string projectRoot = Path.GetFullPath(
            Path.Combine(Application.dataPath, ".."));
        string outputRoot = GetCommandLineValue(OutputArgument);
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            outputRoot = Path.Combine(projectRoot, "PublishedBundles");
        }

        BuildAll(projectRoot, Path.GetFullPath(outputRoot));
    }

    private static void BuildAll(string projectRoot, string outputRoot)
    {
        ValidateShaderAssets();

        string workingRoot = Path.Combine(projectRoot, "ShaderOutput");
        if (Directory.Exists(workingRoot))
        {
            Directory.Delete(workingRoot, true);
        }
        Directory.CreateDirectory(workingRoot);

        var builtBundles = new List<BuiltBundle>();
        foreach (BundleTarget bundleTarget in BundleTargets)
        {
            BuiltBundle builtBundle = BuildBundle(
                workingRoot,
                bundleTarget);
            builtBundles.Add(builtBundle);
            if (bundleTarget.Target == BuildTarget.StandaloneWindows64)
            {
                ValidateRuntimeLoad(builtBundle);
            }
        }

        Directory.CreateDirectory(outputRoot);
        foreach (BuiltBundle builtBundle in builtBundles)
        {
            string publishedPath = Path.Combine(
                outputRoot,
                builtBundle.Name);
            File.Copy(builtBundle.Path, publishedPath, true);

            string staleManifest = publishedPath + ".manifest";
            if (File.Exists(staleManifest))
            {
                File.Delete(staleManifest);
            }

            Debug.Log(
                LogPrefix + "Staged bundle: " + builtBundle.Name);
        }

        Debug.Log(LogPrefix + "All outline bundles completed.");
    }

    private static void ValidateRuntimeLoad(BuiltBundle builtBundle)
    {
        AssetBundle bundle = AssetBundle.LoadFromFile(builtBundle.Path);
        if (bundle == null)
        {
            throw new InvalidOperationException(
                "Unity could not load the freshly built bundle: " +
                builtBundle.Path);
        }

        try
        {
            if (!string.Equals(
                bundle.name,
                builtBundle.Name,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Runtime-loaded Bundle identity mismatch. Expected " +
                    builtBundle.Name + ", found " + bundle.name + ".");
            }

            Shader[] shaders = bundle.LoadAllAssets<Shader>();
            foreach (ShaderAsset expected in ShaderAssets)
            {
                bool found = Array.Exists(
                    shaders,
                    shader => shader != null && string.Equals(
                        shader.name,
                        expected.ShaderName,
                        StringComparison.Ordinal));
                if (!found)
                {
                    throw new InvalidOperationException(
                        "Runtime load omitted shader: " +
                        expected.ShaderName);
                }
            }

            Shader compositeShader = Array.Find(
                shaders,
                shader => shader != null && string.Equals(
                    shader.name,
                    "Hidden/PioneerFramework/PFView/OutlineComposite",
                    StringComparison.Ordinal));
            ValidateCompositeOuterRing(
                compositeShader,
                builtBundle.Name);
            Shader maskShader = Array.Find(
                shaders,
                shader => shader != null && string.Equals(
                    shader.name,
                    "Hidden/PioneerFramework/PFView/OutlineMask",
                    StringComparison.Ordinal));
            ValidateProjectionAlignment(
                maskShader,
                builtBundle.Name);
        }
        finally
        {
            bundle.Unload(true);
        }

        Debug.Log(
            LogPrefix + "Runtime-loaded bundle: " + builtBundle.Name);
    }

    private static void ValidateCompositeOuterRing(
        Shader compositeShader,
        string bundleName)
    {
        const int size = 5;
        const int center = 2;
        int originalMaskId = Shader.PropertyToID(
            "_PFViewOriginalMask");
        int outlineColorId = Shader.PropertyToID(
            "_PFViewOutlineColor");
        int radiusId = Shader.PropertyToID(
            "_PFViewOutlineRadius");

        Texture2D dilatedMask = null;
        Texture2D originalMask = null;
        Texture2D readback = null;
        RenderTexture destination = null;
        Material material = null;
        CommandBuffer commands = null;
        RenderTexture previousActive = RenderTexture.active;

        try
        {
            dilatedMask = CreateMaskTexture(size, true);
            originalMask = CreateMaskTexture(size, false);
            readback = new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false,
                true);
            destination = new RenderTexture(
                size,
                size,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!destination.Create())
            {
                throw new InvalidOperationException(
                    "Could not create the composite validation target.");
            }

            material = new Material(compositeShader);
            commands = new CommandBuffer
            {
                name = "PFView Outline Composite Validation"
            };
            commands.SetGlobalTexture(
                originalMaskId,
                new RenderTargetIdentifier(originalMask));
            commands.SetGlobalColor(outlineColorId, Color.magenta);
            commands.SetGlobalInt(radiusId, 1);
            commands.SetRenderTarget(destination);
            commands.ClearRenderTarget(false, true, Color.white);
            commands.Blit(
                new RenderTargetIdentifier(dilatedMask),
                new RenderTargetIdentifier(destination),
                material,
                0);
            Graphics.ExecuteCommandBuffer(commands);

            RenderTexture.active = destination;
            readback.ReadPixels(
                new Rect(0, 0, size, size),
                0,
                0,
                false);
            readback.Apply(false, false);

            AssertPixel(
                readback.GetPixel(center, center),
                Color.white,
                "Original-mask center must preserve scene color.");
            AssertPixel(
                readback.GetPixel(center, center - 1),
                Color.magenta,
                "Expanded outer-ring pixel must use the configured color.");
            AssertPixel(
                readback.GetPixel(0, 0),
                Color.white,
                "Pixels outside the expanded mask must preserve scene color.");
        }
        finally
        {
            RenderTexture.active = previousActive;
            Shader.SetGlobalTexture(originalMaskId, null);
            Shader.SetGlobalColor(outlineColorId, Color.black);
            Shader.SetGlobalInt(radiusId, 0);

            if (commands != null)
            {
                commands.Release();
            }
            if (destination != null)
            {
                destination.Release();
                UnityEngine.Object.DestroyImmediate(destination);
            }
            if (material != null)
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
            if (readback != null)
            {
                UnityEngine.Object.DestroyImmediate(readback);
            }
            if (originalMask != null)
            {
                UnityEngine.Object.DestroyImmediate(originalMask);
            }
            if (dilatedMask != null)
            {
                UnityEngine.Object.DestroyImmediate(dilatedMask);
            }
        }

        Debug.Log(
            LogPrefix + "Verified exterior-only composite: " +
            bundleName);
    }

    private static void ValidateProjectionAlignment(
        Shader maskShader,
        string bundleName)
    {
        const int size = 64;
        const int testLayer = 30;
        GameObject cameraObject = null;
        Camera camera = null;
        Mesh mesh = null;
        Material material = null;
        RenderTexture cameraTarget = null;
        RenderTexture commandTarget = null;
        Texture2D readback = null;
        CommandBuffer commands = null;
        RenderTexture previousActive = RenderTexture.active;

        try
        {
            cameraTarget = CreateValidationTarget(size, 24);
            commandTarget = CreateValidationTarget(size, 24);
            readback = new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false,
                true);
            material = new Material(maskShader);
            mesh = CreateAsymmetricAlignmentMesh();

            cameraObject = new GameObject(
                "PFView Outline Projection Validation Camera");
            camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 1 << testLayer;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            camera.fieldOfView = 60f;
            camera.aspect = 1f;
            camera.targetTexture = cameraTarget;

            Matrix4x4 objectMatrix = Matrix4x4.TRS(
                new Vector3(0.15f, 0.65f, 3f),
                Quaternion.Euler(0f, 0f, 11f),
                Vector3.one);
            Graphics.DrawMesh(
                mesh,
                objectMatrix,
                material,
                testLayer,
                camera,
                0,
                null);
            camera.Render();
            MaskBounds cameraBounds = ReadMaskBounds(
                cameraTarget,
                readback,
                "Camera-rendered validation mask");

            commands = new CommandBuffer
            {
                name = "PFView Outline Projection Alignment Validation"
            };
            commands.SetViewProjectionMatrices(
                camera.worldToCameraMatrix,
                PFViewOutlineProjection.GetForCommandBuffer(camera));
            commands.SetRenderTarget(commandTarget);
            commands.ClearRenderTarget(true, true, Color.black);
            commands.DrawMesh(
                mesh,
                objectMatrix,
                material,
                0,
                0);
            Graphics.ExecuteCommandBuffer(commands);
            MaskBounds commandBounds = ReadMaskBounds(
                commandTarget,
                readback,
                "CommandBuffer validation mask");

            AssertMaskAlignment(cameraBounds, commandBounds);
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (camera != null)
            {
                camera.targetTexture = null;
            }
            if (commands != null)
            {
                commands.Release();
            }
            DestroyValidationTarget(commandTarget);
            DestroyValidationTarget(cameraTarget);
            if (cameraObject != null)
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
            if (mesh != null)
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
            if (material != null)
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
            if (readback != null)
            {
                UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        Debug.Log(
            LogPrefix + "Verified projection alignment: " + bundleName);
    }

    private static RenderTexture CreateValidationTarget(
        int size,
        int depth)
    {
        var target = new RenderTexture(
            size,
            size,
            depth,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Linear)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            antiAliasing = 1
        };
        if (!target.Create())
        {
            UnityEngine.Object.DestroyImmediate(target);
            throw new InvalidOperationException(
                "Could not create a projection validation target.");
        }
        return target;
    }

    private static void DestroyValidationTarget(RenderTexture target)
    {
        if (target == null)
        {
            return;
        }

        target.Release();
        UnityEngine.Object.DestroyImmediate(target);
    }

    private static Mesh CreateAsymmetricAlignmentMesh()
    {
        var mesh = new Mesh
        {
            name = "PFView Outline Projection Validation Mesh",
            vertices = new[]
            {
                new Vector3(-0.45f, -0.25f, 0f),
                new Vector3(0.50f, -0.15f, 0f),
                new Vector3(0.08f, 0.55f, 0f)
            },
            triangles = new[]
            {
                0, 2, 1,
                0, 1, 2
            }
        };
        mesh.RecalculateBounds();
        return mesh;
    }

    private static MaskBounds ReadMaskBounds(
        RenderTexture source,
        Texture2D readback,
        string description)
    {
        RenderTexture.active = source;
        readback.ReadPixels(
            new Rect(0, 0, source.width, source.height),
            0,
            0,
            false);
        readback.Apply(false, false);

        int minimumX = source.width;
        int minimumY = source.height;
        int maximumX = -1;
        int maximumY = -1;
        long sumX = 0;
        long sumY = 0;
        int count = 0;
        for (int y = 0; y < source.height; y++)
        {
            for (int x = 0; x < source.width; x++)
            {
                if (readback.GetPixel(x, y).r < 0.5f)
                {
                    continue;
                }

                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
                sumX += x;
                sumY += y;
                count++;
            }
        }

        if (count == 0)
        {
            throw new InvalidOperationException(
                description + " contains no rendered pixels.");
        }

        return new MaskBounds(
            minimumX,
            minimumY,
            maximumX,
            maximumY,
            (float)sumX / count,
            (float)sumY / count);
    }

    private static void AssertMaskAlignment(
        MaskBounds expected,
        MaskBounds actual)
    {
        const float tolerance = 1f;
        if (Mathf.Abs(expected.MinimumX - actual.MinimumX) > tolerance ||
            Mathf.Abs(expected.MinimumY - actual.MinimumY) > tolerance ||
            Mathf.Abs(expected.MaximumX - actual.MaximumX) > tolerance ||
            Mathf.Abs(expected.MaximumY - actual.MaximumY) > tolerance ||
            Mathf.Abs(expected.CenterX - actual.CenterX) > tolerance ||
            Mathf.Abs(expected.CenterY - actual.CenterY) > tolerance)
        {
            throw new InvalidOperationException(
                "PFView outline projection is misaligned. Camera=" +
                expected + ", CommandBuffer=" + actual + ".");
        }
    }

    private static Texture2D CreateMaskTexture(
        int size,
        bool includeExpandedRing)
    {
        var texture = new Texture2D(
            size,
            size,
            TextureFormat.RGBA32,
            false,
            true)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        var pixels = new Color[size * size];
        int center = size / 2;
        if (includeExpandedRing)
        {
            for (int y = center - 1; y <= center + 1; y++)
            {
                for (int x = center - 1; x <= center + 1; x++)
                {
                    pixels[y * size + x] = Color.red;
                }
            }
        }
        else
        {
            pixels[center * size + center] = Color.red;
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static void AssertPixel(
        Color actual,
        Color expected,
        string message)
    {
        const float tolerance = 0.05f;
        if (Mathf.Abs(actual.r - expected.r) > tolerance ||
            Mathf.Abs(actual.g - expected.g) > tolerance ||
            Mathf.Abs(actual.b - expected.b) > tolerance)
        {
            throw new InvalidOperationException(
                message + " Expected " + expected +
                ", found " + actual + ".");
        }
    }

    private static void ValidateShaderAssets()
    {
        AssetDatabase.Refresh(
            ImportAssetOptions.ForceSynchronousImport |
            ImportAssetOptions.ForceUpdate);

        foreach (ShaderAsset shaderAsset in ShaderAssets)
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(
                shaderAsset.AssetPath);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Shader asset could not be imported: " +
                    shaderAsset.AssetPath);
            }
            if (!string.Equals(
                shader.name,
                shaderAsset.ShaderName,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Shader name mismatch for " + shaderAsset.AssetPath +
                    ": " + shader.name);
            }
            if (ShaderUtil.ShaderHasError(shader))
            {
                throw new InvalidOperationException(
                    "Shader has compilation errors: " +
                    shaderAsset.AssetPath);
            }

            Debug.Log(
                LogPrefix + "Imported shader: " +
                shaderAsset.ShaderName);
        }
    }

    private static BuiltBundle BuildBundle(
        string workingRoot,
        BundleTarget bundleTarget)
    {
        if (!BuildPipeline.IsBuildTargetSupported(
            BuildTargetGroup.Standalone,
            bundleTarget.Target))
        {
            throw new InvalidOperationException(
                "Unity build support is unavailable for " +
                bundleTarget.Target + ". Install its target support module.");
        }

        // Unity also emits a root manifest bundle named after outputPath's
        // final directory. Keep that directory distinct from the requested
        // user bundle name to avoid a name conflict in Unity 2022.3.
        string targetOutput = Path.Combine(
            workingRoot,
            bundleTarget.Target.ToString());
        Directory.CreateDirectory(targetOutput);

        var build = new AssetBundleBuild
        {
            assetBundleName = bundleTarget.Name,
            assetNames = GetShaderAssetPaths()
        };
        const BuildAssetBundleOptions options =
            BuildAssetBundleOptions.ChunkBasedCompression |
            BuildAssetBundleOptions.ForceRebuildAssetBundle |
            BuildAssetBundleOptions.StrictMode;

        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            targetOutput,
            new[] { build },
            options,
            bundleTarget.Target);
        if (manifest == null)
        {
            throw new InvalidOperationException(
                "AssetBundle build returned no manifest for " +
                bundleTarget.Target + ".");
        }

        bool manifestContainsBundle = Array.Exists(
            manifest.GetAllAssetBundles(),
            name => string.Equals(
                name,
                bundleTarget.Name,
                StringComparison.Ordinal));
        if (!manifestContainsBundle)
        {
            throw new InvalidOperationException(
                "Build manifest omitted bundle: " + bundleTarget.Name);
        }

        string bundlePath = Path.Combine(
            targetOutput,
            bundleTarget.Name);
        if (!File.Exists(bundlePath))
        {
            throw new FileNotFoundException(
                "Unity did not create the expected bundle.",
                bundlePath);
        }

        uint crc;
        if (!BuildPipeline.GetCRCForAssetBundle(bundlePath, out crc))
        {
            throw new InvalidOperationException(
                "Unity could not read the bundle CRC: " + bundlePath);
        }

        Debug.Log(
            LogPrefix + "Built bundle: " + bundleTarget.Name +
            " target=" + bundleTarget.Target + " crc=" + crc);
        return new BuiltBundle(
            bundleTarget.Target,
            bundleTarget.Name,
            bundlePath);
    }

    private static string[] GetShaderAssetPaths()
    {
        var result = new string[ShaderAssets.Length];
        for (int index = 0; index < ShaderAssets.Length; index++)
        {
            result[index] = ShaderAssets[index].AssetPath;
        }
        return result;
    }

    private static string GetCommandLineValue(string argumentName)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(
                arguments[index],
                argumentName,
                StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }
        return null;
    }

    private sealed class ShaderAsset
    {
        public string AssetPath { get; }
        public string ShaderName { get; }

        public ShaderAsset(string assetPath, string shaderName)
        {
            AssetPath = assetPath;
            ShaderName = shaderName;
        }
    }

    private readonly struct MaskBounds
    {
        public int MinimumX { get; }
        public int MinimumY { get; }
        public int MaximumX { get; }
        public int MaximumY { get; }
        public float CenterX { get; }
        public float CenterY { get; }

        public MaskBounds(
            int minimumX,
            int minimumY,
            int maximumX,
            int maximumY,
            float centerX,
            float centerY)
        {
            MinimumX = minimumX;
            MinimumY = minimumY;
            MaximumX = maximumX;
            MaximumY = maximumY;
            CenterX = centerX;
            CenterY = centerY;
        }

        public override string ToString()
        {
            return string.Format(
                "bounds=({0},{1})-({2},{3}) center=({4:F2},{5:F2})",
                MinimumX,
                MinimumY,
                MaximumX,
                MaximumY,
                CenterX,
                CenterY);
        }
    }

    private sealed class BundleTarget
    {
        public BuildTarget Target { get; }
        public string Name { get; }

        public BundleTarget(BuildTarget target, string name)
        {
            Target = target;
            Name = name;
        }
    }

    private sealed class BuiltBundle
    {
        public BuildTarget Target { get; }
        public string Name { get; }
        public string Path { get; }

        public BuiltBundle(
            BuildTarget target,
            string name,
            string path)
        {
            Target = target;
            Name = name;
            Path = path;
        }
    }
}
