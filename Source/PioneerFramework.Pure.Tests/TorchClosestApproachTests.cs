using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **纯 Lambert 轨迹的最近接近**（2026-11-19）：不刹车，看船能靠赭砂多近、那时相对速度多大。
    /// 这是"能不能捕获"的前提：最近距离必须 ≤ SOI，且相对速度要小到 0.3 g/1 g 刹得住。
    /// </summary>
    public class TorchClosestApproachTests
    {
        private readonly ITestOutputHelper output;
        public TorchClosestApproachTests(ITestOutputHelper output) => this.output = output;

        private const double MuStar = 1.327e20;
        private const double MuTarget = 1.0e13;
        private const double Soi = 1.325e8;

        [Theory]
        [InlineData(1.0, 3.0)]
        [InlineData(1.0, 5.0)]
        [InlineData(0.3, 5.0)]
        [InlineData(0.3, 8.0)]
        public void LambertTrajectory_ClosestApproach(double accelerationInG, double tofDays)
        {
            var shipPosition = new DVector3(2.0e10, 0, 0);
            var shipVelocity = new DVector3(0, Math.Sqrt(MuStar / 2.0e10), 0);

            double omega = Math.Sqrt(MuStar / Math.Pow(2.4e10, 3));
            double phase = 0.5;
            (DVector3, DVector3) TargetAt(double t)
            {
                double angle = phase + omega * t;
                double speed = Math.Sqrt(MuStar / 2.4e10);
                return (new DVector3(2.4e10 * Math.Cos(angle), 2.4e10 * Math.Sin(angle), 0),
                        new DVector3(-speed * Math.Sin(angle), speed * Math.Cos(angle), 0));
            }

            double tof = tofDays * 86400.0;
            (DVector3 arrivalPosition, _) = TargetAt(tof);
            Assert.True(InterplanetaryTransferCore.TryPlanDeparture(MuStar, shipPosition, shipVelocity,
                arrivalPosition, tof, out DVector3 departureDv, out string reason), reason);

            // 出航段中点修正
            for (int i = 0; i < 3; i++)
            {
                double tau = departureDv.magnitude / (accelerationInG * TorchTransferCore.StandardGravity);
                var midPos = OrbitMath.GetPositionAtTimeDelta(shipPosition, shipVelocity, tau * 0.5, MuStar);
                var midVel = OrbitMath.GetVelocityAtTimeDelta(shipPosition, shipVelocity, tau * 0.5, MuStar);
                if (!InterplanetaryTransferCore.TryPlanDeparture(MuStar, midPos, midVel, arrivalPosition,
                        tof - tau * 0.5, out DVector3 corrected, out _)) break;
                departureDv = corrected;
            }

            // 不刹车，free-fly 到 1.3×tof，找最近接近
            double acceleration = accelerationInG * TorchTransferCore.StandardGravity;
            double departureSeconds = departureDv.magnitude / acceleration;
            DVector3 departureDirection = departureDv * (1.0 / departureDv.magnitude);

            DVector3 position = shipPosition;
            DVector3 velocity = shipVelocity;
            const double step = 20.0;
            double bestDistance = double.MaxValue;
            double bestTime = 0.0;
            double bestRelativeSpeed = 0.0;

            for (double t = 0.0; t < tof * 1.3; t += step)
            {
                double starRadius = position.magnitude;
                DVector3 accel = position * (-MuStar / (starRadius * starRadius * starRadius));
                (DVector3 targetPosition, _) = TargetAt(t);
                DVector3 relative = position - targetPosition;
                double targetRadius = relative.magnitude;
                if (targetRadius < Soi) accel = accel + relative * (-MuTarget / (targetRadius * targetRadius * targetRadius));
                if (t < departureSeconds) accel = accel + departureDirection * acceleration;

                velocity = velocity + accel * step;
                position = position + velocity * step;

                (DVector3 nextTarget, DVector3 nextTargetVelocity) = TargetAt(t + step);
                DVector3 nextRelative = position - nextTarget;
                double distance = nextRelative.magnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestTime = t + step;
                    bestRelativeSpeed = (velocity - nextTargetVelocity).magnitude;
                }
            }

            double brakeHours = bestRelativeSpeed / acceleration / 3600.0;
            output.WriteLine($"{accelerationInG:F1} g / {tofDays:F0} 天：最近 {bestDistance / 1e6:F1}e6 m"
                + $"（{bestDistance / Soi:F2}×SOI，t={bestTime / 3600.0:F1} h）"
                + $"｜相对速度 {bestRelativeSpeed / 1000.0:F1} km/s｜刹车 {brakeHours:F2} h");

            Assert.True(bestDistance > 0.0);
        }
    }
}
