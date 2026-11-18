using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Verse;

namespace PioneerFramework.ThreadSafety
{
    /// <summary>
    /// 多线程安全检测主编排器。
    ///
    /// 使用方式：
    ///   1. 代码调用：ThreadSafetyTestRunner.RunAll()
    ///   2. 调试窗口：在开发模式下通过 Debug Actions 菜单打开 Window_ThreadSafetyTest
    ///   3. 命令行：调用 ThreadSafetyTestRunner.RunAllAndLog()
    ///
    /// 测试套件：
    ///   - DeterminismTest:      验证单线程/多线程结果一致性
    ///   - RaceConditionDetector: 检测竞态条件、数据竞争
    ///   - PerformanceBenchmark:  对比单线程/多线程性能
    ///   - StressTest:           高负载长时间压力测试
    /// </summary>
    public static class ThreadSafetyTestRunner
    {
        /// <summary>
        /// 运行所有测试套件，返回完整报告。
        /// </summary>
        public static FullTestReport RunAll(
            int threadCount = 8,
            int determinismIterations = 1000,
            int stressDurationSeconds = 5)
        {
            var report = new FullTestReport { RunTime = DateTime.Now };
            var totalSw = Stopwatch.StartNew();

            // ── 套件 1: 确定性验证 ──
            Log.Message("[ThreadSafety] 开始确定性验证...");
            report.Suites.Add(DeterminismTest.Run(threadCount, determinismIterations));

            // ── 套件 2: 竞态检测 ──
            Log.Message("[ThreadSafety] 开始竞态检测...");
            report.Suites.Add(RaceConditionDetector.Run(threadCount));

            // ── 套件 3: 性能基准 ──
            Log.Message("[ThreadSafety] 开始性能基准测试...");
            report.Suites.Add(PerformanceBenchmark.Run(threadCount));

            // ── 套件 4: 压力测试 ──
            Log.Message("[ThreadSafety] 开始压力测试...");
            report.Suites.Add(StressTest.Run(threadCount, stressDurationSeconds));

            // ── 套件 5: FMA 支持检测 ──
            Log.Message("[ThreadSafety] 开始 FMA 支持检测...");
            report.Suites.Add(FmaSupportTest.Run());

            totalSw.Stop();
            report.TotalElapsedMs = totalSw.Elapsed.TotalMilliseconds;

            return report;
        }

        /// <summary>
        /// 运行所有测试并输出到日志。
        /// </summary>
        public static void RunAllAndLog(
            int threadCount = 8,
            int determinismIterations = 1000,
            int stressDurationSeconds = 5)
        {
            var report = RunAll(threadCount, determinismIterations, stressDurationSeconds);
            Log.Message(report.ToString());

            if (report.TotalFailed > 0)
            {
                Log.Warning(string.Format(
                    "[ThreadSafety] *** {0}/{1} 测试失败 *** 请检查上述错误详情。",
                    report.TotalFailed, report.TotalCases));
            }
            else
            {
                Log.Message(string.Format(
                    "[ThreadSafety] 全部 {0} 个测试通过。总耗时 {1:F1}ms",
                    report.TotalCases, report.TotalElapsedMs));
            }
        }

        /// <summary>
        /// 快速冒烟测试（轻量级，适合每次构建后运行）。
        /// </summary>
        public static FullTestReport RunSmoke(int threadCount = 4)
        {
            var report = new FullTestReport { RunTime = DateTime.Now };
            var totalSw = Stopwatch.StartNew();

            // 仅运行确定性验证和竞态检测（跳过性能基准和压力测试）
            report.Suites.Add(DeterminismTest.Run(threadCount, 200));
            report.Suites.Add(RaceConditionDetector.Run(threadCount, 10));

            totalSw.Stop();
            report.TotalElapsedMs = totalSw.Elapsed.TotalMilliseconds;
            return report;
        }

        /// <summary>
        /// 获取格式化的测试摘要（单行）。
        /// </summary>
        public static string GetSummary(FullTestReport report)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("[ThreadSafety] {0}/{1} passed", report.TotalPassed, report.TotalCases);
            if (report.TotalFailed > 0)
                sb.AppendFormat(" *** {0} FAILED ***", report.TotalFailed);
            sb.AppendFormat(" ({0:F0}ms)", report.TotalElapsedMs);
            return sb.ToString();
        }

        /// <summary>
        /// 获取详细信息（多行，适合日志输出）。
        /// </summary>
        public static string GetDetailedReport(FullTestReport report)
        {
            return report.ToString();
        }
    }
}