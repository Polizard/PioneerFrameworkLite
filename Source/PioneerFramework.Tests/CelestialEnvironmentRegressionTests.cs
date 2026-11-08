using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// 2026-11-07 P2 批次 A/C/B-2 的回归面：加载期诊断、物理日长优先、曝光/重力边界、
/// 房间时钟自愈、气压随海拔衰减与 CO2 毒性增量。
/// </summary>
public class CelestialEnvironmentRegressionTests
{
    [Fact]
    public void AuthoringDiagnosticsRejectFieldsIgnoredByTheSelectedMode()
    {
        var authored = new CelestialEnvironmentProfileDef {
            defName = "PD_Authored", climate = new CelestialClimateXml { mode = CelestialClimateMode.Authored, illuminationTemperatureCelsius = 12 } };
        Assert.Contains("AuthoredIgnoresIlluminationTemperature", authored.ConfigErrors());
        var committed = new CelestialEnvironmentProfileDef {
            defName = "PD_Committed", climate = new CelestialClimateXml { mode = CelestialClimateMode.CommittedIllumination, diurnalAmplitudeCelsius = 8 } };
        Assert.Contains("CommittedIlluminationIgnoresDiurnalAmplitude", committed.ConfigErrors());
        var clean = new CelestialEnvironmentProfileDef {
            defName = "PD_Clean", climate = new CelestialClimateXml { mode = CelestialClimateMode.Authored, diurnalAmplitudeCelsius = 8 } };
        Assert.DoesNotContain("AuthoredIgnoresIlluminationTemperature", clean.ConfigErrors());
        Assert.DoesNotContain("CommittedIlluminationIgnoresDiurnalAmplitude", clean.ConfigErrors());
    }

    [Fact]
    public void EnvironmentPhysicsRequiredAcceptsOnlyExactlyOneBinding()
    {
        var climate = new CelestialClimateConfiguration(CelestialClimateMode.CommittedIllumination, 0,
            new[] { new CelestialClimatePoint(0, 0), new CelestialClimatePoint(90, 0) }, 0, 0, 0, 3600000, 0, 0, 1, 0, 0, 0);
        var exposure = new CelestialExposureConfiguration(16000, 20000, 300000, 2, 2, 0, 0, 1000, true);
        var properties = new ExistingEnvironmentResolvedProperties("env",
            new CelestialEnvironmentProfile("PD_Physics", 60000, "sun", Array.Empty<string>(), climate, exposure));
        Assert.False(properties.Validate(new ICelestialResolvedProperties[] { properties }).Success);
        var binding = new ExternalPhysicsResolvedProperties("motion", null,
            new PhysicsInitialization("PFL.ExistingEphemeris", "existing.circular.v1", 1, Array.Empty<byte>()));
        Assert.True(properties.Validate(new ICelestialResolvedProperties[] { properties, binding }).Success);
        Assert.False(properties.Validate(new ICelestialResolvedProperties[] { properties, binding, binding }).Success);
    }

    [Fact]
    public void GravityFactorKeepsItsDocumentedClampBounds()
    {
        Assert.Equal(2, CelestialExposureModel.GravityFactor(.1, true), 12);
        Assert.Equal(.25, CelestialExposureModel.GravityFactor(1000, true), 12);
        Assert.Equal(1.5, CelestialExposureModel.GravityFactor(.1, false), 12);
        Assert.Equal(.5, CelestialExposureModel.GravityFactor(1000, false), 12);
        Assert.Equal(1, CelestialExposureModel.GravityFactor(9.80665, true), 12);
    }

    [Fact]
    public void RoomEnvironmentClockRebaseClampsBackwardTicksAndKeepsAdvancing()
    {
        var topology = new[] { new CelestialRoomTopology(new[] { 0 }, false, 0, false, false, false) };
        var state = new CelestialRoomEnvironmentState("owner");
        state.Advance(600, topology, new(1000, 210, 0, 0));
        Assert.Equal(600, state.LastGameTick);
        state.Rebase(300);
        Assert.Equal(300, state.LastGameTick);
        state.Advance(360, topology, new(1000, 210, 0, 0));
        Assert.Equal(360, state.LastGameTick);
    }

    [Fact]
    public void EnvironmentSamplingUsesPhysicsDayLengthBeforeProfileFallback()
    {
        var sun = CelestialInstanceId.Parse("day:sun"); var planet = CelestialInstanceId.Parse("day:planet");
        CelestialSpawnItem Item(bool star)
        {
            var id = star ? sun : planet;
            var definition = new CelestialBodyDef { defName = star ? "PD_DayStar" : "PD_DayPlanet", comps = new() {
                new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 30 } },
                new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id,
                    motion = new() { orbitRadiusMeters = star ? 0 : 100, orbitPeriodSeconds = star ? 0 : 400,
                        rotation = new() { periodSeconds = 40 } } } } };
            if (star) definition.comps.Add(new CompProperties_LightSource { key = "light" });
            else definition.comps.Add(new CompProperties_ExistingEnvironment { key = "env", profile = new() { defName = "PD_Day", primaryLightReference = "sun", dayLengthTicks = 60000 } });
            var template = definition.ToTemplate().Value;
            return new(id, id.Value, 0, id.Value, template, template.Resolve(new(42, id.Value)).Value,
                parentId: star ? null : sun, initializationReferences: star ? null : new Dictionary<string, CelestialInstanceId> { ["sun"] = sun });
        }
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var runtime = new CelestialRuntime(provider, provider.CommittedStamp, new Resources(), 42, _ => throw new InvalidOperationException());
        runtime.CreateBatch(new(new("day", runtime.WorldSessionId, 0), new("day", 42, null, new[] { Item(true), Item(false) })));
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        // B-2：物理 motion 的 40 tick 必须压过 profile 的 60000 兼容 fallback。
        Assert.Equal(40, Assert.Single(runtime.GetCapabilities<ICelestialDayLengthSource>(planet)).DayLengthTicks, 9);
        var environment = Assert.Single(runtime.GetCapabilities<ICelestialEnvironmentCapability>(planet));
        // EvaluateCycle(10, 40, 0) 先把 tick 缩放到 60000/40 => 15000/60000 = 0.25；用 fallback 60000 则约为 0.000167。
        Assert.Equal(.25, environment.Evaluate(new(10, 0, 0)).DayPercent, 6);
    }

    [Fact]
    public void SurfacePressureFallsWithAltitudeAndStopsAtTopWhileCarbonDioxideRaisesToxicity()
    {
        var sun = CelestialInstanceId.Parse("air:sun"); var planet = CelestialInstanceId.Parse("air:planet");
        CelestialSpawnItem Item(bool star)
        {
            var id = star ? sun : planet;
            var definition = new CelestialBodyDef { defName = star ? "PD_AirStar" : "PD_AirPlanet", comps = new() {
                new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 30 } },
                new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id,
                    gravitationalParameter = new() { value = 9.80665 * 900 }, motion = new() { orbitRadiusMeters = star ? 0 : 100, orbitPeriodSeconds = star ? 0 : 400,
                        rotation = new() { periodSeconds = 40 } } } } };
            if (star) definition.comps.Add(new CompProperties_LightSource { key = "light" });
            else
            {
                definition.comps.Add(new CompProperties_ExistingEnvironment { key = "env", profile = new() { defName = "PD_Air", primaryLightReference = "sun",
                    climate = new() { meanTemperatureCelsius = 0, diurnalAmplitudeCelsius = 0, latitudeCurve = new() { new() { latitudeDegrees = 0 }, new() { latitudeDegrees = 90 } } },
                    exposure = new() { toxicityPerDay = 1, carbonDioxideThresholdPascals = 1000 } } });
                definition.comps.Add(new CompProperties_Atmosphere { key = "air", pressurePascals = new() { value = 100000 },
                    temperatureKelvin = new() { value = 288 }, topAltitude = new() { value = 20000 },
                    composition = new() {
                        new() { gas = "Oxygen", moleFraction = .1 },
                        new() { gas = "CarbonDioxide", moleFraction = .9 } } });
            }
            var template = definition.ToTemplate().Value;
            return new(id, id.Value, 0, id.Value, template, template.Resolve(new(42, id.Value)).Value,
                parentId: star ? null : sun, initializationReferences: star ? null : new Dictionary<string, CelestialInstanceId> { ["sun"] = sun });
        }
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var runtime = new CelestialRuntime(provider, provider.CommittedStamp, new Resources(), 42, _ => throw new InvalidOperationException());
        runtime.CreateBatch(new(new("air", runtime.WorldSessionId, 0), new("air", 42, null, new[] { Item(true), Item(false) })));
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        var capability = Assert.Single(runtime.GetCapabilities<ICelestialSurfaceEnvironmentCapability>(planet));
        var ground = capability.SampleSurface(new(0, 0, 0, 0));
        var high = capability.SampleSurface(new(0, 0, 0, 10000));
        var top = capability.SampleSurface(new(0, 0, 0, 20000));
        Assert.Equal(100000, ground.PressurePascals!.Value, 6);
        Assert.InRange(high.PressurePascals!.Value, 0, 100000);
        Assert.Equal(0, top.PressurePascals!.Value, 6);
        Assert.Equal(ground.PressurePascals.Value * .1, ground.OxygenPartialPressurePascals!.Value, 6);
        // CO2 = 90000 Pa，阈值 1000 => 增量 89 * toxicityPerDay(1)。
        Assert.InRange(ground.ToxicityPerDay, 89, 91);
    }

    private sealed class Resources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
}
