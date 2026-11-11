using System;
using System.Linq;
using System.Runtime.CompilerServices;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
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
        var restored = MapGenerationResolvedProperties.Deserialize("map", 1, resolved.Serialize()).Value.Specification;
        var rules = LocalMapBuiltinConfigurations.Decode<NativeRulesConfiguration>(
            Assert.Single(restored.Recipe.Steps, step => step.HandlerId == LocalMapBuiltinConfigurations.NativeRules).Configuration);
        bool ShouldSkip(string biome, GenStepDef step) =>
            rules.Skip.Any(selector => NativeMapStepSelection.Matches(selector, biome, step));
        var replacement = LocalMapBuiltinConfigurations.Decode<TerrainReplacementConfiguration>(
            Assert.Single(restored.Recipe.Steps, step => step.HandlerId == LocalMapBuiltinConfigurations.TerrainReplacement).Configuration);

        Assert.True(ShouldSkip("Elsewhere", new GenStepDef { defName = "Snow" }));
        Assert.True(ShouldSkip("Elsewhere", new GenStepDef { defName = "AncientCustom" }));
        Assert.True(ShouldSkip("Elsewhere", new GenStepDef { defName = "ModChunks", genStep = new DerivedChunks() }));
        var rocks = new GenStepDef { defName = "ModRocks", genStep = new GenStep_RocksFromGrid() };
        Assert.True(ShouldSkip("CustomFlat", rocks));
        Assert.False(ShouldSkip("Elsewhere", rocks));
        Assert.True(replacement.ShouldReplace("Elsewhere", "Gravel", false));
        Assert.True(replacement.ShouldReplace("CustomFlat", "AnyRock", true));
        Assert.False(replacement.ShouldReplace("Elsewhere", "AnyRock", true));
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
