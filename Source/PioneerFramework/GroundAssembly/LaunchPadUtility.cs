using System.Linq;
using RimWorld;
using Verse;
using PioneerFramework.Launch;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// PFL 通用发射台/火箭/返回舱判定。
    /// 取代 DMS:NF 中硬编码 defName 的 CenteredFuelingPortUtil。
    /// </summary>
    public static class LaunchPadUtility
    {
        /// <summary>是否为 PFL 总装发射台（带 CompAssemblyPad）。</summary>
        public static bool IsAssemblyPad(ThingDef def)
        {
            return def?.comps != null && def.comps.Any(c => c is CompProperties_AssemblyPad);
        }

        /// <summary>是否为 PFL 火箭（带未完工框架、可回收配置或原版 Launchable）。</summary>
        public static bool IsRocket(ThingDef def)
        {
            if (def == null)
                return false;

            if (def.comps != null && def.comps.Any(c => c is CompProperties_UnfinishedRocket))
                return true;

            if (def.comps != null && def.comps.Any(c => c is CompProperties_RocketBlueprint))
                return true;

            if (RocketRecoveryDefFor(def) != null)
                return true;

            return def.comps != null && def.comps.Any(c => c is CompProperties_Launchable_TransportPod);
        }

        /// <summary>是否为回收下来的第一级助推器。</summary>
        public static bool IsReturnedBooster(ThingDef def)
        {
            if (def == null)
                return false;

            foreach (RocketRecoveryDef recovery in DefDatabase<RocketRecoveryDef>.AllDefsListForReading)
            {
                if (recovery.returnedThingDef == def)
                    return true;
            }

            return false;
        }

        /// <summary>获取指定火箭 Def 对应的回收配置；没有则返回 null。</summary>
        public static RocketRecoveryDef RocketRecoveryDefFor(ThingDef rocketDef)
        {
            if (rocketDef == null)
                return null;

            foreach (RocketRecoveryDef recovery in DefDatabase<RocketRecoveryDef>.AllDefsListForReading)
            {
                if (recovery.defName == rocketDef.defName || recovery.launchedRocketDef == rocketDef)
                    return recovery;
            }

            return null;
        }

        /// <summary>
        /// 发射台可建造区域。
        /// 当前固定为发射台中心的 3×3 区域；后续可扩展为发射台等级/大小配置。
        /// </summary>
        public static CellRect GetBuildableArea(Building pad)
        {
            if (pad == null || !pad.Spawned)
                return CellRect.Empty;

            CellRect occupied = pad.OccupiedRect();
            return CellRect.CenteredOn(occupied.CenterCell, 3, 3);
        }

        /// <summary>
        /// 判断一个待放置物（含多格建筑）是否完全落在发射台可建造区域内。
        /// </summary>
        public static bool IsWithinBuildableArea(Building pad, IntVec3 center, ThingDef def, Rot4 rot)
        {
            if (pad == null || def == null)
                return false;

            CellRect area = GetBuildableArea(pad);
            CellRect rect = GenAdj.OccupiedRect(center, rot, def.Size);
            foreach (IntVec3 cell in rect.Cells)
            {
                if (!area.Contains(cell))
                    return false;
            }

            return true;
        }

        /// <summary>返回舱是否可在该发射台上重建。当前统一允许所有 PFL 总装台，后续可加尺寸限制。</summary>
        public static bool CanRebuildOnPad(ThingDef returnedDef, ThingDef padDef)
        {
            return returnedDef != null && IsAssemblyPad(padDef);
        }

        /// <summary>火箭是否可在该发射台上发射。当前统一允许所有 PFL 总装台，后续可加尺寸限制。</summary>
        public static bool CanLaunchFromPad(ThingDef rocketDef, ThingDef padDef)
        {
            return rocketDef != null && IsAssemblyPad(padDef);
        }

        /// <summary>是否为原版运输舱发射器（有燃料口但不是 PFL 总装台）。</summary>
        public static bool IsVanillaPodLauncher(ThingDef def)
        {
            return def?.building?.hasFuelingPort == true && !IsAssemblyPad(def);
        }
    }
}
