using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PFViewUiDragStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingDuringDragThenReopeningCannotKeepDragOrProduceClick(bool pan)
    {
        var state = new PFViewUiDragState();
        state.Begin(pan);
        state.MarkMoved();
        state.Reset();
        Assert.False(state.IsActive);
        Assert.False(state.End());
        state.Begin(false);
        Assert.True(state.End());
        Assert.False(state.IsActive);
    }

    [Fact]
    public void RotationDragAndPanNeverBecomeContextClicks()
    {
        var state = new PFViewUiDragState();
        state.Begin(false);
        state.MarkMoved();
        Assert.False(state.End());
        state.Begin(true);
        Assert.False(state.End());
    }
}
