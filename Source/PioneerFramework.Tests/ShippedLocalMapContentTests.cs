using System;
using System.IO;
using System.Linq;
using System.Xml;
using PFL.CelestialCompSample;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using RimWorld;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

public class ShippedLocalMapContentTests
{
    [Theory]
    [InlineData("PF_EarthMaps", "TemperateForest", null)]
    [InlineData("PF_MoonMaps", "PF_MoonBiomeRegolith", "PF_MoonDirt")]
    [InlineData("PF_MoonMaps", "PF_MoonBiomeWater", "PF_MoonDirt")]
    [InlineData("PF_MoonMaps", "PF_MoonBiomeBasalt", "PF_MoonDirt")]
    [InlineData("PF_BarrenMaps", "PF_BarrenRegolith", "PF_MoonDirt")]
    [InlineData("PF_FrozenMaps", "PF_FrozenPlain", "Ice")]
    [InlineData("PF_GreenhouseMaps", "PF_GreenhousePlain", "Sand")]
    [InlineData("PF_TemperateMaps", "TemperateForest", null)]
    public void MigratedRecipesCompileAcrossNativePurposes(string profileId, string biome, string target)
    {
        using var fixture = new CelestialPlanetKindContentTests.Content();
        var result = fixture.Maps[profileId].ToProfile();
        Assert.True(result.Success, string.Join(", ", result.Errors.Select(e => e.Detail)));
        var profile = result.Value;
        var specification = Assert.IsType<LocalMapGenerationSpec>(profile.LocalSpecification);
        foreach (var purpose in Enum.GetValues<MapGenerationPurpose>())
        {
            bool player = purpose == MapGenerationPurpose.Colony || purpose == MapGenerationPurpose.Landing;
            var candidates = NativeLocalMapExecutionTests.CoreCandidateSnapshot(player);
            var suffix = new GenStepDef { defName = "VoidMonolithExtension", order = 900, genStep = new GenStep_Plants() };
            candidates.Add(new(suffix, default));
            var frozen = new FrozenLocalMapContext(new(profileId, 42, 0, 64, 64, biome), specification, profile);
            var plan = NativeLocalMapPlan.Build(frozen, purpose, candidates, LocalMapGenerationModules.Registry);
            if (profile.Diversity is { } diversity)
            {
                var inputs = Assert.Single(plan.InitialInputs);
                Assert.Equal(diversity.MineralAbundance, inputs.MineralAbundance);
                Assert.Equal(diversity.PlantDensity, inputs.PlantDensity);
                Assert.Equal(diversity.AnimalDensity, inputs.AnimalDensity);
                Assert.Equal(diversity.GeologyVariation, inputs.GeologyVariation);
                Assert.Equal(diversity.AllowCaves, inputs.AllowCaves);
            }
            else Assert.Empty(plan.InitialInputs);
            var ordered = plan.Plan.Steps.Select(node => node.Spec).ToList();
            var replacements = ordered.Where(node => node.HandlerId == LocalMapBuiltinConfigurations.TerrainReplacement).ToArray();
            Assert.Contains(plan.NativeSteps, step => step.Step.def == suffix);
            Assert.Equal(target == null, plan.NativeSteps.Any(step => step.Step.def.defName == "Snow"));
            Assert.Equal(target == null, plan.NativeSteps.Any(step => step.Step.def.defName == "RockChunks"));
            Assert.Equal(target == null || profileId == "PF_MoonMaps", plan.NativeSteps.Any(step => step.Step.def.defName == "Plants"));
            bool expectedRocks = biome != "PF_MoonBiomeRegolith" && biome != "PF_MoonBiomeWater";
            Assert.Equal(expectedRocks, plan.NativeSteps.Any(step => step.Step.def.genStep is GenStep_RocksFromGrid));
            if (target == null)
            {
                Assert.Empty(replacements);
                continue;
            }

            var replacement = Assert.Single(replacements);
            Assert.Equal(target, LocalMapBuiltinConfigurations.Decode<TerrainReplacementConfiguration>(replacement.Configuration).TargetTerrainId);
            Assert.True(ordered.FindIndex(node => node.Key == "native/MutatorPostTerrain/0") < ordered.IndexOf(replacement));
            Assert.True(ordered.IndexOf(replacement) < ordered.FindIndex(node => node.Key == "native/FindPlayerStartSpot/0"));
        }
    }

    [Fact]
    public void LegacyBasePlayerPatchAndStandaloneStepAreRetired()
    {
        var defs = CelestialDefaultContentTests.DataPath("");
        Assert.DoesNotContain(Directory.EnumerateFiles(defs, "*.xml", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains("PF_MoonSurfaceTerrain", StringComparison.Ordinal));
        var patches = CelestialDefaultContentTests.DataPath("../Patches");
        Assert.DoesNotContain(Directory.EnumerateFiles(patches, "*.xml", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains("PF_MoonSurfaceTerrain", StringComparison.Ordinal));
    }

    [Fact]
    public void IndependentSampleLandingRecipeExecutesAgainstTheNativeMap()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "CelestialCompSample", "Defs", "LocalMapRecipe.xml");
        Assert.True(File.Exists(file), "The independent sample should ship a purpose recipe Def.");
        using var fixture = new CelestialPlanetMapDiversityTests.NativeFixture();
        var xml = new XmlDocument();
        xml.Load(file);
        var definition = DirectXmlToObject.ObjectFromXml<LocalMapRecipeDef>(xml.SelectSingleNode("Defs/*[defName='PF_SampleLandingContent']"), false);
        var registry = LocalMapGenerationModules.CreateRegistry();
        var sampleAssembly = typeof(SampleCounterRegistration).Assembly;
        SampleLandingModule.Register(registry);

        var profileXml = new XmlDocument();
        profileXml.Load(CelestialDefaultContentTests.DataPath("CelestialFramework/CelestialProfiles.xml"));
        var patchXml = new XmlDocument();
        patchXml.Load(Path.Combine(AppContext.BaseDirectory, "CelestialCompSample", "Patches", "LocalMapRecipe.xml"));
        var patch = DirectXmlToObject.ObjectFromXml<PatchOperation>(patchXml.SelectSingleNode("Patch/Operation"), false);
        Assert.True(patch.Apply(profileXml));
        var entry = profileXml.SelectSingleNode("/Defs/PioneerFramework.Celestial.CelestialMapProfileDef[defName='PF_EarthMaps']/purposeRecipes/li");
        Assert.NotNull(entry);
        Assert.Equal(definition.defName, entry["recipe"].InnerText);
        var purpose = Enum.Parse<MapGenerationPurpose>(entry["purpose"].InnerText);
        Assert.Equal(MapGenerationPurpose.Landing, purpose);
        var recipe = definition.ToRecipe(registry);
        var map = fixture.Map(fixture.Layer("sample-landing", 42, new()));
        var source = new MapSourceContext("pd:sample-landing", 42, map.Tile.tileId, map.Size.x, map.Size.z, fixture.Biome.defName);
        var frozen = new FrozenLocalMapContext(source, new(new LocalMapRecipe("native", Array.Empty<MapGenerationStepSpec>()),
            new System.Collections.Generic.Dictionary<MapGenerationPurpose, LocalMapRecipe> { [purpose] = recipe }));
        var candidates = Array.Empty<GenStepWithParams>();
        var plan = NativeLocalMapPlan.Build(frozen, purpose, candidates, registry);
        Assert.Same(sampleAssembly, Assert.Single(plan.Modules).Value.GetType().Assembly);
        var services = new NativeMapGenerationServices(map, source, null, plan.NativeSteps);
        var run = new MapGenerationExecutor(plan.Plan, source, new NativeServices(services), preparedModules: plan.Modules);

        NativeLocalMapHooks.ClearWorkingData();
        try
        {
            Assert.Equal(MapGenerationState.Generated, run.Execute().State);
            Assert.Equal(MapGenerationState.Ready, run.PostInitialize().State);
            Assert.True(MapGenerator.TryGetVar<IntVec3>(SampleLandingModule.WorkingDataKey, out var landingCell));
            Assert.Equal(map.Center + IntVec3.East, landingCell);
            Assert.Empty(NativeLocalMapPlan.Build(frozen, MapGenerationPurpose.Encounter, candidates, registry).Plan.Steps);
        }
        finally { NativeLocalMapHooks.ClearWorkingData(); }
    }

    private sealed class NativeServices(INativeMapGenerationServices native) : IServiceProvider
    {
        public object GetService(Type serviceType) => serviceType == typeof(INativeMapGenerationServices) ? native : null;
    }
}
