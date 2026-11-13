using System;
using System.Collections.Generic;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using Xunit;

namespace PioneerFramework.Pure.Tests;

/// <summary>
/// P1-4（2026-11-06）：本地地图生成的确定性 golden 基线。
/// 这些字面量由当前实现一次性冻结。任何改动种子派生、配方 canonical 编码/字段顺序、
/// 或地形采样数值语义的提交，必须先显式升级对应版本（<see cref="CelestialPlanetTerrain.AlgorithmVersion"/>、
/// <see cref="LocalMapGenerationCodec.CurrentVersion"/>），并有意更新本基线；
/// 不允许直接把期望值改成新实现输出（那等于放弃回归保护）。
/// </summary>
public sealed class LocalMapGoldenTests
{
    private static MapSourceContext Source() =>
        new("golden-map", 0x0123456789ABCDEFUL, 42, 250, 250, "TemperateForest");
    private static MapGenerationStepSpec NaturalStep() =>
        new("natural.probe", "probe.handler", 2, 3, MapGenerationStage.NaturalSurface, MapGenerationDomain.Natural,
            new byte[] { 1, 2, 3 }, after: new[] { "x" });
    private static MapGenerationStepSpec PurposeStep() =>
        new("purpose.probe", "probe.handler", 2, 3, MapGenerationStage.Content, MapGenerationDomain.Purpose,
            new byte[] { 4, 5 });
    private static LocalMapGenerationSpec Spec() => new(
        new LocalMapRecipe("golden", new[] { NaturalStep() }),
        new Dictionary<MapGenerationPurpose, LocalMapRecipe>
        {
            [MapGenerationPurpose.Quest] = new("golden.quest", new[] { PurposeStep() }),
        },
        "Base_Player");

    [Fact]
    public void MapSeedsAndRecipeCanonicalBytesMatchFrozenGoldenBaseline()
    {
        var source = Source();
        Assert.Equal(0x0032BCE21B3AABC6UL, MapGenerationSeeds.Root(source));
        Assert.Equal(0x1441895C023E7E1FUL, MapGenerationSeeds.ForStep(source, MapGenerationPurpose.Colony, NaturalStep()));
        Assert.Equal(0x07D7522F1381EE6AUL, MapGenerationSeeds.ForStep(source, MapGenerationPurpose.Quest, PurposeStep()));
        Assert.Equal("7B22666F726D617456657273696F6E223A312C22726563697065223A7B226964223A22676F6C64656E222C227374657073223A5B7B226B6579223A226E61747572616C2E70726F6265222C2268616E646C65724964223A2270726F62652E68616E646C6572222C22636F6E66696775726174696F6E56657273696F6E223A322C22616C676F726974686D56657273696F6E223A332C227374616765223A312C22646F6D61696E223A302C22636F6E66696775726174696F6E223A2241514944222C226265666F7265223A5B5D2C226166746572223A5B2278225D2C22696E70757473223A5B5D2C226F757470757473223A5B5D2C226E61746976654F72646572223A6E756C6C2C2269734F7061717565223A66616C73657D5D7D2C22707572706F736552656369706573223A5B7B22707572706F7365223A332C22726563697065223A7B226964223A22676F6C64656E2E7175657374222C227374657073223A5B7B226B6579223A22707572706F73652E70726F6265222C2268616E646C65724964223A2270726F62652E68616E646C6572222C22636F6E66696775726174696F6E56657273696F6E223A322C22616C676F726974686D56657273696F6E223A332C227374616765223A322C22646F6D61696E223A312C22636F6E66696775726174696F6E223A224241553D222C226265666F7265223A5B5D2C226166746572223A5B5D2C22696E70757473223A5B5D2C226F757470757473223A5B5D2C226E61746976654F72646572223A6E756C6C2C2269734F7061717565223A66616C73657D5D7D7D5D2C226E617469766547656E657261746F724964223A22426173655F506C61796572227D",
            Convert.ToHexString(LocalMapGenerationCodec.Encode(Spec()).Span));
        Assert.Equal("DC207B58DB26BF107333425AE41DCA17466303B36BECF963ECF89661425E8428", LocalMapGenerationCodec.SnapshotHash(Spec()));
        // Domain semantics: natural seeds are purpose-independent, purpose seeds are purpose-scoped.
        Assert.Equal(MapGenerationSeeds.ForStep(source, MapGenerationPurpose.Colony, NaturalStep()),
            MapGenerationSeeds.ForStep(source, MapGenerationPurpose.Quest, NaturalStep()));
        Assert.NotEqual(MapGenerationSeeds.ForStep(source, MapGenerationPurpose.Colony, PurposeStep()),
            MapGenerationSeeds.ForStep(source, MapGenerationPurpose.Quest, PurposeStep()));
    }

    [Fact]
    public void TerrainAlgorithmVersionIsPinnedToItsGoldenLiteral()
    {
        // 改 Sample/SelectBiome/SurfaceMoisture/SelectOceanTiles/Field 的数值语义必须 +1 并显式拒绝旧档；
        // LegacyVersionWithoutHeaderField 表示"该字段引入前写出的 v4 档"，因此它不能随当前版本一起漂移。
        Assert.Equal(1, CelestialPlanetTerrain.AlgorithmVersion);
        Assert.Equal(1, CelestialPlanetTerrain.LegacyVersionWithoutHeaderField);
    }

    [Fact]
    public void TerrainSamplingMatchesFrozenGoldenBaseline()
    {
        var terrain = new CelestialPlanetTerrainConfiguration(
            continentScale: 2.5, elevationAmplitudeMeters: 4200, elevationOffsetMeters: -300, roughness: .27, mountainWeight: .63,
            fieldSeed: 1234567,
            biomeRules: new[] { new CelestialPlanetBiomeRule("fallback", "fallback-biome", fallback: true) });
        var samples = new[]
        {
            CelestialPlanetTerrain.Sample(terrain, 0, 0, terrain.FieldSeed),
            CelestialPlanetTerrain.Sample(terrain, 45, 45, terrain.FieldSeed),
            CelestialPlanetTerrain.Sample(terrain, 123, 89, terrain.FieldSeed),
            CelestialPlanetTerrain.Sample(terrain, -179, -60, terrain.FieldSeed),
        };
        Assert.Equal((1322.57228422505, 0.18999447747945356, 0.1375503161096668, 0.4354646723231576, 0.47033430411087973),
            (samples[0].ElevationMeters, samples[0].Roughness, samples[0].MountainScore, samples[0].Geology, samples[0].Moisture));
        Assert.Equal((1346.403745048069, 0.18918690393809073, 0.15284552729940407, 0.8129456018438268, 0.7540074830058849),
            (samples[1].ElevationMeters, samples[1].Roughness, samples[1].MountainScore, samples[1].Geology, samples[1].Moisture));
        Assert.Equal((1059.6084520007944, 0.11914380313929597, 0.21439504337180695, 0.633082579722406, 0.7554144042932565),
            (samples[2].ElevationMeters, samples[2].Roughness, samples[2].MountainScore, samples[2].Geology, samples[2].Moisture));
        Assert.Equal((-3204.988328705941, 0.1383756782216865, 0.38923395951765405, 0.4298493468091487, 0.2157018049896241),
            (samples[3].ElevationMeters, samples[3].Roughness, samples[3].MountainScore, samples[3].Geology, samples[3].Moisture));
        // 同输入必须同输出（防浮点归约顺序被无意改写）。
        Assert.Equal(samples[0], CelestialPlanetTerrain.Sample(terrain, 0, 0, terrain.FieldSeed));
    }
}
