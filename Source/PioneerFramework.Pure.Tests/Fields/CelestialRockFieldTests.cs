using System;
using System.Text;
using System.Text.Json;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests.Fields
{
    /// <summary>
    /// B 方案岩质场地貌的纯核判据（2026-11-08）。
    /// 这些测试只钉"场是确定的、在域内、真的有地貌"；不钉具体像素值（内容 golden 在集成侧）。
    /// </summary>
    public class CelestialRockFieldTests
    {
        private static CelestialPlanetTerrainConfiguration Rock(double roughness = .5, double mountainWeight = .5,
            double amplitude = 3000, double offset = 0)
            => new(elevationAmplitudeMeters: amplitude, elevationOffsetMeters: offset, roughness: roughness,
                mountainWeight: mountainWeight, fieldKind: CelestialPlanetTerrainFieldKind.Rock);

        [Fact]
        public void RockSampleIsDeterministic()
        {
            var configuration = Rock();
            var first = CelestialPlanetTerrain.Sample(configuration, 37.5, 62.25, 12345);
            var second = CelestialPlanetTerrain.Sample(configuration, 37.5, 62.25, 12345);
            Assert.Equal(first, second);
        }

        [Fact]
        public void RockSampleDependsOnSeed()
        {
            var configuration = Rock();
            var first = CelestialPlanetTerrain.Sample(configuration, 10, 10, 1);
            var second = CelestialPlanetTerrain.Sample(configuration, 10, 10, 2);
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void DirectionIsNormalizedBeforeSampling()
        {
            var configuration = Rock();
            // 同一方向的两种长度必须给出同一场值（陨坑/噪声都按单位球算）。
            // 用能精确归一的方向（(0,0,1)/(0,0,5)）：一般方向缩放会有 1 ulp 的除法舍入，不适合逐位比较。
            var unit = CelestialRockField.Sample(configuration, 0.0, 0.0, 1.0, 7);
            var scaled = CelestialRockField.Sample(configuration, 0.0, 0.0, 5.0, 7);
            Assert.Equal(unit, scaled);
        }

        [Fact]
        public void EveryChannelStaysInItsDomain()
        {
            var configuration = Rock(roughness: .9, mountainWeight: .9, amplitude: 5000, offset: 250);
            for (int i = 0; i < 500; i++)
            {
                double lon = i * 7.31 % 360 - 180, lat = i * 0.37 % 180 - 90;
                var sample = CelestialPlanetTerrain.Sample(configuration, lon, lat, 42);
                Assert.InRange(sample.ElevationMeters, 250 - 5000, 250 + 5000);
                Assert.InRange(sample.Roughness, 0, 1);
                Assert.InRange(sample.MountainScore, 0, 1);
                Assert.InRange(sample.Geology, 0, 1);
                Assert.InRange(sample.Moisture, 0, 1);
            }
        }

        [Fact]
        public void RockFieldActuallyProducesReliefAndBasins()
        {
            var configuration = Rock(roughness: .6, mountainWeight: .8, amplitude: 3000);
            double low = 0, high = 0; const int count = 4096;
            for (int i = 0; i < count; i++)
            {
                double lon = i * 13.7 % 360 - 180, lat = i * 0.73 % 180 - 90;
                var sample = CelestialPlanetTerrain.Sample(configuration, lon, lat, 7);
                if (sample.ElevationMeters < -900) low++;
                if (sample.ElevationMeters > 900) high++;
            }
            // 陨坑/盆地必须真的压出低地，山脊必须真的抬出高地；比例是"有这个特征"的宽松下界。
            Assert.True(low / count > 0.01, "low fraction " + (low / count));
            Assert.True(high / count > 0.01, "high fraction " + (high / count));
        }

        [Fact]
        public void WavesAndRockAreDifferentFieldsButSameConfigurationShape()
        {
            var waves = new CelestialPlanetTerrainConfiguration(elevationAmplitudeMeters: 3000, roughness: .5, mountainWeight: .5);
            var rock = Rock();
            var a = CelestialPlanetTerrain.Sample(waves, 12, 34, 99);
            var b = CelestialPlanetTerrain.Sample(rock, 12, 34, 99);
            Assert.NotEqual(a, b);
            // 省略 fieldKind 与显式 Waves 必须完全一致（旧存档/旧内容哈希不动的判据）。
            var explicitWaves = new CelestialPlanetTerrainConfiguration(elevationAmplitudeMeters: 3000, roughness: .5, mountainWeight: .5,
                fieldKind: CelestialPlanetTerrainFieldKind.Waves);
            Assert.Equal(a, CelestialPlanetTerrain.Sample(explicitWaves, 12, 34, 99));
        }

        [Fact]
        public void HydrologyCarvesChannelsAndRaisesMoistureThere()
        {
            var configuration = Rock(roughness: .6, mountainWeight: .8, amplitude: 3000);
            int channels = 0, carvedDeeper = 0;
            const int count = 1024;
            for (int i = 0; i < count; i++)
            {
                double lon = i * 17.3 % 360 - 180, lat = i * 1.27 % 180 - 90;
                var wet = CelestialPlanetTerrain.Sample(configuration, lon, lat, 11);
                var dry = CelestialRockField.SampleBase(configuration,
                    Math.Sin(lon * Math.PI / 180) * Math.Cos(lat * Math.PI / 180),
                    Math.Sin(lat * Math.PI / 180),
                    -Math.Cos(lon * Math.PI / 180) * Math.Cos(lat * Math.PI / 180), 11, out _, out _);
                if (wet.Moisture - dry.Moisture > 0.2)
                {
                    channels++;
                    if (wet.ElevationMeters < dry.ElevationMeters) carvedDeeper++;
                }
            }
            Assert.True(channels > 0, "no channel samples: " + channels);
            Assert.True(carvedDeeper == channels, "channels should be carved deeper: " + carvedDeeper + "/" + channels);
        }

        [Fact]
        public void HydrologyCacheIsBounded()
        {
            // 不同 seed/参数会产生不同流域图；缓存必须有上限，否则长测试/多天体世界会无限增长。
            for (int seed = 100; seed < 140; seed++)
                CelestialPlanetTerrain.Sample(Rock(roughness: .3 + (seed % 7) * .05), seed * 1.7 % 360 - 180, seed % 170 - 85, seed);
            Assert.InRange(CelestialRockField.CachedHydrologyCount, 0, 16);
        }
        [Fact]
        public void DefaultConfigurationOmitsFieldKindFromJsonButRockWritesIt()
        {
            var waves = new CelestialGenerationProfile("probe-waves", CelestialBiomeGenerationMode.Parameterized, 0, 0, 0,
                Array.Empty<string>(), Array.Empty<string>(), new CelestialPlanetTerrainConfiguration(
                    biomeRules: new[] { new CelestialPlanetBiomeRule("fallback", "TemperateForest", fallback: true) }));
            var rock = new CelestialGenerationProfile("probe-rock", CelestialBiomeGenerationMode.Parameterized, 0, 0, 0,
                Array.Empty<string>(), Array.Empty<string>(), new CelestialPlanetTerrainConfiguration(
                    biomeRules: new[] { new CelestialPlanetBiomeRule("fallback", "TemperateForest", fallback: true) },
                    fieldKind: CelestialPlanetTerrainFieldKind.Rock));
            string wavesJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(waves));
            string rockJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(rock));
            Assert.DoesNotContain("FieldKind", wavesJson, StringComparison.Ordinal);
            Assert.Contains("FieldKind", rockJson, StringComparison.Ordinal);
        }
    }
}