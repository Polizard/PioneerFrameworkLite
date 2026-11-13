using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PioneerFramework.Celestial;
using PioneerFramework.MapGeneration;
using RimWorld;
using RimWorld.Planet;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// P1-4（2026-11-06）：行星地图生成的确定性 golden 基线（Integration 面）。
/// 冻结：shipped CelestialMapProfile → LocalMapGenerationSpec 的 canonical 哈希、世界层 tile 数值快照、
/// 以及地形场在地表层的实际采样值。任何地图布局/世界生成语义改动都必须先升级
/// <see cref="CelestialPlanetTerrain.AlgorithmVersion"/>（或配方 codec 版本）并有意更新本文件。
/// </summary>
public class MapGenerationGoldenBaselineTests
{
    [Fact]
    public void ShippedMapProfilesKeepFrozenCanonicalRecipeHashes()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PF_EarthMaps"] = "3EBF930F5154FB1734AEFE1483E31F9B76B44D0430D85F70D79841C6497180CD",
            ["PF_MoonMaps"] = "182955EA8584526C2370B6C2E2F875A7138D3F2844023B7A8BC02A338F62AFCA",
            ["PF_BarrenMaps"] = "F166F706E329B2EBB57E4AF65DDB1BD9B478B50733421D63A0DD476F876091D9",
            ["PF_FrozenMaps"] = "6B7D621CF4DD10B8F7F5EBABFB3A2450C5852FE542B7BFA22A4AC774CE8775A5",
            ["PF_GreenhouseMaps"] = "4B48089EB79AB606556CF8DE801E1F0EFEBA35778FB477F57E1E156E0161CA9A",
            ["PF_TemperateMaps"] = "3F55AD5E027AB2481D8DD22E83D04ACCBCBEBDB59218B4ABA764D128823B8AB8",
        };
        using var content = new CelestialPlanetKindContentTests.Content();
        foreach (var pair in expected)
        {
            var result = content.Maps[pair.Key].ToProfile();
            Assert.True(result.Success, pair.Key + ": " + string.Join(",", result.Errors.Select(error => error.Detail)));
            var specification = Assert.IsType<LocalMapGenerationSpec>(result.Value.LocalSpecification);
            Assert.Equal(pair.Value, LocalMapGenerationCodec.SnapshotHash(specification));
        }
    }

    [Fact]
    public void WorldLayerTileNumericSnapshotMatchesFrozenGoldenBaseline()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        var source = LocalMapSourceResolver.CaptureSource(new PlanetTile(0, f.Root), 250, 250);
        Assert.Equal("PF_ProbeSystem:chosen[0]", source.SourceId);
        Assert.Equal(0x000000003ACBFCBDUL, source.GenerationSeed);
        Assert.Equal(0, source.TileId);
        Assert.Equal(250, source.SizeX);
        Assert.Equal(250, source.SizeZ);
        Assert.Equal("PF_ProbePrimary", source.BiomeId);
        var numeric = source.NumericData;
        Assert.Equal(200, numeric["coastDirection"]);
        Assert.Equal(1254.0794677734375, numeric["elevation"]);
        Assert.Equal(0.36957966186473823, numeric["geology"]);
        Assert.Equal(2, numeric["hilliness"]);
        Assert.Equal(52.68800735473633, numeric["latitude"]);
        Assert.Equal(-90, numeric["longitude"]);
        Assert.Equal(0, numeric["pollution"]);
        Assert.Equal(0, numeric["rainfall"]);
        Assert.Equal(0, numeric["riverCount"]);
        Assert.Equal(0, numeric["swampiness"]);
        Assert.Equal(0, numeric["temperature"]);
        // P1-5：地表 Header 必须记录当前地形算法版本。
        Assert.Equal(CelestialPlanetTerrain.AlgorithmVersion, f.Root.Header.TerrainAlgorithmVersion);
        Assert.Equal(1, f.Root.Header.TerrainAlgorithmVersion);
    }

    [Fact]
    public void ActualLayerTerrainSamplingMatchesFrozenGoldenBaseline()
    {
        using var f = new CelestialMapIdentityTests.Fixture();
        Assert.True(CelestialMapGeneration.TryGetTerrain(f.Root, out var terrain));
        var samples = new[]
        {
            (0d, 0d), (45d, 45d), (123d, 89d), (-179d, -60d),
        }.Select(p => CelestialPlanetTerrain.Sample(terrain, p.Item1, p.Item2, terrain.FieldSeed)).ToArray();
        Assert.Equal((-1012.1083384879264, 0.19524926975275939, 0.38916792590881144, 0.7730689322511319, 0.534872331410752),
            (samples[0].ElevationMeters, samples[0].Roughness, samples[0].MountainScore, samples[0].Geology, samples[0].Moisture));
        Assert.Equal((1457.040371213428, 0.1893234618682856, 0.33592690824029814, 0.3829123410297678, 0.22135867345237614),
            (samples[1].ElevationMeters, samples[1].Roughness, samples[1].MountainScore, samples[1].Geology, samples[1].Moisture));
        Assert.Equal((516.188850784493, 0.09344948139308634, 0.39917596398312793, 0.15820018112348438, 0.32468543235346226),
            (samples[2].ElevationMeters, samples[2].Roughness, samples[2].MountainScore, samples[2].Geology, samples[2].Moisture));
        Assert.Equal((846.4743804982613, 0.1997717744140148, 0.03929736120255156, 0.4384226131537947, 0.9799408491335253),
            (samples[3].ElevationMeters, samples[3].Roughness, samples[3].MountainScore, samples[3].Geology, samples[3].Moisture));
    }
}
