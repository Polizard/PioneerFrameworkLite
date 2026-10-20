using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewUiViewHistoryTests
{
    private sealed record View(string Game, int Group);

    [Fact]
    public void OnlyMostRecentViewCanBeRestoredOnce()
    {
        var history = new PFViewUiViewHistory<View>();
        history.Remember(new("game", 3));
        history.Remember(new("game", 8));
        Assert.True(history.TryTake(v => v.Game == "game", out var result));
        Assert.Equal(8, result.Group);
        Assert.False(history.TryTake(_ => true, out _));
    }

    [Fact]
    public void InvalidWorldOrObjectNeverReturnsOldSnapshot()
    {
        var history = new PFViewUiViewHistory<View>();
        history.Remember(new("old-game", 3));
        Assert.False(history.TryTake(v => v.Game == "new-game", out var result));
        Assert.Null(result);
        Assert.False(history.HasValue);
    }

    [Fact]
    public void WorldResetClearsPreviousView()
    {
        var history = new PFViewUiViewHistory<View>();
        history.Remember(new("game", 3));
        history.Clear();
        Assert.False(history.TryTake(_ => true, out _));
    }
}
