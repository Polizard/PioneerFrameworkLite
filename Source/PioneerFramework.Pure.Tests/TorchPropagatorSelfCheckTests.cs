using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **传播器自检**（2026-11-19）：这是最基本的判据——没有它，后面所有“捕获验证”都是噪声。
    /// 判据：无推力时，圆轨道跑整整一圈必须回到起点（解析答案已知）；
    ///      同时检查能量漂移。若这项不过，任何“ε>0”的结论都不可信。
    /// </summary>
    public class TorchPropagatorSelfCheckTests
    {
        private readonly ITestOutputHelper output;
        public TorchPropagatorSelfCheckTests(ITestOutputHelper output) => this.output = output;

        private const double MuStar = 2.4369393583e19;
        private const double Radius = 2.0e10;

        [Fact]
        public void ZeroThrust_CircularOrbit_ClosesAfterOnePeriod()
        {
            var position = new DVector3(Radius, 0, 0);
            double speed = Math.Sqrt(MuStar / Radius);
            var velocity = new DVector3(0, speed, 0);
            double period = 2.0 * Math.PI * Math.Sqrt(Radius * Radius * Radius / MuStar);

            // 无推力计划；目标放在极远处，避免目标引力参与
            var plan = default(TorchExecutionPlan);
            (DVector3 Position, DVector3 Velocity) targetAt(double t) => (new DVector3(1e15, 0, 0), DVector3.zero);

            double startEnergy = velocity.magnitude * velocity.magnitude / 2.0 - MuStar / position.magnitude;

            TorchExecutionCore.Propagate(ref position, ref velocity, 0.0, period, plan, targetAt,
                MuStar, 1.0e6, 1.0e6, 0.0);

            double closure = (position - new DVector3(Radius, 0, 0)).magnitude / Radius;
            double endEnergy = velocity.magnitude * velocity.magnitude / 2.0 - MuStar / position.magnitude;
            double drift = (endEnergy - startEnergy) / Math.Abs(startEnergy);

            output.WriteLine($"跑满一圈 {period / 3600.0:F2} h：闭合误差 {closure:E3}（相对半径）｜能量漂移 {drift:P3}"
                + $"｜末速度 {velocity.magnitude / 1000.0:F2} km/s（初速 {speed / 1000.0:F2}）");

            Assert.True(closure < 1e-3, $"圆轨道跑一圈回不到起点：相对误差 {closure:E3}");
            Assert.True(Math.Abs(drift) < 1e-3, $"能量漂移 {drift:P3}");
        }
    }
}
