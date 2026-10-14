using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>舱室组件：对应一张 RW 2D 地图。尺寸按部件水平面物理尺寸（1格=1米）。</summary>
    public class RoomComponentData : PartComponentData
    {
        public string roomTypeDefName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）; // 舱室类型 Def 名（空 = 默认类型）
        public int mapWidth = 10;
        public int mapHeight = 10;
        public float dryMass = 400f; // 组件自重 kg

        static RoomComponentData()
        {
            ComponentRegistry.Register<RoomComponentData>();
        }

        public override void AccumulateStats(ref ShipStats s)
        {
            s.mass += dryMass;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref roomTypeDefName, "roomTypeDefName");
            Scribe_Values.Look(ref mapWidth, "mapWidth", 10);
            Scribe_Values.Look(ref mapHeight, "mapHeight", 10);
            Scribe_Values.Look(ref dryMass, "dryMass", 400f);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(roomTypeDefName ?? "");
            w.Write(mapWidth);
            w.Write(mapHeight);
            w.Write(dryMass);
        }

        public override void Read(BinaryReader r)
        {
            roomTypeDefName = r.ReadString();
            mapWidth = r.ReadInt32();
            mapHeight = r.ReadInt32();
            dryMass = r.ReadSingle();
        }
    }
}
