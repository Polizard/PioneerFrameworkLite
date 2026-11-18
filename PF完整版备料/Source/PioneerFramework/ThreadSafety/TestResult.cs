using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PioneerFramework.ThreadSafety
{
    /// <summary>
    /// 单个测试用例的结果。
    /// </summary>
    public class TestCaseResult
    {
        public string Name;
        public bool Passed;
        public string Message;
        public double ElapsedMs;
        public List<string> Details = new List<string>();

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Passed ? "[PASS]" : "[FAIL]");
            sb.Append("  ").Append(Name);
            sb.Append(" (").Append(ElapsedMs.ToString("F1")).Append("ms)");
            if (!string.IsNullOrEmpty(Message))
            {
                sb.Append(": ").Append(Message);
            }
            foreach (var d in Details)
                sb.AppendLine().Append("    ").Append(d);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 测试套件结果。
    /// </summary>
    public class TestSuiteResult
    {
        public string SuiteName;
        public List<TestCaseResult> Cases = new List<TestCaseResult>();
        public int PassedCount => Cases.FindAll(c => c.Passed).Count;
        public int FailedCount => Cases.FindAll(c => !c.Passed).Count;
        public double TotalElapsedMs;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("══════════════════════════════════════════");
            sb.Append("  ").Append(SuiteName);
            sb.Append("  [").Append(PassedCount).Append("/").Append(Cases.Count).Append(" passed]");
            sb.Append("  (").Append(TotalElapsedMs.ToString("F1")).Append("ms)");
            sb.AppendLine();
            sb.AppendLine("──────────────────────────────────────────");
            foreach (var c in Cases)
                sb.AppendLine(c.ToString());
            sb.AppendLine("══════════════════════════════════════════");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 完整测试报告。
    /// </summary>
    public class FullTestReport
    {
        public DateTime RunTime;
        public List<TestSuiteResult> Suites = new List<TestSuiteResult>();
        public int TotalPassed => Suites.Sum(s => s.PassedCount);
        public int TotalFailed => Suites.Sum(s => s.FailedCount);
        public int TotalCases => Suites.Sum(s => s.Cases.Count);
        public double TotalElapsedMs;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("╔══════════════════════════════════════════════════╗");
            sb.AppendLine("║   THREAD SAFETY TEST REPORT                     ║");
            sb.AppendLine("╠══════════════════════════════════════════════════╣");
            sb.Append("║  Run: ").Append(RunTime.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine("                  ║");
            sb.Append("║  Total: ").Append(TotalPassed).Append("/").Append(TotalCases).Append(" passed");
            if (TotalFailed > 0) sb.Append("  *** ").Append(TotalFailed).Append(" FAILED ***");
            sb.AppendLine("        ║");
            sb.Append("║  Elapsed: ").Append(TotalElapsedMs.ToString("F1")).Append("ms");
            sb.AppendLine("                              ║");
            sb.AppendLine("╚══════════════════════════════════════════════════╝");
            sb.AppendLine();
            foreach (var suite in Suites)
                sb.AppendLine(suite.ToString());
            return sb.ToString();
        }
    }
}