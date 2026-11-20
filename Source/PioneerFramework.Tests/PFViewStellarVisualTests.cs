using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests;

public class PFViewStellarVisualTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly string[] FrameUniformNames =
        { "_StellarEye", "_StellarTime", "_StellarParams", "_StellarDrift", "_StellarArchHeight0", "_StellarArchHeight1" };
    private static readonly int[] FrameUniformOffsets = { 0, 16, 32, 48, 64, 80 };

    [Theory]
    [InlineData("PF_StellarSurface", "Opaque", "true", 2000)]
    [InlineData("PF_StellarHalo", "Custom", "false", 2900)]
    // S3-a 可选加速路径：与 legacy 逐项同规格（同队列/混合/深度/剔除），否则两 pass 分区会变。
    [InlineData("PF_StellarSurfaceS3A", "Opaque", "true", 2000)]
    [InlineData("PF_StellarHaloS3A", "Custom", "false", 2900)]
    // S3-b 合成档（默认关闭）：离屏星体按代理球合成回主 RT，管线状态必须与 legacy 对一致。
    [InlineData("PF_StellarSurfaceComposite", "Opaque", "true", 2000)]
    [InlineData("PF_StellarHaloComposite", "Custom", "false", 2900)]
    [InlineData("PF_StellarSurfaceFast", "Opaque", "true", 2000)]
    [InlineData("PF_StellarHaloFast", "Custom", "false", 2900)]
    public void StellarEffectsUseRealSphereDepthAndExplicitTransparency(string name, string blend, string writesDepth, int queue)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Defs", "ShaderDefs", "PFViewStellar.xml");
        Assert.True(File.Exists(path));
        var shader = XDocument.Load(path).Root.Elements("ShaderDef").Single(e => (string)e.Element("defName") == name).Element("shaderProgram");
        Assert.Equal(blend, (string)shader.Element("blendMode"));
        Assert.Equal("true", (string)shader.Element("depthTest"));
        Assert.Equal(writesDepth, (string)shader.Element("depthWrite"));
        Assert.Equal("Front", (string)shader.Element("cullMode"));
        Assert.Equal(queue, (int)shader.Element("renderQueue"));
        foreach (string stage in new[] { "vertexShaderFileName", "fragmentShaderFileName" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Shaders", (string)shader.Element(stage))));
        if (blend == "Custom")
        {
            Assert.Equal("One", (string)shader.Element("blendSrcFactor"));
            Assert.Equal("OneMinusSrcAlpha", (string)shader.Element("blendDstFactor"));
        }
    }

    [Fact]
    public void VisualsAreWiredToOrdinaryBodyMaterialsWithoutControllingTheCamera()
    {
        var owner = typeof(MainTabWindow_PFView);
        Assert.Contains(Members("GetBaseSphereMaterial"), m => m.Name == "CreateStellarSurfaceMaterial");
        Assert.Contains(Members("DrawCelestialBodiesCore"), m => m.Name == "PrepareStellarVisual");
        Assert.Contains(Members("CleanupResources"), m => m.Name == "DestroyStellarVisualResources");
        var visual = owner.GetMethod("PrepareStellarVisual", Flags);
        Assert.NotNull(visual);
        Assert.DoesNotContain(PFViewUiIntegrationTests.ReferencedMembers(visual), m => m.Name is
            "FocusBody" or "InitializeSunCameraPose" or "get_IsStarFocus" or "set_position" or "set_rotation" or "set_fieldOfView");
        foreach (string name in new[] { "FocusBody", "UpdateCamera", "HandleInput", "ResolveOrbitFrame" })
            Assert.DoesNotContain(Members(name), m => m.DeclaringType?.Name == "PFViewStellarVisualCore");
    }

    [Fact]
    public void HalosAreSubmittedAfterOpaqueSceneObjects()
    {
        var members = Members("DrawAllObjects").Select(m => m.Name).ToArray();
        int halo = Array.IndexOf(members, "DrawStellarHalos");
        Assert.True(halo > Array.IndexOf(members, "DrawProjectiles") && halo >= 0);
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(100f, -20f, 300f)]
    public void SamplingFollowsTheBodyCenterAndRotation(float x, float y, float z)
    {
        var core = Core();
        var eye = core.GetMethod("Eye", Flags);
        Assert.NotNull(eye);
        var center = new Vector3(x, y, z);
        var rotation = PFViewCameraCore.OrbitRotation(35f, 25f, Quaternion.identity);
        var local = new Vector3(1f, 2f, 4f);
        var camera = center + rotation * local * 30f;
        var actual = (Vector3)eye.Invoke(null, new object[] { camera, center, rotation, 30f });
        Assert.True((actual - local).magnitude < 1e-5f);
    }

    [Fact]
    public void AnimationKeepsTheApprovedPeriodicPlasmaClock()
    {
        var phase = Core().GetMethod("Phase", Flags);
        Assert.NotNull(phase);
        float Read(double tick) => (float)phase.Invoke(null, new object[] { tick });
        Assert.Equal(Read(1234), Read(37234), 6);
        Assert.Equal(Read(1e12), Read(1e12 + 36000), 6);
        Assert.InRange(Read(-1), 0, MathF.PI * 2);
        Assert.Equal(0, Read(double.NaN));
    }

    [Theory]
    [InlineData(0d, 0d, 1d, .963558185417,
        .245271442864, .308783914836, .185073653267, .161002647614,
        .186899409762, .211486801209, .216147288972, .126961962941)]
    [InlineData(1234d, .862160277228, .506635624853, -.235219046616,
        .267724332406, .255815356658, .154000375979, .192895320614,
        .176005385530, .268834761178, .169219338985, .124918329268)]
    [InlineData(12000d, .866024412991, -.500001716100, -.713437096719,
        .253919585266, .231708559198, .159433075769, .212378541513,
        .159995817788, .279466311748, .161239883909, .137237795172)]
    [InlineData(1e12, .342020876719, -.939692353852, .566182174279,
        .227438065255, .217333632558, .177542750294, .227770723064,
        .142053827221, .274055145141, .166644641605, .154649131745)]
    public void FrameParametersPreserveTheOriginalPlasmaAnimation(
        double tick, double dx, double dy, double dz,
        double h0, double h1, double h2, double h3, double h4, double h5, double h6, double h7)
    {
        var frame = ReadFrame(tick);
        // Independent binary64 fixtures; bounds include float argument/product rounding.
        AssertVector(frame.Drift, new[] { dx, dy, dz, 0d }, 3.5e-5);
        AssertVector(frame.ArchHeight0, new[] { h0, h1, h2, h3 }, 4e-7);
        AssertVector(frame.ArchHeight1, new[] { h4, h5, h6, h7 }, 4e-7);
    }

    [Theory]
    [InlineData(1234d)]
    [InlineData(1e12)]
    [InlineData(-1d)]
    public void BothPassesCanReuseIdenticalParametersWhilePausedOrAfterWholePeriods(double tick)
    {
        Assert.Equal(ReadFrame(tick), ReadFrame(tick));
        Assert.Equal(ReadFrame(tick), ReadFrame(tick + 36000));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidAnimationTimesUseTheExistingZeroPhaseFallback(double tick)
    {
        Assert.Equal(ReadFrame(0), ReadFrame(tick));
    }

    [Fact]
    public void CommonAnimationParametersRemainContinuousAcrossClockWrap()
    {
        var before = ReadFrame(36000 - .001);
        var after = ReadFrame(.001);
        AssertVector(before.Drift, new[] { (double)after.Drift.x, after.Drift.y, after.Drift.z, 0d }, 4e-5);
        AssertVector(before.ArchHeight0, new[] { (double)after.ArchHeight0.x, after.ArchHeight0.y, after.ArchHeight0.z, after.ArchHeight0.w }, 1e-6);
        AssertVector(before.ArchHeight1, new[] { (double)after.ArchHeight1.x, after.ArchHeight1.y, after.ArchHeight1.z, after.ArchHeight1.w }, 1e-6);
    }

    [Theory]
    [InlineData("vert")]
    [InlineData("frag")]
    public void NativeMaterialParserResolvesTheSameFrameUniformLayoutInBothStages(string stage)
    {
        string name = "PF_StellarVolume." + stage + ".glsl";
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", name));
        var layout = Waylith.Rendering.ShaderMetadataParser.ExtractMaterialLayout(source, name);
        Assert.False(layout.HasErrors);
        Assert.Equal(96, layout.UniformSize);
        Assert.Equal(FrameUniformNames, layout.UniformSlots.Select(slot => slot.PropertyName));
        Assert.Equal(FrameUniformOffsets, layout.UniformSlots.Select(slot => slot.Offset));
        Assert.All(layout.UniformSlots, slot => Assert.Equal(Waylith.Rendering.MaterialUniformType.Vector4, slot.Type));
    }

    [Fact]
    public void ShaderSamplesObjectSpaceAndWritesIntegratedDepth()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Shaders", "PF_StellarVolume.frag.glsl");
        Assert.True(File.Exists(path));
        string source = File.ReadAllText(path);
        Assert.Contains("vLocalPosition", source);
        Assert.Contains("gl_FragDepth", source);
        Assert.Contains("granulation", source);
        Assert.Contains("prominences", source);
        Assert.DoesNotContain("vUv", source);
    }

    [Theory]
    // Legacy (default) shader: the approved old visual keeps its original early-discard contract.
    [InlineData(true, 3.0, 0.0, true, true)]
    [InlineData(true, 1.65, 0.9199, true, true)]
    [InlineData(true, 1.6499, 0.9199, true, true)]
    [InlineData(true, 1.5, 0.0, true, true)]
    [InlineData(true, 1.2, 0.9199, true, true)]
    [InlineData(true, 1.002, 0.9199, true, true)]
    [InlineData(true, 1.2, 0.0, false, false)]
    [InlineData(true, 1.2, 0.9199, false, false)]
    [InlineData(true, 1.5, 0.92, true, false)]
    [InlineData(true, 1.5, 1.1, true, false)]
    [InlineData(false, 1.2, 0.0, true, false)]
    [InlineData(false, 1.2, 0.9199, true, false)]
    public void EarlyDiscardLegacyKeepsApprovedHaloCulling(
        bool haloPass, double distance, double impact, bool inward, bool expectedDiscarded)
        => Assert.Equal(expectedDiscarded,
            AnyGuardDiscards("PF_StellarVolume.frag.glsl", haloPass, distance, impact, inward));

    [Theory]
    // S3-a: the optional accelerated path must keep the approved legacy culling verbatim, because the
    // guards decide which pass owns a pixel (surface vs halo) before any integration happens.
    [InlineData(true, 3.0, 0.0, true, true)]
    [InlineData(true, 1.65, 0.9199, true, true)]
    [InlineData(true, 1.6499, 0.9199, true, true)]
    [InlineData(true, 1.5, 0.0, true, true)]
    [InlineData(true, 1.2, 0.9199, true, true)]
    [InlineData(true, 1.002, 0.9199, true, true)]
    [InlineData(true, 1.2, 0.0, false, false)]
    [InlineData(true, 1.2, 0.9199, false, false)]
    [InlineData(true, 1.5, 0.92, true, false)]
    [InlineData(true, 1.5, 1.1, true, false)]
    [InlineData(false, 1.2, 0.0, true, false)]
    [InlineData(false, 1.2, 0.9199, true, false)]
    public void EarlyDiscardS3AKeepsTheApprovedLegacyHaloCulling(
        bool haloPass, double distance, double impact, bool inward, bool expectedDiscarded)
        => Assert.Equal(expectedDiscarded,
            AnyGuardDiscards("PF_StellarVolumeS3A.frag.glsl", haloPass, distance, impact, inward));

    [Theory]
    // Optional fast renderer: both passes cull "rays leaving the shell away from the star";
    // the surface pass additionally culls the corona ring (impact > 1.42) because the halo pass
    // owns the transparent corona. Inward rays are never culled by these early guards.
    [InlineData(true, 3.0, 0.0, true, false)]
    [InlineData(true, 3.0, 0.0, false, true)]
    [InlineData(true, 1.2, 0.0, false, false)]
    [InlineData(true, 1.2, 0.0, true, false)]
    [InlineData(true, 1.5, 1.1, true, false)]
    [InlineData(true, 3.0, 1.5, false, true)]
    [InlineData(false, 3.0, 0.0, true, false)]
    [InlineData(false, 3.0, 1.5, true, true)]
    [InlineData(false, 3.0, 1.5, false, true)]
    [InlineData(false, 1.2, 0.0, true, false)]
    [InlineData(false, 1.2, 0.0, false, false)]
    [InlineData(false, 1.65, 1.5, false, true)]
    [InlineData(false, 1.65, 0.9, true, false)]
    [InlineData(false, 1.65, 0.9, false, true)]
    public void EarlyDiscardFastCullsOnlyAwayRaysAndSurfaceCoronaRing(
        bool haloPass, double distance, double impact, bool inward, bool expectedDiscarded)
        => Assert.Equal(expectedDiscarded,
            AnyGuardDiscards("PF_StellarVolumeFast.frag.glsl", haloPass, distance, impact, inward));

    /// <summary>Execute the shipped GLSL early-discard guards on one ray fixture (no copied predicate).</summary>
    private static bool AnyGuardDiscards(string shaderFile, bool haloPass, double distance, double impact, bool inward)
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", shaderFile));
        string main = source.Substring(source.IndexOf("void main()", StringComparison.Ordinal));
        string guards = main.Substring(0, main.IndexOf("float phase", StringComparison.Ordinal));
        double b = Math.Sqrt(distance * distance - impact * impact) * (inward ? -1 : 1);

        using var expressions = new DataTable { Locale = CultureInfo.InvariantCulture };
        return Regex.Matches(guards, @"if\s*\((.+)\)\s*discard\s*;")
            .Cast<Match>().Any(match =>
            {
                string condition = Regex.Replace(match.Groups[1].Value, @"length\s*\(eye\)", Number(distance));
                condition = Regex.Replace(condition, @"\bhaloPass\b", haloPass ? "True" : "False");
                condition = Regex.Replace(condition, @"\bimpact\b", Number(impact));
                condition = Regex.Replace(condition, @"\bb\b", Number(b));
                condition = condition.Replace("&&", " AND ").Replace("||", " OR ");
                condition = Regex.Replace(condition, @"!(?!=)", " NOT ");
                return (bool)expressions.Compute(condition, "");
            });
    }

    private static string Number(double value) => "(" + value.ToString("R", CultureInfo.InvariantCulture) + ")";

    private static (float Phase, Vector4 Drift, Vector4 ArchHeight0, Vector4 ArchHeight1) ReadFrame(double tick)
    {
        var frame = PFViewStellarVisualCore.Frame(tick);
        return (frame.Phase, frame.Drift, frame.ArchHeight0, frame.ArchHeight1);
    }

    private static void AssertVector(Vector4 actual, double[] expected, double tolerance)
    {
        double[] components = { actual.x, actual.y, actual.z, actual.w };
        for (int i = 0; i < components.Length; ++i)
            Assert.InRange(Math.Abs(components[i] - expected[i]), 0, tolerance);
    }

    private static Type Core()
    {
        var core = typeof(MainTabWindow_PFView).Assembly.GetType("PioneerFramework.PFViewStellarVisualCore");
        Assert.NotNull(core);
        return core;
    }

    private static MemberInfo[] Members(string name) => PFViewUiIntegrationTests.ReferencedMembers(
        typeof(MainTabWindow_PFView).GetMethod(name, Flags)).ToArray();
}
