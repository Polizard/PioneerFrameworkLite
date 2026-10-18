using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **部件级传感器阵列**（[V7]，2026-09-24）—— 让"灵敏度 / 抗压制 / 抗欺骗"第一次有数据来源。
    ///
    /// ## 它在哪条链上
    /// ```
    /// SensorArrayComponentData（本文件）
    ///   → SpacePart.Stats（部件级求和：components[i].AccumulateStats）
    ///   → SpaceEntity.AggregatedStats（实体级求和：RecalculateShipStats）
    ///   → SpaceEntityGroup.DetectTech（编队级**派生量**：与 SignalStrength 同一趟重算）
    ///   → [V3] 认知层的判据输入（`EffectiveDetectRange` / 抗压制 / 抗欺骗）
    /// ```
    /// **本组件是这条链唯一的"给量纲"处**：[V3] 认知层若先落地会读到全 0（= 完全失明，
    /// 包括看不见自己发射的匿踪导弹）—— 这就是"给量纲的步骤必须先于读量纲的步骤"那条排期原则（§步 1–2 反思 C2）。
    ///
    /// ## 为什么每个部件只写"自己那一份"，不做整体覆盖
    /// `AccumulateStats` 的语义是**对 ref 累加**（与 `HullMassComponent` / `RoomComponentData` 同范式）⇒
    /// 一艘船装多台阵列就是灵敏度相加、抗性相加。**不要**在这里读别的部件或整船状态。
    ///
    /// ## 刻意**不**声明的东西（别当漏了）
    /// 主动增益倍率 / 主动辐射增量（计划稿 §[V7] 示例里的 `activeRangeFactor` / `activeRadiation`）**暂不落地**：
    /// `ShipStats` 没有对应槽位，现在声明就是"声明了没人读"的空字段（本库审计点过名的那种）。
    /// 主动开机的增益属于 [E2]（"看得远的代价"）那一步 ⇒ 到那时再连槽位一起加。
    ///
    /// ## 装在哪（2026-09-24 的安装点）
    /// 空间站 / 交会验收船 / 发射火箭 / 跳帮穿梭机（四处 `part.AddComponent(new SensorArrayComponentData{...})`）。
    /// **导弹暂时不装**（它的导引头灵敏度是 [V3]/[E6] 的事，且弹体默认匿踪、当前不需要自己"看"）——
    /// 这是显式决定，不是漏掉。组件不是 XML 挂载的：`1.6/Defs/ShipParts/` 是空目录、全库无 `ShipPartDef`。
    /// </summary>
    public class SensorArrayComponentData : PartComponentData
    {
        /// <summary>被动灵敏度（m）：有效探测距离的基数（静默基准目标恰好在灵敏度处被发现）。</summary>
        public float passiveRangeMeters = 500000f;

        /// <summary>抗压制（无量纲，≥0）：压制效力除以 `1 + ecmResist`。</summary>
        public float ecmResist ;

        /// <summary>抗欺骗（无量纲，≥0）：大于欺骗强度时**该观测者**识破假接触。</summary>
        public float eccmResist ;

        /// <summary>记忆时长（s，物理秒轴）：观测者的接触在"最后一次刷新"之后还能活多久（[V3] 的 T）。
        /// 0 = 未设 ⇒ 用 `DetectionCore.DefaultMemorySeconds`（待定标）。与灵敏度同属**观测者**属性。</summary>
        public float memorySeconds ;

        /// <summary>**主动增益加成**（≥0，2026-10-01 随主动雷达槽位一起加）：有效探测距离倍率 = `1 + Σ`。
        /// 部件级默认 0 = 无增益（"装了也不主动"），要主动得显式给值（与建筑级同语义）。</summary>
        public float activeGainBonus ;

        /// <summary>**主动开机时的信号增量**（S_REF 倍数，≥0）：加在 `SignalModel.ActivePingMultiplier` 基准之上。
        /// 部件级默认 0（= 开机只付基准代价），装上雷达阵列才付设备那一份。</summary>
        public float activeRadiation ;

        static SensorArrayComponentData()
        {
            ComponentRegistry.Register<SensorArrayComponentData>();
        }

        public override void AccumulateStats(ref ShipStats s)
        {
            s.sensorRange += passiveRangeMeters;
            s.memorySeconds += memorySeconds;
            s.ecmResist += ecmResist;
            s.eccmResist += eccmResist;
            s.activeGainBonus += activeGainBonus;   // [V7]/[E2]：加法语义（加成/增量），与实体聚合一致
            s.activeRadiation += activeRadiation;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref passiveRangeMeters, "passiveRangeMeters", 500000f);
            Scribe_Values.Look(ref ecmResist, "ecmResist", 0f);
            Scribe_Values.Look(ref eccmResist, "eccmResist", 0f);
            // ★ 修复（2026-10-17 批 -1）：其余三场曾漏盘（与 `AccumulateStats` 的贡献面对不上）
            Scribe_Values.Look(ref memorySeconds, "memorySeconds", 0f);
            Scribe_Values.Look(ref activeGainBonus, "activeGainBonus", 0f);
            Scribe_Values.Look(ref activeRadiation, "activeRadiation", 0f);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(passiveRangeMeters);
            w.Write(ecmResist);
            w.Write(eccmResist);
            w.Write(memorySeconds);      // ★ 修复（2026-10-17 批 -1）：与 ExposeData 三场同补（二进制通道对称）
            w.Write(activeGainBonus);
            w.Write(activeRadiation);
        }

        public override void Read(BinaryReader r)
        {
            passiveRangeMeters = r.ReadSingle();
            ecmResist = r.ReadSingle();
            eccmResist = r.ReadSingle();
            memorySeconds = r.ReadSingle();
            activeGainBonus = r.ReadSingle();
            activeRadiation = r.ReadSingle();
        }
    }
}
