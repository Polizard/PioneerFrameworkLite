using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// P4 真空声明跨组件校验（2026-11-03）：
/// Parameterized terrain 能生成的 biome 必须与 environment.vacuumBiomes 一致——
/// inVacuum=true 的生成群落必须被声明，inVacuum=false 的生成群落不得被声明；
/// 不一致时 CelestialBodyDef.ToTemplate() 拒绝，错误码 VacuumContractMismatch。
/// </summary>
public class CelestialVacuumContractTests
{
    [Fact]
    public void ParameterizedGenerationAndEnvironmentDeclarationMustAgree()
    {
        // 可达反例：terrain 生成真空群落但 environment 未声明 ⇒ ToTemplate 拒绝。
        var vacuum = new BiomeDef { defName = "PD_VacuumBiome", inVacuum = true };
        var result = Body(Parameterized(Rule("fallback", vacuum))).ToTemplate();
        Assert.False(result.Success);
        var mismatch = Assert.Single(result.Errors, error => error.Code == "VacuumContractMismatch");
        Assert.Equal("biomes", mismatch.CompKey);
        Assert.Equal("PD_VacuumBiome inVacuum=True", mismatch.Detail);
    }

    [Fact]
    public void DeclaredNonVacuumBiomeIsRejectedByTheContractRule()
    {
        // ToTemplate 路径上该方向会先被 environment profile 自身的真空引用校验拒绝（InvalidEnvironmentProfile），
        // 跨校验只在前置 ConfigErrors 全通过后执行；这里直接锁死跨校验规则本身，并同时钉住两层拒绝语义。
        var nonVacuum = new BiomeDef { defName = "PD_NonVacuumBiome", inVacuum = false };
        var environment = EnvironmentProfile(nonVacuum);
        var errors = CelestialBodyContentValidation.Validate(new CelestialCompProperties[] {
            new CompProperties_BiomeGeneration { key = "biomes", profile = Parameterized(Rule("fallback", nonVacuum)) },
            new CompProperties_ExistingEnvironment { key = "environment", profile = environment } }).ToArray();
        var mismatch = Assert.Single(errors, error => error.Code == "VacuumContractMismatch");
        Assert.Equal("environment", mismatch.CompKey);
        Assert.Equal("PD_NonVacuumBiome inVacuum=False", mismatch.Detail);
        Assert.Contains(environment.ToProfile().Errors, error => error.Code == "InvalidEnvironmentProfile");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConsistentDeclarationsProduceAUsableTemplate(bool declared)
    {
        var biome = new BiomeDef { defName = "PD_Biome", inVacuum = declared };
        var body = Body(Parameterized(Rule("fallback", biome)), declared ? new[] { biome } : Array.Empty<BiomeDef>());
        var template = body.ToTemplate();
        Assert.True(template.Success, string.Join(",", body.ConfigErrors()));
        Assert.True(template.Value.HasCapability("surface"));
    }

    [Fact]
    public void VanillaSurfaceSkipsTheCrossCheck()
    {
        // VanillaSurface 没有参数化生成集合（terrain 省略），声明再多也用原生生成器，不参与跨校验。
        var declared = new BiomeDef { defName = "PD_NativeVacuum", inVacuum = true };
        var body = Body(Vanilla(TerrainStep()), declared);
        Assert.True(body.ToTemplate().Success, string.Join(",", body.ConfigErrors()));
    }

    [Fact]
    public void IncompleteComponentsSkipTheCrossCheckWithoutThrowing()
    {
        var vacuum = new BiomeDef { defName = "PD_VacuumBiome", inVacuum = true };
        var biomeComp = new CompProperties_BiomeGeneration { key = "biomes", profile = Parameterized(Rule("fallback", vacuum)) };
        var environmentComp = new CompProperties_ExistingEnvironment { key = "environment", profile = EnvironmentProfile() };
        Assert.Empty(CelestialBodyContentValidation.Validate(null));
        Assert.Empty(CelestialBodyContentValidation.Validate(new CelestialCompProperties[] { biomeComp }));
        Assert.Empty(CelestialBodyContentValidation.Validate(new CelestialCompProperties[] { environmentComp }));
        Assert.Empty(CelestialBodyContentValidation.Validate(new CelestialCompProperties[] { biomeComp, biomeComp, environmentComp }));
        Assert.Empty(CelestialBodyContentValidation.Validate(new CelestialCompProperties[] { biomeComp, environmentComp, environmentComp }));
        Assert.Empty(CelestialBodyContentValidation.Validate(new CelestialCompProperties[] {
            new CompProperties_BiomeGeneration { key = "biomes" }, environmentComp }));
        Assert.Empty(CelestialBodyContentValidation.Validate(new CelestialCompProperties[] {
            biomeComp, new CompProperties_ExistingEnvironment { key = "environment" } }));
        // 空引用一律跳过：terrain.biomeRules / vacuumBiomes 为 null 时不抛。
        Assert.Empty(CelestialBodyContentValidation.Validate(new CelestialCompProperties[] {
            new CompProperties_BiomeGeneration { key = "biomes", profile = new CelestialBiomeProfileDef {
                defName = "PD_NullRules", mode = CelestialBiomeGenerationMode.Parameterized,
                terrain = new CelestialPlanetTerrainXml { biomeRules = null } } }, environmentComp }));
        Assert.Empty(CelestialBodyContentValidation.Validate(new CelestialCompProperties[] {
            biomeComp, new CompProperties_ExistingEnvironment { key = "environment", profile = new CelestialEnvironmentProfileDef {
                defName = "PD_NullVacuum", vacuumBiomes = null } } }));
    }

    [Fact]
    public void OceanBiomeAndDuplicateRulesParticipateInTheGeneratedSet()
    {
        var nonVacuum = new BiomeDef { defName = "PD_PlainBiome", inVacuum = false };
        var ocean = new BiomeDef { defName = "PD_OceanVacuum", inVacuum = true };
        var terrain = new CelestialPlanetTerrainXml { oceanBiome = ocean, biomeRules = new() {
            Rule("one", nonVacuum, fallback: false), Rule("two", nonVacuum, fallback: false), Rule("fallback", nonVacuum) } };
        var errors = CelestialBodyContentValidation.Validate(new CelestialCompProperties[] {
            new CompProperties_BiomeGeneration { key = "biomes", profile = new CelestialBiomeProfileDef {
                defName = "PD_TerrainBiomes", mode = CelestialBiomeGenerationMode.Parameterized, terrain = terrain } },
            new CompProperties_ExistingEnvironment { key = "environment", profile = EnvironmentProfile() } }).ToArray();
        var mismatch = Assert.Single(errors);
        Assert.Equal("PD_OceanVacuum inVacuum=True", mismatch.Detail);
    }

    [Fact]
    public void ShippedPlanetKindBodiesSatisfyVacuumContract()
    {
        using var content = new CelestialPlanetKindContentTests.Content();
        Assert.Equal(6, content.Bodies.Count);
        foreach (var pair in content.Bodies)
            Assert.True(pair.Value.ToTemplate().Success, pair.Key + ": " + string.Join(",", pair.Value.ConfigErrors()));
        // 防止“全部跳过”导致的空验证：四种固体模板都是 Parameterized terrain（另外两个巨行星没有 biomes）。
        var parameterized = content.Bodies.Values.SelectMany(body => body.comps.OfType<CompProperties_BiomeGeneration>())
            .Where(comp => comp.profile?.terrain != null).ToArray();
        Assert.Equal(4, parameterized.Length);
    }

    [Fact]
    public void ShippedSolarSystemBodiesSatisfyVacuumContract()
    {
        var bodies = LoadSolarSystemBodies();
        // 2026-11-11 合并 origin/master：本地 b9ebf08 新增行星际目标 ZheSha（赭砂）⇒ 期望集合随内容扩一条。
        Assert.Equal(new[] { "Earth", "Moon", "Sun", "ZheSha" }, bodies.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        foreach (var pair in bodies)
            Assert.True(pair.Value.ToTemplate().Success, pair.Key + ": " + string.Join(",", pair.Value.ConfigErrors()));
        // Moon 是太阳系里唯一 Parameterized terrain + 非空真空声明的天体，必须真的走到跨校验。
        var moon = bodies["Moon"].comps.OfType<CompProperties_BiomeGeneration>().Single();
        Assert.NotNull(moon.profile.terrain);
        Assert.Contains(moon.profile.terrain.biomeRules, rule => rule.biome != null && rule.biome.inVacuum);
        Assert.NotEmpty(bodies["Moon"].comps.OfType<CompProperties_ExistingEnvironment>().Single().profile.vacuumBiomes);
    }

    private static CelestialPlanetBiomeRuleXml Rule(string key, BiomeDef biome, bool fallback = true)
        => new() { key = key, biome = biome, fallback = fallback };

    private static CelestialBiomeProfileDef Parameterized(params CelestialPlanetBiomeRuleXml[] rules) => new() {
        defName = "PD_VacuumBiomes", mode = CelestialBiomeGenerationMode.Parameterized,
        terrain = new CelestialPlanetTerrainXml { biomeRules = rules.ToList() } };

    private static CelestialBiomeProfileDef Vanilla(params WorldGenStepDef[] steps) => new() {
        defName = "PD_VanillaBiomes", mode = CelestialBiomeGenerationMode.VanillaSurface, worldGenSteps = steps.ToList() };

    private static WorldGenStepDef TerrainStep() => new() { defName = "Terrain", worldGenStep = new WorldGenStep_Terrain() };

    private static CelestialEnvironmentProfileDef EnvironmentProfile(params BiomeDef[] vacuumBiomes) => new() {
        defName = "PD_VacuumEnvironment", vacuumBiomes = vacuumBiomes.ToList() };

    private static CompProperties_ExistingEnvironment EnvironmentComp(params BiomeDef[] vacuumBiomes)
        => new() { key = "environment", profile = EnvironmentProfile(vacuumBiomes) };

    private static CelestialBodyDef Body(CelestialBiomeProfileDef biomes, params BiomeDef[] vacuumBiomes) => new() {
        defName = "PD_VacuumBody",
        comps = new() {
            new CompProperties_SphericalBody { key = "shape" },
            new CompProperties_Surface { key = "surface" },
            new CompProperties_BiomeGeneration { key = "biomes", profile = biomes },
            EnvironmentComp(vacuumBiomes) } };

    // 已发货 SolarSystem 内容没有可复用的公共夹具：按 CelestialDefaultContentTests 的方式显式加载并补齐 profile 交叉引用。
    // 与那边不同的是：biome 的 inVacuum 必须来自真实 BiomeDef XML（PF_MoonBiome* 为 true），否则跨校验会误判。
    private static Dictionary<string, CelestialBodyDef> LoadSolarSystemBodies()
    {
        using var scope = new CelestialXmlTestScope();
        var biomeDefinitions = LoadBiomeDefinitions();
        BiomeDef Biome(string name) => biomeDefinitions.TryGetValue(name, out var biome) ? biome : new BiomeDef { defName = name };
        var profilesXml = XDocument.Load(CelestialDefaultContentTests.DataPath("CelestialFramework/CelestialProfiles.xml"));
        var visualsXml = XDocument.Load(CelestialDefaultContentTests.DataPath("CelestialFramework/CelestialVisualProfiles.xml"));
        var visuals = visualsXml.Root.Elements("PioneerFramework.Celestial.CelestialVisualProfileDef").ToDictionary(
            element => element.Element("defName")!.Value,
            element => {
                var profile = Parse<CelestialVisualProfileDef>(element);
                profile.backgroundBiome = element.Element("backgroundBiome") is { } background ? Biome(background.Value) : null;
                return profile;
            });
        var environments = profilesXml.Root.Elements("PioneerFramework.Celestial.CelestialEnvironmentProfileDef").ToDictionary(
            element => element.Element("defName")!.Value,
            element => {
                var profile = Parse<CelestialEnvironmentProfileDef>(element);
                profile.vacuumBiomes = element.Element("vacuumBiomes")?.Elements("li").Select(item => Biome(item.Value)).ToList() ?? new();
                return profile;
            });
        var biomes = profilesXml.Root.Elements("PioneerFramework.Celestial.CelestialBiomeProfileDef").ToDictionary(
            element => element.Element("defName")!.Value,
            element => {
                var profile = Parse<CelestialBiomeProfileDef>(element);
                List<WorldGenStepDef> Steps(string field) => element.Element(field)?.Elements("li")
                    .Select(node => new WorldGenStepDef { defName = node.Value, worldGenStep = new WorldGenStep_Terrain() }).ToList() ?? new();
                profile.worldGenSteps = Steps("worldGenSteps"); profile.rootOnlySteps = Steps("rootOnlySteps");
                if (element.Element("terrain")?.Element("biomeRules") is { } rules)
                    for (int index = 0; index < profile.terrain.biomeRules.Count; index++)
                        profile.terrain.biomeRules[index].biome = Biome(rules.Elements("li").ElementAt(index).Element("biome")!.Value);
                return profile;
            });
        var maps = profilesXml.Root.Elements("PioneerFramework.Celestial.CelestialMapProfileDef").ToDictionary(
            element => element.Element("defName")!.Value,
            element => {
                var profile = Parse<CelestialMapProfileDef>(element);
                if (element.Element("localRecipe") is { } local) profile.localRecipe = new LocalMapRecipeDef { defName = local.Value };
                return profile;
            });
        var bodies = new Dictionary<string, CelestialBodyDef>(StringComparer.Ordinal);
        foreach (var element in XDocument.Load(CelestialDefaultContentTests.DataPath("CelestialBodies/CelestialBodies_SolarSystem.xml"))
            .Root.Elements("PioneerFramework.CelestialBodyDef"))
        {
            var body = scope.ParseBody(element.ToString());
            foreach (var properties in body.comps)
            {
                var source = element.Element("comps")!.Elements("li").Single(node => node.Element("key")!.Value == properties.key);
                if (properties is CompProperties_ExistingEnvironment environment) environment.profile = environments[source.Element("profile")!.Value];
                if (properties is CompProperties_BiomeGeneration biome) biome.profile = biomes[source.Element("profile")!.Value];
                if (properties is CompProperties_MapGeneration map) map.profile = maps[source.Element("profile")!.Value];
                if (properties is CelestialVisualCompProperties visual) visual.profile = visuals[source.Element("profile")!.Value];
            }
            bodies[body.defName] = body;
        }
        return bodies;
    }

    private static Dictionary<string, BiomeDef> LoadBiomeDefinitions()
    {
        var result = new Dictionary<string, BiomeDef>(StringComparer.Ordinal);
        foreach (string file in new[] { "BiomesDefs/Biome_PlanetKinds.xml", "BiomesDefs/Biome_Planets.xml" })
            foreach (var element in XDocument.Load(CelestialDefaultContentTests.DataPath(file)).Root.Elements("BiomeDef"))
            {
                // Abstract templates (Name/ParentName) carry no defName; only concrete shipped biomes matter here.
                string? name = element.Element("defName")?.Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result[name] = new BiomeDef { defName = name, inVacuum = element.Element("inVacuum")?.Value == "true" };
            }
        return result;
    }

    private static T Parse<T>(XElement element) where T : new()
    {
        var xml = new XmlDocument(); xml.LoadXml(element.ToString());
        return DirectXmlToObject.ObjectFromXml<T>(xml.DocumentElement, false);
    }

    private static TerrainDef Terrain(string id)
    {
        var terrain = (TerrainDef)RuntimeHelpers.GetUninitializedObject(typeof(TerrainDef));
        terrain.defName = id; return terrain;
    }
}
