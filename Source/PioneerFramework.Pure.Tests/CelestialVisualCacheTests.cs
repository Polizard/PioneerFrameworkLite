using System;
using PioneerFramework.Celestial;
using Xunit;

public class CelestialVisualCacheTests
{
    [Fact]
    public void DiskIdentitySeparatesInstancesConfigurationAndSeedAndNeverUsesRawPaths()
    {
        var id = CelestialInstanceId.Parse("../other/body");
        string first = CelestialBakeCacheIdentity.DiskKey(13, id, 1, "config-a");
        Assert.Matches("^[0-9A-F]{64}$", first);
        Assert.Equal(first, CelestialBakeCacheIdentity.DiskKey(13, id, 1, "config-a"));
        Assert.NotEqual(first, CelestialBakeCacheIdentity.DiskKey(13, CelestialInstanceId.Parse("other-instance"), 1, "config-a"));
        Assert.NotEqual(first, CelestialBakeCacheIdentity.DiskKey(13, id, 1, "config-b"));
        Assert.NotEqual(first, CelestialBakeCacheIdentity.DiskKey(14, id, 1, "config-a"));
        Assert.NotEqual(first, CelestialBakeCacheIdentity.DiskKey(13, id, 2, "config-a"));
    }
    [Fact]
    public void ConfigurationFingerprintIncludesGeneratedValuesButExcludesMutableState()
    {
        var origin = new CelestialGenerationOrigin(13, "system/body");
        CelestialCompSaveRecord Record(byte configuration, byte state) => new("visual", "PFL.Visual.Surface", 1, 1, new[] { configuration }, new[] { state });
        string first = CelestialBakeCacheIdentity.ConfigurationHash(origin, new[] { Record(4, 0) });
        Assert.Equal(first, CelestialBakeCacheIdentity.ConfigurationHash(origin, new[] { Record(4, 99) }));
        Assert.NotEqual(first, CelestialBakeCacheIdentity.ConfigurationHash(origin, new[] { Record(5, 0) }));
        Assert.NotEqual(first, CelestialBakeCacheIdentity.ConfigurationHash(origin with { WorldSeed = 12 }, new[] { Record(4, 0) }));
    }
}
