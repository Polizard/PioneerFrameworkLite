using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **[A3] 进气道建筑（有 Map 侧）**：把大气里的空气自动灌进本船的 `PF_Propellant_IntakeAir` 储量。
    ///
    /// 与无 Map 侧（`Components.IntakeComponentData`）**同一条语义、同一份算式**：
    /// 速率 = `k·A·ρ(h)·|v_air|`（`AirBreathingCore.IntakeRateKgPerSec` 唯一），按**空气罐余量**截断
    /// （`FillStep`），充填相在物理步（并行只算、主线程入账），与燃烧扣料同相。
    /// **没有进气道 ⇒ 一点不充**（[补1]：不许拿"有空气罐"当隐式进气道）。
    /// </summary>
    public class CompProperties_IntakeAir : CompProperties
    {
        /// <summary>进气面积 A（m²，几何量，≥0）。</summary>
        public float intakeArea = 1f;

        /// <summary>进气系数 k（无量纲，≥0）：充填速率 = `k·A·ρ·|v_air|`。1 = 名义。</summary>
        public float coefficient = 1f;

        public CompProperties_IntakeAir()
        {
            compClass = typeof(CompIntakeAir);
        }
    }

    /// <summary>进气道 Comp：Spawn/DeSpawn 进出地图聚合（照 `CompThruster` 的形状）。</summary>
    public class CompIntakeAir : ThingComp
    {
        public CompProperties_IntakeAir Props => (CompProperties_IntakeAir)props;

        /// <summary>本进气道贡献的 `面积 × 系数`（m²·k）。</summary>
        public double IntakeArea
        {
            get
            {
                double k = Props.coefficient > 0f ? Props.coefficient : 0.0;
                return Props.intakeArea * k;
            }
        }

        // 钩子名照 `CompThruster`（本库既有写法）：`PostSpawnSetup` / `PostDeSpawn`
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            parent?.Map?.GetComponent<SpaceMapComp>()?.RegisterIntake(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode)
        {
            map?.GetComponent<SpaceMapComp>()?.UnregisterIntake(this);
            base.PostDeSpawn(map, mode);
        }
    }
}
