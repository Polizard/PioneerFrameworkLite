using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **散热器**（2026-10-22 部件功能化 §5 热闭环的排出端）。
    ///
    /// 只把 `dissipationWatts` 累加进 `ShipStats.heatDissipation`。**不做传导网络**——
    /// 前件文档与本次裁定同结论：热只做"总产热 vs 总散热"的标量（§5），不做逐件温度场。
    ///
    /// 质量由结构件给；本组件没有质量、没有质量贡献。
    /// </summary>
    public class RadiatorComponentData : PartComponentData
    {
        /// <summary>额定散热功率（W，≥0）。</summary>
        public float dissipationWatts;

        static RadiatorComponentData()
        {
            ComponentRegistry.Register<RadiatorComponentData>();
        }

        public override PartRole Role => PartRole.Radiator;

        public override void AccumulateStats(ref ShipStats s, float efficiency)
        {
            // §11.4：散热是运转能力（翅片变形/泵失效 ⇒ 散不动），乘效率。
            s.heatDissipation += dissipationWatts * efficiency;
        }

        public override bool ApplyPreset(string key, string value)
        {
            if (key != "dissipationWatts") return false;
            if (!TryParseFloat(value, out float v)) return false;
            dissipationWatts = v;
            return true;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref dissipationWatts, "radiatorDissipation", 0f);
        }

        public override void Write(BinaryWriter w) => w.Write(dissipationWatts);

        public override void Read(BinaryReader r) => dissipationWatts = r.ReadSingle();
    }
}
