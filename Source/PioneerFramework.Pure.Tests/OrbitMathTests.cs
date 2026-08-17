using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    public class OrbitMathTests
    {
        private const double Mu = 3.986004418e14;

        [Fact]
        public void SpecificEnergy_CircularOrbit_IsNegative()
        {
            double r = 7_000_000.0;
            double v = Math.Sqrt(Mu / r);
            var pos = new DVector3(r, 0, 0);
            var vel = new DVector3(0, 0, v);

            double energy = OrbitMath.SpecificEnergy(pos, vel, Mu);

            Assert.True(energy < 0.0);
            Assert.Equal(-Mu / (2.0 * r), energy, 6);
        }

        [Fact]
        public void SemiMajorAxis_CircularOrbit_EqualsRadius()
        {
            double r = 7_000_000.0;
            double v = Math.Sqrt(Mu / r);
            var pos = new DVector3(r, 0, 0);
            var vel = new DVector3(0, 0, v);

            double sma = OrbitMath.SemiMajorAxis(pos, vel, Mu);

            Assert.Equal(r, sma, 3);
        }

        [Fact]
        public void GetEccentricity_CircularOrbit_IsZero()
        {
            double r = 7_000_000.0;
            double v = Math.Sqrt(Mu / r);
            var pos = new DVector3(r, 0, 0);
            var vel = new DVector3(0, 0, v);

            OrbitMath.GetEccentricity(pos, vel, out _, out double e, Mu);

            Assert.True(e < 1e-6);
        }

        [Fact]
        public void IsOrbitSafe_RejectsPerigeeBelowSafeRadius()
        {
            double r = 7_000_000.0;
            double v = Math.Sqrt(Mu / r);
            var pos = new DVector3(r, 0, 0);
            var vel = new DVector3(0, 0, v);

            Assert.True(OrbitMath.IsOrbitSafe(pos, vel, 6_500_000.0, Mu));
            Assert.False(OrbitMath.IsOrbitSafe(pos, vel, 7_500_000.0, Mu));
        }

        [Fact]
        public void GetPeriapsisAndApoapsis_CircularOrbit_ReturnRadius()
        {
            double r = 7_000_000.0;
            Assert.Equal(r, OrbitMath.GetPeriapsisDistance(r, 0.0), 3);
            Assert.Equal(r, OrbitMath.GetApoapsisDistance(r, 0.0), 3);
        }
    }
}
