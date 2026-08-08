using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>引擎组件：推力（ve × ṁ）与自重。燃烧时消耗推进剂。</summary>
    public class EngineComponentData : PartComponentData
    {
        public float exhaustVelocity = 30000f; // 排气速度 m/s
        public float massFlowRate = 5f;        // 工质流量 kg/s
        public float dryMass = 500f;           // 组件自重 kg

        static EngineComponentData()
        {
            ComponentRegistry.Register<EngineComponentData>();
        }

        public override void AccumulateStats(ref ShipStats s)
        {
            s.mass += dryMass;
            s.thrust += exhaustVelocity * massFlowRate;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref exhaustVelocity, "exhaustVelocity", 30000f);
            Scribe_Values.Look(ref massFlowRate, "massFlowRate", 5f);
            Scribe_Values.Look(ref dryMass, "dryMass", 500f);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(exhaustVelocity);
            w.Write(massFlowRate);
            w.Write(dryMass);
        }

        public override void Read(BinaryReader r)
        {
            exhaustVelocity = r.ReadSingle();
            massFlowRate = r.ReadSingle();
            dryMass = r.ReadSingle();
        }
    }
}
