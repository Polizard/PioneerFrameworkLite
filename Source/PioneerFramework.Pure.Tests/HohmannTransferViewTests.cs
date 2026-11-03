using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 霍曼"转移轨道参考系"（2026-10-27）：`OrbitTransferPlanner` 的第二发现在把
    /// 转移轨道 `OrbitView` 带进 `PlanNode.refOrbit` ⇒ 到达刹车在转移轨道上分解。
    /// 本用例钉住这条参考轨道本身：从起飞状态出发，半个转移周期后应到目标半径、
    /// 速度等于远地点速度（与霍曼解析式一致）。
    /// </summary>
    public class HohmannTransferViewTests
    {
        [Fact]
        public void TransferView_ArrivesAtTargetRadius_AfterHalfPeriod()
        {
            const double mu = 3.986004418e14;
            const double r1 = 6.7e6;
            const double r2 = 42.16e6;

            double a = 0.5 * (r1 + r2);
            double vp = Math.Sqrt(mu * (2.0 / r1 - 1.0 / a));   // 转移轨道近地点速度
            double tof = Math.PI * Math.Sqrt(a * a * a / mu);  // 半个转移周期

            var ov = OrbitView.FromStateVectors(
                new DVector3(r1, 0, 0), new DVector3(0, vp, 0),
                mu, 0.0, DVector3.up, 6.371e6);

            (DVector3 rA, DVector3 vA) = ov.GameFrameStateAtUT(tof);

            Assert.True(Math.Abs(rA.magnitude - r2) / r2 < 1e-6,
                $"到达半径 {rA.magnitude / 1000.0:F1}km，期望 {r2 / 1000.0:F1}km");

            double va = Math.Sqrt(mu * (2.0 / r2 - 1.0 / a));
            Assert.True(Math.Abs(vA.magnitude - va) / va < 1e-6,
                $"到达速度 {vA.magnitude:F2}，期望 {va:F2}");
        }
    }
}
