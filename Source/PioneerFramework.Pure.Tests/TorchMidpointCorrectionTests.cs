using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **有限燃烧中点修正的离线验证**（2026-11-18）。
    ///
    /// 实机实测：实交 Δv 与命令一分不差，但落点晚约 1.9 h —— 冲量 vs 燃烧弧的中点偏移。
    /// 这里用同一套 Lambert（`InterplanetaryTransferCore.TryPlanDeparture`）+ 蛙跳引力重放：
    ///   A：按冲量解直接烧（旧算法）
    ///   B：从"燃烧中点状态"重解 Lambert（新算法）
    /// 判据：B 的落点误差 ≤ SOI，且明显小于 A。
    /// </summary>
    public class TorchMidpointCorrectionTests
    {
        private readonly ITestOutputHelper output;
        public TorchMidpointCorrectionTests(ITestOutputHelper output) => this.output = output;

        private const double Mu = 1.327e20;
        private const double Soi = 1.325e8;
        private const double Acceleration = 9.80665;

        [Fact]
        public void MidpointCorrection_LandsInsideSoiAndBeatsTheImpulsePlan()
        {
            var r1 = new DVector3(2.0e10, 0, 0);
            double circular = Math.Sqrt(Mu / 2.0e10);
            var v1 = new DVector3(0, circular, 0);

            const double tof = 2.0 * 86400.0;
            double omega = Math.Sqrt(Mu / Math.Pow(2.4e10, 3));
            DVector3 TargetAt(double t) => new(2.4e10 * Math.Cos(0.5 + omega * t), 2.4e10 * Math.Sin(0.5 + omega * t), 0);

            // A：冲量 Lambert（旧）
            Assert.True(InterplanetaryTransferCore.TryPlanDeparture(Mu, r1, v1, TargetAt(tof), tof, out DVector3 dvA, out string reasonA), reasonA);
            double missA = Simulate(r1, v1, dvA, tof, TargetAt);

            // B：中点修正（新）
            double tauA = dvA.magnitude / Acceleration;
            var midPos = PropagateOnly(r1, v1, tauA * 0.5, out DVector3 midVel);
            Assert.True(InterplanetaryTransferCore.TryPlanDeparture(Mu, midPos, midVel, TargetAt(tof), tof - tauA * 0.5, out DVector3 dvB, out string reasonB), reasonB);
            double missB = Simulate(r1, v1, dvB, tof, TargetAt);

            output.WriteLine($"A 冲量：Δv={dvA.magnitude / 1000.0:F0} km/s，燃时 {tauA / 3600.0:F2} h，落点差 {missA / 1e9:F4}e9 m（{missA / Soi:F2}×SOI）");
            output.WriteLine($"B 中点：Δv={dvB.magnitude / 1000.0:F0} km/s，燃时 {dvB.magnitude / Acceleration / 3600.0:F2} h，落点差 {missB / 1e9:F4}e9 m（{missB / Soi:F2}×SOI）");

            Assert.True(missB < missA, "中点修正应当改善落点");
            Assert.True(missB <= Soi, $"中点修正后仍差 {missB / 1e6:F1}e6 m，超过 SOI");
        }

        [Fact]
        public void ArrivalMidpointCorrection_StopsInsideSoi()
        {
            var r1 = new DVector3(2.0e10, 0, 0);
            double circular = Math.Sqrt(Mu / 2.0e10);
            var v1 = new DVector3(0, circular, 0);
            const double tof = 2.0 * 86400.0;
            double omega = Math.Sqrt(Mu / Math.Pow(2.4e10, 3));
            DVector3 TargetAt(double t) => new(2.4e10 * Math.Cos(0.5 + omega * t), 2.4e10 * Math.Sin(0.5 + omega * t), 0);

            double StopDistance(bool shiftAim)
            {
                // 出航段：中点修正
                Assert.True(InterplanetaryTransferCore.TryPlanDeparture(Mu, r1, v1, TargetAt(tof), tof, out DVector3 dv, out _));
                double tauDep = dv.magnitude / Acceleration;
                var depMidPos = PropagateOnly(r1, v1, tauDep * 0.5, out DVector3 depMidVel);
                Assert.True(InterplanetaryTransferCore.TryPlanDeparture(Mu, depMidPos, depMidVel, TargetAt(tof),
                    tof - tauDep * 0.5, out dv, out _));

                // 到达段
                double tau1 = dv.magnitude / Acceleration;
                var midPos = PropagateOnly(r1, v1, tau1 * 0.5, out DVector3 midVel);
                double remaining = tof - tau1 * 0.5;
                DVector3 arrVel = PropagateOnly(midPos, midVel + dv, remaining, out _).magnitude > 0
                    ? VelocityAt(midPos, midVel + dv, remaining) : midVel;
                DVector3 arrDv = VelocityAt(new DVector3(2.4e10, 0, 0), DVector3.zero, 0) * 0 + (TargetVelocity(tof) - arrVel);
                double tau2 = arrDv.magnitude / Acceleration;

                if (shiftAim)
                {
                    DVector3 relDir = arrDv * (-1.0 / arrDv.magnitude);
                    DVector3 shifted = TargetAt(tof) + relDir * (arrDv.magnitude * tau2 * 0.5);
                    Assert.True(InterplanetaryTransferCore.TryPlanDeparture(Mu, midPos, midVel, shifted, remaining, out dv, out _));
                    tau1 = dv.magnitude / Acceleration;
                    midPos = PropagateOnly(r1, v1, tau1 * 0.5, out midVel);
                    remaining = tof - tau1 * 0.5;
                    arrVel = VelocityAt(midPos, midVel + dv, remaining);
                    arrDv = TargetVelocity(tof) - arrVel;
                    tau2 = arrDv.magnitude / Acceleration;
                }

                // 重放：烧 dv（τ1）→ 滑行 → 到达前 τ2 反推 → 到 tof
                var position = r1; var velocity = v1;
                const double step = 10.0;
                double burn1End = tau1, burn2Start = tof - tau2;
                DVector3 dir1 = dv * (1.0 / dv.magnitude);
                DVector3 dir2 = arrDv * (1.0 / arrDv.magnitude);
                for (double t = 0; t < tof; t += step)
                {
                    DVector3 accel = Gravity(position);
                    if (t < burn1End) accel = accel + dir1 * Acceleration;
                    else if (t >= burn2Start) accel = accel + dir2 * Acceleration;
                    velocity = velocity + accel * step;
                    position = position + velocity * step;
                }
                return (TargetAt(tof) - position).magnitude;
            }

            double noShift = StopDistance(false);
            double shifted = StopDistance(true);
            output.WriteLine($"不修正瞄准点：{noShift / 1e9:F4}e9 m（{noShift / Soi:F2}×SOI）");
            output.WriteLine($"修正瞄准点：  {shifted / 1e9:F4}e9 m（{shifted / Soi:F2}×SOI）");
            Assert.True(shifted < noShift, "到达段修正应缩短停点距离");
            Assert.True(shifted <= Soi, $"仍差 {shifted / 1e6:F1}e6 m，超过 SOI");
        }

        private static DVector3 VelocityAt(DVector3 r, DVector3 v, double seconds)
        {
            const double step = 10.0;
            for (double t = 0; t < seconds; t += step)
            {
                v = v + Gravity(r) * step;
                r = r + v * step;
            }
            return v;
        }

        private static DVector3 TargetVelocity(double t)
        {
            double omega = Math.Sqrt(Mu / Math.Pow(2.4e10, 3));
            double angle = 0.5 + omega * t;
            double speed = Math.Sqrt(Mu / 2.4e10);
            return new DVector3(-speed * Math.Sin(angle), speed * Math.Cos(angle), 0);
        }
        /// <summary>按 Δv 烧 τ=|Δv|/a 秒，再滑行到 tof，返回与目标位置的距离。</summary>
        private static double Simulate(DVector3 r, DVector3 v, DVector3 dv, double tof, Func<double, DVector3> targetAt)
        {
            double tau = dv.magnitude / Acceleration;
            DVector3 direction = dv * (1.0 / dv.magnitude);
            DVector3 position = r;
            DVector3 velocity = v;
            double step = 10.0;
            for (double t = 0; t < tau; t += step)
            {
                DVector3 accel = Gravity(position) + direction * Acceleration;
                velocity = velocity + accel * step;
                position = position + velocity * step;
            }
            for (double t = tau; t < tof; t += step)
            {
                velocity = velocity + Gravity(position) * step;
                position = position + velocity * step;
            }
            return (targetAt(tof) - position).magnitude;
        }

        private static DVector3 PropagateOnly(DVector3 r, DVector3 v, double seconds, out DVector3 endVelocity)
        {
            const double step = 10.0;
            for (double t = 0; t < seconds; t += step)
            {
                v = v + Gravity(r) * step;
                r = r + v * step;
            }
            endVelocity = v;
            return r;
        }

        private static DVector3 Gravity(DVector3 position)
        {
            double radius = position.magnitude;
            return position * (-Mu / (radius * radius * radius));
        }
    }
}
