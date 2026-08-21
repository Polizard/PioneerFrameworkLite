using HarmonyLib;
using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly.Patches
{
    /// <summary>
    /// 原版 Blueprint_Build 转 Frame 时，把蓝图上的 RocketDesignSpec 转交给新 Frame 的 CompUnfinishedRocket。
    /// </summary>
    [HarmonyPatch(typeof(Blueprint_Build), "MakeSolidThing")]
    public static class BlueprintMakeSolidThingPatch
    {
        private static void Postfix(Blueprint_Build __instance, Thing __result)
        {
            if (!(__result is Frame frame))
                return;

            var blueprintComp = __instance?.TryGetComp<CompRocketBlueprint>();
            var frameComp = frame.TryGetComp<CompUnfinishedRocket>();
            if (blueprintComp != null && frameComp != null)
                frameComp.AssignSpec(blueprintComp.Spec);
        }
    }
}
