using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// 群级推进聚合器（IPropulsionSource，2026-09-08 思路 A：弹群治本）。
    /// 弹群 = 1 SpaceEntityGroup 内 N 个无 Map 导弹实体、**每枚自带满罐 PropulsionData**——
    /// 物理核心的单源 burn 路径经本聚合器按「全组引擎同步」工作，数学精确（N 台同型引擎 ≡ 1×N）：
    ///   · Networks：按 (工质, 主网/RCS) 合并各实体引擎（同构 → ṁ 为 N 倍、ve 不变）；
    ///   · GetPropellantMass / Total*：Σ 各实体存量/容量；
    ///   · TryConsumePropellant：沿实体分摊扣料——**燃料归实体自己**：实体阵亡燃料随之消失，
    ///     群质量/推力/总料自动收缩，无任何移交/平分记账。
    /// 实例由 SpaceGameComp.GetGroupPropulsionSource 按「实体集+推进指纹」缓存在 group 上复用
    /// （SpaceEntityGroup.cachedPropulsionAggregator；指纹不变则 Networks 只构建一次）。
    /// 并行物理区只读：Networks/GetPropellantMass/EffectiveVe；消耗在主线程 TryConsume。
    /// </summary>
    public class GroupPropulsionAggregator : IPropulsionSource
    {
        private readonly SpaceEntityGroup group;
        private readonly SpaceGameComp spaceGame;
        private readonly int sourceStamp;

        private List<PropellantNetwork> cachedNetworks = new List<PropellantNetwork>();
        private bool dirty = true;

        /// <param name="sourceStamp">构建时的实体集+推进指纹（见 SpaceEntityGroup.cachedPropulsionStamp）。</param>
        public GroupPropulsionAggregator(SpaceEntityGroup group, SpaceGameComp spaceGame, int sourceStamp)
        {
            this.group = group;
            this.spaceGame = spaceGame;
            this.sourceStamp = sourceStamp;
        }

        /// <summary>本实例对应的指纹：调用方据此判断能否复用缓存实例。</summary>
        public int SourceStamp => sourceStamp;

        public IReadOnlyList<PropellantNetwork> Networks
        {
            get
            {
                if (dirty)
                {
                    cachedNetworks = BuildNetworks();
                    dirty = false;
                }
                return cachedNetworks;
            }
        }

        /// <summary>按 (工质, 主网/RCS) 合并全组实体引擎（镜像 SpaceEntity.Networks 的单实体构建）。</summary>
        private List<PropellantNetwork> BuildNetworks()
        {
            var nets = new List<PropellantNetwork>();
            foreach (int eid in group.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e == null) continue;
                for (int i = 0; i < e.propulsion.Count; i++)
                {
                    var d = e.propulsion[i];
                    if (d == null || d.propellant == null || d.massFlow <= 0f) continue;
                    PropellantNetwork net = null!;
                    for (int j = 0; j < nets.Count; j++)
                    {
                        // 同工质且同引擎类（主/RCS）才合并；同一工质主+副并存时各自成网
                        if (nets[j].propellant == d.propellant && nets[j].HasRcsEngines == d.isVectoredRcs)
                        {
                            net = nets[j];
                            break;
                        }
                    }
                    if (net == null)
                    {
                        net = new PropellantNetwork { propellant = d.propellant };
                        nets.Add(net);
                    }
                    if (d.isVectoredRcs)
                        net.RegisterEngineRcs(d.ve, d.massFlow);
                    else
                        net.RegisterEngine(d.ve, d.massFlow);
                    net.RegisterGasDraws(d.gasIntakes, d.massFlow);   // [A5] 附带进气（空 = 纯火箭）
                    net.RegisterEnvelope(d.ve, d.massFlow, d.densityExponent, d.machCurve);   // [A4] 包线（火箭项不入表）
                }
            }
            return nets;
        }

        // ⚠ 四个质量成员一律 **double**（2026-09-27 用户裁定）：它们是"每步扣一点"的累加量，
        //    float 在 72 t 处只剩 7.8 g 分辨率 ⇒ 一步的舍入差被 `a=F/m` 放大成可测的 Δv/位置差
        //    （逐步指纹实测：`burnDbgMass Δ=0.015625 kg = 2 ULP`）。速率类（ve/ṁ/推力）保持 float。
        public double GetPropellantMass(PropellantDef def)
        {
            if (def == null) return 0.0;
            double total = 0.0;
            foreach (int eid in group.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e != null) total += e.GetPropellantMass(def);
            }
            return total;
        }

        /// <summary>沿实体分摊扣料（同构同率 → 各枚余额均等；先吃存量大的，天然按可用量截断）。**double**：同上。</summary>
        public double TryConsumePropellant(PropellantDef def, double kg)
        {
            if (def == null || kg <= 0.0) return 0.0;
            double remaining = kg;
            foreach (int eid in group.GetEntityIds())
            {
                if (remaining <= 0.0) break;
                var e = spaceGame.GetEntityById(eid);
                if (e == null) continue;
                remaining -= e.TryConsumePropellant(def, remaining);
            }
            return kg - remaining;
        }

        public double TotalPropellantMass
        {
            get
            {
                double total = 0.0;
                foreach (int eid in group.GetEntityIds())
                {
                    var e = spaceGame.GetEntityById(eid);
                    if (e != null) total += e.TotalPropellantMass;
                }
                return total;
            }
        }

        public double TotalPropellantCapacity
        {
            get
            {
                double total = 0.0;
                foreach (int eid in group.GetEntityIds())
                {
                    var e = spaceGame.GetEntityById(eid);
                    if (e != null) total += e.TotalPropellantCapacity;
                }
                return total;
            }
        }

        /// <summary>等效排气速度 ve = Σ(ve·ṁ)/Σṁ（m/s，全网络并行烧的加权平均）。</summary>
        public float EffectiveVe
        {
            get
            {
                float f = 0f, mf = 0f;
                var nets = Networks;
                for (int i = 0; i < nets.Count; i++)
                {
                    f += nets[i].Thrust;
                    mf += nets[i].MassFlow;
                }
                return mf > 0f ? f / mf : 0f;
            }
        }
    }
}
