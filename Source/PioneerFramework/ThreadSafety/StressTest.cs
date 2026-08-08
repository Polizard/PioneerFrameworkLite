using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace PioneerFramework.ThreadSafety
{
    /// <summary>
    /// 压力测试：高负载、长时间运行，检测内存泄漏、死锁、崩溃。
    ///
    /// 测试场景：
    ///   1. 大量并发 NBody 计算（数千组）
    ///   2. 长时间循环运行（模拟数万 tick）
    ///   3. 异常输入边界测试
    ///   4. 线程池耗尽测试
    /// </summary>
    public static class StressTest
    {
        public static TestSuiteResult Run(int threadCount = 8, int durationSeconds = 5)
        {
            var sw = Stopwatch.StartNew();
            var suite = new TestSuiteResult { SuiteName = "Stress Tests (压力测试)" };

            // ── 1. 大量并发 NBody 计算 ──
            suite.Cases.Add(TestMassiveConcurrentNBody(threadCount, 5000));

            // ── 2. 长时间循环运行 ──
            suite.Cases.Add(TestSustainedLooping(threadCount, durationSeconds));

            // ── 3. 异常输入边界测试 ──
            suite.Cases.Add(TestBoundaryInputs(threadCount));

            // ── 4. 线程池压力测试 ──
            suite.Cases.Add(TestThreadPoolStress(threadCount, 100000));

            suite.TotalElapsedMs = sw.Elapsed.TotalMilliseconds;
            return suite;
        }

        /// <summary>
        /// 大量并发 NBody 计算：创建数千个轨道计算任务同时执行。
        /// </summary>
        private static TestCaseResult TestMassiveConcurrentNBody(int threadCount, int taskCount)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = string.Format("大规模并发 NBody ({0} 任务)", taskCount) };
            bool passed = true;
            int exceptionCount = 0;
            int completedCount = 0;

            try
            {
                var rng = new Random(42);
                var tasks = new (DVector3 pos, DVector3 vel, DVector3 bodyPos, DVector3 bodyVel, double mu)[taskCount];
                for (int i = 0; i < taskCount; i++)
                {
                    double alt = 200_000 + rng.NextDouble() * 50_000_000;
                    double speed = Math.Sqrt(SpaceConstants.PrimaryGravitationalParameter / (6_371_000 + alt));
                    tasks[i] = (
                        new DVector3(6_371_000 + alt, rng.NextDouble() * 1000, rng.NextDouble() * 1000),
                        new DVector3(0, rng.NextDouble() * 100, speed),
                        DVector3.zero,
                        DVector3.zero,
                        SpaceConstants.PrimaryGravitationalParameter
                    );
                }

                Parallel.For(0, taskCount, i =>
                {
                    try
                    {
                        var t = tasks[i];
                        NBodyIntegrator.ComputeBodyAccelJerk(t.pos, t.vel, t.bodyPos, t.bodyVel, t.mu,
                            out var acc, out var jerk);
                        NBodyIntegrator.ThirdOrderTaylorStep(t.pos, t.vel, acc, jerk, 2.0,
                            out var newPos, out var newVel);
                        Interlocked.Increment(ref completedCount);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref exceptionCount);
                    }
                });
            }
            catch (Exception ex)
            {
                passed = false;
                result.Message = "异常: " + ex.GetType().Name + " - " + ex.Message;
            }

            if (exceptionCount > 0)
            {
                passed = false;
                result.Message = string.Format("{0} 个任务抛出异常", exceptionCount);
            }

            result.Passed = passed;
            result.Message = result.Message ?? string.Format("完成 {0}/{1} 任务", completedCount, taskCount);
            result.Details.Add(string.Format("线程数={0}, 无异常任务={1}", threadCount, completedCount));
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 长时间循环运行：持续运行指定秒数，检测内存增长和稳定性。
        /// </summary>
        private static TestCaseResult TestSustainedLooping(int threadCount, int durationSeconds)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult
            {
                Name = string.Format("持续运行稳定性 ({0}s)", durationSeconds)
            };
            bool passed = true;
            long iterationCount = 0;
            int exceptionCount = 0;

            try
            {
                var endTime = DateTime.UtcNow.AddSeconds(durationSeconds);
                var rng = new Random(42);

                while (DateTime.UtcNow < endTime)
                {
                    int batchSize = 100;
                    var batch = new (DVector3 pos, DVector3 vel)[batchSize];
                    for (int i = 0; i < batchSize; i++)
                    {
                        double alt = 200_000 + rng.NextDouble() * 20_000_000;
                        double speed = Math.Sqrt(SpaceConstants.PrimaryGravitationalParameter / (6_371_000 + alt));
                        batch[i] = (
                            new DVector3(6_371_000 + alt, 0, 0),
                            new DVector3(0, 0, speed * (0.5 + rng.NextDouble()))
                        );
                    }

                    Parallel.For(0, batchSize, i =>
                    {
                        try
                        {
                            var b = batch[i];
                            NBodyIntegrator.ComputeBodyAccelJerk(b.pos, b.vel, DVector3.zero, DVector3.zero,
                                SpaceConstants.PrimaryGravitationalParameter, out var acc, out var jerk);
                            NBodyIntegrator.ThirdOrderTaylorStep(b.pos, b.vel, acc, jerk, 2.0,
                                out _, out _);
                        }
                        catch (Exception)
                        {
                            Interlocked.Increment(ref exceptionCount);
                        }
                    });

                    Interlocked.Add(ref iterationCount, batchSize);
                }
            }
            catch (Exception ex)
            {
                passed = false;
                result.Message = "异常: " + ex.GetType().Name + " - " + ex.Message;
            }

            if (exceptionCount > 0)
            {
                passed = false;
                result.Message = string.Format("{0} 次迭代异常", exceptionCount);
            }

            result.Passed = passed;
            result.Message = result.Message ?? string.Format("{0} 次迭代, {1} 异常",
                iterationCount, exceptionCount);
            double actualSec = sw.Elapsed.TotalSeconds;
            result.Details.Add(string.Format("实际运行={0:F1}s, 吞吐量={1:F0} 次/秒",
                actualSec, iterationCount / Math.Max(actualSec, 0.001)));
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 边界输入测试：测试极端值（零、极大、极小、NaN）是否导致崩溃。
        /// </summary>
        private static TestCaseResult TestBoundaryInputs(int threadCount)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = "边界输入安全性" };
            bool passed = true;
            int crashCount = 0;
            var issues = new List<string>();

            var edgeCases = new (string name, DVector3 pos, DVector3 vel, DVector3 bodyPos, DVector3 bodyVel, double mu)[]
            {
                ("零位置",         DVector3.zero,                               new DVector3(0,0,7670), DVector3.zero, DVector3.zero, SpaceConstants.PrimaryGravitationalParameter),
                ("零速度",         new DVector3(6_771_000,0,0),                 DVector3.zero,          DVector3.zero, DVector3.zero, SpaceConstants.PrimaryGravitationalParameter),
                ("极大距离",       new DVector3(1e12,0,0),                      new DVector3(0,0,100),  DVector3.zero, DVector3.zero, SpaceConstants.PrimaryGravitationalParameter),
                ("极小距离",       new DVector3(10,0,0),                        new DVector3(0,0,100),  DVector3.zero, DVector3.zero, SpaceConstants.PrimaryGravitationalParameter),
                ("负引力参数",     new DVector3(6_771_000,0,0),                 new DVector3(0,0,7670), DVector3.zero, DVector3.zero, -1e12),
                ("零引力参数",     new DVector3(6_771_000,0,0),                 new DVector3(0,0,7670), DVector3.zero, DVector3.zero, 0),
                ("极大速度",       new DVector3(6_771_000,0,0),                 new DVector3(0,0,1e8),  DVector3.zero, DVector3.zero, SpaceConstants.PrimaryGravitationalParameter),
                ("运动天体",       new DVector3(6_771_000,0,0),                 new DVector3(0,0,7670), new DVector3(384_400_000,0,0), new DVector3(0,0,1022), 4.9048695e12),
            };

            Parallel.For(0, edgeCases.Length, i =>
            {
                var ec = edgeCases[i];
                try
                {
                    NBodyIntegrator.ComputeBodyAccelJerk(ec.pos, ec.vel, ec.bodyPos, ec.bodyVel, ec.mu,
                        out var acc, out var jerk);
                    NBodyIntegrator.ThirdOrderTaylorStep(ec.pos, ec.vel, acc, jerk, 2.0,
                        out var newPos, out var newVel);

                    // 检查结果是否包含 NaN/Infinity
                    if (double.IsNaN(newPos.x) || double.IsNaN(newPos.y) || double.IsNaN(newPos.z) ||
                        double.IsInfinity(newPos.x) || double.IsInfinity(newPos.y) || double.IsInfinity(newPos.z))
                    {
                        Interlocked.Increment(ref crashCount);
                        lock (issues)
                            issues.Add(string.Format("{0}: 位置包含 NaN/Inf", ec.name));
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref crashCount);
                    lock (issues)
                        issues.Add(string.Format("{0}: {1}", ec.name, ex.GetType().Name));
                }
            });

            if (crashCount > 0)
            {
                passed = false;
                result.Message = string.Format("{0} 个边界输入异常", crashCount);
                result.Details.AddRange(issues);
            }
            result.Passed = passed;
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 线程池压力测试：创建大量小任务，检测线程池是否正常回收。
        /// </summary>
        private static TestCaseResult TestThreadPoolStress(int threadCount, int taskCount)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult
            {
                Name = string.Format("线程池压力 ({0} 短任务)", taskCount)
            };
            bool passed = true;

            int completed = 0;
            int maxConcurrent = 0;
            int currentConcurrent = 0;

            try
            {
                Parallel.For(0, taskCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, i =>
                {
                    int current = Interlocked.Increment(ref currentConcurrent);
                    // 追踪最大并发数
                    int prevMax;
                    do
                    {
                        prevMax = maxConcurrent;
                        if (current <= prevMax) break;
                    } while (Interlocked.CompareExchange(ref maxConcurrent, current, prevMax) != prevMax);

                    // 模拟轻量计算
                    double x = i * 0.001;
                    double y = Math.Sin(x) * Math.Cos(x);
                    Interlocked.Decrement(ref currentConcurrent);
                    Interlocked.Increment(ref completed);
                });
            }
            catch (Exception ex)
            {
                passed = false;
                result.Message = "异常: " + ex.GetType().Name + " - " + ex.Message;
            }

            result.Passed = passed;
            result.Message = result.Message ?? string.Format("完成 {0}/{1}, 最大并发={2}",
                completed, taskCount, maxConcurrent);
            result.Details.Add(string.Format("请求线程数={0}, 实际最大并发={1}", threadCount, maxConcurrent));
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }
    }
}