using System.Globalization;
using PioneerFramework.ShipSkin;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 调试窗口（2026-10-18 用户：「把本该有的 debug 选项放回 PFView」）。
    /// 从工具条「调试」打开；包含原先散在工具条/信息面板里的测试与作弊项：
    /// 放大部件 / 太阳标记 / 地图浮雕 / 定居点 / 部署交火测试对 / 目标传送 / 目标 ±100 m/s / 预生成舱室。
    /// 与 DebugTabMenu_PFL 双入口（两处操作同一份状态）。
    ///
    /// 2026-10-28 P0 恒星性能探针：本窗口另承担"拉近恒星掉帧"的测量入口（surface/halo 开关、
    /// 步数预算、RT 缩放、距离跳转、帧时读数）。**窗口关闭即停止记录（零开销）**；
    /// 测量结束可按「恢复默认」还原渲染设置。
    /// </summary>
    public class Window_PFViewDebug : Window
    {
        private readonly MainTabWindow_PFView win;
        private Vector2 scroll;
        private const float ContentHeight = 1292f;

        public override Vector2 InitialSize => new Vector2(620f, 760f);

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

        public override void PostOpen()
        {
            base.PostOpen();
            PFViewStellarProbe.Active = true;
            win.DebugStellarResetStats();
        }

        public override void PreClose()
        {
            PFViewStellarProbe.Active = false;
            base.PreClose();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 28f), "PFView 调试");
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.6f, 0.6f, 0.6f);
            Widgets.Label(new Rect(0f, 24f, inRect.width, 18f), "与「PFL 测试 → 20 视图/表现」是同一份状态");
            GUI.color = Color.white;

            var view = new Rect(0f, 44f, inRect.width, Mathf.Max(1f, inRect.height - 44f));
            var content = new Rect(0f, 0f, Mathf.Max(1f, view.width - 20f), ContentHeight);
            Widgets.BeginScrollView(view, ref scroll, content);
            float y = 0f;
            float w = content.width;
            const float Row = 26f;

            bool v = MainTabWindow_PFView.DebugScaleParts;
            bool before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "放大部件（×200）", ref v);
            if (v != before) MainTabWindow_PFView.DebugScaleParts = v;
            y += Row;

            v = MainTabWindow_PFView.DebugShowSunMarker;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "太阳位置标记（黄色方块）", ref v);
            if (v != before) MainTabWindow_PFView.DebugShowSunMarker = v;
            y += Row;

            // 2026-11-08：PFView 引擎喷焰（P0/P1）。锚点检查 = 只画喷口点；强制显示 = 不看点火判据。
            v = PFViewEnginePlumeProbe.Enabled;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "引擎喷焰（PFView 3D）", ref v);
            if (v != before) PFViewEnginePlumeProbe.Enabled = v;
            y += Row;

            v = PFViewEnginePlumeProbe.NozzleOnly;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "引擎喷焰：只画喷口点（诊断，不是尾焰）", ref v);
            if (v != before) PFViewEnginePlumeProbe.NozzleOnly = v;
            y += Row;

            v = PFViewEnginePlumeProbe.ForceAllEngines;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "引擎喷焰：强制显示全部喷口（不点火也画）", ref v);
            if (v != before) PFViewEnginePlumeProbe.ForceAllEngines = v;
            y += Row;

            v = PFViewEnginePlumeProbe.DrawOnlySelectedGroup;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "引擎喷焰：只看当前选中编队", ref v);
            if (v != before) PFViewEnginePlumeProbe.DrawOnlySelectedGroup = v;
            y += Row;

            if (Widgets.ButtonText(new Rect(0f, y, w * 0.24f, Row), "强度 -"))
                PFViewEnginePlumeProbe.IntensityScale = Mathf.Max(0.3f, PFViewEnginePlumeProbe.IntensityScale - 0.1f);
            if (Widgets.ButtonText(new Rect(w * 0.26f, y, w * 0.24f, Row), "强度 +"))
                PFViewEnginePlumeProbe.IntensityScale = Mathf.Min(2f, PFViewEnginePlumeProbe.IntensityScale + 0.1f);
            Widgets.Label(new Rect(w * 0.52f, y, w * 0.48f, Row),
                $"喷焰强度 ×{PFViewEnginePlumeProbe.IntensityScale:F1}");
            y += Row;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.62f, 0.68f, 0.78f);
            Widgets.Label(new Rect(4f, y, w - 4f, 18f),
                $"引擎 {PFViewEnginePlumeProbe.LastEngineCount} · 片 {PFViewEnginePlumeProbe.LastQuadCount}"
                + $" · 推进器 {PFViewEnginePlumeProbe.LastMapThrusters}"
                + $" · 超限 {PFViewEnginePlumeProbe.LastSkippedEngines}");
            GUI.color = Color.white;
            y += 20f;

            v = win.DebugMapRelief;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "地图浮雕（有 Map 的编队）", ref v);
            if (v != before) win.DebugMapRelief = v;
            y += Row;

            // 2026-10-19 舰船蒙皮：只加表面层（板缝/倒角/阵营色），不改形体；关 = 旧纯色盒体/三色浮雕。
            v = ShipSkinSettings.Enabled;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "舰船蒙皮：工业装甲（关 = 旧纯色）", ref v);
            if (v != before) ShipSkinSettings.Enabled = v;
            y += Row;

            // 现场读数：是"没加载"还是"加载了但看不见"——这张行文字就是判据。
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.62f, 0.68f, 0.78f);
            Widgets.Label(new Rect(4f, y, w - 4f, 18f), "蒙皮状态：" + ShipSkinAssets.StatusText);
            GUI.color = Color.white;
            y += 20f;

            v = ShipSkinAssets.DebugHighFrequency;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "蒙皮调试：高频板缝（验证贴图是否生效）", ref v);
            if (v != before) ShipSkinAssets.SetDebugHighFrequency(v);
            y += Row;

            v = win.DebugSettlements;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "定居点层（含夜灯）", ref v);
            if (v != before) win.DebugSettlements = v;
            y += Row + 6f;

            if (Widgets.ButtonText(new Rect(0f, y, w, Row), "部署交火测试对（我方 + 敌对）"))
                win.DebugDeployCombatPair();
            y += Row + 4f;

            if (Widgets.ButtonText(new Rect(0f, y, w, Row), "把目标传送到正前方 1km"))
                win.DebugTeleportTarget();
            y += Row + 4f;

            if (Widgets.ButtonText(new Rect(0f, y, w * 0.48f, Row), "目标 +100 m/s"))
                win.DebugAdjustTargetSpeed(100.0);
            if (Widgets.ButtonText(new Rect(w * 0.52f, y, w * 0.48f, Row), "目标 -100 m/s"))
                win.DebugAdjustTargetSpeed(-100.0);
            y += Row + 4f;

            if (Widgets.ButtonText(new Rect(0f, y, w, Row), "预生成当前选中编队的舱室地图"))
                win.DebugPregenerateRoomMaps();
            y += Row + 10f;

            // ════════ P0 恒星性能探针（2026-10-28） ════════
            GUI.color = new Color(0.85f, 0.9f, 1f);
            Widgets.Label(new Rect(0f, y, w, 20f), "— 恒星性能探针 —");
            GUI.color = Color.white;
            y += 22f;

            v = PFViewStellarProbe.SurfacePass;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "surface pass（不透明光球/日冕体积）", ref v);
            if (v != before) PFViewStellarProbe.SurfacePass = v;
            y += Row;

            v = PFViewStellarProbe.HaloPass;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "halo pass（透明日冕）", ref v);
            if (v != before) PFViewStellarProbe.HaloPass = v;
            y += Row + 2f;

            v = win.DebugUseStellarHeatCache;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "PFV_StellarHeatCache".Translate(), ref v);
            if (v != win.DebugUseStellarHeatCache)
            {
                win.DebugUseStellarHeatCache = v;
                win.DebugStellarResetStats();
            }
            y += Row;
            if (v)
            {
                Widgets.Label(new Rect(0f, y, w, Row), win.DebugStellarHeatCacheStatus.Translate());
                y += Row;
            }

            v = PFViewStellarProbe.S3AEnabled;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "区域跳过积分（S3A；重开 PFView 生效）", ref v);
            if (v != before)
            {
                PFViewStellarProbe.S3AEnabled = v;
                win.DebugStellarResetStats();
            }
            y += Row;

            v = PFViewStellarProbe.S3BEnabled;
            before = v;
            Widgets.CheckboxLabeled(new Rect(0f, y, w, Row), "星体半分辨率离屏（S3B 实验；重开 PFView 生效）", ref v);
            if (v != before)
            {
                PFViewStellarProbe.S3BEnabled = v;
                win.DebugStellarResetStats();
            }
            y += Row + 2f;

            Widgets.Label(new Rect(0f, y, w, 18f), "步数预算（两 pass 同步；默认 64）");
            y += 18f;
            DrawBudgetRow(y, w);
            y += 26f;

            Widgets.Label(new Rect(0f, y, w, 18f), "PFView 渲染纹理缩放");
            y += 18f;
            DrawScaleRow(y, w);
            y += 26f;

            Widgets.Label(new Rect(0f, y, w, 18f), "一键跳到恒星机位（先聚焦恒星，再定距）");
            y += 18f;
            DrawDistanceRow(y, w, true, 3.0f, 2.7f, 2.35f, 2.1f);
            y += 28f;
            DrawDistanceRow(y, w, true, 1.8f, 1.65f, 1.5f, 1.2f);
            y += 28f;
            DrawDistanceRow(y, w, false, 1e7f, 5e7f, 8e7f, 0f);
            y += 28f;

            if (Widgets.ButtonText(new Rect(0f, y, w * 0.46f, Row), "恢复默认（全部开关）"))
                win.DebugStellarResetDefaults();
            if (Widgets.ButtonText(new Rect(w * 0.50f, y, w * 0.50f, Row), "清空帧时样本"))
                win.DebugStellarResetStats();
            y += Row + 8f;

            GUI.color = new Color(0.85f, 0.9f, 1f);
            bool wordWrap = Text.WordWrap;
            Text.WordWrap = true;
            Widgets.Label(new Rect(0f, y, w, 84f), "读数（关窗即停止记录）：\n" + win.DebugStellarStatsText());
            GUI.color = Color.white;
            y += 88f;

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.55f, 0.55f, 0.55f);
            Widgets.Label(new Rect(0f, y, w, 80f),
                "测量：固定存档/分辨率/画质，暂停游戏 → 选距离 → 等 2–3 秒 → 读中位/p99；"
                + "切开关/距离会自动清空样本。示例矩阵：距离 × surface/halo × RT 缩放。"
                + "步骤：先 A/A 测噪声地板，再 A/B/A/B 比较。");
            GUI.color = Color.white;
            Text.WordWrap = wordWrap;

            Widgets.EndScrollView();
        }

        private static void DrawBudgetRow(float y, float w)
        {
            float bw = w * 0.235f;
            DrawBudgetButton(0f, y, bw, 64);
            DrawBudgetButton(bw * 1.02f, y, bw, 32);
            DrawBudgetButton(bw * 2.04f, y, bw, 16);
            DrawBudgetButton(bw * 3.06f, y, bw, 8);
        }

        private static void DrawBudgetButton(float x, float y, float width, int budget)
        {
            int current = PFViewStellarProbe.StepBudget;
            bool active = current == budget || (current <= 0 && budget == 64);
            if (Widgets.ButtonText(new Rect(x, y, width, 24f), (active ? "● " : "") + budget))
                PFViewStellarProbe.StepBudget = budget;
        }

        private static void DrawScaleRow(float y, float w)
        {
            float bw = w * 0.31f;
            DrawScaleButton(0f, y, bw, PFViewStellarProbe.DefaultRenderScale, "1.00（默认）");
            DrawScaleButton(bw * 1.03f, y, bw, 0.75f, "0.75");
            DrawScaleButton(bw * 2.06f, y, bw, 0.5f, "0.50（降采样）");
        }

        private static void DrawScaleButton(float x, float y, float width, float scale, string label)
        {
            bool active = Mathf.Abs(PFViewStellarProbe.RenderScale - scale) < 0.001f;
            if (Widgets.ButtonText(new Rect(x, y, width, 24f), (active ? "● " : "") + label))
                PFViewStellarProbe.RenderScale = scale;
        }

        private void DrawDistanceRow(float y, float w, bool bodyRadii, float a, float b, float c, float d)
        {
            float bw = w * 0.235f;
            DrawDistanceButton(0f, y, bw, bodyRadii, a);
            DrawDistanceButton(bw * 1.02f, y, bw, bodyRadii, b);
            DrawDistanceButton(bw * 2.04f, y, bw, bodyRadii, c);
            DrawDistanceButton(bw * 3.06f, y, bw, bodyRadii, d);
        }

        private void DrawDistanceButton(float x, float y, float width, bool bodyRadii, float value)
        {
            if (value <= 0f) return;
            string label = bodyRadii
                ? value.ToString("F2", CultureInfo.InvariantCulture) + "R"
                : (value >= 1e6f ? (value / 1e6f).ToString("F0", CultureInfo.InvariantCulture) + "e6"
                                 : value.ToString("F0", CultureInfo.InvariantCulture));
            if (Widgets.ButtonText(new Rect(x, y, width, 24f), label))
            {
                if (bodyRadii) win.DebugStellarJumpToBodyRadii(value);
                else win.DebugStellarJumpToViewDistance(value);
            }
        }
    }
}