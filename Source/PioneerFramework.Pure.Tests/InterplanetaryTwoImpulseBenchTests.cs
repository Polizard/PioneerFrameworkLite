using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 行星际双脉冲极简台架（2026-11-03，只读台架 / 不碰生产后端）。
    ///
    /// 目的：先验证“最朴素的霍曼两冲量”能不能用，再谈规划器和执行器。
    /// 数据：燧阳 GM=2.4369393583e19；Rimata r=20,000,000 km；赭砂 r=2/3 Rimata。
    ///
    /// 只测四件事：
    ///   ① 霍曼两冲量的 Δv / TOF 与解析式一致；
    ///   ② 到达后第二发能圆化；
    ///   ③ 目标（赭砂）按相位运动，到达时刻正好在到达点；
    ///   ④ 出发 Δv 误差（1/10/20/67 m/s）传播 22.82 天后，落到赭砂 SOI 的哪个位置。
    ///
    /// ⚠ 这是纯数学台架：不验证点火执行器、不验证圆锥轨、不验证换系；那些是下一层。
    /// </summary>
    public class InterplanetaryTwoImpulseBenchTests
    {
        /// <summary>燧阳 GM（m³/s²）：0.183625541 M☉，由 a=20M km / T=60 游戏日反解。</summary>
        private const double Mu = 2.4369393583e19;
        /// <summary>Rimata 轨道半径（m）。</summary>
        private const double R1 = 20_000_000_000.0;
        /// <summary>赭砂轨道半径（m）= 2/3 Rimata。</summary>
        private const double R2 = R1 * 2.0 / 3.0;
        /// <summary>赭砂 SOI（m）：行星 GM=2.4e14、a=2/3 Rimata 时的 Laplace 近似。</summary>
        private const double SoiR2 = 132_521_195.3;
        private const double GameDaySeconds = 60_000.0;

        private readonly ITestOutputHelper output;

        public InterplanetaryTwoImpulseBenchTests(ITestOutputHelper output) => this.output = output;

        private static (DVector3 r, DVector3 v) Circular(double radius)
            => (new DVector3(radius, 0, 0), new DVector3(0, Math.Sqrt(Mu / radius), 0));

        [Fact]
        public void Hohmann_TwoImpulse_HitsZheSha_AndSurvivesDvError()
        {
            var (r1, v1) = Circular(R1);
            var res = HohmannCore.Compute(r1, v1, Mu, R2, new DVector3(0, 0, 1));

            Assert.True(res.Valid, res.Reason);

            double vc1 = Math.Sqrt(Mu / R1);
            double vc2 = Math.Sqrt(Mu / R2);
            double a = 0.5 * (R1 + R2);
            double vp = Math.Sqrt(Mu * (2.0 / R1 - 1.0 / a));
            double va = Math.Sqrt(Mu * (2.0 / R2 - 1.0 / a));

            output.WriteLine("=== 输入 ===");
            output.WriteLine($"GM={Mu:E6} m3/s2; R1={R1 / 1e9:F3}M km; R2={R2 / 1e9:F3}M km");
            output.WriteLine($"v1={vc1:F3} m/s; v2={vc2:F3} m/s; vp={vp:F3}; va={va:F3}");
            output.WriteLine("=== 两冲量 ===");
            output.WriteLine($"dv1={res.TanDv1:F3} m/s; dv2={res.TanDv2:F3} m/s; total={res.TotalDv:F3} m/s");
            output.WriteLine($"TOF={res.TransferSeconds:F1} s = {res.TransferSeconds / GameDaySeconds:F6} game days");
            output.WriteLine($"arrival |r|={res.ArrivalPos.magnitude / 1e9:F6}M km; |v|={res.ArrivalVel.magnitude:F3} m/s");

            Assert.Equal(vp - vc1, res.TanDv1, 6);
            Assert.Equal(vc2 - va, res.TanDv2, 6);
            Assert.Equal(Math.PI * Math.Sqrt(a * a * a / Mu), res.TransferSeconds, 3);
            Assert.True(Math.Abs(res.ArrivalPos.magnitude - R2) < 1.0,
                $"到达半径 {res.ArrivalPos.magnitude / 1e9:F6}M km，期望 {R2 / 1e9:F6}M km");
            Assert.True(Math.Abs(res.ArrivalVel.magnitude - va) < 1e-6,
                $"到达速度 {res.ArrivalVel.magnitude:F6}，期望 {va:F6}");

            // ② 到达后第二发把轨道圆化到 R2
            var ov = OrbitView.FromStateVectors(res.ArrivalPos, res.ArrivalVel + res.Dv2,
                Mu, res.TransferSeconds, DVector3.up, 1.3914e8);
            output.WriteLine($"=== 到达圆化 ===\na={ov.semiMajorAxis / 1e9:F6}M km; e={ov.eccentricity:E3}");
            Assert.True(Math.Abs(ov.semiMajorAxis - R2) / R2 < 1e-6,
                $"到达后半长轴 {ov.semiMajorAxis / 1e9:F6}M km，期望 {R2 / 1e9:F6}M km");
            Assert.True(Math.Abs(ov.eccentricity) < 1e-6, $"到达后应圆：e={ov.eccentricity:E3}");

            // ③ 目标按相位运动：出发相位 phi0 = pi - n2*tof，到达时应在 (-R2,0,0)
            double t2 = 2.0 * Math.PI * Math.Sqrt(R2 * R2 * R2 / Mu);
            double n2 = 2.0 * Math.PI / t2;
            double phi0 = Math.PI - n2 * res.TransferSeconds;
            var targetAtArrival = new DVector3(
                R2 * Math.Cos(phi0 + n2 * res.TransferSeconds),
                R2 * Math.Sin(phi0 + n2 * res.TransferSeconds), 0);
            double phaseMiss = (targetAtArrival - res.ArrivalPos).magnitude;
            output.WriteLine("=== 目标相位 ===");
            output.WriteLine($"T2={t2:F1} s = {t2 / GameDaySeconds:F6} game days; n2={n2:E6} rad/s");
            output.WriteLine($"phi0={phi0:F6} rad; target-at-arrival miss={phaseMiss:F3} m");
            Assert.True(phaseMiss < 1.0, $"目标相位到达点偏差 {phaseMiss:F3} m");

            // ④ 出发 Δv 误差 → 到达位置误差（切向/径向各一组）
            DVector3 vTransfer = v1 + res.Dv1;
            var errDirs = new[]
            {
                (name: "tan", dir: new DVector3(0, 1, 0)),
                (name: "rad", dir: new DVector3(1, 0, 0)),
            };
            output.WriteLine("=== 出发 Δv 误差 → 到达脱靶 ===");
            output.WriteLine($"SOI={SoiR2 / 1000.0:F1} km");
            foreach (var (name, dir) in errDirs)
            {
                foreach (double dvErr in new[] { 1.0, 10.0, 20.0, 67.0 })
                {
                    var rArr = OrbitMath.GetPositionAtTimeDelta(r1, vTransfer + dir * dvErr, res.TransferSeconds, Mu);
                    double miss = (rArr - res.ArrivalPos).magnitude;
                    output.WriteLine($"{name} dvErr={dvErr:F1} m/s -> miss={miss / 1000.0:F1} km = {miss / SoiR2 * 100.0:F1}% SOI");
                }
            }

            // 把台架从“只打印”收紧成“判容差”：
            //   实测（2026-11-03）：切向 10 m/s→32.8% SOI、20 m/s→65.6%、67 m/s→219%；
            //   径向约为切向的 1/2（67 m/s→108%）。
            //   ⇒ “67 m/s 满 SOI”是线性估计，实际切向容差约 30 m/s 满 SOI / 10 m/s 半 SOI。
            double miss10Tan = (OrbitMath.GetPositionAtTimeDelta(r1, vTransfer + new DVector3(0, 10.0, 0), res.TransferSeconds, Mu) - res.ArrivalPos).magnitude;
            double miss20Tan = (OrbitMath.GetPositionAtTimeDelta(r1, vTransfer + new DVector3(0, 20.0, 0), res.TransferSeconds, Mu) - res.ArrivalPos).magnitude;
            double miss20Rad = (OrbitMath.GetPositionAtTimeDelta(r1, vTransfer + new DVector3(20.0, 0, 0), res.TransferSeconds, Mu) - res.ArrivalPos).magnitude;
            double miss67Tan = (OrbitMath.GetPositionAtTimeDelta(r1, vTransfer + new DVector3(0, 67.0, 0), res.TransferSeconds, Mu) - res.ArrivalPos).magnitude;
            output.WriteLine($"bench envelope: tan 10={miss10Tan / SoiR2 * 100.0:F1}% SOI; " +
                $"tan 20={miss20Tan / SoiR2 * 100.0:F1}%; tan 67={miss67Tan / SoiR2 * 100.0:F1}%; " +
                $"rad 20={miss20Rad / SoiR2 * 100.0:F1}%");
            Assert.True(miss10Tan < 0.5 * SoiR2,
                $"切向 10 m/s 应在半个 SOI 内，实际 {miss10Tan / SoiR2 * 100.0:F1}%");
            Assert.True(miss20Tan > 0.5 * SoiR2,
                $"切向 20 m/s 应超过半个 SOI（否则容差比实测宽），实际 {miss20Tan / SoiR2 * 100.0:F1}%");
            Assert.True(miss67Tan > SoiR2,
                $"切向 67 m/s 应已丢 SOI，实际 {miss67Tan / SoiR2 * 100.0:F1}%");
            Assert.True(miss20Rad < 0.5 * SoiR2,
                $"径向 20 m/s 应在半个 SOI 内，实际 {miss20Rad / SoiR2 * 100.0:F1}%");
        }
    }
}
