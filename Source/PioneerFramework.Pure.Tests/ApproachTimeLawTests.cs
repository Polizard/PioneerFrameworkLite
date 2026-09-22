using System;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **接近段时间律**（`RendezvousGuidanceCore.ApproachTimeSeconds`）判据 —— 2026-09-20 换成 MJ 的形式。
    ///
    /// 上游本体：`MechJebModuleDockingAutopilot.MaxSpeedForDistance`（`:117-124`）
    /// > *how fast the ship can move on the axis and still be able to stop before reaching the distance*
    /// > `v = √(2·|d|·a)`（再 clamp 到限速）
    ///
    /// 我们此前抄的是它**轨道段**那个常数时间律（`tof = 距离/50`）✗ —— 那条在**终端窗口边界**跳变，
    /// 靠一个 `tof = 1.0` 的补丁才压住极限环 ✗。
    ///
    /// 判据（都钉"物理含义"，不钉常数）：
    ///   ① **还能刹住**：给定 d 与 a，实际速度 `d/tof ≤ √(2da)`（这就是 MJ 那条的本意 ✓）；
    ///   ② **对 d 连续**：跨过 5 km（旧补丁的边界）时 tof **不许跳**（旧律在那里从 100 s 跳到 1 s ✗）；
    ///   ③ **限速是上界**：a 很大时 `tof → d/vMax`（单调逼近，不越过 ✓）；
    ///   ④ **逐轴取最慢**：纯切向 1000 m 与"三轴各 1000 m"相比，后者 tof 应相同（都是 √(d/(2a)) 那根轴主导 ✓），
    ///      而"某一轴特别大"时 tof 由**那根轴**决定 ✓。
    /// </summary>
    public class ApproachTimeLawTests
    {
        private const double VMAX = 50.0;     // 与游戏的 `ApproachMaxClosingSpeed` 同值
        private const double A = 0.5;         // 一个中推档的可用加速度（m/s²）

        [Fact]
        public void CanStillStop()
        {
            foreach (double d in new[] { 100.0, 1000.0, 5000.0, 50_000.0 })
            {
                double tof = RendezvousGuidanceCore.ApproachTimeSeconds(d, 0, 0, A, VMAX);
                double v = d / tof;                                  // 实际平均接近速度
                Assert.True(v <= Math.Sqrt(2.0 * d * A) + 1e-9,
                    $"d={d:F0}：速度 {v:F2} 超过「还能刹住」的上界 {Math.Sqrt(2.0 * d * A):F2} m/s");
                Assert.True(v <= VMAX + 1e-9, $"d={d:F0}：速度 {v:F2} 超过限速 {VMAX}");
            }
        }

        [Fact]
        public void ContinuousAcrossTerminalBoundary()
        {
            double below = RendezvousGuidanceCore.ApproachTimeSeconds(4999.0, 0, 0, A, VMAX);
            double above = RendezvousGuidanceCore.ApproachTimeSeconds(5001.0, 0, 0, A, VMAX);
            Assert.True(Math.Abs(above - below) / Math.Max(below, 1e-9) < 1e-3,
                $"跨 5 km 边界不许跳：{below:F2} s → {above:F2} s（旧常数律在这里从 100 s 跳到 1 s ✗）");
        }

        [Fact]
        public void SpeedLimitBindsWhenAccelerationIsPlenty()
        {
            double d = 1.0e6;                                    // 1000 km
            double tofBigA = RendezvousGuidanceCore.ApproachTimeSeconds(d, 0, 0, 100.0, VMAX);
            Assert.True(Math.Abs(tofBigA - d / VMAX) / (d / VMAX) < 1e-9,
                $"加速度充裕时应被限速支配：{tofBigA:F1} s vs d/vMax={d / VMAX:F1} s");

            // ⚠ 单调性只能在**加速度受限的那一区**里验（`√(2da) < 限速` ⇔ `d < vMax²/(2a)`）：
            //   我第一版拿 d=1000 km 去比两个加速度 ⇒ 那里 `√(2da)` 两边都超过 50 ⇒ 限速把两边**都**夹住
            //   ⇒ 时间当然相同 ✗ **是我的判据前提写错，不是律错** ✓（判据自己的前提也要先验 ✓）。
            double dSmall = 1000.0;                              // vMax²/(2·0.5)=2500 m ⇒ 本档落在加速度区
            double tofStrong = RendezvousGuidanceCore.ApproachTimeSeconds(dSmall, 0, 0, 0.5, VMAX);
            double tofWeak = RendezvousGuidanceCore.ApproachTimeSeconds(dSmall, 0, 0, 0.05, VMAX);
            Assert.True(tofWeak > tofStrong * 2.0,
                $"加速度小 ⇒ 时间应显著更长：{tofWeak:F1} s vs {tofStrong:F1} s（d={dSmall:F0} m，加速度区）");
        }

        [Fact]
        public void SlowestAxisGoverns()
        {
            double one = RendezvousGuidanceCore.ApproachTimeSeconds(1000.0, 0, 0, A, VMAX);
            double allThree = RendezvousGuidanceCore.ApproachTimeSeconds(1000.0, 1000.0, 1000.0, A, VMAX);
            Assert.True(Math.Abs(allThree - one) / one < 1e-9,
                $"三轴各 1000 m：最慢轴就是 1000 m 那根 ⇒ 应与单轴相同（{allThree:F2} vs {one:F2}）");

            double skewed = RendezvousGuidanceCore.ApproachTimeSeconds(200.0, 200.0, 8000.0, A, VMAX);
            double bigAxisOnly = RendezvousGuidanceCore.ApproachTimeSeconds(0.0, 0.0, 8000.0, A, VMAX);
            Assert.True(Math.Abs(skewed - bigAxisOnly) / bigAxisOnly < 1e-9,
                $"某轴特别大时由它决定（{skewed:F2} vs {bigAxisOnly:F2}）");
        }

        [Fact]
        public void DegenerateInputs()
        {
            Assert.Equal(0.0, RendezvousGuidanceCore.ApproachTimeSeconds(0, 0, 0, A, VMAX));
            Assert.Equal(0.0, RendezvousGuidanceCore.ApproachTimeSeconds(1000, 0, 0, 0.0, VMAX));
            Assert.Equal(0.0, RendezvousGuidanceCore.ApproachTimeSeconds(1000, 0, 0, A, 0.0));
        }
    }
}
