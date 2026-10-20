using PioneerFramework;
using PioneerFramework.Launch;
using UnityEngine;
using Xunit;

namespace PioneerFramework.Tests
{
    public class LaunchTransitWorldObjectTests
    {
        [Fact]
        public void TransitBecomesDueOnlyAfterAnimationAndStopsAfterFailure()
        {
            var target = new OrbitState
            {
                positionSystem = (SystemVec3)new DVector3(0, 0, 140),
                velocitySystem = (SystemVec3)new DVector3(1, 0, 0),
                rotation = DQuaternion.identity
            };
            var transit = new LaunchTransitWorldObject();
            transit.Initialize(42, "ship-42", 1, 7, DVector3.zero,
                new DVector3(100, 0, 0), target, 100, 60);

            Assert.Equal(LaunchTransitStatus.InFlight, transit.Status);
            Assert.False(transit.IsDue(159));
            Assert.True(transit.IsDue(160));
            Assert.Equal(new DVector3(0, 0, 140), transit.PositionAt(160));

            DVector3 stoppedAt = transit.PositionAt(130);
            transit.MarkFailed("map missing", 130);
            Assert.Equal(LaunchTransitStatus.Failed, transit.Status);
            Assert.Equal("map missing", transit.FailureReason);
            Assert.False(transit.IsDue(999));
            Assert.Equal(stoppedAt, transit.PositionAt(999));
            Assert.Equal(stoppedAt, transit.PositionAt(999, new DVector3(500, 0, 0)));
        }
    }
}
