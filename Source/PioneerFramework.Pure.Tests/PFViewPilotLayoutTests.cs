using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewPilotLayoutTests
{
    [Theory]
    [InlineData(1120, 640)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void FlightHudFitsOneBottomBandWhenAllInstrumentsAreVisible(float width, float height)
    {
        var layout = PFViewUiLayoutCore.Compute(width, height, true, new(true, true, false), piloting: true);
        var nav = layout.Navball;
        var pilot = layout.PilotPanel;
        var controls = layout.FlightControls;
        Assert.Equal(88f, nav.Height);
        Assert.True(nav.Width > 0 && pilot.Width > 0 && controls.Width > 0);
        Assert.True(nav.X + nav.Width <= pilot.X);
        Assert.True(pilot.X + pilot.Width <= controls.X);
        Assert.Equal(height - 32, nav.Y + nav.Height);
        Assert.InRange(pilot.Y, nav.Y, nav.Y + nav.Height - pilot.Height);
        Assert.InRange(controls.Y, nav.Y, nav.Y + nav.Height - controls.Height);
    }

    [Fact]
    public void CompactDrawerKeepsBottomCommandsReachableAndDetachesOversizeInstruments()
    {
        var layout = PFViewUiLayoutCore.Compute(640, 360, true, new(true, true, false), piloting: true,
            drawerTab: PFViewDrawerTab.Weapons);
        Assert.True(layout.RequiresInstrumentDrawer);
        Assert.True(layout.ShowsToolbarItem(PFViewToolbarItem.Navball));
        Assert.Equal(0, layout.Navball.Width);
        Assert.False(layout.Drawer.Overlaps(layout.FlightControls));
        Assert.InRange(layout.FlightControls.Y, 360 - 32 - 54, 360 - 32 - layout.FlightControls.Height);
        Assert.True(layout.FlightControls.Width >= 112);
    }

    [Fact]
    public void ExtremeScaleKeepsFlightCommandsClearOfAlertsAndDrawer()
    {
        var shortView = PFViewUiLayoutCore.Compute(640, 176, false, new(false, false, false), alertVisible: true);
        Assert.True(shortView.FlightControls.Height >= 28);
        Assert.False(shortView.FlightControls.Overlaps(shortView.Alert));

        var narrowView = PFViewUiLayoutCore.Compute(320, 480, false, new(true, true, false), piloting: true,
            drawerTab: PFViewDrawerTab.Weapons);
        Assert.True(narrowView.Weapons.Width > 0);
        Assert.True(narrowView.FlightControls.Width >= 112);
        Assert.False(narrowView.FlightControls.Overlaps(narrowView.Drawer));
    }

    [Theory]
    [InlineData(1120, 640, true, true)]
    [InlineData(1280, 720, true, true)]
    [InlineData(1024, 576, true, true)]
    [InlineData(853, 480, true, true)]
    [InlineData(640, 360, true, true)]
    [InlineData(1920, 1080, true, false)]
    [InlineData(853, 480, false, true)]
    public void ActivePilotingAlwaysHasReachableControlsWithoutCoveringTheInstruments(float width, float height, bool nav, bool radar)
    {
        var layout = PFViewUiLayoutCore.Compute(width, height, true, new(nav, radar, !nav), piloting: true, alertVisible: true);
        var pilot = layout.PilotPanel;
        Assert.True(pilot.Width >= 220);
        Assert.Equal(54f, pilot.Height);
        foreach (var obstacle in new[] { layout.Toolbar, layout.ObjectList, layout.Details, layout.Navball,
                     layout.Radar, layout.Status, layout.SteeringStrip, layout.FlightControls, layout.Alert, layout.ToastArea }) Assert.False(pilot.Overlaps(obstacle));
        Assert.InRange(pilot.Y, height - 32f - 88f, height - 32f - pilot.Height);
        Assert.True(pilot.X + pilot.Width <= layout.FlightControls.X);
        Assert.InRange(pilot.X, 0, width - pilot.Width);
        Assert.InRange(pilot.Y, 0, height - pilot.Height);
    }
}
