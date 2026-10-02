// Unity 2022.3.35f1 editor-only builder for the PF_Toon shader bundle.
//
// Normal use goes through Tools/PFViewShaderBundle/
// Build-PFViewToonBundles.ps1, which creates a temporary Unity project and
// calls this interface:
//
//   Unity.exe -batchmode -quit -projectPath <temporary project>
//       -executeMethod PFViewToonBundleBuilder.BuildAll
//       -pfViewOutput <temporary project>/PublishedBundles
//
// PF_Toon is the team-authored cel-shading asset (二分阴影 + 逐物体描边).
// Runtime code loads the active-platform bundle through PFViewToonShader and
// falls back to the Standard shader when the bundle is absent.
//
// This builder is a sibling of the outline builder and shares the same
// staging contract; it never touches the outline shader set.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class PFViewToonBundleBuilder
{
    private const string LogPrefix = "[PFViewToonBundle] ";
    private const string OutputArgument = "-pfViewOutput";

    private const string ShaderAssetPath =
        "Assets/Data/NewFrontierTeam.PioneerFramework/Materials/" +
        "PFView/PF_Toon.shader";

    private const string ShaderName = "PioneerFramework/Toon";

    private static readonly BundleTarget[] BundleTargets =
    {
        new BundleTarget(
            BuildTarget.StandaloneWindows64,
            "pfview_toon_win"),
        new BundleTarget(
            BuildTarget.StandaloneLinux64,
            "pfview_toon_linux"),
        new BundleTarget(
            BuildTarget.StandaloneOSX,
            "pfview_toon_mac")
    };

    [MenuItem("PFView/Build Toon Shader Bundles")]
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
        ValidateShaderAsset();

        string workingRoot = Path.Combine(projectRoot, "ShaderOutput");
        if (Directory.Exists(workingRoot))
        {
            Directory.Delete(workingRoot, true);
        }
        Directory.CreateDirectory(workingRoot);

        var builtBundles = new List<BuiltBundle>();
        foreach (BundleTarget bundleTarget in BundleTargets)
        {
            BuiltBundle builtBundle = BuildBundle(workingRoot, bundleTarget);
            builtBundles.Add(builtBundle);
            if (bundleTarget.Target == BuildTarget.StandaloneWindows64)
            {
                ValidateRuntimeLoad(builtBundle);
            }
        }

        Directory.CreateDirectory(outputRoot);
        foreach (BuiltBundle builtBundle in builtBundles)
        {
            string publishedPath = Path.Combine(outputRoot, builtBundle.Name);
            File.Copy(builtBundle.Path, publishedPath, true);

            string staleManifest = publishedPath + ".manifest";
            if (File.Exists(staleManifest))
            {
                File.Delete(staleManifest);
            }

            Debug.Log(LogPrefix + "Staged bundle: " + builtBundle.Name);
        }

        Debug.Log(LogPrefix + "All toon bundles completed.");
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
            bool found = Array.Exists(
                shaders,
                shader => shader != null && string.Equals(
                    shader.name,
                    ShaderName,
                    StringComparison.Ordinal));
            if (!found)
            {
                throw new InvalidOperationException(
                    "Runtime load omitted shader: " + ShaderName);
            }
        }
        finally
        {
            bundle.Unload(true);
        }

        Debug.Log(LogPrefix + "Runtime-loaded bundle: " + builtBundle.Name);
    }

    private static void ValidateShaderAsset()
    {
        AssetDatabase.Refresh(
            ImportAssetOptions.ForceSynchronousImport |
            ImportAssetOptions.ForceUpdate);

        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderAssetPath);
        if (shader == null)
        {
            throw new InvalidOperationException(
                "Shader asset could not be imported: " + ShaderAssetPath);
        }
        if (!string.Equals(shader.name, ShaderName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Shader name mismatch for " + ShaderAssetPath + ": " +
                shader.name);
        }
        if (ShaderUtil.ShaderHasError(shader))
        {
            throw new InvalidOperationException(
                "Shader has compilation errors: " + ShaderAssetPath);
        }

        Debug.Log(LogPrefix + "Imported shader: " + ShaderName);
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

        // Keep the per-target directory distinct from the requested bundle
        // name: Unity also emits a root manifest bundle named after
        // outputPath's final directory (see the outline tooling note).
        string targetOutput = Path.Combine(
            workingRoot,
            bundleTarget.Target.ToString());
        Directory.CreateDirectory(targetOutput);

        var build = new AssetBundleBuild
        {
            assetBundleName = bundleTarget.Name,
            assetNames = new[] { ShaderAssetPath }
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

        string bundlePath = Path.Combine(targetOutput, bundleTarget.Name);
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

        public BuiltBundle(BuildTarget target, string name, string path)
        {
            Target = target;
            Name = name;
            Path = path;
        }
    }
}
