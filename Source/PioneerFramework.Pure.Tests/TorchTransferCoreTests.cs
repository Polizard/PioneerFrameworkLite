using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 火炬弹道纯核测试（2026-11-18，用户定稿：聚变 ve=1000 km/s、自动翻转、加速度为控制量）。
    /// 钉住：半程时间/总 Δv 的闭式、Δv 随 √a 增长、质量比与可行性、非法输入。
    /// </summary>
    public class TorchTransferCoreTests
    {
        private readonly ITestOutputHelper output;
        public TorchTransferCoreTests(ITestOutputHelper output) => this.output = output;

        private const double Distance = 2.0e10;      // Rimata → 赭砂 的实距量级
        private const double FusionVe = 1_000_000;   // 1000 km/s

        [Fact]
        public void HalfTimeAndDeltaV_MatchTheBrachistochroneClosedForm()
        {
            const double acceleration = 1.0 * TorchTransferCore.StandardGravity;
            double half = TorchTransferCore.HalfTime(Distance, acceleration);
            double dv = TorchTransferCore.TotalDeltaV(Distance, acceleration);

            output.WriteLine($"1 g：半程 {half / 86400.0:F3} d，总时长 {2 * half / 86400.0:F3} d，Δv {dv / 1000.0:F0} km/s");
            Assert.Equal(Math.Sqrt(Distance / acceleration), half, 6);
            Assert.Equal(2.0 * acceleration * half, dv, 3);
            Assert.InRange(2 * half / 86400.0, 1.0, 1.1);   // ≈1.05 天
        }

        [Fact]
        public void ShorterTimeCostsMoreDeltaV_BySqrtOfAcceleration()
        {
            double dv1 = TorchTransferCore.TotalDeltaV(Distance, 1.0 * TorchTransferCore.StandardGravity);
            double dv3 = TorchTransferCore.TotalDeltaV(Distance, 3.0 * TorchTransferCore.StandardGravity);
            output.WriteLine($"3 g / 1 g 的 Δv 比 = {dv3 / dv1:F3}（应 = √3 ≈ 1.732）");
            Assert.Equal(Math.Sqrt(3.0), dv3 / dv1, 9);
        }

        [Fact]
        public void FusionExhaustMakesOneGFeasibleUnderTheMassRatioCap()
        {
            Assert.True(TorchTransferCore.TrySolve(Distance, 1.0, FusionVe, out double half, out double dv, out double ratio, out string reason), reason);
            output.WriteLine($"1 g @ve=1000 km/s：质量比 {ratio:F2}（上限 {TorchTransferCore.MaximumMassRatio:F0}）");
            Assert.InRange(ratio, 2.0, 3.0);
            Assert.True(ratio < TorchTransferCore.MaximumMassRatio);
            Assert.True(half > 0 && dv > 0);
        }

        [Fact]
        public void ChemicalExhaustCannotDoOneG()
        {
            Assert.True(TorchTransferCore.TrySolve(Distance, 1.0, 9_000, out _, out _, out double ratio, out _));
            output.WriteLine($"1 g @ve=9 km/s：质量比 {ratio:E2}");
            Assert.True(ratio > TorchTransferCore.MaximumMassRatio, "化学引擎不该能跑 1 g 火炬");
        }

        [Fact]
        public void TrySolve_RejectsInvalidInputs()
        {
            Assert.False(TorchTransferCore.TrySolve(0.0, 1.0, FusionVe, out _, out _, out _, out string reason));
            Assert.Equal("距离无效", reason);

            Assert.False(TorchTransferCore.TrySolve(Distance, 0.0, FusionVe, out _, out _, out _, out reason));
            Assert.Equal("加速度无效", reason);

            Assert.False(TorchTransferCore.TrySolve(Distance, 1.0, 0.0, out _, out _, out _, out reason));
            Assert.Equal("排气速度无效", reason);
        }

        [Fact]
        public void MassRatio_IsInfiniteWithoutExhaustVelocity()
        {
            Assert.True(double.IsPositiveInfinity(TorchTransferCore.MassRatio(1e6, 0.0)));
            Assert.Equal(Math.E, TorchTransferCore.MassRatio(1.0, 1.0), 12);
            Assert.Equal(0.0, TorchTransferCore.HalfTime(-1.0, 9.8));
            Assert.Equal(0.0, TorchTransferCore.TotalDeltaV(Distance, 0.0));
        }

        [Fact]
        public void RealThrustIntegration_IsFasterAndCostsMoreDeltaVThanTheClosedForm()
        {
            // 干重 5 t + 工质 12 t，ve=1e6，ṁ = m₀·1g/ve ⇒ 起始正好 1 g，之后随质量下降上升。
            double massFlow = 17_000.0 * TorchTransferCore.StandardGravity / FusionVe;
            double thrust = FusionVe * massFlow;
            Assert.True(TorchTransferCore.TrySimulateConstantThrust(Distance, 5_000, 12_000, thrust, massFlow,
                out double half, out double halfDeltaV, out string reason), reason);

            double closedHalf = TorchTransferCore.HalfTime(Distance, TorchTransferCore.StandardGravity);
            double closedTotal = TorchTransferCore.TotalDeltaV(Distance, TorchTransferCore.StandardGravity);
            output.WriteLine($"积分半程 {half / 86400.0:F3} d（闭式 {closedHalf / 86400.0:F3} d）"
                + $"｜积分总 Δv {2 * halfDeltaV / 1000.0:F0} km/s（闭式 {closedTotal / 1000.0:F0} km/s）");
            // 恒定推力下加速度随质量下降上升 ⇒ 半程更早走完，但峰值速度更高 ⇒ 总 Δv 更大。
            Assert.True(half < closedHalf, "真实半程时间应短于恒加速闭式（加速度在上升）");
            Assert.True(2 * halfDeltaV > closedTotal, "真实总 Δv 应大于恒加速闭式");
        }

        [Fact]
        public void RealThrustIntegration_ReportsInsufficientPropellant()
        {
            double massFlow = 17_000.0 * TorchTransferCore.StandardGravity / FusionVe;
            double thrust = FusionVe * massFlow;
            Assert.False(TorchTransferCore.TrySimulateConstantThrust(Distance, 5_000, 100, thrust, massFlow,
                out _, out _, out string reason));
            Assert.Equal("工质不足以走过半程", reason);
        }

        [Fact]
        public void BangBang_SolvesTheFlipMassAnalytically()
        {
            const double span = 2.0e10;
            double massFlow = 45_000.0 * TorchTransferCore.StandardGravity / FusionVe;
            Assert.True(TorchTransferCore.TrySolveBangBang(span, 5_000, 40_000, FusionVe, massFlow,
                out double accel, out double decel, out double halfDeltaV, out double massAtFlip, out string reason), reason);

            double massFinal = massAtFlip * massAtFlip / 45_000.0;
            output.WriteLine($"加速 {accel / 86400.0:F3} d／减速 {decel / 86400.0:F3} d"
                + $"｜翻转质量 {massAtFlip:F0} kg、终点质量 {massFinal:F0} kg｜半程 Δv {halfDeltaV / 1000.0:F0} km/s");
            Assert.True(massFinal > 5_000.0 - 1e-6, "终点质量不能低于干重");
            Assert.True(accel > decel, "质量变轻 ⇒ 减速段更短（这正是数值对称解差 10% 的原因）");
            Assert.Equal(FusionVe * Math.Log(45_000.0 / massAtFlip), halfDeltaV, 6);
        }

        [Fact]
        public void BangBang_ReportsInsufficientRange()
        {
            double massFlow = 5_000.0 * TorchTransferCore.StandardGravity / FusionVe;
            Assert.False(TorchTransferCore.TrySolveBangBang(2.0e10, 5_000, 100, FusionVe, massFlow,
                out _, out _, out _, out _, out string reason));
            output.WriteLine(reason);
            Assert.Contains("射程不足", reason);
        }

        [Fact]
        public void BangBang_ScalesRangeWithPropellant()
        {
            double massFlow = 45_000.0 * TorchTransferCore.StandardGravity / FusionVe;
            Assert.True(TorchTransferCore.TrySolveBangBang(1.0e10, 5_000, 40_000, FusionVe, massFlow,
                out double smallAccel, out _, out double smallDv, out _, out _));
            Assert.True(TorchTransferCore.TrySolveBangBang(2.0e10, 5_000, 40_000, FusionVe, massFlow,
                out double largeAccel, out _, out double largeDv, out _, out _));
            output.WriteLine($"1e10: {smallDv / 1000.0:F0} km/s｜2e10: {largeDv / 1000.0:F0} km/s");
            Assert.True(largeDv > smallDv, "距离翻倍 ⇒ Δv 单调上升");
            Assert.True(largeAccel > smallAccel);
        }
        [Fact]
        public void GravityDisplacement_PointsAtTheStarAndScalesWithTimeSquared()
        {
            const double mu = 1.327e20;
            var ship = new DVector3(2.0e10, 0, 0);
            var aim = new DVector3(2.0e10, 1.0e10, 0);   // 中点 (2e10, 0.5e10)：远离恒星，非退化

            DVector3 sag = TorchTransferCore.GravityDisplacement(ship, aim, mu, 80_000.0);
            output.WriteLine($"引力位移 {sag.magnitude / 1e6:F1}e6 m（方向 x={sag.x / 1e9:F3}e9）");

            Assert.True(sag.magnitude > 0.0);
            Assert.True(sag.x < 0.0 || Math.Abs(sag.x) < 1e-6, "中点若在恒星另一侧，引力应指向恒星");

            // 时间翻倍 ⇒ 位移 ×4（½·g·T²）。
            DVector3 doubleSag = TorchTransferCore.GravityDisplacement(ship, aim, mu, 160_000.0);
            Assert.Equal(4.0, doubleSag.magnitude / sag.magnitude, 6);

            // 无引力 ⇒ 零位移。
            Assert.Equal(0.0, TorchTransferCore.GravityDisplacement(ship, aim, 0.0, 80_000.0).magnitude);
        }
        [Fact]
        public void AlongLineSolve_DegeneratesToClassicBangBangWhenStartingAtRest()
        {
            const double span = 2.0e10;
            const double acceleration = 9.80665;
            Assert.True(TorchTransferCore.TrySolveAlongLine(span, 0.0, acceleration,
                out double accel, out double decel, out double dv1, out double dv2, out string reason), reason);
            output.WriteLine($"u₀=0：加速 {accel / 86400.0:F3} d、减速 {decel / 86400.0:F3} d｜Δv {dv1 / 1000.0:F0}/{dv2 / 1000.0:F0} km/s");
            Assert.Equal(Math.Sqrt(span / acceleration), accel, 6);
            Assert.Equal(accel, decel, 6);
            Assert.Equal(dv1, dv2, 6);
        }

        [Fact]
        public void AlongLineSolve_MakesTheBrakingPhaseLongerWhenAlreadyMoving()
        {
            const double span = 2.0e10;
            const double acceleration = 9.80665;
            const double along = 30_000.0;   // 沿航迹 30 km/s
            Assert.True(TorchTransferCore.TrySolveAlongLine(span, along, acceleration,
                out double accel, out double decel, out double dv1, out double dv2, out string reason), reason);
            output.WriteLine($"u₀=30 km/s：加速 {accel / 86400.0:F3} d、减速 {decel / 86400.0:F3} d"
                + $"｜Δv {dv1 / 1000.0:F0}/{dv2 / 1000.0:F0} km/s");
            Assert.True(decel > accel, "已有正向初速 ⇒ 减速段必须更长");
            Assert.True(dv2 > dv1);
            Assert.Equal(decel - accel, along / acceleration, 6);
        }

        [Fact]
        public void AlongLineSolve_AcceptsMovingAwayWithShorterBraking()
        {
            // u₀<0（正在远离目标）：减速段应比加速段短，且必须被接受。
            const double span = 2.0e10;
            const double acceleration = 9.80665;
            Assert.True(TorchTransferCore.TrySolveAlongLine(span, -30_000.0, acceleration,
                out double accel, out double decel, out double dv1, out double dv2, out string reason), reason);
            output.WriteLine($"u₀=−30 km/s：加速 {accel / 86400.0:F3} d、减速 {decel / 86400.0:F3} d"
                + $"｜Δv {dv1 / 1000.0:F0}/{dv2 / 1000.0:F0} km/s");
            Assert.True(decel < accel, "正在远离 ⇒ 减速段更短");
            Assert.Equal(accel - decel, 30_000.0 / acceleration, 6);
        }

        [Fact]
        public void AlongLineSolve_RejectsWhenInitialSpeedIsTooHighForTheDistance()
        {
            // 距离很小、初速很大 ⇒ t₁ 会解成负值（必须先减速）。
            Assert.False(TorchTransferCore.TrySolveAlongLine(1.0e6, 200_000.0, 9.80665,
                out _, out _, out _, out _, out string reason));
            output.WriteLine(reason);
            Assert.Contains("过大", reason);
        }
        [Theory]
        [InlineData(0.0)]
        [InlineData(30_000.0)]
        [InlineData(-30_000.0)]
        [InlineData(120_000.0)]
        public void AlongLinePlan_ActuallyArrivesAtTheTarget(double alongVelocity)
        {
            const double span = 2.0e10;
            const double acceleration = 9.80665;
            Assert.True(TorchTransferCore.TrySolveAlongLine(span, alongVelocity, acceleration,
                out double t1, out double t2, out _, out _, out string reason), reason);

            // 用数值积分重放同一段计划：加速 +A 持续 t1，再 −A 持续 t2。
            // 步长 0.5 s 的欧拉积分本身有 O(A·h) 误差（末速 ~5 m/s、位置 ~2e-5 相对），
            // 所以判据取 1e-3 相对 / 20 m/s —— 这已经足以判死代数错误（初版差 −24%）。
            double step = 0.5;
            double position = 0.0, velocity = alongVelocity;
            for (double t = 0; t < t1; t += step)
            {
                velocity += acceleration * step;
                position += velocity * step;
            }
            for (double t = 0; t < t2; t += step)
            {
                velocity -= acceleration * step;
                position += velocity * step;
            }

            output.WriteLine($"u₀={alongVelocity / 1000.0:F1} km/s：t₁={t1:F0}s、t₂={t2:F0}s"
                + $"｜终点 {position / 1e9:F4}e9 m（目标 {span / 1e9:F4}e9）｜末速 {velocity:F2} m/s");
            Assert.True(Math.Abs(position - span) <= span * 1e-3,
                $"终点 {position:F0} m 与目标 {span:F0} m 的相对误差应 ≤ 1e-3");
            Assert.True(Math.Abs(velocity) < 20.0, $"末速应归零，实为 {velocity:F3} m/s");
        }
    }
}
