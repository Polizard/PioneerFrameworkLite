using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewUiPolicyTests
{
    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, true, false)]
    public void EntityProjectionRequiresActualPlayerKnowledge(bool active, bool own, bool stealth, bool known, bool expected)
        => Assert.Equal(expected, PFViewUiPolicy.CanShowEntity(active, own, stealth, known));

    // Breaks caught: wrong automatic trigger, ignored manual override, or hidden exit control.
    [Theory]
    [InlineData(false, false, 0, 0, false, false, false)]
    [InlineData(false, false, 0, 1, false, true, false)]
    [InlineData(false, false, 0, 2, false, false, false)]
    [InlineData(false, false, 1, 0, true, false, false)]
    [InlineData(false, false, 1, 1, true, true, false)]
    [InlineData(false, false, 1, 2, true, false, false)]
    [InlineData(false, false, 2, 0, false, false, false)]
    [InlineData(false, false, 2, 1, false, true, false)]
    [InlineData(false, false, 2, 2, false, false, false)]
    [InlineData(true, false, 0, 0, true, false, false)]
    [InlineData(true, false, 0, 1, true, true, false)]
    [InlineData(true, false, 0, 2, true, false, false)]
    [InlineData(true, false, 1, 0, true, false, false)]
    [InlineData(true, false, 1, 1, true, true, false)]
    [InlineData(true, false, 1, 2, true, false, false)]
    [InlineData(true, false, 2, 0, false, false, true)]
    [InlineData(true, false, 2, 1, false, true, true)]
    [InlineData(true, false, 2, 2, false, false, true)]
    [InlineData(false, true, 0, 0, false, true, false)]
    [InlineData(false, true, 0, 1, false, true, false)]
    [InlineData(false, true, 0, 2, false, false, false)]
    [InlineData(false, true, 1, 0, true, true, false)]
    [InlineData(false, true, 1, 1, true, true, false)]
    [InlineData(false, true, 1, 2, true, false, false)]
    [InlineData(false, true, 2, 0, false, true, false)]
    [InlineData(false, true, 2, 1, false, true, false)]
    [InlineData(false, true, 2, 2, false, false, false)]
    [InlineData(true, true, 0, 0, true, true, false)]
    [InlineData(true, true, 0, 1, true, true, false)]
    [InlineData(true, true, 0, 2, true, false, false)]
    [InlineData(true, true, 1, 0, true, true, false)]
    [InlineData(true, true, 1, 1, true, true, false)]
    [InlineData(true, true, 1, 2, true, false, false)]
    [InlineData(true, true, 2, 0, false, true, true)]
    [InlineData(true, true, 2, 1, false, true, true)]
    [InlineData(true, true, 2, 2, false, false, true)]
    public void InstrumentsFollowTasksAndExplicitOverrides(bool steering, bool weapons,
        int navMode, int radarMode, bool navVisible, bool radarVisible, bool exitStrip)
    {
        var result = PFViewUiPolicy.Resolve(steering, weapons,
            (PFViewPanelMode)navMode, (PFViewPanelMode)radarMode);
        Assert.Equal(navVisible, result.ShowNavball);
        Assert.Equal(radarVisible, result.ShowRadar);
        Assert.Equal(exitStrip, result.ShowSteeringStrip);
    }
}
