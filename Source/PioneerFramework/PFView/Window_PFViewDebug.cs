using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 调试窗口（2026-10-18 用户：「把本该有的 debug 选项放回 PFView」）。
    /// 从工具条「调试」打开；包含原先散在工具条/信息面板里的测试与作弊项：
    /// 放大部件 / 太阳标记 / 地图浮雕 / 定居点 / 部署交火测试对 / 目标传送 / 目标 ±100 m/s / 预生成舱室。
    /// 与 DebugTabMenu_PFL 双入口（两处操作同一份状态）。
    /// </summary>
    public class Window_PFViewDebug : Window
    {
        private readonly MainTabWindow_PFView win;

        public override Vector2 InitialSize => new Vector2(390f, 330f);

        public Window_PFViewDebug(MainTabWindow_PFView win)
        {
            this.win = win;
            doCloseX = true;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;   // 只吃窗口自身区域，别挡 3D 视口
            draggable = true;
            resizeable = false;
        }

        public static void Toggle(MainTabWindow_PFView win)
        {
            var existing = Find.WindowStack?.WindowOfType<Window_PFViewDebug>();
            if (existing != null)
            {
                existing.Close();
                return;
            }
            Find.WindowStack?.Add(new Window_PFViewDebug(win));
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 28f), "PFView 调试");
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.6f, 0.6f, 0.6f);
            Widgets.Label(new Rect(0f, 24f, inRect.width, 18f), "与「PFL 测试 → 20 视图/表现」是同一份状态");
            GUI.color = Color.white;

            float y = 48f;
            float w = inRect.width;
            bool v = MainTabWindow_PFView.DebugScaleParts;
            bool before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, 24f), "放大部件（×200）", ref v);
            if (v != before) MainTabWindow_PFView.DebugScaleParts = v;
            y += 26f;

            v = MainTabWindow_PFView.DebugShowSunMarker;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, 24f), "太阳位置标记（黄色方块）", ref v);
            if (v != before) MainTabWindow_PFView.DebugShowSunMarker = v;
            y += 26f;

            v = win.DebugMapRelief;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, 24f), "地图浮雕（有 Map 的编队）", ref v);
            if (v != before) win.DebugMapRelief = v;
            y += 26f;

            v = win.DebugSettlements;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, 24f), "定居点层（含夜灯）", ref v);
            if (v != before) win.DebugSettlements = v;
            y += 32f;

            if (Widgets.ButtonText(new Rect(0f, y, w, 26f), "部署交火测试对（我方 + 敌对）"))
                win.DebugDeployCombatPair();
            y += 30f;

            if (Widgets.ButtonText(new Rect(0f, y, w, 26f), "把目标传送到正前方 1km"))
                win.DebugTeleportTarget();
            y += 30f;

            if (Widgets.ButtonText(new Rect(0f, y, w * 0.48f, 26f), "目标 +100 m/s"))
                win.DebugAdjustTargetSpeed(100.0);
            if (Widgets.ButtonText(new Rect(w * 0.52f, y, w * 0.48f, 26f), "目标 -100 m/s"))
                win.DebugAdjustTargetSpeed(-100.0);
            y += 30f;

            if (Widgets.ButtonText(new Rect(0f, y, w, 26f), "预生成当前选中编队的舱室地图"))
                win.DebugPregenerateRoomMaps();
            y += 32f;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.55f, 0.55f, 0.55f);
            Widgets.Label(new Rect(0f, y, w, 34f),
                "目标操作需先选中射手并设目标（Shift+左键）；部署交火测试对会改写选中与目标。");
            GUI.color = Color.white;
        }
    }
}
