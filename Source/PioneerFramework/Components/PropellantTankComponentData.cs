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

        /// <summary>**RCS 工质罐标签**（2026-10-26 用户裁定）：true ⇒ 本罐给**整条船**的 RCS 供工质。
        /// 自动假设舰上配有 RCS（不需要 RCS 件）；效率写在 `rcsEfficiency`。</summary>
        public bool rcsSupply;

        /// <summary>RCS 效率（0–1；把 `ve·臂·η` 作为每 kg 工质能卸载的角动量）。默认 0.8。</summary>
        public float rcsEfficiency = 0.8f;

        /// <summary>**供油阀门**（2026-10-27 用户裁定）：true ⇒ 本罐参与供油计划/分配；
        /// 按 `stageId` **自动开关**（活动级 = 当前最大 stageId）——抛级后阀门自动切到下一级，
        /// 上面级的油不会被下级引擎烧掉。</summary>
        public bool feedEnabled = true;

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
            dst.rcsSupply = rcsSupply;
            dst.rcsEfficiency = rcsEfficiency;
            dst.feedEnabled = feedEnabled;
            return dst;
        }

        public override PartRole Role => PartRole.Tank;

        public override void AccumulateStats(ref ShipStats s, float efficiency) { }

        public override bool ApplyPreset(string key, string value)
        {
            switch (key)
            {
                case "capacityKg":
                    if (!TryParseFloat(value, out float cap)) return false;
                    capacityKg = cap;
                    return true;
                case "propellant":
                    propellantDefName = value;
                    return true;
                case "rcsSupply":
                    if (!bool.TryParse(value, out bool supply)) return false;
                    rcsSupply = supply;
                    return true;
                case "rcsEfficiency":
                    if (!TryParseFloat(value, out float eff)) return false;
                    rcsEfficiency = eff < 0f ? 0f : (eff > 1f ? 1f : eff);
                    return true;
                case "feedEnabled":
                    if (!bool.TryParse(value, out bool feed)) return false;
                    feedEnabled = feed;
                    return true;
                default:
                    return false;
            }
        }

        public override void ExposeData()
        {
            string? loadedPropellant = propellantDefName;
            Scribe_Values.Look(ref loadedPropellant, "tankPropellant");
            if (loadedPropellant != null)
                propellantDefName = loadedPropellant;
            Scribe_Values.Look(ref capacityKg, "tankCapacityKg", 0.0);
            Scribe_Values.Look(ref massKg, "tankMassKg", 0.0);
            Scribe_Values.Look(ref leaked, "tankLeaked", false);
            Scribe_Values.Look(ref rcsSupply, "tankRcsSupply", false);
            Scribe_Values.Look(ref rcsEfficiency, "tankRcsEfficiency", 0.8f);
            Scribe_Values.Look(ref feedEnabled, "tankFeedEnabled", true);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(propellantDefName ?? "");
            w.Write(capacityKg);
            w.Write(massKg);
            w.Write(leaked);
            w.Write(rcsSupply);
            w.Write(rcsEfficiency);
            w.Write(feedEnabled);
        }

        public override void Read(BinaryReader r)
        {
            propellantDefName = r.ReadString() ?? "";
            capacityKg = r.ReadDouble();
            massKg = r.ReadDouble();
            leaked = r.ReadBoolean();
            rcsSupply = r.ReadBoolean();
            rcsEfficiency = r.ReadSingle();
            feedEnabled = r.ReadBoolean();
        }
    }
}
