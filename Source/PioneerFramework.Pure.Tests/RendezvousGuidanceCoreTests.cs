using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// `RendezvousGuidanceCore` 的**现役 API** 钉扎（2026-10-28）。
    ///
    /// 背景：旧律的整套仿真测试在 `Source/已弃置/Pure.Tests/`（2026-09-22 用户裁定旧律作废），
    /// 但本类的 `ApproachTimeSeconds` 仍被交会引导调用、`SelectActuator` 被台架/政策件引用。
    /// 这里只恢复**还活着的**三件事：接近时间、执行机构选择（新语义）、到位判据。
    /// 旧律的相位/形状/全交会仿真是历史，不搬回来。
    /// </summary>
    public class RendezvousGuidanceCoreTests
    {
        [Fact]
        public void ApproachTimeSeconds_UsesBrakingLimitedSpeedPerAxis()
        {
            // vMaxStop = √(2·d·a) = √2000 ≈ 44.721；t = 1000 / 44.721 ≈ 22.36
            double t = RendezvousGuidanceCore.ApproachTimeSeconds(1000, 0, 0, 1.0, 100.0);
            Assert.True(Math.Abs(t - 1000.0 / Math.Sqrt(2000.0)) < 1e-9, $"t={t}");

            // 限速更紧 ⇒ 按限速：1000 / 10 = 100
            Assert.Equal(100.0,
                RendezvousGuidanceCore.ApproachTimeSeconds(1000, 0, 0, 1.0, 10.0), 9);

            // 多轴取最慢那条：x=100 → √200≈14.14 m/s ⇒ 7.07 s；z=400 → √800≈28.28 ⇒ 14.14 s
            Assert.True(Math.Abs(
                RendezvousGuidanceCore.ApproachTimeSeconds(100, 0, 400, 1.0, 100.0)
                - 400.0 / Math.Sqrt(800.0)) < 1e-9);
        }

        [Fact]
        public void ApproachTimeSeconds_ZeroOrInvalid_IsZero()
        {
            Assert.Equal(0.0, RendezvousGuidanceCore.ApproachTimeSeconds(0, 0, 0, 1.0, 10.0), 9);
            Assert.Equal(0.0, RendezvousGuidanceCore.ApproachTimeSeconds(100, 0, 0, 0.0, 10.0), 9);
            Assert.Equal(0.0, RendezvousGuidanceCore.ApproachTimeSeconds(100, 0, 0, 1.0, 0.0), 9);
        }

        [Fact]
        public void SelectActuator_NoCrossClassFallback()
        {
            const double aMain = 9.0, aRcs = 0.85;   // RCS 窗口 = 12.75 m/s

            Assert.Equal(RendezvousGuidanceCore.Actuator.Rcs,
                RendezvousGuidanceCore.SelectActuator(5.0, aMain, aRcs, mainAligned: true));

            Assert.Equal(RendezvousGuidanceCore.Actuator.Main,
                RendezvousGuidanceCore.SelectActuator(500.0, aMain, aRcs, mainAligned: true));

            // 2026-10-27 裁决：大 Δv 未对准 ⇒ **主网（等对准）**，不再 RCS 兜底
            Assert.Equal(RendezvousGuidanceCore.Actuator.Main,
                RendezvousGuidanceCore.SelectActuator(500.0, aMain, aRcs, mainAligned: false));

            Assert.Equal(RendezvousGuidanceCore.Actuator.None,
                RendezvousGuidanceCore.SelectActuator(5.0, 0.0, 0.0, mainAligned: true));
        }

        [Fact]
        public void HasRendezvoused_DefaultTolerances()
        {
            Assert.True(RendezvousGuidanceCore.HasRendezvoused(300.0, 2.0));
            Assert.False(RendezvousGuidanceCore.HasRendezvoused(300.1, 1.0));
            Assert.False(RendezvousGuidanceCore.HasRendezvoused(100.0, 2.1));
        }
    }
}
