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

        private static IEnumerable<(int Offset, MethodBase Method)> GetCalledMethodsWithOffsets(MethodBase method)
        {
            var body = method.GetMethodBody();
            if (body == null) yield break;

            var bytes = body.GetILAsByteArray();
            var module = method.Module;
            var typeArguments = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;

            for (int i = 0; i <= bytes.Length - 5; i++)
            {
                if (bytes[i] != 0x28 && bytes[i] != 0x6f && bytes[i] != 0x73)
                {
                    continue;
                }

                int token = BitConverter.ToInt32(bytes, i + 1);
                MethodBase resolved = null;
                try
                {
                    resolved = module.ResolveMethod(token, typeArguments, methodArguments);
                }
                catch
                {
                    // Ignore opcode bytes embedded in another operand.
                }
                if (resolved != null) yield return (i, resolved);
            }
        }

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
        public void RenderScene_ClampsCameraInTheFrameUsedToDrawBodies()
        {
            var renderScene = typeof(MainTabWindow_PFView).GetMethod("RenderScene", AllFlags);
            var calls = GetCalledMethods(renderScene).Select(call => call.Name).ToList();
            int frame = calls.IndexOf("UpdateViewFrame");
            int camera = calls.IndexOf("UpdateCamera");
            int draw = calls.IndexOf("DrawAllObjects");
            Assert.True(frame >= 0 && camera > frame && draw > camera,
                "先确定本帧参考系，再用同帧原点/时钟夹取相机，最后绘制天体。");

            var updateCamera = typeof(MainTabWindow_PFView).GetMethod("UpdateCamera", AllFlags);
            var cameraCalls = GetCalledMethods(updateCamera).Select(call => call.Name).ToList();
            Assert.DoesNotContain("RenderTick", cameraCalls);
            Assert.DoesNotContain("ResolveFocusOrigin", cameraCalls);
            Assert.Single(calls, name => name == "RenderTick");
        }

        [Fact]
        public void RenderScene_RendersTheStarOffscreenOnceThenTheMainCameraOnce()
        {
            var renderScene = typeof(MainTabWindow_PFView).GetMethod(
                "RenderScene", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(renderScene);

            // 用户已批准（S3-b）：每帧全窗口 Camera.Render == 2 —— RenderScene 直接调用主相机一次，
            // RenderStellarOffscreen 内部调用星体相机一次。旧"恰好一次"契约**显式改写**为"主相机一次 +
            // 星体离屏一次"，不是放宽成不检查。
            var calls = GetCalledMethodsWithOffsets(renderScene).ToList();
            var directRenders = calls
                .Where(call => call.Method.DeclaringType == typeof(Camera) && call.Method.Name == "Render")
                .ToList();
            Assert.Single(directRenders);

            var offscreenCalls = calls.Where(call => call.Method.Name == "RenderStellarOffscreen").ToList();
            Assert.Single(offscreenCalls);

            var offscreen = typeof(MainTabWindow_PFView).GetMethod(
                "RenderStellarOffscreen", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(offscreen);
            var offscreenBody = GetCalledMethods(offscreen).ToList();
            Assert.Single(offscreenBody, call => call.DeclaringType == typeof(Camera) && call.Name == "Render");

            // 顺序：DrawAllObjects → RenderStellarOffscreen → 主相机 Render。
            int draw = calls.FindIndex(call => call.Method.Name == "DrawAllObjects");
            int star = calls.FindIndex(call => call.Method.Name == "RenderStellarOffscreen");
            Assert.True(draw >= 0 && star > draw, "RenderStellarOffscreen must run after DrawAllObjects");
            Assert.True(directRenders[0].Offset > offscreenCalls[0].Offset,
                "the main camera must render after the offscreen star pass");
        }

        [Fact]
        public void WindowHasNoCameraRenderOutsideTheTwoSanctionedCalls()
        {
            // 任何新增的 Camera.Render 都必须显式过这条契约：只允许 RenderScene 一次（主相机）与
            // RenderStellarOffscreen 一次（星体离屏）；其余方法出现 Camera.Render 直接失败。
            var allowed = new Dictionary<string, int> { ["RenderScene"] = 1, ["RenderStellarOffscreen"] = 1 };
            foreach (var method in typeof(MainTabWindow_PFView).GetMethods(AllFlags))
            {
                if (method.GetMethodBody() == null) continue;
                int renders = GetCalledMethods(method)
                    .Count(call => call.DeclaringType == typeof(Camera) && call.Name == "Render");
                if (renders == 0) continue;
                Assert.True(allowed.TryGetValue(method.Name, out int expected) && renders == expected,
                    $"unexpected Camera.Render ({renders}) in {method.Name}; only RenderScene/RenderStellarOffscreen may render");
            }
        }

        [Fact]
        public void PFViewSource_ContainsNoOutlineIntegrationTokens()
        {
            // 2026-10-02：PFView 拆成 4 个 partial 文件（V-0 地基，见仓库根修缮方案）。
            // 扫描面必须覆盖全部四份——否则搬到新文件里的代码会掉出本判据射程（只许加覆盖，不许放软）。
            // 2026-10-11（D-1）：画船的绘制段抽到 `PFView/ShipModelRenderer.cs`（零件/朝向棒/浮雕）⇒ 一并扫。
            foreach (var file in new[]
            {
                "PFView/MainTabWindow_PFView.cs",
                "PFView/MainTabWindow_PFView.Render.cs",
                "PFView/MainTabWindow_PFView.Scene.cs",
                "PFView/MainTabWindow_PFView.ProjectileVisuals.cs",
                "PFView/MainTabWindow_PFView.ImpactVisuals.cs",
                "PFView/MainTabWindow_PFView.GUI.cs",
                "PFView/PFViewToonShader.cs",
                "PFView/ShipModelRenderer.cs"
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
        public void OutlineAssetBundleApi_IsAvailableFromResolvedGameReferences()
        {
            // Resolve the shipped API instead of depending on how the project spells its imports.
            Assert.Equal("UnityStubs", typeof(AssetBundle).Assembly.GetName().Name);
            Assert.NotNull(typeof(AssetBundle).GetMethod("LoadFromFile", new[] { typeof(string) }));
            Assert.NotNull(typeof(AssetBundle).GetMethod("Unload", new[] { typeof(bool) }));
        }
    }
}
