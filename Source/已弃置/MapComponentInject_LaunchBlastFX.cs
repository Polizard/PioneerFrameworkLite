using System.Linq;
using HarmonyLib;
using Verse;
using PioneerFramework.Launch;

namespace PioneerFramework.GroundAssembly.Patches
{
    /// <summary>为每张地图注入 <see cref="MapComponent_LaunchBlastFX"/>；不依赖存档 Def，存档中途加载亦会补齐组件。</summary>
    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    internal static class MapInject_LaunchBlastFXPatch
    {
        private static void Postfix(Map __instance)
        {
            if (__instance == null)
            {
                return;
            }

            if (!__instance.components.Any(c => c is MapComponent_LaunchBlastFX))
            {
                __instance.components.Add(new MapComponent_LaunchBlastFX(__instance));
            }

            if (!__instance.components.Any(c => c is MapComponent_RocketBoosterReturnScheduler))
            {
                __instance.components.Add(new MapComponent_RocketBoosterReturnScheduler(__instance));
            }
        }
    }
}
