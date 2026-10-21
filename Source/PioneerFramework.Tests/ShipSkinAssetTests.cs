using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PioneerFramework.ShipSkin;
using UnityEngine;
using Xunit;

/// <summary>
/// 2026-10-19 舰船蒙皮（极简硬表面）的**静态资产 / 配色判据**回归。
/// 不启动游戏、不渲染；只钉"接线没断 + 视觉参数不退化"：
///   · 新 shader/ShaderDef/贴图三件套齐全且契约字段正确；
///   · 三平面蒙皮必须用 PFLit 同源的昼夜色常量（改了就与行星光照分家）；
///   · 阵营 tint 的亮度保底（暗阵营色不能把船压成"看不见的泥色"）。
/// </summary>
public class ShipSkinAssetTests
{
    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "1.7", "Defs"))
                && Directory.Exists(Path.Combine(directory.FullName, "Source")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate PioneerFrameworkLite workspace root.");
    }

    [Theory]
    [InlineData("PF_ShipSkin.vert.glsl", new[] { "#version 450", "push.modelMatrix", "vWorldPos", "vNormal" })]
    [InlineData("PF_ShipSkin.frag.glsl", new[] { "#version 450", "_SkinTex", "triplanar", "_CamPos", "fwidth", "_SkinOrigin", "_SkinRotation", "_HullColor", "_SkinSurface", "0.482", "0.603", "0.682" })]
    public void ShaderFilesExistAndFollowContract(string file, string[] tokens)
    {
        string path = Path.Combine(Root(), "1.7", "Shaders", file);
        Assert.True(File.Exists(path), path);
        string text = File.ReadAllText(path);
        foreach (string token in tokens)
            Assert.Contains(token, text);
        // 与其它 Mod shader 相同的引擎约束：不许声明 FragmentUniforms / gl_FrontFacing。
        string code = StripCommentLines(text);
        Assert.DoesNotContain("FragmentUniforms", code);
        Assert.DoesNotContain("gl_FrontFacing", code);
    }

    [Fact]
    public void ShaderDefDeclaresShipSkinOpaque()
    {
        var document = XDocument.Load(Path.Combine(Root(), "1.7", "Defs", "ShaderDefs", "PFShaderDefs.xml"));
        var defs = document.Root!.Elements("ShaderDef")
            .ToDictionary(element => element.Element("defName")!.Value, StringComparer.Ordinal);
        Assert.True(defs.ContainsKey("PF_ShipSkin"), "PF_ShipSkin ShaderDef missing");

        XElement program = defs["PF_ShipSkin"].Element("shaderProgram")!;
        Assert.Equal("PF_ShipSkin.vert.glsl", program.Element("vertexShaderFileName")!.Value);
        Assert.Equal("PF_ShipSkin.frag.glsl", program.Element("fragmentShaderFileName")!.Value);
        Assert.Equal("true", program.Element("depthWrite")!.Value);
        Assert.Equal("2000", program.Element("renderQueue")!.Value);
    }

    [Fact]
    public void SkinTextureIsPowerOfTwoRgbPng()
    {
        string path = Path.Combine(Root(), "1.7", "Textures", "PFView", "Ships", "ShipSkinPlates.png");
        Assert.True(File.Exists(path), path);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 50_000, $"贴图过小（{bytes.Length} B）");

        // PNG 头：89 50 4E 47 0D 0A 1A 0A + IHDR
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes.Take(8).ToArray());
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));

        int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        int colorType = bytes[25];
        Assert.Equal(1024, width);
        Assert.Equal(1024, height);
        Assert.True((width & (width - 1)) == 0 && (height & (height - 1)) == 0, "必须是 2 的幂");
        Assert.True(colorType == 2 || colorType == 6, $"PNG colorType 应为 RGB/RGBA（实际 {colorType}）");
    }

    [Fact]
    public void TintLiftsDarkFactionColoursToReadableLuminance()
    {
        foreach (float v in new[] { 0f, 0.05f, 0.15f, 0.30f })
        {
            Color dark = new(v, v * 0.8f, v * 0.6f);
            Color lifted = ShipSkinPalette.EnsureReadable(dark);
            Assert.True(ShipSkinPalette.Luminance(lifted) >= 0.42f,
                $"tint 过暗（输入 {v}，输出亮度 {ShipSkinPalette.Luminance(lifted):F3}）");
            Assert.InRange(lifted.r, 0f, 1f);
            Assert.InRange(lifted.g, 0f, 1f);
            Assert.InRange(lifted.b, 0f, 1f);
        }
    }

    [Fact]
    public void BrightTintIsLeftUntouched()
    {
        var bright = new Color(0.76f, 0.80f, 0.86f);
        Assert.Equal(bright, ShipSkinPalette.EnsureReadable(bright));
    }

    private static string StripCommentLines(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int comment = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) lines[i] = lines[i].Substring(0, comment);
        }
        return string.Join("\n", lines);
    }
}
