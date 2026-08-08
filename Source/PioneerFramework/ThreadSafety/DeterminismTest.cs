using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Verse;

namespace PioneerFramework.ThreadSafety
{
    /// <summary>
    /// 确定性验证：同一输入在单线程和多线程下是否产生相同结果。
    /// 核心原理：如果多线程实现正确，则 N 次并行执行的结果应与串行一致。
    /// </summary>
    public static class DeterminismTest
    {
        /// <summary>
        /// 测试参数：位置/速度/时间步长/迭代次数/线程数。
        /// </summary>
        private struct TestCase
        {
            public DVector3 Pos;
            public DVector3 Vel;
            public DVector3 BodyPos;
            public DVector3 BodyVel;
            public double Mu;
            public double Dt;
            public int Iterations;
            public int ThreadCount;
            public string Description;
        }

        public static TestSuiteResult Run(int threadCount = 8, int iterations = 1000)
        {
            var sw = Stopwatch.StartNew();
            var suite = new TestSuiteResult { SuiteName = "Determinism Tests (确定性验证)" };

            // ── 测试用例集 ──
            var testCases = new List<TestCase>
            {
                // 1. 近地轨道 LEO
                new TestCase
                {
                    Pos = new DVector3(6_371_000 + 400_000, 0, 0), // 400km 高度
                    Vel = new DVector3(0, 0, 7_670),                // 近圆轨道速度
                    BodyPos = DVector3.zero,
                    BodyVel = DVector3.zero,
                    Mu = SpaceConstants.PrimaryGravitationalParameter,
                    Dt = 2.0,
                    Iterations = iterations,
                    ThreadCount = threadCount,
                    Description = "LEO 400km 圆轨道"
                },
                // 2. 椭圆轨道
                new TestCase
                {
                    Pos = new DVector3(6_371_000 + 1_000_000, 0, 0),
                    Vel = new DVector3(0, 0, 5_000),
                    BodyPos = DVector3.zero,
                    BodyVel = DVector3.zero,
                    Mu = SpaceConstants.PrimaryGravitationalParameter,
                    Dt = 2.0,
                    Iterations = iterations,
                    ThreadCount = threadCount,
                    Description = "椭圆轨道"
                },
                // 3. 双天体（行星 + 月球）
                new TestCase
                {
                    Pos = new DVector3(6_371_000 + 400_000, 0, 0),
                    Vel = new DVector3(0, 0, 7_670),
                    BodyPos = new DVector3(384_400_000, 0, 0),  // 月球距离
                    BodyVel = new DVector3(0, 0, 1_022),          // 月球轨道速度
                    Mu = 4.9048695e12,                            // 月球 GM
                    Dt = 2.0,
                    Iterations = iterations,
                    ThreadCount = threadCount,
                    Description = "行星 + 月球双天体"
                },
                // 4. 高精度小步长
                new TestCase
                {
                    Pos = new DVector3(6_371_000 + 200_000, 0, 0),
                    Vel = new DVector3(0, 0, 7_800),
                    BodyPos = DVector3.zero,
                    BodyVel = DVector3.zero,
                    Mu = SpaceConstants.PrimaryGravitationalParameter,
                    Dt = 0.1,
                    Iterations = iterations * 10,
                    ThreadCount = threadCount,
                    Description = "高精度小步长 (0.1s)"
                },
            };

            foreach (var tc in testCases)
            {
                var caseSw = Stopwatch.StartNew();
                var result = new TestCaseResult { Name = tc.Description };
                bool passed = true;

                try
                {
                    // 1) 串行计算参考值
                    var serialResults = RunSerial(tc);
                    var reference = serialResults[serialResults.Length - 1];

                    // 2) 并行计算（多次运行检查一致性）
                    bool allParallelConsistent = true;
                    DVector3 firstParallelPos = default;
                    DVector3 firstParallelVel = default;
                    bool firstRun = true;

                    for (int run = 0; run < 3; run++)
                    {
                        var parallelResults = RunParallel(tc);
                        var endResult = parallelResults[parallelResults.Length - 1];

                        if (firstRun)
                        {
                            firstParallelPos = endResult.pos;
                            firstParallelVel = endResult.vel;
                            firstRun = false;
                        }
                        else
                        {
                            // 检查多次并行运行之间是否一致
                            if (!ThreadSafetyAssert.AreEqual(firstParallelPos, endResult.pos, 1e-3) ||
                                !ThreadSafetyAssert.AreEqual(firstParallelVel, endResult.vel, 1e-3))
                            {
                                allParallelConsistent = false;
                                result.Details.Add(string.Format(
                                    "Run {0}: 并行结果不一致 pos=({1},{2},{3}) vel=({4},{5},{6})",
                                    run,
                                    endResult.pos.x.ToString("E6"), endResult.pos.y.ToString("E6"), endResult.pos.z.ToString("E6"),
                                    endResult.vel.x.ToString("E6"), endResult.vel.y.ToString("E6"), endResult.vel.z.ToString("E6")));
                            }
                        }
                    }

                    // 3) 比较串行 vs 并行最终结果
                    bool posMatch = ThreadSafetyAssert.AreEqual(reference.pos, firstParallelPos, 1e-3);
                    bool velMatch = ThreadSafetyAssert.AreEqual(reference.vel, firstParallelVel, 1e-3);

                    if (!posMatch || !velMatch)
                    {
                        passed = false;
                        result.Message = "串行/并行结果不一致";
                        result.Details.Add(string.Format(
                            "  串行: pos=({0},{1},{2}) vel=({3},{4},{5})",
                            reference.pos.x.ToString("E6"), reference.pos.y.ToString("E6"), reference.pos.z.ToString("E6"),
                            reference.vel.x.ToString("E6"), reference.vel.y.ToString("E6"), reference.vel.z.ToString("E6")));
                        result.Details.Add(string.Format(
                            "  并行: pos=({0},{1},{2}) vel=({3},{4},{5})",
                            firstParallelPos.x.ToString("E6"), firstParallelPos.y.ToString("E6"), firstParallelPos.z.ToString("E6"),
                            firstParallelVel.x.ToString("E6"), firstParallelVel.y.ToString("E6"), firstParallelVel.z.ToString("E6")));
                        result.Details.Add(string.Format(
                            "  差异: pos=({0:E3},{1:E3},{2:E3}) vel=({3:E3},{4:E3},{5:E3})",
                            reference.pos.x - firstParallelPos.x,
                            reference.pos.y - firstParallelPos.y,
                            reference.pos.z - firstParallelPos.z,
                            reference.vel.x - firstParallelVel.x,
                            reference.vel.y - firstParallelVel.y,
                            reference.vel.z - firstParallelVel.z));
                    }

                    if (!allParallelConsistent)
                    {
                        passed = false;
                        if (string.IsNullOrEmpty(result.Message))
                            result.Message = "多次并行运行结果不一致";
                    }
                }
                catch (Exception ex)
                {
                    passed = false;
                    result.Message = "异常: " + ex.GetType().Name + " - " + ex.Message;
                    result.Details.Add(ex.StackTrace ?? "(no stack trace)");
                }

                result.Passed = passed;
                result.ElapsedMs = caseSw.Elapsed.TotalMilliseconds;
                suite.Cases.Add(result);
            }

            suite.TotalElapsedMs = sw.Elapsed.TotalMilliseconds;
            return suite;
        }

        /// <summary>
        /// 串行执行：依次计算每个迭代步。
        /// </summary>
        private static (DVector3 pos, DVector3 vel)[] RunSerial(TestCase tc)
        {
            var results = new (DVector3 pos, DVector3 vel)[tc.Iterations + 1];
            DVector3 pos = tc.Pos;
            DVector3 vel = tc.Vel;
            results[0] = (pos, vel);

            for (int i = 0; i < tc.Iterations; i++)
            {
                NBodyIntegrator.ComputeBodyAccelJerk(pos, vel, tc.BodyPos, tc.BodyVel, tc.Mu,
                    out var acc, out var jerk);
                NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, acc, jerk, tc.Dt,
                    out pos, out vel);
                results[i + 1] = (pos, vel);
            }
            return results;
        }

        /// <summary>
        /// 并行执行：将 N 次迭代均分到多个线程，每个线程独立计算一片。
        /// 注意：NBodyIntegrator 的计算是纯函数，每个线程独立计算自己的轨迹段。
        /// 此测试验证：将工作拆分为多个独立子任务并行执行时，结果是否一致。
        /// </summary>
        private static (DVector3 pos, DVector3 vel)[] RunParallel(TestCase tc)
        {
            int batchSize = tc.Iterations / tc.ThreadCount;
            int remainder = tc.Iterations % tc.ThreadCount;
            var results = new (DVector3 pos, DVector3 vel)[tc.Iterations + 1];
            results[0] = (tc.Pos, tc.Vel);

            // 每个线程从初始状态开始独立计算自己的批次
            Parallel.For(0, tc.ThreadCount, threadIdx =>
            {
                int start = 1 + threadIdx * batchSize + Math.Min(threadIdx, remainder);
                int count = batchSize + (threadIdx < remainder ? 1 : 0);
                if (count <= 0) return;

                // 从初始状态独立计算到该批次起点
                DVector3 pos = tc.Pos;
                DVector3 vel = tc.Vel;
                int totalSteps = start - 1;
                for (int s = 0; s < totalSteps; s++)
                {
                    NBodyIntegrator.ComputeBodyAccelJerk(pos, vel, tc.BodyPos, tc.BodyVel, tc.Mu,
                        out var acc, out var jerk);
                    NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, acc, jerk, tc.Dt,
                        out pos, out vel);
                }

                // 计算该批次
                for (int s = 0; s < count; s++)
                {
                    NBodyIntegrator.ComputeBodyAccelJerk(pos, vel, tc.BodyPos, tc.BodyVel, tc.Mu,
                        out var acc, out var jerk);
                    NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, acc, jerk, tc.Dt,
                        out pos, out vel);
                    results[start + s] = (pos, vel);
                }
            });

            return results;
        }
    }
}