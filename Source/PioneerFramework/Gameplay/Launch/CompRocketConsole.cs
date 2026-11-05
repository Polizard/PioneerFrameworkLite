using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 火箭控制台 Comp 配置。DMS:NF 正式控制台建筑可通过挂载本 Comp
    /// 接入 PFL 的发射条件校验与后续舱内点火 Gizmo。
    /// </summary>
    public class CompProperties_RocketConsole : CompProperties
    {
        public CompProperties_RocketConsole()
        {
            compClass = typeof(CompRocketConsole);
        }
    }

    /// <summary>
    /// 火箭控制台 Comp：作为「有人控制的控制台」标记。
    /// 首发只承担发射条件校验标记；后续可扩展为舱内点火入口。
    /// </summary>
    public class CompRocketConsole : ThingComp
    {
        /// <summary>当前是否可用。默认未接电网也视为可用，正式版可覆盖为需要供电。</summary>
        public bool IsOperational => parent != null && !parent.Destroyed;

        public override string CompInspectStringExtra()
        {
            return IsOperational ? "火箭控制台：可用" : "火箭控制台：不可用";
        }
    }
}
