using System;
using PioneerFramework.Combat;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    public class ShotMathTests
    {
        [Theory]
        [InlineData(0, 4, 1, 1)]
        [InlineData(2, 4, 1, 0.5)]
        [InlineData(2, 4, 2, 0.25)]
        [InlineData(4, 4, 1, 0)]
        [InlineData(5, 4, 1, 0)]
        public void ExplosionFalloffHonorsRadiusAndXmlExponent(double distance, double radius, double exponent, double expected)
            => Assert.Equal(expected, ShotMath.BlastFalloff(distance, radius, exponent), 6);

        [Fact]
        public void InvalidVelocityFactorsCannotProduceAValidLaunch()
        {
            Assert.Equal(600, ShotMath.MuzzleSpeed(1000, 1.2, 0.5));
            Assert.Equal(0, ShotMath.MuzzleSpeed(1000, double.NaN, 1));
            Assert.Equal(0, ShotMath.MuzzleSpeed(1000, 1, -1));
            Assert.Equal(0, ShotMath.MuzzleSpeed(double.MaxValue, 2, 1));
        }
    }
}
