using PioneerFramework;
using PioneerFramework.Landing;
using Xunit;

namespace PioneerFramework.Tests;

public class LandingSourceMapTests
{
    [Fact]
    public void ArchivedEntityIsNeverMapless()
    {
        var archived = new SpaceEntity(7) { MapId = 42, HasMap = false, IsMapArchived = true };
        LandingSourceMapService.Describe(new[] { archived }, out var maps, out var mapless);
        Assert.Single(maps); Assert.Equal(42, maps[0].MapId); Assert.Empty(mapless);
    }
    [Fact]
    public void SharedMapCollectedOnce()
    {
        LandingSourceMapService.Describe(new[] { new SpaceEntity(7) { MapId = 42 }, new SpaceEntity(8) { MapId = 42 }, new SpaceEntity(9) }, out var maps, out var mapless);
        Assert.Single(maps); Assert.Equal(new[] { 7, 8 }, maps[0].EntityIds); Assert.Equal(new[] { 9 }, mapless);
    }
}
