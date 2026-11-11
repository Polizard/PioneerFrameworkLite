using System;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialLayerConfigurationTests
{
    private static CelestialSurfaceHeader Header(ReadOnlyMemory<byte> configuration)
        => new(CelestialInstanceId.Parse("layer-config"), -1, 100000, 30, 1, 42, configuration);

    [Fact]
    public void SameHeaderDecodesOnceAndIsSharedByEveryConsumer()
    {
        var header = Header(CelestialGenerationConfiguration.Encode(Array.Empty<CelestialCompSaveRecord>()));
        var first = CelestialLayerConfiguration.For(header);
        var second = CelestialLayerConfiguration.For(header);
        Assert.Same(first, second);
        Assert.True(first.Success);
        Assert.Empty(first.Value);
    }

    [Fact]
    public void InvalidBlobIsRejectedOncePerHeaderWithoutThrowing()
    {
        var header = Header(new byte[] { 9, 0, 0, 0 });
        var decoded = CelestialLayerConfiguration.For(header);
        Assert.False(decoded.Success);
        Assert.Equal("InvalidGenerationConfiguration", Assert.Single(decoded.Errors).Code);
        Assert.Same(decoded, CelestialLayerConfiguration.For(header));
    }
}
