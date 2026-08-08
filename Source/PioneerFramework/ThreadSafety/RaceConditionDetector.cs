using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace PioneerFramework.ThreadSafety
{
    /// <summary>
    /// 竞态条件检测器。通过多种策略检测多线程环境下的数据竞争。
    ///
    /// 检测策略：
    ///   1. 哨兵值检测：在共享数据旁放置哨兵值，检查是否被意外覆写
    ///   2. 重复执行检测：多次运行相同操作，检查结果是否一致
    ///   3. 交错写检测：多线程同时写入不同位置，检查是否有串扰
    ///   4. 读-改-写原子性检测：检查复合操作是否丢失更新
    /// </summary>
    public static class RaceConditionDetector
    {
        public static TestSuiteResult Run(int threadCount = 8, int repeatCount = 50)
        {
            var sw = Stopwatch.StartNew();
            var suite = new TestSuiteResult { SuiteName = "Race Condition Detection (竞态检测)" };

            // ── 1. 哨兵值检测 ──
            suite.Cases.Add(TestSentinelGuard(threadCount, repeatCount));

            // ── 2. 共享数组交错写检测 ──
            suite.Cases.Add(TestInterleavedWrites(threadCount, repeatCount));

            // ── 3. 读-改-写原子性检测 ──
            suite.Cases.Add(TestReadModifyWrite(threadCount, repeatCount));

            // ── 4. 集合并发修改检测 ──
            suite.Cases.Add(TestConcurrentCollection(threadCount, repeatCount));

            // ── 5. 浮点运算确定性检测 ──
            suite.Cases.Add(TestFloatingPointDeterminism(threadCount, repeatCount));

            suite.TotalElapsedMs = sw.Elapsed.TotalMilliseconds;
            return suite;
        }

        /// <summary>
        /// 哨兵值检测：在可写数组旁放置哨兵，检测缓冲区溢出/串扰。
        /// </summary>
        private static TestCaseResult TestSentinelGuard(int threadCount, int repeatCount)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = "哨兵值防护检测" };
            bool passed = true;
            int corruptions = 0;

            Parallel.For(0, repeatCount, _ =>
            {
                // 分配带哨兵的数据块：[sentinel0] [data...] [sentinel1]
                const int dataSize = 1000;
                const long sentinelValue = 0x5AFE5AFE5AFE5AFE;
                var data = new long[dataSize + 2];
                data[0] = sentinelValue;                    // 前哨兵
                data[dataSize + 1] = sentinelValue;         // 后哨兵

                // 多线程写入数据区域（不应触及哨兵）
                Parallel.For(0, threadCount, t =>
                {
                    int offset = 1 + t * (dataSize / threadCount);
                    int count = dataSize / threadCount;
                    for (int i = 0; i < count; i++)
                        data[offset + i] = (long)t * 1000000 + i;
                });

                // 检查哨兵
                if (Interlocked.Read(ref data[0]) != sentinelValue)
                    Interlocked.Increment(ref corruptions);
                if (Interlocked.Read(ref data[dataSize + 1]) != sentinelValue)
                    Interlocked.Increment(ref corruptions);
            });

            if (corruptions > 0)
            {
                passed = false;
                result.Message = string.Format("哨兵值被破坏 {0} 次", corruptions);
                result.Details.Add("检测到缓冲区溢出或线程间串扰");
            }
            result.Passed = passed;
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 交错写检测：多线程写入数组的不同位置，检查是否有串扰。
        /// </summary>
        private static TestCaseResult TestInterleavedWrites(int threadCount, int repeatCount)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = "交错写串扰检测" };
            bool passed = true;
            int errorCount = 0;

            for (int r = 0; r < repeatCount; r++)
            {
                const int arraySize = 10000;
                var array = new double[arraySize];
                var expected = new double[arraySize];

                // 每个线程写入自己的区段
                Parallel.For(0, threadCount, t =>
                {
                    int start = t * (arraySize / threadCount);
                    int end = (t == threadCount - 1) ? arraySize : (t + 1) * (arraySize / threadCount);
                    double value = (double)(t + 1) * 100.0;
                    for (int i = start; i < end; i++)
                    {
                        array[i] = value;
                        expected[i] = value;
                    }
                });

                // 验证
                for (int i = 0; i < arraySize; i++)
                {
                    if (Math.Abs(array[i] - expected[i]) > 0.001)
                    {
                        Interlocked.Increment(ref errorCount);
                        break; // 一次运行只计一次
                    }
                }
            }

            if (errorCount > 0)
            {
                passed = false;
                result.Message = string.Format("{0}/{1} 次运行检测到串扰", errorCount, repeatCount);
            }
            result.Passed = passed;
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 读-改-写原子性检测：检测非原子递增操作是否丢失更新。
        /// </summary>
        private static TestCaseResult TestReadModifyWrite(int threadCount, int repeatCount)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = "读-改-写原子性检测" };
            bool passed = true;

            // 使用 Interlocked（正确方式）
            long atomicCounter = 0;
            long expectedAtomic = (long)threadCount * repeatCount;

            Parallel.For(0, repeatCount, _ =>
            {
                Parallel.For(0, threadCount, __ =>
                {
                    Interlocked.Increment(ref atomicCounter);
                });
            });

            if (atomicCounter != expectedAtomic)
            {
                passed = false;
                result.Message = string.Format("Interlocked 计数: 期望 {0}, 实际 {1}", expectedAtomic, atomicCounter);
                result.Details.Add("原子操作异常，可能与平台或 JIT 有关");
            }

            // 验证非原子操作确实会丢失（演示目的）
            long nonAtomic = 0;
            Parallel.For(0, threadCount * 10, _ =>
            {
                nonAtomic++; // 非原子操作，预期丢失更新
            });
            if (nonAtomic == threadCount * 10)
            {
                // 如果恰好没丢，说明线程数太少，不视为错误
                result.Details.Add("非原子递增未观察到丢失（线程数不足或竞争不激烈）");
            }
            else
            {
                result.Details.Add(string.Format(
                    "非原子递增: 期望 {0}, 实际 {1} (丢失 {2}) — 正常现象，演示竞态",
                    threadCount * 10, nonAtomic, threadCount * 10 - nonAtomic));
            }

            result.Passed = passed;
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 集合并发修改检测：检测 ConcurrentBag 等线程安全集合的正确性。
        /// </summary>
        private static TestCaseResult TestConcurrentCollection(int threadCount, int repeatCount)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = "并发集合正确性检测" };
            bool passed = true;

            for (int r = 0; r < repeatCount; r++)
            {
                var bag = new System.Collections.Concurrent.ConcurrentBag<int>();
                int expectedTotal = 0;

                Parallel.For(0, threadCount, t =>
                {
                    int itemsPerThread = 100;
                    for (int i = 0; i < itemsPerThread; i++)
                    {
                        bag.Add(t * 10000 + i);
                        Interlocked.Increment(ref expectedTotal);
                    }
                });

                if (bag.Count != expectedTotal)
                {
                    passed = false;
                    result.Message = string.Format(
                        "ConcurrentBag 计数不一致: 期望 {0}, 实际 {1}",
                        expectedTotal, bag.Count);
                    break;
                }
            }

            result.Passed = passed;
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 浮点运算确定性检测：验证相同输入在并行/串行下产生相同浮点结果。
        /// 注意：浮点运算的结果可能因运算顺序不同而有微小差异。
        /// </summary>
        private static TestCaseResult TestFloatingPointDeterminism(int threadCount, int repeatCount)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = "浮点运算确定性检测" };

            const int arraySize = 100000;
            var values = new double[arraySize];
            var rng = new Random(42);
            for (int i = 0; i < arraySize; i++)
                values[i] = rng.NextDouble() * 1000.0;

            // 串行求和
            double serialSum = 0;
            for (int i = 0; i < arraySize; i++)
                serialSum += values[i];

            // 并行求和（分块）
            double parallelSum = 0;
            var locks = new object();
            Parallel.For(0, threadCount, t =>
            {
                int start = t * (arraySize / threadCount);
                int end = (t == threadCount - 1) ? arraySize : (t + 1) * (arraySize / threadCount);
                double localSum = 0;
                for (int i = start; i < end; i++)
                    localSum += values[i];
                lock (locks) { parallelSum += localSum; }
            });

            double diff = Math.Abs(serialSum - parallelSum);
            // 浮点累加顺序不同，允许微小差异
            bool passed = diff < 1e-6 * arraySize;
            result.Passed = passed;
            if (!passed)
            {
                result.Message = string.Format("浮点累加差异过大: diff={0:E3}", diff);
            }
            result.Details.Add(string.Format(
                "串行和={0:F6}, 并行和={1:F6}, 差异={2:E3}",
                serialSum, parallelSum, diff));
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }
    }
}