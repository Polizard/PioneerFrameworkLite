using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    public class GoodingTests
    {
        private const double Mu = 3.986004418e14;

        [Fact]
        public void Solve_HohmannCollinearTransfer_ReturnsFiniteVelocities()
        {
            double r1 = 7_000_000.0;
            double r2 = 42_000_000.0;
            double a = (r1 + r2) * 0.5;
            double tof = Math.PI * Math.Sqrt(a * a * a / Mu);

            var (vi, vf) = Gooding.Solve(
                Mu,
                new DVector3(r1, 0, 0),
                new DVector3(-r2, 0, 0),
                tof,
                TransferGeometry.ShortWay,
                0);

            Assert.False(double.IsNaN(vi.x) || double.IsNaN(vi.y) || double.IsNaN(vi.z));
            Assert.False(double.IsNaN(vf.x) || double.IsNaN(vf.y) || double.IsNaN(vf.z));
            Assert.True(vi.magnitude > 1e-9);
            Assert.True(vf.magnitude > 1e-9);
        }

        [Fact]
        public void Solve_NonPositiveTimeOfFlight_Throws()
        {
            Assert.Throws<ArgumentException>(() => Gooding.Solve(
                Mu,
                new DVector3(7_000_000, 0, 0),
                new DVector3(42_000_000, 0, 0),
                0.0));
        }
    }
}
