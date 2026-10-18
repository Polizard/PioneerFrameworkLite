using PioneerFramework.Landing;
using Verse;
using Xunit;

namespace PioneerFramework.Tests;
public class LandingDestinationTests
{
    [Fact]
    public void RequiredMapSizeLeavesTwentyCellsAndMinimum250()
    { Assert.Equal(new IntVec2(320, 250), LandingDestinationService.RequiredMapSize(new IntVec2(300, 120))); }
    [Theory]
    [InlineData(true, false, true)] [InlineData(false, false, false)] [InlineData(true, true, false)]
    public void DestinationPolicyRejectsForeignAndQuestMaps(bool friendly, bool quest, bool expected)
    { Assert.Equal(expected, LandingDestinationService.IsAllowed(friendly, quest, true, true)); }
    [Fact]
    public void ExistingDestinationNeverInvokesCreate()
    {
        object map = new object(); int creates = 0;
        Assert.Same(map, LandingDestinationService.ResolveExisting(() => map, () => { creates++; return new object(); }));
        Assert.Equal(0, creates);
    }
}
