using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using Verse;

namespace PioneerFramework.PFView
{
    internal enum PFViewLoadedBundleLookupResult
    {
        NotRetained,
        Reused,
        RetainedButUnusable
    }

    /// <summary>
    /// Resolves the three PFView outline shaders from the platform-specific
    /// AssetBundle registered for the active RimWorld mod.
    /// </summary>
    /// <remarks>
    /// Create one provider for each owning PFView window with
    /// <see cref="TryLoad"/>, then pass it to
    /// <see cref="PFViewMaskOutlineRenderer"/>. RimWorld normally loads the
    /// bundle before mod classes initialize, so this provider reuses that
    /// instance and leaves its lifetime to <see cref="ModContentPack"/>. A
    /// direct file-load fallback is owned by this provider. In both cases the
    /// provider must outlive the renderer; dispose the renderer first.
    /// </remarks>
    /// <example>
    /// <code>
    /// PFViewOutlineAssetBundleProvider provider;
    /// string failureReason;
    /// if (PFViewOutlineAssetBundleProvider.TryLoad(
    ///     out provider,
    ///     out failureReason))
    /// {
    ///     IPFViewOutlineRenderer renderer =
    ///         new PFViewMaskOutlineRenderer(provider);
    ///
    ///     // Per frame: BeginFrame, Submit, Camera.Render, Composite.
    ///     // On window close: renderer.Dispose(), then provider.Dispose().
    /// }
    /// </code>
    /// </example>
    public sealed class PFViewOutlineAssetBundleProvider :
        IPFViewOutlineShaderProvider,
        IDisposable
    {
        private const string ModPackageId =
            "NewFrontierTeam.PioneerFramework";

        internal const string MaskShaderName =
            "Hidden/PioneerFramework/PFView/OutlineMask";
        internal const string DilateShaderName =
            "Hidden/PioneerFramework/PFView/OutlineDilate";
        internal const string CompositeShaderName =
            "Hidden/PioneerFramework/PFView/OutlineComposite";

        private AssetBundle? assetBundle;   // 契约改真（2026-10-15 手清）：清理路径会归零（约 430 行），可空
        private PFViewOutlineShaderSet shaderSet;
        private readonly bool ownsAssetBundle;
        private bool disposed;

        private PFViewOutlineAssetBundleProvider(
            AssetBundle assetBundle,
            PFViewOutlineShaderSet shaderSet,
            bool ownsAssetBundle)
        {
            this.assetBundle = assetBundle;
            this.shaderSet = shaderSet;
            this.ownsAssetBundle = ownsAssetBundle;
        }

        /// <summary>
        /// Reuses RimWorld's already loaded mod AssetBundle when available;
        /// otherwise loads the active-platform file directly. This ordering
        /// prevents Unity from rejecting a second load of the same bundle.
        /// </summary>
        /// <param name="provider">
        /// A provider for the resolved shader set on success; null on failure.
        /// </param>
        /// <param name="failureReason">
        /// A diagnostic suitable for a single PFView warning on failure.
        /// </param>
        /// <returns>True only when a complete shader set was loaded.</returns>
        public static bool TryLoad(
            out PFViewOutlineAssetBundleProvider provider,
            out string failureReason)
        {
            provider = null;

            if (!TryGetBundleName(
                Application.platform,
                out string bundleName))
            {
                failureReason =
                    $"PFView outline is unavailable on " +
                    $"{Application.platform}.";
                return false;
            }

            PFViewLoadedBundleLookupResult lookupResult =
                TryGetLoadedModShaderSet(
                bundleName,
                out AssetBundle modBundle,
                out PFViewOutlineShaderSet modShaderSet,
                out string lookupFailureReason);
            if (lookupResult ==
                PFViewLoadedBundleLookupResult.Reused)
            {
                provider = new PFViewOutlineAssetBundleProvider(
                    modBundle,
                    modShaderSet,
                    false);
                failureReason = string.Empty;
                return true;
            }

            if (!ShouldLoadFromFile(lookupResult))
            {
                failureReason = lookupFailureReason;
                return false;
            }

            string bundlePath;
            try
            {
                bundlePath = GetBundlePath(
                    typeof(PFViewOutlineAssetBundleProvider).Assembly.Location,
                    bundleName);
            }
            catch (Exception exception)
            {
                failureReason =
                    "Could not resolve the PFView outline AssetBundle path: " +
                    exception.Message;
                return false;
            }

            if (!File.Exists(bundlePath))
            {
                failureReason =
                    $"PFView outline AssetBundle was not found: {bundlePath}";
                return false;
            }

            AssetBundle? loadedBundle = null;
            try
            {
                loadedBundle = AssetBundle.LoadFromFile(bundlePath);
                if (ReferenceEquals(loadedBundle, null))
                {
                    failureReason =
                        $"Unity could not load the PFView outline " +
                        $"AssetBundle: {bundlePath}";
                    return false;
                }

                Shader[] shaders = loadedBundle.LoadAllAssets<Shader>();
                PFViewOutlineShaderSet resolvedSet = ResolveShaderSet(
                    shaders);

                if (!resolvedSet.IsComplete)
                {
                    failureReason =
                        "The PFView outline AssetBundle does not contain " +
                        "the complete mask, dilate, and composite shader set.";
                    return false;
                }

                provider = new PFViewOutlineAssetBundleProvider(
                    loadedBundle,
                    resolvedSet,
                    true);
                loadedBundle = null;
                failureReason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                failureReason =
                    $"Failed to load PFView outline shaders from " +
                    $"{bundlePath}: {exception.Message}";
                return false;
            }
            finally
            {
                if (!ReferenceEquals(loadedBundle, null))
                {
                    loadedBundle.Unload(false);
                }
            }
        }

        /// <summary>
        /// Searches only PioneerFramework bundles retained by RimWorld's mod
        /// loader. The returned bundle remains owned by RimWorld and must not
        /// be unloaded by this provider.
        /// </summary>
        private static PFViewLoadedBundleLookupResult
            TryGetLoadedModShaderSet(
            string bundleName,
            out AssetBundle resolvedBundle,
            out PFViewOutlineShaderSet resolvedSet,
            out string failureReason)
        {
            resolvedBundle = null;
            resolvedSet = default;
            failureReason = string.Empty;

            try
            {
                foreach (ModContentPack mod in
                    LoadedModManager.RunningModsListForReading)
                {
                    if (!string.Equals(
                        mod.PackageId,
                        ModPackageId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (mod.assetBundles?.loadedAssetBundles == null)
                    {
                        return PFViewLoadedBundleLookupResult.NotRetained;
                    }

                    string? identityFailure = null;
                    foreach (AssetBundle bundle in
                        mod.assetBundles.loadedAssetBundles)
                    {
                        if (bundle == null)
                        {
                            continue;
                        }

                        string retainedBundleName;
                        try
                        {
                            retainedBundleName = bundle.name;
                        }
                        catch (Exception exception)
                        {
                            identityFailure =
                                "Could not inspect a RimWorld-retained " +
                                "AssetBundle identity: " +
                                exception.Message;
                            continue;
                        }

                        if (!string.Equals(
                            retainedBundleName,
                            bundleName,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        try
                        {
                            PFViewOutlineShaderSet candidateSet =
                                ResolveShaderSet(
                                    bundle.LoadAllAssets<Shader>());
                            if (!candidateSet.IsComplete)
                            {
                                failureReason =
                                    "RimWorld retained the PFView outline " +
                                    "AssetBundle, but its mask, dilate, " +
                                    "and composite shader set is incomplete.";
                                return PFViewLoadedBundleLookupResult.
                                    RetainedButUnusable;
                            }

                            resolvedBundle = bundle;
                            resolvedSet = candidateSet;
                            return PFViewLoadedBundleLookupResult.Reused;
                        }
                        catch (Exception exception)
                        {
                            failureReason =
                                "RimWorld retained the PFView outline " +
                                "AssetBundle, but its shaders could not be " +
                                "read: " + exception.Message;
                            return PFViewLoadedBundleLookupResult.
                                RetainedButUnusable;
                        }
                    }

                    if (!string.IsNullOrEmpty(identityFailure))
                    {
                        failureReason = identityFailure;
                        return PFViewLoadedBundleLookupResult.
                            RetainedButUnusable;
                    }

                    return PFViewLoadedBundleLookupResult.NotRetained;
                }
            }
            catch (Exception exception)
            {
                failureReason =
                    "Could not inspect RimWorld's loaded AssetBundle " +
                    "registry safely: " + exception.Message;
                return PFViewLoadedBundleLookupResult.
                    RetainedButUnusable;
            }

            return PFViewLoadedBundleLookupResult.NotRetained;
        }

        /// <summary>
        /// Permits a direct file load only after the RimWorld registry proves
        /// that the platform Bundle is not already retained. This prevents an
        /// uncertain or unusable retained Bundle from being loaded twice.
        /// </summary>
        internal static bool ShouldLoadFromFile(
            PFViewLoadedBundleLookupResult lookupResult)
        {
            return lookupResult ==
                PFViewLoadedBundleLookupResult.NotRetained;
        }

        /// <summary>
        /// Returns the compiled bundle name for a supported Unity desktop
        /// platform. Kept separate from loading so mapping behavior remains
        /// testable without starting Unity's render loop.
        /// </summary>
        internal static bool TryGetBundleName(
            RuntimePlatform platform,
            out string bundleName)
        {
            switch (platform)
            {
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                    bundleName = "pfview_outline_win";
                    return true;
                case RuntimePlatform.LinuxEditor:
                case RuntimePlatform.LinuxPlayer:
                    bundleName = "pfview_outline_linux";
                    return true;
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                    bundleName = "pfview_outline_mac";
                    return true;
                default:
                    bundleName = string.Empty;
                    return false;
            }
        }

        /// <summary>
        /// Resolves `1.6/AssetBundles/&lt;bundle&gt;` from an assembly located
        /// in `1.6/Assemblies`.
        /// </summary>
        internal static string GetBundlePath(
            string assemblyLocation,
            string bundleName)
        {
            if (string.IsNullOrWhiteSpace(assemblyLocation))
            {
                throw new ArgumentException(
                    "An assembly location is required.",
                    nameof(assemblyLocation));
            }

            if (string.IsNullOrWhiteSpace(bundleName))
            {
                throw new ArgumentException(
                    "An AssetBundle name is required.",
                    nameof(bundleName));
            }

            string? assemblyDirectory = Path.GetDirectoryName(
                Path.GetFullPath(assemblyLocation));
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                throw new InvalidOperationException(
                    "The assembly directory could not be resolved.");
            }

            return Path.GetFullPath(Path.Combine(
                assemblyDirectory,
                "..",
                "AssetBundles",
                bundleName));
        }

        /// <inheritdoc />
        public bool TryGetShaderSet(
            out PFViewOutlineShaderSet result,
            out string failureReason)
        {
            result = shaderSet;
            if (disposed)
            {
                failureReason =
                    "The PFView outline AssetBundle provider was disposed.";
                return false;
            }

            if (!shaderSet.IsComplete)
            {
                failureReason =
                    "The PFView outline shader set is incomplete.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }

        /// <summary>
        /// Releases a direct file-load fallback after the renderer and its
        /// materials are disposed. RimWorld-owned bundles remain registered
        /// with the mod content pack. Repeated calls are safe.
        /// </summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            shaderSet = default;

            AssetBundle? bundleToUnload = assetBundle;
            assetBundle = null;
            if (ownsAssetBundle &&
                !ReferenceEquals(bundleToUnload, null))
            {
                bundleToUnload.Unload(false);
            }
        }

        private static PFViewOutlineShaderSet ResolveShaderSet(
            Shader[] shaders)
        {
            return new PFViewOutlineShaderSet(
                FindShader(shaders, MaskShaderName),
                FindShader(shaders, DilateShaderName),
                FindShader(shaders, CompositeShaderName));
        }

        private static Shader? FindShader(
            Shader[] shaders,
            string shaderName)
        {
            if (shaders == null)
            {
                return null;
            }

            foreach (Shader shader in shaders)
            {
                if (!ReferenceEquals(shader, null) &&
                    string.Equals(
                        shader.name,
                        shaderName,
                        StringComparison.Ordinal))
                {
                    return shader;
                }
            }

            return null;
        }
    }
}
