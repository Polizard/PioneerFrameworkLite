using PioneerFramework.Landing;
using RimWorld.Planet;
using Xunit;
namespace PioneerFramework.Tests;
public class LandingTargetSessionTests
{
    [Theory]
    [InlineData(true, false, false, LandingTargetAction.PrepareMaps)]
    [InlineData(true, false, true, LandingTargetAction.PrepareMaps)]
    [InlineData(true, true, true, LandingTargetAction.PrepareMaps)]
    [InlineData(false, false, true, LandingTargetAction.None)]
    [InlineData(false, true, false, LandingTargetAction.None)]
    [InlineData(false, true, true, LandingTargetAction.BeginDescent)]
    public void ArchivedSourcesCanBePreparedBeforeFuelQuote(bool archived, bool validQuote, bool selected, LandingTargetAction expected)
    {
        var session = new LandingTargetSession(1);
        if (selected) session.SelectTile(new PlanetTile(42, 7));
        Assert.Equal(expected, session.GetAction(archived, validQuote));
        session.Close();
        Assert.Equal(LandingTargetAction.None, session.GetAction(archived, validQuote));
    }

    [Fact]
    public void SwitchingBodyInvalidatesTileAndOldCallbacks()
    {
        var session = new LandingTargetSession(1); session.SelectTile(new PlanetTile(42, 7));
        int token = session.Token; session.SelectBody(2);
        Assert.False(session.HasSelection); Assert.NotEqual(token, session.Token);
    }
    [Fact]
    public void ClosingInvalidatesPreparedCallback()
    {
        var session = new LandingTargetSession(1); int token = session.Token;
        session.Close(); Assert.False(session.Accepts(token));
    }

    [Fact]
    public void ChangingTileOnSameBodyInvalidatesPreparedCallback()
    {
        var session = new LandingTargetSession(1);
        session.SelectTile(new PlanetTile(42, 7));
        int prepared = session.Token;
        session.SelectTile(new PlanetTile(43, 7));
        Assert.False(session.Accepts(prepared));
        Assert.True(session.HasSelection);
        Assert.Equal(new PlanetTile(43, 7), session.Tile);
    }

    [Fact]
    public void ClearingTileInvalidatesPreparedCallback()
    {
        var session = new LandingTargetSession(1);
        session.SelectTile(new PlanetTile(42, 7));
        int prepared = session.Token;
        session.SelectTile(PlanetTile.Invalid);
        Assert.False(session.Accepts(prepared));
        Assert.False(session.HasSelection);
    }

    [Fact]
    public void ReselectingSameTileKeepsPreparedCallbackValid()
    {
        var session = new LandingTargetSession(1);
        session.SelectTile(new PlanetTile(42, 7));
        int prepared = session.Token;
        session.SelectTile(new PlanetTile(42, 7));
        Assert.True(session.Accepts(prepared));
    }
}
