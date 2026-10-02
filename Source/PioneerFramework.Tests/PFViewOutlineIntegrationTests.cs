using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PioneerFramework.PFView;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    public class PFViewOutlineIntegrationTests
    {
        private const BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static string SourcePath(string relative) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Source", "PioneerFramework", relative);

        private static IEnumerable<MethodBase> GetCalledMethods(MethodBase method)
        {
            var body = method.GetMethodBody();
            if (body == null)
            {
                return Enumerable.Empty<MethodBase>();
            }

            var bytes = body.GetILAsByteArray();
            var module = method.Module;
            var typeArguments = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
            var called = new List<MethodBase>();

            for (int i = 0; i <= bytes.Length - 5; i++)
            {
                if (bytes[i] != 0x28 && bytes[i] != 0x6f && bytes[i] != 0x73)
                {
                    continue;
                }

                int token = BitConverter.ToInt32(bytes, i + 1);
                try
                {
                    var resolved = module.ResolveMethod(token, typeArguments, methodArguments);
                    if (resolved != null)
                    {
                        called.Add(resolved);
                    }
                }
                catch
                {
                    // Ignore opcode bytes embedded in another operand.
                }
            }

            return called;
        }

        [Fact]
        public void ReservedTypes_ImplementRequiredInterfaces()
        {
            Assert.True(typeof(IPFViewOutlineShaderProvider).IsAssignableFrom(
                typeof(PFViewOutlineAssetBundleProvider)));
            Assert.True(typeof(IPFViewOutlineRenderer).IsAssignableFrom(
                typeof(PFViewMaskOutlineRenderer)));
            Assert.True(typeof(IDisposable).IsAssignableFrom(
                typeof(PFViewOutlineAssetBundleProvider)));
            Assert.NotNull(typeof(PFViewOutlineProjection));
        }

        [Fact]
        public void BundleNameMapping_MatchesCommittedAssetBundles()
        {
            Assert.True(PFViewOutlineAssetBundleProvider.TryGetBundleName(
                RuntimePlatform.WindowsPlayer, out string win));
            Assert.Equal("pfview_outline_win", win);

            Assert.True(PFViewOutlineAssetBundleProvider.TryGetBundleName(
                RuntimePlatform.LinuxPlayer, out string linux));
            Assert.Equal("pfview_outline_linux", linux);

            Assert.True(PFViewOutlineAssetBundleProvider.TryGetBundleName(
                RuntimePlatform.OSXPlayer, out string mac));
            Assert.Equal("pfview_outline_mac", mac);
        }

        [Fact]
        public void PFView_DoesNotOwnOutlineState()
        {
            var outlineStateTypes = new[]
            {
                typeof(IPFViewOutlineRenderer),
                typeof(IPFViewOutlineShaderProvider),
                typeof(PFViewOutlineAssetBundleProvider)
            };

            foreach (var field in typeof(MainTabWindow_PFView).GetFields(AllFlags))
            {
                Assert.False(
                    outlineStateTypes.Any(t => t.IsAssignableFrom(field.FieldType)),
                    $"PFView must not own outline state but has field {field.Name} of type {field.FieldType}");
            }
        }

        [Fact]
        public void PFView_DoesNotCallReservedOutlineModule()
        {
            foreach (var method in typeof(MainTabWindow_PFView).GetMethods(AllFlags))
            {
                var outlineCalls = GetCalledMethods(method)
                    .Where(call => call.DeclaringType != null &&
                        call.DeclaringType.Namespace == "PioneerFramework.PFView")
                    .Select(call => $"{method.Name} -> {call.DeclaringType.Name}.{call.Name}")
                    .ToList();

                Assert.True(
                    outlineCalls.Count == 0,
                    $"PFView must not call the reserved outline module: {string.Join(", ", outlineCalls)}");
            }
        }

        [Fact]
        public void DrawRuntimeParts_RemainsStatic()
        {
            var method = typeof(MainTabWindow_PFView).GetMethod(
                "DrawRuntimeParts", AllFlags);
            Assert.NotNull(method);
            Assert.True(method.IsStatic, "DrawRuntimeParts must be static.");
        }

        [Fact]
        public void RenderScene_KeepsExactlyOneDirectCameraRenderCall()
        {
            var renderScene = typeof(MainTabWindow_PFView).GetMethod(
                "RenderScene", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(renderScene);

            var cameraRenderCalls = GetCalledMethods(renderScene)
                .Where(call =>
                    call.DeclaringType == typeof(Camera) &&
                    call.Name == "Render")
                .ToList();

            Assert.Single(cameraRenderCalls);
        }

        [Fact]
        public void PFViewSource_ContainsNoOutlineIntegrationTokens()
        {
            // 2026-10-02：PFView 拆成 4 个 partial 文件（V-0 地基，见仓库根修缮方案）。
            // 扫描面必须覆盖全部四份——否则搬到新文件里的代码会掉出本判据射程（只许加覆盖，不许放软）。
            foreach (var file in new[]
            {
                "PFView/MainTabWindow_PFView.cs",
                "PFView/MainTabWindow_PFView.Render.cs",
                "PFView/MainTabWindow_PFView.Scene.cs",
                "PFView/MainTabWindow_PFView.GUI.cs",
                "PFView/PFViewToonShader.cs"
            })
            {
                string source = File.ReadAllText(SourcePath(file));
                foreach (var forbidden in new[]
                {
                    "PioneerFramework.PFView",
                    "outlineShaderProvider",
                    "outlineRenderer",
                    "InitializeShipOutlineResources",
                    "BeginShipOutlineFrame",
                    "SubmitShipOutline",
                    "CompleteShipOutlineFrame",
                    "CleanupShipOutlineResources"
                })
                {
                    Assert.False(source.Contains(forbidden), $"{file} still contains outline integration token: {forbidden}");
                }
            }
        }

        [Fact]
        public void OutlineContractsAndProviderSource_KeepUsageDocumentation()
        {
            string contracts = File.ReadAllText(SourcePath("PFView/ToonOutline/PFViewOutlineContracts.cs"));
            string provider = File.ReadAllText(SourcePath("PFView/ToonOutline/PFViewOutlineAssetBundleProvider.cs"));

            foreach (var token in new[]
            {
                "<example>",
                "BeginFrame",
                "Submit",
                "Camera.Render",
                "Composite",
                "Dispose"
            })
            {
                Assert.True(contracts.Contains(token), $"Outline interface usage documentation is missing: {token}");
            }

            Assert.True(provider.Contains("<example>") && provider.Contains("TryLoad"),
                "AssetBundle provider usage documentation must remain in source.");
        }

        [Fact]
        public void ContractsExample_DrawsSameCameraAndSubmesh()
        {
            string contracts = File.ReadAllText(SourcePath("PFView/ToonOutline/PFViewOutlineContracts.cs"));
            string exampleText = string.Join("\n",
                contracts.Split('\n').Select(line => line.TrimStart().StartsWith("///") ? line.TrimStart().Substring(3) : line));

            const string pattern =
                @"Graphics\.DrawMesh\(\s*part\.Mesh,\s*part\.Matrix,\s*" +
                @"part\.Material,\s*sceneLayer,\s*renderCamera,\s*" +
                @"part\.SubMeshIndex\s*\);";

            Assert.Matches(pattern, exampleText);
        }

        [Fact]
        public void Project_RetainsAssetBundleModuleReference()
        {
            string project = File.ReadAllText(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Source", "PioneerFramework", "PioneerFramework.csproj"));

            Assert.Contains("Include=\"UnityEngine.AssetBundleModule\"", project);
            Assert.Contains("Libs\\Unity\\UnityEngine.AssetBundleModule.dll", project);
        }
    }
}
