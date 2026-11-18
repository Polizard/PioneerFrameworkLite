using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **火炬 = Lambert 圆锥 + 长烧** 的离线验证（2026-11-18 重构后）。
    ///
    /// 直线模型已被证伪（无引力、目标不动仍差 1.28e9 m）。新方案复用已测过的
    /// `InterplanetaryTransferCore`（Lambert，11 条纯核全绿），唯一的**新**误差是
    /// "长烧 ≠ 冲量"。这里直接量这个差：
    ///   A：在 t=0 施加 Δv 冲量，随后只在恒星引力下自由飞 T 秒；
    ///   B：沿同一方向以 a = Δv/τ 烧 τ 秒（τ = Δv/a），再自由飞 T−τ 秒。
    /// 判据：两者终点差 ≤ SOI 半径（1.325e8 m）。
    /// </summary>
    public class TorchFiniteBurnTests
    {
        private readonly ITestOutputHelper output;
        public TorchFiniteBurnTests(ITestOutputHelper output) => this.output = output;

        private const double Mu = 1.327e20;
        private const double SoiRadius = 1.325e8;

        [Theory]
        [InlineData(0.3)]
        [InlineData(1.0)]
        [InlineData(3.0)]
        public void FiniteBurnDivergesFromImpulseByLessThanSoi(double accelerationInG)
        {
            var position0 = new DVector3(2.0e10, 0, 0);
            double circular = Math.Sqrt(Mu / 2.0e10);
            var velocity0 = new DVector3(0, circular, 0);

            // 典型出航 Δv：3800 m/s，方向取"顺行 + 一点径向"，模拟 Lambert 解的形状
            var dvDirection = new DVector3(0.25, 0.968, 0).normalized;
            const double dvMagnitude = 3_800.0;
            double acceleration = accelerationInG * TorchTransferCore.StandardGravity;
            double burnSeconds = dvMagnitude / acceleration;
            const double coastSeconds = 10.0 * 86400.0;

            // A：冲量
            var a0 = position0;
            var v0 = velocity0 + dvDirection * dvMagnitude;
            (var posA, _) = Propagate(a0, v0, coastSeconds, DVector3.zero, 0.0);

            // B：长烧 + 剩余滑行
            (var posB, var velB) = Propagate(position0, velocity0, burnSeconds, dvDirection, acceleration);
            (var posB2, _) = Propagate(posB, velB, coastSeconds - burnSeconds, DVector3.zero, 0.0);

            double divergence = (posB2 - posA).magnitude;
            output.WriteLine($"{accelerationInG:F1} g：燃时 {burnSeconds:F0} s（{burnSeconds / 3600.0:F2} h）"
                + $"｜冲量 vs 长烧 终点差 {divergence / 1e6:F1}e6 m（SOI 的 {divergence / SoiRadius:F2} 倍）");

            Assert.True(divergence <= SoiRadius,
                $"{accelerationInG:F1} g 的长烧偏差 {divergence / 1e6:F1}e6 m 超过 SOI");
        }

        private static (DVector3 Position, DVector3 Velocity) Propagate(
            DVector3 position, DVector3 velocity, double seconds, DVector3 thrustDirection, double acceleration)
        {
            // 蛙跳（kick-drift-kick）：欧拉在 10 天弧上会累积远超物理量级的误差，
            // 上一版 3 g 那 13× SOI 就是积分器噪声，不是有限燃烧的真实偏差。
            const double step = 10.0;
            for (double t = 0; t < seconds; t += step)
            {
                double r0 = position.magnitude;
                DVector3 a0 = position * (-Mu / (r0 * r0 * r0)) + thrustDirection * acceleration;
                velocity = velocity + a0 * (0.5 * step);
                position = position + velocity * step;
                double r1 = position.magnitude;
                DVector3 a1 = position * (-Mu / (r1 * r1 * r1)) + thrustDirection * acceleration;
                velocity = velocity + a1 * (0.5 * step);
            }
            return (position, velocity);
        }
    }
}
