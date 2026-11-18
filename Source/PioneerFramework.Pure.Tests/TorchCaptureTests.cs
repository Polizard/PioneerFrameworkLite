using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// `TryPlanCapture` 的单元测试（2026-11-18）。这段是“主动进 SOI”唯一的数学件，
    /// 之前**零覆盖**就上船，导致到达 Δv 被算成 0 而无人发现。
    /// </summary>
    public class TorchCaptureTests
    {
        private readonly ITestOutputHelper output;
        public TorchCaptureTests(ITestOutputHelper output) => this.output = output;

        private const double MuTarget = 1.0e13;   // 行星级 GM
        private const double Soi = 1.3e8;

        [Fact]
        public void CaptureFromSoiEdge_ProducesFiniteRetrogradeDeltaV()
        {
            double entryRadius = Soi * 0.98;
            var position = new DVector3(entryRadius, 0, 0);
            var velocity = new DVector3(0, 3.0e4, 0);   // 30 km/s 相对速度
            Assert.True(TorchTransferCore.TryPlanCapture(MuTarget, position, velocity, Soi * 0.5,
                out double dv, out string reason), reason);

            double required = Math.Sqrt(MuTarget * (2.0 / entryRadius - 1.0 / (0.5 * (entryRadius + Soi * 0.5))));
            output.WriteLine($"入口 r={entryRadius / 1e6:F1}e6 m，相对速度 30 km/s ⇒ 捕获 Δv={dv:F1} m/s（目标速度 {required:F1}）");
            Assert.True(dv > 0.0);
            Assert.Equal(3.0e4 - required, dv, 6);
            Assert.True(dv < 3.0e4, "捕获 Δv 必须小于相对速度");
        }

        [Fact]
        public void CaptureAtTargetCentre_IsRejected()
        {
            // 这正是当天实机的情形：冲量解把船放在目标点上，r≈0 ⇒ 必须报错而不是静默返回 0
            Assert.False(TorchTransferCore.TryPlanCapture(MuTarget, new DVector3(1.0, 0, 0),
                new DVector3(0, 3.0e4, 0), Soi * 0.5, out double dv, out string reason));
            output.WriteLine(reason);
            Assert.Equal(0.0, dv);
            Assert.False(string.IsNullOrEmpty(reason));   // r=1 命中"相对位置无效"，非"近心点"分支
        }

        [Fact]
        public void CaptureWithPeriapsisOutsideCurrentRadius_IsRejected()
        {
            Assert.False(TorchTransferCore.TryPlanCapture(MuTarget, new DVector3(1.0e7, 0, 0),
                new DVector3(0, 5.0e3, 0), Soi * 0.5, out _, out string reason));
            Assert.Contains("近心点", reason);
        }

        [Fact]
        public void AlreadySlowShip_NeedsNoCaptureBurn()
        {
            Assert.True(TorchTransferCore.TryPlanCapture(MuTarget, new DVector3(Soi, 0, 0),
                new DVector3(0, 1.0, 0), Soi * 0.5, out double dv, out _));
            Assert.True(dv <= 1.0);
        }

        [Fact]
        public void InvalidTargetGravity_IsRejected()
        {
            Assert.False(TorchTransferCore.TryPlanCapture(0.0, new DVector3(Soi, 0, 0),
                new DVector3(0, 3.0e4, 0), Soi * 0.5, out _, out string reason));
            Assert.Contains("GM", reason);
        }
    }
}
