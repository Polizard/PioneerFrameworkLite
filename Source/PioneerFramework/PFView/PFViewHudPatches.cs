using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

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

    /// <summary>
    /// 1.7 Waylith 先向焦点窗口派发键盘，再进入 UIRoot_Play.OnGUI 处理宿主界面。
    /// PFView 的持续操舵键通过轮询发布，未匹配的快捷键也会留下未消费的事件；
    /// 在此消费剩余键盘，避免继续触发世界地图、地图选择器和主页签快捷键。
    /// 鼠标事件保留给既有窗口路由与底部页签；上层弹窗同样先于此处收到输入。
    /// </summary>
    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    internal static class Patch_PFView_BlockHostKeyboard
    {
        private static void Prefix(UIRoot_Play __instance)
            => ConsumeHostKeyboard(Event.current, __instance.windows.IsOpen(typeof(MainTabWindow_PFView)));

        internal static void ConsumeHostKeyboard(Event? input, bool viewOpen)
        {
            if (viewOpen && input?.isKey == true) input.Use();
        }
    }
}
