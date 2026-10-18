using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **B4a：推进器 part 的引擎元数据**（2026-10-17）。运行时聚合仍按 M2 甲留在实体/地图级，
    /// 但每件推进器把 ve/ṁ/轴/RCS/工质记在 part 上，供：
    /// · 推力作用点/偏心推进力矩（B4b）；
    /// · 分级/拆除时按 part 重算推进（后续）；
    /// · 读数/验收核对目录来源。
    /// **本组件没有质量**（结构质量由 HullMassComponent 给）。
    /// </summary>
    public class EngineComponentData : PartComponentData
    {
        public float exhaustVelocity;
        public float massFlowRate;
        public ThrustAxis thrustAxis = ThrustAxis.Back;
        public bool isVectoredRcs;
        public string propellantDefName = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        static EngineComponentData()
        {
            ComponentRegistry.Register<EngineComponentData>();
        }

        public override PartComponentData? Clone()
        {
            var dst = ComponentRegistry.Create(TypeId) as EngineComponentData;
            if (dst == null) return null;
            dst.exhaustVelocity = exhaustVelocity;
            dst.massFlowRate = massFlowRate;
            dst.thrustAxis = thrustAxis;
            dst.isVectoredRcs = isVectoredRcs;
            dst.propellantDefName = propellantDefName;
            return dst;
        }

        public override void AccumulateStats(ref ShipStats s) { }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref exhaustVelocity, "engineVe", 0f);
            Scribe_Values.Look(ref massFlowRate, "engineMassFlow", 0f);
            Scribe_Values.Look(ref thrustAxis, "engineAxis", ThrustAxis.Back);
            Scribe_Values.Look(ref isVectoredRcs, "engineRcs", false);
            string? loadedPropellant = propellantDefName;
            Scribe_Values.Look(ref loadedPropellant, "enginePropellant");
            if (loadedPropellant != null)
                propellantDefName = loadedPropellant;
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(exhaustVelocity);
            w.Write(massFlowRate);
            w.Write((byte)thrustAxis);
            w.Write(isVectoredRcs);
            w.Write(propellantDefName ?? "");
        }

        public override void Read(BinaryReader r)
        {
            exhaustVelocity = r.ReadSingle();
            massFlowRate = r.ReadSingle();
            thrustAxis = (ThrustAxis)r.ReadByte();
            isVectoredRcs = r.ReadBoolean();
            propellantDefName = r.ReadString() ?? "";
        }
    }
}
