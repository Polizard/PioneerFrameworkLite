using System;
using System.Collections.Generic;
using System.Diagnostics;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class FrameBudgetSchedulerTests
{
    [Fact]
    public void EmptyQueuePumpDoesNothing()
    {
        // 钉住：空队列时 Pump 不做任何事、不计完成 —— 否则每帧空转也会把 CompletedCount 抬上去，诊断失真。
        var scheduler = new FrameBudgetScheduler();
        Assert.False(scheduler.Pump(out int ran, out int completed));
        Assert.Equal(0, ran);
        Assert.Equal(0, completed);
        Assert.Equal(0, scheduler.PendingCount);
        Assert.Equal(0L, scheduler.CompletedCount);
    }

    [Fact]
    public void CompletedTaskIsRemovedAndCountedOnce()
    {
        // 钉住：step 返回 true = 完成 ⇒ 只算一次完成、立刻出队；再 Pump 不会再调它。
        var scheduler = new FrameBudgetScheduler();
        int calls = 0;
        scheduler.Enqueue("a", () => { calls++; return true; });
        Assert.Equal(1, scheduler.PendingCount);

        Assert.False(scheduler.Pump(out int ran, out int completed));

        Assert.Equal(1, ran);
        Assert.Equal(1, completed);
        Assert.Equal(1, calls);
        Assert.Equal(0, scheduler.PendingCount);
        Assert.Equal(1L, scheduler.CompletedCount);

        Assert.False(scheduler.Pump(out ran, out completed));
        Assert.Equal(0, ran);
        Assert.Equal(1, calls);
        Assert.Equal(1L, scheduler.CompletedCount);
    }

    [Fact]
    public void TasksRunInInsertionOrder()
    {
        // 钉住：同一帧内按插入顺序推进（参考模组按天体逐个 Advance 的顺序感；顺序乱了会出现"先画山再画地表"）。
        var scheduler = new FrameBudgetScheduler();
        var order = new List<string>();
        scheduler.Enqueue("a", () => { order.Add("a"); return false; });
        scheduler.Enqueue("b", () => { order.Add("b"); return false; });
        scheduler.Enqueue("c", () => { order.Add("c"); return false; });

        Assert.True(scheduler.Pump(out int ran, out int completed));

        Assert.Equal(new[] { "a", "b", "c" }, order);
        Assert.Equal(3, ran);
        Assert.Equal(0, completed);
        Assert.Equal(3, scheduler.PendingCount);
    }

    [Fact]
    public void EachTaskRunsAtMostOncePerPump()
    {
        // 钉住：一次 Pump 里每个任务只跑一步（Step 语义）。若不限制，宽预算会把一个任务在同一帧里反复推进，
        // 表现成"某一帧突然长出一大块"，等于没分帧。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 1e9);
        var calls = new int[3];
        scheduler.Enqueue("a", () => { calls[0]++; return false; });
        scheduler.Enqueue("b", () => { calls[1]++; return false; });
        scheduler.Enqueue("c", () => { calls[2]++; return false; });

        scheduler.Pump(out int ran, out _);

        Assert.Equal(3, ran);
        Assert.Equal(new[] { 1, 1, 1 }, calls);
    }

    [Fact]
    public void ZeroBudgetStillRunsExactlyOneTask()
    {
        // 钉住"至少跑一个"：预算 0 时不做事会让加载永远停在第 0 格（参考模组 do-while 也是先推进再判预算）。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 0.0);
        var order = new List<string>();
        scheduler.Enqueue("a", () => { order.Add("a"); return false; });
        scheduler.Enqueue("b", () => { order.Add("b"); return false; });
        scheduler.Enqueue("c", () => { order.Add("c"); return false; });

        scheduler.Pump(out int ran, out _);

        Assert.Equal(1, ran);
        Assert.Equal(new[] { "a" }, order);
    }

    [Fact]
    public void SlowTaskStopsTheFrameOnceBudgetIsConsumed()
    {
        // 钉住"累计耗时超预算即停"：慢任务吃掉的毫秒数必须算进本帧预算，否则 4 ms 上限形同虚设。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 1.0);
        bool secondRan = false;
        scheduler.Enqueue("slow", () =>
        {
            var spin = Stopwatch.StartNew();
            while (spin.Elapsed.TotalMilliseconds < 3.0) { }
            return false;
        });
        scheduler.Enqueue("next", () => { secondRan = true; return false; });

        scheduler.Pump(out int ran, out _);

        Assert.Equal(1, ran);
        Assert.False(secondRan);
    }

    [Fact]
    public void FaultedTaskIsRemovedRecordedAndDoesNotThrow()
    {
        // 钉住异常纪律：坏任务只炸一次（不是每帧炸），Pump 不向上抛，且未被算作完成 ——
        // 否则渲染路径会被一个坏缓存每帧打断，或者统计里"完成数"虚高掩盖故障。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 1e9);
        int faultCalls = 0;
        int goodCalls = 0;
        scheduler.Enqueue("bad", () => { faultCalls++; throw new InvalidOperationException("boom"); });
        scheduler.Enqueue("good", () => { goodCalls++; return false; });

        scheduler.Pump(out int ran, out int completed);

        Assert.Equal(2, ran);          // 抛异常那次确实执行了，计入 ran
        Assert.Equal(0, completed);    // 但绝不算完成
        Assert.Equal(0L, scheduler.CompletedCount);
        Assert.Equal(1, scheduler.PendingCount);
        Assert.Equal("bad", scheduler.LastFaultKey);
        Assert.Equal("boom", scheduler.LastFaultMessage);
        Assert.Equal(1, goodCalls);

        // 第二次 Pump：坏任务已出队，不会再抛/再刷日志。
        scheduler.Pump(out ran, out _);
        Assert.Equal(1, ran);
        Assert.Equal(1, faultCalls);
        Assert.Equal(2, goodCalls);
        Assert.Equal("bad", scheduler.LastFaultKey);   // 诊断保留，便于事后定位
    }

    [Fact]
    public void OnlyFaultedTaskLeavesQueueEmpty()
    {
        // 钉住：故障任务出队后队列真的空了（Pump 返回 false），调用方才能停掉分帧状态。
        var scheduler = new FrameBudgetScheduler();
        scheduler.Enqueue("only", () => throw new ArgumentException("nope"));

        Assert.False(scheduler.Pump(out int ran, out int completed));

        Assert.Equal(1, ran);
        Assert.Equal(0, completed);
        Assert.Equal(0, scheduler.PendingCount);
        Assert.Equal("only", scheduler.LastFaultKey);
        Assert.Equal("nope", scheduler.LastFaultMessage);
    }

    [Fact]
    public void RoundRobinStartsEachFrameAtTheTaskAfterTheLastOne()
    {
        // 钉住反饥饿：预算被单个任务吃满时，下一帧必须从下一个任务开始（round-robin 游标）。
        // 不这么做的话，队首任务永远霸占预算，后面的任务永远轮不到（加载永远停在第一格）。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 0.0);
        var order = new List<string>();
        scheduler.Enqueue("a", () => { order.Add("a"); return false; });
        scheduler.Enqueue("b", () => { order.Add("b"); return false; });
        scheduler.Enqueue("c", () => { order.Add("c"); return false; });

        for (int frame = 0; frame < 6; frame++) scheduler.Pump(out _, out _);

        Assert.Equal(new[] { "a", "b", "c", "a", "b", "c" }, order);
    }

    [Fact]
    public void RoundRobinSkipsCompletedTasks()
    {
        // 钉住：完成任务被摘掉后游标不跳格、也不复用空格（b 完成后下一帧应轮到 c，不是又回 a）。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 0.0);
        var order = new List<string>();
        scheduler.Enqueue("a", () => { order.Add("a"); return false; });
        scheduler.Enqueue("b", () => { order.Add("b"); return true; });
        scheduler.Enqueue("c", () => { order.Add("c"); return false; });

        for (int frame = 0; frame < 4; frame++) scheduler.Pump(out _, out _);

        Assert.Equal(new[] { "a", "b", "c", "a" }, order);
        Assert.Equal(2, scheduler.PendingCount);
        Assert.Equal(1L, scheduler.CompletedCount);
    }

    [Fact]
    public void SameKeyReplacesStepAndKeepsItsPosition()
    {
        // 钉住"同 key 覆盖保留原位置"：只换委托不换顺序。若挪到队尾，同一批任务的分帧顺序会每帧抖动。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 1e9);
        var order = new List<string>();
        scheduler.Enqueue("first", () => { order.Add("old"); return false; });
        scheduler.Enqueue("second", () => { order.Add("second"); return false; });

        scheduler.Enqueue("first", () => { order.Add("new"); return false; });

        Assert.Equal(2, scheduler.PendingCount);
        scheduler.Pump(out _, out _);
        Assert.Equal(new[] { "new", "second" }, order);
    }

    [Fact]
    public void RemoveDropsOnlyThatTask()
    {
        // 钉住 Remove 的判据：存在返回 true、不存在返回 false；只摘掉指定任务。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 1e9);
        var order = new List<string>();
        scheduler.Enqueue("a", () => { order.Add("a"); return false; });
        scheduler.Enqueue("b", () => { order.Add("b"); return false; });
        scheduler.Enqueue("c", () => { order.Add("c"); return false; });

        Assert.True(scheduler.Remove("b"));
        Assert.False(scheduler.Remove("b"));
        Assert.False(scheduler.Remove("missing"));

        scheduler.Pump(out _, out _);
        Assert.Equal(new[] { "a", "c" }, order);
        Assert.Equal(2, scheduler.PendingCount);
    }

    [Fact]
    public void RemoveBeforeCursorDoesNotSkipTheNextTask()
    {
        // 钉住游标修正：摘掉"游标之前"的任务会让后面的元素整体前移；若不同步前移游标，
        // 下一帧会跳过本该先跑的任务（漏推进一帧 = 该格永远停在旧状态）。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 0.0);
        var order = new List<string>();
        scheduler.Enqueue("a", () => { order.Add("a"); return false; });
        scheduler.Enqueue("b", () => { order.Add("b"); return false; });
        scheduler.Enqueue("c", () => { order.Add("c"); return false; });

        scheduler.Pump(out _, out _);      // 跑 a，游标 -> b
        Assert.True(scheduler.Remove("a")); // a 在游标之前
        scheduler.Pump(out _, out _);      // 应该跑 b，不是 c

        Assert.Equal(new[] { "a", "b" }, order);
    }

    [Fact]
    public void ClearEmptiesQueueAndKeepsCumulativeDiagnostics()
    {
        // 钉住 Clear 的边界：只清队列与游标；CompletedCount / LastFault 是累积诊断，不清空
        // （清掉会让"这一整轮到底坏了几个任务"没法事后复盘）。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 1e9);
        scheduler.Enqueue("done", () => true);
        scheduler.Pump(out _, out _);
        Assert.Equal(1L, scheduler.CompletedCount);

        scheduler.Enqueue("fault", () => throw new InvalidOperationException("kaboom"));
        scheduler.Pump(out _, out _);
        Assert.Equal("fault", scheduler.LastFaultKey);

        scheduler.Enqueue("pending", () => false);
        scheduler.Clear();

        Assert.Equal(0, scheduler.PendingCount);
        Assert.False(scheduler.Pump(out int ran, out int completed));
        Assert.Equal(0, ran);
        Assert.Equal(0, completed);
        Assert.Equal(1L, scheduler.CompletedCount);
        Assert.Equal("fault", scheduler.LastFaultKey);
    }

    [Fact]
    public void BudgetPropertyClampsInvalidValues()
    {
        // 钉住预算属性的退化口径（热路径上不抛）：负/NaN -> 0，+∞ -> 不限，正常值原样保留。
        Assert.Equal(4.0, new FrameBudgetScheduler().BudgetMilliseconds);   // 默认 4 ms
        Assert.Equal(0.0, new FrameBudgetScheduler(-5.0).BudgetMilliseconds);
        Assert.Equal(0.0, new FrameBudgetScheduler(double.NaN).BudgetMilliseconds);
        Assert.Equal(double.MaxValue, new FrameBudgetScheduler(double.PositiveInfinity).BudgetMilliseconds);

        var scheduler = new FrameBudgetScheduler();
        scheduler.BudgetMilliseconds = double.NegativeInfinity;
        Assert.Equal(0.0, scheduler.BudgetMilliseconds);
        scheduler.BudgetMilliseconds = 8.0;
        Assert.Equal(8.0, scheduler.BudgetMilliseconds);
    }

    [Fact]
    public void PumpReturnValueTracksPendingWork()
    {
        // 钉住 Pump 返回值 = "还要不要继续分帧"：完成一个但还有 pending 时返回 true，全清后返回 false。
        // 预算 0 保证这一帧只完成 first，才能把 true/false 两种返回都钉到。
        var scheduler = new FrameBudgetScheduler(budgetMilliseconds: 0.0);
        bool finished = false;
        scheduler.Enqueue("first", () => true);
        scheduler.Enqueue("second", () => { finished = true; return true; });

        Assert.True(scheduler.Pump(out int ran, out int completed));
        Assert.Equal(1, ran);
        Assert.Equal(1, completed);
        Assert.False(finished);
        Assert.Equal(1, scheduler.PendingCount);

        Assert.False(scheduler.Pump(out ran, out completed));
        Assert.Equal(1, ran);
        Assert.Equal(1, completed);
        Assert.True(finished);
        Assert.Equal(0, scheduler.PendingCount);
        Assert.Equal(2L, scheduler.CompletedCount);
        Assert.False(scheduler.Pump(out _, out _));
    }
}
