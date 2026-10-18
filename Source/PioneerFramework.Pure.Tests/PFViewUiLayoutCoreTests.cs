using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewUiLayoutCoreTests
{
    public static IEnumerable<object[]> SupportedViews()
    {
        int configuration = 0;
        foreach (var size in new[] { (1280f,720f), (1024f,576f), (1280f/1.5f,480f),
                     (1920f,1080f), (1536f,864f), (1280f,720f) })
        {
            for (int flags = 0; flags < 16; flags++)
                yield return new object[] { size.Item1, size.Item2, flags, configuration };
            configuration++;
        }
    }

    [Theory]
    [MemberData(nameof(SupportedViews))]
    public void SupportedScaleCombinationsKeepControlsInsideDisjointRegions(float width, float height, int flags, int configuration)
    {
        bool nav = (flags & 1) != 0, radar = (flags & 2) != 0, details = (flags & 4) != 0;
        bool strip = !nav && (flags & 8) != 0;
        var layout = PFViewUiLayoutCore.Compute(width, height, details, new(nav, radar, strip));
        var rects = new[] { layout.Toolbar, layout.ObjectList, layout.Details, layout.Radar,
            layout.Navball, layout.Status, layout.SteeringStrip };
        for (int i = 0; i < rects.Length; i++)
        {
            var rect = rects[i];
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            Assert.InRange(rect.X, 0, width);
            Assert.InRange(rect.Y, 0, height);
            Assert.True(rect.X + rect.Width <= width, $"Configuration {configuration}: horizontal bounds");
            Assert.True(rect.Y + rect.Height <= height);
            for (int j = i + 1; j < rects.Length; j++) Assert.False(rect.Overlaps(rects[j]));
        }
        if (nav) Assert.Equal(222f, layout.Navball.Height);
        else Assert.Equal(0, layout.Navball.Width);
        if (!radar) Assert.Equal(0, layout.Radar.Width);
        Assert.False(layout.RequiresInstrumentDrawer);
    }

    [Fact]
    public void EmptyRegionsNeverCaptureInput()
    {
        var layout = PFViewUiLayoutCore.Compute(1280, 720, false, new(false, false, false));
        Assert.False(layout.Navball.Contains(0, 0));
        Assert.False(layout.Details.Overlaps(layout.ObjectList));
        Assert.True(layout.ObjectList.Contains(12, 100));
    }

    [Fact]
    public void CompactInspectionReusesSidebarWithoutChangingOtherAnchors()
    {
        var list = PFViewUiLayoutCore.Compute(853, 480, false, new(true, true, false));
        var details = PFViewUiLayoutCore.Compute(853, 480, true, new(true, true, false));
        Assert.True(list.Compact);
        Assert.Equal(list.ObjectList, details.Details);
        Assert.Equal(0, details.ObjectList.Width);
        Assert.Equal(list.Navball, details.Navball);
        Assert.Equal(list.Radar, details.Radar);
    }

    [Fact]
    public void VerySmallViewsOfferInstrumentDrawerRatherThanShrinkingBall()
    {
        var layout = PFViewUiLayoutCore.Compute(640, 360, true, new(true, true, false));
        Assert.True(layout.RequiresInstrumentDrawer);
        Assert.Equal(0, layout.Navball.Width);
        Assert.Equal(0, layout.Radar.Width);
    }

    [Fact]
    public void LabelsAvoidDetailsAndInstrumentsWithoutMovingWorldPoints()
    {
        var layout = PFViewUiLayoutCore.Compute(1280, 720, true, new(true, true, false));
        var desired = new PFViewUiRect(1030, 60, 100, 20);
        var label = PFViewUiLayoutCore.PlaceLabel(desired, 1280, 720, layout);
        Assert.True(label.Width > 0);
        Assert.False(label.Overlaps(layout.Details));
        Assert.False(label.Overlaps(layout.Radar));
        Assert.False(label.Overlaps(layout.Navball));
        Assert.InRange(label.X, 0, 1180);
        Assert.InRange(label.Y, 0, 700);
        Assert.Equal(new PFViewUiRect(1030, 60, 100, 20), desired);
    }
}
