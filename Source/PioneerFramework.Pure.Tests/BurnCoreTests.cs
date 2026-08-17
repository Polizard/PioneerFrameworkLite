using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    public class BurnCoreTests
    {
        [Fact]
        public void Estimate_SingleFeasibleNetwork_ReturnsExpectedShape()
        {
            var networks = new List<BurnNetworkInput>
            {
                new BurnNetworkInput(
                    thrust: 1000f,
                    massFlow: 1f,
                    ve: 1000f,
                    availableMass: 200f)
            };

            var result = BurnCore.Estimate(100f, 1000f, networks);

            Assert.True(result.Feasible);
            Assert.Single(result.Networks);
            Assert.True(result.RequiredPropellant > 0f);
            Assert.True(result.BurnSeconds > 0f);
            Assert.True(result.MaxDeltaV > 0f);
            Assert.True(result.Networks[0].RequiredPropellant <= 200f);
        }

        [Fact]
        public void Estimate_NoNetworks_IsInfeasible()
        {
            var result = BurnCore.Estimate(100f, 1000f, new List<BurnNetworkInput>());

            Assert.False(result.Feasible);
            Assert.Null(result.Networks);
            Assert.Equal(0f, result.RequiredPropellant);
        }

        [Fact]
        public void Estimate_InsufficientPropellant_IsInfeasible()
        {
            var networks = new List<BurnNetworkInput>
            {
                new BurnNetworkInput(1000f, 1f, 1000f, 1f)
            };

            var result = BurnCore.Estimate(100f, 1000f, networks);

            Assert.False(result.Feasible);
            Assert.False(result.Networks[0].Feasible);
        }
    }
}
