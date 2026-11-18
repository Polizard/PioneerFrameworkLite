using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 驾驶舱几何与仪表。灰度沿用 PFViewPalette，雷达接触点只读玩家感知层。
    /// </summary>
    public partial class MainTabWindow_PFView : MainTabWindow
    {
        // ── V-1 布局常量 ──
        private const float PanelPad = 8f;

        // 本帧分区（ComputeLayout 算好，绘制与输入共用同一几何）
        private Rect rToolbar;
        private Rect rLeftCol;
        private Rect rRadar;
        private Rect rNavball;
        private Rect rStatus;
        private Rect rInspector;
        private Rect rSteeringStrip;
        private Rect rPilotPanel;
        private PFViewUiLayout uiLayout;
        private (float Width, float Height, bool Details, PFViewUiVisibility Visibility, bool Piloting,
            PFViewDrawerTab Drawer, bool Alert) layoutKey;
        private bool layoutCached;
        private Rect viewportRect;

        // 列表缓冲（追加式，免每帧分配；[M3] 硬规则：不用 IEnumerable）
        private readonly List<Contact> contactBuf = new();

        // 姿态球圆盘贴图（程序化生成一次；RimWorld 纪律：不用外部贴图资源）
        private static Texture2D? pfvDiscTex;

        // 中键/Shift+右键平移拖拽中
        private bool isDraggingPan => dragState.Pan;

        private void ComputeLayout(Rect inRect)
        {
            viewportRect = inRect;
            var tab = drawerTab == PFViewDrawerTab.None && WantsInspector ? PFViewDrawerTab.Details : drawerTab;
            var key = (inRect.width, inRect.height, WantsInspector, instrumentVisibility,
                SteeringController.IsOpen, tab, cockpitAlerts.Count > 0);
            if (!layoutCached || key != layoutKey)
            {
                uiLayout = PFViewUiLayoutCore.Compute(key.width, key.height, key.WantsInspector,
                    instrumentVisibility, SteeringController.IsOpen, tab, key.Item7);
                layoutKey = key;
                layoutCached = true;
            }
            rToolbar = FromUiRect(uiLayout.Toolbar, inRect);
            rLeftCol = FromUiRect(uiLayout.ObjectList, inRect);
            rInspector = FromUiRect(uiLayout.Details, inRect);
            rRadar = FromUiRect(uiLayout.Radar, inRect);
            rNavball = FromUiRect(uiLayout.Navball, inRect);
            rStatus = FromUiRect(uiLayout.Status, inRect);
            rSteeringStrip = FromUiRect(uiLayout.SteeringStrip, inRect);
            rPilotPanel = FromUiRect(uiLayout.PilotPanel, inRect);
            for (int i = 0; i < PFViewUiLayout.OccupiedCount; i++)
                RegisterUiRect(FromUiRect(uiLayout.Occupied(i), inRect));
        }

        private static Rect FromUiRect(PFViewUiRect rect, Rect origin)
            => rect.Width > 0 && rect.Height > 0
                ? new(origin.x + rect.X, origin.y + rect.Y, rect.Width, rect.Height) : default;

        private void RegisterUiRect(Rect rect)
        {
            if (rect.width > 0 && rect.height > 0) uiBlockRects.Add(rect);
        }

        // ════════════════════════════════════════════════════
        //  顶部工具条
        // ════════════════════════════════════════════════════

        private void DrawToolbar()
        {
            var r = rToolbar;
            Widgets.DrawBoxSolid(r, PFViewPalette.BarBg);
            UiKit.SpacecraftUIDecor.DrawDivider(r, r.yMax - 1f, r.width, PFViewPalette.Divider);
            Text.Font = GameFont.Small;
            float by = 4f;
            float x = 6f;
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Navigate), "PFV_Navigate".Translate(), "PFV_NavigateTip".Translate()))
                ShowSystemMenu();
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Body), "PFV_BodyView".Translate(), "PFV_BodyViewTip".Translate()))
                FocusCurrentBody();
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Previous), "PFV_PreviousView".Translate(), "PFV_PreviousViewTip".Translate()))
                RestorePreviousView();
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Display), "PFV_Display".Translate(), "PFV_DisplayTip".Translate()))
                ShowDisplayMenu();
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && focusKind == PFViewFocusKind.Group;
            // 工具条按钮宽度固定（PFViewUiLayoutCore: Camera=144）⇒ 用短名；全名留给模式菜单与设置窗口。
            string cameraMode = ("PFV_CameraShort_" + (focusKind == PFViewFocusKind.Group ? groupCameraMode : PFViewGroupCameraMode.Free)).Translate();
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Camera), "PFV_CameraMode".Translate(cameraMode), "PFV_CameraModeTip".Translate()))
                ShowCameraModeMenu();
            GUI.enabled = previousEnabled;
            string frame = ("PFV_Frame_" + viewFrameMode).Translate();
            string resolved = (viewFrameIsFixed ? "PFV_Frame_Fixed" : "PFV_Frame_Inertial").Translate();
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Frame), frame + " · " + resolved, "PFV_FrameTip".Translate()))
            {
                viewFrameMode = (PFViewFrameMode)(((int)viewFrameMode + 1) % 3);
                lastFrameLogTick = double.MinValue;
            }
            Rect time = NextToolbarRect(ref x, ref by, PFViewToolbarItem.Time);
            DrawTimeControls(time);
            DrawTransferBias(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Transfer));
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Help), "?", "PFV_Help".Translate()))
                Find.WindowStack.Add(new Window_PFViewHelp());
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Debug), "PFV_Debug".Translate(), "PFV_DebugTip".Translate()))
                Window_PFViewDebug.Toggle(this);
            if (uiLayout.ShowsToolbarItem(PFViewToolbarItem.Navball)
                && ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Navball),
                    "PFV_Navball".Translate(), "PFV_NavballReadouts".Translate()))
                OpenInstrumentWindow(PFViewUiInstrument.Navball);
            if (uiLayout.ShowsToolbarItem(PFViewToolbarItem.Cockpit)
                && Widgets.ButtonText(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Cockpit), "PFV_Cockpit".Translate()))
                ToggleDrawer(PFViewDrawerTab.Status);
            if (uiLayout.ShowsToolbarItem(PFViewToolbarItem.Objects)
                && Widgets.ButtonText(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Objects), "PFV_CockpitQueue".Translate()))
                ToggleDrawer(PFViewDrawerTab.Objects);
        }

        private Rect NextToolbarRect(ref float x, ref float y, PFViewToolbarItem item)
        {
            return FromUiRect(PFViewUiLayoutCore.NextToolbarRect(rToolbar.width, ref x, ref y, item), rToolbar);
        }

        private static bool ToolbarButton(Rect rr, string label, string tip)
        {
            TooltipHandler.TipRegion(rr, tip);
            return Widgets.ButtonText(rr, label);
        }

        /// <summary>玩家向显示开关（调试项已移到 DebugTabMenu_PFL）。</summary>
        private void ShowDisplayMenu()
        {
            var opts = new List<FloatMenuOption>
            {
                new((debugMapRelief ? "✓ " : "   ") + "地图浮雕", () => debugMapRelief = !debugMapRelief),
                new((PFView.ShipBuildingVisuals.ShipBuildingVisualRenderer.Enabled ? "✓ " : "   ") + "PFV_BuildingVisuals".Translate(),
                    () => PFView.ShipBuildingVisuals.ShipBuildingVisualRenderer.Enabled = !PFView.ShipBuildingVisuals.ShipBuildingVisualRenderer.Enabled),
                new((debugSettlements ? "✓ " : "   ") + "定居点（含夜灯）", () => debugSettlements = !debugSettlements),
                new("PFV_Navball".Translate() + " ▸", () => ShowInstrumentModeMenu(PFViewUiInstrument.Navball)),
                new("PFV_Radar".Translate() + " ▸", () => ShowInstrumentModeMenu(PFViewUiInstrument.Radar)),
            };
            if (!uiLayout.RequiresInstrumentDrawer)
                opts.Add(new("PFV_NavballReadouts".Translate(), () => OpenInstrumentWindow(PFViewUiInstrument.Navball)));
            Find.WindowStack.Add(new FloatMenu(opts));
        }


        /// <summary>
        /// PFView 内的时间速度控制（2026-11-18 用户令**恢复**；`bee087b` 曾把它换成"只留暂停"）。
        ///
        /// 为什么要恢复：视图是全屏的，宿主 HUD 的时间档按钮被 HUD 隔离补丁挡住/盖住 ⇒ 视图内
        /// 没有任何办法加速。行星际转移动辄几天模拟时间，没有 Fast/Ultrafast 根本跑不完。
        ///
        /// 宽度约束：`PFViewToolbarItem.Time` 是 200 px —— 5 个按钮 ×30 + 4×2 = 158 px，
        /// 剩下的 42 px 再放状态标签会挤成一团 ⇒ 状态只进 tooltip，不再画文字。
        /// </summary>
        private static void DrawTimeControls(Rect rect)
        {
            const float width = 30f;
            const float gap = 2f;
            float x = rect.x;
            // 第一组：**游戏速度**（原版 1/2/3/4 档，和原版 HUD 同义）。
            DrawTimeSpeedButton(new Rect(x, rect.y, width, rect.height), "II", TimeSpeed.Paused, "暂停");
            x += width + gap;
            DrawTimeSpeedButton(new Rect(x, rect.y, width, rect.height), "1×", TimeSpeed.Normal, "正常速度（1×）");
            x += width + gap;
            DrawTimeSpeedButton(new Rect(x, rect.y, width, rect.height), "3×", TimeSpeed.Fast, "快速（3×）");
            x += width + gap;
            DrawTimeSpeedButton(new Rect(x, rect.y, width, rect.height), "6×", TimeSpeed.Superfast, "超快（6×）");
            x += width + gap;
            DrawTimeSpeedButton(new Rect(x, rect.y, width, rect.height), "15×", TimeSpeed.Ultrafast, "极速（15×）");
            x += width + gap + 4f;

            // 第二组：**PFL 物理速率**（与游戏速度正交）—— Combat 1/60× / Normal 1× / Fast 8×。
            // 2026-11-18 用户：「和游戏那个 123 正交的那组」。它决定每个游戏 tick 推进多少物理秒，
            // 与上面那组是乘法关系。
            var clock = Current.Game?.GetComponent<TimeScaleManager>();
            DrawPhysicsRateButton(new Rect(x, rect.y, 36f, rect.height), "1/60×", TimeScaleLevel.Combat,
                clock, "物理速率 1/60×（战斗档：慢放，每 tick 1/60 物理秒）");
            x += 36f + gap;
            DrawPhysicsRateButton(new Rect(x, rect.y, 36f, rect.height), "1×", TimeScaleLevel.Normal,
                clock, "物理速率 1×（正常：每 tick 1 物理秒）");
            x += 36f + gap;
            DrawPhysicsRateButton(new Rect(x, rect.y, 36f, rect.height), "8×", TimeScaleLevel.Fast,
                clock, "物理速率 8×（快速：每 tick 8 物理秒；跨行星转移通常用这档）");
            x += 36f + gap;

            var status = new Rect(x, rect.y, Math.Max(0f, rect.xMax - x), rect.height);
            if (clock != null && status.width > 0f) TooltipHandler.TipRegion(status, clock.StatusTip);
        }

        /// <summary>PFL 物理速率按钮（1/60× / 1× / 8×），点击调 `TimeScaleManager` 的对应档。</summary>
        private static void DrawPhysicsRateButton(Rect rect, string label, TimeScaleLevel level,
            TimeScaleManager? clock, string tip)
        {
            TooltipHandler.TipRegion(rect, tip);
            bool active = clock != null && clock.CurrentLevel == level;
            Color previous = GUI.color;
            GUI.color = active ? Color.white : new Color(0.62f, 0.62f, 0.62f);
            if (Widgets.ButtonText(rect, label))
            {
                if (clock != null)
                {
                    switch (level)
                    {
                        case TimeScaleLevel.Combat: clock.SetCombatMode(); break;
                        case TimeScaleLevel.Fast: clock.SetFastMode(); break;
                        default: clock.SetNormalMode(); break;
                    }
                }
            }
            GUI.color = previous;
        }

        /// <summary>
        /// **行星际转移偏好滑条**（2026-11-18，用户选型）：0 = 最省 Δv、1 = 最快。
        /// 取值写进静态字段，规划时由 `TryBeginTorchTransfer(..., timeBias)` 读取。
        /// </summary>
        internal static float transferTimeBias = 0.5f;

        private static void DrawTransferBias(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y, 58f, rect.height), "用时/省油");
            var slider = new Rect(rect.x + 60f, rect.y + 2f, Math.Max(20f, rect.width - 64f), rect.height - 4f);
            float bias = GUI.HorizontalSlider(slider, transferTimeBias, 0f, 1f);
            if (Math.Abs(bias - transferTimeBias) > 0.001f)
            {
                transferTimeBias = bias;
                Log.Message($"[火炬·偏好] 转移到用时偏好 = {transferTimeBias:F2}（0=最省 Δv，1=最快）");
            }
            TooltipHandler.TipRegion(rect, $"行星际转移偏好 {transferTimeBias:F2}：0=最省 Δv，1=最快（选择仍受 Δv 预算约束）");
        }
        private static void DrawTimeSpeedButton(Rect rect, string label, TimeSpeed speed, string tip)
        {
            TooltipHandler.TipRegion(rect, tip);
            bool active = Find.TickManager != null && Find.TickManager.CurTimeSpeed == speed;
            Color previous = GUI.color;
            GUI.color = active ? Color.white : new Color(0.62f, 0.62f, 0.62f);
            if (Widgets.ButtonText(rect, label)) SetGameSpeed(speed);
            GUI.color = previous;
        }

        /// <summary>设置游戏时间档（暂停走原版 TogglePaused，保留 prePause 语义）。</summary>
        internal static void SetGameSpeed(TimeSpeed speed)
        {
            var clock = Current.Game?.GetComponent<TimeScaleManager>();
            if (clock != null)
            {
                clock.SetGameSpeed(speed);
                return;
            }
            var ticks = Find.TickManager;
            if (ticks == null) return;
            if (speed == TimeSpeed.Paused)
            {
                if (ticks.CurTimeSpeed != TimeSpeed.Paused) ticks.TogglePaused();
                return;
            }
            ticks.CurTimeSpeed = speed;
        }

        // ════════════════════════════════════════════════════
        //  右侧雷达（以操作舰或所选己方编队为原点）
        // ════════════════════════════════════════════════════

        private void DrawRadarPanel(int groupId)
        {
            var r = rRadar;
            Widgets.DrawBoxSolid(r, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(r, PFViewPalette.Border);

            bool targeting = operationContext.WeaponsOpen && groupId >= 0 && ResolveOperationGroupId() == groupId;
            MiniLabel(new Rect(r.x + 8f, r.y + 4f, r.width - 16f, 20f),
                "PFV_RadarSource".Translate(groupId >= 0 ? "G" + groupId : "—"), PFViewPalette.TextSecondary);

            Vector2 c = new Vector2(r.center.x, r.center.y + 8f);
            float radius = Mathf.Min(r.width, r.height) * 0.5f - 28f;
            if (radius < 12f) return;

            // 环 + 十字；"上 = 顺行方向"
            for (int i = 1; i <= 3; i++)
                DrawScreenCircle(c, radius * i / 3f, PFViewPalette.Grid);
            Widgets.DrawLine(new Vector2(c.x, c.y - radius), new Vector2(c.x, c.y + radius), PFViewPalette.Grid, 1f);
            Widgets.DrawLine(new Vector2(c.x - radius, c.y), new Vector2(c.x + radius, c.y), PFViewPalette.Grid, 1f);
            MiniLabel(new Rect(c.x - 34f, c.y - radius - 14f, 68f, 12f), "PFV_CockpitPrograde".Translate(), PFViewPalette.TextMuted, TextAnchor.MiddleCenter);
            MiniLabel(new Rect(r.xMax - 46f, c.y - 19f, 44f, 12f), "PFV_CockpitRadial".Translate(), PFViewPalette.TextMuted, TextAnchor.MiddleLeft);

            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (groupId < 0 || sensor == null || !PhysicsApi.TryGetSpacecraft(groupId, out var snap))
            {
                MiniLabel(new Rect(r.x + 8f, r.yMax - 22f, r.width - 16f, 16f), "PFV_NoSelection".Translate(), PFViewPalette.TextSecondary, TextAnchor.MiddleCenter);
                return;
            }

            var player = Faction.OfPlayer;
            int playerFid = player?.loadID ?? 0;

            // 选中编队的轨道系基：R = 径向外、N = 轨道法向、T = 顺行（= N × R）
            DVector3 center = GetGroupRelPos(groupId);
            DVector3 radial = center.sqrMagnitude > 1.0 ? center.normalized : new DVector3(1, 0, 0);
            DVector3 nrm = snap.CircularOrbitNormal.sqrMagnitude > 0.001
                ? snap.CircularOrbitNormal.normalized
                : DVector3.Cross(radial, snap.VelocitySystem).normalized;
            DVector3 pro = DVector3.Cross(nrm, radial).normalized;

            contactBuf.Clear();
            sensor.AppendVisible(playerFid, contactBuf);

            // 自适应量程（含全环标注）
            float maxR = 1000f;
            for (int i = 0; i < contactBuf.Count; i++)
            {
                var cc = contactBuf[i];
                if (cc.targetGroupId == groupId) continue;
                DVector3 rel = cc.position - center;
                float up = (float)DVector3.Dot(rel, pro);
                float right = (float)DVector3.Dot(rel, radial);
                maxR = Mathf.Max(maxR, Mathf.Sqrt(up * up + right * right));
            }
            float scale = radius * 0.92f / maxR;

            // 扫描线 + 尾迹（2026-10-18）：真实波束联动；**先于接触点**绘制（余辉要读本帧扫过的区间）
            DrawRadarSweep(c, radius, groupId, snap, radial, pro);

            bool pick = targeting && Event.current.type == EventType.MouseDown && Event.current.button == 0 && r.Contains(Event.current.mousePosition);
            int picked = -1;
            float bestPickDistance = 12f * 12f;
            // 接触 blip（灰度：形状/明度表态，不靠色相）
            for (int i = 0; i < contactBuf.Count; i++)
            {
                var cc = contactBuf[i];
                if (cc.targetGroupId == groupId) continue;

                DVector3 rel = cc.position - center;
                float up = (float)DVector3.Dot(rel, pro);
                float right = (float)DVector3.Dot(rel, radial);
                Vector2 p = c + new Vector2(right, -up) * scale;
                if ((p - c).magnitude > radius * 1.02f) continue;

                // 被扫描线扫过 ⇒ 提亮 + 微微放大 + 短余辉（灰度：仍是明度差，不用色相）
                int blipKey = cc.targetGroupId >= 0 ? cc.targetGroupId : -1 - cc.decoyId;
                RegisterBlipSweep(blipKey, Mathf.Atan2(p.y - c.y, p.x - c.x) * Mathf.Rad2Deg);
                float flash = BlipFlashOf(blipKey);
                Color col = cc.isDecoy ? PFViewPalette.StateDead : PFViewPalette.StateWarn;
                if (flash > 0f) col = Color.Lerp(col, PFViewPalette.SweepLine, flash * BlipFlashPeak);
                float half = 1.75f + flash * 1.1f;
                Widgets.DrawBoxSolid(new Rect(p.x - half, p.y - half, half * 2f, half * 2f), col);
                TooltipHandler.TipRegion(new Rect(p.x - 8, p.y - 8, 16, 16),
                    targeting && !cc.isDecoy ? ContactDisplayLabel(cc) + "\n" + "PFV_RadarTargetHint".Translate() : ContactDisplayLabel(cc));
                if (weaponTarget.IsSet && weaponTarget.GroupId == cc.targetGroupId && weaponTarget.DecoyId == cc.decoyId)
                {
                    Widgets.DrawBox(new Rect(p.x - 6, p.y - 6, 12, 12));
                    float labelX = Mathf.Clamp(p.x + 9, r.x + 4, r.xMax - 94);
                    MiniLabel(new Rect(labelX, p.y - 10, 90, 20), ContactDisplayLabel(cc), PFViewPalette.TextPrimary);
                }
                float pickDistance = (Event.current.mousePosition - p).sqrMagnitude;
                if (pick && !cc.isDecoy && pickDistance < bestPickDistance)
                { picked = i; bestPickDistance = pickDistance; }
            }
            if (picked >= 0)
            {
                SetTargetContact(contactBuf[picked]);
                Event.current.Use();
            }

            // 中心 = 选中编队
            Widgets.DrawLine(new Vector2(c.x - 5f, c.y), new Vector2(c.x + 5f, c.y), PFViewPalette.Accent, 1f);
            Widgets.DrawLine(new Vector2(c.x, c.y - 5f), new Vector2(c.x, c.y + 5f), PFViewPalette.Accent, 1f);

            MiniLabel(new Rect(r.x + 8f, r.yMax - 18f, r.width - 16f, 14f),
                "PFV_RadarRange".Translate(PFViewUiObjectListCore.FormatDistance(maxR)), PFViewPalette.TextSecondary);
        }

        private static void DrawScreenCircle(Vector2 c, float r, Color col)
        {
            const int seg = 48;
            Vector2 prev = c + new Vector2(r, 0f);
            for (int i = 1; i <= seg; i++)
            {
                float a = Mathf.PI * 2f * i / seg;
                Vector2 p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                Widgets.DrawLine(prev, p, col, 1f);
                prev = p;
            }
        }

        // ════════════════════════════════════════════════════
        //  下边中央姿态球（KSP 式）
        // ════════════════════════════════════════════════════

        private void DrawNavballPanel(int groupId)
        {
            var r = rNavball;
            bool docked = r.height <= PFViewUiLayoutCore.DockNavballHeight;
            bool withReadouts = docked && r.width >= PFViewUiLayoutCore.DockNavballReadoutsWidth;
            var title = docked
                ? new Rect(withReadouts ? r.x + 88f : r.x + 5f, r.y + 4f, withReadouts ? r.width - 94f : r.width - 10f, 16f)
                : new Rect(r.x, r.y - 24f, r.width, 24f);
            Widgets.DrawBoxSolid(r, PFViewPalette.BarBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(r, PFViewPalette.Border);
            string source = "PFV_NavballSource".Translate(groupId >= 0 ? "G" + groupId : "—");
            if (docked) MiniLabel(title, source, PFViewPalette.TextPrimary,
                withReadouts ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter);
            else
            {
                Widgets.DrawBoxSolid(title, PFViewPalette.PanelBg);
                Widgets.Label(title, source);
            }

            float radius = docked ? (withReadouts ? 34f : 30f) : Mathf.Min(r.width, r.height) * 0.5f - 12f;
            Vector2 c = withReadouts ? new(r.x + 41f, r.center.y)
                : docked ? r.center + new Vector2(0f, 9f) : r.center;

            EnsureDiscTexture();
            if (pfvDiscTex == null) return;   // 贴图生成失败：不画球（防御，正常不会发生）
            GUI.color = PFViewPalette.PanelBg;
            GUI.DrawTexture(new Rect(c.x - radius, c.y - radius, radius * 2f, radius * 2f), pfvDiscTex);
            GUI.color = Color.white;

            if (groupId < 0 || !PhysicsApi.TryGetSpacecraft(groupId, out var snap))
            {
                MiniLabel(new Rect(c.x - radius, c.y - 8f, radius * 2, 16f), docked ? "—" : "PFV_NoSelection".Translate().ToString(),
                    PFViewPalette.TextSecondary, TextAnchor.MiddleCenter);
                return;
            }

            // 球面固定网格 = 船体系；屏幕 +Y 上 = 船体 +Y，+Z 朝向观察者
            DrawNavballGrid(c, radius);

            Quaternion qi = Quaternion.Inverse(snap.Rotation.ToQuaternion());

            Vector3 vel = (Vector3)snap.VelocitySystem;
            Vector3 pos = (Vector3)snap.PositionSystem;
            Vector3 nrm = (Vector3)snap.CircularOrbitNormal;

            DrawNavballMarker(c, radius, qi, vel, "P", PFViewPalette.Accent);
            DrawNavballMarker(c, radius, qi, -vel, "A", PFViewPalette.TextSecondary);
            DrawNavballMarker(c, radius, qi, pos, "R", PFViewPalette.TextSecondary);
            DrawNavballMarker(c, radius, qi, -pos, "r", PFViewPalette.TextSecondary);
            if (nrm.sqrMagnitude > 0.001f)
            {
                DrawNavballMarker(c, radius, qi, nrm, "N", PFViewPalette.TextSecondary);
                DrawNavballMarker(c, radius, qi, -nrm, "S", PFViewPalette.TextSecondary);
            }

            if (PhysicsQueries.TryGetMainThrustAxis(groupId, out DVector3 thrust) && thrust.sqrMagnitude > 1e-12)
                DrawNavballMarker(c, radius, qi, (Vector3)thrust.normalized, "T", PFViewPalette.Accent);

            // 左右文字区（左：姿态/主推轴；右：速度 + 记号图例）——球放大 1.5× 后放两侧
            if (withReadouts)
            {
                Vector3 euler = snap.Rotation.ToQuaternion().eulerAngles;
                float x = title.x, w = title.width, y = title.yMax + 5f;
                MiniLabel(new(x, y, w, 16f), $"V {vel.magnitude:F0} m/s", PFViewPalette.TextPrimary);
                MiniLabel(new(x, y + 17f, w, 16f), $"Y {euler.y:F0}° · P {euler.x:F0}°", PFViewPalette.TextSecondary);
                MiniLabel(new(x, y + 34f, w, 16f), $"R {euler.z:F0}°", PFViewPalette.TextSecondary);
            }
            else if (!docked && r.width >= 500f) DrawNavballFlanks(r, c, radius, groupId, snap, vel);
        }

        /// <summary>姿态球两侧文字读数（V-1.1）：左 = 姿态/角速度/主推轴；右 = 速度 + 记号图例。</summary>
        private void DrawNavballFlanks(Rect r, Vector2 c, float radius, int groupId, in SpacecraftSnapshot snap, Vector3 vel)
        {
            float sideW = (r.width - radius * 2f) * 0.5f;
            float lx = r.x + 8f;
            float rx = c.x + radius + 8f;
            const float lh = 16f;

            // 左：姿态 + 主推轴（与姿态球上的 T 记号同一份数据）
            float y = r.y + 10f;
            MiniLabel(new Rect(lx, y, sideW - 16f, lh), "姿态", PFViewPalette.TextSecondary);
            y += lh;
            Vector3 euler = snap.Rotation.ToQuaternion().eulerAngles;
            MiniLabel(new Rect(lx, y, sideW - 16f, lh), $"yaw {euler.y:F0}° pitch {euler.x:F0}° roll {euler.z:F0}°", PFViewPalette.TextPrimary);
            y += lh;
            MiniLabel(new Rect(lx, y, sideW - 16f, lh), $"角速度 {snap.AngularVelocity.magnitude * Mathf.Rad2Deg:F1}°/s", PFViewPalette.TextPrimary);
            y += lh + 6f;

            MiniLabel(new Rect(lx, y, sideW - 16f, lh), "主推轴（T 记号）", PFViewPalette.TextSecondary);
            y += lh;
            PhysicsQueries.TryGetMainThrustAxis(groupId, out DVector3 thrustAxis);
            if (thrustAxis.sqrMagnitude > 1e-12)
            {
                MiniLabel(new Rect(lx, y, sideW - 16f, lh), DirectionLabel(thrustAxis), PFViewPalette.TextPrimary);
                y += lh;
                if (vel.sqrMagnitude > 1e-6f)
                {
                    float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot((Vector3)thrustAxis.normalized, vel.normalized), -1f, 1f)) * Mathf.Rad2Deg;
                    MiniLabel(new Rect(lx, y, sideW - 16f, lh), $"与速度夹角 {ang:F0}°", PFViewPalette.TextMuted);
                }
            }
            else
            {
                MiniLabel(new Rect(lx, y, sideW - 16f, lh), "无（无引擎/全向）", PFViewPalette.TextMuted);
            }

            // 右：本组速度 + 记号图例
            y = r.y + 10f;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "本组速度", PFViewPalette.TextSecondary);
            y += lh;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), $"{vel.magnitude:F0} m/s", PFViewPalette.TextPrimary);
            y += lh + 6f;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "记号图例", PFViewPalette.TextSecondary);
            y += lh;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "P 顺行 / A 逆行", PFViewPalette.TextMuted); y += lh;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "R 径外 / r 径内", PFViewPalette.TextMuted); y += lh;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "N 法向 / S 反法向", PFViewPalette.TextMuted); y += lh;
            MiniLabel(new Rect(rx, y, sideW - 16f, lh), "T 主推轴", PFViewPalette.TextMuted);
        }

        /// <summary>船体系固定网格（子午线 + 纬线 + 外圈），只画正面半球段。</summary>
        private static void DrawNavballGrid(Vector2 c, float radius)
        {
            const int seg = 20;
            Color col = PFViewPalette.Grid;

            // 子午线（含 Y 轴的大圆）
            for (int k = 0; k < 4; k++)
            {
                float phi = Mathf.PI * k / 4f;
                bool has = false;
                Vector2 prev = Vector2.zero;
                for (int i = 0; i <= seg; i++)
                {
                    float th = Mathf.PI * i / seg;
                    Vector3 d = new Vector3(Mathf.Sin(th) * Mathf.Cos(phi), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(phi));
                    if (d.z <= 0.03f) { has = false; continue; }
                    Vector2 p = c + new Vector2(d.x, -d.y) * radius;
                    if (has) Widgets.DrawLine(prev, p, col, 1f);
                    prev = p; has = true;
                }
            }

            // 纬线（3 条）
            for (int k = 1; k <= 3; k++)
            {
                float lat = -Mathf.PI * 0.5f + Mathf.PI * k / 4f;
                float y = Mathf.Sin(lat), rr = Mathf.Cos(lat);
                bool has = false;
                Vector2 prev = Vector2.zero;
                for (int i = 0; i <= seg * 2; i++)
                {
                    float ph = Mathf.PI * 2f * i / (seg * 2);
                    Vector3 d = new Vector3(rr * Mathf.Cos(ph), y, rr * Mathf.Sin(ph));
                    if (d.z <= 0.03f) { has = false; continue; }
                    Vector2 p = c + new Vector2(d.x, -d.y) * radius;
                    if (has) Widgets.DrawLine(prev, p, col, 1f);
                    prev = p; has = true;
                }
            }

            // 外圈
            DrawScreenCircle(c, radius, PFViewPalette.Divider);
        }

        /// <summary>把世界方向投到球上（体轴系 + 小方点 + 字母标签；背面镜像贴边）。</summary>
        private static void DrawNavballMarker(Vector2 c, float radius, Quaternion qi, Vector3 worldDir, string label, Color col)
        {
            if (worldDir.sqrMagnitude < 1e-9f) return;

            Vector3 d = qi * worldDir.normalized;
            bool front = d.z > 0f;
            Vector2 off = new Vector2(d.x, -d.y);
            float m = off.magnitude;
            if (m < 1e-4f) off = Vector2.zero;
            else if (m > 0.96f) off = off / m * 0.96f;
            if (!front) off = -off;

            Vector2 p = c + off * radius;
            float half = radius < 48f ? 1.5f : 2.5f;
            float labelWidth = radius < 48f ? 10f : 22f;
            Widgets.DrawBoxSolid(new Rect(p.x - half, p.y - half, 2 * half, 2 * half), col);
            UiKit.SpacecraftUIDecor.DrawColoredLabel(new Rect(p.x + half + 1.5f, p.y - 8f, labelWidth, 14f), label,
                front ? col : PFViewPalette.TextMuted, GameFont.Tiny, TextAnchor.MiddleLeft);
        }

        private static void EnsureDiscTexture()
        {
            if (pfvDiscTex != null) return;
            const int size = 96;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size * 0.5f;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r, dy = y + 0.5f - r;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(r - dist);   // 1px 羽化
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            pfvDiscTex = tex;
        }

        // ════════════════════════════════════════════════════
        //  状态行 + 小工具
        // ════════════════════════════════════════════════════

        private void DrawStatusLine()
        {
            string viewed = inspectedContactKey != null && contactDisplayIds.TryGetValue(inspectedContactKey, out string? contactId)
                ? contactId : selectedGroupId >= 0 ? "G" + selectedGroupId : "—";
            var mgr = Current.Game?.GetComponent<CelestialBodyManager>();
            string focus = focusKind == PFViewFocusKind.Group ? "G" + focusGroupId
                : focusKind == PFViewFocusKind.Frozen ? "PFV_Frozen".Translate().ToString() : mgr?.GetBodyName(focusBodyId) ?? "—";
            int sourceId = ResolveOperationGroupId();
            string source = sourceId >= 0 ? "G" + sourceId : "—";
            string target = WeaponTargetLabel;
            string distance = PFViewUiObjectListCore.FormatDistance(camDisplayDistance * ViewToMeters);
            string text = "PFV_Status".Translate(viewed, focus, source, target, distance);
            Widgets.DrawBoxSolid(rStatus, PFViewPalette.BarBg);
            Text.Font = GameFont.Small;
            Widgets.Label(new(rStatus.x + 8, rStatus.y, rStatus.width - 16, rStatus.height), text);
            TooltipHandler.TipRegion(rStatus, text);
        }
        private static bool ListRow(Rect rr, bool selected, string text, Color color, out bool doubleClicked)
        {
            doubleClicked = false;
            bool over = Mouse.IsOver(rr);
            if (selected) Widgets.DrawBoxSolid(rr, PFViewPalette.PanelBgRaised);
            else if (over) Widgets.DrawBoxSolid(rr, PFViewPalette.PanelBgHover);

            UiKit.SpacecraftUIDecor.DrawColoredLabel(new Rect(rr.x + 4f, rr.y, rr.width - 8f, rr.height),
                text, color, GameFont.Tiny, TextAnchor.MiddleLeft);

            if (!over) return false;
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (e.clickCount >= 2) doubleClicked = true;
                e.Use();
                return true;
            }
            return false;
        }

        private static void MiniLabel(Rect r, string text, Color color, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            UiKit.SpacecraftUIDecor.DrawColoredLabel(r, text, color, GameFont.Tiny, anchor);
        }
    }
}
