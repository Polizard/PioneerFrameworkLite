using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PioneerFramework.GroundAssembly.Patches
{
    /// <summary>
    /// 原版 Frame 完成施工时会销毁 Frame 并生成目标建筑。
    /// 本补丁在完成后通知所属总装发射台：已完工火箭已就位，状态切到 Completed。
    /// </summary>
    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    public static class FrameCompleteConstructionPatch
    {
        private class State
        {
            public RocketDesignSpec Spec;
            public IntVec3 Pos;
            public Map Map;
        }

        private static void Prefix(Frame __instance, out State __state)
        {
            __state = new State
            {
                Spec = __instance?.TryGetComp<CompUnfinishedRocket>()?.Spec,
                Pos = __instance?.Position ?? IntVec3.Invalid,
                Map = __instance?.Map
            };
        }

        private static void Postfix(Frame __instance, State __state)
        {
            if (__state?.Spec == null || __state.Map == null || !__state.Pos.IsValid)
                return;

            ThingDef rocketDef = DefDatabase<ThingDef>.GetNamedSilentFail(__state.Spec.rocketDefName);
            if (rocketDef == null)
                return;

            Thing built = __state.Map.thingGrid.ThingsListAt(__state.Pos)
                .FirstOrDefault(t => !t.Destroyed && t.def == rocketDef);
            if (built == null)
                return;

            Building pad = __state.Map.thingGrid.ThingsListAt(__state.Pos)
                .OfType<Building>()
                .FirstOrDefault(b => LaunchPadUtility.IsAssemblyPad(b.def));
            pad?.TryGetComp<CompAssemblyPad>()?.NotifyRocketCompleted(built);
        }
    }
}
