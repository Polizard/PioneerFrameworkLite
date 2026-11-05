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

        /// <summary>**满推力时的产热**（W，§11.5 热闭环的"成对补入"引擎那一半）：0 = 不产热（默认，零行为变化）。
        /// 只进 `ShipStats.heatProduction`；不参与推力/耗料。</summary>
        public float heatProductionWatts;

        /// <summary>**喷口被动散热**（W，2026-10-27 用户口径：引擎的喷口自己能散热）。
        /// 与产热成对，只进 `ShipStats.heatDissipation`；和散热器**同一统计口径**，但它是引擎自己的属性，
        /// 不额外挂散热器组件。损毁件由 `RecalculateShipStats` 跳过（随件出局）。</summary>
        public float heatDissipationWatts;

        /// <summary>**点火时的标称功耗**（W，§5 电闭环的"推进"通道）：0 = 不用电（默认 ⇒ 老船零行为变化）。
        /// 只进 `ShipStats.powerDrawPropulsion`；**活动折算**由群级规则按 `HasActiveBurn` 做，不在这件里做。</summary>
        public float powerDrawWatts;

        static EngineComponentData()
        {
            ComponentRegistry.Register<EngineComponentData>();
        }

        public override PartRole Role => PartRole.Thruster;

        public override PartComponentData? Clone()
        {
            var dst = ComponentRegistry.Create(TypeId) as EngineComponentData;
            if (dst == null) return null;
            dst.exhaustVelocity = exhaustVelocity;
            dst.massFlowRate = massFlowRate;
            dst.thrustAxis = thrustAxis;
            dst.isVectoredRcs = isVectoredRcs;
            dst.propellantDefName = propellantDefName;
            dst.heatProductionWatts = heatProductionWatts;
            dst.heatDissipationWatts = heatDissipationWatts;
            dst.powerDrawWatts = powerDrawWatts;
            return dst;
        }

        public override void AccumulateStats(ref ShipStats s, float efficiency)
        {
            // 产热/散热/功耗都乘效率（§11.4：失修的引擎烧不满 ⇒ 也产不满热、散不满热、用不满电）；
            // 推力不在这里聚合（M2 甲，实体/地图级）。
            s.heatProduction += heatProductionWatts * efficiency;
            s.heatDissipation += heatDissipationWatts * efficiency;
            s.powerDrawPropulsion += powerDrawWatts * efficiency;
        }

        public override bool ApplyPreset(string key, string value)
        {
            switch (key)
            {
                case "exhaustVelocity":
                    if (!TryParseFloat(value, out float ve)) return false;
                    exhaustVelocity = ve;
                    return true;
                case "massFlowRate":
                    if (!TryParseFloat(value, out float flow)) return false;
                    massFlowRate = flow;
                    return true;
                case "heatProduction":
                    if (!TryParseFloat(value, out float heat)) return false;
                    heatProductionWatts = heat;
                    return true;
                case "heatDissipationWatts":
                    if (!TryParseFloat(value, out float diss)) return false;
                    heatDissipationWatts = diss;
                    return true;
                case "powerDrawWatts":
                    if (!TryParseFloat(value, out float draw)) return false;
                    powerDrawWatts = draw;
                    return true;
                case "propellant":
                    propellantDefName = value;
                    return true;
                case "isVectoredRcs":
                    isVectoredRcs = value == "1" || string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);
                    return true;
                case "thrustAxis":
                    if (!System.Enum.TryParse(value, true, out ThrustAxis axis)) return false;
                    thrustAxis = axis;
                    return true;
                default:
                    return false;
            }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref exhaustVelocity, "engineVe", 0f);
            Scribe_Values.Look(ref massFlowRate, "engineMassFlow", 0f);
            Scribe_Values.Look(ref thrustAxis, "engineAxis", ThrustAxis.Back);
            Scribe_Values.Look(ref isVectoredRcs, "engineRcs", false);
            Scribe_Values.Look(ref heatProductionWatts, "engineHeat", 0f);
            Scribe_Values.Look(ref heatDissipationWatts, "engineHeatDissipation", 0f);
            Scribe_Values.Look(ref powerDrawWatts, "enginePowerDraw", 0f);
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
            w.Write(heatProductionWatts);
            w.Write(heatDissipationWatts);
            w.Write(powerDrawWatts);
        }

        public override void Read(BinaryReader r)
        {
            exhaustVelocity = r.ReadSingle();
            massFlowRate = r.ReadSingle();
            thrustAxis = (ThrustAxis)r.ReadByte();
            isVectoredRcs = r.ReadBoolean();
            propellantDefName = r.ReadString() ?? "";
            heatProductionWatts = r.ReadSingle();
            heatDissipationWatts = r.ReadSingle();
            powerDrawWatts = r.ReadSingle();
        }
    }
}
