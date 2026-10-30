using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **货舱**（2026-10-22 部件功能化 §5 的 `cargoCapacity` 生产者）。
    ///
    /// 只把 `capacityKg` 累加进 `ShipStats.cargoCapacity`。**刻意不乘效率**：容量是几何量
    /// （舱壁围出来的体积），失修不改变能装多少；"装得慢/装坏了"属于装卸规则，不是容量。
    /// 与 `PropellantTankComponentData` 的区别：那个是**工质**分布镜像（权威池在地图/实体侧），
    /// 这个是**载荷**容量，只给后续打捞/经济线读（§10 未定项：与打捞的接口口径未定）。
    /// </summary>
    public class CargoComponentData : PartComponentData
    {
        /// <summary>载货容量（kg，≥0）。</summary>
        public float capacityKg;

        static CargoComponentData()
        {
            ComponentRegistry.Register<CargoComponentData>();
        }

        public override PartRole Role => PartRole.Cargo;

        public override void AccumulateStats(ref ShipStats s, float efficiency)
        {
            s.cargoCapacity += capacityKg;   // 容量不随维护值变（见类头注）
        }

        public override bool ApplyPreset(string key, string value)
        {
            if (key != "capacityKg") return false;
            if (!TryParseFloat(value, out float v)) return false;
            capacityKg = v;
            return true;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref capacityKg, "cargoCapacityKg", 0f);
        }

        public override void Write(BinaryWriter w) => w.Write(capacityKg);

        public override void Read(BinaryReader r) => capacityKg = r.ReadSingle();
    }
}
