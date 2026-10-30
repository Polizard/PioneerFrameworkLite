using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 点火唯一入口（2026-10-27）：`ManeuverPlan` → `BeginBurn`。
    /// 判据：计划字段正确落到燃烧状态；选网三口径；零 Δv 拒绝。
    /// </summary>
    public class ManeuverPlanTests
    {
        private static ManeuverPlan PlanAll(double dv = 100.0)
            => new() { dv = new DVector3(dv, 0, 0), networks = ManeuverNetworkMode.All, burnSeconds = 20.0 };

        [Fact]
        public void BeginBurn_AllNetworks_SetsCommandAccel()
        {
            var g = new SpaceEntityGroup(1, 0);

            Assert.True(g.BeginBurn(PlanAll()));

            Assert.True(g.HasActiveBurn);
            Assert.Equal(100.0, g.remainingBurnDV, 6);
            Assert.Equal(5.0, g.burnCommandAccel, 6);   // 100 / 20 s
            Assert.Null(g.burnEnabledNetworks);          // 全网络
            Assert.False(g.burnUseRcs);
        }

        [Fact]
        public void BeginBurn_Explicit_SelectsOnlyThatNetwork()
        {
            var g = new SpaceEntityGroup(2, 0);
            var hyd = new PropellantDef { defName = "PF_Propellant_Hydrazine" };
            var plan = new ManeuverPlan
            {
                dv = new DVector3(0, 10, 0),
                networks = ManeuverNetworkMode.Explicit,
                useNetworks = new HashSet<PropellantDef> { hyd },
                useRcs = true,
                burnSeconds = 10.0,
                isCorrection = true,
            };

            Assert.True(g.BeginBurn(in plan));

            Assert.True(g.burnUseRcs);
            Assert.True(g.burnIsCorrection);
            Assert.NotNull(g.burnEnabledNetworks);
            Assert.Contains(hyd, g.burnEnabledNetworks!);
            Assert.Equal(1.0, g.burnCommandAccel, 6);    // 10 / 10 s
        }

        [Fact]
        public void BeginBurn_ZeroDv_IsRejected()
        {
            var g = new SpaceEntityGroup(3, 0);
            var plan = PlanAll(0.0);

            Assert.False(g.BeginBurn(in plan));
            Assert.False(g.HasActiveBurn);
        }

        [Fact]
        public void BeginBurn_CommandAccelFallback_WhenNoBurnSeconds()
        {
            var g = new SpaceEntityGroup(4, 0);
            var plan = new ManeuverPlan
            {
                dv = new DVector3(0, 0, 50),
                networks = ManeuverNetworkMode.All,
                burnSeconds = 0.0,
                commandAccel = 2.5,
            };

            Assert.True(g.BeginBurn(in plan));
            Assert.Equal(2.5, g.burnCommandAccel, 6);
        }
    }
}
