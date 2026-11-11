using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using HarmonyLib;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialPlanetMapDiversityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeatherOnlyConfigurationPreservesOtherActualNativeMapInputs(bool xml)
    {
        using var f = new NativeFixture();
        var weather = new WeatherDef { defName = "PD_WeatherOnly" }; f.Native.AddDefs(weather);
        var diversity = xml ? new CelestialPlanetMapDiversityXml { weather = new() { new() { def = weather } } }.Resolve() :
            new CelestialPlanetMapDiversity(weather: new[] { new CelestialWeightedReference(weather.defName) });
        var native = f.Layer("neutral-native", 71, null);
        var configured = f.Layer("neutral-weather", 71, diversity);
        TerrainDef Soil(string id, float fertility)
        { var d = (TerrainDef)RuntimeHelpers.GetUninitializedObject(typeof(TerrainDef)); d.defName = id; d.fertility = fertility; d.tags = new(); return d; }
        var poor = Soil("PD_NeutralPoor", .1f); var rich = Soil("PD_NeutralRich", 1);
        f.Biome.terrainsByFertility = new() { new() { terrain = poor, min = 0, max = .5f }, new() { terrain = rich, min = .5f, max = 100 } };
        f.Biome.terrainPatchMakers = new();
        var step = new GenStep_ScatterLumpsMineable { count = 100 };
        var count = AccessTools.Method(typeof(GenStep_ScatterLumpsMineable), "CalculateFinalCount");
        for (int i = 0; i < 3; i++)
        {
            var baseline = f.Map(native, i); var map = f.Map(configured, i);
            Assert.Same(weather, Assert.Single(new WeatherDecider(map).WeatherCommonalities).weather);
            Assert.Equal(native[i].PlantDensityFactor, configured[i].PlantDensityFactor);
            Assert.Equal(native[i].AnimalDensity, configured[i].AnimalDensity);
            Assert.Equal((int)count.Invoke(step, new object[] { baseline }), (int)count.Invoke(step, new object[] { map }));
            foreach (float fertility in new[] { .49f, .51f })
                Assert.Same(MapGenUtility.TerrainFrom(new(1, 0, 1), baseline, 0, fertility, false), MapGenUtility.TerrainFrom(new(1, 0, 1), map, 0, fertility, false));
        }
    }
    [Theory]
    [InlineData(true, true, 5.5f)]
    [InlineData(false, true, 2f)]
    [InlineData(true, false, 2f)]
    public void ActualNativeAnimalSelectionScoreRetainsAllowedCoastalWeightAndRejectsExcluded(bool odyssey, bool coastal, float expected)
    {
        using var f = new NativeFixture();
        PawnKindDef Animal(string id)
        {
            var race = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); race.defName = id + "Race";
            race.race = new RaceProperties { intelligence = Intelligence.Animal };
            return new PawnKindDef { defName = id, race = race, wildGroupSize = new IntRange(2, 2) };
        }
        var allowed = Animal("PD_CoastAllowed"); var excluded = Animal("PD_CoastExcluded"); var inland = Animal("PD_InlandAllowed");
        f.Native.AddDefs(allowed, excluded, inland);
        var nativeAnimals = (List<BiomeAnimalRecord>)AccessTools.Field(typeof(BiomeDef), "wildAnimals").GetValue(f.Biome);
        var nativeCoastal = (List<BiomeAnimalRecord>)AccessTools.Field(typeof(BiomeDef), "coastalWildAnimals").GetValue(f.Biome);
        nativeAnimals.Add(new() { animal = allowed, commonality = 1 });
        nativeCoastal.Add(new() { animal = allowed, commonality = 7 });
        nativeCoastal.Add(new() { animal = excluded, commonality = 9 });
        var layer = f.Layer("coastal", 22, new(animals: new[] { new CelestialWeightedReference(allowed.defName, 4), new CelestialWeightedReference(inland.defName, 4) }, geologyVariation: 0));
        var ocean = new BiomeDef { defName = "PD_CoastOcean" }; var previousOcean = BiomeDefOf.Ocean;
        var odysseyField = AccessTools.Field(typeof(ModsConfig), "odysseyActive"); var previousOdyssey = odysseyField.GetValue(null);
        var probe = new Harmony("PFL.Tests.NativeCoastalSelectionScore");
        try
        {
            BiomeDefOf.Ocean = ocean; odysseyField.SetValue(null, odyssey);
            var neighbors = new List<PlanetTile>(); f.Native.Grid.GetTileNeighbors(new(0, layer), neighbors);
            if (coastal) layer[neighbors[0].tileId].PrimaryBiome = ocean;
            var map = f.Map(layer); map.mapTemperature = new MapTemperature(map);
            Assert.Equal(coastal, map.TileInfo.IsCoastal);
            probe.Patch(AccessTools.Method(typeof(MapTemperature), nameof(MapTemperature.SeasonAcceptableFor)), prefix: new(typeof(CelestialPlanetMapDiversityTests), nameof(SeasonInput)));
            probe.Patch(AccessTools.PropertyGetter(typeof(WildAnimalSpawner), "DesiredAnimalDensity"), postfix: new(typeof(CelestialPlanetMapDiversityTests), nameof(ObserveAnimalSelectionScores)));
            bool observed = false;
            animalSelectionProbe = spawner => {
                var score = AccessTools.Method(typeof(WildAnimalSpawner), "CommonalityOfAnimalNow");
                Assert.Equal(expected, (float)score.Invoke(spawner, new object[] { allowed, new IntVec3(1, 0, 1) }));
                Assert.Equal(0f, (float)score.Invoke(spawner, new object[] { excluded, new IntVec3(1, 0, 1) }));
                Assert.Equal(2f, (float)score.Invoke(spawner, new object[] { inland, new IntVec3(1, 0, 1) }));
                Assert.Equal(odyssey, f.Biome.ShouldSpawnAnimalOnCoast(allowed));
                observed = true;
            };
            Assert.Equal(2f, (float)AccessTools.PropertyGetter(typeof(WildAnimalSpawner), "DesiredAnimalDensity").Invoke(new WildAnimalSpawner(map), null));
            Assert.True(observed);
            Assert.Equal(1f, f.Biome.CommonalityOfAnimal(allowed));
            Assert.Equal(odyssey ? 7f : 0f, f.Biome.CommonalityOfCoastalAnimal(allowed));
            Assert.Equal(odyssey ? 9f : 0f, f.Biome.CommonalityOfCoastalAnimal(excluded));
            Assert.Equal(1f, Assert.Single(nativeAnimals).commonality);
            Assert.Collection(nativeCoastal, a => Assert.Equal(7f, a.commonality), a => Assert.Equal(9f, a.commonality));
        }
        finally { animalSelectionProbe = null; probe.UnpatchAll(probe.Id); odysseyField.SetValue(null, previousOdyssey); BiomeDefOf.Ocean = previousOcean; }
    }
    private static Action<WildAnimalSpawner> animalSelectionProbe;
    private static void ObserveAnimalSelectionScores(WildAnimalSpawner __instance) => animalSelectionProbe(__instance);
    [Fact]
    public void ExplicitDiversityHasANativeConsumerAdapter()
    {
        var adapter = typeof(CelestialMapGeneration).Assembly.GetType("PioneerFramework.Celestial.CelestialPlanetMapHooks");
        Assert.NotNull(adapter);
    }
    [Fact]
    public void ActualWorldRockAndTileEcologyConsumersSeparateOwnersAndTiles()
    {
        using var fixture = new NativeFixture();
        var rockA = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); rockA.defName = "PD_RockA";
        var rockB = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); rockB.defName = "PD_RockB";
        fixture.Native.AddDefs(rockA, rockB);
        var a = fixture.Layer("a", 12, new(rocks: new[] { new CelestialWeightedReference(rockA.defName) }, plantDensity: 0, animalDensity: 0, geologyVariation: .5));
        var b = fixture.Layer("b", 71, new(rocks: new[] { new CelestialWeightedReference(rockB.defName) }, plantDensity: 2, animalDensity: 3, geologyVariation: .5));
        Assert.Same(rockA, Assert.Single(fixture.Native.World.NaturalRockTypesIn(new(0, a))));
        Assert.Same(rockB, Assert.Single(fixture.Native.World.NaturalRockTypesIn(new(0, b))));
        Assert.Equal(0, a[0].PlantDensityFactor); Assert.Equal(0, a[0].AnimalDensity);
        Assert.True(b[0].PlantDensityFactor > 0); Assert.True(b[0].AnimalDensity > 0);
        Assert.NotEqual(b[0].PlantDensityFactor, b[1].PlantDensityFactor);
        Assert.Equal(1, fixture.Biome.plantDensity); Assert.Equal(2, fixture.Biome.animalDensity);
    }
    [Fact]
    public void NativeWeatherCandidateGetterAndCavePredicateUseFrozenTables()
    {
        using var f = new NativeFixture();
        var allowed = new WeatherDef { defName = "PD_Allowed" }; var other = new WeatherDef { defName = "PD_Other" };
        f.Native.AddDefs(allowed, other);
        f.Biome.baseWeatherCommonalities.Add(new() { weather = other, commonality = 7 });
        var layer = f.Layer("weather", 17, new(weather: new[] { new CelestialWeightedReference(allowed.defName, 3) }, allowCaves: false));
        var cave = new TileMutatorDef { defName = "PD_Caves", categories = new() { "Caves" } }; AccessTools.Field(typeof(TileMutatorDef), "workerClass").SetValue(cave, typeof(TileMutatorWorker_Caves));
        layer[0].mutatorsNullable = new() { cave };
        var map = f.Map(layer); var decider = new WeatherDecider(map);
        var entry = Assert.Single(decider.WeatherCommonalities);
        Assert.Same(allowed, entry.weather); Assert.Equal(3, entry.commonality);
        Assert.Same(other, Assert.Single(f.Biome.baseWeatherCommonalities).weather);
        Assert.False(f.Native.World.HasCaves(map.Tile));
    }
    [Fact]
    public void ActualMineralChooserAndNativeCountConsumeMapScopeAndRestoreIt()
    {
        using var f = new NativeFixture();
        var mineral = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); mineral.defName = "PD_Mineral";
        f.Native.AddDefs(mineral);
        var layer = f.Layer("minerals", 5, new(minerals: new[] { new CelestialWeightedReference(mineral.defName) }, mineralAbundance: 0));
        var map = f.Map(layer);
        var step = new GenStep_ScatterLumpsMineable { count = 10 };
        var count = AccessTools.Method(typeof(GenStep_ScatterLumpsMineable), "CalculateFinalCount");
        Assert.Equal(0, (int)count.Invoke(step, new object[] { map }));
        observedMineral = null;
        var probe = new Harmony("PFL.Tests.NativeMineralScope");
        probe.Patch(count, postfix: new(typeof(CelestialPlanetMapDiversityTests), nameof(ObserveMineral)));
        try { step.Generate(map, default); } finally { probe.UnpatchAll(probe.Id); }
        Assert.Same(mineral, observedMineral);
        var hooks = typeof(CelestialMapGeneration).Assembly.GetType("PioneerFramework.Celestial.CelestialPlanetMapHooks");
        Assert.Null(AccessTools.Field(hooks, "mineralMap").GetValue(null));
    }
    private static ThingDef observedMineral;
    private static void ObserveMineral(GenStep_ScatterLumpsMineable __instance)
    { observedMineral = (ThingDef)AccessTools.Method(typeof(GenStep_ScatterLumpsMineable), "ChooseThingDef").Invoke(__instance, null); }
    [Fact]
    public void NativePlantSpawnerCandidatePoolAndCommonalityReadInstanceWhitelist()
    {
        using var f = new NativeFixture();
        var plant = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); plant.defName = "PD_Plant"; plant.category = ThingCategory.Plant; plant.plant = new PlantProperties();
        var excluded = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); excluded.defName = "PD_ExcludedPlant"; excluded.category = ThingCategory.Plant; excluded.plant = new PlantProperties();
        f.Native.AddDefs(plant, excluded);
        var layer = f.Layer("plants", 20, new(plants: new[] { new CelestialWeightedReference(plant.defName, 4) }, plantDensity: 2, geologyVariation: 0));
        var spawner = new WildPlantSpawner(f.Map(layer));
        Assert.Same(plant, Assert.Single(spawner.AllWildPlants));
        var commonality = AccessTools.Method(typeof(WildPlantSpawner), "GetCommonalityOfPlant");
        Assert.Equal(4f, (float)commonality.Invoke(spawner, new object[] { plant }));
        Assert.Equal(0f, (float)commonality.Invoke(spawner, new object[] { excluded }));
        Assert.Equal(2f, spawner.CurrentPlantDensityFactor);
    }
    [Fact]
    public void NativePlantSeedingUsesLowestOrderFromTheExplicitCandidateSet()
    {
        using var f = new NativeFixture();
        ThingDef Plant(string id, float order)
        { var d = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); d.defName = id; d.category = ThingCategory.Plant; d.plant = new PlantProperties { wildOrder = order }; return d; }
        var excluded = Plant("PD_ExcludedLowOrder", 0); var allowed = Plant("PD_AllowedHighOrder", 2); f.Native.AddDefs(excluded, allowed);
        f.Biome.wildPlants.Add(new() { plant = excluded, commonality = 1 });
        var map = f.Map(f.Layer("plant-order", 4, new(plants: new[] { new CelestialWeightedReference(allowed.defName) })));
        var probe = new Harmony("PFL.Tests.ExplicitPlantOrderInputs");
        probe.Patch(AccessTools.Method(typeof(PlantUtility), nameof(PlantUtility.CanEverPlantAt), new[] { typeof(ThingDef), typeof(IntVec3), typeof(Map), typeof(Thing).MakeByRefType(), typeof(bool), typeof(bool), typeof(bool) }), prefix: new(typeof(CelestialPlanetMapDiversityTests), nameof(FertileCellInput)));
        probe.Patch(AccessTools.Method(typeof(WildPlantSpawner), "EnoughLowerOrderPlantsNearby"), prefix: new(typeof(CelestialPlanetMapDiversityTests), nameof(NoExistingLowerPlants)));
        try
        {
            var plants = new List<ThingDef>();
            AccessTools.Method(typeof(WildPlantSpawner), "CalculatePlantsWhichCanGrowAt").Invoke(new WildPlantSpawner(map), new object[] { new IntVec3(1, 0, 1), plants, false, 1f });
            Assert.Same(allowed, Assert.Single(plants));
            Assert.Equal(0, f.Biome.LowestWildAndCavePlantOrder);
        }
        finally { probe.UnpatchAll(probe.Id); }
    }
    private static bool NoExistingLowerPlants(ref bool __result) { __result = false; return false; }
    private static bool FertileCellInput(ref AcceptanceReport __result) { __result = true; return false; }
    [Fact]
    public void NativeWeatherScorePreservesRainfallAndConditionMultipliers()
    {
        using var f = new NativeFixture();
        var allowed = new WeatherDef { defName = "PD_ScoreWeather", favorability = (Favorability)2,
            repeatable = true, temperatureRange = new FloatRange(-1000, 1000), commonalityRainfallFactor = new SimpleCurve { new CurvePoint(0, 2), new CurvePoint(1000, 2) } };
        f.Native.AddDefs(allowed);
        var layer = f.Layer("score", 18, new(weather: new[] { new CelestialWeightedReference(allowed.defName, 3) }));
        var map = f.Map(layer); map.mapTemperature = new MapTemperature(map); map.fireWatcher = new FireWatcher(map);
        AccessTools.Field(typeof(FireWatcher), "fireDanger").SetValue(map.fireWatcher, 0f);
        map.weatherManager = new WeatherManager(map) { curWeather = allowed };
        var condition = new WeatherFactor { def = new GameConditionDef { defName = "PD_WeatherFactor" } };
        map.gameConditionManager.ActiveConditions.Add(condition);
        var probe = new Harmony("PFL.Tests.WeatherTemperatureInput");
        probe.Patch(AccessTools.PropertyGetter(typeof(MapTemperature), nameof(MapTemperature.OutdoorTemp)), prefix: new(typeof(CelestialPlanetMapDiversityTests), nameof(TemperatureInput)));
        try
        {
            var score = AccessTools.Method(typeof(WeatherDecider), "CurrentWeatherCommonality");
            Assert.Equal(12f, (float)score.Invoke(new WeatherDecider(map), new object[] { allowed }));
        }
        finally { probe.UnpatchAll(probe.Id); }
    }
    public sealed class WeatherFactor : GameCondition
    { public override float WeatherCommonalityFactor(WeatherDef weather, Map map) => 2; }
    private static bool TemperatureInput(ref float __result) { __result = 20; return false; }
    [Fact]
    public void NativeAnimalDensityEnumeratesScopedCandidatesAndRestoresSharedBiome()
    {
        using var f = new NativeFixture();
        var race = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); race.defName = "PD_AnimalRace"; race.race = new RaceProperties { intelligence = Intelligence.Animal };
        var animal = new PawnKindDef { defName = "PD_Animal", race = race };
        f.Native.AddDefs(animal);
        var layer = f.Layer("animals", 22, new(animals: new[] { new CelestialWeightedReference(animal.defName, 4) }, animalDensity: 3, geologyVariation: 0));
        var map = f.Map(layer); map.mapTemperature = new MapTemperature(map);
        var probe = new Harmony("PFL.Tests.AnimalSeasonInput");
        probe.Patch(AccessTools.Method(typeof(MapTemperature), nameof(MapTemperature.SeasonAcceptableFor)), prefix: new(typeof(CelestialPlanetMapDiversityTests), nameof(SeasonInput)));
        try { Assert.Equal(6f, (float)AccessTools.PropertyGetter(typeof(WildAnimalSpawner), "DesiredAnimalDensity").Invoke(new WildAnimalSpawner(map), null)); }
        finally { probe.UnpatchAll(probe.Id); }
        Assert.Equal(0f, f.Biome.CommonalityOfAnimal(animal));
    }
    private static bool SeasonInput(ref bool __result) { __result = true; return false; }
    [Fact]
    public void ActualNativeTerrainSelectionConsumesSoilsAndFertilityWithoutChangingBiomeThresholds()
    {
        using var f = new NativeFixture();
        TerrainDef Soil(string id, float fertility)
        { var terrain = (TerrainDef)RuntimeHelpers.GetUninitializedObject(typeof(TerrainDef)); terrain.defName = id; terrain.fertility = fertility; terrain.tags = new(); return terrain; }
        var poor = Soil("PD_PoorSoil", .1f); var rich = Soil("PD_RichSoil", 1f);
        f.Native.AddDefs(poor, rich);
        f.Biome.terrainsByFertility = new() { new TerrainThreshold { terrain = poor, min = 0, max = .6f }, new TerrainThreshold { terrain = rich, min = .6f, max = 100 } };
        f.Biome.terrainPatchMakers = new();
        var low = f.Map(f.Layer("poor", 1, new(soilFertility: .1, geologyVariation: 0)));
        var high = f.Map(f.Layer("rich", 2, new(soilFertility: 3, geologyVariation: 0)));
        Assert.Same(poor, MapGenUtility.TerrainFrom(new(1, 0, 1), low, 0, .5f, false));
        Assert.Same(rich, MapGenUtility.TerrainFrom(new(1, 0, 1), high, 0, .5f, false));
        Assert.Equal(.6f, f.Biome.terrainsByFertility[0].max);
        var explicitSoil = f.Map(f.Layer("explicit", 3, new(soils: new[] { new CelestialWeightedReference(rich.defName) })));
        Assert.Same(rich, MapGenUtility.TerrainFrom(new(1, 0, 1), explicitSoil, 0, .1f, false));
    }
    [Fact]
    public void NativeStorytellerScorerConsumesIncidentAndConditionCandidates()
    {
        using var f = new NativeFixture();
        var condition = new GameConditionDef { defName = "PD_Condition" };
        var allowed = new IncidentDef { defName = "PD_Incident", gameCondition = condition, workerClass = typeof(IncidentWorker_MakeGameCondition), baseChance = 2 };
        var excluded = new IncidentDef { defName = "PD_ExcludedIncident", workerClass = typeof(IncidentWorker), baseChance = 100 };
        var layer = f.Layer("incidents", 3, new(incidents: new[] { new CelestialWeightedReference(allowed.defName, 5) }, conditions: new[] { new CelestialWeightedReference(condition.defName, 3) }));
        var map = f.Map(layer);
        AccessTools.Field(typeof(IncidentDef), "ignoreRecentSelectionWeighting").SetValue(allowed, true); AccessTools.Field(typeof(IncidentDef), "ignoreRecentSelectionWeighting").SetValue(excluded, true);
        var score = AccessTools.Method(typeof(StorytellerComp), "IncidentChanceFinal"); var storyteller = new StorytellerProbe();
        Assert.Equal(30f, (float)score.Invoke(storyteller, new object[] { allowed, map }));
        Assert.Equal(0f, (float)score.Invoke(storyteller, new object[] { excluded, map }));
        Assert.Equal(2, allowed.baseChance); Assert.Equal(100, excluded.baseChance);
        var otherCondition = new GameConditionDef { defName = "PD_ExcludedCondition" };
        var otherIncident = new IncidentDef { defName = allowed.defName, gameCondition = otherCondition, workerClass = typeof(IncidentWorker_MakeGameCondition), baseChance = 2 };
        AccessTools.Field(typeof(IncidentDef), "ignoreRecentSelectionWeighting").SetValue(otherIncident, true);
        Assert.Equal(0f, (float)score.Invoke(storyteller, new object[] { otherIncident, map }));
    }
    private sealed class StorytellerProbe : StorytellerComp { }
    [Fact]
    public void NativeRiverCoastalInputAndLakeFloodFillStayOnRequestedLayer()
    {
        using var f = new NativeFixture();
        var ocean = new BiomeDef { defName = "Ocean" }; var lake = new BiomeDef { defName = "Lake" };
        f.Native.AddDefs(ocean, lake);
        var previousOcean = BiomeDefOf.Ocean; var previousLake = BiomeDefOf.Lake; BiomeDefOf.Ocean = ocean; BiomeDefOf.Lake = lake;
        var probe = new Harmony("PFL.Tests.RiverTemperatureInput");
        probe.Patch(AccessTools.Method(typeof(GenTemperature), nameof(GenTemperature.GetTemperatureFromSeasonAtTile)), prefix: new(typeof(CelestialPlanetMapDiversityTests), nameof(TemperatureInput)));
        try
        {
            CelestialPlanetTerrainConfiguration Terrain() => new(oceanCoverage: .1, biomeRules: new[] { new CelestialPlanetBiomeRule("fallback", f.Biome.defName, fallback: true) });
            var a = f.Layer("water-a", 10, new(), Terrain()); var b = f.Layer("water-b", 20, new(), Terrain());
            a[0].PrimaryBiome = ocean; b[1].PrimaryBiome = ocean;
            CelestialPlanetHydrologyHooks.Install();
            var getCoasts = AccessTools.Method(typeof(WorldGenStep_Rivers), "GetCoastalWaterTiles");
            var coasts = (List<PlanetTile>)getCoasts.Invoke(new WorldGenStep_Rivers(), new object[] { b });
            Assert.Equal(new PlanetTile(1, b), Assert.Single(coasts));
            f.Native.World.pathGrid = new WorldPathGrid(); f.Native.World.reachability = new WorldReachability();
            var river = new RiverDef { defName = "PD_River", spawnFlowThreshold = 1, spawnChance = 1, degradeThreshold = 0 };
            f.Native.AddDefs(river);
            foreach (var tile in b.Tiles) { tile.rainfall = 1000; tile.elevation = tile.PrimaryBiome == ocean ? -100 : 100; tile.hilliness = Hilliness.Flat; }
            new WorldGenStep_Rivers().GenerateFresh("rivers", b);
            Assert.Contains(b.Tiles.Cast<SurfaceTile>(), t => t.potentialRivers?.Count > 0);
            Assert.All(a.Tiles.Cast<SurfaceTile>(), t => Assert.True(t.potentialRivers == null || t.potentialRivers.Count == 0));
            new WorldGenStep_Lakes().GenerateFresh("lakes", b);
            Assert.Same(lake, b[1].PrimaryBiome); Assert.Same(ocean, a[0].PrimaryBiome);
        }
        finally { probe.UnpatchAll(probe.Id); BiomeDefOf.Ocean = previousOcean; BiomeDefOf.Lake = previousLake; }
    }
    [Fact]
    public void OuterV3RestoreRetainsFrozenTerrainMapAndNativeObjectsAfterXmlChanges()
    {
        var surface = new CelestialBiomeProfileDef { defName = "PD_SavedSurface", mode = CelestialBiomeGenerationMode.Parameterized,
            terrain = new() { parameters = new() { new() { key = "continentScale", value = new() { min = .5, max = 3 } } },
                biomeRules = new() { new() { key = "fallback", biome = new BiomeDef { defName = "PF_ProbePrimary" }, fallback = true } } } };
        var maps = new CelestialMapProfileDef { defName = "PD_SavedMaps", diversity = new() { mineralAbundance = 2 } };
        using var f = new CelestialMapIdentityTests.Fixture(template => CelestialBodyTemplate.Create(template.DefinitionId, template.Label, template.Category, template.Tags,
            template.Components.Select(c => c.Descriptor.HandlerId == BiomeGenerationResolvedProperties.Handler ? new CompProperties_BiomeGeneration { key = c.Descriptor.Key, profile = surface }.ToDefinition() :
                c.Descriptor.HandlerId == MapGenerationResolvedProperties.Handler ? new CompProperties_MapGeneration { key = c.Descriptor.Key, profile = maps }.ToDefinition() : c)).Value);
        var first = f.Request("saved-map", CelestialMapAction.Generate); Assert.Equal(CelestialOperationStatus.Applied, first.Operation.Status);
        var map = f.Backend.LastMap; var tiles = f.Root.Tiles.Select(t => (t.PrimaryBiome, t.elevation, t.hilliness)).ToArray();
        var saved = new CelestialSaveAdapter(f.Scope.Runtime).CaptureCheckpoint(); Assert.True(saved.Success, string.Join(",", saved.Errors));
        var encoded = CelestialSaveCodec.Encode(saved.Value).Value; var decoded = CelestialSaveCodec.Decode(encoded).Value;
        Assert.Equal(4, decoded.FormatVersion);
        var oldMapBytes = decoded.Bodies[0].Components.Single(c => c.HandlerId == MapGenerationResolvedProperties.Handler).ConfigurationPayload.ToArray();
        maps.diversity.mineralAbundance = 9; surface.terrain.elevationOffsetMeters = 50000; surface.terrain.parameters.Clear();
        var bridge = new PioneerFramework.TestSupport.Celestial.DeterministicTestPhysicsBridge(Guid.NewGuid());
        var restore = new CelestialRestoreCoordinator(bridge.SessionId, bridge, CelestialBuiltinCompRegistry.Create(), _ => throw new Exception("No XML resolution or sampling"), mapBackend: f.Backend);
        Assert.True(restore.RestoreIdentities(decoded).Success); Assert.True(restore.BindNativeObjects().Success); Assert.True(restore.CompleteRestore().Success);
        var restoredProfile = Assert.Single(restore.Runtime.GetCapabilities<ICelestialMapGenerationCapability>(f.Root.Header.OwnerId)).GetProfile();
        Assert.Equal(2, restoredProfile.Diversity.MineralAbundance);
        Assert.Equal(oldMapBytes, new MapGenerationResolvedProperties("maps", restoredProfile).Serialize().ToArray());
        var again = restore.Runtime.RequestMap(new(new("same-map", restore.Runtime.WorldSessionId, restore.Runtime.Catalog.Revision), f.Address, CelestialMapAction.Generate, 250, 250));
        Assert.Equal(CelestialOperationStatus.Applied, again.Operation.Status); Assert.Equal(first.Handle.MapId, again.Handle.MapId); Assert.Equal(1, f.Backend.Generated); Assert.Same(map, f.Backend.LastMap);
        Assert.Equal(tiles, f.Root.Tiles.Select(t => (t.PrimaryBiome, t.elevation, t.hilliness)).ToArray());
    }
    [Fact]
    public void BiomeAndMapProfilePayloadBytesAndDecoderVersionsStayPinned()
    {
        var profile = new CelestialGenerationProfile("legacy", CelestialBiomeGenerationMode.VanillaSurface, 0, 0, 0,
            new[] { "Terrain" }, Array.Empty<string>());
        var properties = new BiomeGenerationResolvedProperties("biomes", profile);
        const string expected = "{\"Id\":\"legacy\",\"Mode\":1,\"Temperature\":0,\"Rainfall\":0,\"Swampiness\":0,\"WorldGenSteps\":[\"Terrain\"],\"RootOnlySteps\":[]}";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), properties.Serialize().ToArray()); Assert.Equal(1, properties.Version);
        Assert.True(BiomeGenerationResolvedProperties.Deserialize("biomes", 1, properties.Serialize()).Success);
        Assert.False(BiomeGenerationResolvedProperties.Deserialize("biomes", 2, properties.Serialize()).Success);
        // v2 载荷按 terrain 判定；per-biome hilliness 随规则序列化并往返保持。
        var terrain = new CelestialPlanetTerrainConfiguration(biomeRules: new[] { new CelestialPlanetBiomeRule("fallback", "plain", fallback: true, hilliness: 2) });
        var parameterized = new BiomeGenerationResolvedProperties("biomes", new CelestialGenerationProfile("legacy", CelestialBiomeGenerationMode.Parameterized,
            0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), terrain));
        Assert.Equal(2, parameterized.Version);
        var roundTrip = BiomeGenerationResolvedProperties.Deserialize("biomes", 2, parameterized.Serialize());
        Assert.True(roundTrip.Success, string.Join(",", roundTrip.Errors.Select(error => error.Code)));
        Assert.Equal(2, roundTrip.Value.Profile.Terrain.BiomeRules[0].Hilliness);
        Assert.False(BiomeGenerationResolvedProperties.Deserialize("biomes", 1, parameterized.Serialize()).Success);
        var map = new MapGenerationResolvedProperties("map", new("legacy-map", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<CelestialMapBiomeProfile>()));
        const string expectedMap = "{\"Id\":\"legacy-map\",\"MapGeneratorId\":null,\"SkipStepNames\":[],\"SkipStepPrefixes\":[],\"SkipStepTypes\":[],\"ReplaceTerrainIds\":[],\"ReplacementTerrainId\":null,\"BiomeRules\":[]}";
        Assert.Equal(Encoding.UTF8.GetBytes(expectedMap), map.Serialize().ToArray()); Assert.Equal(1, map.Version);
        Assert.True(MapGenerationResolvedProperties.Deserialize("map", 1, map.Serialize()).Success);
        Assert.False(MapGenerationResolvedProperties.Deserialize("map", 2, map.Serialize()).Success);
    }
    [Fact]
    public void NativeXmlLoadsTypedCandidatesRangesAndOverridesWithoutDlcReferences()
    {
        using var f = new NativeFixture();
        var weather = new WeatherDef { defName = "PD_XmlWeather" }; f.Native.AddDefs(weather);
        var condition = new GameConditionDef { defName = "PD_XmlCondition" }; f.Native.AddDefs(condition);
        var incident = new IncidentDef { defName = "PD_XmlIncident" }; f.Native.AddDefs(incident);
        var animalRace = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); animalRace.race = new RaceProperties { intelligence = Intelligence.Animal };
        AccessTools.Field(typeof(RaceProperties), "fleshType").SetValue(animalRace.race, new FleshTypeDef { defName = "PD_OrganicFlesh", isOrganic = true });
        var animal = new PawnKindDef { defName = "PD_XmlAnimal", race = animalRace }; f.Native.AddDefs(animal);
        ThingDef Thing(string id) { var d = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); d.defName = id; return d; }
        var rock = Thing("PD_XmlRock"); rock.category = ThingCategory.Building; rock.building = new BuildingProperties { isNaturalRock = true };
        var mineral = Thing("PD_XmlMineral"); mineral.category = ThingCategory.Building; mineral.building = new BuildingProperties { mineableThing = rock };
        var plant = Thing("PD_XmlPlant"); plant.category = ThingCategory.Plant; plant.plant = new PlantProperties();
        f.Native.AddDefs(rock, mineral, plant);
        var soil = (TerrainDef)RuntimeHelpers.GetUninitializedObject(typeof(TerrainDef)); soil.defName = "PD_XmlSoil"; soil.tags = new(); f.Native.AddDefs(soil);
        var surfaceDocument = new XmlDocument();
        surfaceDocument.LoadXml("<PioneerFramework.Celestial.CelestialBiomeProfileDef><defName>PD_XmlSurface</defName><mode>Parameterized</mode><terrain>" +
            "<continentScale>2</continentScale><elevationAmplitudeMeters>4000</elevationAmplitudeMeters><elevationOffsetMeters>100</elevationOffsetMeters><roughness>0.4</roughness><mountainWeight>0.7</mountainWeight>" +
            "<seaLevelMeters><min>-200</min><max>200</max></seaLevelMeters><allowRivers>false</allowRivers><allowLakes>false</allowLakes>" +
            "<parameters><li><key>continentScale</key><value><min>1</min><max>3</max></value></li></parameters>" +
            "<biomeRules><li><key>fallback</key><biome>PD_Plain</biome><fallback>true</fallback></li>" +
            "<li><key>cold</key><biome>PD_Plain</biome><minimumTemperatureCelsius>-100</minimumTemperatureCelsius><maximumTemperatureCelsius>-10</maximumTemperatureCelsius><priority>4</priority><weight>2</weight><hilliness>SmallHills</hilliness></li></biomeRules>" +
            "</terrain></PioneerFramework.Celestial.CelestialBiomeProfileDef>");
        var mapDocument = new XmlDocument();
        mapDocument.LoadXml("<PioneerFramework.Celestial.CelestialMapProfileDef><defName>PD_XmlMap</defName><diversity>" +
            "<rocks><li><def>PD_XmlRock</def><weight>3</weight></li></rocks><minerals><li><def>PD_XmlMineral</def></li></minerals><soils><li><def>PD_XmlSoil</def></li></soils>" +
            "<plants><li><def>PD_XmlPlant</def></li></plants><animals><li><def>PD_XmlAnimal</def></li></animals><weather><li><def>PD_XmlWeather</def></li></weather>" +
            "<conditions><li><def>PD_XmlCondition</def></li></conditions><incidents><li><def>PD_XmlIncident</def></li></incidents>" +
            "<mineralAbundance>2</mineralAbundance><soilFertility>1.2</soilFertility><plantDensity>0.8</plantDensity><animalDensity>0.5</animalDensity><geologyVariation>0.3</geologyVariation><allowCaves>false</allowCaves>" +
            "<parameters><li><key>mineralAbundance</key><value><min>1</min><max>4</max></value></li></parameters>" +
            "</diversity></PioneerFramework.Celestial.CelestialMapProfileDef>");
        var surface = DirectXmlToObject.ObjectFromXml<CelestialBiomeProfileDef>(surfaceDocument.DocumentElement, false);
        var maps = DirectXmlToObject.ObjectFromXml<CelestialMapProfileDef>(mapDocument.DocumentElement, false);
        DirectXmlCrossRefLoader.ResolveAllWantedCrossReferences(FailMode.LogErrors);
        Assert.Same(rock, maps.diversity.rocks[0].def); Assert.True(rock.IsNonResourceNaturalRock);
        Assert.Same(mineral, maps.diversity.minerals[0].def); Assert.NotNull(mineral.building.mineableThing);
        Assert.Same(plant, maps.diversity.plants[0].def); Assert.Same(soil, maps.diversity.soils[0].def);
        var resolvedSurface = surface.ToProfile(new CelestialGenerationContext(1, "body", "biomes", new[] { new CelestialParameterOverride("biomes", "terrain.continentScale", Value: 1.75) }));
        Assert.True(resolvedSurface.Success, string.Join(",", resolvedSurface.Errors)); Assert.Equal(1.75, resolvedSurface.Value.Terrain.ContinentScale);
        Assert.Equal((int)Hilliness.SmallHills, resolvedSurface.Value.Terrain.BiomeRules.Single(rule => rule.Key == "cold").Hilliness);
        Assert.Null(resolvedSurface.Value.Terrain.BiomeRules.Single(rule => rule.Key == "fallback").Hilliness);
        var resolvedMaps = maps.ToProfile(new CelestialGenerationContext(1, "body", "maps", new[] { new CelestialParameterOverride("maps", "diversity.mineralAbundance", Value: 3) }));
        Assert.True(resolvedMaps.Success, string.Join(",", resolvedMaps.Errors)); Assert.Equal(3, resolvedMaps.Value.Diversity.MineralAbundance);
        Assert.Equal(rock.defName, Assert.Single(resolvedMaps.Value.Diversity.Rocks).Id); Assert.False(resolvedMaps.Value.Diversity.AllowCaves);
        Assert.Equal(weather.defName, Assert.Single(resolvedMaps.Value.Diversity.Weather).Id);
    }
    [Fact]
    public void InvalidNativePlantAnimalAndMineralKindsRejectBeforeGeneration()
    {
        var building = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef)); building.defName = "PD_BadPlant";
        building.category = ThingCategory.Building; building.plant = new PlantProperties();
        var profile = new CelestialMapProfileDef { defName = "PD_InvalidKinds", diversity = new() { plants = new() { new() { def = building } } } };
        Assert.False(profile.ToProfile().Success);
        profile.diversity.plants.Clear();
        profile.diversity.animals.Add(new() { def = new PawnKindDef { defName = "PD_MissingRace" } });
        Assert.False(profile.ToProfile().Success);
        profile.diversity.animals.Clear();
        building.building = new BuildingProperties { mineableThing = building }; building.category = ThingCategory.Plant;
        profile.diversity.minerals.Add(new() { def = building });
        Assert.False(profile.ToProfile().Success);
    }
    [Fact]
    public void NativeGeologicalMutatorAndCaveCarvingEntryConsumePermissionWithoutDlc()
    {
        using var f = new NativeFixture();
        var caves = new TileMutatorDef { defName = "PD_GeologicalCaves", categories = new() { "Caves" } };
        AccessTools.Field(typeof(TileMutatorDef), "workerClass").SetValue(caves, typeof(TileMutatorWorker_Caves)); f.Native.AddDefs(caves);
        var terrain = new CelestialPlanetTerrainConfiguration(elevationAmplitudeMeters: 0, elevationOffsetMeters: 10000,
            biomeRules: new[] { new CelestialPlanetBiomeRule("fallback", f.Biome.defName, fallback: true) }, landmarks: new[] { new CelestialWeightedReference(caves.defName) });
        var enabled = f.Layer("cave-enabled", 10, new(allowCaves: true), terrain);
        var disabled = f.Layer("cave-disabled", 20, new(allowCaves: false), terrain);
        CelestialGenerationProfile Profile(CelestialSurfaceLayer layer)
        {
            var record = CelestialGenerationConfiguration.Decode(layer.Header.GenerationConfiguration).Value.Single(c => c.HandlerId == BiomeGenerationResolvedProperties.Handler);
            return BiomeGenerationResolvedProperties.Deserialize(record.Key, record.ConfigurationVersion, record.ConfigurationPayload).Value.Profile;
        }
        Assert.True(CelestialWorldGeneration.Generate(enabled, Profile(enabled), "caves").Success);
        Assert.True(CelestialWorldGeneration.Generate(disabled, Profile(disabled), "caves").Success);
        Assert.All(enabled.Tiles, t => { Assert.Same(caves, Assert.Single(t.Mutators)); Assert.True(f.Native.World.HasCaves(t.tile)); });
        Assert.All(disabled.Tiles, t => Assert.Empty(t.Mutators));
        // With permission disabled the real carving entry must return before requesting native map generation grids/noise.
        caves.Worker.GeneratePostElevationFertility(f.Map(disabled));
        Assert.False(f.Native.World.HasCaves(new(0, disabled)));
    }
    internal sealed class NativeFixture : IDisposable
    {
        public CelestialNativeWorldTestScope Native { get; } = new();
        public BiomeDef Biome { get; } = new() { defName = "PD_Plain", plantDensity = 1, animalDensity = 2, baseWeatherCommonalities = new() };
        public PlanetLayerDef Definition { get; } = new() { defName = "PD_SharedLayer", layerType = typeof(CelestialSurfaceLayer), tileType = typeof(SurfaceTile), worldDrawLayers = new() };
        public NativeFixture()
        {
            Native.AddDefs(Biome); Native.AddDefs(Definition); Native.Game.tickManager = new TickManager();
            AccessTools.Field(typeof(TickManager), "gameStartAbsTick").SetValue(Native.Game.tickManager, 60000);
            typeof(CelestialMapGeneration).Assembly.GetType("PioneerFramework.Celestial.CelestialPlanetMapHooks")?.GetMethod("Install")?.Invoke(null, null);
        }
        public CelestialSurfaceLayer Layer(string id, int seed, CelestialPlanetMapDiversity diversity, CelestialPlanetTerrainConfiguration terrain = null)
        {
            var profile = new CelestialMapProfile("PD_Map", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<CelestialMapBiomeProfile>(), diversity);
            var configuration = new MapGenerationResolvedProperties("map", profile);
            var record = new CelestialCompSaveRecord("map", configuration.HandlerId, configuration.Version, 1, configuration.Serialize().Span, ReadOnlySpan<byte>.Empty);
            var records = new List<CelestialCompSaveRecord> { record };
            if (terrain != null)
            {
                var generation = new BiomeGenerationResolvedProperties("biomes", new("PD_Surface", CelestialBiomeGenerationMode.Parameterized, 20, 1000, .5f,
                    Array.Empty<string>(), Array.Empty<string>(), terrain));
                records.Add(new("biomes", generation.HandlerId, generation.Version, 1, generation.Serialize().Span, ReadOnlySpan<byte>.Empty));
            }
            var layer = RuntimePlanetLayerService.RegisterCelestialInstance(Native.Grid, Definition, new(CelestialInstanceId.Parse("pd:" + id), -1, 100000, 30, 0, seed, CelestialGenerationConfiguration.Encode(records)));
            for (int i = 0; i < layer.TilesCount; i++) layer.Tiles.Add(new SurfaceTile(new(i, layer)) { PrimaryBiome = Biome });
            return layer;
        }
        public Map Map(CelestialSurfaceLayer layer, int tile = 0)
        {
            var backend = new CelestialMapIdentityTests.Backend(); var map = backend.Generate(backend.CreateParent(new(layer.Header.OwnerId, layer.LayerID, tile)), 6, 6, null);
            map.info.Size = new(6, 1, 6); map.gameConditionManager = new GameConditionManager(map);
            map.listerThings = new ListerThings(ListerThingsUse.Global);
            map.components = new() { new MixedBiomeMapComponent(map) }; return map;
        }
        public void Dispose() => Native.Dispose();
    }
}
