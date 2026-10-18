using System;
using PioneerFramework.Burn;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **牛顿搜索体（KSA-① 候迁件）的护栏判据**（2026-10-17）：五条护栏里可单测的四条——
    /// 收敛、越界不静默钳位、奇异、非法输入。orbit 侧的接线由 `MechJebLibTest/BurnArcCompensationTests`
    /// 与 `BurnArcPhaseChoiceTests` 回归。
    /// </summary>
    public class BurnRefineNewtonTests
    {
        [Fact]
        public void Refine_LinearModel_Converges()
        {
            // 目标 (scale=1.02, shift=4)；线性 miss ⇒ 一步牛顿即可命中，后续一步收敛。
            var r = BurnRefineNewton.Refine(
                (double s, double sh, ref Miss3 m) =>
                {
                    m.X = (s - 1.02) * 100.0;
                    m.Y = (sh - 4.0) * 0.2;
                    m.Z = 0.0;
                },
                1.0, 0.0, tolerance: 1e-6, maxScaleDeviation: 0.10, maxShiftSeconds: 10.0);

            Assert.True(r.Converged, r.ToString());
            Assert.True(Math.Abs(r.Scale - 1.02) < 1e-6, r.ToString());
            Assert.True(Math.Abs(r.ShiftSeconds - 4.0) < 1e-4, r.ToString());
            Assert.InRange(r.Calls, 1, 12);
        }

        [Fact]
        public void Refine_Bounds_DoNotSilentlyClamp()
        {
            // 解需要 shift=4、但界只有 ±1 ⇒ 不得静默钳到界上并声称收敛（护栏④）。
            var r = BurnRefineNewton.Refine(
                (double s, double sh, ref Miss3 m) =>
                {
                    m.X = (s - 1.02) * 100.0;
                    m.Y = (sh - 4.0) * 0.2;
                    m.Z = 0.0;
                },
                1.0, 0.0, tolerance: 1e-6, maxScaleDeviation: 0.10, maxShiftSeconds: 1.0);

            Assert.False(r.Converged, r.ToString());
            Assert.True(r.Status == RefineStatus.OutOfBounds || r.Status == RefineStatus.NoImprovement, r.ToString());
            Assert.True(Math.Abs(r.ShiftSeconds) <= 1.0 + 1e-12, "返回的 best-so-far 必须在界内");
        }

        [Fact]
        public void Refine_ConstantMiss_IsSingular()
        {
            // 对两个自由度都不变 ⇒ 雅可比退化 ⇒ 必须报 Singular（护栏③），交调用方兜底。
            var r = BurnRefineNewton.Refine(
                (double s, double sh, ref Miss3 m) =>
                {
                    m.X = 0.1; m.Y = 0.2; m.Z = 0.3;
                },
                1.0, 0.0, tolerance: 1e-6, maxScaleDeviation: 0.10, maxShiftSeconds: 10.0);

            Assert.Equal(RefineStatus.Singular, r.Status);
        }

        [Fact]
        public void Refine_InvalidTolerance_IsRejected()
        {
            var r = BurnRefineNewton.Refine(
                (double s, double sh, ref Miss3 m) => { m.X = 0.0; m.Y = 0.0; m.Z = 0.0; },
                1.0, 0.0, tolerance: 0.0);
            Assert.Equal(RefineStatus.InvalidInput, r.Status);
        }
    }
}
