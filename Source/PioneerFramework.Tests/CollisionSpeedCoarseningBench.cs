using System;
using System.Collections.Generic;
using System.Diagnostics;
using PioneerFramework;
using PioneerFramework.Combat;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **速度 × 粗化档台架**（2026-10-19）：固定 2s 物理步，比较
    /// 组级 swept AABB、以及 1/4/8/16/32/64 个 part 采样点，能否发现一次穿越。
    /// 不判 PASS/FAIL，只输出读数；用于决定不同速度用哪一档。
    /// </summary>
    public class CollisionSpeedCoarseningBench
    {
        private readonly ITestOutputHelper _out;
        public CollisionSpeedCoarseningBench(ITestOutputHelper output) => _out = output;

        private static readonly double[] Speeds = { 10, 50, 100, 500, 1000, 5000, 10000 };
        private static readonly int[] SampleCounts = { 1, 4, 8, 16, 32, 64 };

        [Fact]
        public void SpeedVsCoarsening_Table()
        {
            _out.WriteLine("[速度×粗化] 步长 dt=2.0s；target=单个 2m 盒；读数=是否命中");
            RunCase("2m 小件", half: 1.0);

            _out.WriteLine("[速度×粗化] target=40m 船体盒；读数=是否命中");
            RunCase("40m 船体", half: 20.0);

            _out.WriteLine("[速度×粗化] target=256 件长条（40×560×40 并集）");
            RunLongStack();
        }

        private void RunCase(string label, double half)
        {
            var a = new List<CombatManager.PartAabb>
            {
                new(new DVector3(-half, -half, -half), new DVector3(half, half, half), 0)
            };

            _out.WriteLine($"[{label}] speed | step | group | K1 | K4 | K8 | K16 | K32 | K64 | ref");
            foreach (double speed in Speeds)
            {
                double step = speed * 2.0;
                double start = step + 2.0 * half + 10.0;
                double end = -2.0 * half - 10.0;

                bool reference = SampleHits(a, half, start, end, 20000);
                bool group = GroupSweepHits(half, start, end);

                string row = $"[{label}] {speed,6:F0} | {step,7:F0} | {(group ? "Y" : "-"),5}";
                foreach (int k in SampleCounts)
                    row += $" | {(SampleHits(a, half, start, end, k) ? "Y" : "-"),3}";
                row += $" | {(reference ? "Y" : "-")}";
                _out.WriteLine(row);
            }
        }

        private void RunLongStack()
        {
            var a = new List<CombatManager.PartAabb>();
            for (int i = 0; i < 256; i++)
            {
                double y = -i * 2.0;
                a.Add(new CombatManager.PartAabb(
                    new DVector3(-20, y - 1, -20), new DVector3(20, y + 1, 20), i));
            }

            const double half = 20.0;
            _out.WriteLine("[256 长条] speed | step | group | K8 | K16 | K32 | K64 | ref");
            foreach (double speed in Speeds)
            {
                double step = speed * 2.0;
                double start = step + 2.0 * half + 10.0;
                double end = -2.0 * half - 10.0;

                bool reference = SampleHits(a, half, start, end, 2000);
                bool group = GroupSweepHits(half, start, end);

                string row = $"[256 长条] {speed,6:F0} | {step,7:F0} | {(group ? "Y" : "-"),5}";
                foreach (int k in new[] { 8, 16, 32, 64 })
                    row += $" | {(SampleHits(a, half, start, end, k) ? "Y" : "-"),3}";
                row += $" | {(reference ? "Y" : "-")}";
                _out.WriteLine(row);
            }
        }

        private static bool GroupSweepHits(double half, double start, double end)
        {
            return CombatManager.TryComputeTunneledStopWithNormal(
                new DVector3(start, 0, 0), new DVector3(end, 0, 0),
                new DVector3(-half, -half, -half), new DVector3(half, half, half),
                new DVector3(-half, -half, -half), new DVector3(half, half, half),
                out _, out _);
        }

        private static bool SampleHits(List<CombatManager.PartAabb> a, double half,
            double start, double end, int samples)
        {
            // 采样 B 盒中心；A 盒在原点。
            for (int i = 0; i < samples; i++)
            {
                double u = samples == 1 ? 0.0 : i / (double)(samples - 1);
                double x = start + (end - start) * u;
                // 快速 AABB 重叠：A 在 [-half,half]，B 在 [x-half,x+half]
                if (Math.Abs(x) < 2.0 * half) return true;
            }
            return false;
        }
    }
}
