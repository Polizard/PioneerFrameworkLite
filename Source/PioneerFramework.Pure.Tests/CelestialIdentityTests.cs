using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class CelestialIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" moon")]
    [InlineData("moon ")]
    public void InvalidIdentityCannotEnterAnInstance(string? value)
    {
        Assert.Throws<ArgumentException>(() => CelestialInstanceId.Parse(value!));
    }

    [Fact]
    public void IdentitiesRemainCaseSensitiveAndStable()
    {
        Assert.Equal(CelestialInstanceId.Parse("system/moon[1]"), CelestialInstanceId.Parse("system/moon[1]"));
        Assert.NotEqual(CelestialInstanceId.Parse("Moon"), CelestialInstanceId.Parse("moon"));
    }
}
