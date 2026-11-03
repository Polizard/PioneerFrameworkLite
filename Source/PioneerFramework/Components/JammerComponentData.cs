using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **干扰机部件组件**（[E3]/S4 浅层）：只做一件事——把自己的干扰效力累加进 `ShipStats.jamStrength`，
    /// 由群级 `JamStrength` 汇总、`SensorManager` 在扫描时按"干扰机→观测方"距离平方反比折算。
    ///
    /// 口径（设计稿 §11.3 压制式干扰）：
    /// · **压制 = 抬高观测方的阈值**（`A_obs` 的分母 `1 + J/(1+ecm)`）⇒ **烧穿是免费推论**：
    ///   目标一靠近，信号按平方涨而阈值不变 ⇒ 必然在某个距离上重新可见（不需要新机制）；
    /// · 干扰机自身**必须辐射**（W1）——它的开机开关是 `SpaceEntityGroup.JammingEnabled`，
    ///   将来与信号项（"开机即更响"）联动时由同一处读；
    /// · 本组件**不 Tick、不改 Group**（与传感器阵列同纪律：一切推动放物理步）。
    /// </summary>
    public class JammerComponentData : PartComponentData
    {
        /// <summary>干扰效力（无量纲，≥0）：1.0 = 在 `DetectionCore.JamRefMeters` 处把有效信号砍半。</summary>
        public float jamStrength = 1f;

        static JammerComponentData()
        {
            ComponentRegistry.Register<JammerComponentData>();   // 与 `SensorArrayComponentData` 同款（无反射注册）
        }

        public override PartRole Role => PartRole.Jammer;

        public override void AccumulateStats(ref ShipStats s, float efficiency)
        {
            s.jamStrength += jamStrength * efficiency;   // §11.4：干扰效力属"运转能力"，乘效率
        }

        public override bool ApplyPreset(string key, string value)
        {
            if (key != "jamStrength") return false;
            if (!TryParseFloat(value, out float v)) return false;
            jamStrength = v;
            return true;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref jamStrength, "jamStrength", 1f);
        }

        public override void Write(BinaryWriter w) => w.Write(jamStrength);
        public override void Read(BinaryReader r) => jamStrength = r.ReadSingle();
    }
}
