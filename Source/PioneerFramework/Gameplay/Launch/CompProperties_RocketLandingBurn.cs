using RimWorld;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 入轨末段（默认最后 0.5s）着陆缓冲：在箭体下方喷烟与火焰 Fleck。
    /// </summary>
    public class CompProperties_RocketLandingBurn : CompProperties
    {
        /// <summary>距撞击前多长（秒）内开启特效，默认 0.5。</summary>
        public float burnDurationSeconds = 0.5f;

        /// <summary>沿航向反方向从 DrawPos 回退，靠近发动机（米）。</summary>
        public float engineBackset = 2.6f;

        /// <summary>喷口锚点在高度轴上的额外偏移（米），默认 0。</summary>
        public float engineAnchorAltitudeOffset ;

        public FleckDef flameFleck = null!;

        /// <summary>每 tick 创建的烟雾 Fleck 数量（约）。</summary>
        public int smokePuffsPerTick = 5;

        /// <summary>每 tick 创建的火焰类 Fleck 批次数。</summary>
        public int flameBurstsPerTick = 2;

        public float smokeScale = 2.2f;

        public float flameScale = 1.35f;

        public float spawnJitter = 0.35f;

        /// <summary>是否生成厚尘团（与烟雾分离，便于仅关尘）。</summary>
        public bool spawnDustPuffs = true;

        /// <summary>仅当前查看地图播放。</summary>
        public bool onlyOnCurrentMap = true;

        public CompProperties_RocketLandingBurn()
        {
            compClass = typeof(CompRocketLandingBurn);
        }
    }
}
