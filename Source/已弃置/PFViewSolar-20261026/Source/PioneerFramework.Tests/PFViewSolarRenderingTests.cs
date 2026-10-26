using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewSolarRenderingTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData("PF_SolarPlasma", "Opaque")]
    [InlineData("PF_SolarBloom", "Opaque")]
    [InlineData("PF_SolarComposite", "Custom")]
    public void SolarStagesExistAndDoNotWriteSceneDepth(string name, string blend)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Defs", "ShaderDefs", "PFViewSolar.xml");
        Assert.True(File.Exists(file), "Solar ShaderDefs are missing");
        var shader = XDocument.Load(file).Root.Elements("ShaderDef")
            .Single(e => (string)e.Element("defName") == name).Element("shaderProgram");
        Assert.Equal(blend, (string)shader.Element("blendMode"));
        Assert.Equal("false", (string)shader.Element("depthTest"));
        Assert.Equal("false", (string)shader.Element("depthWrite"));
        Assert.Equal("None", (string)shader.Element("cullMode"));
        foreach (var stage in new[] { "vertexShaderFileName", "fragmentShaderFileName" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Shaders", (string)shader.Element(stage))));
        if (name == "PF_SolarComposite")
        {
            Assert.Equal(PFViewLayers.SunDisc, (int)shader.Element("renderQueue"));
            Assert.Equal("One", (string)shader.Element("blendSrcFactor"));
            Assert.Equal("OneMinusSrcAlpha", (string)shader.Element("blendDstFactor"));
        }
    }

    [Fact]
    public void SolarRendererIsWiredWithFallbackAndCleanup()
    {
        AssertCall("DrawSunDisc", "TryDrawSolarPlasma");
        AssertCall("CleanupResources", "DestroySolarResources");
        AssertCall("TryDrawSolarPlasma", "RenderSolarTexture");
        AssertCall("RenderSolarTexture", "Blit");
    }

    [Fact]
    public void StarfieldKeepsItsSkyDirectionWhenTheSolarCameraOrbits()
    {
        var method = typeof(MainTabWindow_PFView).GetMethod("DrawStarfield", Flags);
        var members = PFViewUiIntegrationTests.ReferencedMembers(method).ToArray();
        Assert.DoesNotContain(members, m => m.Name == "SunDiscDir");
        Assert.Contains(members, m => m.Name == "sunDirectionView");
        var lighting = typeof(MainTabWindow_PFView).GetMethod("UpdateLighting", Flags);
        Assert.Contains(PFViewUiIntegrationTests.ReferencedMembers(lighting), m => m.Name == "sunDirectionView");
    }

    [Fact]
    public void PannedSolarCameraIsConstrainedOutsideTheEmissionVolume()
    {
        var boundary = typeof(MainTabWindow_PFView).Assembly.GetType("PioneerFramework.PFViewSolarCore")
            .GetMethod("CameraBoundary", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(boundary);
        float radius = (float)(double)boundary.Invoke(null, new object[] { 100d });
        var position = PFViewCameraCore.ClampCameraOutsideSphere(new UnityEngine.Vector3(0f, 0f, 120f),
            UnityEngine.Vector3.zero, radius, 1f);
        Assert.Equal(165f, position.magnitude, 3);
        var update = typeof(MainTabWindow_PFView).GetMethod("UpdateCamera", Flags);
        Assert.Contains(PFViewUiIntegrationTests.ReferencedMembers(update),
            m => m.DeclaringType?.Name == "PFViewSolarCore" && m.Name == "CameraBoundary");
    }

    [Fact]
    public void SolarCameraKeepsItsCoronaDistancePolicyAndUsesSharedBodyRotation()
    {
        foreach (var name in new[] { "UpdateCamera", "HandleInput" })
        {
            var method = typeof(MainTabWindow_PFView).GetMethod(name, Flags);
            Assert.Contains(PFViewUiIntegrationTests.ReferencedMembers(method),
                m => m.DeclaringType?.Name == "PFViewSolarCore" && m.Name == "ClampDistance");
        }
        AssertCall("DefaultBodyDistance", "DefaultDistance");
        AssertCall("UpdateCamera", "get_IsStarFocus");
        var drag = typeof(MainTabWindow_PFView).GetMethod("HandleCameraDrag", Flags);
        Assert.DoesNotContain(PFViewUiIntegrationTests.ReferencedMembers(drag), m => m.Name == "get_IsStarFocus");
    }

    private static void AssertCall(string owner, string callee)
    {
        var method = typeof(MainTabWindow_PFView).GetMethod(owner, Flags);
        Assert.NotNull(method);
        Assert.Contains(PFViewUiIntegrationTests.ReferencedMembers(method), m => m.Name == callee);
    }
}
