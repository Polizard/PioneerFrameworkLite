using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **B4a：储罐 part 的燃料存量**（2026-10-17）。每件储罐持有本工质的一份**分布镜像**；
    /// 权威池仍是 `SpaceMapComp`/`PropulsionData` 的工质总量，本组件只用于：
    /// · 燃料质量按储罐位置进入 COM/惯量（燃烧时 COM 会移动）；
    /// · 读数/验收对账（Σtank = 池）。
    ///
    /// **刻意不写 `AccumulateStats`**：质量口径保持 `ShipStats.mass` = 结构质量；
    /// 湿质量仍由推进源工质总量与惯量合成单独计入，避免双计。
    /// </summary>
    public class PropellantTankComponentData : PartComponentData
    {
        public string propellantDefName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）
        public double capacityKg;
        /// <summary>当前存量（分布镜像，kg）。由池变化时按容量比例重算。</summary>
        public double massKg;
        /// <summary>B5：损毁后已漏光（防重复扣池/扣容量；序列化，读档后不再二次漏光）。</summary>
        public bool leaked;

        static PropellantTankComponentData()
        {
            ComponentRegistry.Register<PropellantTankComponentData>();
        }

        public override PartComponentData? Clone()
        {
            var dst = ComponentRegistry.Create(TypeId) as PropellantTankComponentData;
            if (dst == null) return null;
            dst.propellantDefName = propellantDefName;
            dst.capacityKg = capacityKg;
            dst.massKg = massKg;
            dst.leaked = leaked;
            return dst;
        }

        public override void AccumulateStats(ref ShipStats s) { }

        public override void ExposeData()
        {
            string? loadedPropellant = propellantDefName;
            Scribe_Values.Look(ref loadedPropellant, "tankPropellant");
            if (loadedPropellant != null)
                propellantDefName = loadedPropellant;
            Scribe_Values.Look(ref capacityKg, "tankCapacityKg", 0.0);
            Scribe_Values.Look(ref massKg, "tankMassKg", 0.0);
            Scribe_Values.Look(ref leaked, "tankLeaked", false);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(propellantDefName ?? "");
            w.Write(capacityKg);
            w.Write(massKg);
            w.Write(leaked);
        }

        public override void Read(BinaryReader r)
        {
            propellantDefName = r.ReadString() ?? "";
            capacityKg = r.ReadDouble();
            massKg = r.ReadDouble();
            leaked = r.ReadBoolean();
        }
    }
}
