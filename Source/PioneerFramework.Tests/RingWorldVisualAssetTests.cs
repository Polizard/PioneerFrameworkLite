using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PioneerFramework.RingWorld;
using RimWorld.Planet;
using Xunit;

/// <summary>
/// 2026-10-17 电影感视觉升级的静态资产/接线回归：新 shader 文件、ShaderDef、
/// PlanetLayerDef 的新绘制层条目、新贴图（PNG 头 + 2 次幂 + 预期尺寸区间）。
/// 这些测试**不启动游戏、不渲染**；画面验收仍由人工完成。
/// </summary>
public class RingWorldVisualAssetTests
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
    [InlineData("PF_RingLit.vert.glsl", "layout(location = 1) in vec3 inNormal")]
    [InlineData("PF_RingLit.frag.glsl", "_RingSunDir")]
    [InlineData("PF_RingGlow.vert.glsl", "layout(location = 1) in vec3 inNormal")]
    [InlineData("PF_RingGlow.frag.glsl", "_GlowTex")]
    [InlineData("PF_RingOrnament.frag.glsl", "_RingSunDir")]
    public void ShaderFilesExistAndFollowContract(string file, string requiredToken)
    {
        string path = Path.Combine(Root(), "1.7", "Shaders", file);
        Assert.True(File.Exists(path), path);
        string text = File.ReadAllText(path);
        Assert.Contains("#version 450", text);
        Assert.Contains(requiredToken, text);
        string code = StripCommentLines(text);
        Assert.DoesNotContain("gl_FrontFacing", code);
        Assert.DoesNotContain("FragmentUniforms", code);
    }

    [Theory]
    [InlineData("PF_RingLit.frag.glsl", new[] { "_RingCameraPos", "_EmissionTex", "0.482", "0.603", "0.682" })]
    [InlineData("PF_RingGlow.frag.glsl", new[] { "_RingCameraPos", "_GlowParams", "_RingTime" })]
    public void ShaderFragmentsCarryContractUniforms(string file, string[] tokens)
    {
        string text = File.ReadAllText(Path.Combine(Root(), "1.7", "Shaders", file));
        foreach (string token in tokens) Assert.Contains(token, text);
    }

    [Fact]
    public void ShaderDefsDeclareRingLitAndRingGlow()
    {
        var document = XDocument.Load(Path.Combine(Root(), "1.7", "Defs", "ShaderDefs", "PFShaderDefs.xml"));
        var defs = document.Root!.Elements("ShaderDef")
            .ToDictionary(element => element.Element("defName")!.Value, StringComparer.Ordinal);
        Assert.True(defs.ContainsKey("PF_RingLit"), "PF_RingLit ShaderDef missing");
        Assert.True(defs.ContainsKey("PF_RingGlow"), "PF_RingGlow ShaderDef missing");

        XElement lit = defs["PF_RingLit"].Element("shaderProgram")!;
        Assert.Equal("PF_RingLit.vert.glsl", lit.Element("vertexShaderFileName")!.Value);
        Assert.Equal("PF_RingLit.frag.glsl", lit.Element("fragmentShaderFileName")!.Value);
        Assert.Equal("false", lit.Element("flipTextureST")!.Value);
        Assert.Equal("true", lit.Element("depthWrite")!.Value);
        Assert.Equal("Back", lit.Element("cullMode")!.Value);

        XElement glow = defs["PF_RingGlow"].Element("shaderProgram")!;
        Assert.Equal("false", glow.Element("flipTextureST")!.Value);
        Assert.Equal("Additive", glow.Element("blendMode")!.Value);
        Assert.Equal("false", glow.Element("depthWrite")!.Value);
        Assert.Equal("None", glow.Element("cullMode")!.Value);
        Assert.Equal("3550", glow.Element("renderQueue")!.Value);
    }

    [Fact]
    public void OrnamentShaderUsesOpaqueDirectionalLighting()
    {
        var document = XDocument.Load(Path.Combine(Root(), "1.7", "Defs", "ShaderDefs", "PFShaderDefs.xml"));
        var def = document.Root!.Elements("ShaderDef")
            .SingleOrDefault(element => element.Element("defName")!.Value == "PF_RingOrnament");
        Assert.NotNull(def);
        XElement program = def!.Element("shaderProgram")!;
        Assert.Equal("PF_RingLit.vert.glsl", program.Element("vertexShaderFileName")!.Value);
        Assert.Equal("PF_RingOrnament.frag.glsl", program.Element("fragmentShaderFileName")!.Value);
        Assert.Equal("true", program.Element("depthWrite")!.Value);
        Assert.Equal("Back", program.Element("cullMode")!.Value);
        Assert.Equal("false", program.Element("flipTextureST")!.Value);
    }

    [Fact]
    public void OrnamentAlbedoIsDedicatedSquareTexture()
    {
        string path = Path.Combine(Root(), "1.7", "Textures", "World", "RingWorld", "RingOrnamentMetal.png");
        Assert.True(File.Exists(path), path);
        (int width, int height, int colorType) = ReadPngHeader(path);
        Assert.Equal(width, height);
        Assert.InRange(width, 1024, 2048);
        Assert.True(colorType is 2 or 6);
    }

    [Fact]
    public void OrnamentMetalDelegatesToSharedHullMaterial()
    {
        var assembly = typeof(WorldDrawLayer_RingOrnaments).Assembly;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var source = assembly.GetType("PioneerFramework.RingWorld.RingOrnamentMaterials", true)!.GetProperty("Metal", flags)!.GetMethod!;
        var target = assembly.GetType("PioneerFramework.RingWorld.RingWorldMaterials", true)!.GetProperty("HullMaterial", flags)?.GetMethod;
        Assert.NotNull(target);
        byte[] call = new byte[] { 0x28 }.Concat(BitConverter.GetBytes(target!.MetadataToken)).ToArray();
        byte[] body = source.GetMethodBody()!.GetILAsByteArray()!;
        Assert.True(Enumerable.Range(0, Math.Max(0, body.Length - call.Length + 1))
            .Any(index => body.Skip(index).Take(call.Length).SequenceEqual(call)),
            "ornament metal must reuse the hull material to keep texture, lighting and emission in sync");
    }

    [Fact]
    public void GlowShaderHasDedicatedTerminalPulseMode()
    {
        string code = StripCommentLines(File.ReadAllText(Path.Combine(Root(), "1.7", "Shaders", "PF_RingGlow.frag.glsl")));
        Assert.Contains("mode == 3", code);
        Assert.Contains("0.08 + 0.92", code);
    }

    [Fact]
    public void TerminalCoreUsesOpaqueEmissionShader()
    {
        var document = XDocument.Load(Path.Combine(Root(), "1.7", "Defs", "ShaderDefs", "PFShaderDefs.xml"));
        var def = document.Root!.Elements("ShaderDef").SingleOrDefault(e => e.Element("defName")!.Value == "PF_RingTerminal");
        Assert.NotNull(def);
        var program = def!.Element("shaderProgram")!;
        Assert.Equal("true", program.Element("depthWrite")!.Value);
        Assert.Equal("true", program.Element("depthTest")!.Value);
        Assert.Equal("Back", program.Element("cullMode")!.Value);
        Assert.Null(program.Element("blendMode"));
        string code = StripCommentLines(File.ReadAllText(Path.Combine(Root(), "1.7", "Shaders", "PF_RingGlow.frag.glsl")));
        Assert.Contains("mode == 4", code);
    }

    [Fact]
    public void RingPlanetLayerListsOrnamentsAndGlowAfterShell()
    {
        var document = XDocument.Load(Path.Combine(
            Root(), "1.7", "Defs", "PlanetLayerDefs", "PlanetLayers_RingWorld.xml"));
        var def = document.Root!.Elements("PlanetLayerDef")
            .Single(element => element.Element("defName")!.Value == "PF_RingWorld");
        string[] layers = def.Element("worldDrawLayers")!.Elements("li").Select(li => li.Value.Trim()).ToArray();
        int terrain = Array.IndexOf(layers, "PioneerFramework.RingWorld.WorldDrawLayer_RingTerrain");
        int shell = Array.IndexOf(layers, "PioneerFramework.RingWorld.WorldDrawLayer_RingShell");
        int ornaments = Array.IndexOf(layers, "PioneerFramework.RingWorld.WorldDrawLayer_RingOrnaments");
        int glow = Array.IndexOf(layers, "PioneerFramework.RingWorld.WorldDrawLayer_RingGlow");
        Assert.True(terrain >= 0 && shell >= 0 && ornaments >= 0 && glow >= 0,
            $"new layers must be listed: {string.Join(", ", layers)}");
        Assert.True(terrain < shell && shell < ornaments && ornaments < glow,
            "expected order Terrain -> Shell -> Ornaments -> Glow");
    }

    [Fact]
    public void NewDrawLayerTypesResolveAndAreWorldDrawLayers()
    {
        Assert.True(typeof(WorldDrawLayer).IsAssignableFrom(typeof(WorldDrawLayer_RingGlow)));
        Assert.True(typeof(WorldDrawLayer).IsAssignableFrom(typeof(WorldDrawLayer_RingOrnaments)));
        Assert.True(typeof(RingOrnamentGeometry).IsPublic);
        Assert.True(typeof(RingViewFraming).IsPublic);
    }

    [Theory]
    [InlineData("RingHull.png", 2048, 256)]
    [InlineData("RingHullEmissive.png", 1024, 128)]
    [InlineData("RingInnerHull.png", 2048, 256)]
    [InlineData("RingInnerEmissive.png", 1024, 128)]
    [InlineData("RingGlowDot.png", 64, 64)]
    public void RingTexturesExistArePowerOfTwoAndMatchExpectedScale(string name, int minWidth, int minHeight)
    {
        string path = Path.Combine(Root(), "1.7", "Textures", "World", "RingWorld", name);
        Assert.True(File.Exists(path), path);
        (int width, int height, int colorType) = ReadPngHeader(path);
        Assert.True(IsPowerOfTwo(width) && IsPowerOfTwo(height), $"{name}: {width}x{height} not power-of-two");
        Assert.True(width >= minWidth, $"{name}: width {width} < {minWidth}");
        Assert.True(height >= minHeight, $"{name}: height {height} < {minHeight}");
        Assert.True(colorType is 2 or 6, $"{name}: unexpected PNG color type {colorType}");
    }

    private static string StripCommentLines(string text) =>
        string.Join("\n", text.Split('\n').Select(line =>
            line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line));

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static (int Width, int Height, int ColorType) ReadPngHeader(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 26, path);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.Take(8).ToArray());
        int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        int colorType = bytes[25];
        return (width, height, colorType);
    }
}
