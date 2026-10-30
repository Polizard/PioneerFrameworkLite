using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **反应堆**（2026-10-22 部件功能化 §5 电闭环的供给端）。
    ///
    /// 只做两件事：把 `powerGenWatts` 累加进 `ShipStats.powerGen`、把 `heatProductionWatts` 累加进
    /// `ShipStats.heatProduction`。**不在这里做分配**——电闭环的"按优先级分配 / brownout"是
    /// **快照上的每秒规则**（§0-1：每步循环里不许遍历组件），消费端只乘一个系数。
    ///
    /// ⚠ **产热必须与发电成对填**（前件文档 §10.2 的坑）：只填发电不填产热 ⇒ 热闭环永远不超温，
    /// 反应堆变成白嫖。目录预置里两项一起给（见 `1.7/Defs/ShipParts/ShipParts.xml`）。
    ///
    /// 质量由结构件（`HullMassComponent`）给，本组件**没有质量**（与 `EngineComponentData` 同口径）。
    /// </summary>
    public class ReactorComponentData : PartComponentData
    {
        /// <summary>额定发电功率（W，≥0）。</summary>
        public float powerGenWatts;

        /// <summary>额定产热（W，≥0）——与 <see cref="powerGenWatts"/> 成对填，别只填一边。</summary>
        public float heatProductionWatts;

        static ReactorComponentData()
        {
            ComponentRegistry.Register<ReactorComponentData>();
        }

        public override PartRole Role => PartRole.Reactor;

        public override void AccumulateStats(ref ShipStats s, float efficiency)
        {
            // §11.4：发电与产热都乘效率（失修的反应堆发不满，也不会满功率烧）。
            s.powerGen += powerGenWatts * efficiency;
            s.heatProduction += heatProductionWatts * efficiency;
        }

        public override bool ApplyPreset(string key, string value)
        {
            switch (key)
            {
                case "powerGenWatts":
                    if (!TryParseFloat(value, out float gen)) return false;
                    powerGenWatts = gen;
                    return true;
                case "heatProductionWatts":
                    if (!TryParseFloat(value, out float heat)) return false;
                    heatProductionWatts = heat;
                    return true;
                default:
                    return false;
            }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref powerGenWatts, "reactorPowerGen", 0f);
            Scribe_Values.Look(ref heatProductionWatts, "reactorHeat", 0f);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(powerGenWatts);
            w.Write(heatProductionWatts);
        }

        public override void Read(BinaryReader r)
        {
            powerGenWatts = r.ReadSingle();
            heatProductionWatts = r.ReadSingle();
        }
    }
}
