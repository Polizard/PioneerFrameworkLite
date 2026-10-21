using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// **B6：多图/多源推进聚合**——把一条船内所有图（`SpaceMapComp`）与无图实体的推进源
    /// 合成一个 `IPropulsionSource`。所有图的工质都计入；`MapSystemFlags.Propulsion` 关闭的图
    /// 只从**网络/推力**里剔除，其工质质量仍计入（质量不会凭空消失）。
    ///
    /// 网络合并用 `PropellantNetwork.MergeFrom`（Σ(ve·ṁ)/Σṁ/包线/附带进气），
    /// 与单图内部的多引擎聚合同一套线性口径。实例由 `SpaceGameComp.GetGroupPropulsionSource`
    /// 按指纹缓存在 group 上；本类只读（`Networks` 懒构建一次）。
    /// </summary>
    public sealed class CompositePropulsionSource : IPropulsionSource
    {
        private readonly List<IPropulsionSource> sources;
        private List<PropellantNetwork>? cachedNetworks;

        public int SourceStamp { get; }

        public CompositePropulsionSource(List<IPropulsionSource> sources, int sourceStamp)
        {
            this.sources = sources ?? new List<IPropulsionSource>();
            SourceStamp = sourceStamp;
        }

        public IReadOnlyList<PropellantNetwork> Networks
        {
            get
            {
                cachedNetworks ??= BuildNetworks();
                return cachedNetworks;
            }
        }

        private List<PropellantNetwork> BuildNetworks()
        {
            var merged = new Dictionary<PropellantDef, PropellantNetwork>();
            var result = new List<PropellantNetwork>();
            for (int s = 0; s < sources.Count; s++)
            {
                var src = sources[s];
                if (src == null) continue;
                // 切断推进系统的图：引擎不出力；燃料仍在 GetPropellantMass 里计数。
                if (src is SpaceMapComp mc && !mc.PropulsionEnabled) continue;

                var nets = src.Networks;
                for (int i = 0; i < nets.Count; i++)
                {
                    var net = nets[i];
                    if (net == null || net.propellant == null || net.MassFlow <= 0f) continue;
                    if (!merged.TryGetValue(net.propellant, out var target))
                    {
                        target = new PropellantNetwork { propellant = net.propellant };
                        merged[net.propellant] = target;
                        result.Add(target);
                    }
                    target.MergeFrom(net);
                }
            }
            return result;
        }

        public double GetPropellantMass(PropellantDef def)
        {
            if (def == null) return 0.0;
            double total = 0.0;
            for (int i = 0; i < sources.Count; i++)
                total += sources[i]?.GetPropellantMass(def) ?? 0.0;
            return total;
        }

        public double TryConsumePropellant(PropellantDef def, double kg)
        {
            if (def == null || kg <= 0.0) return 0.0;
            double remaining = kg;
            for (int i = 0; i < sources.Count && remaining > 0.0; i++)
            {
                var src = sources[i];
                if (src == null) continue;
                remaining -= src.TryConsumePropellant(def, remaining);
            }
            return kg - remaining;
        }

        public double TotalPropellantMass
        {
            get
            {
                double total = 0.0;
                for (int i = 0; i < sources.Count; i++)
                    total += sources[i]?.TotalPropellantMass ?? 0.0;
                return total;
            }
        }

        public double TotalPropellantCapacity
        {
            get
            {
                double total = 0.0;
                for (int i = 0; i < sources.Count; i++)
                    total += sources[i]?.TotalPropellantCapacity ?? 0.0;
                return total;
            }
        }

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
