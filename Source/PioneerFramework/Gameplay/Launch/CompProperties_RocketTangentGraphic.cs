using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 离港火箭贴图朝向改为沿轨迹切线（由上一 tick 与当前最终 drawLoc 差分近似）。
    /// </summary>
    public class CompProperties_RocketTangentGraphic : CompProperties
    {
        public CompProperties_RocketTangentGraphic()
        {
            compClass = typeof(CompRocketTangentGraphic);
        }
    }
}
