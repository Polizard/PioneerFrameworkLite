using System;
using System.Text;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPlanetClimateTests
{
    [Fact]
    public void LatitudeAltitudeAndAnalyticThermalResponseAreBoundedAndReadOrderIndependent()
    {
        var xml = new CelestialClimateXml { meanTemperatureCelsius = 20, diurnalAmplitudeCelsius = 10, thermalResponseSeconds = 100 };
        var climate = xml.Resolve();
        var noon = new CelestialSurfaceEnvironmentQuery(30000, 0, 0);
        double temperature = CelestialClimateModel.Temperature(climate, noon, 60000, 1);
        Assert.Equal(20 + 10 / (1 + Math.Pow(2 * Math.PI * 100 / 60000, 2)), temperature, 10);
        Assert.Equal(temperature - 6.5, CelestialClimateModel.Temperature(climate, noon with { AltitudeMeters = 1000 }, 60000, 1), 10);
        Assert.Equal(-20, CelestialClimateModel.Temperature(climate, noon with { LatitudeDegrees = 90 }, 60000, 1), 10);
        Assert.Equal(temperature, CelestialClimateModel.Temperature(climate, noon, 60000, 1));
        Assert.Equal(CelestialClimateModel.Temperature(climate, noon with { LongitudeDegrees = -180 }, 60000, 1),
            CelestialClimateModel.Temperature(climate, noon with { LongitudeDegrees = 180 }, 60000, 1), 10);
    }

    [Fact]
    public void AuthoredPhaseOverridePutsTheDiurnalPeakAtLocalNoon()
    {
        // 2026-11-07 P1-1：纯核新增的可选 authoredPhaseRadians（0 = 本地正午）。传 null 保持旧日历相位。
        var climate = new CelestialClimateXml { meanTemperatureCelsius = 0, diurnalAmplitudeCelsius = 100,
            thermalResponseSeconds = 0, latitudeCurve = new() { new() { latitudeDegrees = 0 }, new() { latitudeDegrees = 90 } } }.Resolve();
        var query = new CelestialSurfaceEnvironmentQuery(12345, 0, 0);
        Assert.Equal(100, CelestialClimateModel.Temperature(climate, query, 60000, 1, authoredPhaseRadians: 0), 9);
        Assert.Equal(-100, CelestialClimateModel.Temperature(climate, query, 60000, 1, authoredPhaseRadians: Math.PI), 9);
        Assert.Equal(0, CelestialClimateModel.Temperature(climate, query, 60000, 1, authoredPhaseRadians: Math.PI / 2), 9);
        Assert.Equal(CelestialClimateModel.Temperature(climate, query, 60000, 1),
            CelestialClimateModel.Temperature(climate, query, 60000, 1, authoredPhaseRadians: null), 12);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CelestialClimateModel.Temperature(climate, query, 60000, 1, authoredPhaseRadians: double.NaN));
    }

    [Fact]
    public void ConfigurationVersionMismatchAndInvalidCurveReject()
    {
        var xml = new CelestialClimateXml();
        var definition = new CelestialEnvironmentProfileDef { defName = "climate", climate = xml };
        var resolved = new ExistingEnvironmentResolvedProperties("env", definition.ToProfile().Value);
        Assert.False(ExistingEnvironmentResolvedProperties.Deserialize("env", 1, resolved.Serialize()).Success);
        xml.latitudeCurve.Reverse();
        Assert.False(definition.ToProfile().Success);
        var old = new ExistingEnvironmentResolvedProperties("env", new("old", 60000, "sun", Array.Empty<string>()));
        Assert.False(ExistingEnvironmentResolvedProperties.Deserialize("env", 2, old.Serialize()).Success);
    }
    [Fact]
    public void NumericParametersSampleDeterministicallyAndConsumeOnlyKnownOverrides()
    {
        var definition = new CelestialBodyDef { defName = "RangePlanet", comps = new() {
            new CompProperties_SphericalBody { key = "shape" }, new CompProperties_ExistingEnvironment { key = "env", profile = new() {
                defName = "range", climate = new() { parameters = new() { new() { key = "meanTemperatureCelsius", value = new() { min = -50, max = 50 } } } }, exposure = new() } } } };
        var template = definition.ToTemplate().Value;
        ExistingEnvironmentResolvedProperties Resolve(string path, params CelestialParameterOverride[] overrides) =>
            (ExistingEnvironmentResolvedProperties)System.Linq.Enumerable.Single(template.Resolve(new(42, path, parameterOverrides: overrides)).Value,
                p => p.Descriptor.Key == "env").Properties;
        Assert.Equal(Resolve("one").Serialize().ToArray(), Resolve("one").Serialize().ToArray());
        Assert.NotEqual(Resolve("one").Profile.Climate.MeanTemperatureCelsius, Resolve("two").Profile.Climate.MeanTemperatureCelsius);
        Assert.Equal(37, Resolve("one", new CelestialParameterOverride("env", "climate.meanTemperatureCelsius", 37)).Profile.Climate.MeanTemperatureCelsius);
        Assert.Equal(3, Resolve("one", new CelestialParameterOverride("env", "exposure.toxicityPerDay", 3)).Profile.Exposure.ToxicityPerDay);
        Assert.False(template.Resolve(new(42, "one", parameterOverrides: new[] { new CelestialParameterOverride("env", "climate.typo", 1) })).Success);
    }
    [Fact]
    public void EntireNumericRangeMustBeValidBeforeSampling()
    {
        var profile = new CelestialEnvironmentProfileDef { defName = "invalid-range", climate = new() {
            parameters = new() { new() { key = "humidity", value = new() { min = 0, max = 2 } } } } };
        Assert.False(profile.ToProfile().Success);
        profile.climate.parameters[0] = new() { key = "meanTemperatureCelsius", value = new() { min = -300, max = 20 } };
        Assert.False(profile.ToProfile().Success);
    }
    [Fact]
    public void PressureRangesMustRemainOrderedForEveryPossibleSample()
    {
        var profile = new CelestialEnvironmentProfileDef { defName = "pressure-range", exposure = new() { parameters = new() {
            new() { key = "minimumPressurePascals", value = new() { min = 10000, max = 500000 } },
            new() { key = "maximumPressurePascals", value = new() { min = 100000, max = 900000 } } } } };
        Assert.False(profile.ToProfile().Success);
    }
    [Fact]
    public void LegacyEnvironmentRetainsExactV1Payload()
    {
        var p = new ExistingEnvironmentResolvedProperties("environment", new("cold", 60000, "sun", Array.Empty<string>()));
        Assert.Equal(1, p.Version);
        Assert.Equal("{\"Id\":\"cold\",\"DayLengthTicks\":60000,\"PrimaryLightReference\":\"sun\",\"VacuumBiomeIds\":[]}", Encoding.UTF8.GetString(p.Serialize().Span));
    }

    [Fact]
    public void ExplicitClimateIsFrozenInVersionTwoWithoutAddingAtmosphere()
    {
        var field = typeof(CelestialEnvironmentProfileDef).GetField("climate");
        Assert.NotNull(field);
        var climate = Activator.CreateInstance(field!.FieldType)!;
        field.FieldType.GetField("meanTemperatureCelsius")!.SetValue(climate, -40d);
        var definition = new CelestialEnvironmentProfileDef { defName = "PF_ClimateProbe" };
        field.SetValue(definition, climate);
        var resolved = new ExistingEnvironmentResolvedProperties("environment", definition.ToProfile().Value);
        Assert.Equal(2, resolved.Version);
        var saved = resolved.Serialize().ToArray();
        field.FieldType.GetField("meanTemperatureCelsius")!.SetValue(climate, 80d);
        var restored = ExistingEnvironmentResolvedProperties.Deserialize("environment", 2, saved);
        Assert.True(restored.Success);
        Assert.Equal(saved, restored.Value.Serialize().ToArray());
        Assert.Contains("-40", Encoding.UTF8.GetString(saved));
    }
}
