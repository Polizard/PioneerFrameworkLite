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
    /// 每次由 SpaceGameComp.GetGroupPropulsionSource 现建（物理步内实体集稳定；
    /// 并行物理区只读：Networks/GetPropellantMass/EffectiveVe；消耗在主线程 TryConsume）。
    /// </summary>
    public class GroupPropulsionAggregator : IPropulsionSource
    {
        private readonly SpaceEntityGroup group;
        private readonly SpaceGameComp spaceGame;

        private List<PropellantNetwork> cachedNetworks = new List<PropellantNetwork>();
        private bool dirty = true;

        public GroupPropulsionAggregator(SpaceEntityGroup group, SpaceGameComp spaceGame)
        {
            this.group = group;
            this.spaceGame = spaceGame;
        }

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
                }
            }
            return nets;
        }

        public float GetPropellantMass(PropellantDef def)
        {
            if (def == null) return 0f;
            float total = 0f;
            foreach (int eid in group.GetEntityIds())
            {
                var e = spaceGame.GetEntityById(eid);
                if (e != null) total += e.GetPropellantMass(def);
            }
            return total;
        }

        /// <summary>沿实体分摊扣料（同构同率 → 各枚余额均等；先吃存量大的，天然按可用量截断）。</summary>
        public float TryConsumePropellant(PropellantDef def, float kg)
        {
            if (def == null || kg <= 0f) return 0f;
            float remaining = kg;
            foreach (int eid in group.GetEntityIds())
            {
                if (remaining <= 0f) break;
                var e = spaceGame.GetEntityById(eid);
                if (e == null) continue;
                remaining -= e.TryConsumePropellant(def, remaining);
            }
            return kg - remaining;
        }

        public float TotalPropellantMass
        {
            get
            {
                float total = 0f;
                foreach (int eid in group.GetEntityIds())
                {
                    var e = spaceGame.GetEntityById(eid);
                    if (e != null) total += e.TotalPropellantMass;
                }
                return total;
            }
        }

        public float TotalPropellantCapacity
        {
            get
            {
                float total = 0f;
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
