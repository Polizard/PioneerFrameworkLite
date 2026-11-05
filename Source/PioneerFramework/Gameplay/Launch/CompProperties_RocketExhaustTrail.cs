using RimWorld;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>DMS 包同级冷发射尾焰：每 tick 火焰 + 烟 Fleck。</summary>
    public class CompProperties_RocketExhaustTrail : CompProperties
    {
        /// <summary>此 tick 数之后才开始抛 Fleck（并参与 evaluate 分母）。</summary>
        public int delayTicks = 40;

        /// <summary>短尾迹：喷口沿 (sin,cos)*offset 的米数（上一版基准）。</summary>
        public float offsetY = -2.2f;

        /// <summary>延长尾迹：更大 |offsetY|，在 <see cref="trailExtendDelayTicks"/> 之后启用。</summary>
        public float extendedOffsetY = -3.4f;

        /// <summary>短尾迹：锚点回退米数。</summary>
        public float centerToEngineBackset = 2.4f;

        /// <summary>延长尾迹：锚点回退米数。</summary>
        public float extendedCenterToEngineBackset = 2.9f;

        /// <summary>自升空（生成）起，超过此 tick 数后启用延长拖尾（默认 0.5s≈30tick，且须已过 <see cref="delayTicks"/> 才开始计喷焰）。</summary>
        public int trailExtendDelayTicks = 30;

        /// <summary>短尾迹沿喷管叠火段数（上一版为 5）。</summary>
        public int plumeExtraSegments = 5;

        /// <summary>延长尾迹叠火段数。</summary>
        public int extendedPlumeExtraSegments = 8;

        /// <summary>每段沿 vector 的步长系数。</summary>
        public float plumeSegmentScale = 0.5f;

        /// <summary>evaluate→1 时密度下限，末段不熄光。</summary>
        public float minDensity = 0.35f;

        /// <summary>相对上一版火焰的宽度（scale/targetSize）倍率，默认 1.2 = +20%。</summary>
        public float flameWidthMultiplier = 1.2f;

        /// <summary>火焰 Fleck（DMS 核心包 <c>DMS_Fleck_RocketFlame</c>）。</summary>
        public FleckDef exhaustFleck = null!;

        public SimpleCurve exhaustCurve = null!;

        public FleckDef smokeFleck = null!;

        public SimpleCurve smokeCurve = null!;

        /// <summary>外层批次数。</summary>
        public int exhaustBatches = 3;

        /// <summary>仅当前查看地图与 parent 相同时播放。</summary>
        public bool onlyOnCurrentMap = true;

        /// <summary>第一阶段（点火预喷）占比：0~1 的 evaluate 阈值。</summary>
        public float initialLateralPhasePct = 0.15f;

        /// <summary>第二阶段火焰从小到大爬升区间（evaluate 百分比）。2 秒约等于 0.1875（640 tick 总时长）。</summary>
        public float secondStageFlameRampPct = 0.1875f;

        /// <summary>第二阶段火焰起始尺寸倍率（宽度相关）。</summary>
        public float secondStageFlameMinScaleMultiplier = 0.2f;

        /// <summary>第二阶段火焰起始长度倍率（拖尾相关）。</summary>
        public float secondStageFlameMinLengthMultiplier = 0.25f;

        /// <summary>高速段收敛起点（evaluate）。超过后逐步缩短寿命与拖尾。</summary>
        public float highSpeedTaperStartPct = 0.7f;

        /// <summary>高速段收敛终点（evaluate）。</summary>
        public float highSpeedTaperEndPct = 1f;

        /// <summary>高速段火焰寿命倍率下限。</summary>
        public float highSpeedFlameLifetimeMultiplier = 0.5f;

        /// <summary>高速段烟雾寿命倍率下限。</summary>
        public float highSpeedSmokeLifetimeMultiplier = 0.6f;

        /// <summary>高速段拖尾长度倍率下限（同时影响分段数与步长）。</summary>
        public float highSpeedLengthMultiplier = 0.7f;

        /// <summary>速度补偿起点（CurrentSkyfallerSpeed，世界单位/ tick）。</summary>
        public float speedCompStart = 0.18f;

        /// <summary>速度补偿终点（CurrentSkyfallerSpeed）。</summary>
        public float speedCompEnd = 0.30f;

        /// <summary>高速下火焰生成概率下限（减少残留孤立火焰）。</summary>
        public float highSpeedFlameSpawnChance = 0.55f;

        /// <summary>高速下烟雾生成概率下限。</summary>
        public float highSpeedSmokeSpawnChance = 0.65f;

        /// <summary>高速下火焰尺寸倍率下限。</summary>
        public float highSpeedFlameSizeMultiplier = 0.7f;

        /// <summary>
        /// 第二阶段是否启用「连续主焰」增强：强制每 tick 生成一批尾向单束 Fleck，并抬高主束 scale 下限；
        /// <see cref="continuousFlameLength"/> 等仍用于 <c>PostDraw</c> 锥形。设为 false 则仅走原有多批 Fleck 逻辑。
        /// </summary>
        public bool enableContinuousFlame = true;

        /// <summary><c>PostDraw</c> 锥形叠层：与 <c>lastComp01</c> 混合的起算阈值。</summary>
        public float continuousFlameStartComp01 = 0.5f;

        /// <summary><c>PostDraw</c> 锥形长度参考（世界单位，米量级）。</summary>
        public float continuousFlameLength = 2.8f;

        /// <summary><c>PostDraw</c> 锥形宽度参考。</summary>
        public float continuousFlameWidth = 0.75f;

        /// <summary>仅 <c>PostDraw</c> 纯色锥形叠层：最小 alpha。</summary>
        public float continuousFlameMinAlpha = 0.36f;

        /// <summary>仅 <c>PostDraw</c> 纯色锥形叠层：最大 alpha。</summary>
        public float continuousFlameMaxAlpha = 0.62f;

        public CompProperties_RocketExhaustTrail()
        {
            compClass = typeof(CompRocketExhaustTrail);
        }
    }
}
