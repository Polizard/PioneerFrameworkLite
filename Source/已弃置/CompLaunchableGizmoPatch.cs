using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using PioneerFramework.Launch;

namespace PioneerFramework.GroundAssembly.Patches
{
    /// <summary>
    /// PFL 管理范围内的火箭不再显示原版 CompLaunchable 发射 Gizmo，
    /// 统一由“发射…”入口进入 RocketLaunchManager/Dialog_RocketLaunch。
    /// </summary>
    [HarmonyPatch(typeof(CompLaunchable), nameof(CompLaunchable.CompGetGizmosExtra))]
    public static class CompLaunchableGizmoPatch
    {
        private static bool Prefix(CompLaunchable __instance, ref IEnumerable<Gizmo> __result)
        {
            if (__instance == null || __instance.parent == null)
                return true;

            if (RocketLaunchManager.IsManagedRocket(__instance.parent))
            {
                __result = Enumerable.Empty<Gizmo>();
                return false;
            }

            return true;
        }
    }
}
