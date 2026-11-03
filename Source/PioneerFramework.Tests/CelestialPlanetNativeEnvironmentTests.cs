using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPlanetNativeEnvironmentTests
{
    [Fact]
    public void ExplicitClimateBaseWindPreservesNativeWeatherConditionsAndWindSources()
    {
        using var f = new Fixture(new CelestialClimateXml { windSpeed = 1 });
        var wind = f.Map.windManager;
        f.Weather.windSpeedFactor = 2;
        wind.WindManagerTick(); Assert.Equal(2, wind.WindSpeed);
        f.Weather.windSpeedOffset = .4f;
        wind.WindManagerTick(); Assert.Equal(.4f + (2 - .08f) / (4 - .08f) * (4 - .4f), wind.WindSpeed, 5);
        f.Map.gameConditionManager.ActiveConditions.Add(new NativeEnvironmentCondition { def = new GameConditionDef(), minimumWind = .8f });
        wind.WindManagerTick(); Assert.Equal(.8f + (2 - .08f) / (4 - .08f) * (4 - .8f), wind.WindSpeed, 5);
        var source = new ThingWithComps();
        AccessTools.Field(typeof(ThingWithComps), "comps").SetValue(source, new List<ThingComp> { new CompWindSource { parent = source, wind = 3.25f } });
        var lists = (List<Thing>[])AccessTools.Field(typeof(ListerThings), "listsByGroup").GetValue(f.Map.listerThings);
        lists[(uint)ThingRequestGroup.WindSource] = new() { source };
        wind.WindManagerTick(); Assert.Equal(3.25f, wind.WindSpeed);
    }
    [Fact]
    public void ExposureOnlyDoesNotOverrideNativeWindAndAirlessClimateSuppressesWind()
    {
        using (var f = new Fixture(null))
        {
            f.Weather.windSpeedFactor = 1.7f;
            float nativeBase = (float)AccessTools.Method(typeof(WindManager), "BaseWindSpeedAt").Invoke(f.Map.windManager, new object[] { Find.TickManager.TicksAbs });
            f.Map.windManager.WindManagerTick();
            Assert.Equal(nativeBase * 1.7f, f.Map.windManager.WindSpeed);
        }
        using (var f = new Fixture(new CelestialClimateXml { windSpeed = 1 }, atmosphere: false))
        {
            f.Weather.windSpeedFactor = 2; f.Weather.windSpeedOffset = 1;
            f.Map.gameConditionManager.ActiveConditions.Add(new NativeEnvironmentCondition { def = new GameConditionDef(), minimumWind = 3 });
            f.Map.windManager.WindManagerTick(); Assert.Equal(0, f.Map.windManager.WindSpeed);
        }
    }
    [Fact]
    public void NativeSeasonalTemperatureExcludesDiurnalCycleAndMapConditionOffset()
    {
        using var f = new Fixture(new CelestialClimateXml { meanTemperatureCelsius = 20, diurnalAmplitudeCelsius = 10, lapseCelsiusPerMeter = 0,
            latitudeCurve = new() { new() { latitudeDegrees = 0 }, new() { latitudeDegrees = 90 } } });
        f.Map.gameConditionManager.ActiveConditions.Add(new NativeEnvironmentCondition { def = new GameConditionDef(), temperature = 9 });
        var native = new TileTemperaturesComp(f.Native.World);
        var coords = f.Native.Grid.LongLatOf(f.Map.Tile);
        int noon = (int)(30000 - coords.x / 360d * 60000);
        foreach (int tick in new[] { noon, noon + 30000 })
        {
            f.Native.Game.tickManager.DebugSetTicksGame(tick);
            var expected = f.Capability.SampleSurface(new(Find.TickManager.TicksAbs, coords.x, coords.y));
            Assert.Equal((float)expected.TemperatureCelsius.Value + 9, native.GetOutdoorTemp(f.Map.Tile));
            Assert.Equal(20, native.GetSeasonalTemp(f.Map.Tile));
        }
    }
    private sealed class NativeEnvironmentCondition : GameCondition
    {
        public float minimumWind, temperature;
        public override float MinWindSpeed() => minimumWind;
        public override float TemperatureOffset() => temperature;
    }
    internal sealed class Fixture : IDisposable
    {
        public CelestialNativeWorldTestScope Native { get; } = new();
        public CelestialRuntime Runtime { get; }
        public Map Map { get; }
        public WeatherDef Weather { get; } = new() { defName = "PF_EnvironmentWeather", windSpeedFactor = 1 };
        public CelestialInstanceId Planet { get; } = CelestialInstanceId.Parse("native:fixture");
        public ICelestialSurfaceEnvironmentCapability Capability => Assert.Single(Runtime.GetCapabilities<ICelestialSurfaceEnvironmentCapability>(Planet));
        public Fixture(CelestialClimateXml climate, bool atmosphere = true, CelestialExposureXml exposure = null)
        {
            Native.Game.tickManager = new TickManager();
            AccessTools.Field(typeof(TickManager), "gameStartAbsTick").SetValue(Native.Game.tickManager, 60000);
            var sun = CelestialInstanceId.Parse("native:sun");
            var bodyDef = new CelestialBodyDef { defName = "NativeEnvironmentBody", comps = new() {
                new CompProperties_SphericalBody { key = "shape" }, new CompProperties_ExistingEnvironment { key = "env", profile = new() {
                    defName = "native-profile", primaryLightReference = "sun", climate = climate, exposure = exposure ?? new() } } } };
            if (atmosphere) bodyDef.comps.Add(new CompProperties_Atmosphere { key = "air", rho0 = new() { value = 1 }, scaleHeight = new() { value = 1000 }, topAltitude = new() { value = 10000 } });
            var star = new CelestialBodyDef { defName = "NativeEnvironmentSun", comps = new() { new CompProperties_SphericalBody { key = "shape" }, new CompProperties_LightSource { key = "light" } } }.ToTemplate().Value;
            var body = bodyDef.ToTemplate().Value;
            CelestialSpawnItem Item(CelestialInstanceId id, CelestialBodyTemplate template) => new(id, id.Value, 0, id.Value, template, template.Resolve(new(42, id.Value)).Value,
                initializationReferences: new Dictionary<string, CelestialInstanceId> { ["sun"] = sun });
            var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid()); bridge.SetFrame(0, new Dictionary<CelestialInstanceId, CelestialMotionState>());
            Runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, new Resources(), 42, _ => throw new InvalidOperationException());
            Runtime.CreateBatch(new(new("native-fixture", Runtime.WorldSessionId, 0), new("native-fixture", 42, null, new[] { Item(sun, star), Item(Planet, body) })));
            Assert.True(Runtime.Advance(new(Runtime.WorldSessionId, 0, 0, 0)).Success);
            Assert.True(Native.Game.GetComponent<CelestialBodyManager>().AttachCelestialRuntime(Runtime).Success);
            var layerDef = new PlanetLayerDef { defName = "PF_NativeEnvironment", layerType = typeof(CelestialSurfaceLayer), tileType = typeof(SurfaceTile), worldDrawLayers = new() };
            Native.AddDefs(layerDef);
            var layer = RuntimePlanetLayerService.RegisterCelestialInstance(Native.Grid, layerDef, new(Planet, -1, 100000, 30, 0, 1, ReadOnlyMemory<byte>.Empty));
            for (int i = 0; i < layer.TilesCount; i++) layer.Tiles.Add(new SurfaceTile(new(i, layer)) { PrimaryBiome = new() { defName = "plain" } });
            var backend = new CelestialMapIdentityTests.Backend();
            Map = backend.Generate(backend.CreateParent(new(Planet, layer.LayerID, 0)), 6, 6, null);
            Map.info.Size = new IntVec3(6, 1, 6); Map.generatorDef = new MapGeneratorDef { defName = "PF_NativeEnvironmentMap" };
            Map.weatherManager = new WeatherManager(Map) { curWeather = Weather, lastWeather = Weather };
            Map.gameConditionManager = new GameConditionManager(Map); Map.listerThings = new ListerThings(ListerThingsUse.Global);
            Map.windManager = new WindManager(Map); CelestialPlanetEnvironmentHooks.Install();
        }
        public void Dispose() => Native.Dispose();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeDefPatchesApplyWithAndWithoutOdyssey(bool odyssey)
    {
        using var scope = new CelestialXmlTestScope();
        var target = new System.Xml.XmlDocument();
        target.LoadXml("<Defs><StatDef><defName>MoveSpeed</defName><parts /></StatDef><StatDef><defName>CarryingCapacity</defName><parts /></StatDef>" +
            (odyssey ? "<ThingDef><defName>Apparel_VacsuitHelmet</defName></ThingDef><ThingDef><defName>OxygenPump</defName><comps /></ThingDef>" : "") + "</Defs>");
        var patch = new System.Xml.XmlDocument();
        patch.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "Patches", "CelestialEnvironment.xml"));
        var operation = DirectXmlToObject.ObjectFromXml<PatchOperation>(patch.SelectSingleNode("Patch/Operation"), false);
        Assert.True(operation.Apply(target));
        Assert.Equal(2, target.SelectNodes("Defs/StatDef/parts/li").Count);
        if (odyssey)
        {
            Assert.Equal("1", target.SelectSingleNode("Defs/ThingDef[defName='Apparel_VacsuitHelmet']/modExtensions/li/oxygenSupply").InnerText);
            Assert.Null(target.SelectSingleNode("Defs/ThingDef[defName='Apparel_VacsuitHelmet']/modExtensions/li/highPressureResistance"));
            var supply = DirectXmlToObject.ObjectFromXml<CompProperties_CelestialLifeSupport>(target.SelectSingleNode("Defs/ThingDef[defName='OxygenPump']/comps/li"), false);
            Assert.True(supply.suppliesOxygen); Assert.False(supply.filtersToxins); Assert.False(supply.regulatesPressure);
        }
    }
    [Fact]
    public void RealNativeTemperatureEntrySeparatesSameDefinitionAndTileAndPreservesLegacy()
    {
        using var scope = new CelestialNativeWorldTestScope();
        var native = typeof(CelestialEnvironmentProfile).Assembly.GetType("PioneerFramework.Celestial.CelestialPlanetEnvironmentHooks");
        Assert.NotNull(native);
        native!.GetMethod("Install")!.Invoke(null, null);
        var one = CelestialInstanceId.Parse("planet:cold"); var two = CelestialInstanceId.Parse("planet:hot");
        var sun = CelestialInstanceId.Parse("star:light");
        CelestialBodyTemplate Template(double? temperature) => new CelestialBodyDef { defName = "SharedPlanet", comps = new() {
            new CompProperties_SphericalBody { key = "shape" },
            new CompProperties_Atmosphere { key = "air", rho0 = new() { value = 1 }, scaleHeight = new() { value = 1000 }, topAltitude = new() { value = 10000 } },
            new CompProperties_ExistingEnvironment { key = "env", profile = new() { defName = "shared", primaryLightReference = "sun",
                climate = temperature.HasValue ? new() { meanTemperatureCelsius = temperature.Value, lapseCelsiusPerMeter = 0,
                    windSpeed = temperature < 0 ? .25 : 1.25,
                    latitudeCurve = new() { new() { latitudeDegrees = 0 }, new() { latitudeDegrees = 90 } } } : null } } } }.ToTemplate().Value;
        var star = new CelestialBodyDef { defName = "Star", comps = new() { new CompProperties_SphericalBody { key = "shape" }, new CompProperties_LightSource { key = "light" } } }.ToTemplate().Value;
        CelestialSpawnItem Spawn(CelestialInstanceId id, CelestialBodyTemplate template) => new(id, id.Value, 0, id.Value, template,
            template.Resolve(new(1, id.Value)).Value, initializationReferences: new Dictionary<string, CelestialInstanceId> { ["sun"] = sun });
        var bridge = new DeterministicTestPhysicsBridge(Guid.NewGuid()); bridge.SetFrame(0, new Dictionary<CelestialInstanceId, CelestialMotionState>());
        var runtime = new CelestialRuntime(bridge, bridge.CommittedStamp, new Resources(), 1, _ => throw new InvalidOperationException());
        var legacy = CelestialInstanceId.Parse("planet:legacy");
        runtime.CreateBatch(new(new("create", runtime.WorldSessionId, 0), new("native", 1, null,
            new[] { Spawn(sun, star), Spawn(one, Template(-40)), Spawn(two, Template(80)), Spawn(legacy, Template(null)) })));
        Assert.True(runtime.Advance(new(runtime.WorldSessionId, 0, 0, 0)).Success);
        Assert.True(scope.Game.GetComponent<CelestialBodyManager>().AttachCelestialRuntime(runtime).Success);
        var definition = new PlanetLayerDef { defName = "PF_SharedClimate", layerType = typeof(CelestialSurfaceLayer), tileType = typeof(SurfaceTile), worldDrawLayers = new() };
        scope.AddDefs(definition);
        PlanetTile Tile(CelestialInstanceId id)
        {
            var layer = RuntimePlanetLayerService.RegisterCelestialInstance(scope.Grid, definition, new(id, -1, 100000, 30, 0, 1, ReadOnlyMemory<byte>.Empty));
            for (int i = 0; i < layer.TilesCount; i++) layer.Tiles.Add(new SurfaceTile(new PlanetTile(i, layer)) { PrimaryBiome = new() { defName = "plain" } });
            return new(0, layer);
        }
        var a = Tile(one); var b = Tile(two);
        var temperatures = new TileTemperaturesComp(scope.World);
        Assert.Equal(-40, temperatures.GetOutdoorTemp(a)); Assert.Equal(80, temperatures.GetOutdoorTemp(b));
        Assert.Equal(-40, temperatures.OutdoorTemperatureAt(a, 30000)); Assert.Equal(80, temperatures.GetSeasonalTemp(b));
        var backend = new CelestialMapIdentityTests.Backend();
        var mapA = backend.Generate(backend.CreateParent(new(one, a.Layer.LayerID, 0)), 6, 6, null);
        var mapB = backend.Generate(backend.CreateParent(new(two, b.Layer.LayerID, 0)), 6, 6, null);
        scope.Game.tickManager = new TickManager();
        AccessTools.Field(typeof(TickManager), "gameStartAbsTick").SetValue(scope.Game.tickManager, 60000);
        foreach (var map in new[] { mapA, mapB })
        {
            var weather = new WeatherDef { defName = "PF_IdentityWind", windSpeedFactor = 1 };
            map.weatherManager = new WeatherManager(map) { curWeather = weather, lastWeather = weather };
            map.gameConditionManager = new GameConditionManager(map); map.listerThings = new ListerThings(ListerThingsUse.Global);
            map.windManager = new WindManager(map); map.windManager.WindManagerTick();
        }
        Assert.Equal(.25f, mapA.windManager.WindSpeed); Assert.Equal(1.25f, mapB.windManager.WindSpeed);
        Assert.Empty(runtime.GetCapabilities<ICelestialSurfaceEnvironmentCapability>(legacy));
    }
    private sealed class Resources : ICelestialRemovalResources
    {
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
    }
}
