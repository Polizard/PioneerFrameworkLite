using System;
using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 无外力矩刚体转动核：中间轴自旋的符号翻转（贾尼别科夫）必须是 Euler 方程的自然结果，
    /// 同时验证 RK4 子步下 **动能与角动量守恒**（比"看起来在翻"更硬的判据）。
    /// </summary>
    public class TorqueFreeRotationTests
    {
        [Fact]
        public void IntermediateAxis_Flips_And_EnergyAngularMomentumConserved()
        {
            const double m = 1000.0;
            const double sx = 2.0, sy = 3.0, sz = 5.0;
            var r = InertiaTensor.Compose(new List<InertiaTensor.PartContribution>
            {
                new()
                {
                    mass = m,
                    cxx = m / 12.0 * (sy * sy + sz * sz),
                    cyy = m / 12.0 * (sx * sx + sz * sz),
                    czz = m / 12.0 * (sx * sx + sy * sy),
                }
            });

            Assert.True(r.HasData);
            Assert.True(r.p1 > r.p2 && r.p2 > r.p3, "三个主惯量必须严格分开（中间轴才不稳定）");

            DQuaternion q = DQuaternion.identity;
            // 中间轴 + 极小扰动
            DVector3 w = new(0.0004, 0.0200, 0.0006);
            DVector3 p0 = ToPrincipal(r, q, w);
            double e0 = Energy(r, p0);
            double l0 = AngularMomentum2(r, p0);

            int flips = 0;
            int lastSign = Math.Sign(p0.y);
            for (int i = 0; i < 600; i++)
            {
                Assert.True(TorqueFreeRotation.StepRaw(r, q, w, 1.0, out q, out w));
                DVector3 p = ToPrincipal(r, q, w);

                int sign = Math.Sign(p.y);
                if (lastSign != 0 && sign != 0 && sign != lastSign) flips++;
                lastSign = sign;

                double e = Energy(r, p);
                double l = AngularMomentum2(r, p);
                Assert.True(Math.Abs(e - e0) / Math.Max(1.0, e0) < 1e-5, $"动能漂移过大 t={i}");
                Assert.True(Math.Abs(l - l0) / Math.Max(1.0, l0) < 1e-5, $"角动量漂移过大 t={i}");
            }

            Assert.True(flips > 0, "600 s 内中间轴分量从未翻号 —— Euler 方程/积分可能有误");
        }

        private static DVector3 ToPrincipal(in InertiaTensor.InertiaResult r, DQuaternion q, DVector3 world)
        {
            DVector3 body = DVector3.Rotate(world, DQuaternion.Inverse(q));
            return new DVector3(
                r.r00 * body.x + r.r10 * body.y + r.r20 * body.z,
                r.r01 * body.x + r.r11 * body.y + r.r21 * body.z,
                r.r02 * body.x + r.r12 * body.y + r.r22 * body.z);
        }

        private static double Energy(in InertiaTensor.InertiaResult r, DVector3 p)
            => 0.5 * (r.p1 * p.x * p.x + r.p2 * p.y * p.y + r.p3 * p.z * p.z);

        private static double AngularMomentum2(in InertiaTensor.InertiaResult r, DVector3 p)
        {
            double h1 = r.p1 * p.x, h2 = r.p2 * p.y, h3 = r.p3 * p.z;
            return h1 * h1 + h2 * h2 + h3 * h3;
        }
    }
}
