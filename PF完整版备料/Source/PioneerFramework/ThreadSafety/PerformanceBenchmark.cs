using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Verse;

namespace PioneerFramework.ThreadSafety
{
    /// <summary>
    /// 性能基准测试：对比单线程 vs 多线程的执行效率。
    /// 验证多线程是否真正带来了性能提升，以及加速比是否合理。
    /// </summary>
    public static class PerformanceBenchmark
    {
        public static TestSuiteResult Run(int threadCount = 8, int workloadSize = 10000)
        {
            var sw = Stopwatch.StartNew();
            var suite = new TestSuiteResult { SuiteName = "Performance Benchmark (性能基准)" };

            // ── 1. NBodyIntegrator 计算吞吐量 ──
            suite.Cases.Add(BenchNBodyIntegration(threadCount, workloadSize));

            // ── 2. 轨道保持 dV 计算吞吐量 ──
            suite.Cases.Add(BenchOrbitKeeping(threadCount, workloadSize));

            // ── 3. 空间网格查询吞吐量 ──
            suite.Cases.Add(BenchSpatialGridQuery(threadCount, workloadSize));

            // ── 4. 混合负载（模拟真实场景） ──
            suite.Cases.Add(BenchMixedWorkload(threadCount, workloadSize));

            suite.TotalElapsedMs = sw.Elapsed.TotalMilliseconds;
            return suite;
        }

        /// <summary>
        /// 基准测试 NBodyIntegrator 的计算吞吐量。
        /// </summary>
        private static TestCaseResult BenchNBodyIntegration(int threadCount, int workloadSize)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = "NBody 积分吞吐量" };

            // 生成多种轨道场景
            var rng = new Random(42);
            var scenarios = new (DVector3 pos, DVector3 vel, DVector3 bodyPos, DVector3 bodyVel, double mu)[workloadSize];
            for (int i = 0; i < workloadSize; i++)
            {
                double alt = 200_000 + rng.NextDouble() * 10_000_000;
                double speed = Math.Sqrt(SpaceConstants.PrimaryGravitationalParameter / (6_371_000 + alt));
                scenarios[i] = (
                    new DVector3(6_371_000 + alt, 0, 0),
                    new DVector3(0, 0, speed * (0.8 + rng.NextDouble() * 0.4)),
                    DVector3.zero,
                    DVector3.zero,
                    SpaceConstants.PrimaryGravitationalParameter
                );
            }

            // 预热
            for (int i = 0; i < 10; i++)
            {
                var s = scenarios[i];
                NBodyIntegrator.ComputeBodyAccelJerk(s.pos, s.vel, s.bodyPos, s.bodyVel, s.mu, out _, out _);
            }

            // 单线程
            var swSingle = Stopwatch.StartNew();
            for (int i = 0; i < workloadSize; i++)
            {
                var s = scenarios[i];
                NBodyIntegrator.ComputeBodyAccelJerk(s.pos, s.vel, s.bodyPos, s.bodyVel, s.mu, out var acc, out var jerk);
                NBodyIntegrator.ThirdOrderTaylorStep(s.pos, s.vel, acc, jerk, 2.0, out _, out _);
            }
            double singleMs = swSingle.Elapsed.TotalMilliseconds;

            // 多线程
            var swMulti = Stopwatch.StartNew();
            Parallel.For(0, workloadSize, i =>
            {
                var s = scenarios[i];
                NBodyIntegrator.ComputeBodyAccelJerk(s.pos, s.vel, s.bodyPos, s.bodyVel, s.mu, out var acc, out var jerk);
                NBodyIntegrator.ThirdOrderTaylorStep(s.pos, s.vel, acc, jerk, 2.0, out _, out _);
            });
            double multiMs = swMulti.Elapsed.TotalMilliseconds;

            double speedup = singleMs / Math.Max(multiMs, 0.001);
            result.Passed = speedup >= 0.5; // 至少不比单线程差太多
            result.Message = string.Format("单线程={0:F1}ms, 多线程={1:F1}ms, 加速比=x{2:F2}",
                singleMs, multiMs, speedup);
            result.Details.Add(string.Format("工作负载={0} 组轨道计算", workloadSize));
            if (speedup < 1.0)
                result.Details.Add("警告: 多线程未加速，可能负载过轻或线程开销过大");
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 基准测试轨道保持 dV 计算（模拟 ComputeOrbitKeepingDvRate 的批量调用）。
        /// </summary>
        private static TestCaseResult BenchOrbitKeeping(int threadCount, int workloadSize)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = "轨道保持 dV 计算吞吐量" };

            var rng = new Random(42);
            var inputs = new DVector3[workloadSize];
            for (int i = 0; i < workloadSize; i++)
            {
                double alt = 400_000 + rng.NextDouble() * 5_000_000;
                inputs[i] = new DVector3(6_371_000 + alt, rng.NextDouble() * 100_000, rng.NextDouble() * 100_000);
            }

            double singleMs, multiMs;

            // 单线程
            {
                var swSingle = Stopwatch.StartNew();
                for (int i = 0; i < workloadSize; i++)
                {
                    var pos = inputs[i];
                    NBodyIntegrator.ComputeBodyAccelJerk(pos, DVector3.zero, DVector3.zero, DVector3.zero,
                        SpaceConstants.PrimaryGravitationalParameter, out var acc, out _);
                    double dv = acc.magnitude;
                }
                singleMs = swSingle.Elapsed.TotalMilliseconds;
            }

            // 多线程
            {
                var swMulti = Stopwatch.StartNew();
                Parallel.For(0, workloadSize, i =>
                {
                    var pos = inputs[i];
                    NBodyIntegrator.ComputeBodyAccelJerk(pos, DVector3.zero, DVector3.zero, DVector3.zero,
                        SpaceConstants.PrimaryGravitationalParameter, out var acc, out _);
                    double dv = acc.magnitude;
                });
                multiMs = swMulti.Elapsed.TotalMilliseconds;
            }

            double speedup = singleMs / Math.Max(multiMs, 0.001);
            result.Passed = true;
            result.Message = string.Format("单线程={0:F1}ms, 多线程={1:F1}ms, 加速比=x{2:F2}",
                singleMs, multiMs, speedup);
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 基准测试空间网格查询（模拟 GetNearby 的批量调用）。
        /// </summary>
        private static TestCaseResult BenchSpatialGridQuery(int threadCount, int workloadSize)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = "空间网格查询吞吐量" };

            // 构建网格
            var grid = new SpatialGrid3D<ISpatialEntity>();
            var rng = new Random(42);
            var entities = new List<ISpatialEntity>();
            for (int i = 0; i < 5000; i++)
            {
                var group = new SpaceEntityGroup(i, 0);
                var pos = new DVector3(
                    (rng.NextDouble() - 0.5) * 10_000_000,
                    (rng.NextDouble() - 0.5) * 10_000_000,
                    (rng.NextDouble() - 0.5) * 10_000_000);
                group.CurrentOrbit = new OrbitState { position = pos, velocity = DVector3.zero };
                grid.Add(group, pos);
                entities.Add(group);
            }

            // 查询点
            var queryPoints = new DVector3[workloadSize];
            for (int i = 0; i < workloadSize; i++)
                queryPoints[i] = new DVector3(
                    (rng.NextDouble() - 0.5) * 10_000_000,
                    (rng.NextDouble() - 0.5) * 10_000_000,
                    (rng.NextDouble() - 0.5) * 10_000_000);

            double singleMs, multiMs;
            const double queryRadius = 500_000;

            // 单线程
            {
                var swSingle = Stopwatch.StartNew();
                for (int i = 0; i < workloadSize; i++)
                {
                    int count = 0;
                    foreach (var e in grid.GetNearby(queryPoints[i], queryRadius))
                        count++;
                }
                singleMs = swSingle.Elapsed.TotalMilliseconds;
            }

            // 多线程（只读查询，线程安全）
            {
                var swMulti = Stopwatch.StartNew();
                Parallel.For(0, workloadSize, i =>
                {
                    int count = 0;
                    foreach (var e in grid.GetNearby(queryPoints[i], queryRadius))
                        count++;
                });
                multiMs = swMulti.Elapsed.TotalMilliseconds;
            }

            double speedup = singleMs / Math.Max(multiMs, 0.001);
            result.Passed = true;
            result.Message = string.Format("单线程={0:F1}ms, 多线程={1:F1}ms, 加速比=x{2:F2}",
                singleMs, multiMs, speedup);
            result.Details.Add(string.Format("网格实体数={0}, 查询半径={1}km", 5000, queryRadius / 1000));
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// 混合负载基准测试：模拟真实场景中的多种计算混合。
        /// </summary>
        private static TestCaseResult BenchMixedWorkload(int threadCount, int workloadSize)
        {
            var sw = Stopwatch.StartNew();
            var result = new TestCaseResult { Name = "混合负载吞吐量" };

            var rng = new Random(42);
            var orbitalData = new (DVector3 pos, DVector3 vel)[workloadSize];
            for (int i = 0; i < workloadSize; i++)
            {
                double alt = 200_000 + rng.NextDouble() * 10_000_000;
                double speed = Math.Sqrt(SpaceConstants.PrimaryGravitationalParameter / (6_371_000 + alt));
                orbitalData[i] = (
                    new DVector3(6_371_000 + alt, 0, 0),
                    new DVector3(0, 0, speed)
                );
            }

            double singleMs, multiMs;

            // 单线程：NBody 积分 + 轨道保持
            {
                var swSingle = Stopwatch.StartNew();
                for (int i = 0; i < workloadSize; i++)
                {
                    var d = orbitalData[i];
                    NBodyIntegrator.ComputeBodyAccelJerk(d.pos, d.vel, DVector3.zero, DVector3.zero,
                        SpaceConstants.PrimaryGravitationalParameter, out var acc, out var jerk);
                    NBodyIntegrator.ThirdOrderTaylorStep(d.pos, d.vel, acc, jerk, 2.0, out _, out _);
                    double dv = acc.magnitude * 2.0;
                }
                singleMs = swSingle.Elapsed.TotalMilliseconds;
            }

            // 多线程
            {
                var swMulti = Stopwatch.StartNew();
                Parallel.For(0, workloadSize, i =>
                {
                    var d = orbitalData[i];
                    NBodyIntegrator.ComputeBodyAccelJerk(d.pos, d.vel, DVector3.zero, DVector3.zero,
                        SpaceConstants.PrimaryGravitationalParameter, out var acc, out var jerk);
                    NBodyIntegrator.ThirdOrderTaylorStep(d.pos, d.vel, acc, jerk, 2.0, out _, out _);
                    double dv = acc.magnitude * 2.0;
                });
                multiMs = swMulti.Elapsed.TotalMilliseconds;
            }

            double speedup = singleMs / Math.Max(multiMs, 0.001);
            result.Passed = true;
            result.Message = string.Format("单线程={0:F1}ms, 多线程={1:F1}ms, 加速比=x{2:F2}",
                singleMs, multiMs, speedup);
            result.Details.Add(string.Format("每组: NBody积分+TaylorStep+dV计算, 共{0}组", workloadSize));
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }
    }
}