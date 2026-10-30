using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialEnvironmentTests
{
    [Fact]
    public void AtmosphereCapabilityUsesTheUnchangedDensityTableAndSavedResolvedNumbers()
    {
        var properties = new CompProperties_Atmosphere { key = "air", rho0 = new() { value = 1.2 }, scaleHeight = new() { value = 8000 }, topAltitude = new() { value = 100000 } };
        var resolved = (AtmosphereResolvedProperties)properties.Resolve(new(1, "earth", "air")).Value;
        var restored = AtmosphereResolvedProperties.Deserialize("air", 1, resolved.Serialize()).Value;
        Assert.Equal(1.2, restored.Rho0); Assert.Equal(8000, restored.ScaleHeight); Assert.Equal(100000, restored.TopAltitude);
        var baseline = AtmosphereModel.Build(1.2, 8000, 100000);
        var actual = restored.BuildProfile();
        foreach (double altitude in new[] { -1d, 0, 500, 76000, 99999, 100000, 200000 }) Assert.Equal(baseline.Density(altitude), actual.Density(altitude));
        Assert.False(new AtmosphereResolvedProperties("none", 0, 0, 0).BuildProfile().IsActive);
    }

    [Fact]
    public void SharedProfileFreezesPerInstanceAndExplicitPrimaryLightUsesExistingSunModel()
    {
        var primary = CelestialInstanceId.Parse("star:primary");
        var other = CelestialInstanceId.Parse("star:other");
        var one = CelestialInstanceId.Parse("planet:one");
        var two = CelestialInstanceId.Parse("planet:two");
        var profile = new CelestialEnvironmentProfileDef { defName = "PF_ProbeEnvironment", dayLengthTicks = 317900,
            primaryLightReference = "mainLight", vacuumBiomes = new() { new BiomeDef { defName = "CustomVacuum", inVacuum = true } } };
        CelestialBodyTemplate Template(string name, params CelestialCompProperties[] comps) => new CelestialBodyDef { defName = name,
            comps = new[] { new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 1500000 } } }.Cast<CelestialCompProperties>().Concat(comps).ToList() }.ToTemplate().Value;
        var star = Template("AnyStar", new CompProperties_LightSource { key = "light" });
        var body = Template("AnyBody", new CompProperties_ExistingEnvironment { key = "environment", profile = profile });
        CelestialSpawnItem Spawn(CelestialInstanceId id, CelestialBodyTemplate template) => new(id, id.Value, 0, id.Value,
            template, template.Resolve(new(1, id.Value)).Value, initializationReferences: new Dictionary<string, CelestialInstanceId> { ["mainLight"] = primary });
        var manifest = new CelestialSpawnManifest("environment", 1, null, new[] { Spawn(primary, star), Spawn(other, star), Spawn(one, body), Spawn(two, body) });
        profile.dayLengthTicks = 1;
        var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid()); bridge.SetFrame(0, new Dictionary<CelestialInstanceId, CelestialMotionState>());
        var runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, new EmptyResources(), 1, _ => throw new Exception("No implicit templates"));
        Assert.Equal(CelestialOperationStatus.Pending, runtime.CreateBatch(new(new("environment", runtime.WorldSessionId, 0), manifest)).Status);
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        foreach (var id in new[] { one, two })
        {
            var capability = runtime.Creation.GetComponents(id).OfType<ICelestialEnvironmentCapability>().Single();
            foreach (double ticks in new[] { 0d, 13000d, 317900d, -500d })
            {
                var result = capability.Evaluate(new(ticks, 23f, 12f, "CustomVacuum"));
                long scaled = (long)((long)ticks * (60000.0 / 317900f));
                double days = (scaled + (double)(23f / 360f * 60000f)) / 60000.0;
                float day = (float)(((days % 1) + 1) % 1);
                int yearDay = (int)(((Math.Floor(days) % 60) + 60) % 60);
                Assert.Equal(day, result.DayPercent);
                Assert.Equal(PF_SunMath.SunGlowPercent(12f, yearDay, day), result.SunGlow);
                Assert.Equal(primary, result.PrimaryLightId);
                Assert.Equal("CustomVacuum", result.VacuumBiomeId);
            }
        }
        Assert.Equal(2, runtime.Catalog.CaptureInstances().Count(instance => runtime.Creation.GetComponents(instance.Id).OfType<ICelestialLightSourceCapability>().Any()));
        Assert.Contains(runtime.CanRemove(primary).Blockers, error => error.Code == "InitializationReference");
        Assert.True(runtime.CanRemove(other).Allowed);

        using var native = new CelestialNativeWorldTestScope();
        Assert.True(native.Game.GetComponent<CelestialBodyManager>().AttachCelestialRuntime(runtime).Success);
        var layerDef = new PlanetLayerDef { defName = "PF_EnvironmentProbe", layerType = typeof(CelestialSurfaceLayer), tileType = typeof(SurfaceTile), worldDrawLayers = new() };
        native.AddDefs(layerDef);
        var layer = RuntimePlanetLayerService.RegisterCelestialInstance(native.Grid, layerDef,
            new CelestialSurfaceHeader(one, -1, 1500000, 30, 0, 1, CelestialGenerationConfiguration.Encode(runtime.Catalog.CaptureInstances().Single(instance => instance.Id == one).CompStateRecords.Values)));
        var biomeDef = new BiomeDef { defName = "CustomVacuum", inVacuum = true };
        for (int index = 0; index < layer.TilesCount; index++) layer.Tiles.Add(new SurfaceTile(new PlanetTile(index, layer)) { PrimaryBiome = biomeDef });
        var backend = new CelestialMapIdentityTests.Backend();
        var parent = backend.CreateParent(new(one, layer.LayerID, 0));
        var map = backend.Generate(parent, 250, 250, null);
        var longLat = native.Grid.LongLatOf(map.Tile);
        var expected = runtime.GetCapabilities<ICelestialEnvironmentCapability>(one).Single().Evaluate(new(0, longLat.x, longLat.y, biomeDef.defName));
        float glow = -1, dayPercent = -1;
        Assert.False(CalendarAndSunPatch.CurCelestialSunGlow_Prefix(map, ref glow));
        Assert.False(CalendarAndSunPatch.GenLocalDate_DayPercent_Map_Prefix(map, ref dayPercent));
        Assert.Equal(expected.SunGlow, glow); Assert.Equal(expected.DayPercent, dayPercent);
    }
    [Fact]
    public void EnvironmentRequiresAnExplicitResolvableLightAndInvalidProfilesAreRejected()
    {
        Assert.False(new CelestialEnvironmentProfileDef { defName = "Bad", dayLengthTicks = 0, primaryLightReference = "main" }.ToProfile().Success);
        Assert.False(new CelestialEnvironmentProfileDef { defName = "Bad", primaryLightReference = "" }.ToProfile().Success);
        Assert.False(new CompProperties_Atmosphere { key = "bad", rho0 = new() { value = -1 } }.Resolve(new(1, "bad")).Success);
        var definition = new CelestialBodyDef { defName = "PF_MissingPrimary", category = "Star", comps = new() {
            new CompProperties_SphericalBody { key = "shape" }, new CompProperties_ExistingEnvironment { key = "environment",
                profile = new CelestialEnvironmentProfileDef { defName = "PF_ExplicitEnvironment", primaryLightReference = "main" } } } };
        var template = definition.ToTemplate().Value;
        var item = new CelestialSpawnItem(CelestialInstanceId.Parse("missing-light"), "body", 0, "body", template, template.Resolve(new(1, "body")).Value);
        var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid()); bridge.SetFrame(0, new Dictionary<CelestialInstanceId, CelestialMotionState>());
        var runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, new EmptyResources(), 1, _ => throw new Exception("No implicit components"));
        Assert.Equal(CelestialOperationStatus.Pending, runtime.CreateBatch(new(new("missing", runtime.WorldSessionId, 0), new("missing", 1, null, new[] { item }))).Status);
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        Assert.Equal(CelestialOperationStatus.Rejected, runtime.GetOperation("missing").Status);
        Assert.Contains(runtime.GetOperation("missing").Errors, error => error.Detail.Contains("PrimaryLightUnavailable", StringComparison.Ordinal));
        Assert.Empty(runtime.GetCapabilities<ICelestialLightSourceCapability>(item.Id));
    }
    private sealed class EmptyResources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
}
