using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPlanetParameterTests
{
    internal static T Set<T>(T target, string field, object value)
    {
        var member = target!.GetType().GetField(field);
        Assert.NotNull(member);
        member.SetValue(target, value);
        return target;
    }
    internal static object New(string name) => Activator.CreateInstance(typeof(CelestialBodyTemplate).Assembly.GetType("PioneerFramework.Celestial." + name, true)!)!;
    internal static CelestialBodyTemplate Template(params CelestialCompProperties[] properties)
    {
        var result = CelestialBodyTemplate.Create("probe", "probe", "OpenCategory", Array.Empty<string>(), properties.Select(p => p.ToDefinition()));
        Assert.True(result.Success, string.Join(",", result.Errors.Select(e => e.Code)));
        return result.Value;
    }
    internal static CompProperties_SphericalBody Shape(double radius = 6371000) => new() { key = "shape", radiusMeters = new() { value = radius } };
    internal static CompProperties_ExternalPhysicsBinding Motion(double density = 5514)
        => Set(new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = "external", modeId = "opaque", initializationPayload = "AQID" },
            "meanDensityKilogramsPerCubicMeter", new CelestialDoubleParameterXml { value = density });
    [Fact]
    public void RadiusAndMeanDensityDeriveGmWithStableIndependentResolutions()
    {
        var shape = Shape(); shape.radiusMeters = new() { min = 6e6, max = 7e6 };
        var motion = Motion(); var template = Template(motion, shape);
        var context = new CelestialGenerationContext(77, "planet");
        var a = template.Resolve(context).Value; var b = template.Resolve(context).Value;
        double radius = ((ICelestialGeometryProperties)a.Single(c => c.Descriptor.Key == "shape").Properties).RadiusMeters;
        double gm = ((ICelestialPhysicsBindingProperties)a.Single(c => c.Descriptor.Key == "motion").Properties).GravitationalParameter!.Value;
        Assert.Equal(6.67430e-11 * (4.0 / 3.0) * Math.PI * radius * radius * radius * 5514, gm);
        Assert.Equal(a.Select(c => c.Properties.Serialize().ToArray()), b.Select(c => c.Properties.Serialize().ToArray()));
        shape.radiusMeters = new() { value = 3e6 };
        var c = template.Resolve(context).Value;
        Assert.NotEqual(gm, ((ICelestialPhysicsBindingProperties)c.Single(c => c.Descriptor.Key == "motion").Properties).GravitationalParameter);
        Assert.Equal(radius, ((ICelestialGeometryProperties)a.Single(c => c.Descriptor.Key == "shape").Properties).RadiusMeters);
    }
    [Fact]
    public void DirectGmConflictsWithDensityAndUnusedOverridesRejectOnEveryResolution()
    {
        var motion = Motion(); motion.gravitationalParameter = new() { value = 10 };
        Assert.False(Template(Shape(), motion).Resolve(new(1, "p")).Success);
        var clean = Template(Shape(), Motion());
        var context = new CelestialGenerationContext(1, "p", parameterOverrides: new[] { new CelestialParameterOverride("motion", "misspelled", Value: 4) });
        Assert.False(clean.Resolve(context).Success); Assert.False(clean.Resolve(context).Success);
    }
    [Fact]
    public void LegacyDensityAndOpaquePhysicsBytesRoundTripWithoutChanges()
    {
        var atmosphere = new AtmosphereResolvedProperties("air", 1.2, 8500, 100000);
        Assert.Equal("{\"Rho0\":1.2,\"ScaleHeight\":8500,\"TopAltitude\":100000}", System.Text.Encoding.UTF8.GetString(atmosphere.Serialize().Span));
        Assert.Equal(atmosphere.Serialize().ToArray(), AtmosphereResolvedProperties.Deserialize("air", 1, atmosphere.Serialize()).Value.Serialize().ToArray());
        var physics = new ExternalPhysicsResolvedProperties("motion", 42, new("third.party", "opaque", 9, new byte[] { 1, 2, 3 }));
        Assert.Equal(physics.Serialize().ToArray(), ExternalPhysicsResolvedProperties.Deserialize("motion", 1, physics.Serialize()).Value.Serialize().ToArray());
    }
}

public class CelestialPlanetAtmosphereTests
{
    [Fact]
    public void ExplicitGasRegistrationResolvesCustomCompositionAndFreezesMolarMassForRestore()
    {
        var type = typeof(AtmosphereResolvedProperties).Assembly.GetType("PioneerFramework.Celestial.CelestialAtmosphereGasRegistry"); Assert.NotNull(type);
        object registry = type.GetProperty("Global")!.GetValue(null)!;
        var register = type.GetMethod("Register")!;
        CelestialResult<bool> Add(string id, double mass) => (CelestialResult<bool>)register.Invoke(registry, new object[] { id, mass })!;
        Assert.True(Add("test.Task1.CustomGas", .050).Success);
        Assert.False(Add("test.Task1.CustomGas", .051).Success);
        Assert.False(Add("invalid gas", .050).Success);
        Assert.False(Add("invalidMass", 0).Success); Assert.False(Add("invalidMass", double.NaN).Success);
        var definition = Air(); definition.composition = new() { new() { gas = "test.Task1.CustomGas", moleFraction = 1 } };
        var resolved = Resolve(definition);
        Assert.Equal(101325 * .050 / (8.31446261815324 * 288.15), resolved.Rho0);
        var restored = AtmosphereResolvedProperties.Deserialize("air", 2, resolved.Serialize()); Assert.True(restored.Success);
        Assert.Equal(resolved.Serialize().ToArray(), restored.Value.Serialize().ToArray());
        Assert.Equal(resolved.Sample(125), restored.Value.Sample(125));
        string frozenJson = System.Text.Encoding.UTF8.GetString(resolved.Serialize().Span).Replace("test.Task1.CustomGas", "test.Task1.FrozenUnregistered", StringComparison.Ordinal);
        var frozen = AtmosphereResolvedProperties.Deserialize("air", 2, System.Text.Encoding.UTF8.GetBytes(frozenJson));
        Assert.True(frozen.Success); Assert.Equal(resolved.Sample(125), frozen.Value.Sample(125));
        definition.composition[0].gas = "test.Task1.Unregistered";
        Assert.NotEmpty(definition.ConfigErrors());
    }
    [Fact]
    public void UnknownGasCompositionXmlFieldRejectsRatherThanDefaulting()
    {
        var gas = new CelestialGasFractionXml();
        var method = gas.GetType().GetMethod("LoadDataFromXmlCustom"); Assert.NotNull(method);
        var doc = new System.Xml.XmlDocument(); doc.LoadXml("<li><gas>Nitrogen</gas><moleFraction>1</moleFraction><misspelled>2</misspelled></li>");
        method.Invoke(gas, new object[] { doc.DocumentElement });
        var air = Air(); air.composition = new() { gas };
        Assert.NotEmpty(air.ConfigErrors());
    }
    [Fact]
    public void LegacyDensityConfigurationDoesNotAdvertiseCompositionCapability()
    {
        var legacy = new CompProperties_Atmosphere { key = "air", rho0 = new() { value = 1.2 }, scaleHeight = new() { value = 8500 } };
        Assert.False(legacy.CreateComp() is ICelestialAtmosphereCompositionCapability);
        Assert.DoesNotContain("atmosphere.composition", legacy.Descriptor.Provides);
        Assert.IsAssignableFrom<ICelestialAtmosphereCompositionCapability>(Air().CreateComp());
    }
    internal static CompProperties_Atmosphere Air(double pressure = 101325, double temperature = 288.15)
    {
        var air = new CompProperties_Atmosphere { key = "air", topAltitude = new() { value = 100000 } };
        CelestialPlanetParameterTests.Set(air, "pressurePascals", new CelestialDoubleParameterXml { value = pressure });
        CelestialPlanetParameterTests.Set(air, "temperatureKelvin", new CelestialDoubleParameterXml { value = temperature });
        var listType = air.GetType().GetField("composition")!.FieldType;
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var pair in new[] { ("Nitrogen", .79), ("Oxygen", .21) })
        {
            object item = Activator.CreateInstance(listType.GetGenericArguments()[0])!;
            CelestialPlanetParameterTests.Set(item, "gas", pair.Item1); CelestialPlanetParameterTests.Set(item, "moleFraction", pair.Item2); list.Add(item);
        }
        CelestialPlanetParameterTests.Set(air, "composition", list);
        return air;
    }
    internal static AtmosphereResolvedProperties Resolve(CompProperties_Atmosphere air, double gm = 3.986004418e14)
    {
        var motion = new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = "external", modeId = "opaque", gravitationalParameter = new() { value = gm } };
        var result = CelestialPlanetParameterTests.Template(air, motion, CelestialPlanetParameterTests.Shape()).Resolve(new(4, "p"));
        Assert.True(result.Success, string.Join(",", result.Errors.Select(e => e.Code + ":" + e.Detail)));
        return (AtmosphereResolvedProperties)result.Value.Single(c => c.Descriptor.Key == "air").Properties;
    }
    internal static object Sample(AtmosphereResolvedProperties air, double height) => air.GetType().GetMethod("Sample")!.Invoke(air, new object[] { height })!;
    internal static double Read(object sample, string name) => (double)sample.GetType().GetProperty(name)!.GetValue(sample)!;
    [Fact]
    public void CompositionDerivesIdealGasDensityAndScaleHeightFromResolvedGeometryAndGm()
    {
        var air = Resolve(Air()); Assert.Equal(2, air.Version);
        var sample = Sample(air, 0);
        Assert.InRange(Read(sample, "DensityKilogramsPerCubicMeter"), 1.21, 1.23);
        Assert.Equal(101325 * .21, Read(sample, "OxygenPartialPressurePascals"));
        Assert.Equal(0, Read(sample, "CarbonDioxidePartialPressurePascals"));
        double molarMass = .79 * .0280134 + .21 * .0319988;
        Assert.Equal(8.31446261815324 * 288.15 / (molarMass * (3.986004418e14 / (6371000.0 * 6371000))), air.ScaleHeight);
        Assert.True(Read(Sample(air, 1000), "PressurePascals") < 101325);
        Assert.Equal(0, Read(Sample(air, 100000), "PressurePascals"));
        var restored = AtmosphereResolvedProperties.Deserialize("air", 2, air.Serialize()); Assert.True(restored.Success);
        Assert.Equal(air.Serialize().ToArray(), restored.Value.Serialize().ToArray());
        var component = Air().Create(air);
        var record = CelestialCompStateCodec.Encode(component, air);
        Assert.True(CelestialCompStateCodec.Decode(record, CelestialBuiltinCompRegistry.Create()).Value.IsReady);
    }
    [Theory]
    [InlineData("Nitrogen", -.1)] [InlineData("Nitrogen", .8)] [InlineData("Unobtainium", .79)]
    public void InvalidCompositionRejects(string gas, double fraction)
    {
        var air = Air(); var list = (System.Collections.IList)air.GetType().GetField("composition")!.GetValue(air)!;
        CelestialPlanetParameterTests.Set(list[0]!, "gas", gas); CelestialPlanetParameterTests.Set(list[0]!, "moleFraction", fraction);
        Assert.NotEmpty(air.ConfigErrors());
    }
    [Theory]
    [InlineData(0)] [InlineData(-10)] [InlineData(double.NaN)]
    public void InvalidTemperaturesReject(double temperature) => Assert.NotEmpty(Air(temperature: temperature).ConfigErrors());
    [Fact]
    public void MixedLegacyAndPressureFormsAndZeroGravityDerivedScaleHeightReject()
    {
        var mixed = Air(); mixed.rho0 = new() { value = 1 };
        Assert.NotEmpty(mixed.ConfigErrors());
        var air = Air();
        var motion = new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = "x", modeId = "x", gravitationalParameter = new() { value = 0 } };
        Assert.False(CelestialPlanetParameterTests.Template(air, motion, CelestialPlanetParameterTests.Shape()).Resolve(new(1, "p")).Success);
    }
    [Fact]
    public void PressureConfigurationFreezesCompositionAndRejectsCorruptVersionTwoPayloads()
    {
        var definition = Air(); var resolved = Resolve(definition);
        definition.composition[0].moleFraction = .5;
        Assert.Equal(.79, resolved.Composition[0].MoleFraction);
        Assert.Throws<NotSupportedException>(() => ((IList<CelestialGasFraction>)resolved.Composition)[0] = new("Helium", 1));
        string json = System.Text.Encoding.UTF8.GetString(resolved.Serialize().Span);
        foreach (string invalid in new[] { json.Replace("\"TemperatureKelvin\":288.15", "\"TemperatureKelvin\":0", StringComparison.Ordinal),
            json.Replace("\"PressurePascals\":101325", "\"PressurePascals\":101325,\"unknown\":0", StringComparison.Ordinal),
            json.Replace("\"MolarMassKilogramsPerMole\":0.0280134", "\"MolarMassKilogramsPerMole\":-1", StringComparison.Ordinal) })
            Assert.False(AtmosphereResolvedProperties.Deserialize("air", 2, System.Text.Encoding.UTF8.GetBytes(invalid)).Success);
        Assert.Throws<ArgumentOutOfRangeException>(() => resolved.Sample(double.NaN));
    }
}
