using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// 工质罐组件：推进剂存储。质量口径为湿重满值（推进剂静态满箱），
    /// 燃耗真实化接入后改为随推进剂动态。
    /// </summary>
    public class PropellantTankComponentData : PartComponentData
    {
        public float dryMass = 500f;   // 罐体干重 kg
        public float wetMass = 5000f;  // 湿重（含满箱推进剂）kg

        static PropellantTankComponentData()
        {
            ComponentRegistry.Register<PropellantTankComponentData>();
        }

        public override void AccumulateStats(ref ShipStats s)
        {
            s.propellant += wetMass - dryMass;
            s.mass += wetMass; // 湿重满值口径：罐体 + 满推进剂
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref dryMass, "dryMass", 500f);
            Scribe_Values.Look(ref wetMass, "wetMass", 5000f);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(dryMass);
            w.Write(wetMass);
        }

        public override void Read(BinaryReader r)
        {
            dryMass = r.ReadSingle();
            wetMass = r.ReadSingle();
        }
    }
}
