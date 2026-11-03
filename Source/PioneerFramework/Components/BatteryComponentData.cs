using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **电池**（2026-10-22 部件功能化 §5 步 4 电闭环的储能端）。
    ///
    /// 口径（**关键**）：本组件只给**容量与充放上限**三个静态量，**不存当前电量**。
    /// 当前存量是**群级**状态 `SpaceEntityGroup.StoredEnergyJ`（一个 float，随存档落盘）：
    /// · 理由 = §0-1「每步循环里不得遍历组件」：存量每步都在变，若逐件存就要每步扫组件；
    /// · 代价 = 电池不再"按件记账"（击毁某件不会带走它那一份电量）——首版接受，
    ///   真正做"按件储能"要连"漏电/起火/分舱"一起设计，见交接的挂账。
    ///
    /// 三个量都乘维护效率（失修电池容量与充放能力一起缩）。
    /// </summary>
    public class BatteryComponentData : PartComponentData
    {
        /// <summary>容量（J，≥0）。</summary>
        public float capacityJoules;

        /// <summary>放电上限（W，≥0）。0 = 不能放电（只能充）。</summary>
        public float maxDischargeWatts;

        /// <summary>充电上限（W，≥0）。0 = 不能充电。</summary>
        public float maxChargeWatts;

        static BatteryComponentData()
        {
            ComponentRegistry.Register<BatteryComponentData>();
        }

        public override PartRole Role => PartRole.Battery;

        public override void AccumulateStats(ref ShipStats s, float efficiency)
        {
            s.powerStorage += capacityJoules * efficiency;
            s.powerDischargeLimit += maxDischargeWatts * efficiency;
            s.powerChargeLimit += maxChargeWatts * efficiency;
        }

        public override bool ApplyPreset(string key, string value)
        {
            switch (key)
            {
                case "capacityJoules":
                    if (!TryParseFloat(value, out float cap)) return false;
                    capacityJoules = cap;
                    return true;
                case "maxDischargeWatts":
                    if (!TryParseFloat(value, out float dis)) return false;
                    maxDischargeWatts = dis;
                    return true;
                case "maxChargeWatts":
                    if (!TryParseFloat(value, out float chg)) return false;
                    maxChargeWatts = chg;
                    return true;
                default:
                    return false;
            }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref capacityJoules, "batteryCapacityJ", 0f);
            Scribe_Values.Look(ref maxDischargeWatts, "batteryDischargeW", 0f);
            Scribe_Values.Look(ref maxChargeWatts, "batteryChargeW", 0f);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(capacityJoules);
            w.Write(maxDischargeWatts);
            w.Write(maxChargeWatts);
        }

        public override void Read(BinaryReader r)
        {
            capacityJoules = r.ReadSingle();
            maxDischargeWatts = r.ReadSingle();
            maxChargeWatts = r.ReadSingle();
        }
    }
}
