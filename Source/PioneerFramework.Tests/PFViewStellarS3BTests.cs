using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// **S3-b 星体半分辨率离屏的测试契约**（2026-11-20）。
///
/// 结构：星体 surface/halo 改到独立 <c>StarLayer</c>（29，与 <c>SceneLayer</c>=30 不同）由
/// <c>stellarCamera</c> 渲进半分辨率 <c>stellarRenderTexture</c>；主相机只画 SceneLayer，随后用
/// <c>PF_StellarComposite.frag.glsl</c> 的代理球按 gl_FragDepth 合成回主 RT。用户已明确批准
/// "RenderScene 每帧两次 Camera.Render"的契约变更（主相机一次 + 星体离屏一次），见
/// <c>PFViewOutlineIntegrationTests</c>。
///
/// 本类只做**结构/开关/生命周期**契约；默认关闭（<c>DefaultS3BEnabled=false</c>），默认观感
/// 仍是全分辨率 S3-a/legacy。只读测试；游戏内观感/帧时仍 UNCHECKED（C）。
/// </summary>
public class PFViewStellarS3BTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private static readonly string[] FrameUniformNames =
        { "_StellarEye", "_StellarTime", "_StellarParams", "_StellarDrift", "_StellarArchHeight0", "_StellarArchHeight1" };
    private static readonly int[] FrameUniformOffsets = { 0, 16, 32, 48, 64, 80 };

    private static string ShaderPath =>
        Path.Combine(AppContext.BaseDirectory, "Shaders", "PF_StellarComposite.frag.glsl");

    private static string ReadComposite()
    {
        Assert.True(File.Exists(ShaderPath), "S3-b composite shader is missing from the test output: " + ShaderPath);
        return File.ReadAllText(ShaderPath);
    }

    private static string RenderSource() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Source",
        "PioneerFramework", "Presentation", "PFView", "MainTabWindow_PFView.Render.cs"));

    private static string VisualsSource() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Source",
        "PioneerFramework", "Presentation", "PFView", "MainTabWindow_PFView.StellarVisuals.cs"));

    private static string SceneSource() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Source",
        "PioneerFramework", "Presentation", "PFView", "MainTabWindow_PFView.Scene.cs"));

    /// <summary>Extract one C# method body by brace matching (sufficient for these single-level methods).</summary>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "method not found in source: " + signature);
        int open = source.IndexOf('{', start);
        Assert.True(open > start);
        int depth = 0;
        for (int i = open; i < source.Length; ++i)
        {
            if (source[i] == '{') ++depth;
            else if (source[i] == '}')
            {
                if (--depth == 0) return source.Substring(open, i - open + 1);
            }
        }
        throw new InvalidOperationException("unbalanced braces after: " + signature);
    }

    private static int LayerConstant(string name)
    {
        foreach (Type type in new[] { typeof(MainTabWindow_PFView), typeof(PFViewLayers) })
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field != null && field.IsLiteral) return Convert.ToInt32(field.GetRawConstantValue());
        }
        throw new InvalidOperationException("layer constant not found: " + name);
    }

    [Fact]
    public void StarLayerIsTwentyNineAndDistinctFromTheSceneLayer()
    {
        // 星体层必须与场景层分离：主相机 cullingMask 只含 SceneLayer，星体相机只含 StarLayer。
        Assert.Equal(29, LayerConstant("StarLayer"));
        Assert.Equal(30, LayerConstant("SceneLayer"));
        Assert.NotEqual(LayerConstant("StarLayer"), LayerConstant("SceneLayer"));
    }

    [Fact]
    public void EnsureResourcesWiresTheStellarCameraAndExcludesTheStarLayerFromTheHostCamera()
    {
        string render = RenderSource();
        Assert.Contains("stellarCameraObject", render);
        Assert.Contains("stellarCamera", render);
        Assert.Matches(@"stellarCamera\.cullingMask\s*=\s*1\s*<<\s*StarLayer", render);
        // 宿主相机与主相机都不得收 StarLayer（否则星体画到主屏/重复绘制）。
        Assert.Matches(@"Find\.Camera\.cullingMask\s*&=\s*~\(1\s*<<\s*StarLayer\)", render);
        Assert.Matches(@"Find\.Camera\.cullingMask\s*&=\s*~\(1\s*<<\s*SceneLayer\)", render);
        Assert.Matches(@"renderCamera\.cullingMask\s*=\s*1\s*<<\s*SceneLayer", render);
        // 星体相机同样关闭自动捕获（只由 RenderStellarOffscreen 手动 Render）。
        Assert.Matches(@"stellarCamera\.enabled\s*=\s*false", render);
    }

    [Fact]
    public void HalfResolutionStellarTextureFollowsRecreateAndCleanup()
    {
        string render = RenderSource();

        // RecreateRenderTexture 把星体 RT 委托给 RecreateStellarRenderTexture(mainWidth, mainHeight)。
        string recreate = MethodBody(render, "private void RecreateRenderTexture()");
        Assert.Contains("RecreateStellarRenderTexture(", recreate);

        // 星体 RT = 主 RT 的半分辨率；宽取半、高按主 RT 宽高比取整（避免奇数高度拉伸）。
        string stellarRecreate = MethodBody(render, "private void RecreateStellarRenderTexture(int mainWidth, int mainHeight)");
        Assert.Contains("stellarRenderTexture", stellarRecreate);
        Assert.Matches(@"mainWidth\s*/\s*2", stellarRecreate);
        Assert.Contains("Mathf.RoundToInt", stellarRecreate);
        Assert.Contains("stellarRenderTexture.width == sw", stellarRecreate);
        Assert.Contains("stellarCamera.targetTexture = stellarRenderTexture", stellarRecreate);
        // 开关关闭时销毁并置空（完全回退旧路径）。
        Assert.Matches(@"if\s*\(\s*!PFViewStellarProbe\.S3BEnabled\s*\)", stellarRecreate);
        Assert.Contains("ReleaseStellarRenderTexture()", stellarRecreate);

        string release = MethodBody(render, "private void ReleaseStellarRenderTexture()");
        Assert.Contains("stellarCamera.targetTexture = null", release);
        Assert.Contains("stellarRenderTexture.Release()", release);
        Assert.Contains("Destroy(stellarRenderTexture)", release);
        Assert.Contains("stellarRenderTexture = null", release);

        string cleanup = MethodBody(render, "private void CleanupResources()");
        Assert.Contains("ReleaseStellarRenderTexture()", cleanup);
        Assert.Contains("Destroy(stellarCameraObject)", cleanup);
        Assert.Contains("stellarCamera = null", cleanup);
    }

    [Fact]
    public void CompositeShaderSamplesTheOffscreenStarTextureAndWritesIntegratedDepth()
    {
        string source = ReadComposite();
        Assert.Contains("vLocalPosition", source);
        Assert.Contains("gl_FragDepth", source);
        Assert.DoesNotContain("vUv", source);

        // 离屏星体贴图：binding 1；UV = gl_FragCoord / (2 * textureSize)（2× 下采样比）。
        Assert.Matches(@"layout\s*\(\s*set\s*=\s*2\s*,\s*binding\s*=\s*1\s*\)\s*uniform\s+sampler2D\s+_StellarStarTex\s*;", source);
        Assert.Contains("vec2 starUv()", source);
        Assert.Contains("textureSize(_StellarStarTex, 0)", source);
        Assert.Contains("gl_FragCoord.xy / (2.0 *", source);
        Assert.Contains("_StellarParams", source);

        // 深度近似（2026-11-20 第三版，最终）：surface 用 **1.12R 光球**（离线 legacy-64 扫描：.5-opacity
        // 边界在 r≈1.11–1.14R，face-on/limb 都成立；impact>R 时 max(...,0) 使公式退化为 -b=tangent）；
        // halo 用切点 `max(-b, 0.0001)`（不再用 1.65R 壳入口——入口会把日冕画到船前面）。
        // 旧 1.04R / 1.18R / 1.65R 公式全部退役，这里显式钉"不再出现"防回潮。
        Assert.Contains("const float PHOTO_DEPTH_R = 1.12;", source);
        Assert.Matches(@"float\s+t\s*=\s*-b\s*-\s*sqrt\s*\(\s*max\s*\(\s*(PHOTO_DEPTH_R\s*\*\s*PHOTO_DEPTH_R|1\.12\s*\*\s*1\.12)\s*-\s*impact\s*\*\s*impact\s*,\s*0\.0\s*\)\s*\)\s*;", source);
        Assert.Contains("float t = max(-b, 0.0001);", source);
        Assert.DoesNotContain("1.04 * 1.04", source);
        Assert.DoesNotContain("1.18 * 1.18", source);
        Assert.DoesNotContain("1.65 * 1.65", source);
        Assert.Contains("gl_FragDepth = clamp(clip.z / clip.w, 0.0, 1.0);", source);
    }

    [Fact]
    public void CompositeShaderPartitionsSurfaceAndHaloByThePassFlagAndAlpha()
    {
        string source = ReadComposite();
        Assert.Matches(@"uMaterial\._StellarParams\.y\s*>\s*\.5", source);
        // surface composite：只保留星体 RT 里 alpha 饱和（>=.999）的像素。
        Assert.Contains("if (star.a < .999) discard;", source);
        // halo composite：surface 拥有的像素（alpha>=.999）丢弃，只混合透明日冕。
        Assert.Contains("if (star.a >= .999) discard;", source);
        // halo 必须再次丢弃完全透明的像素（避免无谓混合/深度写入）。
        Assert.Matches(@"if\s*\(star\.a < 1e-4", source);
    }

    [Fact]
    public void CompositeShaderKeepsTheFrameUniformAndSingleStarTextureSlot()
    {
        string source = ReadComposite();
        string name = Path.GetFileName(ShaderPath);
        var layout = Waylith.Rendering.ShaderMetadataParser.ExtractMaterialLayout(source, name);
        Assert.False(layout.HasErrors);
        // 与恒星 shader 共用同一 96 B / 6×vec4 帧 uniform 布局（C# 侧同一套 SetVector）。
        Assert.Equal(96, layout.UniformSize);
        Assert.Equal(FrameUniformNames, layout.UniformSlots.Select(slot => slot.PropertyName));
        Assert.Equal(FrameUniformOffsets, layout.UniformSlots.Select(slot => slot.Offset));
        var slot = Assert.Single(layout.TextureSlots);
        Assert.Equal("_StellarStarTex", slot.PropertyName);
        Assert.Equal(1, slot.Binding);
        Assert.Equal(Waylith.Rendering.MaterialTextureKind.Combined, slot.Kind);
    }

    [Fact]
    public void S3BDefaultSwitchIsFrozenOff()
    {
        // 默认关闭：默认观感仍是 S3-a/legacy 全分辨率；S3-b 只是玩家显式选择的性能档。
        Assert.False(PFViewStellarProbe.DefaultS3BEnabled);
        Assert.Equal(PFViewStellarProbe.DefaultS3BEnabled, PFViewStellarProbe.S3BEnabled);
    }

    [Fact]
    public void CompositeSelectionReadsTheS3BSwitchAndHasAFallback()
    {
        // 选择链：EnsureStellarShaders 读 S3BEnabled，找两条 composite shader；缺失或星体 RT 未就绪
        // 时只置 stellarOffscreenActive=false 并告警，完全回退普通恒星路径。
        var method = typeof(MainTabWindow_PFView).GetMethod("EnsureStellarShaders", Flags);
        Assert.NotNull(method);
        var calls = PFViewUiIntegrationTests.ReferencedMembers(method).ToArray();
        Assert.Contains(calls, member => member.Name is "S3BEnabled" or "get_S3BEnabled");
        Assert.Contains(calls, member => member.Name == "Warning");

        string visuals = VisualsSource();
        Assert.Contains("Shader.Find(\"PF_StellarSurfaceComposite\")", visuals);
        Assert.Contains("Shader.Find(\"PF_StellarHaloComposite\")", visuals);
        Assert.Contains("stellarOffscreenActive = true", visuals);
        Assert.Contains("else if (stellarRenderTexture == null)", visuals);

        // 绘制侧：star draw 走 StarLayer；composite surface 立即排 SceneLayer；composite halo 在
        // DrawStellarHalos 里排 SceneLayer。
        Assert.Contains("Graphics.DrawMesh(mesh, matrix, surface, StarLayer, null, 0)", visuals);
        Assert.Contains("Graphics.DrawMesh(mesh, matrix, compSurface, SceneLayer, null, 0)", visuals);
        string halos = MethodBody(visuals, "private void DrawStellarHalos()");
        Assert.Contains("stellarCompositeHaloDraws", halos);
        Assert.Contains("SceneLayer", halos);
        Assert.Contains("StarLayer", halos);
    }

    [Fact]
    public void CompositeMaterialsAreCachedPerBodyAndBoundToTheStarTexture()
    {
        string visuals = VisualsSource();
        Assert.Contains("stellarCompositeSurfaceMaterials", visuals);
        Assert.Contains("stellarCompositeHaloMaterials", visuals);
        Assert.Contains("GetOrCreateCompositeSurfaceMaterial(", visuals);
        Assert.Contains("GetOrCreateCompositeHaloMaterial(", visuals);
        Assert.Contains("UpdateStellarCompositeTextures(", visuals);
        // 材质缓存命中 + 新 RT 重建后重绑纹理。
        string surfaceFactory = MethodBody(visuals, "private Material GetOrCreateCompositeSurfaceMaterial(int bodyId)");
        Assert.Contains("stellarCompositeSurfaceMaterials.TryGetValue(bodyId, out var existing)", surfaceFactory);
        Assert.Contains("material.SetTexture(\"_StellarStarTex\", stellarRenderTexture)", surfaceFactory);
        string update = MethodBody(visuals, "private void UpdateStellarCompositeTextures()");
        Assert.Contains("stellarCompositeSurfaceMaterials.Values", update);
        Assert.Contains("stellarCompositeHaloMaterials.Values", update);
    }

    [Fact]
    public void DebugWindowExposesTheS3BSwitch()
    {
        var method = typeof(Window_PFViewDebug).GetMethod("DoWindowContents", Flags);
        Assert.NotNull(method);
        var calls = PFViewUiIntegrationTests.ReferencedMembers(method).ToArray();
        int touches = calls.Count(member => member.Name is "S3BEnabled" or "get_S3BEnabled" or "set_S3BEnabled");
        Assert.True(touches >= 2, "PFView 调试窗必须暴露 S3-b 开关（读 + 写），实际引用次数：" + touches);
    }

    [Fact]
    public void CleanupReleasesTheCompositeMaterialsAndStellarCameraState()
    {
        string visuals = VisualsSource();
        string destroy = MethodBody(visuals, "private void DestroyStellarVisualResources()");
        Assert.Contains("stellarCompositeSurfaceMaterials", destroy);
        Assert.Contains("stellarCompositeSurfaceMaterials.Clear()", destroy);
        Assert.Contains("stellarCompositeHaloMaterials", destroy);
        Assert.Contains("stellarCompositeHaloMaterials.Clear()", destroy);
        Assert.Contains("stellarStarHaloDraws.Clear()", destroy);
        Assert.Contains("stellarCompositeHaloDraws.Clear()", destroy);
        Assert.Contains("stellarOffscreenActive = false", destroy);

        string perBody = MethodBody(visuals, "private void DestroyStellarVisualResourcesForBody(int handle)");
        Assert.Contains("DestroyOwnedResource(stellarCompositeSurfaceMaterials, handle)", perBody);
        Assert.Contains("DestroyOwnedResource(stellarCompositeHaloMaterials, handle)", perBody);

        string render = RenderSource();
        string cleanup = MethodBody(render, "private void CleanupResources()");
        Assert.Contains("ReleaseStellarRenderTexture()", cleanup);
        Assert.Contains("Destroy(stellarCameraObject)", cleanup);
    }

    [Fact]
    public void OffscreenModeSkipsTheStarSurfaceInTheMainRenderPass()
    {
        // S3-b 生效时，星体 surface 只由 stellarCamera 离屏渲染 + composite 合成；主相机 pass
        // 必须跳过星体 surface（否则同一恒星画两遍）。场景侧用 StellarOffscreenActive 做闸门。
        string scene = SceneSource();
        Assert.Contains("StellarOffscreenActive", scene);
        Assert.Matches(@"!StellarOffscreenActive\s*&&\s*PFViewStellarProbe\.SurfacePass", scene);
        Assert.Contains("RenderStellarOffscreen()", scene);
    }
}
