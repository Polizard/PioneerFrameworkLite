using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class CelestialParameterTests
{
    [Theory]
    [InlineData("earth[0]", 0x798D2A0A26C992FCUL)]
    [InlineData("earth[0]/moon[0]", 0x2CC6D4A1C30AA049UL)]
    [InlineData("earth[0]/moon[1]", 0x5F5F885420BB199AUL)]
    public void SeedProtocolMatchesIndependentGoldenFixtures(string path, ulong expected)
        => Assert.Equal(expected, CelestialSeed.Derive(1, path, "shape", "radiusMeters"));

    [Fact]
    public void FieldSamplingIsIndependentOfEvaluationOrder()
    {
        ulong radiusSeed = CelestialSeed.Derive(31, "地球/月球[0]", "shape", "radius");
        double first = CelestialParameterSampler.SampleDouble(DoubleParameterSpec.Uniform(100, 200), radiusSeed);
        _ = CelestialSeed.Derive(31, "地球/月球[0]", "atmosphere", "scaleHeight");
        _ = CelestialSeed.Derive(31, "地球/月球[1]", "shape", "radius");
        double second = CelestialParameterSampler.SampleDouble(DoubleParameterSpec.Uniform(100, 200),
            CelestialSeed.Derive(31, "地球/月球[0]", "shape", "radius"));
        Assert.Equal(first, second);
        Assert.NotEqual(CelestialSeed.Derive(1, "ab", "c", "d"), CelestialSeed.Derive(1, "a", "bc", "d"));
    }

    [Fact]
    public void FixedValuesAndClosedFloatingBoundsArePreserved()
    {
        Assert.Equal(1500000d, CelestialParameterSampler.SampleDouble(DoubleParameterSpec.Fixed(1500000), 1));
        Assert.Equal(30d, CelestialParameterSampler.SampleDouble(DoubleParameterSpec.Uniform(30, 30), ulong.MaxValue));
        Assert.Equal(10d, CelestialParameterSampler.SampleDouble(DoubleParameterSpec.Uniform(10, 20), 0));
        Assert.Equal(20d, CelestialParameterSampler.SampleDouble(DoubleParameterSpec.Uniform(10, 20), ulong.MaxValue));
        double large = CelestialParameterSampler.SampleDouble(DoubleParameterSpec.Uniform(-double.MaxValue, double.MaxValue), 1UL << 63);
        Assert.True(double.IsFinite(large));
        Assert.InRange(large, -double.MaxValue, double.MaxValue);
    }

    [Fact]
    public void IntegerSamplingIncludesBothBoundsAndSupportsTheEntireIntDomain()
    {
        Assert.Equal(1, CelestialParameterSampler.SampleInt(IntParameterSpec.Uniform(1, 3), 0));
        Assert.Equal(3, CelestialParameterSampler.SampleInt(IntParameterSpec.Uniform(1, 3), 2));
        Assert.Equal(int.MinValue, CelestialParameterSampler.SampleInt(IntParameterSpec.Uniform(int.MinValue, int.MaxValue), 0));
        Assert.Equal(int.MaxValue, CelestialParameterSampler.SampleInt(IntParameterSpec.Uniform(int.MinValue, int.MaxValue), uint.MaxValue));
        Assert.Equal(5, CelestialParameterSampler.SampleInt(IntParameterSpec.Fixed(5), ulong.MaxValue));
        Assert.InRange(CelestialParameterSampler.SampleInt(IntParameterSpec.Uniform(1, 3), ulong.MaxValue), 1, 3);
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(2, 1)]
    public void InvalidFloatingIntervalsAreRejected(double min, double max)
        => Assert.Throws<ArgumentOutOfRangeException>(() => DoubleParameterSpec.Uniform(min, max));

    [Fact]
    public void ReversedIntegerIntervalIsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => IntParameterSpec.Uniform(2, 1));
}
