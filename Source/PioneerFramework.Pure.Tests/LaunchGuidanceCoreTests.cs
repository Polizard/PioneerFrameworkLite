using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 爬升段制导核心单测（设计 §7）：收敛性 / 稳定性 / 预算 / 多方角。
    /// 仿真模型 = **有限推力闭环**（2026-09-02 修正）：每步 ComputeStep → 施加 a=F/m·dt
    /// （沿 dv 方向，受剩余工质约束，质量随烧耗递减），再用 NBodyIntegrator
    /// ThirdOrderTaylorStep 积分重力（acc=-GM/r³·pos, dt=2s）。
    /// 与游戏 ComputeGroupPhysics 行为一致——旧的「理想冲量一步施加完整 dv」掩盖了低 TWR 失效。
    /// </summary>
    public class LaunchGuidanceCoreTests
    {
        private const double DayLengthTicks = 60000.0; // CelestialBodyDef.dayLengthTicks 默认

        // 与 LaunchTestScenarios 强力大火箭一致（F=1.05MN, mf=300, dry=4000, fuel=50000, TWR≈2.43）
        private const double Thrust = 1050000.0;
        private const double MassFlow = 300.0;
        private const double DryMass = 4000.0;
        private const double FuelMass = 50000.0;
        private const double Dt = 2.0;

        private struct AscentResult
        {
            public double finalR;
            public double finalV;
            public double maxRadialSpeed;
            public double maxR;
            public double totalDv;
            public double fuelUsed;
            public double fuelRemaining;
            public double arrivalTime;
            public bool arrived;
            public bool fuelExhausted;
        }

        /// <summary>赤道发射初始状态：pos = r(cosλ,0,sinλ)，vel = 自转初速 = ω·(p×up)（真实自转切向，与 progradeSign=−1 同向）。</summary>
        private static (DVector3 pos, DVector3 vel) EquatorialLaunchState(double lambdaRad)
        {
            double R = SpaceConstants.PlanetRadiusMeters;
            double omega = 2.0 * Math.PI / DayLengthTicks;
            DVector3 pos = new DVector3(Math.Cos(lambdaRad) * R, 0, Math.Sin(lambdaRad) * R);
            DVector3 vel = DVector3.Cross(pos, DVector3.up * omega); // p×up·ω（2026-09-05 自转方向修正）
            return (pos, vel);
        }

        /// <summary>
        /// 高纬发射初始状态：p = r(cosφ·cosλ, sinφ, cosφ·sinλ)，v = ω·(p×up)。
        /// 与 EquatorialLaunchState 同构（纬度 φ=0 即退化为赤道）。用于复现「高纬殖民地 + 70000kg」游戏内失效。
        /// </summary>
        private static (DVector3 pos, DVector3 vel) SurfaceLaunchState(double latRad, double lambdaRad)
        {
            double R = SpaceConstants.PlanetRadiusMeters;
            double omega = 2.0 * Math.PI / DayLengthTicks;
            double cosLat = Math.Cos(latRad);
            DVector3 pos = new DVector3(cosLat * Math.Cos(lambdaRad) * R, Math.Sin(latRad) * R, cosLat * Math.Sin(lambdaRad) * R);
            DVector3 vel = DVector3.Cross(pos, DVector3.up * omega); // p×up·ω（真实自转切向，2026-09-05 修正）
            return (pos, vel);
        }

        /// <summary>
        /// 有限推力闭环仿真：每步 ComputeStep → 沿 dv 方向施加 a·dt（质量递减），积分重力。
        /// 与 ComputeGroupPhysics 的 burn 分支一致（dvThisTick=a·dt, burnDv=min(..., remaining)，
        /// 每步重新 StartBurn 重置 remaining → 实际每步施加 a·dt）。
        /// </summary>
        private static AscentResult RunAscent(DVector3 pos0, DVector3 vel0, double gm,
            in LaunchGuidanceParams p, double thrust = Thrust, double massFlow = MassFlow,
            double dryMass = DryMass, double fuelMass = FuelMass, int maxSteps = 40000)
        {
            DVector3 pos = pos0;
            DVector3 vel = vel0;
            double mass = dryMass + fuelMass;
            double fuel = fuelMass;
            double maxRadialSpeed = 0.0, maxR = 0.0, totalDv = 0.0, fuelUsed = 0.0;
            var result = new AscentResult { arrivalTime = -1.0 };

            for (int i = 0; i < maxSteps && !result.arrived; i++)
            {
                var step = LaunchGuidanceCore.ComputeStep(pos, vel, gm, p);
                if (step.arrived)
                {
                    result.arrived = true;
                    result.arrivalTime = i * Dt;
                }

                // 有限推力：质量守恒、沿 dv 方向施加速度增量（受工质约束）
                if (fuel > 0.0 && step.dv.sqrMagnitude > 1e-9)
                {
                    double a = thrust / Math.Max(mass, 1.0);          // 当前加速度（质量递减 → 增大）
                    double dvMag = Math.Min(a * Dt, step.dv.magnitude);
                    vel = vel + step.dv.normalized * dvMag;
                    totalDv += dvMag;
                    double used = massFlow * Dt * (a * Dt > 0.0 ? dvMag / (a * Dt) : 0.0);
                    used = Math.Min(used, fuel);
                    fuel -= used;
                    mass -= used;
                    fuelUsed += used;
                    if (fuel <= 1e-6)
                        result.fuelExhausted = true;
                }

                double r = pos.magnitude;
                DVector3 acc = pos * (-gm / (r * r * r)); // 向心引力，jerk = 0
                NBodyIntegrator.ThirdOrderTaylorStep(pos, vel, acc, DVector3.zero, Dt, out pos, out vel);

                double radialSpeed = DVector3.Dot(vel, pos.normalized);
                maxRadialSpeed = Math.Max(maxRadialSpeed, Math.Abs(radialSpeed));
                maxR = Math.Max(maxR, pos.magnitude);
            }

            result.finalR = pos.magnitude;
            result.finalV = vel.magnitude;
            result.maxRadialSpeed = maxRadialSpeed;
            result.maxR = maxR;
            result.totalDv = totalDv;
            result.fuelUsed = fuelUsed;
            result.fuelRemaining = fuel;
            return result;
        }

        // ── 收敛性：有限推力下不同发射方位角（经度）都收敛到目标窗口 ──
        [Theory]
        [InlineData(0.0)]
        [InlineData(45.0)]
        [InlineData(90.0)]
        [InlineData(180.0)]
        [InlineData(270.0)]
        public void Ascent_Converges_ForEachAzimuth(double lambdaDeg)
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            double vC = Math.Sqrt(gm / p.rTarget);
            var (pos0, vel0) = EquatorialLaunchState(lambdaDeg * Math.PI / 180.0);

            var result = RunAscent(pos0, vel0, gm, p);

            Assert.True(result.arrived, "有限推力下爬升应在预算内到达目标窗口");
            Assert.False(result.fuelExhausted, "不应在到达前耗尽工质");
            Assert.True(result.fuelRemaining > 2000.0,
                $"到达时工质余量不足: {result.fuelRemaining:F0}kg（需 >2000kg 供游戏内物理微差 + 后续机动）");
            Assert.InRange(result.finalR, p.rTarget - p.tolR, p.rTarget + p.tolR);
            Assert.InRange(result.finalV, vC - p.tolV, vC + p.tolV);
            Assert.True(result.arrivalTime > 0.0 && result.arrivalTime < 20000.0,
                $"到达时间异常: {result.arrivalTime}s");
        }

        // ── 稳定性：不过冲、径向速度受竖直段/收拢速度约束 ──
        [Fact]
        public void Ascent_NoOvershoot_AndRadialSpeedBounded()
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            var (pos0, vel0) = EquatorialLaunchState(0.0);

            var result = RunAscent(pos0, vel0, gm, p);

            Assert.True(result.arrived);
            Assert.True(result.maxR < p.rTarget + 5.0 * p.tolR,
                $"半径过冲: maxR={result.maxR:F0} > rTarget+50km={p.rTarget + 5.0 * p.tolR:F0}");
            double radialLimit = Math.Max(p.vRadMax, p.vAscent) + 1.0;
            Assert.True(result.maxRadialSpeed <= radialLimit,
                $"径向速度超上限: {result.maxRadialSpeed:F2} m/s > {radialLimit:F2}");
        }

        // ── **高纬收敛性**（2026-10-06 修：切向基准必须在轨道面内）──────────────────────────
        // 旧律的切向基准 = 全局东方 `Cross(up, r̂)`：赤道时恰在轨道面内，**倾斜轨道上会漂出面** ⇒
        // 每步命令一个面外修正 ⇒ 燃料烧在改面上。游戏内实测（φ=51.7°、50 t）：烧干、到不了 5500 km
        // （apo≈5351 km）；台架同步复现（耗尽 197 km / v=6042 / apo 5368）。
        // 本用例即那条缺陷的**回归判据**（既有用例全在赤道，照不出它）。
        [Theory]
        [InlineData(0.0)]
        [InlineData(30.0)]
        [InlineData(51.7)]   // 游戏内实测发射场纬度
        [InlineData(70.0)]
        public void Ascent_Converges_AtHighLatitude(double latDeg)
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            double vC = Math.Sqrt(gm / p.rTarget);
            var (pos0, vel0) = SurfaceLaunchState(latDeg * Math.PI / 180.0, 0.0);

            var result = RunAscent(pos0, vel0, gm, p);

            Assert.True(result.arrived, $"高纬 {latDeg}° 应到达（切向基准必须面内）");
            Assert.False(result.fuelExhausted, "不应在到达前耗尽工质");
            Assert.True(result.fuelRemaining > 2000.0,
                $"到达时工质余量不足: {result.fuelRemaining:F0}kg（需 >2000kg）");
            Assert.InRange(result.finalR, p.rTarget - p.tolR, p.rTarget + p.tolR);
            Assert.InRange(result.finalV, vC - p.tolV, vC + p.tolV);
        }

        // ── 预算：有限推力闭环总耗 ΔV 应在 BurnCore 估算范围内 ──
        [Fact]
        public void Budget_FiniteThrust_ConsumptionWithinEstimate()
        {
            double gm = SpaceConstants.PrimaryGravitationalParameter;
            var p = LaunchGuidanceParams.CreateDefault(SpaceConstants.PlanetRadiusMeters);
            var (pos0, vel0) = EquatorialLaunchState(0.0);

            var result = RunAscent(pos0, vel0, gm, p);

            Assert.True(result.arrived, "有限推力应能收敛");
            Assert.False(result.fuelExhausted, "工质应在到达后才耗尽");
            // ΔV 消耗 ≤ 全部可用工质对应的 ΔV（质量比），留 5% 余量
            double vTotal = Thrust / MassFlow * Math.Log((DryMass + FuelMass) / DryMass);
            Assert.True(result.totalDv < vTotal * 0.95,
                $"实际消耗 ΔV {result.totalDv:F0} 超预算 {vTotal:F0}×0.95");
        }

        // ── 预算：大储罐网络（50t）可行性（设计 §4.1，保留原评估）──
        [Fact]
        public void Budget_LargeTankNetwork_Feasible()
        {
            const float totalDv = 9000f;
            const float dryMass = 2000f;
            var networks = new System.Collections.Generic.List<BurnNetworkInput>
            {
                new BurnNetworkInput(thrust: 70000f, massFlow: 20f, ve: 3500f, availableMass: 50000f)
            };
            var est = BurnCore.Estimate(totalDv, dryMass, networks);
            Assert.True(est.Feasible, "50t 工质化学网络应满足 9000 m/s 预算");
            Assert.True(est.RequiredPropellant < 50000f);
        }

        [Fact]
        public void Budget_DebugSmallTank_Infeasible()
        {
            const float totalDv = 9000f;
            const float dryMass = 1000f;
            var networks = new System.Collections.Generic.List<BurnNetworkInput>
            {
                new BurnNetworkInput(thrust: 70000f, massFlow: 20f, ve: 3500f, availableMass: 1000f)
            };
            var est = BurnCore.Estimate(totalDv, dryMass, networks);
            Assert.False(est.Feasible, "1t 工质化学网络不可能满足 9000 m/s");
        }
    }
}
