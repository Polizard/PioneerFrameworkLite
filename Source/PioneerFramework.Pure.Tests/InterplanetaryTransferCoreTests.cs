using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 行星际粗转移纯核测试（2026-11-18，授权件 `后端变更授权-20261118-行星际粗转移`）。
    ///
    /// 钉住四件事：
    ///   ① 转移时长是霍曼半周期（地球→火星 ≈ 259 天）；
    ///   ② Lambert 解出的出发速度，用同一套二体传播推 `tof` 后**命中目标点**；
    ///   ③ 捕获 Δv 施加后轨道**有界**；已经是有界轨道时 dv = 0 且给出 AlreadyBound；
    ///   ④ 非法输入一律拒绝（不抛、不返回“看起来有效”的 0）。
    /// </summary>
    public class InterplanetaryTransferCoreTests
    {
        private readonly ITestOutputHelper output;
        public InterplanetaryTransferCoreTests(ITestOutputHelper output) => this.output = output;

        // 日心：GM 与两条圆轨半径（米）。
        private const double SunMu = 1.32712440018e20;
        private const double EarthRadius = 1.495978707e11;
        private const double MarsRadius = 2.279e11;

        // 月球量级：GM、SOI 半径、入 SOI 相对速度。
        private const double MoonMu = 4.9028e12;
        private const double MoonSoi = 6.6e7;
        private const double MoonArrivalSpeed = 800.0;
        private const double MoonPeriapsis = 1.9e6;

        [Fact]
        public void TransferTimeOfFlight_EarthToMars_IsAbout259Days()
        {
            double tof = InterplanetaryTransferCore.TransferTimeOfFlight(EarthRadius, MarsRadius, SunMu);
            double days = tof / 86400.0;
            output.WriteLine($"tof = {days:F2} d");
            Assert.InRange(days, 250.0, 270.0);
        }

        [Fact]
        public void TransferTimeOfFlight_RejectsInvalidInputs()
        {
            Assert.Equal(0.0, InterplanetaryTransferCore.TransferTimeOfFlight(0.0, MarsRadius, SunMu));
            Assert.Equal(0.0, InterplanetaryTransferCore.TransferTimeOfFlight(EarthRadius, -1.0, SunMu));
            Assert.Equal(0.0, InterplanetaryTransferCore.TransferTimeOfFlight(EarthRadius, MarsRadius, 0.0));
            Assert.Equal(0.0, InterplanetaryTransferCore.TransferTimeOfFlight(double.NaN, MarsRadius, SunMu));
        }

        [Fact]
        public void DepartureVelocity_PropagatesToTheTargetPoint()
        {
            // 地球在 +X，火星在 120°（XZ 平面，法向 +Y），走 Lambert 短程。
            double angle = 120.0 * Math.PI / 180.0;
            var r1 = new DVector3(EarthRadius, 0, 0);
            var v1 = new DVector3(0, 0, Math.Sqrt(SunMu / EarthRadius));
            var r2 = new DVector3(MarsRadius * Math.Cos(angle), 0, MarsRadius * Math.Sin(angle));
            double tof = InterplanetaryTransferCore.TransferTimeOfFlight(EarthRadius, MarsRadius, SunMu);

            bool ok = InterplanetaryTransferCore.TryPlanDeparture(SunMu, r1, v1, r2, tof, out var dv, out string reason);
            output.WriteLine($"ok={ok} reason={reason} |dv|={dv.magnitude:F2} m/s");
            Assert.True(ok, reason);

            DVector3 departureVelocity = v1 + dv;
            DVector3 arrival = OrbitMath.GetPositionAtTimeDelta(r1, departureVelocity, tof, SunMu);
            double miss = (arrival - r2).magnitude / r2.magnitude;
            output.WriteLine($"arrival miss = {miss:E3} (relative)");
            Assert.True(miss < 1e-6, $"Lambert 解传播后未命中目标点：relative miss = {miss:E3}");
        }

        [Fact]
        public void CaptureDv_MakesTheOrbitBound()
        {
            bool ok = InterplanetaryTransferCore.TryCaptureDv(MoonMu, MoonSoi, MoonArrivalSpeed, MoonPeriapsis,
                InterplanetaryTransferCore.DefaultCaptureMargin, out double dv, out string reason);
            output.WriteLine($"ok={ok} reason={reason} dv={dv:F2} m/s escape={InterplanetaryTransferCore.EscapeSpeed(MoonMu, MoonSoi):F2}");

            Assert.True(ok, reason);
            Assert.True(dv > 0.0);
            Assert.False(InterplanetaryTransferCore.IsBound(MoonMu, MoonSoi, MoonArrivalSpeed));
            Assert.True(InterplanetaryTransferCore.IsBound(MoonMu, MoonSoi, MoonArrivalSpeed - dv));
        }

        [Fact]
        public void CaptureDv_AlreadyBoundReturnsZero()
        {
            bool ok = InterplanetaryTransferCore.TryCaptureDv(MoonMu, MoonSoi, 100.0, MoonPeriapsis,
                InterplanetaryTransferCore.DefaultCaptureMargin, out double dv, out string reason);

            Assert.True(ok);
            Assert.Equal(0.0, dv);
            Assert.Equal("AlreadyBound", reason);
        }

        [Fact]
        public void CaptureDv_MarginOnlyMakesTheBurnBigger()
        {
            InterplanetaryTransferCore.TryCaptureDv(MoonMu, MoonSoi, MoonArrivalSpeed, MoonPeriapsis, 0.0, out double tight, out _);
            InterplanetaryTransferCore.TryCaptureDv(MoonMu, MoonSoi, MoonArrivalSpeed, MoonPeriapsis, 0.10, out double loose, out _);
            output.WriteLine($"dv(tight)={tight:F2} m/s  dv(10% margin)={loose:F2} m/s");
            Assert.True(loose > tight, "余量应当只增大 Δv（偏过烧）");
            Assert.True(InterplanetaryTransferCore.IsBound(MoonMu, MoonSoi, MoonArrivalSpeed - loose));
        }

        [Fact]
        public void CaptureDv_RejectsInvalidGeometry()
        {
            // 近心点必须小于当前半径。
            Assert.False(InterplanetaryTransferCore.TryCaptureDv(MoonMu, MoonSoi, MoonArrivalSpeed, MoonSoi,
                0.03, out _, out string reason));
            Assert.Equal("InvalidPeriapsis", reason);

            Assert.False(InterplanetaryTransferCore.TryCaptureDv(0.0, MoonSoi, MoonArrivalSpeed, MoonPeriapsis,
                0.03, out _, out reason));
            Assert.Equal("InvalidState", reason);

            Assert.False(InterplanetaryTransferCore.TryCaptureDv(MoonMu, MoonSoi, MoonArrivalSpeed, MoonPeriapsis,
                1.5, out _, out reason));
            Assert.Equal("InvalidMargin", reason);
        }

        [Fact]
        public void EscapeSpeed_MatchesDefinition()
        {
            Assert.Equal(Math.Sqrt(2.0 * MoonMu / MoonSoi), InterplanetaryTransferCore.EscapeSpeed(MoonMu, MoonSoi), 6);
            Assert.Equal(0.0, InterplanetaryTransferCore.EscapeSpeed(0.0, MoonSoi));
        }

        // ── 出发候选扫描（2026-11-18 补）──────────────────────────────────────────

        private static DVector3 MarsAt(double timeOffset, double phase0)
        {
            double omega = Math.Sqrt(SunMu / (MarsRadius * MarsRadius * MarsRadius));
            return new DVector3(MarsRadius * Math.Cos(phase0 + omega * timeOffset), 0,
                MarsRadius * Math.Sin(phase0 + omega * timeOffset));
        }

        [Fact]
        public void ScanFindsCheaperTransferThanTheFixedHohmannTime()
        {
            // 目标相位 120°：立刻出发 + 固定霍曼时长会解出高能大椭圆；扫描（等窗口）应显著更便宜。
            double phase0 = 120.0 * Math.PI / 180.0;
            var r1 = new DVector3(EarthRadius, 0, 0);
            var v1 = new DVector3(0, 0, Math.Sqrt(SunMu / EarthRadius));
            double hohmann = InterplanetaryTransferCore.TransferTimeOfFlight(EarthRadius, MarsRadius, SunMu);
            const double window = 365.0 * 86400.0;

            Assert.True(InterplanetaryTransferCore.TryPlanDeparture(SunMu, r1, v1, MarsAt(hohmann, phase0), hohmann,
                out DVector3 fixedDv, out string fixedReason), fixedReason);

            Assert.True(InterplanetaryTransferCore.TryPlanDepartureWithScan(SunMu, r1, v1, hohmann, window,
                offset => MarsAt(offset, phase0),
                out DVector3 scanDv, out double delay, out double timeOfFlight, out string scanReason), scanReason);

            output.WriteLine($"fixed |dv|={fixedDv.magnitude:F0} m/s；scan |dv|={scanDv.magnitude:F0} m/s @ delay={delay / 86400.0:F1} d, tof={timeOfFlight / 86400.0:F1} d");

            Assert.True(scanDv.magnitude < 5000.0, $"扫描后 Δv 仍然过大：{scanDv.magnitude:F0} m/s");
            Assert.True(scanDv.magnitude < fixedDv.magnitude * 0.5, "扫描应比固定霍曼时长明显便宜");

            // 扫出来的解同样要命中目标点（船按二体传播到出发时刻再点火）。
            DVector3 departurePosition = OrbitMath.GetPositionAtTimeDelta(r1, v1, delay, SunMu);
            DVector3 departureVelocity = OrbitMath.GetVelocityAtTimeDelta(r1, v1, delay, SunMu);
            DVector3 arrival = OrbitMath.GetPositionAtTimeDelta(departurePosition, departureVelocity + scanDv, timeOfFlight, SunMu);
            double miss = (arrival - MarsAt(delay + timeOfFlight, phase0)).magnitude / MarsRadius;
            output.WriteLine($"scan arrival miss = {miss:E3}");
            Assert.True(miss < 1e-6, $"扫描解传播后未命中目标点：{miss:E3}");
        }

        [Fact]
        public void ScanIsBitForBitDeterministic()
        {
            double phase0 = 94.0 * Math.PI / 180.0;
            var r1 = new DVector3(EarthRadius, 0, 0);
            var v1 = new DVector3(0, 0, Math.Sqrt(SunMu / EarthRadius));
            double hohmann = InterplanetaryTransferCore.TransferTimeOfFlight(EarthRadius, MarsRadius, SunMu);
            const double window = 400.0 * 86400.0;

            Assert.True(InterplanetaryTransferCore.TryPlanDepartureWithScan(SunMu, r1, v1, hohmann, window,
                t => MarsAt(t, phase0), out DVector3 first, out double firstDelay, out double firstTime, out _));
            Assert.True(InterplanetaryTransferCore.TryPlanDepartureWithScan(SunMu, r1, v1, hohmann, window,
                t => MarsAt(t, phase0), out DVector3 second, out double secondDelay, out double secondTime, out _));

            Assert.Equal(firstDelay, secondDelay);
            Assert.Equal(firstTime, secondTime);
            Assert.Equal(first.x, second.x);
            Assert.Equal(first.y, second.y);
            Assert.Equal(first.z, second.z);
        }

        [Fact]
        public void ScanRejectsInvalidInputs()
        {
            var r1 = new DVector3(EarthRadius, 0, 0);
            var v1 = new DVector3(0, 0, Math.Sqrt(SunMu / EarthRadius));
            double hohmann = InterplanetaryTransferCore.TransferTimeOfFlight(EarthRadius, MarsRadius, SunMu);

            Assert.False(InterplanetaryTransferCore.TryPlanDepartureWithScan(SunMu, r1, v1, 0.0, 1e7,
                _ => r1, out _, out _, out _, out string reason));
            Assert.Equal("InvalidTimeOfFlight", reason);

            Assert.False(InterplanetaryTransferCore.TryPlanDepartureWithScan(SunMu, r1, v1, hohmann, 0.0,
                _ => r1, out _, out _, out _, out reason));
            Assert.Equal("InvalidWindow", reason);

            Assert.False(InterplanetaryTransferCore.TryPlanDepartureWithScan(SunMu, r1, v1, hohmann, 1e7,
                null!, out _, out _, out _, out reason));
            Assert.Equal("InvalidTargetProvider", reason);
        }
    }
}
