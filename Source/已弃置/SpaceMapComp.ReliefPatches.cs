using HarmonyLib;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 地图浮雕分类的**置脏挂钩**（2026-10-02）。
    ///
    /// "图上盖/拆东西 ⇒ PFView 里的浮雕跟着变"靠这四处事件：地板、屋顶、建筑出现、建筑消失。
    /// 事件里**只置一个脏标记**（`MarkReliefDirty`）——重建推迟到 PFView 取用时做，因为
    /// 这些事件全都发生在物理步/建造流程的上下文中，绝不能在事件里扫整张图。
    ///
    /// 登记方式：本文件只写 `[HarmonyPatch]` 类 —— `GroundAssemblyHarmony`
    /// （`[StaticConstructorOnStartup]`）用 `PatchAll(Assembly.GetExecutingAssembly())` 统一登记全程序集的补丁类。
    /// </summary>
    internal static class SpaceMapCompReliefPatches
    {
        // 地形是"装甲/结构梁"分类的一个输入（有没有实心地板）——铺地板/拆地板都必须置脏。
        [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
        private static class TerrainGrid_SetTerrain
        {
            private static void Postfix(Map ___map) => SpaceMapComp.NotifyReliefDirty(___map);
        }

        [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveTopLayer))]
        private static class TerrainGrid_RemoveTopLayer
        {
            private static void Postfix(Map ___map) => SpaceMapComp.NotifyReliefDirty(___map);
        }

        // 屋顶 = "装甲"那一档的判据（房间全屋顶）。
        [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
        private static class RoofGrid_SetRoof
        {
            private static void Postfix(Map ___map) => SpaceMapComp.NotifyReliefDirty(___map);
        }

        // 建筑出现/消失 = 房间边界与"结构梁"格的变化源。
        // 挂在 Thing 基类（Building 的各子类大量 override DeSpawn/SpawnSetup，挂 Building 会漏掉不调 base 的实现；
        // 挂在 Thing 上再按 `is Building` 过滤，覆盖面最稳）。非建筑只多一次类型判断，代价可忽略。
        [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
        private static class Thing_SpawnSetup
        {
            private static void Postfix(Thing __instance)
            {
                if (__instance is Building)
                    SpaceMapComp.NotifyReliefDirty(__instance.Map);
            }
        }

        [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
        private static class Thing_DeSpawn
        {
            private static void Prefix(Thing __instance)
            {
                if (__instance is Building)
                    SpaceMapComp.NotifyReliefDirty(__instance.Map);
            }
        }
    }
}
