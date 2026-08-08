using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>货舱组件：货运容量。载重计入质量（装载系统接入后换为实际载重）。</summary>
    public class CargoComponentData : PartComponentData
    {
        public float capacity = 10000f; // 容量 kg
        public float dryMass = 200f;    // 组件自重 kg

        static CargoComponentData()
        {
            ComponentRegistry.Register<CargoComponentData>();
        }

        public override void AccumulateStats(ref ShipStats s)
        {
            s.cargoCapacity += capacity;
            s.mass += dryMass + capacity; // 结构 + 满载货物（与旧 CargoHold 特判口径一致）
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref capacity, "capacity", 10000f);
            Scribe_Values.Look(ref dryMass, "dryMass", 200f);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(capacity);
            w.Write(dryMass);
        }

        public override void Read(BinaryReader r)
        {
            capacity = r.ReadSingle();
            dryMass = r.ReadSingle();
        }
    }
}
