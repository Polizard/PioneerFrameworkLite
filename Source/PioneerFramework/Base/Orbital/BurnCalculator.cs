using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>单个工质网络在本机动的分配明细。</summary>
    public struct NetworkEstimate
    {
        public PropellantDef Propellant;
        public float Ve;
        public float MassFlow;
        public float AllocatedDv;        // 本次机动按推力比例分给该网络的 ΔV（m/s）
        public float RequiredPropellant; // 完成该分配所需工质（kg）
        public float Available;          // 当前可用存量（kg）
        public float MaxDv;              // 该网络单独全部烧完的可达 ΔV（m/s，近似）
        public bool Feasible;            // 所需 ≤ 可用
    }

    /// <summary>
    /// 一次机动的燃烧估算结果（MechJeb 式：用实际推进参数反推规划值）。
    /// 多工质网络并行烧：ΔV 按各网络推力比例分配，各自消耗各自工质，时长取最慢网络。
    /// </summary>
    public struct BurnEstimate
    {
        /// <summary>全部启用网络都能完成本次机动。</summary>
        public bool Feasible;

        /// <summary>总所需工质（kg）= Σ 各网络所需。</summary>
        public float RequiredPropellant;

        /// <summary>燃烧时长（s）= max(Δm_i/ṁ_i)（并行烧，最慢网络决定）。</summary>
        public float BurnSeconds;

        /// <summary>等效网络（全网络并行）当前最大可达 ΔV（m/s）。</summary>
        public float MaxDeltaV;

        /// <summary>逐网络明细（UI 展示用）。</summary>
        public List<NetworkEstimate> Networks;
    }

    /// <summary>
    /// 机动燃烧估算工具（火箭方程精确式，多工质网络并行烧）。
    /// </summary>
    public static class BurnCalculator
    {
        /// <summary>
        /// 估算一次机动（按玩家策略启用网络）。
        /// </summary>
        /// <param name="dvMps">计划 ΔV（m/s，标量）。</param>
        /// <param name="dryMass">干质量（kg，结构质量，不含工质）。</param>
        /// <param name="src">推进源（SpaceMapComp 或实体自带推进数据，取工质存量）。</param>
        /// <param name="networks">全部已注册工质网络。</param>
        /// <param name="enabled">玩家启用的工质集合（null/空 = 全启用）。</param>
        public static BurnEstimate Estimate(float dvMps, float dryMass, IPropulsionSource src,
            IReadOnlyList<PropellantNetwork> networks, HashSet<PropellantDef> enabled = null)
        {
            var est = new BurnEstimate();

            // 收集启用网络
            var active = new List<PropellantNetwork>();
            if (networks != null)
            {
                foreach (var n in networks)
                {
                    if (n == null || n.MassFlow <= 0f) continue;
                    if (enabled != null && !enabled.Contains(n.propellant)) continue;
                    active.Add(n);
                }
            }
            if (active.Count == 0 || dvMps <= 0f)
                return est; // 无启用网络 / 无计划 → 不可行

            // 交给纯逻辑核计算（数学部分与 BurnCore 保持单一来源）
            var inputs = new List<BurnNetworkInput>(active.Count);
            foreach (var n in active)
            {
                inputs.Add(new BurnNetworkInput(
                    n.Thrust,
                    n.MassFlow,
                    n.Ve,
                    n.AvailableMass(src)));
            }

            var core = BurnCore.Estimate(dvMps, dryMass, inputs);
            est.Feasible = core.Feasible;
            est.RequiredPropellant = core.RequiredPropellant;
            est.BurnSeconds = core.BurnSeconds;
            est.MaxDeltaV = core.MaxDeltaV;

            if (core.Networks != null)
            {
                est.Networks = new List<NetworkEstimate>(core.Networks.Count);
                for (int i = 0; i < core.Networks.Count; i++)
                {
                    var n = active[i];
                    var r = core.Networks[i];
                    est.Networks.Add(new NetworkEstimate
                    {
                        Propellant = n.propellant,
                        Ve = n.Ve,
                        MassFlow = n.MassFlow,
                        AllocatedDv = r.AllocatedDv,
                        RequiredPropellant = r.RequiredPropellant,
                        Available = r.Available,
                        MaxDv = r.MaxDv,
                        Feasible = r.Feasible
                    });
                }
            }

            return est;
        }
    }
}
