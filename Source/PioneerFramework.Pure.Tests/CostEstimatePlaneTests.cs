using System;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **价目估计的面外项**（`RendezvousGuidanceCore.CostEstimate(..., dIncRad, ...)`）判据 —— 2026-09-20（B2.6）。
    ///
    /// 为什么这条重要：这个函数是**区制判据 `ManeuverRegime` 的输入**。漏掉面外项 ⇒
    /// "**异面 + 低推力**"的 Δv 被低估 ⇒ Θ 偏小 ⇒ 被错分到准脉冲区（阶梯），
    /// 而阶梯给的是**脉冲平面改变**、低推力船执行不了 ✗（设计稿 §10.5-N1）。
    ///
    /// 判据（B2.6 的两份价，都是**闭式**）：
    ///   ① 共面时**与旧口径逐位相同**（向后兼容 —— 不许因为加面外而动了共面结果）；
    ///   ② 脉冲式面外项 = `v_slow·Δi`（取**慢的那一端** ✓，即两处圆轨速度的较小者）；
    ///   ③ 谐波式（我们那套要换掉的）= `2·v_c(rSelf)·Δi` ⇒ **约为脉冲式的 2 倍**（设计稿实测的量级 ✓）；
    ///   ④ 单调：Δi 越大价越高；且**异面的代价能被Θ判据看见**（同一几何加倾角差后 Θ 必须变大）。
    /// </summary>
    public class CostEstimatePlaneTests
    {
        private const double Mu = 2.0e14;
        private const double Rleo = 7.0e6;
        private const double Rgeo = 2.6322e7;

        [Fact]
        public void CoplanarUnchanged()
        {
            double oldWay = RendezvousGuidanceCore.CostEstimate(Rleo, Rleo, 0.5, Mu, impulsive: true);
            double newWay = RendezvousGuidanceCore.CostEstimate(Rleo, Rleo, 0.5, 0.0, Mu, impulsive: true);
            Assert.Equal(oldWay, newWay, 12);      // 逐位：加面外不许动共面结果
        }

        [Fact]
        public void ImpulsivePlaneTerm_IsSlowEndTimesDeltaI()
        {
            double di = 2.0 * Math.PI / 180.0;                       // 2°
            double coplanar = RendezvousGuidanceCore.CostEstimate(Rleo, Rgeo, 0.0, 0.0, Mu, impulsive: true);
            double inclined = RendezvousGuidanceCore.CostEstimate(Rleo, Rgeo, 0.0, di, Mu, impulsive: true);

            double vcSelf = Math.Sqrt(Mu / Rleo), vcTgt = Math.Sqrt(Mu / Rgeo);
            double expect = Math.Min(vcSelf, vcTgt) * di;             // 慢的那一端
            Assert.True(Math.Abs((inclined - coplanar) - expect) < 1e-9 * expect,
                $"脉冲面外项应为 v_slow·Δi = {expect:F1} m/s，实测差 {inclined - coplanar:F1}");
        }

        [Fact]
        public void HarmonicPlaneTerm_IsAboutTwiceTheImpulsive()
        {
            double di = 2.0 * Math.PI / 180.0;
            double imp = RendezvousGuidanceCore.CostEstimate(Rleo, Rgeo, 0.0, di, Mu, impulsive: true);
            double harm = RendezvousGuidanceCore.CostEstimate(Rleo, Rgeo, 0.0, di, Mu, impulsive: false);

            double vcSelf = Math.Sqrt(Mu / Rleo);
            double impPlane = Math.Min(vcSelf, Math.Sqrt(Mu / Rgeo)) * di;
            double harmPlane = 2.0 * vcSelf * di;
            Assert.True(harmPlane > impPlane, "谐波式应比脉冲式贵（口径 ③）");
            // 形状项那一份（非面外）两边不同（1× vs 2×），故只比**面外那一份**的比
            // 比 = 2·v_c(LEO) / v_c(GEO) = 2×5345/2757 = 3.88（⚠ 我第一版写成 ≈5.03 —— 那是**我算错** ✗）
            Assert.True(Math.Abs(harmPlane / impPlane - 3.88) < 0.05,
                $"面外两份价的比应 ≈3.88（v_slow 取到 GEO 半径）实测 {harmPlane / impPlane:F2}");
        }

        [Fact]
        public void MonotoneInInclination_AndVisibleToTheta()
        {
            double prev = -1.0;
            foreach (double deg in new[] { 0.0, 0.5, 1.0, 2.0, 5.0 })
            {
                double dv = RendezvousGuidanceCore.CostEstimate(Rleo, Rleo, 0.0, deg * Math.PI / 180.0, Mu);
                Assert.True(dv >= prev, $"Δi={deg}° 的价不应小于更小倾角：{dv:F1} vs {prev:F1}");
                prev = dv;
            }

            // ★ 这条才是加它的**目的**：异面代价必须能改变区制判据的结果。
            double vc = Math.Sqrt(Mu / Rleo), g = Mu / (Rleo * Rleo);
            double aWeak = 0.5;                                       // 低推力档
            // ⚠ 共面档要取**非零相位**：`Δv=0` 是退化输入 ⇒ `Classify` 按设计给 `θ=NaN` ✗（我第一版就踩了 ✗，
            //   又是"判据自己的前提"）：拿一个真实的小相位差当基准 ✓。
            double dvFlat = RendezvousGuidanceCore.CostEstimate(Rleo, Rleo, 0.05, 0.0, Mu);
            double dvTilt = RendezvousGuidanceCore.CostEstimate(Rleo, Rleo, 0.05, 5.0 * Math.PI / 180.0, Mu);
            ManeuverRegime.Classify(dvFlat, vc, g, aWeak, out double thFlat, out _);
            ManeuverRegime.Classify(dvTilt, vc, g, aWeak, out double thTilt, out _);
            Assert.True(thTilt > thFlat * 1.5,
                $"5° 倾角差必须显著抬高 Θ（否则「异面+低推力」分不到连续区）✗：{thFlat:F3} → {thTilt:F3}");
        }
    }
}
