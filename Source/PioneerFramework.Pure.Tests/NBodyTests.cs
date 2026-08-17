using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    public class NBodyTests
    {
        [Fact]
        public void ComputeBodyAccelJerk_AccelerationPointsTowardBody()
        {
            double mu = 3.986004418e14;
            var sc = DVector3.zero;
            var scVel = DVector3.zero;
            var body = new DVector3(7_000_000, 0, 0);
            var bodyVel = DVector3.zero;

            NBodyIntegrator.ComputeBodyAccelJerk(
                sc, scVel, body, bodyVel, mu,
                out var acc, out var jerk);

            Assert.True(acc.x > 0.0);
            Assert.Equal(0.0, acc.y, 12);
            Assert.Equal(0.0, acc.z, 12);
            Assert.Equal(0.0, jerk.magnitude, 6);
        }

        [Fact]
        public void ThirdOrderTaylorStep_NoAcceleration_MovesLinearly()
        {
            var pos = new DVector3(1, 2, 3);
            var vel = new DVector3(4, 5, 6);
            double dt = 2.0;

            NBodyIntegrator.ThirdOrderTaylorStep(
                pos, vel, DVector3.zero, DVector3.zero, dt,
                out var newPos, out var newVel);

            Assert.Equal(9.0, newPos.x, 12);
            Assert.Equal(12.0, newPos.y, 12);
            Assert.Equal(15.0, newPos.z, 12);
            Assert.Equal(vel.x, newVel.x, 12);
            Assert.Equal(vel.y, newVel.y, 12);
            Assert.Equal(vel.z, newVel.z, 12);
        }
    }
}
