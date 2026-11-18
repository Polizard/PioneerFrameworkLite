using System;
using System.Collections.Generic;
using System.Threading;

namespace PioneerFramework.ThreadSafety
{
    /// <summary>
    /// 线程安全断言工具。提供并发安全的计数器、哨兵值检测等。
    /// </summary>
    public static class ThreadSafetyAssert
    {
        /// <summary>
        /// 断言两个 DVector3 在指定容差内相等。
        /// </summary>
        public static bool AreEqual(DVector3 a, DVector3 b, double tolerance = 1e-3)
        {
            return Math.Abs(a.x - b.x) <= tolerance
                && Math.Abs(a.y - b.y) <= tolerance
                && Math.Abs(a.z - b.z) <= tolerance;
        }

        /// <summary>
        /// 断言两个 double 在指定容差内相等。
        /// </summary>
        public static bool AreEqual(double a, double b, double tolerance = 1e-9)
        {
            return Math.Abs(a - b) <= tolerance;
        }

        /// <summary>
        /// 并发安全计数器。用于检测并行任务是否正确执行了预期次数。
        /// </summary>
        public class ConcurrentCounter
        {
            private int _count;
            public int Count => Volatile.Read(ref _count);

            public void Increment() => Interlocked.Increment(ref _count);
            public void Add(int value) => Interlocked.Add(ref _count, value);
            public void Reset() => Interlocked.Exchange(ref _count, 0);
        }

        /// <summary>
        /// 哨兵值检测器。用于检测数据竞争：
        /// 在所有线程写入之前设置一个已知的哨兵值，
        /// 写入后检查该值是否被意外修改。
        /// </summary>
        public class SentinelDetector
        {
            private long _sentinel;
            private readonly long _expected;
            private volatile bool _corrupted;

            public bool Corrupted => _corrupted;

            public SentinelDetector(long expected = 0xDEADBEEF)
            {
                _expected = expected;
                _sentinel = expected;
            }

            /// <summary>
            /// 检查哨兵值是否被破坏，并重置。
            /// </summary>
            public bool CheckAndReset()
            {
                long current = Interlocked.Read(ref _sentinel);
                if (current != _expected)
                {
                    _corrupted = true;
                    return false;
                }
                return true;
            }

            /// <summary>
            /// 原子地写入新值并检查旧值。
            /// </summary>
            public bool TryCorrupt(long newValue)
            {
                long old = Interlocked.Exchange(ref _sentinel, newValue);
                if (old != _expected)
                {
                    _corrupted = true;
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// 直方图收集器（线程安全）。用于检测并行执行中结果的一致性。
        /// </summary>
        public class ConcurrentHistogram
        {
            private readonly ConcurrentCounter[] _buckets;

            public ConcurrentHistogram(int bucketCount)
            {
                _buckets = new ConcurrentCounter[bucketCount];
                for (int i = 0; i < bucketCount; i++)
                    _buckets[i] = new ConcurrentCounter();
            }

            public void Record(int bucketIndex)
            {
                if (bucketIndex >= 0 && bucketIndex < _buckets.Length)
                    _buckets[bucketIndex].Increment();
            }

            public int[] GetDistribution()
            {
                var result = new int[_buckets.Length];
                for (int i = 0; i < _buckets.Length; i++)
                    result[i] = _buckets[i].Count;
                return result;
            }
        }

        /// <summary>
        /// 结果收集器：收集多个并行执行的结果，检查是否全部一致。
        /// </summary>
        public class ResultCollector<T>
        {
            private readonly List<T> _results = new List<T>();
            private readonly object _lock = new object();
            private readonly Func<T, T, bool> _comparer;

            public IReadOnlyList<T> Results
            {
                get { lock (_lock) return _results.ToArray(); }
            }

            public ResultCollector(Func<T, T, bool> comparer = null)
            {
                _comparer = comparer ?? EqualityComparer<T>.Default.Equals;
            }

            public void Add(T result)
            {
                lock (_lock) _results.Add(result);
            }

            /// <summary>
            /// 检查所有收集的结果是否一致。返回不一致的索引对列表。
            /// </summary>
            public List<(int, int)> FindInconsistencies()
            {
                var inconsistent = new List<(int, int)>();
                T[] snapshot;
                lock (_lock) snapshot = _results.ToArray();

                if (snapshot.Length < 2) return inconsistent;

                for (int i = 1; i < snapshot.Length; i++)
                {
                    if (!_comparer(snapshot[0], snapshot[i]))
                        inconsistent.Add((0, i));
                }
                return inconsistent;
            }

            public int Count
            {
                get { lock (_lock) return _results.Count; }
            }
        }
    }
}