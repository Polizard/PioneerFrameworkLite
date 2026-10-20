using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewPilotLayoutTests
{
    [Theory]
    [InlineData(1280, 720, true, true)]
    [InlineData(1024, 576, true, true)]
    [InlineData(853, 480, true, true)]
    [InlineData(640, 360, true, true)]
    [InlineData(1920, 1080, true, false)]
    [InlineData(853, 480, false, true)]
    public void ActivePilotingAlwaysHasReachableControlsWithoutCoveringTheInstruments(float width, float height, bool nav, bool radar)
    {
        var layout = PFViewUiLayoutCore.Compute(width, height, true, new(nav, radar, !nav), piloting: true);
        var pilot = layout.PilotPanel;
        Assert.True(pilot.Width >= 250);
        Assert.True(pilot.Height >= 72);
        foreach (var obstacle in new[] { layout.Toolbar, layout.ObjectList, layout.Details, layout.Navball,
                     layout.Radar, layout.Status, layout.SteeringStrip }) Assert.False(pilot.Overlaps(obstacle));
        if (layout.Navball.Width > 0)
            Assert.True(pilot.Y + pilot.Height <= layout.Navball.Y - 24);
        Assert.InRange(pilot.X, 0, width - pilot.Width);
        Assert.InRange(pilot.Y, 0, height - pilot.Height);
    }
}
