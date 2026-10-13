using System.IO;
using Verse;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **进气道部件（[A3]/D4）**：把大气里的空气自动灌进本船的 `PF_Propellant_IntakeAir` 工质条目。
    ///
    /// 为什么要有这么一个"显式开关"：充填的物理量是 `k·A_进气·ρ(h)·|v_air|`，其中只有 ρ 与空速是环境量，
    /// **A_进气是船自己的属性**。若拿"船上有空气罐"当条件，等于说**任何带空气罐的船都有进气道** ——
    /// 那是把数据事故当默认（[补1] 的同一条纪律：不许静默给生产者兜底）。故：**没有这个部件 ⇒ 一点不充**。
    ///
    /// 数据形状：与 `HullMassComponent`/`SensorArrayComponentData` 同（纯数据 + Write/Read 深拷贝通道）。
    /// **不进 `AccumulateStats`**：它不贡献质量/产出（部件自重由结构件/`HullMassComponent` 承担），
    /// 只被物理步的充填相按"面积×系数"求和（唯一消费者 = `SpaceGameComp.GetGroupIntakeArea`）。
    /// </summary>
    public class IntakeComponentData : PartComponentData
    {
        /// <summary>进气面积 A（m²，几何量，≥0）。</summary>
        public float intakeArea = 1f;

        /// <summary>进气系数 k（无量纲，≥0）：充填速率 = `k·A·ρ·|v_air|`（`AirBreathingCore.IntakeRateKgPerSec`）。1 = 名义。</summary>
        public float coefficient = 1f;

        static IntakeComponentData()
        {
            ComponentRegistry.Register<IntakeComponentData>();
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref intakeArea, "intakeArea", 1f);
            Scribe_Values.Look(ref coefficient, "coefficient", 1f);
        }

        public override void Write(BinaryWriter w)
        {
            w.Write(intakeArea);
            w.Write(coefficient);
        }

        public override void Read(BinaryReader r)
        {
            intakeArea = r.ReadSingle();
            coefficient = r.ReadSingle();
        }
    }
}
