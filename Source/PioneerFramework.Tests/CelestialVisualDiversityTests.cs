using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Reflection;
using System.Xml;
using PioneerFramework.Celestial;
using UnityEngine;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialVisualDiversityTests
{
    internal const string Legacy = "{\"Id\":\"control\",\"Red\":0.2,\"Green\":0.4,\"Blue\":0.8,\"Alpha\":1,\"TexturePath\":null,\"BackgroundBiomeId\":null,\"ShellHeightMeters\":10000,\"FadeRangeMeters\":5000,\"DriftPeriodTicks\":600000,\"DistortionPeriodTicks\":6000}";
    internal static string Modern => Legacy.Replace("\"ShellHeightMeters\":10000", "\"ShellHeightMeters\":0", StringComparison.Ordinal)[..^1] + ",\"Appearance\":{\"Style\":1,\"CloudCoverage\":1,\"BandFrequency\":16,\"BandContrast\":0.6}}";
    private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    [Fact]
    public void VersionTwoCodecRetainsExplicitAppearanceWhileVersionOneBytesRemainIdentical()
    {
        var old = VisualResolvedProperties.Deserialize("clouds", 1, Encoding.UTF8.GetBytes(Legacy), CelestialVisualKind.Clouds);
        Assert.True(old.Success);
        Assert.Equal(Legacy, Encoding.UTF8.GetString(old.Value.Serialize().Span));
        var modern = VisualResolvedProperties.Deserialize("visual", 2, Encoding.UTF8.GetBytes(Modern), CelestialVisualKind.Surface);
        Assert.True(modern.Success, string.Join(",", modern.Errors));
        Assert.Equal(2, modern.Value.Version);
        Assert.Equal(Modern, Encoding.UTF8.GetString(modern.Value.Serialize().Span));
        Assert.False(VisualResolvedProperties.Deserialize("visual", 1, Encoding.UTF8.GetBytes(Modern), CelestialVisualKind.Surface).Success);
        Assert.False(VisualResolvedProperties.Deserialize("visual", 2, Encoding.UTF8.GetBytes(Legacy), CelestialVisualKind.Surface).Success);
    }
    [Theory]
    [InlineData(CelestialVisualKind.Surface, "appearance.cloudCoverage", 0.4)]
    [InlineData(CelestialVisualKind.Clouds, "appearance.bandFrequency", 20)]
    [InlineData(CelestialVisualKind.Atmosphere, "appearance.driftPeriodTicks", 3000)]
    [InlineData(CelestialVisualKind.Stellar, "appearance.red", 0.5)]
    public void NewParametersMustHaveAConsumerInTheSelectedVisualKind(CelestialVisualKind kind, string parameter, double value)
    {
        var profile = Profile();
        CelestialVisualCompProperties comp = kind switch {
            CelestialVisualKind.Surface => new CompProperties_CelestialVisual(),
            CelestialVisualKind.Clouds => new CompProperties_CloudsVisual(),
            CelestialVisualKind.Atmosphere => new CompProperties_AtmosphereVisual(),
            _ => new CompProperties_StellarVisual() };
        comp.key = "visual"; comp.profile = profile;
        var template = Template(comp);
        Assert.False(template.Resolve(new(3, "consumer", parameterOverrides: new[] { new CelestialParameterOverride("visual", parameter, Value: value) })).Success);
    }
    [Fact]
    public void VersionAndStyleMismatchesAndInactiveFieldsAreRejected()
    {
        var p = Profile(); p.visualVersion = 1;
        Assert.False(p.ToProfile().Success);
        p = Profile(); p.appearance = null;
        Assert.False(p.ToProfile().Success);
        p = Profile(); p.appearance.style = CelestialSurfaceStyle.GasBands;
        Assert.False(new CompProperties_CloudsVisual { key = "clouds", profile = p }.Resolve(new(1, "wrong", "clouds")).Success);
        p = Profile(); p.appearance.cloudCoverage = 0.5;
        Assert.False(new CompProperties_CelestialVisual { key = "visual", profile = p }.Resolve(new(1, "wrong", "visual")).Success);
        p = Profile(); p.appearance.style = CelestialSurfaceStyle.GasBands; p.color = new(1, 1, 1, 0.5f);
        Assert.False(new CompProperties_CelestialVisual { key = "visual", profile = p }.Resolve(new(1, "wrong", "visual")).Success);
    }

    [Fact]
    public void XmlRangesAndOverridesResolveOnceAndReachRealCloudAndBandBindings()
    {
        using var scope = new CelestialXmlTestScope();
        var xml = new XmlDocument();
        xml.LoadXml("<PioneerFramework.Celestial.CelestialVisualProfileDef><defName>PF_VisualXml</defName><visualVersion>2</visualVersion>" +
            "<shellHeightMeters>10000</shellHeightMeters><appearance><cloudCoverage>0.8</cloudCoverage><parameters>" +
            "<li><key>cloudCoverage</key><value><min>0.2</min><max>0.9</max></value></li>" +
            "<li><key>red</key><value><value>0.25</value></value></li>" +
            "<li><key>driftPeriodTicks</key><value><value>1200</value></value></li>" +
            "<li><key>distortionPeriodTicks</key><value><value>100</value></value></li>" +
            "<li><key>shellHeightMeters</key><value><value>20000</value></value></li>" +
            "<li><key>fadeRangeMeters</key><value><value>10000</value></value></li>" +
            "</parameters></appearance></PioneerFramework.Celestial.CelestialVisualProfileDef>");
        var profile = DirectXmlToObject.ObjectFromXml<CelestialVisualProfileDef>(xml.DocumentElement, false);
        var template = Template(new CompProperties_CloudsVisual { key = "clouds", profile = profile });
        VisualResolvedProperties Resolve(ulong seed, params CelestialParameterOverride[] overrides) =>
            Assert.Single(template.Resolve(new(seed, "same", parameterOverrides: overrides)).Value.Select(c => c.Properties).OfType<VisualResolvedProperties>());
        var first = Resolve(9); var same = Resolve(9); var other = Resolve(10);
        Assert.Equal(first.Serialize().ToArray(), same.Serialize().ToArray());
        Assert.NotEqual(first.Profile.Appearance.CloudCoverage, other.Profile.Appearance.CloudCoverage);
        Assert.InRange(first.Profile.Appearance.CloudCoverage, 0.2, 0.9);
        var selected = Resolve(9, new("clouds", "appearance.cloudCoverage", Value: 0.4), new("clouds", "appearance.blue", Min: 0.6, Max: 0.7));
        var plan = PFViewVisualBindings.Clouds(selected.Profile, 300, 25000);
        Assert.Equal(20000, plan.AltitudeMeters);
        Assert.Equal(new Vector4(0.5f, 0.25f, 0, 0.4f), plan.Parameters);
        Assert.Equal(0.25f, plan.Tint.x);
        Assert.InRange(plan.Tint.z, 0.6f, 0.7f);
        Assert.Equal(plan, PFViewVisualBindings.Clouds(selected.Profile, 300, 25000));
        var bytes = selected.Serialize().ToArray();
        profile.appearance.cloudCoverage = 0; profile.color = Color.black;
        Assert.Equal(bytes, selected.Serialize().ToArray());
        var restored = VisualResolvedProperties.Deserialize("clouds", 2, bytes, CelestialVisualKind.Clouds).Value;
        Assert.Equal(plan, PFViewVisualBindings.Clouds(restored.Profile, 300, 25000));
        var gas = Profile(); gas.appearance.style = CelestialSurfaceStyle.GasBands;
        gas.appearance.parameters.Add(new() { key = "bandFrequency", value = new() { min = 10, max = 20 } });
        var gasTemplate = Template(new CompProperties_CelestialVisual { key = "visual", profile = gas });
        var result = gasTemplate.Resolve(new(9, "same", parameterOverrides: new[] {
            new CelestialParameterOverride("visual", "appearance.bandFrequency", Value: 16), new CelestialParameterOverride("visual", "appearance.bandContrast", Value: 0.6) }));
        var bands = PFViewVisualBindings.Bands(Assert.Single(result.Value.Select(c => c.Properties).OfType<VisualResolvedProperties>()).Profile);
        Assert.Equal(new Vector4(16, 0.6f, 0, 0), bands.Parameters);
        Assert.Equal(new Vector4(1, 1, 1, 1), bands.Color);
    }
    [Theory]
    [InlineData("red", -0.01, 0.8)]
    [InlineData("green", 0.2, 1.01)]
    [InlineData("blue", 0, 1.01)]
    [InlineData("alpha", -0.01, 1)]
    [InlineData("cloudCoverage", -0.01, 0.9)]
    [InlineData("cloudCoverage", 0.2, 1.01)]
    [InlineData("shellHeightMeters", -1, 10000)]
    [InlineData("shellHeightMeters", 10000, 1000000001)]
    [InlineData("fadeRangeMeters", 0, 5000)]
    [InlineData("fadeRangeMeters", 5000, 1000000001)]
    [InlineData("driftPeriodTicks", 0.9, 600000)]
    [InlineData("distortionPeriodTicks", 100, 1000000000000001)]
    [InlineData("bandFrequency", 0.9, 10)]
    [InlineData("bandFrequency", 10, 128.01)]
    [InlineData("bandContrast", -0.01, 0.5)]
    [InlineData("bandContrast", 0.5, 1.01)]
    public void CompleteAuthoredAndOverrideIntervalsAreValidatedBeforeSampling(string key, double min, double max)
    {
        var profile = Profile(); bool gas = key.StartsWith("band", StringComparison.Ordinal);
        if (gas) profile.appearance.style = CelestialSurfaceStyle.GasBands;
        CelestialVisualCompProperties comp = gas ? new CompProperties_CelestialVisual() : new CompProperties_CloudsVisual();
        comp.key = "visual"; comp.profile = profile;
        profile.appearance.parameters.Add(new() { key = key, value = new() { min = min, max = max } });
        Assert.False(Template(comp).Resolve(new(71, "interval")).Success);
        profile.appearance.parameters.Clear();
        Assert.False(Template(comp).Resolve(new(71, "interval", parameterOverrides: new[] { new CelestialParameterOverride("visual", "appearance." + key, Min: min, Max: max) })).Success);
    }
    [Fact]
    public void InvalidFormsUnknownNumericKeysAndUnknownOverridesAreRejected()
    {
        var profile = Profile(); var comp = new CompProperties_CloudsVisual { key = "clouds", profile = profile };
        profile.appearance.parameters.Add(new() { key = "cloudCoverage", value = new() { value = 0.4, min = 0.2, max = 0.8 } });
        Assert.False(Template(comp).Resolve(new(1, "form")).Success);
        profile.appearance.parameters.Clear();
        profile.appearance.parameters.Add(new() { key = "cloudCovrage", value = new() { value = 0.4 } });
        Assert.False(Template(comp).Resolve(new(1, "form")).Success);
        profile.appearance.parameters.Clear();
        Assert.False(Template(comp).Resolve(new(1, "form", parameterOverrides: new[] { new CelestialParameterOverride("clouds", "appearance.cloudCovrage", Value: 0.4) })).Success);
    }
    [Fact]
    public void DefaultAndVersionOneCloudInputsKeepTheLegacyPhasesOpacityAndPalette()
    {
        var legacy = VisualResolvedProperties.Deserialize("clouds", 1, Encoding.UTF8.GetBytes(Legacy), CelestialVisualKind.Clouds).Value.Profile;
        var old = PFViewVisualBindings.Clouds(legacy, 150000, 12500);
        Assert.Equal(new Vector4(0.5f, 0.25f, 0, 1), old.Parameters);
        Assert.Equal(new Vector4(1, 1, 1, 1), old.Tint);
        Assert.Equal(old, PFViewVisualBindings.Clouds(null, 150000, 12500));
        Assert.Equal(10000, old.AltitudeMeters);
        var zero = Profile(); zero.appearance.cloudCoverage = 0;
        Assert.Equal(0, PFViewVisualBindings.Clouds(zero.ToProfile().Value, 0, 20000).Parameters.w);
        Assert.Throws<ArgumentException>(() => PFViewVisualBindings.Bands(legacy));
    }
    [Fact]
    public void RegistryDecodesBothVisualVersionsWithoutResamplingAndRejectsMalformedPayloads()
    {
        var registry = CelestialBuiltinCompRegistry.Create();
        foreach (var kind in Enum.GetValues<CelestialVisualKind>())
        {
            Assert.True(registry.TryGet(VisualResolvedProperties.HandlerFor(kind), out var registration));
            Assert.Equal(2, registration.ConfigurationVersion);
            var old = registration.Codec.DecodeProperties("visual", 1, Encoding.UTF8.GetBytes(Legacy));
            Assert.True(old.Success); Assert.Equal(1, old.Value.Version);
            Assert.Equal(Legacy, Encoding.UTF8.GetString(old.Value.Serialize().Span));
        }
        Assert.True(registry.TryGet(VisualResolvedProperties.HandlerFor(CelestialVisualKind.Surface), out var modern));
        Assert.True(modern.Codec.DecodeProperties("visual", 2, Encoding.UTF8.GetBytes(Modern)).Success);
        foreach (var invalid in new[] { Modern.Replace("\"Style\":1", "\"Style\":9", StringComparison.Ordinal),
            Modern.Replace("\"BandFrequency\":16", "\"BandFrequency\":129", StringComparison.Ordinal),
            Modern.Replace("\"BandContrast\":0.6", "\"BandContrast\":null", StringComparison.Ordinal),
            Modern.Replace("\"BandContrast\":0.6", "\"BandContrast\":0.6,\"BandContrast\":0.2", StringComparison.Ordinal),
            Modern.Replace("\"BandContrast\":0.6", "\"BandContrast\":0.6,\"typo\":0.2", StringComparison.Ordinal),
            Modern.Replace("\"BandFrequency\":16,", "", StringComparison.Ordinal),
            Modern.Replace("\"CloudCoverage\":1", "\"CloudCoverage\":0.5", StringComparison.Ordinal) })
            Assert.False(modern.Codec.DecodeProperties("visual", 2, Encoding.UTF8.GetBytes(invalid)).Success);
    }
    [Fact]
    public void CloudAndGasVisualsNeedOnlyGeometryAndQueryingDoesNotCreateLayersOrMaps()
    {
        var gasProfile = Profile(); gasProfile.appearance.style = CelestialSurfaceStyle.GasBands;
        var gas = Template(new CompProperties_CelestialVisual { key = "visual", profile = gasProfile },
            new CompProperties_CloudsVisual { key = "clouds", profile = Profile() });
        var minimal = Template();
        foreach (var template in new[] { minimal, gas })
        {
            Assert.False(template.HasCapability("surface")); Assert.False(template.HasCapability("atmosphere"));
            Assert.False(template.HasCapability("mapGeneration")); Assert.False(template.HasCapability("environment"));
            Assert.True(template.Resolve(new(9, "geometry-only")).Success);
        }
        var bridge = new PioneerFramework.TestSupport.Celestial.DeterministicTestPhysicsBridge(Guid.NewGuid());
        var id = CelestialInstanceId.Parse("visual:gas");
        bridge.SetFrame(0, new Dictionary<CelestialInstanceId, CelestialMotionState> { [id] = new(1, new(100, 0, 0), DVector3.zero, DQuaternion.identity, DVector3.zero) });
        var runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, new NoResources(), 1, _ => throw new Exception("No implicit creation"));
        var runtimeGas = CelestialBodyTemplate.Create("PF_RuntimeGas", "gas", "VisualProbe", Array.Empty<string>(), gas.Components.Append(
            new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = PioneerFramework.TestSupport.Celestial.DeterministicTestPhysicsBridge.Id, modeId = "replay" }.ToDefinition())).Value;
        var spawn = new CelestialSpawnItem(id, "gas", 0, "gas", runtimeGas, runtimeGas.Resolve(new(9, "gas")).Value);
        Assert.Equal(CelestialOperationStatus.Pending, runtime.CreateBatch(new(new("gas", runtime.WorldSessionId, 0), new("gas", 1, null, new[] { spawn }))).Status);
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        var view = new CelestialViewAdapter(runtime).Capture(id, runtime.CurrentStamp);
        Assert.True(view.Success, string.Join(",", view.Errors));
        Assert.Null(view.Value.CacheKey.LayerId); Assert.Null(view.Value.NativeDisplayRadius);
        Assert.Equal(2, view.Value.Visuals.Count);
        Assert.Null(runtime.Surfaces); Assert.Null(runtime.Maps);
        var bands = PFViewVisualBindings.Bands(view.Value.Visuals.Single(v => v.Kind == CelestialVisualKind.Surface).Profile);
        Assert.Equal(12, bands.Parameters.x);
    }
    [Fact]
    public void CompiledDrawPathsSubmitTheTestedBindingsAndReuseExistingSurfaceBake()
    {
        MemberInfo[] Members(string name) => PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod(name, Flags)).ToArray();
        var clouds = Members("DrawCloudLayer");
        Assert.Contains(clouds, m => m.DeclaringType == typeof(PFViewVisualBindings) && m.Name == "Clouds");
        Assert.Contains(clouds, m => m.DeclaringType == typeof(Material) && m.Name == "SetVector");
        Assert.Contains(clouds, m => m.DeclaringType == typeof(Graphics) && m.Name == "DrawMesh");
        var material = Members("GetBaseSphereMaterial");
        Assert.Contains(material, m => m.DeclaringType == typeof(PFViewVisualBindings) && m.Name == "Bands");
        Assert.Contains(material, m => m.DeclaringType == typeof(Material) && m.Name == "SetVector");
        var draw = Members("DrawCelestialBodiesCore");
        Assert.Contains(draw, m => m.Name == "GetBaseSphereMaterial"); Assert.Contains(draw, m => m.Name == "DrawCelestialSphere");
        Assert.Contains(Members("DrawCelestialSphere"), m => m.DeclaringType == typeof(Graphics) && m.Name == "DrawMesh");
        Assert.Contains(Members("TryGetSurfacePatch"), m => m.DeclaringType == typeof(PFViewSurfaceBake) && m.Name == "EnsureReady");
        Assert.Contains(Members("TryGetSurfacePatch"), m => m.DeclaringType == typeof(PFViewSurfaceBake) && m.Name == "TryGetPatch");
        foreach (var list in new[] { clouds, material, draw, Members("GetCelestialRenderLayer"), Members("TryGetSurfacePatch") })
            Assert.DoesNotContain(list, m => m.Name is "RequestSurface" or "GenerateMap" or "CreateLayer");
        string Source(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Source", "PioneerFramework", "Presentation", "PFView", file));
        string cloudSource = Source("MainTabWindow_PFView.Clouds.cs");
        Assert.Contains("mat.SetVector(\"_CloudParams\", binding.Parameters)", cloudSource);
        Assert.Contains("mat.SetVector(\"_CloudTint\", binding.Tint)", cloudSource);
        string terrainSource = Source("MainTabWindow_PFView.Terrain.cs");
        Assert.Contains("mat.SetVector(\"_BandParams\", binding.Parameters)", terrainSource);
        Assert.Contains("mat.SetVector(\"_BandColor\", binding.Color)", terrainSource);
    }
    [Theory]
    [InlineData("PF_PlanetClouds.frag.glsl", 80, "_CloudTint", 64)]
    [InlineData("PF_PlanetBands.frag.glsl", 64, "_BandColor", 48)]
    public void InstalledNativeMaterialParserResolvesTheActualUniformNamesAndOffsets(string file, int size, string property, int offset)
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", file));
        var layout = Waylith.Rendering.ShaderMetadataParser.ExtractMaterialLayout(source, file);
        Assert.False(layout.HasErrors); Assert.Equal(size, layout.UniformSize);
        var slot = Assert.Single(layout.UniformSlots, s => s.PropertyName == property);
        Assert.Equal(offset, slot.Offset); Assert.Equal(Waylith.Rendering.MaterialUniformType.Vector4, slot.Type);
    }

    [Fact]
    public void VersionTwoAtmosphereHasARealShellColorBindingAndSurfaceCannotSilentlyIgnoreTint()
    {
        var profile = Profile(); profile.shellHeightMeters = 50000; profile.color = new(0.25f, 0.5f, 0.75f, 0.5f);
        profile.appearance.parameters.Add(new() { key = "shellHeightMeters", value = new() { min = 40000, max = 60000 } });
        var atmosphere = new CompProperties_AtmosphereVisual { key = "atmosphereVisual", profile = profile };
        var template = Template(atmosphere);
        var resolved = Assert.Single(template.Resolve(new(8, "atmosphere", parameterOverrides: new[] {
            new CelestialParameterOverride("atmosphereVisual", "appearance.shellHeightMeters", Value: 55000) })).Value.Select(c => c.Properties).OfType<VisualResolvedProperties>());
        Assert.Equal(55000, resolved.Profile.ShellHeightMeters);
        Assert.Equal(new Color(0.25f, 0.5f, 0.75f, 0.09f), PFViewVisualBindings.Atmosphere(Color.white, resolved.Profile, false));
        Assert.Equal(new Color(0.25f, 0.5f, 0.75f, 0.03f), PFViewVisualBindings.Atmosphere(Color.white, resolved.Profile, true));
        var v1 = VisualResolvedProperties.Deserialize("atmosphereVisual", 1, Encoding.UTF8.GetBytes(Legacy), CelestialVisualKind.Atmosphere).Value;
        Assert.Equal(new Color(0.2f, 0.4f, 0.8f, 0.18f), PFViewVisualBindings.Atmosphere(new Color(0.2f, 0.4f, 0.8f, 1), v1.Profile, false));
        var members = PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("GetAtmosphereShellMaterial", Flags)).ToArray();
        Assert.Contains(members, m => m.DeclaringType == typeof(PFViewVisualBindings) && m.Name == "Atmosphere");
        Assert.Contains(members, m => m.DeclaringType == typeof(Material) && m.Name == "set_color");
        Assert.Contains(PFViewUiIntegrationTests.ReferencedMembers(typeof(MainTabWindow_PFView).GetMethod("DrawAtmosphereShell", Flags)),
            m => m.DeclaringType == typeof(Graphics) && m.Name == "DrawMesh");
        var p = Profile(); p.texturePath = "World/CloudMap";
        Assert.False(new CompProperties_CelestialVisual { key = "visual", profile = p }.Resolve(new(1, "texture", "visual")).Success);
        p = Profile(); p.color = new(1, 1, 1, 0.5f);
        Assert.False(new CompProperties_CelestialVisual { key = "visual", profile = p }.Resolve(new(1, "alpha", "visual")).Success);
    }

    [Fact]
    public void ExistingCompiledExtensionsKeepTheOriginalProfileConstructorAndDefMethod()
    {
        var ctor = typeof(CelestialVisualProfile).GetConstructor(new[] { typeof(string), typeof(double), typeof(double), typeof(double), typeof(double),
            typeof(string), typeof(string), typeof(double), typeof(double), typeof(double), typeof(double) });
        Assert.NotNull(ctor);
        var old = (CelestialVisualProfile)ctor.Invoke(new object[] { "control", 0.2, 0.4, 0.8, 1d, null, null, 10000d, 5000d, 600000d, 6000d });
        Assert.Equal(Legacy, Encoding.UTF8.GetString(new VisualResolvedProperties("clouds", CelestialVisualKind.Clouds, old).Serialize().Span));
        var method = typeof(CelestialVisualProfileDef).GetMethod("ToProfile", Type.EmptyTypes);
        Assert.NotNull(method);
        Assert.True(((CelestialResult<CelestialVisualProfile>)method.Invoke(new CelestialVisualProfileDef { defName = "PF_Old" }, null)).Success);
    }
    private sealed class NoResources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => throw new Exception("No surface");
    }
    private static CelestialVisualProfileDef Profile() => new() { defName = "PF_VisualProbe", visualVersion = 2, appearance = new() };
    private static CelestialBodyTemplate Template(params CelestialCompProperties[] visual) => CelestialBodyTemplate.Create("PF_Probe", "probe", "VisualProbe", Array.Empty<string>(),
        new CelestialCompProperties[] { new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 1000 } } }.Concat(visual).Select(p => p.ToDefinition())).Value;
}
