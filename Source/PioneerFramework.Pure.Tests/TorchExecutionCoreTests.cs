using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **火炬规划复刻台**（2026-11-19 重写）：真参数（燧阳 GM 2.4369e19、赭砂 GM 2.4e14、
    /// 赭砂轨道 1.3333e10 m / 周期 1959591.794 s / 相位 4.596）。
    ///
    /// 判据交给规划器自己：`TryPlan` 内部已用同一个 `Propagate` 重放验证
    /// （束缚 + 远心点 ≤ 0.95×SOI）。这里只断言"规划成立"并打印计划数值。
    /// </summary>
    public class TorchExecutionCoreTests
    {
        private readonly ITestOutputHelper output;
        public TorchExecutionCoreTests(ITestOutputHelper output) => this.output = output;

        private const double MuStar = 2.4369393583e19;
        private const double MuTarget = 2.4e14;
        private const double Soi = 1.325e8;
        private const double TargetRadius = 13333333333.333334;
        private const double TargetPeriod = 1959591.794;
        private const double TargetPhase = 4.596;

        [Theory]
        [InlineData(1.0, 5.0)]
        [InlineData(1.0, 3.0)]
        [InlineData(0.3, 8.0)]
        [InlineData(0.3, 5.0)]
        [InlineData(1.0, 2.0)]
        public void Plan_ProducesACapturableTransfer(double accelerationInG, double tofDays)
        {
            var shipPosition = new DVector3(2.0e10, 0, 0);
            var shipVelocity = new DVector3(0, Math.Sqrt(MuStar / 2.0e10), 0);

            double omega = 2.0 * Math.PI / TargetPeriod;
            (DVector3, DVector3) TargetAt(double t)
            {
                double angle = TargetPhase + omega * t;
                double speed = Math.Sqrt(MuStar / TargetRadius);
                return (new DVector3(TargetRadius * Math.Cos(angle), TargetRadius * Math.Sin(angle), 0),
                        new DVector3(-speed * Math.Sin(angle), speed * Math.Cos(angle), 0));
            }

            double acceleration = accelerationInG * TorchTransferCore.StandardGravity;
            double tof = tofDays * 86400.0;
            bool planned = TorchExecutionCore.TryPlan(shipPosition, shipVelocity, TargetAt,
                MuStar, MuTarget, Soi, acceleration, tof, 0.5 * Soi,
                out TorchExecutionPlan plan, out string reason);

            output.WriteLine($"{accelerationInG:F1} g / {tofDays:F0} 天：{(planned ? "成立" : "失败 → " + reason)}"
                + (planned
                    ? $"｜出航 Δv={plan.DepartureDv.magnitude / 1000.0:F1} km/s（{plan.DepartureSeconds / 3600.0:F2} h）"
                      + $"｜捕获 Δv={plan.CaptureDv.magnitude / 1000.0:F1} km/s（{plan.CaptureSeconds / 3600.0:F2} h）"
                      + $"｜边界 {plan.EntrySeconds / 3600.0:F2} h、r={plan.EntryRadius / 1e6:F1}e6 m"
                    : ""));

            Assert.True(planned, reason);
            Assert.True(plan.DepartureDv.magnitude > 0.0);
            Assert.True(plan.CaptureDv.magnitude > 0.0);
            Assert.True(plan.EntryRadius <= Soi);
            Assert.True(plan.CaptureStartSeconds > 0.0);
        }
    }
}
