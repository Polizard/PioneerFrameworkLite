using System;
using PioneerFramework;
using PioneerFramework.Launch;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    public class LaunchTransitServiceTests
    {
        [Fact]
        public void RegistrationFailureRollsBackMutationEvenWhenRegisterNeverReturns()
        {
            bool registered = false;
            bool success = LaunchTransitService.TryRunWithRollback(
                () =>
                {
                    registered = true;
                    throw new InvalidOperationException("post-registration failure");
                },
                () => registered = false,
                out string reason);

            Assert.False(success);
            Assert.False(registered);
            Assert.Contains("post-registration failure", reason);
        }

        [Fact]
        public void SuccessfulRegistrationDoesNotRunRollback()
        {
            bool registered = false;
            bool success = LaunchTransitService.TryRunWithRollback(
                () => registered = true,
                () => registered = false,
                out string reason);

            Assert.True(success);
            Assert.True(registered);
            Assert.Empty(reason);
        }

        [Fact]
        public void RollbackFailureIsReturnedTogetherWithOriginalFailure()
        {
            bool success = LaunchTransitService.TryRunWithRollback(
                () => throw new InvalidOperationException("registration failed"),
                () => throw new InvalidOperationException("cleanup failed"),
                out string reason);

            Assert.False(success);
            Assert.Contains("registration failed", reason);
            Assert.Contains("cleanup failed", reason);
        }

        [Fact]
        public void OrbitTargetRejectsPositionInsideBodyAndNonFiniteVelocity()
        {
            var orbit = new OrbitState
            {
                positionSystem = (SystemVec3)new DVector3(0, 0, 90),
                velocitySystem = (SystemVec3)new DVector3(1, 0, 0),
                rotation = DQuaternion.identity
            };

            Assert.False(LaunchTransitService.TryValidateTarget(orbit, DVector3.zero, 100, out string reason));
            Assert.NotEmpty(reason);

            orbit.positionSystem = (SystemVec3)new DVector3(0, 0, 140);
            orbit.velocitySystem = (SystemVec3)new DVector3(double.NaN, 0, 0);
            Assert.False(LaunchTransitService.TryValidateTarget(orbit, DVector3.zero, 100, out reason));
            Assert.NotEmpty(reason);

            orbit.velocitySystem = (SystemVec3)new DVector3(1, 0, 0);
            Assert.True(LaunchTransitService.TryValidateTarget(orbit, DVector3.zero, 100, out reason));
        }
    }
}
