using System;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **反推着陆制导纯核**（`LandingGuidanceCore`）的判据（2026-09-22）。
    ///
    /// 要钉的是三件**可判定**的事（而不是"看起来合理"）：
    /// ① **点火高度**的量级对：`v²/(2a)`——拿一个手算得出来的例子对死；
    /// ② **"能不能刹住"是可判定的**：工质不够时必须报 `Insufficient`，**不许静默继续**；
    /// ③ **反推段的净减速度公式**：`a_net = (v²−vEnd²)/(2h)`，指令 = 净需求 + g。
    ///
    /// ⚠ 本核是**纯几何/运动学**，但它的**输入口径**（下降率为正、`aMax` 是"当前可用"）
    /// 是它最容易出错的地方 ⇒ 用例直接用具体数字钉住口径。
    /// </summary>
    public class LandingGuidanceCoreTests
    {
        private static readonly LandingGuidanceCore.Params P = LandingGuidanceCore.Params.CreateDefault();

        // 一个像样的着陆器：干重 2 t、工质 3 t、ve=3000 m/s、满油门推重比 ~2g
        private const double DryMass = 2000.0;
        private const double Mass0 = 5000.0;
        private const double Ve = 3000.0;
        private const double G = 8.87;     // 类金星的重力（随便挑个"不小"的）
        private const double AMax = 2.0 * G;

        /// <summary>① **点火高度 = v²/(2a)**（取 vEnd=0 的档来对死手算）。</summary>
        [Fact]
        public void IgnitionAltitude_MatchesHandComputed()
        {
            var p = P;
            p.terminalSpeed = 0.0;                  // 对死闭式：vEnd=0

            double v = 200.0;                       // 下降率 200 m/s
            // ⚠ 期望值 = **净**减速度口径：`v²/(2·(aMax − g))`。
            //   第一版按 `v²/(2·aMax)` 写（= "重力在帮忙减速"），**那是错的**：
            //   推力先要抵掉 g，剩下的才减速 ⇒ 用 aMax 会把点火高度算小 `aMax/(aMax−g)` 倍
            //   （本档 = 2.0 倍）⇒ 点火晚一半 ⇒ 刹不住。用例跟着核一起改口径。
            double expected = v * v / (2.0 * (AMax - G));

            var o = LandingGuidanceCore.Probe(altitude: 5000.0, radialSpeed: v, g: G,
                mass: Mass0, dryMass: DryMass, ve: Ve, aMax: AMax, p);

            Assert.Equal(LandingGuidanceCore.Action.Coast, o.action);   // 还高 ⇒ 等
            Assert.True(Math.Abs(o.ignitionAltitude - expected) < 1e-6,
                $"点火高度应为 v²/(2a) = {expected:F2} m，实测 {o.ignitionAltitude:F2} m");
        }

        /// <summary>①（续）**到点火高度就烧**，且指令 = 净需求 + g。</summary>
        [Fact]
        public void BelowIgnitionAltitude_BurnsWithNetPlusGravity()
        {
            var p = P;
            p.terminalSpeed = 0.0;
            p.targetAltitude = 0.0;

            double v = 200.0, h = 1000.0;
            double aNet = v * v / (2.0 * h);        // = 20 m/s²
            // ⚠ 这里必须用 `Probe`：本用例考的是"单步的算式"，而 `Step` 会置锁存、跨用例互相污染。
            var o = LandingGuidanceCore.Probe(h, v, G, Mass0, DryMass, Ve, AMax, p);

            Assert.Equal(LandingGuidanceCore.Action.Burn, o.action);
            Assert.True(Math.Abs(o.targetAccel - Math.Min(aNet + G, AMax)) < 1e-9,
                $"指令应为 min(a_net+g, aMax) = {Math.Min(aNet + G, AMax):F3}，实测 {o.targetAccel:F3}");
        }

        /// <summary>② **刹不住必须报**（工质不够）——同一初速、把干重抬到"几乎没工质"。</summary>
        [Fact]
        public void NotEnoughPropellant_ReportsInsufficient()
        {
            var o = LandingGuidanceCore.Probe(altitude: 300.0, radialSpeed: 900.0, g: G,
                mass: 2100.0, dryMass: 2000.0, ve: Ve, aMax: AMax, p: P);

            Assert.Equal(LandingGuidanceCore.Action.Insufficient, o.action);
            Assert.True(o.dvNeed > o.dvCap,
                $"需求 {o.dvNeed:F1} 应大于能力 {o.dvCap:F1}");
            Assert.Contains("刹不住", o.why);
        }

        /// <summary>②（续）**工质够时必须不报**（反例保护：别把能干的活判成不能干）。</summary>
        [Fact]
        public void WithPlentyOfPropellant_DoesNotReportInsufficient()
        {
            var o = LandingGuidanceCore.Probe(altitude: 3000.0, radialSpeed: 150.0, g: G,
                mass: Mass0, dryMass: DryMass, ve: Ve, aMax: AMax, p: P);
            Assert.NotEqual(LandingGuidanceCore.Action.Insufficient, o.action);
        }

        /// <summary>③ **到达**：高度与下降率都在窗口内 ⇒ `Landed`。</summary>
        [Fact]
        public void AtTarget_Lands()
        {
            // ⚠ 判据是**单向**的："降到目标高度或以下 + 下降率在窗口内" ⇒ 用它自己的口径构造
            var o = LandingGuidanceCore.Probe(altitude: P.targetAltitude, radialSpeed: 0.5, g: G,
                mass: 2200.0, dryMass: DryMass, ve: Ve, aMax: AMax, p: P);
            Assert.Equal(LandingGuidanceCore.Action.Landed, o.action);
        }

        /// <summary>③（续）**一整个反推过程**：给定初始条件，按本核逐步烧，应当收敛到 `Landed`。
        /// 这是"闭式自洽"的端到端证据（分步积分用的是与执行器同族的简单欧拉）。</summary>
        [Fact]
        public void ClosedLoop_ConvergesToLanded()
        {
            // 推力固定 ⇒ aMax(m) = F/m，F = AMax·Mass0（点火时的推力）
            double F = AMax * Mass0;
            double h = 4000.0, v = 180.0, m = Mass0;
            const double dt = 0.5;
            var p = P;
            var st = new LandingGuidanceCore.State();
            int steps = 0;
            string lastWhy = "";
            while (steps++ < 20000)
            {
                double aMax = F / m;
                var o = LandingGuidanceCore.Step(h, v, G, m, DryMass, Ve, aMax, p, ref st);
                lastWhy = o.why;
                if (o.action == LandingGuidanceCore.Action.Landed) break;
                Assert.NotEqual(LandingGuidanceCore.Action.Insufficient, o.action);

                if (o.action == LandingGuidanceCore.Action.Burn)
                {
                    double mf = F / Ve;                       // ṁ = F/ve
                    double acc = Math.Min(o.targetAccel, aMax);
                    double fuelBurn = Math.Min(mf * dt, m - DryMass);
                    m -= fuelBurn;
                    v -= (acc - G) * dt;                      // 净减速度
                    if (v < 0.0) v = 0.0;
                }
                h -= v * dt;
                // ⚠ **不要夹到 0**：目标是"降到 targetAltitude"，夹 0 会让 h 停在地面上、
                //   而判据（altitude ≤ target）在 h=0 时是成立的——但第一版判据是"±x 内"⇒ 永不命中。
                //   这里保留"可以降到目标之下"，模拟"落地/继续下沉"（判据在核里单向处理）。
            }

            Assert.True(steps < 20000, "反推闭环应在有限步内到达");
            Assert.True(h <= P.targetAltitude + 30.0,
                $"结束时高度应接近目标 {P.targetAltitude:F0} m，实测 {h:F1} m（{lastWhy}）");
            Assert.True(v <= P.landSpeedTol + 1.0,
                $"结束时下降率应落进窗口，实测 {v:F2} m/s");
        }
    }
}
