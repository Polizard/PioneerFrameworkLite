using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **0.3 g 可行性检查**（2026-11-18）：对不同的转移时长，算 Lambert 的到达相对速度，
    /// 再算 0.3 g 下刹掉它需要多久、占转移时长的比例。
    /// 判据不是“越短越好”，而是“刹车时长 ≪ 转移时长”才谈得上能刹在 SOI 里。
    /// </summary>
    public class Torch030gFeasibilityTests
    {
        private readonly ITestOutputHelper output;
        public Torch030gFeasibilityTests(ITestOutputHelper output) => this.output = output;

        private const double MuStar = 1.327e20;


        [Theory]
        [InlineData(1.0, 1.0)]
        [InlineData(1.0, 2.0)]
        [InlineData(1.0, 3.0)]
        [InlineData(1.0, 5.0)]
        [InlineData(1.0, 7.0)]
        [InlineData(3.0, 1.0)]
        [InlineData(3.0, 2.0)]
        [InlineData(3.0, 3.0)]
        public void BrakeTimeVersusTransferTime(double accelerationInG, double tofDays)
        {
            var shipPosition = new DVector3(2.0e10, 0, 0);
            var shipVelocity = new DVector3(0, Math.Sqrt(MuStar / 2.0e10), 0);

            double omega = Math.Sqrt(MuStar / Math.Pow(2.4e10, 3));
            double phase = 0.5;
            double acceleration = accelerationInG * TorchTransferCore.StandardGravity;
            double tof = tofDays * 86400.0;
            double angle = phase + omega * tof;
            var targetPosition = new DVector3(2.4e10 * Math.Cos(angle), 2.4e10 * Math.Sin(angle), 0);
            double targetSpeed = Math.Sqrt(MuStar / 2.4e10);
            var targetVelocity = new DVector3(-targetSpeed * Math.Sin(angle), targetSpeed * Math.Cos(angle), 0);

            Assert.True(InterplanetaryTransferCore.TryPlanDeparture(MuStar, shipPosition, shipVelocity,
                targetPosition, tof, out DVector3 departureDv, out string reason), reason);

            DVector3 arrivalShipVelocity = OrbitMath.GetVelocityAtTimeDelta(shipPosition, shipVelocity + departureDv,
                tof, MuStar);
            double relativeSpeed = (arrivalShipVelocity - targetVelocity).magnitude;
            double brakeSeconds = relativeSpeed / acceleration;

            output.WriteLine($"{accelerationInG,4:F1} g / {tofDays,4:F0} 天：出航 Δv={departureDv.magnitude / 1000.0,7:F1} km/s"
                + $"｜到达相对速度 {relativeSpeed / 1000.0,7:F1} km/s"
                + $"｜刹车 {brakeSeconds / 3600.0,7:F2} h（占转移 {brakeSeconds / tof * 100.0,5:F1}%）");

            Assert.True(relativeSpeed > 0.0);
        }
    }
}
