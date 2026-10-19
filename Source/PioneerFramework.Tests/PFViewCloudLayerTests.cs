using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewCloudLayerTests
{
    [Fact]
    public void CloudPassBlendsAfterOpaqueSurfacesAndKeepsShipDepthOcclusion()
    {
        XElement shader = LoadShader();
        Assert.Equal("Alpha", (string)shader.Element("blendMode"));
        Assert.Equal("true", (string)shader.Element("depthTest"));
        Assert.Equal("false", (string)shader.Element("depthWrite"));
        Assert.Equal("Back", (string)shader.Element("cullMode"));
        int queue = (int)shader.Element("renderQueue");
        Assert.True(queue > PFViewLayers.PlanetAndShips);
        Assert.Equal(PFViewLayers.Clouds, queue);
    }

    [Fact]
    public void CloudPassResolvesItsShaderStagesAndRepeatsOnlyTheCloudAndNoiseTextures()
    {
        XElement shader = LoadShader();
        foreach (string stage in new[] { "vertexShaderFileName", "fragmentShaderFileName" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Shaders", (string)shader.Element(stage))));
        var wraps = shader.Element("textureWrapOverrides").Elements("li")
            .ToDictionary(e => (string)e.Element("key"), e => (string)e.Element("value"));
        Assert.Equal("Repeat", wraps["_CloudTex"]);
        Assert.Equal("Repeat", wraps["_NoiseTex"]);
        Assert.Equal("Clamp", wraps["_SunsetColorRamp"]);
    }

    private static XElement LoadShader()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "Defs", "ShaderDefs", "PFViewClouds.xml");
        Assert.True(File.Exists(file), "PFView cloud ShaderDef is missing");
        return XDocument.Load(file).Root.Elements("ShaderDef")
            .Single(e => (string)e.Element("defName") == "PF_PlanetClouds").Element("shaderProgram");
    }
}
