using System;
using System.Diagnostics;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 到达/捕获/解析圆轨台架（2026-11-03，只读台架 / 不碰生产后端）。
    ///
    /// 用户裁定：
    ///   · 待命轨 = 目标行星希尔球（SOI）的 90%；
    ///   · 近拱点一次烧到圆轨速度（单烧）；
    ///   · 瞄准怎么简单怎么来（不搞 B 平面；按实际近拱点建圆轨）；
    ///   · 保留一般 orbit-keeping，不做纯中心特例。
    ///
    /// 纯数学：SOI → 90% 半径 → 双曲线近拱点 → 捕获 Δv → 圆轨（EnterCircularOrbit 语义）/周期/Θ；
    /// 外加 v∞ ±10/20 m/s、近拱点 ±1,000 km 的扰动表。
    /// </summary>
    public class CaptureCircularBenchTests
    {
        private const double MuZ = 2.4e14;                 // 赭砂 GM（1.2× Rimata）
        private const double MuSun = 2.4369393583e19;      // 燧阳 GM
        private const double AZ = 13_333_333_333.3;        // 赭砂轨道半径 = 2/3 Rimata
        private const double Vinf = 4080.4;                // 霍曼到达的 v∞
        private const double GameDaySeconds = 60_000.0;

        private readonly ITestOutputHelper output;

        public CaptureCircularBenchTests(ITestOutputHelper output) => this.output = output;

        /// <summary>SOI = a·(m/M)^0.4（与 OrbitAdapter.SoiRadiusApprox 同式）。</summary>
        private static double Soi => AZ * Math.Pow(MuZ / MuSun, 0.4);

        private static (double vc, double vp, double dv, double period, double theta) Capture(double r, double vinf)
        {
            double vc = Math.Sqrt(MuZ / r);
            double vp = Math.Sqrt(vinf * vinf + 2.0 * MuZ / r);
            double dv = vp - vc;
            double period = 2.0 * Math.PI * r / vc;
            ManeuverRegime.Classify(dv, vc, MuZ / (r * r), 1.0, out double theta, out _);
            return (vc, vp, dv, period, theta);
        }

        /// <summary>捕获后若不再“snap”，由 (r, 切向 v) 算出的 e；snap 后 e=0。</summary>
        private static double RawEccentricity(double r, double vTangential)
        {
            double eps = vTangential * vTangential / 2.0 - MuZ / r;
            double h = r * vTangential;
            return Math.Sqrt(Math.Max(0.0, 1.0 + 2.0 * eps * h * h / (MuZ * MuZ)));
        }

        [Fact]
        public void Capture_At90PercentSoi_EntersCircularStandby()
        {
            double soi = Soi;
            double r = 0.9 * soi;
            var nominal = Capture(r, Vinf);
            var kind = ManeuverRegime.Classify(nominal.dv, nominal.vc, MuZ / (r * r), 1.0, out _, out _);

            output.WriteLine($"SOI={soi / 1000.0:F1} km；90%={r / 1000.0:F1} km");
            output.WriteLine($"v_circ={nominal.vc:F3} m/s；v_peri={nominal.vp:F3}；capture dv={nominal.dv:F3} m/s");
            output.WriteLine($"period={nominal.period:F1} s = {nominal.period / GameDaySeconds:F4} game days；theta={nominal.theta:F4} rad；regime={kind}");

            Assert.True(Math.Abs(soi - 132_521_195.3) < 1.0, $"SOI={soi:F1}，期望 132,521,195.3");
            Assert.Equal(1_418.5, nominal.vc, 1);
            Assert.Equal(3_128.346, nominal.dv, 3);
            Assert.Equal(8.805, nominal.period / GameDaySeconds, 3);
            Assert.True(nominal.theta < 0.2, $"theta={nominal.theta:F4} 应 ≤0.2");
            Assert.Equal(ManeuverRegime.Kind.QuasiImpulsive, kind);

            // 精确烧 → 圆轨；EnterCircularOrbit 会 snap 到 v_circ，raw e 应为 0
            double eExact = RawEccentricity(r, nominal.vc);
            output.WriteLine($"exact burn raw e={eExact:E3}；snap 后 e=0，r={r / 1000.0:F1} km（待命）");
            Assert.True(eExact < 1e-12);

            output.WriteLine("");
            output.WriteLine("v∞±20/10：标称烧 dv，实际 v_peri 变了 ⇒ raw e（snap 前）");
            output.WriteLine("dv∞    v_circ    v_peri    capture dv   period(d)  theta   raw_e");
            foreach (double dvInf in new[] { -20.0, -10.0, 0.0, 10.0, 20.0 })
            {
                var c = Capture(r, Vinf + dvInf);
                double vAfter = c.vp - nominal.dv;      // 仍按标称 dv 烧
                double rawE = RawEccentricity(r, vAfter);
                output.WriteLine($"{dvInf,5:F0} {c.vc,9:F1} {c.vp,9:F1} {c.dv,12:F2} {c.period / GameDaySeconds,9:F4} {c.theta,7:F4} {rawE,8:F5}");
            }

            output.WriteLine("");
            output.WriteLine("近拱点 ±1,000 km（v∞ 标称）：");
            output.WriteLine("dr(km)   r(km)      v_circ    capture dv   period(d)  theta");
            foreach (double dr in new[] { -1000e3, 0.0, 1000e3 })
            {
                double rr = r + dr;
                var c = Capture(rr, Vinf);
                output.WriteLine($"{dr / 1000.0,6:F0} {rr / 1000.0,10:F1} {c.vc,9:F1} {c.dv,12:F2} {c.period / GameDaySeconds,9:F4} {c.theta,7:F4}");
            }

            int n = 10_000;
            for (int i = 0; i < 200; i++) _ = Capture(r, Vinf);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) _ = Capture(r, Vinf);
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            output.WriteLine("");
            output.WriteLine($"capture 闭式单次成本：{us:F3} us/call");
            Assert.True(us < 10.0, $"capture {us:F3} us > 10 us");
        }
    }
}
