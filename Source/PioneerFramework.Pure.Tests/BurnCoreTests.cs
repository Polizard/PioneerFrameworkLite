using System;
using System.Collections.Generic;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    public class BurnCoreTests
    {
        [Fact]
        public void Estimate_SingleFeasibleNetwork_ReturnsExpectedShape()
        {
            var networks = new List<BurnNetworkInput>
            {
                new BurnNetworkInput(
                    thrust: 1000f,
                    massFlow: 1f,
                    ve: 1000f,
                    availableMass: 200f)
            };

            var result = BurnCore.Estimate(100f, 1000f, networks);

            Assert.True(result.Feasible);
            Assert.Single(result.Networks);
            Assert.True(result.RequiredPropellant > 0f);
            Assert.True(result.BurnSeconds > 0f);
            Assert.True(result.MaxDeltaV > 0f);
            Assert.True(result.Networks[0].RequiredPropellant <= 200f);
        }

        [Fact]
        public void Estimate_NoNetworks_IsInfeasible()
        {
            var result = BurnCore.Estimate(100f, 1000f, new List<BurnNetworkInput>());

            Assert.False(result.Feasible);
            Assert.Null(result.Networks);
            Assert.Equal(0f, result.RequiredPropellant);
        }

        [Fact]
        public void Estimate_InsufficientPropellant_IsInfeasible()
        {
            var networks = new List<BurnNetworkInput>
            {
                new BurnNetworkInput(1000f, 1f, 1000f, 1f)
            };

            var result = BurnCore.Estimate(100f, 1000f, networks);

            Assert.False(result.Feasible);
            Assert.False(result.Networks[0].Feasible);
        }

        // ── 角色语义（2026-09-16）：主网承担机动、RCS 不参与分配 ──────────────────

        /// <summary>单网输入必须与"旧式按网络各自 ve 分摊"逐位一致（回归护栏：单网船的既有验收数字不许动）。</summary>
        [Fact]
        public void Estimate_SingleNetwork_MatchesClosedForm()
        {
            const float dry = 5000f, avail = 12000f, ve = 4000f, mf = 4f, dv = 500f;

            var result = BurnCore.Estimate(dv, dry,
                new List<BurnNetworkInput> { new BurnNetworkInput(ve * mf, mf, ve, avail) });

            double m0 = dry + avail;
            float expectDm = (float)(m0 * (1.0 - Math.Exp(-dv / ve)));
            Assert.True(result.Feasible);
            Assert.Equal(expectDm, result.RequiredPropellant, 2);          // 需料 = 火箭方程闭式
            // ★ 恒定加速度政策（用户 2026-09-19 裁定）：时长 = Δv / a_cmd，a_cmd = F/m₀
            //   （旧口径"需料/流率" = 全油门时长 499.39 ⇒ 新值 531.25，比值 = m₀/m_avg ≈ 1.064）
            Assert.Equal(dv * m0 / (ve * mf), result.BurnSeconds, 2);
            Assert.Equal((float)(ve * Math.Log(m0 / dry)), result.MaxDeltaV, 1);
        }

        /// <summary>有主网时：RCS 只作备用（需求 0、标 IsReserve），机动料与时长全算在主网上。</summary>
        [Fact]
        public void Estimate_WithMainAndRcs_RcsIsReserveOnly()
        {
            // 主网：ve 4000、ṁ 4、12t；RCS：ve 1500、ṁ 1、0.5t；干质量 5t
            var networks = new List<BurnNetworkInput>
            {
                new BurnNetworkInput(16000f, 4f, 4000f, 12000f),
                new BurnNetworkInput(1500f, 1f, 1500f, 500f, isRcs: true),
            };

            var result = BurnCore.Estimate(500f, 5000f, networks);

            Assert.True(result.Feasible);
            Assert.False(result.Networks[0].IsReserve);
            Assert.True(result.Networks[1].IsReserve);
            Assert.Equal(0f, result.Networks[1].RequiredPropellant);
            Assert.Equal(0f, result.Networks[1].AllocatedDv);
            Assert.True(result.Networks[1].Feasible);          // 备用网络不欠账，不该出现在"工质不足"里

            // 需料用全船 m0(17.5t) 与主网 ve(4000)：与"RCS 一起并行烧"（ve_eff 3500）必须不同——
            // 旧口径会把 RCS 算成同时出力，因此低估主网需求、高估可达 ΔV。
            double m0 = 5000 + 12000 + 500;
            float expectDm = (float)(m0 * (1.0 - Math.Exp(-500.0 / 4000.0)));
            Assert.Equal(expectDm, result.RequiredPropellant, 1);
            // ★ 恒定加速度政策：时长 = Δv / (F主/m₀)（旧"需料/流率" = 514.1 ⇒ 新值 546.9）
            Assert.Equal(500f * (float)m0 / 16000f, result.BurnSeconds, 1);
            Assert.Equal(result.RequiredPropellant, result.Networks[0].RequiredPropellant, 1);
        }

        /// <summary>
        /// 实测回归（2026-09-16 游戏内交会终止）：主网单独差一点、RCS 接力够 ⇒ **必须判可行**。
        /// 数据 = 该次失败的真实状态（出发段烧完：干 5t + H₂ 5373kg + 肼 500kg，引导要 2733.7 m/s；
        /// 主网单独需 5384kg ⇒ 差 10kg 被判"工质不足"而终止交会——其实 RCS 接力还有 ~143 m/s 能力）。
        /// </summary>
        [Fact]
        public void Estimate_MainShortByHair_RcsRelayMakesItFeasible()
        {
            var networks = new List<BurnNetworkInput>
            {
                new BurnNetworkInput(16000f, 4f, 4000f, 5373f),
                new BurnNetworkInput(1500f, 1f, 1500f, 500f, isRcs: true),
            };

            var result = BurnCore.Estimate(2733.7f, 5000f, networks);

            Assert.True(result.Feasible);                                    // 关键：不再刀刃式误判
            Assert.False(result.Networks[0].IsReserve);                      // 主网全烧
            Assert.False(result.Networks[1].IsReserve);                      // RCS 接力补足
            Assert.Equal(5373f, result.Networks[0].RequiredPropellant, 1);   // 主网烧光
            Assert.True(result.Networks[1].RequiredPropellant > 0f);
            Assert.True(result.Networks[1].RequiredPropellant <= 500f);
            Assert.True(result.BurnSeconds > 5373f / 4f);                    // 时长 = 主网段 + RCS 段
            double m0 = 5000 + 5373 + 500, m1 = 5500;
            float capMain = (float)(4000 * Math.Log(m0 / m1));
            float capRcs = (float)(1500 * Math.Log(m1 / (m1 - 500)));
            Assert.Equal(capMain + capRcs, result.MaxDeltaV, 0);
        }

        /// <summary>整船只有 RCS（纯全矢量船）：这时 RCS 就是唯一推力，按主网口径承担整条机动。</summary>
        [Fact]
        public void Estimate_RcsOnlyShip_RcsCarriesManeuver()
        {
            var networks = new List<BurnNetworkInput>
            {
                new BurnNetworkInput(1500f, 1f, 1500f, 500f, isRcs: true),
            };

            var result = BurnCore.Estimate(20f, 1000f, networks);

            Assert.True(result.Feasible);
            Assert.False(result.Networks[0].IsReserve);
            Assert.True(result.Networks[0].RequiredPropellant > 0f);
        }

        /// <summary>主网料尽但 RCS 够 ⇒ 仍可行（物理上"主网尽 → RCS 兜底"），且料算在 RCS 上。</summary>
        [Fact]
        public void Estimate_MainEmpty_FallsBackToRcs()
        {
            var networks = new List<BurnNetworkInput>
            {
                new BurnNetworkInput(16000f, 4f, 4000f, 0f),            // 主网空罐
                new BurnNetworkInput(1500f, 1f, 1500f, 500f, isRcs: true),
            };

            var result = BurnCore.Estimate(20f, 1000f, networks);

            Assert.True(result.Feasible);
            Assert.True(result.Networks[0].IsReserve);              // 主网退为备用（没料）
            Assert.False(result.Networks[1].IsReserve);             // RCS 承担
            Assert.True(result.Networks[1].RequiredPropellant <= 500f);
        }
    }
}
