using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>纯数据工质网络输入，供 <see cref="BurnCore"/> 使用。</summary>
    public struct BurnNetworkInput
    {
        public float Thrust;
        public float MassFlow;
        public float Ve;
        public float AvailableMass;

        /// <summary>
        /// RCS 网（全矢量）：**不参与机动的 ΔV 分配**——对准期出力、主网尽时兜底；
        /// 只有在整船没有主网时才按主网口径承担整条机动。默认 false ⇒ 既有调用点（单网/主网）行为逐位不变。
        /// </summary>
        public bool IsRcs;

        public BurnNetworkInput(float thrust, float massFlow, float ve, float availableMass, bool isRcs = false)
        {
            Thrust = thrust;
            MassFlow = massFlow;
            Ve = ve;
            AvailableMass = availableMass;
            IsRcs = isRcs;
        }
    }

    /// <summary>单个网络在纯燃烧估算中的结果。</summary>
    public struct BurnNetworkResult
    {
        /// <summary>该网络承担的**冲量份额**对应 ΔV（m/s，按推力占比）——份额读数，不是"它负责这段 ΔV"。</summary>
        public float AllocatedDv;
        /// <summary>本次机动从该网络抽取的工质（kg，**按质量流率占比**——与 SpaceGameComp 实际扣料口径一致）。</summary>
        public float RequiredPropellant;
        public float Available;
        /// <summary>该网络单独烧完的可达 ΔV（m/s，近似）。</summary>
        public float MaxDv;
        public bool Feasible;
        /// <summary>true = 本次机动不动它（RCS 备用 / 未承担机动的网络）。</summary>
        public bool IsReserve;
    }

    /// <summary>纯火箭方程估算结果，不依赖 RimWorld / UnityEngine 类型。</summary>
    public struct BurnEstimateCore
    {
        public bool Feasible;
        public float RequiredPropellant;
        public float BurnSeconds;
        public float MaxDeltaV;
        public List<BurnNetworkResult> Networks;
    }

    /// <summary>
    /// 纯燃烧估算核心。**口径 = SpaceGameComp 的实际燃烧语义**（2026-09-16 修正，此前与物理不一致）：
    ///   · 主网（非 RCS）承担机动：多个主网并行烧，各自按**自身质量流率**耗料
    ///     （SpaceGameComp 扣料 = dmTotal 按 ṁ_i/Σṁ 分摊），总 ΔV = ve_eff·ln(m0/m1)，ve_eff = ΣF/Σṁ；
    ///   · **RCS 网不参与机动分配**（它只在对准期出力、主网尽时兜底）⇒ 结果里标 IsReserve、需求 0；
    ///   · 整船没有主网时（纯全矢量船）RCS 才是唯一推力，按主网口径承担整条机动；
    ///   · 主网料不足而 RCS 够 ⇒ 按 RCS 兜底重算（仍有料就说能烧，不误判不可行）。
    /// **单网输入的结果与旧实现逐位相同**（旧式 dmI = m0(1−e^(−dv_i/ve_i))，单网时等于新式
    /// dmTotal = m0(1−e^(−dv/ve_eff))；BurnSeconds 也由 max(dmI/ṁ) 退化为同一值）。
    /// 与 <see cref="BurnCalculator"/> 的数学部分保持一致，便于独立单元测试。
    /// </summary>
    public static class BurnCore
    {
        public static BurnEstimateCore Estimate(
            float dvMps,
            float dryMass,
            IReadOnlyList<BurnNetworkInput> networks)
        {
            var est = new BurnEstimateCore();
            if (networks == null || networks.Count == 0 || dvMps <= 0f)
            {
                return est;
            }

            // 角色划分（ṁ<=0 的网络直接不参与）
            var isRcs = new bool[networks.Count];
            int mainCount = 0, rcsCount = 0;
            for (int i = 0; i < networks.Count; i++)
            {
                if (networks[i].MassFlow <= 0f) continue;
                isRcs[i] = networks[i].IsRcs;
                if (isRcs[i]) rcsCount++; else mainCount++;
            }
            if (mainCount == 0 && rcsCount == 0)
            {
                return est; // 无可用网络
            }

            // 承担机动的网络：优先主网；没有主网 → RCS 就是唯一推力
            bool useMain = mainCount > 0;
            var core = EstimateOver(networks, isRcs, useMain, dvMps, dryMass);

            // 主网单独不够 → **接力**（主网全烧，余量由 RCS 补足）。
            // 这是物理真做的事：无 Map 船未对准期由 RCS 出力、对准后主网接手、主网尽 RCS 兜底，
            // 两者是**顺序接力**而不是并行 ⇒ 能力应相加，不是二选一（2026-09-16 实测：
            // 引导要 2733.7 m/s，主网单独差 10 kg 被判"工质不足"而终止交会，其实 RCS 还有 143 m/s）。
            if (!core.Feasible && useMain && rcsCount > 0)
            {
                var relay = EstimateRelay(networks, isRcs, dvMps, dryMass);
                if (relay.Feasible) core = relay;
            }

            return core;
        }

        /// <summary>
        /// 接力估算：主网烧到尽（它的可达 Δv 全部兑现），剩余 Δv 由 RCS 在"主网烧完后的质量"上补足。
        /// 输出：主网 RequiredPropellant = 它的全部存量；RCS = 补足量；BurnSeconds = 两段时长之和。
        /// </summary>
        private static BurnEstimateCore EstimateRelay(
            IReadOnlyList<BurnNetworkInput> networks, bool[] isRcs, float dvMps, double dryMass)
        {
            var est = new BurnEstimateCore { Networks = new List<BurnNetworkResult>(networks.Count) };
            double mDry = Math.Max(dryMass, 1.0);

            double m0 = mDry;
            float fMain = 0f, mfMain = 0f, availMain = 0f;
            float fRcs = 0f, mfRcs = 0f, availRcs = 0f;
            for (int i = 0; i < networks.Count; i++)
            {
                var n = networks[i];
                if (n.MassFlow <= 0f) continue;
                m0 += n.AvailableMass;
                if (isRcs[i]) { fRcs += n.Thrust; mfRcs += n.MassFlow; availRcs += n.AvailableMass; }
                else { fMain += n.Thrust; mfMain += n.MassFlow; availMain += n.AvailableMass; }
            }
            if (mfMain <= 0f || mfRcs <= 0f || m0 <= mDry) return est;

            float veMain = fMain / mfMain;
            float veRcs = fRcs / mfRcs;
            double m1 = Math.Max(m0 - availMain, mDry);                 // 主网烧完后的质量
            double capMain = veMain > 0f ? veMain * Math.Log(m0 / m1) : 0.0;
            double capRcs = veRcs > 0f && m1 > mDry ? veRcs * Math.Log(m1 / (m1 - availRcs)) : 0.0;

            double remainDv = Math.Max(dvMps - capMain, 0.0);
            double dmRcs = veRcs > 0f ? m1 * (1.0 - Math.Exp(-remainDv / veRcs)) : 0.0;

            est.RequiredPropellant = (float)(availMain + dmRcs);
            // ★ 恒定加速度政策下，**整段按同一条命令加速度走**（执行器只有一个 `burnCommandAccel`，
            //   主网段与 RCS 接力段都是它）⇒ 时长 = Δv / (F主/m₀)。旧式"主网段+ RCS 段各自全油门"
            //   与新执行器不一致（会让居中用的 t_b 偏短 ~6%）⇒ 与 `EstimateOver` 用同一条公式。
            //   ⚠ 唯一残余：RCS 接力段的**可用**加速度可能低于命令（RCS 推力小）⇒ 那一段会按可用烧、
            //   实际略长于估算（执行器有 clamp + 一行日志）。登记：属边缘路径。
            est.BurnSeconds = (fMain > 0f && m0 > 0.0)
                ? (float)(dvMps * m0 / fMain)
                : 0f;
            est.MaxDeltaV = (float)(capMain + capRcs);
            est.Feasible = dmRcs <= availRcs;

            for (int i = 0; i < networks.Count; i++)
            {
                var n = networks[i];
                bool participates = n.MassFlow > 0f;
                bool main = !isRcs[i];
                var r = new BurnNetworkResult
                {
                    Available = n.AvailableMass,
                    MaxDv = (n.Ve > 0f && m0 > mDry)
                        ? (float)(n.Ve * Math.Log(m0 / (m0 - n.AvailableMass)))
                        : 0f,
                    IsReserve = !participates,
                    Feasible = true,
                };
                if (participates)
                {
                    // 主网全烧；RCS 补足剩余 Δv（两者按各自的 Δv 贡献记，不是冲量份额）
                    double need = main ? availMain : dmRcs;
                    r.RequiredPropellant = (float)need;
                    r.AllocatedDv = (float)(main ? capMain : Math.Min(remainDv, capRcs));
                    r.Feasible = main || dmRcs <= availRcs;
                    // 空罐网络（无需供料）算备用：它不出力，别出现在"工质不足"里
                    r.IsReserve = need <= 0.0;
                }
                est.Networks.Add(r);
            }
            return est;
        }

        /// <summary>按指定角色集合估算一次机动；每个输入网络都会得到一条结果（未承担者标 IsReserve）。</summary>
        private static BurnEstimateCore EstimateOver(
            IReadOnlyList<BurnNetworkInput> networks, bool[] isRcs, bool useMain,
            float dvMps, float dryMass)
        {
            var est = new BurnEstimateCore { Networks = new List<BurnNetworkResult>(networks.Count) };

            double mDry = Math.Max(dryMass, 1.0);
            // m0 = **全船质量**：干质量 + 所有网络的工质（RCS 罐里的料也是被加速的质量），
            // 但本次机动只从承担机动的网络里抽料。
            double m0 = mDry;
            float fTotal = 0f, mfTotal = 0f, availActive = 0f;
            for (int i = 0; i < networks.Count; i++)
            {
                var n = networks[i];
                if (n.MassFlow <= 0f) continue;
                m0 += n.AvailableMass;
                // 承担机动的网络 = 主网（useMain）或整船没有主网时的 RCS 网
                bool participates = useMain ? !isRcs[i] : isRcs[i];
                if (!participates) continue;
                fTotal += n.Thrust;
                mfTotal += n.MassFlow;
                availActive += n.AvailableMass;
            }
            if (mfTotal <= 0f) return est;

            float veEff = fTotal / mfTotal;
            // 需抽取的总工质（Tsiolkovsky，m0 为全船初始质量 ⇒ 与积分器的 a = F/m 逐位同源）
            double dmTotal = veEff > 0f ? m0 * (1.0 - Math.Exp(-dvMps / veEff)) : 0.0;
            est.RequiredPropellant = (float)dmTotal;
            // ★ 燃烧时长 = **命令恒定加速度**下的时长（用户 2026-09-19 裁定）：
            //   机动 = Δv + 加速度；油门随时适配质量 ⇒ `a_cmd = F参与 / m₀`（点火质量 ⇒ 可行域里最紧的恒定值），
            //   `t_b = Δv / a_cmd`。
            //   ⚠ 旧式 `dmTotal / mfTotal`（= 全油门时长）会照**可用上限**烧，而上限 `F/m` 随料耗上升 ⇒
            //   Δv 后重前轻（实测交付率 76→90/10s）。恒定加速度把它摊平（实测恒为 74.671/10s ✓）。
            //   ⚠ **回写（引以为戒）**：我曾断言"该质心偏移是 e 的主项"——**实测证伪** ✗：
            //   摊平后 e 反而 +18%（1.108e-3，窗口宽 10% 的二阶项）。e 的主项是**执行延迟**
            //   （首次交付晚一步 = 2 s，见 `ignitionTick` 处注释），与油门政策无关。
            //   本政策真正的收益 = **模型 ≡ 执行器**（B2.2 的闸门），不是当场把 e 压小。
            //   工质不变（火箭方程只依赖总 Δv）—— 只是烧久一点（本场景 98 s ⇒ ~108 s）。
            est.BurnSeconds = (fTotal > 0f && m0 > 0.0)
                ? (float)(dvMps * m0 / fTotal)
                : (float)(dmTotal / mfTotal);
            double m1 = m0 - availActive;
            est.MaxDeltaV = (veEff > 0f && m1 > 0.0)
                ? (float)(veEff * Math.Log(m0 / m1))               // 承担机动的网络全烧完的可达 ΔV
                : 0f;

            bool allFeasible = true;
            for (int i = 0; i < networks.Count; i++)
            {
                var n = networks[i];
                bool participates = n.MassFlow > 0f && (useMain ? !isRcs[i] : isRcs[i]);
                var r = new BurnNetworkResult
                {
                    Available = n.AvailableMass,
                    MaxDv = (n.Ve > 0f && m0 > mDry)
                        ? (float)(n.Ve * Math.Log(m0 / (m0 - n.AvailableMass)))
                        : 0f,
                    IsReserve = !participates,
                };
                if (participates)
                {
                    r.AllocatedDv = fTotal > 0f ? dvMps * (n.Thrust / fTotal) : 0f;   // 冲量份额
                    r.RequiredPropellant = (float)(dmTotal * (n.MassFlow / mfTotal)); // 与实际扣料同口径
                    r.Feasible = n.MassFlow > 0f && (double)r.RequiredPropellant <= n.AvailableMass;
                    if (!r.Feasible) allFeasible = false;
                }
                else
                {
                    r.Feasible = true; // 备用网络不欠账
                }
                est.Networks.Add(r);
            }

            est.Feasible = allFeasible;
            return est;
        }
    }
}
