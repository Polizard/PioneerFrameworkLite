using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 第 24 条缓存本体（`BodySampleCache`）的**并发不变式**测试。
    ///
    /// 为什么要有这个文件：缓存被 `SpaceGameComp` 的 `Parallel.For` 群组物理并发读写。
    /// 2026-09-14 游戏内实测到旧实现（`Dictionary&lt;int,(double tick, DVector3 v)&gt;`，32 字节值类型元组）
    /// 出现 3 次"命中 tick 逐位相同、值却差 5.3/15.8/31.5 m"（天体 2 = 月球）——因为值类型的字段是
    /// 逐个拷贝的，**写入不是原子操作**，读者会拿到"新 tick + 旧分量"的撕裂值。
    ///
    /// 不变式（本文件守的这条）：**命中 ⇒ 返回的值必定属于该 tick**（算式是纯函数）。
    /// 只要缓存守得住这条，多线程下就不可能给调用方一个错值——撕裂写破坏的正是这条。
    /// </summary>
    public class BodySampleCacheTests
    {
        private readonly ITestOutputHelper _out;

        public BodySampleCacheTests(ITestOutputHelper output) => _out = output;

        /// <summary>采样值模拟天体位置：只由 tick 决定（纯函数），相邻 tick 差异明显。</summary>
        private static DVector3 PosAt(double tick) => new DVector3(tick * 1000.0, -tick * 250.0, tick * 37.5);

        private static DVector3 VelAt(double tick) => new DVector3(tick * 3.0, tick * 5.0, -tick * 7.0);

        /// <summary>写者与读者共享的"当前 tick"（模拟一个物理步内所有群组查同一个 tick）。</summary>
        private sealed class Clock
        {
            public double Tick;
        }

        /// <summary>
        /// 多写者 + 多读者压同一批槽位：**任何一次命中都必须拿到该 tick 对应的值**。
        ///
        /// 形态刻意照搬真实热路径：写者把**同一个 tick** 连写多次才进入下一个 tick（一个物理步里
        /// 每个群组都会查一次），读者读共享时钟给出的 tick。这正是撕裂最容易发生的窗口
        /// ——读者在写者重写同一条目时读它。
        /// </summary>
        [Fact]
        public async Task ConcurrentWritersAndReaders_NeverReturnValueFromAnotherTick()
        {
            const int slots = 4;
            var cache = new BodySampleCache();
            cache.EnsureCapacity(slots);

            var clock = new Clock();
            using var cts = new CancellationTokenSource();
            long mismatches = 0;
            long hits = 0;

            var writers = Enumerable.Range(0, slots).Select(w => Task.Run(() =>
            {
                int slot = w;
                double tick = 0.0;
                while (!cts.IsCancellationRequested)
                {
                    clock.Tick = tick;
                    for (int rep = 0; rep < 64 && !cts.IsCancellationRequested; rep++)
                        cache.Store(slot, tick, PosAt(tick), VelAt(tick));
                    tick += 2.0;                     // 一个"物理步"推进 2 tick
                    if (tick > 20000.0) tick = 0.0;  // 循环复用 tick 值（也顺带覆盖"旧 tick 再次出现"）
                }
            })).ToArray();

            var readers = Enumerable.Range(0, slots).Select(r => Task.Run(() =>
            {
                int slot = r;
                while (!cts.IsCancellationRequested)
                {
                    double tick = clock.Tick;
                    if (!cache.TryGet(slot, tick, out var pos, out var vel)) continue;
                    Interlocked.Increment(ref hits);

                    // 命中即断言：值必须等于"该 tick 的纯函数值"
                    if ((pos - PosAt(tick)).magnitude > 0.0 || (vel - VelAt(tick)).magnitude > 0.0)
                        Interlocked.Increment(ref mismatches);
                }
            })).ToArray();

            await Task.Delay(1500);
            cts.Cancel();
            await Task.WhenAll(writers.Concat(readers));

            // 读数打出来：对照「同负载压旧实现（值类型元组 + Dictionary）」实测 656 万命中 / 1585 次撕裂
            _out.WriteLine($"BodySampleCache 压测：命中={hits} 撕裂={mismatches}");
            Assert.True(hits > 100_000, $"命中样本太少（{hits}），压测没起到作用");
            Assert.Equal(0, mismatches);
        }

        /// <summary>
        /// 单线程语义：未写过的槽位不命中；写了之后同 tick 命中、异 tick 不命中；
        /// `Reset` 后一律不命中（读档/重建天体表时靠它作废旧值）。
        /// </summary>
        [Fact]
        public void BasicSemantics_HitOnlyForExactTick()
        {
            var cache = new BodySampleCache();
            cache.EnsureCapacity(3);

            Assert.False(cache.TryGet(0, 10.0, out _, out _));

            cache.Store(0, 10.0, PosAt(10.0), VelAt(10.0));
            Assert.True(cache.TryGet(0, 10.0, out var pos, out var vel));
            Assert.Equal(0.0, (pos - PosAt(10.0)).magnitude);
            Assert.Equal(0.0, (vel - VelAt(10.0)).magnitude);

            // tick 必须**逐位相等**：差一个最小可表示量即视为未命中
            double justOff = Math.BitIncrement(10.0);
            Assert.NotEqual(10.0, justOff);
            Assert.False(cache.TryGet(0, justOff, out _, out _));

            // 槽位之间互不干扰
            Assert.False(cache.TryGet(1, 10.0, out _, out _));

            cache.Reset();
            Assert.False(cache.TryGet(0, 10.0, out _, out _));
        }

        /// <summary>槽位越界/未扩容时安全返回未命中（未登记天体走这条，不该抛）。</summary>
        [Fact]
        public void OutOfRangeSlot_IsMissNotThrow()
        {
            var cache = new BodySampleCache();
            Assert.False(cache.TryGet(-1, 1.0, out _, out _));
            Assert.False(cache.TryGet(9999, 1.0, out _, out _));
            cache.Store(9999, 1.0, PosAt(1.0), VelAt(1.0));   // 空转
            Assert.False(cache.TryGet(9999, 1.0, out _, out _));
        }
    }
}
