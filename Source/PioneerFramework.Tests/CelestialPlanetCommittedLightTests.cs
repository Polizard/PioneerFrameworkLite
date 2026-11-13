using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using Xunit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Tests;
public class CelestialPlanetCommittedLightTests
{
    [Fact]
    public void RotatingCommittedIlluminationRetainsHeatAfterNoonRatherThanBeforeIt()
    {
        var sun = CelestialInstanceId.Parse("thermal:sun"); var planet = CelestialInstanceId.Parse("thermal:planet");
        double angularSpeed = 2 * Math.PI * (1d / 40 + 1d / 400);
        CelestialSpawnItem Item(bool star)
        {
            var id = star ? sun : planet;
            var definition = new CelestialBodyDef { defName = star ? "ThermalStar" : "ThermalPlanet", comps = new() {
                new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 30 } },
                new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id,
                    motion = new() { orbitRadiusMeters = star ? 0 : 100, orbitPeriodSeconds = star ? 0 : 400,
                        rotation = new() { periodSeconds = 40 } } } } };
            if (star) definition.comps.Add(new CompProperties_LightSource { key = "light" });
            else definition.comps.Add(new CompProperties_ExistingEnvironment { key = "env", profile = new() { defName = "thermal", primaryLightReference = "sun",
                climate = new() { mode = CelestialClimateMode.CommittedIllumination, meanTemperatureCelsius = -100,
                    illuminationTemperatureCelsius = 100, thermalResponseSeconds = 1 / angularSpeed,
                    latitudeCurve = new() { new() { latitudeDegrees = 0 }, new() { latitudeDegrees = 90 } } } } });
            var template = definition.ToTemplate().Value;
            return new(id, id.Value, 0, id.Value, template, template.Resolve(new(42, id.Value)).Value,
                parentId: star ? null : sun, initializationReferences: star ? null : new Dictionary<string, CelestialInstanceId> { ["sun"] = sun });
        }
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var runtime = new CelestialRuntime(provider, provider.CommittedStamp, new Resources(), 42, _ => throw new InvalidOperationException());
        runtime.CreateBatch(new(new("thermal", runtime.WorldSessionId, 0), new("thermal", 42, null, new[] { Item(true), Item(false) })));
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        var climate = Assert.Single(runtime.GetCapabilities<ICelestialSurfaceEnvironmentCapability>(planet));
        var morning = climate.SampleSurface(new(0, -135, 0));
        var afternoon = climate.SampleSurface(new(0, -45, 0));
        Assert.Equal(Math.Sqrt(.5), morning.SunGlow, 10); Assert.Equal(morning.SunGlow, afternoon.SunGlow, 10);
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, runtime.Catalog.Revision, 0, .001)).Success);
        Assert.True(climate.SampleSurface(new(0, -135, 0)).SunUp > morning.SunUp);
        Assert.True(climate.SampleSurface(new(0, -45, 0)).SunUp < afternoon.SunUp);
        Assert.Equal(-100, morning.TemperatureCelsius!.Value, 10);
        Assert.Equal(-100 + 100 * Math.Sqrt(.5), afternoon.TemperatureCelsius!.Value, 10);
    }
    [Fact]
    public void AuthoredDiurnalTemperaturePeaksWhereThePhysicalSunIsHighest()
    {
        // 2026-11-07 P1-1：有物理绑定时 Authored 日相位必须跟物理太阳，而不是日历 tick。
        var sun = CelestialInstanceId.Parse("authored:sun"); var planet = CelestialInstanceId.Parse("authored:planet");
        CelestialSpawnItem Item(bool star)
        {
            var id = star ? sun : planet;
            var definition = new CelestialBodyDef { defName = star ? "AuthoredStar" : "AuthoredPlanet", comps = new() {
                new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 30 } },
                new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id,
                    motion = new() { orbitRadiusMeters = star ? 0 : 100, orbitPeriodSeconds = star ? 0 : 400,
                        rotation = new() { periodSeconds = 40 } } } } };
            if (star) definition.comps.Add(new CompProperties_LightSource { key = "light" });
            else definition.comps.Add(new CompProperties_ExistingEnvironment { key = "env", profile = new() { defName = "authored", primaryLightReference = "sun",
                climate = new() { mode = CelestialClimateMode.Authored, meanTemperatureCelsius = 0, diurnalAmplitudeCelsius = 100,
                    thermalResponseSeconds = 0, latitudeCurve = new() { new() { latitudeDegrees = 0 }, new() { latitudeDegrees = 90 } } } } });
            var template = definition.ToTemplate().Value;
            return new(id, id.Value, 0, id.Value, template, template.Resolve(new(42, id.Value)).Value,
                parentId: star ? null : sun, initializationReferences: star ? null : new Dictionary<string, CelestialInstanceId> { ["sun"] = sun });
        }
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var runtime = new CelestialRuntime(provider, provider.CommittedStamp, new Resources(), 42, _ => throw new InvalidOperationException());
        runtime.CreateBatch(new(new("authored", runtime.WorldSessionId, 0), new("authored", 42, null, new[] { Item(true), Item(false) })));
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        var capability = Assert.Single(runtime.GetCapabilities<ICelestialSurfaceEnvironmentCapability>(planet));
        // 赤道一圈经度：热响应为 0 时，Authored 温度的最大值必须与物理太阳最高的经度重合。
        var samples = Enumerable.Range(0, 180).Select(index => capability.SampleSurface(new(0, -180 + index * 2, 0))).ToArray();
        int hottest = 0, highest = 0;
        for (int index = 1; index < samples.Length; index++)
        {
            if (samples[index].TemperatureCelsius > samples[hottest].TemperatureCelsius) hottest = index;
            if (samples[index].SunUp > samples[highest].SunUp) highest = index;
        }
        Assert.Equal(highest, hottest);
        Assert.True(samples.Max(sample => sample.TemperatureCelsius!.Value) > 99);
        Assert.True(samples.Min(sample => sample.TemperatureCelsius!.Value) < -99);
    }
    [Theory]
    [InlineData(1, .5, CelestialClimateMode.CommittedIllumination)]
    [InlineData(4, 2, CelestialClimateMode.CommittedIllumination)]
    [InlineData(1, .5, CelestialClimateMode.Authored)]
    [InlineData(4, 2, CelestialClimateMode.Authored)]
    public void SynchronousLightAndGravityUseOneCommittedSnapshotAndSurviveFrozenRestore(double gravityMultiplier, double intensity, CelestialClimateMode mode)
    {
        using var diagnostics = new CelestialExpectedRestoreErrorScope();
        using var native = new CelestialNativeWorldTestScope();
        native.AddDefs(new RimWorld.PlanetLayerDef { defName = "PF_CelestialSurface", layerType = typeof(CelestialSurfaceLayer), tileType = typeof(RimWorld.Planet.SurfaceTile), worldDrawLayers = new() });
        var sun = CelestialInstanceId.Parse("star"); var planet = CelestialInstanceId.Parse("planet");
        CelestialBodyTemplate Template(bool star)
        {
            var definition = new CelestialBodyDef { defName = star ? "Star" : "Planet", comps = new() {
                new CompProperties_SphericalBody { key = "shape", radiusMeters = new() { value = 30 } },
                new CompProperties_ExternalPhysicsBinding { key = "motion", providerId = ExistingCelestialEphemerisBridge.Id,
                    gravitationalParameter = new() { value = 9.80665 * 900 * gravityMultiplier }, motion = new() { orbitRadiusMeters = star ? 0 : 100,
                        orbitPeriodSeconds = star ? 0 : 400, rotation = star ? new() { periodSeconds = 20 } : new() { synchronousTargetReference = "sun" } } } } };
            if (star) definition.comps.Add(new CompProperties_LightSource { key = "light", intensity = new() { value = intensity } });
            else definition.comps.Add(new CompProperties_ExistingEnvironment { key = "env", profile = new() { defName = "locked", primaryLightReference = "sun",
                climate = new() { mode = mode, meanTemperatureCelsius = -100, seasonalAmplitudeCelsius = mode == CelestialClimateMode.CommittedIllumination ? 10 : 0,
                    latitudeCurve = new() { new() { latitudeDegrees = 0 }, new() { latitudeDegrees = 90 } } }, exposure = new() { gravityStats = mode == CelestialClimateMode.CommittedIllumination } } });
            return definition.ToTemplate().Value;
        }
        CelestialSpawnItem Item(CelestialInstanceId id, bool star)
        {
            var template = Template(star); return new(id, id.Value, 0, id.Value, template, template.Resolve(new(42, id.Value)).Value,
                parentId: star ? null : sun, initializationReferences: star ? null : new Dictionary<string, CelestialInstanceId> { ["sun"] = sun });
        }
        var provider = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var runtime = new CelestialRuntime(provider, provider.CommittedStamp, new Resources(), 42, _ => throw new InvalidOperationException("No current XML"));
        runtime.CreateBatch(new(new("create", runtime.WorldSessionId, 0), new("light", 42, null, new[] { Item(sun, true), Item(planet, false) })));
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        Assert.Equal(CelestialOperationStatus.Applied, runtime.GetOperation("create").Status);
        var capability = Assert.Single(runtime.GetCapabilities<ICelestialSurfaceEnvironmentCapability>(planet));
        var day = capability.SampleSurface(new(0, 0, 0)); var night = capability.SampleSurface(new(0, 180, 0));
        Assert.Equal(intensity, day.SunGlow, 10); Assert.Equal(0, night.SunGlow, 10); Assert.Equal(9.80665 * gravityMultiplier, day.GravityMetersPerSecondSquared.Value, 10);
        Assert.Equal(mode == CelestialClimateMode.CommittedIllumination ? -100 + 50 * intensity : -100, day.TemperatureCelsius.Value, 10);
        Assert.True(day.UsesCommittedLight);
        Assert.Equal(day, capability.SampleSurface(new(900000, 0, 0)));
        Assert.Equal(capability.SampleSurface(new(0, 0, 45)), capability.SampleSurface(new(900000, 0, 45)));
        Assert.True(native.Game.GetComponent<CelestialBodyManager>().AttachCelestialRuntime(runtime).Success);
        var layer = RuntimePlanetLayerService.RegisterCelestialInstance(native.Grid, DefDatabase<PlanetLayerDef>.GetNamed("PF_CelestialSurface"),
            new(planet, -1, 30, 30, 0, 1, ReadOnlyMemory<byte>.Empty));
        for (int i = 0; i < layer.TilesCount; i++) layer.Tiles.Add(new SurfaceTile(new(i, layer)) { PrimaryBiome = new BiomeDef { defName = "plain" } });
        var backend = new CelestialMapIdentityTests.Backend();
        var map = backend.Generate(backend.CreateParent(new(planet, layer.LayerID, 0)), 6, 6, null);
        map.info.Size = new IntVec3(6, 1, 6);
        CelestialPlanetEnvironmentHooks.Install();
        var wind = new WindManager(map); Assert.Equal(0, wind.WindSpeed);
        var pawn = CelestialPlanetExposureTests.BiologicalPawn(); var pawnDef = pawn.def;
        AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(pawn, (sbyte)0);
        float move = 4, carry = 75;
        new StatPart_CelestialGravity().TransformValue(StatRequest.For(pawn), ref move);
        new StatPart_CelestialGravity { carrying = true }.TransformValue(StatRequest.For(pawn), ref carry);
        Assert.Equal(mode == CelestialClimateMode.CommittedIllumination ? 4 * CelestialExposureModel.GravityFactor(9.80665 * gravityMultiplier, false) : 4, move, 5);
        Assert.Equal(mode == CelestialClimateMode.CommittedIllumination ? 75 * CelestialExposureModel.GravityFactor(9.80665 * gravityMultiplier, true) : 75, carry, 5);
        pawnDef.modExtensions = new() { new CelestialEnvironmentProtectionExtension { gravityAlreadyAdapted = true } };
        float adapted = 4; new StatPart_CelestialGravity().TransformValue(StatRequest.For(pawn), ref adapted); Assert.Equal(4, adapted);
        var coords = native.Grid.LongLatOf(map.Tile);
        var input = capability.SampleSurface(new(0, coords.x, coords.y));
        float glow = -1; CalendarAndSunPatch.CurCelestialSunGlow_Prefix(map, ref glow);
        Assert.Equal((float)Math.Clamp(input.SunGlow, 0, 1), glow);
        var light = GenCelestial.GetLightSourceInfo(map, GenCelestial.LightType.LightingSun);
        Assert.Equal((float)Math.Clamp(input.SunGlow, 0, 1), light.intensity);
        var clearWeather = new WeatherDef { defName = "PF_ClearProbe", overlayClasses = new(), maxGlow = 1 };
        // WeatherWorker owns sky attenuation, and native solar plants consume SkyManager.CurSkyGlow.
        var sky = clearWeather.Worker.CurSkyTarget(map);
        Assert.Equal(glow, sky.glow);
        map.skyManager = new SkyManager(map);
        AccessTools.Field(typeof(SkyManager), "curSkyGlowInt").SetValue(map.skyManager, sky.glow);
        map.cellIndices = new CellIndices(map); map.roofGrid = new RoofGrid(map);
        var solarDef = (ThingDef)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
        solarDef.defName = "PF_SolarProbe"; solarDef.size = new IntVec2(1, 1);
        var solarBuilding = new Building { def = solarDef, Position = new IntVec3(1, 0, 1) };
        AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(solarBuilding, (sbyte)0);
        var solar = new CompPowerPlantSolar { parent = solarBuilding };
        var solarProperties = new CompProperties_Power();
        AccessTools.Field(typeof(CompProperties_Power), "basePowerConsumption").SetValue(solarProperties, -1700f);
        solar.Initialize(solarProperties);
        float output = (float)AccessTools.PropertyGetter(typeof(CompPowerPlantSolar), "DesiredPowerOutput").Invoke(solar, null);
        Assert.Equal(1700 * glow, output, 4);
        // Native StatWorker must finalize each part once, including repeated queries at the same committed instant.
        var moveDefinition = new StatDef { defName = "PF_MoveProbe", parts = new() { new StatPart_CelestialGravity() } };
        var carryDefinition = new StatDef { defName = "PF_CarryProbe", parts = new() { new StatPart_CelestialGravity { carrying = true } } };
        pawnDef.modExtensions.Clear();
        float nativeMove = 4, nativeCarry = 75;
        moveDefinition.Worker.FinalizeValue(StatRequest.For(pawn), ref nativeMove, false);
        carryDefinition.Worker.FinalizeValue(StatRequest.For(pawn), ref nativeCarry, false);
        Assert.Equal(move, nativeMove); Assert.Equal(carry, nativeCarry);

        // A failed room payload participates in the manager's existing space/save/notice/retry contract.
        var roomState = new CelestialRoomEnvironmentState(planet.Value);
        roomState.Advance(0, new[] { new CelestialRoomTopology(new[] { 0 }, false, 0, false, false, false) }, new(500000, 0, 1, 0));
        var environment = new MapComponent_CelestialEnvironment(map);
        map.components = new() { environment };
        var payloadField = AccessTools.Field(typeof(MapComponent_CelestialEnvironment), "savedPayload");
        AccessTools.Field(typeof(MapComponent_CelestialEnvironment), "savedOwner").SetValue(environment, planet.Value);
        payloadField.SetValue(environment, "{");
        var manager = native.Game.GetComponent<CelestialBodyManager>();
        Assert.False(environment.EnsureRestored()); Assert.True(manager.CelestialRestorePending);
        var destination = new IntVec3(1, 0, 1); bool allowedDestination = true;
        CelestialPlanetEnvironmentHooks.SafeTemperatureDestination(pawn, ref destination, ref allowedDestination);
        Assert.False(allowedDestination); Assert.Equal(IntVec3.Invalid, destination);
        Assert.True((bool)AccessTools.Field(typeof(CelestialBodyManager), "restoreNoticePending").GetValue(manager));
        var space = new SpaceGameComp(native.Game);
        var stampBeforeFailure = runtime.CurrentStamp;
        space.AdvancePhysics(1000);
        Assert.Equal(stampBeforeFailure, runtime.CurrentStamp);
        Assert.Contains(space.CelestialPhysicsErrors, error => error.Code == "RoomEnvironmentRestoreBlocked");
        var guard = AccessTools.Method(typeof(CelestialRuntime).Assembly.GetType("PioneerFramework.Celestial.CelestialSaveGuard"), "BeforeSave");
        Assert.False((bool)guard.Invoke(null, null));
        Assert.False(manager.RetryCelestialRestore().Success); Assert.Equal("{", payloadField.GetValue(environment));
        // Repair the retained dependency/payload; automatic queries stay blocked until explicit retry succeeds.
        string validPayload = System.Text.Encoding.UTF8.GetString(roomState.Serialize());
        payloadField.SetValue(environment, validPayload);
        Assert.False(environment.EnsureRestored());
        Assert.True(manager.RetryCelestialRestore().Success);
        Assert.False(manager.CelestialRestorePending); Assert.Empty(manager.CelestialRestoreErrors);
        Assert.False((bool)AccessTools.Field(typeof(CelestialBodyManager), "restoreNoticePending").GetValue(manager));
        Assert.True((bool)guard.Invoke(null, null)); Assert.Equal(validPayload, payloadField.GetValue(environment));
        var restoredRooms = Assert.IsType<CelestialRoomEnvironmentState>(AccessTools.Field(typeof(MapComponent_CelestialEnvironment), "state").GetValue(environment));
        Assert.Equal(roomState.Serialize(), restoredRooms.Serialize());
        var previousOdyssey = AccessTools.Field(typeof(ModsConfig), "odysseyActive").GetValue(null);
        var previousVacuum = StatDefOf.VacuumResistance; var previousToxic = StatDefOf.ToxicResistance; var previousToxicEnvironment = StatDefOf.ToxicEnvironmentResistance;
        try
        {
            AccessTools.Field(typeof(ModsConfig), "odysseyActive").SetValue(null, true);
            StatDefOf.VacuumResistance = new StatDef { defName = "PF_VacuumProtectionProbe", defaultBaseValue = 1, immutable = true };
            StatDefOf.ToxicResistance = new StatDef { defName = "PF_ToxicProbe", defaultBaseValue = 0, immutable = true };
            StatDefOf.ToxicEnvironmentResistance = new StatDef { defName = "PF_ToxicEnvironmentProbe", defaultBaseValue = 0, immutable = true };
            StatDefOf.VacuumResistance.Worker.SetCacheability(true);
            StatDefOf.ToxicResistance.Worker.SetCacheability(true); StatDefOf.ToxicEnvironmentResistance.Worker.SetCacheability(true);
            map.Biome.inVacuum = true; map.edificeGrid = new EdificeGrid(map); map.regionGrid = new RegionGrid(map);
            map.regionDirtyer = new RegionDirtyer(map);
            map.regionAndRoomUpdater = new RegionAndRoomUpdater(map); map.regionAndRoomUpdater.Enabled = false;
            AccessTools.Field(typeof(Thing), "positionInt").SetValue(pawn, new IntVec3(1, 0, 1));
            Assert.False(pawn.HarmedByVacuum);
            Assert.False(environment.IsDangerous(pawn, pawn.Position));
        }
        finally
        {
            map.Biome.inVacuum = false; AccessTools.Field(typeof(ModsConfig), "odysseyActive").SetValue(null, previousOdyssey);
            StatDefOf.VacuumResistance = previousVacuum; StatDefOf.ToxicResistance = previousToxic; StatDefOf.ToxicEnvironmentResistance = previousToxicEnvironment;
        }
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, runtime.Catalog.Revision, 0, 100)).Success);
        var later = capability.SampleSurface(new(100, 0, 0)); Assert.Equal(intensity, later.SunGlow, 10); Assert.Equal(100, later.PhysicalSeconds);
        var saved = new CelestialSaveAdapter(runtime).CaptureCheckpoint().Value;
        var replacement = new ExistingCelestialEphemerisBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(replacement.SessionId, replacement, CelestialBuiltinCompRegistry.Create(), _ => throw new InvalidOperationException("No resampling"));
        var identities = restore.RestoreIdentities(saved); Assert.True(identities.Success, string.Join(";", identities.Errors));
        var binding = restore.BindNativeObjects(); Assert.True(binding.Success, string.Join(";", binding.Errors));
        var completion = restore.CompleteRestore(); Assert.True(completion.Success, string.Join(";", completion.Errors));
        Assert.Equal(later, Assert.Single(restore.Runtime.GetCapabilities<ICelestialSurfaceEnvironmentCapability>(planet)).SampleSurface(new(100, 0, 0)));
    }
    private sealed class Resources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
}
