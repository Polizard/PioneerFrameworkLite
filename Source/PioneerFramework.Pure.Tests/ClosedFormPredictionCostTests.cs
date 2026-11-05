using System;
using System.Diagnostics;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 行星际“闭式预测”成本台架（2026-11-03，只读台架 / 不碰生产后端）。
    ///
    /// 用户硬预算：运行时只接受“几次 10 µs 级”的计算。
    /// 本台架只量闭式路径，不含量 Lambert（它 5.4–8.3 ms，直接出局）：
    ///   ① HohmannCore.Compute（初始双脉冲，闭式）
    ///   ② OrbitMath.GetPositionAtTimeDelta（一次开普勒传播）
    ///   ③ 线性修正：1 次传播 + 3×3 求解（运行时，增益已预存）
    ///   ④ 一次性增益：4 次传播 + 3×3（出发时算一次，可预存）
    ///
    /// 判定：①–③ 每次 ≤10 µs；④ 一次性 ≤50 µs。
    /// ⚠ 台架读数依机器/Release/JIT 而定；它是“量级证据”，不是跨机硬保证。
    /// </summary>
    public class ClosedFormPredictionCostTests
    {
        private const double Mu = 2.4369393583e19;
        private const double R1 = 20_000_000_000.0;
        private const double R2 = R1 * 2.0 / 3.0;
        private double sink;

        private readonly ITestOutputHelper output;

        public ClosedFormPredictionCostTests(ITestOutputHelper output) => this.output = output;

        private double Measure(string name, int iterations, Action action)
        {
            for (int i = 0; i < 200; i++) action(); // JIT 预热
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) action();
            long end = Stopwatch.GetTimestamp();
            double us = (end - start) * 1_000_000.0 / Stopwatch.Frequency / iterations;
            output.WriteLine($"{name}: {us:F3} us/call（{iterations} 次）");
            return us;
        }

        private static DVector3 Solve3(DVector3 c0, DVector3 c1, DVector3 c2, DVector3 b)
        {
            double det = DVector3.Dot(c0, DVector3.Cross(c1, c2));
            return (DVector3.Cross(c1, c2) * b.x
                  + DVector3.Cross(c2, c0) * b.y
                  + DVector3.Cross(c0, c1) * b.z) / det;
        }

        [Fact]
        public void ClosedFormPrediction_StaysInTenMicrosecondBudget()
        {
            var r1 = new DVector3(R1, 0, 0);
            var v1 = new DVector3(0, Math.Sqrt(Mu / R1), 0);
            var normal = new DVector3(0, 0, 1);
            var res = HohmannCore.Compute(r1, v1, Mu, R2, normal);
            Assert.True(res.Valid, res.Reason);
            DVector3 vTransfer = v1 + res.Dv1;
            double tof = res.TransferSeconds;

            double hohmannUs = Measure("HohmannCore.Compute", 10_000, () =>
            {
                var h = HohmannCore.Compute(r1, v1, Mu, R2, normal);
                sink += h.TotalDv;
            });

            double propagUs = Measure("OrbitMath.GetPositionAtTimeDelta", 10_000, () =>
            {
                var p = OrbitMath.GetPositionAtTimeDelta(r1, vTransfer, tof, Mu);
                sink += p.x;
            });

            // 一次性增益：中点状态 → 到达点的位置敏感度 3×3（4 次传播）
            double tCorr = tof * 0.5;
            double remain = tof - tCorr;
            var rC = OrbitMath.GetPositionAtTimeDelta(r1, vTransfer, tCorr, Mu);
            var vC = OrbitMath.GetVelocityAtTimeDelta(r1, vTransfer, tCorr, Mu);
            const double eps = 0.01; // m/s
            var miss = new DVector3(1.0, 0, 0);
            var c0 = (OrbitMath.GetPositionAtTimeDelta(rC, vC + new DVector3(eps, 0, 0), remain, Mu) - OrbitMath.GetPositionAtTimeDelta(rC, vC, remain, Mu)) / eps;
            var c1 = (OrbitMath.GetPositionAtTimeDelta(rC, vC + new DVector3(0, eps, 0), remain, Mu) - OrbitMath.GetPositionAtTimeDelta(rC, vC, remain, Mu)) / eps;
            var c2 = (OrbitMath.GetPositionAtTimeDelta(rC, vC + new DVector3(0, 0, eps), remain, Mu) - OrbitMath.GetPositionAtTimeDelta(rC, vC, remain, Mu)) / eps;

            double gainUs = Measure("one-time gain (4 propagations + 3x3)", 1_000, () =>
            {
                var a = OrbitMath.GetPositionAtTimeDelta(rC, vC, remain, Mu);
                var d0 = (OrbitMath.GetPositionAtTimeDelta(rC, vC + new DVector3(eps, 0, 0), remain, Mu) - a) / eps;
                var d1 = (OrbitMath.GetPositionAtTimeDelta(rC, vC + new DVector3(0, eps, 0), remain, Mu) - a) / eps;
                var d2 = (OrbitMath.GetPositionAtTimeDelta(rC, vC + new DVector3(0, 0, eps), remain, Mu) - a) / eps;
                var dv = Solve3(d0, d1, d2, miss);
                sink += dv.x;
            });

            double correctionUs = Measure("runtime correction (1 propagation + 3x3)", 10_000, () =>
            {
                var rNow = OrbitMath.GetPositionAtTimeDelta(r1, vTransfer, tCorr, Mu);
                var dv = Solve3(c0, c1, c2, rNow - res.ArrivalPos);
                sink += dv.x;
            });

            output.WriteLine($"预算：①–③ 每次 ≤10 µs；④ 一次性 ≤50 µs；GC.KeepAlive={GC.GetTotalMemory(false)} B");
            Assert.True(hohmannUs <= 10.0, $"HohmannCore {hohmannUs:F3} us > 10 us");
            Assert.True(propagUs <= 10.0, $"propagation {propagUs:F3} us > 10 us");
            Assert.True(correctionUs <= 10.0, $"runtime correction {correctionUs:F3} us > 10 us");
            Assert.True(gainUs <= 50.0, $"one-time gain {gainUs:F3} us > 50 us");
            GC.KeepAlive(sink);
        }
    }
}
