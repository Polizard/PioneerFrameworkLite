using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// **S3-a v1 可选加速路径的测试契约**（2026-11-07；Lead 冻结的结构）。
///
/// S3-a v1 **不是**"把平滑项解析化后把 64 步缩到 12–16"：它保留 legacy 的 64 步上限与密集区采样网格，
/// 收益来自"跳过 r&gt;OUTER 的外层日冕 + 外层 3 点粗积分 + 可选 surface cull"。因此本类钉死：
/// · 对象空间采样 + 真实深度（vLocalPosition / gl_FragDepth，无 vUv）；
/// · legacy 观感锚点全部逐字保留：granulation / prominences / 8 拱常量表 / square·cube·fifth /
///   热缓存 prologue（`cachedHeatTile`/`cachedSurfaceHeat`、`_StellarEye.w > .5`）；
/// · 96 B / 6×vec4 帧 uniform 布局、唯一 `_StellarHeatCache` 纹理槽（binding 1, Combined）、无新 uniform；
/// · 64 步上限 + `clamp(..., 4.0, 64.0)` 预算仍在（优化不许靠缩循环上限）；
/// · 新常量 `OUTER`/`OUTER_STEP`/`SURFACE_CULL_IMPACT` 与三个稳定 helper `stellarStep`/`skipOuterEntry`/`integrateCoarse`；
/// · 两 pass 分区语义（`haloPass == opaque` discard）与 `float phase` 之前的 early-discard guard
///   （同 12 组射线等价用例在 `PFViewStellarVisualTests`）；
/// · 选择链：Fast（显式视觉）&gt; S3A &gt; legacy，S3A 缺失回退 legacy；调试窗可读可写开关。
///
/// 证据/脚本：`.zcode/s3a/contracts/`（原生编译 + 契约矩阵）。游戏内观感 A/B 与帧时仍为 UNCHECKED。
/// </summary>
public class PFViewStellarS3ATests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    /// <summary>
    /// S3-a 默认开关的冻结值：离线保真度验证 PASS ⇒ true，FAIL ⇒ false。
    /// TODO(Lead): 离线验证出结论后把本断言与 `PFViewStellarPerfProbeTests` 中的默认值一起冻结。
    /// </summary>
    private const bool ExpectedS3ADefault = true;

    private static readonly string[] FrameUniformNames =
        { "_StellarEye", "_StellarTime", "_StellarParams", "_StellarDrift", "_StellarArchHeight0", "_StellarArchHeight1" };
    private static readonly int[] FrameUniformOffsets = { 0, 16, 32, 48, 64, 80 };

    private static string ShaderPath =>
        Path.Combine(AppContext.BaseDirectory, "Shaders", "PF_StellarVolumeS3A.frag.glsl");

    private static string ReadS3A()
    {
        Assert.True(File.Exists(ShaderPath), "S3-a shader is missing from the test output: " + ShaderPath);
        return File.ReadAllText(ShaderPath);
    }

    [Fact]
    public void S3AShaderSamplesObjectSpaceAndWritesIntegratedDepth()
    {
        string source = ReadS3A();
        Assert.Contains("vLocalPosition", source);
        Assert.Contains("gl_FragDepth", source);
        Assert.Contains("granulation", source);
        Assert.Contains("prominences", source);
        Assert.DoesNotContain("vUv", source);
        // 两 pass 分区语义是"surface/halo 各占一半观感"的载体，S3-a 只许改积分/跳过，不许改分区。
        Assert.Matches(@"haloPass\s*=\s*uMaterial\._StellarParams\.y\s*>\s*\.5", source);
        Assert.Matches(@"if\s*\(\s*haloPass\s*==\s*opaque\s*\)\s*discard\s*;", source);
    }

    [Fact]
    public void S3AMarchKeepsTheLegacyStepGridAndAddsTheBoundedOuterSkip()
    {
        string source = ReadS3A();

        // ① 保留 legacy 的 64 步安全上限与预算钳制：S3-a v1 的收益不许来自缩小循环上限。
        Assert.Contains("for (int i = 0; i < 64; ++i)", source);
        Assert.Contains("clamp(uMaterial._StellarParams.x, 4.0, 64.0)", source);
        foreach (Match loop in Regex.Matches(source, @"for\s*\(\s*int\s+\w+\s*=\s*0\s*;\s*\w+\s*<\s*(\d+)\s*;"))
            Assert.InRange(int.Parse(loop.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1, 64);

        // ② 新常量（2026-11-07 Lead 最终冻结；唯一值，不许再放宽成候选集）：
        //    OUTER=1.42 外层跳过阈值、OUTER_STEP=.12 外层粗步长；
        //    SURFACE_CULL_IMPACT=1.22。原候选 1.19 被 validator 加密复扫否决
        //    （1.19 边界 max opacity=0.9086 ≥ 0.90 安全门），1.42 只是"关 cull"的等价语义、不再是合法冻结值。
        Assert.Matches(@"const\s+float\s+OUTER\s*=\s*1\.42\s*;", source);
        Assert.Matches(@"const\s+float\s+OUTER_STEP\s*=\s*\.12\s*;", source);
        Assert.Matches(@"const\s+float\s+SURFACE_CULL_IMPACT\s*=\s*1\.22\s*;", source);

        // ③ 三个稳定 helper：legacy 同款步长 + 相位对齐跳过 lead-in + 3 点粗积分（Lead 冻结的 helper 名）。
        Assert.Contains("stellarStep(", source);
        Assert.Contains("integrateCoarse(", source);
        Assert.Matches(@"float\s+skipOuterEntry\s*\(\s*vec3\s+eye\s*,\s*vec3\s+ray\s*,\s*float\s+start\s*,\s*float\s+baseStep\s*,\s*float\s+impact\s*\)", source);
        Assert.Matches(@"float\s+tIn\s*=\s*skipOuterEntry\s*\(\s*eye\s*,\s*ray\s*,\s*start\s*,\s*baseStep\s*,\s*impact\s*\)", source);
        Assert.Contains("max(impact < 1.10 ? .019 : .041, span / budget)", source);

        // ④ 外层跳过：相位对齐推进 lead-in（tIn += baseStep 直到进入 OUTER），lead-in 与尾段都走 3 点粗积分；
        //    impact > OUTER 的射线整段用 OUTER_STEP。S3-a v1 不引入解析光球边界（那是被否的其它方案）。
        Assert.Matches(@"tIn\s*\+=\s*baseStep\s*;", source);
        Assert.Matches(@"length\s*\(\s*eye\s*\+\s*tIn\s*\*\s*ray\s*\)\s*<=\s*OUTER", source);
        Assert.Matches(@"integrateCoarse\s*\(\s*eye\s*,\s*ray\s*,\s*start\s*,\s*tIn", source);
        Assert.Matches(@"integrateCoarse\s*\(\s*eye\s*,\s*ray\s*,\s*t\s*,\s*end", source);
        Assert.Matches(@"impact\s*>\s*OUTER\s*\?\s*OUTER_STEP\s*:\s*baseStep", source);
        Assert.DoesNotContain("photosphereHit(", source);
        Assert.DoesNotContain("gaussIntegral(", source);
        Assert.DoesNotContain("coronaIntegral(", source);

        // ⑤ 可选 surface cull 的 guard 放在 `float phase` 之后（不进入 legacy guard 的 12 组射线契约）。
        Assert.Matches(@"if\s*\(\s*!haloPass\s*&&\s*impact\s*>\s*SURFACE_CULL_IMPACT\s*\)\s*discard\s*;", source);
    }

    [Fact]
    public void S3AShaderResolvesTheSameFrameUniformLayoutInTheFragmentStage()
    {
        string source = ReadS3A();
        string name = Path.GetFileName(ShaderPath);
        var layout = Waylith.Rendering.ShaderMetadataParser.ExtractMaterialLayout(source, name);
        Assert.False(layout.HasErrors);
        Assert.Equal(96, layout.UniformSize);
        Assert.Equal(FrameUniformNames, layout.UniformSlots.Select(slot => slot.PropertyName));
        Assert.Equal(FrameUniformOffsets, layout.UniformSlots.Select(slot => slot.Offset));
        Assert.All(layout.UniformSlots, slot => Assert.Equal(Waylith.Rendering.MaterialUniformType.Vector4, slot.Type));
    }

    [Fact]
    public void S3AShaderKeepsTheSingleHeatCacheTextureSlotAndTheLegacyPrologue()
    {
        string source = ReadS3A();
        Assert.Contains("uniform sampler2D _StellarHeatCache", source);
        Assert.Contains("cachedHeatTile(", source);
        Assert.Contains("cachedSurfaceHeat(", source);
        Assert.Contains("uMaterial._StellarEye.w > .5", source);
        Assert.Contains("granulation(photoQ * 49.0", source);
        Assert.Contains(".59 + .48 * cells + .09 * noise(photoQ * 130.0 + photoWarp * 2.0)", source);
        Assert.Contains("noise(photoQ * 7.0 + drift.yzx + 7.0)", source);
        Assert.Contains("noise(photoQ * 7.0 + drift.zxy + 9.0)", source);
        Assert.Contains("gl_FragDepth", source);

        var layout = Waylith.Rendering.ShaderMetadataParser.ExtractMaterialLayout(source, Path.GetFileName(ShaderPath));
        Assert.False(layout.HasErrors);
        var slot = Assert.Single(layout.TextureSlots);
        Assert.Equal("_StellarHeatCache", slot.PropertyName);
        Assert.Equal(1, slot.Binding);
        Assert.Equal(Waylith.Rendering.MaterialTextureKind.Combined, slot.Kind);
    }

    [Fact]
    public void S3ADefaultSwitchIsFrozen()
    {
        Assert.Equal(ExpectedS3ADefault, PFViewStellarProbe.DefaultS3AEnabled);
    }

    [Fact]
    public void EnsureStellarShadersPrefersFastThenS3AThenLegacyWithFallbacks()
    {
        var method = typeof(MainTabWindow_PFView).GetMethod("SelectStellarShaders", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var calls = PFViewUiIntegrationTests.ReferencedMembers(method).ToArray();
        // 选择链必须读两个开关：显式 Fast 视觉 > S3-a（默认）> legacy。
        Assert.Contains(calls, member => member.Name is "FastRendererEnabled" or "get_FastRendererEnabled");
        Assert.Contains(calls, member => member.Name is "S3AEnabled" or "get_S3AEnabled");
        // 缺失回退必须留告警（与 Fast 缺失回退同一处置）。
        Assert.Contains(calls, member => member.Name == "Warning");
        // 材质选择不得控制相机（S3-a 仍走普通天体相机，测试钉死）。
        Assert.DoesNotContain(calls, member => member.Name is
            "FocusBody" or "InitializeSunCameraPose" or "get_IsStarFocus" or "set_position" or "set_rotation" or "set_fieldOfView");

        // 六个 shader 名字必须真的出现在选择方法所在文件里（Shader.Find 的字面量无法从 IL 还原）。
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Source", "PioneerFramework", "PFView",
            "MainTabWindow_PFView.StellarVisuals.cs"));
        foreach (string name in new[]
                 {
                     "PF_StellarSurface", "PF_StellarHalo",
                     "PF_StellarSurfaceFast", "PF_StellarHaloFast",
                     "PF_StellarSurfaceS3A", "PF_StellarHaloS3A"
                 })
            Assert.Contains("Shader.Find(\"" + name + "\")", source);
    }

    [Fact]
    public void DebugWindowExposesTheS3ASwitch()
    {
        var method = typeof(Window_PFViewDebug).GetMethod("DoWindowContents", Flags);
        Assert.NotNull(method);
        var calls = PFViewUiIntegrationTests.ReferencedMembers(method).ToArray();
        // 复选框既要读开关（显示当前值）也要写开关（点击生效）；字段访问解析为 FieldInfo，
        // 属性访问解析为 get_/set_，两种实现都接受。
        int touches = calls.Count(member => member.Name is "S3AEnabled" or "get_S3AEnabled" or "set_S3AEnabled");
        Assert.True(touches >= 2, "PFView 调试窗必须暴露 S3A 开关（读 + 写），实际引用次数：" + touches);
    }

    [Fact]
    public void S1KeepsTheBoundedProminenceRejectAndTheHaloFarPhotospherePruning()
    {
        string source = ReadS3A();

        // S1(d): prominences 每拱廉价外接剪枝（解析上界每拱 1.06e-8、8 拱合计 8.44e-8；实测 max |Δdensity| 6.2e-9）。
        // 只许"continue"跳过 tube 计算，不许改 tube/wisps 公式本身。
        int prom = source.IndexOf("float prominences(vec3 p, float phase)", StringComparison.Ordinal);
        Assert.True(prom >= 0, "prominences() is missing");
        int promEnd = source.IndexOf("// ── S3-a constants", prom, StringComparison.Ordinal);
        Assert.True(promEnd > prom, "prominences() body boundary not found");
        string promBody = source.Substring(prom, promEnd - prom);
        Assert.Matches(@"if\s*\(\s*abs\s*\(\s*across\s*\)\s*>\s*\.15\s*\|\|\s*arch\s*<\s*\.40\s*\|\|\s*arch\s*>\s*1\.60\s*\)\s*continue\s*;", promBody);
        Assert.Contains("float tube = exp(-square((arch - 1.0) / .14) - square(across / .032));", promBody);
        Assert.Contains("float wisps = .65 + .35 * sin(along * 97.0 + phase * 19.0 + binormal.w);", promBody);

        // S1(b): halo 远区剪枝条件 —— 可证上界 height <= 0.0305 + 0.15*filament，再留 0.2412 余量；
        // 只对 halo pass 生效（surface pass 的密集区采样网格/相位必须保持 legacy）。
        Assert.Contains("0.0305 + 0.15 * filament", source);
        Assert.Contains("+ 0.2412", source);
        Assert.Contains("if (haloPass && r > 1.0 + (0.0305 + 0.15 * filament) + 0.2412)", source);

        // far 分支：只保留 warp+stream（7 noise）+ corona（+ farLoops），跳过 convection/gran/fine（6 noise）
        // 与 photosphere/chromosphere 混合，且必须返回已批准的 coronaColor。用切片断言防止只改注释不改行为。
        int branch = source.IndexOf("if (haloPass && r > 1.0 + (0.0305 + 0.15 * filament) + 0.2412)", StringComparison.Ordinal);
        int farEnd = source.IndexOf("float convection", branch, StringComparison.Ordinal);
        Assert.True(branch >= 0 && farEnd > branch, "halo far branch not found");
        string far = source.Substring(branch, farEnd - branch);
        Assert.Contains("vec3(4.6,1.10,.13)", far);
        Assert.Contains("return StellarSample(", far);
        Assert.Contains("farLoops", far);
        Assert.DoesNotContain("noise(q * 66.0", far);    // gran
        Assert.DoesNotContain("noise(q * 173.0", far);   // fine
        Assert.DoesNotContain("convection", far);
        Assert.DoesNotContain("photosphere", far);

        // far 剪枝需要把 haloPass 传进单样本/粗积分路径（否则 surface pass 会被误剪）。
        Assert.Matches(@"StellarSample\s+sampleStellar\s*\(\s*vec3\s+p\s*,\s*vec3\s+ray\s*,\s*float\s+phase\s*,\s*vec3\s+drift\s*,\s*float\s+surfaceHeat\s*,\s*bool\s+haloPass\s*\)", source);
        Assert.True(Regex.Matches(source, @"sampleStellar\s*\(\s*p\s*,\s*ray\s*,\s*phase\s*,\s*drift\s*,\s*surfaceHeat\s*,\s*haloPass\s*\)").Count >= 2,
            "sampleStellar(..., haloPass) must be threaded from both the coarse and fine march paths");
    }

    [Fact]
    public void CleanupResetsTheS3AShaderStateSoReopeningTheTabReSelects()
    {
        // 材质/ shader 按 body 缓存，切换开关只在重开 PFView 后生效 ⇒ 清理必须把 S3A 标志清零，
        // 否则重开页签会沿用旧选择（与 stellarShadersAreFast 同处置）。
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Source", "PioneerFramework", "PFView",
            "MainTabWindow_PFView.StellarVisuals.cs"));
        Assert.Matches(@"DestroyStellarVisualResources[\s\S]*?stellarShadersAreS3A\s*=\s*false\s*;", source);
    }
}
