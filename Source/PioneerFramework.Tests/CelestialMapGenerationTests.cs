using System;
using System.Runtime.CompilerServices;
using PioneerFramework.Celestial;
using RimWorld;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialMapGenerationTests
{
    [Fact]
    public void ProfileSnapshotsTypedRulesAndUsesAssignableStepTypes()
    {
        var flat = new BiomeDef { defName = "CustomFlat" };
        var profile = new CelestialMapProfileDef { defName = "CustomMap",
            skipStepNames = new() { "Snow" }, skipStepPrefixes = new() { "Ancient" },
            skipStepTypes = new() { typeof(GenStep_RockChunks) },
            replaceTerrains = new() { Terrain("Gravel") },
            replacementTerrain = Terrain("CustomDirt"),
            biomeRules = new() { new CelestialMapBiomeRule { biome = flat, replaceRockTerrains = true,
                skipStepTypes = new() { typeof(GenStep_RocksFromGrid) } } } };
        var resolved = new MapGenerationResolvedProperties("map", profile.ToProfile().Value);
        profile.skipStepNames.Clear();
        var restored = MapGenerationResolvedProperties.Deserialize("map", 1, resolved.Serialize()).Value.Profile;
        Assert.True(CelestialMapGeneration.ShouldSkip(restored, "Elsewhere", new GenStepDef { defName = "Snow" }));
        Assert.True(CelestialMapGeneration.ShouldSkip(restored, "Elsewhere", new GenStepDef { defName = "AncientCustom" }));
        Assert.True(CelestialMapGeneration.ShouldSkip(restored, "Elsewhere", new GenStepDef { defName = "ModChunks", genStep = new DerivedChunks() }));
        var rocks = new GenStepDef { defName = "ModRocks", genStep = new GenStep_RocksFromGrid() };
        Assert.True(CelestialMapGeneration.ShouldSkip(restored, "CustomFlat", rocks));
        Assert.False(CelestialMapGeneration.ShouldSkip(restored, "Elsewhere", rocks));
        Assert.True(restored.ShouldReplace("Elsewhere", "Gravel", false));
        Assert.True(restored.ShouldReplace("CustomFlat", "AnyRock", true));
        Assert.False(restored.ShouldReplace("Elsewhere", "AnyRock", true));
    }
    [Fact]
    public void InvalidTypesAndIncompleteTerrainReplacementAreRejected()
    {
        Assert.False(new CelestialMapProfileDef { defName = "Bad", skipStepTypes = new() { typeof(string) } }.ToProfile().Success);
        Assert.False(new CelestialMapProfileDef { defName = "Bad", replaceTerrains = new() { Terrain("Gravel") } }.ToProfile().Success);
    }
    private sealed class DerivedChunks : GenStep_RockChunks { }
    private static TerrainDef Terrain(string id)
    { var terrain = (TerrainDef)RuntimeHelpers.GetUninitializedObject(typeof(TerrainDef)); terrain.defName = id; return terrain; }
}
