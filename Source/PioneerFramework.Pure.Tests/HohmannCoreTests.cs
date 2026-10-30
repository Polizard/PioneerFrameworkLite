using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 霍曼纯核（2026-10-27）：升轨/降轨分量、到达状态圆化、平面变化法向。
    /// </summary>
    public class HohmannCoreTests
    {
        private const double Mu = 3.986004418e14;
        private const double R1 = 6.7e6;
        private const double R2 = 42.16e6;

        private static (DVector3 r, DVector3 v) Circular(double radius)
            => (new DVector3(radius, 0, 0), new DVector3(0, Math.Sqrt(Mu / radius), 0));

        [Fact]
        public void RaiseOrbit_ComponentsMatchAnalytic()
        {
            var (r1, v1) = Circular(R1);
            var res = HohmannCore.Compute(r1, v1, Mu, R2, new DVector3(0, 0, 1));

            double a = 0.5 * (R1 + R2);
            double vp = Math.Sqrt(Mu * (2.0 / R1 - 1.0 / a));
            double va = Math.Sqrt(Mu * (2.0 / R2 - 1.0 / a));
            double vc1 = Math.Sqrt(Mu / R1);
            double vc2 = Math.Sqrt(Mu / R2);

            Assert.True(res.Valid);
            Assert.Equal(vp - vc1, res.TanDv1, 6);
            Assert.Equal(vc2 - va, res.TanDv2, 6);
            Assert.Equal(0.0, res.NormDv2, 6);
            Assert.Equal(0.0, res.DeltaI, 6);
            Assert.Equal(Math.PI * Math.Sqrt(a * a * a / Mu), res.TransferSeconds, 3);
            Assert.Equal(Math.Abs(res.TanDv1) + res.ArrivalDv, res.TotalDv, 6);
        }

        [Fact]
        public void LowerOrbit_TangentialSignsFlip()
        {
            var (r1, v1) = Circular(R2);
            var res = HohmannCore.Compute(r1, v1, Mu, R1, new DVector3(0, 0, 1));

            Assert.True(res.Valid);
            Assert.True(res.TanDv1 < 0.0, $"降轨出发应为负：{res.TanDv1}");
            Assert.True(res.TanDv2 < 0.0, $"降轨到达也要减速（负）：{res.TanDv2}");
        }

        [Fact]
        public void ArrivalBurn_EntersTargetCircularOrbit()
        {
            var (r1, v1) = Circular(R1);
            var res = HohmannCore.Compute(r1, v1, Mu, R2, new DVector3(0, 0, 1));

            var ov = OrbitView.FromStateVectors(res.ArrivalPos, res.ArrivalVel + res.Dv2,
                Mu, res.TransferSeconds, DVector3.up, 6.371e6);

            Assert.True(Math.Abs(ov.semiMajorAxis - R2) / R2 < 1e-6,
                $"到达后半长轴 {ov.semiMajorAxis / 1000.0:F1}km，期望 {R2 / 1000.0:F1}km");
            Assert.True(Math.Abs(ov.eccentricity) < 1e-6, $"到达后应圆：e={ov.eccentricity:E2}");
        }

        [Fact]
        public void PlaneChange_ProducesNormalComponentOfRightSize()
        {
            const double inc = 30.0 * Math.PI / 180.0;
            var (r1, v1) = Circular(R1);
            DVector3 h2 = new DVector3(0, Math.Sin(inc), Math.Cos(inc));
            var res = HohmannCore.Compute(r1, v1, Mu, R2, h2);

            double vc2 = Math.Sqrt(Mu / R2);
            Assert.Equal(inc, res.DeltaI, 6);
            Assert.True(Math.Abs(Math.Abs(res.NormDv2) - vc2 * Math.Sin(inc)) < 1e-6,
                $"法向 {res.NormDv2:F3}，期望 ±{vc2 * Math.Sin(inc):F3}");
        }
    }
}
