using System;
using PioneerFramework.Landing;
using RimWorld.Planet;
using Xunit;

namespace PioneerFramework.Tests;

public class LandingPresentationTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("  ")] [InlineData("\r\n")]
    public void NormalFlightWithoutFailureHasNoEmptyInspectLine(string failure)
    {
        Assert.Equal("降落阶段：InFlight · 11%", LandingWorldObjectPresentation.JoinInspectLines("降落阶段：InFlight · 11%", failure));
    }

    [Fact]
    public void RecoveryInspectWithoutBaseTextStartsWithItsContent()
    {
        Assert.Equal("等待回收：1", LandingWorldObjectPresentation.JoinInspectLines("", "等待回收：1"));
    }

    [Fact]
    public void MultilineFailureRetainsUsefulLinesWithoutBlankSeparators()
    {
        Assert.Equal("降落失败\n地图不可用\n请重试", LandingWorldObjectPresentation.JoinInspectLines("降落失败\n", "\r\n地图不可用\r\n\r\n请重试\n"));
    }

    [Theory]
    [InlineData(typeof(LandingTransitWorldObject))]
    [InlineData(typeof(LandedGroupWorldObject))]
    [InlineData(typeof(LandedShipMapParent))]
    public void LandingMarkersStayExpandedAtNearZoomAndRespectHiddenIconPreference(Type type)
    {
        Assert.Equal(1f, LandingWorldObjectPresentation.ExpandedTransition(type, true, false, 0f));
        Assert.Equal(1f, LandingWorldObjectPresentation.ExpandedTransition(type, true, false, 0.5f));
        Assert.Equal(0f, LandingWorldObjectPresentation.ExpandedTransition(type, true, true, 0f));
        Assert.Equal(0f, LandingWorldObjectPresentation.ExpandedTransition(type, false, false, 0f));
    }

    [Fact]
    public void OtherWorldObjectsKeepTheirNativeZoomTransition()
    {
        Assert.Equal(0.35f, LandingWorldObjectPresentation.ExpandedTransition(typeof(WorldObject), true, false, 0.35f));
    }
}
