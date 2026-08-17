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

        public BurnNetworkInput(float thrust, float massFlow, float ve, float availableMass)
        {
            Thrust = thrust;
            MassFlow = massFlow;
            Ve = ve;
            AvailableMass = availableMass;
        }
    }

    /// <summary>单个网络在纯燃烧估算中的结果。</summary>
    public struct BurnNetworkResult
    {
        public float AllocatedDv;
        public float RequiredPropellant;
        public float Available;
        public float MaxDv;
        public bool Feasible;
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
    /// 纯燃烧估算核心：MechJeb 式多工质网络并行烧。
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

            if (networks == null)
            {
                return est;
            }

            var active = new List<BurnNetworkInput>();
            foreach (var n in networks)
            {
                if (n.MassFlow <= 0f)
                {
                    continue;
                }
                active.Add(n);
            }

            if (active.Count == 0 || dvMps <= 0f)
            {
                return est;
            }

            double mDry = Math.Max(dryMass, 1.0);
            double m0 = mDry;
            foreach (var n in active)
            {
                m0 += n.AvailableMass;
            }

            float fTotal = 0f, mfTotal = 0f;
            foreach (var n in active)
            {
                fTotal += n.Thrust;
                mfTotal += n.MassFlow;
            }
            if (mfTotal <= 0f)
            {
                return est;
            }

            est.Networks = new List<BurnNetworkResult>(active.Count);
            float veEff = fTotal / mfTotal;
            bool allFeasible = true;

            foreach (var n in active)
            {
                float fI = n.Thrust;
                float dvI = fTotal > 0f ? dvMps * (fI / fTotal) : 0f;
                float veI = n.Ve;
                float availI = n.AvailableMass;

                float dmI = veI > 0f
                    ? (float)(m0 * (1.0 - Math.Exp(-dvI / veI)))
                    : 0f;
                float maxDvI = veI > 0f && m0 > mDry
                    ? (float)(veI * Math.Log(m0 / mDry))
                    : 0f;
                float tI = n.MassFlow > 0f ? dmI / n.MassFlow : 0f;

                var result = new BurnNetworkResult
                {
                    AllocatedDv = dvI,
                    RequiredPropellant = dmI,
                    Available = availI,
                    MaxDv = maxDvI,
                    Feasible = dmI <= availI
                };
                est.Networks.Add(result);
                est.RequiredPropellant += dmI;
                if (tI > est.BurnSeconds)
                {
                    est.BurnSeconds = tI;
                }
                if (!result.Feasible)
                {
                    allFeasible = false;
                }
            }

            est.MaxDeltaV = m0 > mDry
                ? (float)(veEff * Math.Log(m0 / mDry))
                : 0f;
            est.Feasible = allFeasible;
            return est;
        }
    }
}
