using System;
using System.Diagnostics;
using MechJebLib.Lambert;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 快速圆锥（“火炬”）鲁棒性台架（2026-11-03，只读台架 / 不碰生产后端）。
    ///
    /// 扫 6 个转移角 × 5 个 TOF 档：用 Gooding Lambert 解两冲量（离线精度），
    /// 看每条的 Δv、圆锥类型（椭圆/双曲）、以及 10/20 m/s 切向/径向出发误差下的 SOI 脱靶。
    ///
    /// ⚠ 这里用 Gooding（单次 Lambert）**只做离线扫描**；运行时仍受 10 µs 预算约束，
    ///   需要预计算表/插值，不能用运行时 Lambert。
    /// </summary>
    public class FastConicTorchBenchTests
    {
        private const double Mu = 2.4369393583e19;
        private const double R1 = 20_000_000_000.0;
        private const double R2 = R1 * 2.0 / 3.0;
        private const double SoiR2 = 132_521_195.3;
        private const double GameDaySeconds = 60_000.0;
        private const double HohmannTof = 1_369_306.4;

        private readonly ITestOutputHelper output;

        public FastConicTorchBenchTests(ITestOutputHelper output) => this.output = output;

        private static double ArrivalMiss(DVector3 r1, DVector3 v, double tof, DVector3 target)
            => (OrbitMath.GetPositionAtTimeDelta(r1, v, tof, Mu) - target).magnitude;

        [Fact]
        public void FastConic_TorchRobustness_ScansAngleAndTof()
        {
            var r1 = new DVector3(R1, 0, 0);
            var v1 = new DVector3(0, Math.Sqrt(Mu / R1), 0);
            double vc1 = Math.Sqrt(Mu / R1);
            double vc2 = Math.Sqrt(Mu / R2);

            var hohmann = HohmannCore.Compute(r1, v1, Mu, R2, new DVector3(0, 0, 1));
            Assert.True(hohmann.Valid);
            output.WriteLine($"Hohmann 参考：dv1={hohmann.TanDv1:F3} dv2={hohmann.TanDv2:F3} total={hohmann.TotalDv:F3} m/s; " +
                $"TOF={hohmann.TransferSeconds / GameDaySeconds:F6} game days");
            Assert.Equal(7765.6234, hohmann.TotalDv, 2);

            output.WriteLine("");
            output.WriteLine("θ°   TOF(d)  dv1      dv2      total    e       tan10%  rad10%  tan20%  10m/s鲁棒?  Lambertµs");
            var angles = new[] { 170.0, 150.0, 120.0, 90.0, 60.0, 30.0 };
            var tofFactors = new[] { 1.0, 0.7, 0.5, 0.35, 0.25 };
            int solves = 0, infeasible = 0, valid = 0;
            double solveUsTotal = 0.0;

            foreach (double angleDeg in angles)
            {
                double th = angleDeg * Math.PI / 180.0;
                var r2 = new DVector3(R2 * Math.Cos(th), R2 * Math.Sin(th), 0);
                var v2Target = new DVector3(-vc2 * Math.Sin(th), vc2 * Math.Cos(th), 0);

                foreach (double factor in tofFactors)
                {
                    double tof = HohmannTof * factor;
                    solves++;
                    DVector3 vi, vf;
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        (vi, vf) = Gooding.Solve(Mu, r1, r2, tof, TransferGeometry.ShortWay);
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        infeasible++;
                        output.WriteLine($"{angleDeg,4:F0} {tof / GameDaySeconds,7:F2}  infeasible: {ex.GetType().Name}");
                        continue;
                    }
                    sw.Stop();
                    solveUsTotal += sw.Elapsed.TotalMilliseconds * 1000.0;
                    valid++;

                    var dv1 = vi - v1;
                    var dv2 = v2Target - vf;
                    double totalDv = dv1.magnitude + dv2.magnitude;
                    double arriveMiss = ArrivalMiss(r1, vi, tof, r2);

                    DVector3 h = DVector3.Cross(r1, vi);
                    double rMag = r1.magnitude, v2 = vi.sqrMagnitude;
                    DVector3 eVec = DVector3.Cross(vi, h) / Mu - r1 / rMag;
                    double e = eVec.magnitude;

                    double tan10 = ArrivalMiss(r1, vi + new DVector3(0, 10.0, 0), tof, r2);
                    double rad10 = ArrivalMiss(r1, vi + new DVector3(10.0, 0, 0), tof, r2);
                    double tan20 = ArrivalMiss(r1, vi + new DVector3(0, 20.0, 0), tof, r2);
                    bool robust10 = tan10 < 0.5 * SoiR2;

                    output.WriteLine($"{angleDeg,4:F0} {tof / GameDaySeconds,7:F2}  {dv1.magnitude,8:F1} {dv2.magnitude,8:F1} {totalDv,8:F1} " +
                        $"{e,6:F3}  {tan10 / SoiR2 * 100,6:F1} {rad10 / SoiR2 * 100,6:F1} {tan20 / SoiR2 * 100,6:F1} " +
                        $"{(robust10 ? "Y" : "N"),9}  {sw.Elapsed.TotalMilliseconds * 1000.0,8:F0}");

                    if (arriveMiss > 1.0)
                        output.WriteLine($"  ⚠ Lambert 到达点偏差 {arriveMiss:F3} m > 1 m");
                }
            }

            output.WriteLine("");
            output.WriteLine($"扫描：{solves} 组，valid={valid}，infeasible={infeasible}；Gooding 平均 {solveUsTotal / Math.Max(valid, 1):F1} µs/次");
            output.WriteLine("判读：tan10% < 50 视为“10 m/s 切向误差仍在中半个 SOI 内”；>100 已丢 SOI。");
            Assert.True(valid > 0, "至少应有可解组合");
            Assert.True(infeasible < solves, "不应全部不可解");
        }
    }
}
