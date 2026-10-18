using HarmonyLib;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 打开时**屏蔽宿主地图 UI**（2026-10-18 视图系统 v2 · U10）。
    ///
    /// 为什么不是"设 absorbInputAroundWindow"：宿主 `GenUI.AbsorbClicksInRect` 的守卫是
    /// `WindowStack.MouseObscuredNow`（= GetWindowAt(mouse) != currentlyDrawnWindow），
    /// 而 HUD 不是 Window、且在 WindowStack 之前绘制 ⇒ 只改窗口标志挡不住。
    /// 正确做法是让 HUD 段整体不画（不画 = 不处理点击）。只跳这两段：
    /// · MapInterface.MapInterfaceOnGUI_BeforeMainTabs（殖民者条/资源栏/右下时间档/地图 gizmo）；
    /// · AlertsReadout.AlertsReadoutOnGUI（右上警报）。
    /// 底部页签栏（MainButtonsRoot）不跳——它在 PFView 视口外，必须留着才能切页签。
    ///
    /// ⚠ 机制已由宿主反编译确认（审计 §6.5）；**具体观感/点击需一次游戏内 5 分钟实测**。
    /// </summary>
    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs))]
    internal static class Patch_PFView_HideMapHud
    {
        private static bool Prefix() => !MainTabWindow_PFView.IsOpen;
    }

    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs))]
    internal static class Patch_PFView_HideMapHudAfterTabs
    {
        private static bool Prefix() => !MainTabWindow_PFView.IsOpen;
    }

    [HarmonyPatch(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutOnGUI))]
    internal static class Patch_PFView_HideAlerts
    {
        private static bool Prefix() => !MainTabWindow_PFView.IsOpen;
    }
}
