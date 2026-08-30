using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// 结构自重组件：给无 Map 纯物理实体（EVA 单兵 / 穿梭机 / 导弹）提供真实结构质量。
    /// 碰撞动量交换 / 爆炸冲量按此质量结算，避免默认 1kg 兜底导致被弹飞。
    /// </summary>
    public class HullMassComponent : PartComponentData
    {
        public float mass = 100f;

        static HullMassComponent()
        {
            ComponentRegistry.Register<HullMassComponent>();
        }

        public override void AccumulateStats(ref ShipStats s)
        {
            s.mass += mass;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref mass, "mass", 100f);
        }

        public override void Write(BinaryWriter w) => w.Write(mass);

        public override void Read(BinaryReader r) => mass = r.ReadSingle();
    }
}
