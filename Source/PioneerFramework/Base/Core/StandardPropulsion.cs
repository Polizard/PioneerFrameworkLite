using System.Collections.Generic;
using PioneerFramework.Components;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **无 Map 标准推进数据（[A4]；计划稿 §3.3 的"单一出处"）**。
    ///
    /// 无 Map 侧的 `PropulsionData` 是**手写**的（没有建筑 Defs 可读）⇒ 散在各场景里必然与有 Map 侧漂移。
    /// 本类给**新内容**一份标准档；**既有场景里为各自判据调过的数字保持原样**（场景一经裁决即冻结，
    /// 只许加覆盖）。
    ///
    /// **与有 Map 侧的对照**（`CompProperties_Thruster` 的默认值，见 `Base/Buildings/CompThruster.cs`）：
    /// `exhaustVelocity = 9000`、`massFlowRate = 5` ⇒ 无 Map 标准档取**同一组**。两边要一起改时，
    /// 至少这里有一条交叉引用（"别让两套数字漂移"的落点）。
    /// </summary>
    public static class StandardPropulsion
    {
        /// <summary>标准 ve（m/s）——与有 Map 侧 `CompProperties_Thruster.exhaustVelocity` 默认值同值。</summary>
        public const float StandardVe = 9000f;

        /// <summary>标准 ṁ（kg/s）——与有 Map 侧 `CompProperties_Thruster.massFlowRate` 默认值同值。</summary>
        public const float StandardMassFlow = 5f;

        /// <summary>吸气式 ve（m/s）：D4 裁定 ≈30,000（与离子档同量级）⇒ 同油耗下推力是化学标准档的 3.33×。</summary>
        public const float AirBreathingVe = 30000f;

        /// <summary>吸气式密度指数 a = 1（推力正比 ρ；D5 常规取法）。</summary>
        public const float AirBreathingDensityExponent = 1f;

        /// <summary>吸气式混合比（kg 空气 / kg 主工质）：15:1，KSP `IntakeAir` 形状。</summary>
        public const float AirBreathingAirPerFuel = 15f;

        /// <summary>进气工质 Def 名（`1.6/Defs/Propellants/PropellantDefs.xml`）。</summary>
        public const string IntakeAirDefName = "PF_Propellant_IntakeAir";

        /// <summary>进气工质 Def（**主线程**调用；缺席 ⇒ null ⇒ 吸气式整条路径缺席即零）。</summary>
        public static PropellantDef IntakeAirDef => DefDatabase<PropellantDef>.GetNamedSilentFail(IntakeAirDefName);

        /// <summary>化学档主网：满罐（`mass == capacity`）的 `PropulsionData`。</summary>
        public static PropulsionData Chemical(PropellantDef propellant, double massKg,
            float ve = StandardVe, float massFlow = StandardMassFlow)
            => new() {
                propellant = propellant,
                ve = ve,
                massFlow = massFlow,
                mass = massKg,
                capacity = massKg,
            };

        /// <summary>
        /// **吸气式主网**：`a = 1`（推力 ∝ ρ）+ 混合比 15:1 抽空气。**不含空气罐**（见 <see cref="AirTank"/>）。
        /// `airDef` 缺席 ⇒ 退化成普通化学网（不抽气、包线照算）——那是数据事故，调用方应先查 <see cref="IntakeAirDef"/>。
        /// </summary>
        public static PropulsionData AirBreathingEngine(PropellantDef fuel, double fuelKg, PropellantDef airDef,
            float ve = AirBreathingVe, float massFlow = StandardMassFlow)
        {
            var d = new PropulsionData
            {
                propellant = fuel,
                ve = ve,
                massFlow = massFlow,
                mass = fuelKg,
                capacity = fuelKg,
                densityExponent = AirBreathingDensityExponent,
            };
            if (airDef != null)
            {
                d.gasIntakes = new List<GasIntake>
                {
                    new() { propellant = airDef, perUnitRatio = AirBreathingAirPerFuel },
                };
            }
            return d;
        }

        /// <summary>
        /// **空气罐**：`massFlow = 0` ⇒ **不成网络**（建网条件是 `massFlow &gt; 0`）——它只是一只容器，
        /// 由进气道（<see cref="Intake"/>）充填、被吸气式引擎按配比抽干。
        /// </summary>
        public static PropulsionData AirTank(PropellantDef airDef, double capacityKg, double fillKg = 0.0)
            => new() {
                propellant = airDef,
                ve = 0f,
                massFlow = 0f,
                mass = fillKg,
                capacity = capacityKg,
            };

        /// <summary>进气道部件（面积 m² × 系数 k；缺省 k = 1）。</summary>
        public static IntakeComponentData Intake(float areaM2, float coefficient = 1f)
            => new() { intakeArea = areaM2, coefficient = coefficient };
    }
}
