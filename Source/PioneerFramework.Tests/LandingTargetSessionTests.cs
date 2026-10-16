using PioneerFramework.Landing;
using RimWorld.Planet;
using Xunit;
namespace PioneerFramework.Tests;
public class LandingTargetSessionTests
{
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
}
