using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PF_Toon（三渲二 shader，**团队调好的成品**）的运行时载入器。
    ///
    /// **为什么需要它**：Unity 的 shader 必须在编辑器里编译进 AssetBundle 游戏才能加载——
    /// mod 的 `Shaders/` 目录是散装源码，RimWorld 既不编译也不加载，所以 `Shader.Find` 恒失败、
    /// `ToonMat` 一直静默回退 Standard（完整版 MasterPlan 与 Lite 的 TODO M5-2 都记着这笔）。
    /// 本类取包走两条通道（照抄 outline provider 的实证做法）：
    ///   ① RimWorld 已保留的 mod AssetBundle（`1.6/AssetBundles/` 由框架自动加载）；
    ///   ② 按本程序集相邻路径 `1.6/AssetBundles/&lt;包名&gt;` 直接 LoadFromFile。
    /// 命中返回 shader；**取不到返回 null，调用方回退**（与从前行为一致，零风险）。
    ///
    /// 包由 `tools/PFViewShaderBundle/Build-PFViewToonBundles.ps1` 产出（需 Unity 2022.3.35f1）：
    /// 文件名 `pfview_toon_{win,linux,mac}`，内含 shader `PioneerFramework/Toon`。
    /// </summary>
    internal static class PFViewToonShader
    {
        internal const string ToonShaderName = "PioneerFramework/Toon";

        private const string ModPackageId = "NewFrontierTeam.PioneerFrameworkLite";

        private static bool resolved;
        private static Shader? shader;
        private static string? failure;

        /// <summary>取 toon shader；取不到返回 null（调用方回退 Standard）。只尝试一次并缓存结果。</summary>
        internal static Shader? TryGet()
        {
            if (resolved) return shader;
            resolved = true;
            shader = Resolve();
            return shader;
        }

        /// <summary>上一次失败原因（给日志；成功时为空）。</summary>
        internal static string? Failure => failure;

        private static string? BundleNameFor(RuntimePlatform platform)
        
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       => platform switch
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       {
            RuntimePlatform.WindowsPlayer or RuntimePlatform.WindowsEditor => "pfview_toon_win",
            RuntimePlatform.LinuxPlayer => "pfview_toon_linux",
            RuntimePlatform.OSXPlayer or RuntimePlatform.OSXEditor => "pfview_toon_mac",
            _ => null,
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       };

        private static Shader? Resolve()
        {
            string? bundleName = BundleNameFor(Application.platform);
            if (bundleName == null)
            {
                failure = "平台不支持：" + Application.platform;
                return null;
            }

            // 通道 ①：RimWorld 已保留的 mod 包（包在但 shader 读不出 ⇒ 记原因，继续走通道 ②）
            if (TryFromRetainedBundles(bundleName, out Shader? retained, out string? retainedReason))
                return retained;
            if (!string.IsNullOrEmpty(retainedReason))
                failure = retainedReason;

            // 通道 ②：DLL 相邻路径（1.6/Assemblies → ../AssetBundles/<bundle>）
            string path;
            try
            {
                string assemblyDir = Path.GetDirectoryName(
                    Path.GetFullPath(Assembly.GetExecutingAssembly().Location)) ?? ".";
                path = Path.GetFullPath(Path.Combine(assemblyDir, "..", "AssetBundles", bundleName));
            }
            catch (Exception exception)
            {
                failure = "包路径解析失败：" + exception.Message;
                return null;
            }

            if (!File.Exists(path))
            {
                failure = (failure ?? "") + "；包不存在：" + path;
                return null;
            }

            AssetBundle? bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    failure = "Unity 无法加载包：" + path;
                    return null;
                }

                Shader? found = FindToonShader(bundle.LoadAllAssets<Shader>());
                if (found == null)
                {
                    failure = "包内没有 shader：" + ToonShaderName;
                    return null;
                }

                // 不卸载：shader 资产由这个 bundle 持有（与 RimWorld 保留包同策）。
                bundle = null;
                failure = null;
                return found;
            }
            catch (Exception exception)
            {
                failure = "载入异常：" + exception.Message;
                return null;
            }
            finally
            {
                if (!ReferenceEquals(bundle, null))
                    bundle.Unload(false);
            }
        }

        /// <summary>在 RimWorld 已保留的 mod 包里找；返回 true = 找到 shader。</summary>
        private static bool TryFromRetainedBundles(string bundleName, out Shader? found, out string? reason)
        {
            found = null;
            reason = null;
            try
            {
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                {
                    if (!string.Equals(mod.PackageId, ModPackageId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (mod.assetBundles?.loadedAssetBundles == null)
                        return false;

                    foreach (AssetBundle bundle in mod.assetBundles.loadedAssetBundles)
                    {
                        if (ReferenceEquals(bundle, null)) continue;

                        string name;
                        try { name = bundle.name; }
                        catch (Exception exception)
                        {
                            reason = "包身份不可读：" + exception.Message;
                            continue;
                        }
                        if (!string.Equals(name, bundleName, StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            found = FindToonShader(bundle.LoadAllAssets<Shader>());
                            if (found == null)
                                reason = "RimWorld 已保留该包，但包内没有 " + ToonShaderName;
                            return !ReferenceEquals(found, null);
                        }
                        catch (Exception exception)
                        {
                            reason = "已保留包的 shader 读取失败：" + exception.Message;
                            return false;
                        }
                    }
                    return false;
                }
            }
            catch (Exception exception)
            {
                reason = "检查 RimWorld 已加载包表失败：" + exception.Message;
            }
            return false;
        }

        private static Shader? FindToonShader(Shader[]? shaders)
        {
            if (shaders == null) return null;
            for (int i = 0; i < shaders.Length; i++)
            {
                var s = shaders[i];
                if (s != null && string.Equals(s.name, ToonShaderName, StringComparison.Ordinal))
                    return s;
            }
            return null;
        }
    }
}
