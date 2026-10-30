using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class PhysicsTimeLineTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    public void RateChangesSplitTheActualIntervalRegardlessOfTickParity(int start)
    {
        var clock = new PhysicsTimeLine(start, 100, TimeScaleLevel.Normal);
        clock.SetRate(start + 1, TimeScaleLevel.Combat);
        var first = clock.NextStep(start + 2)!.Value;
        Assert.Equal(100, first.StartTick);
        Assert.Equal(101, first.EndTick);
        Assert.Equal(1, first.Duration);
        clock.Commit(first);
        var second = clock.NextStep(start + 2)!.Value;
        Assert.Equal(101, second.StartTick);
        Assert.Equal(101 + 1.0 / 60.0, second.EndTick);
        Assert.Equal(clock.At(start + 2) - 100, first.Duration + second.Duration);
        clock.Commit(second);
        Assert.Null(clock.NextStep(start + 2));
    }

    [Fact]
    public void ChangingRateAtOneTickDoesNotRewriteEarlierTime()
    {
        var clock = new PhysicsTimeLine(100, 100, TimeScaleLevel.Fast);
        clock.SetRate(101, TimeScaleLevel.Combat);
        Assert.Equal(104, clock.At(100.5));
        Assert.Equal(108, clock.At(101));
        Assert.Equal(108 + 1.0 / 120.0, clock.At(101.5));
        clock.SetRate(101, TimeScaleLevel.Normal);
        Assert.Equal(104, clock.At(100.5));
        Assert.Equal(108.5, clock.At(101.5));
    }

    [Fact]
    public void RestoredPendingIntervalsResumeAtTheCommittedState()
    {
        var clock = new PhysicsTimeLine(100, 800, TimeScaleLevel.Normal);
        clock.SetRate(101, TimeScaleLevel.Combat);
        var restored = new PhysicsTimeLine(clock.Epochs, clock.CommittedGameTick, clock.CommittedPhysicsTick);
        Assert.Equal(800, restored.NextStep(102)!.Value.StartTick);
        var first = restored.NextStep(102)!.Value;
        restored.Commit(first);
        Assert.Equal(801, restored.NextStep(102)!.Value.StartTick);
        Assert.Equal(801 + 1.0 / 60.0, restored.At(102));
    }

    [Fact]
    public void LongRunningClockRetainsBoundedHistoryForRendering()
    {
        var clock = new PhysicsTimeLine(0, 0, TimeScaleLevel.Normal);
        for (int tick = 1; tick <= 1000; tick++)
        {
            clock.SetRate(tick, tick % 2 == 0 ? TimeScaleLevel.Fast : TimeScaleLevel.Normal);
            clock.Commit(clock.NextStep(tick)!.Value);
        }
        Assert.InRange(clock.Epochs.Count, 1, 4);
        Assert.True(clock.At(999.5) <= clock.At(1000));
    }
}
