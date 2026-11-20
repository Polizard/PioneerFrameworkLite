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
            PFViewDrawerTab Drawer, bool Alert, bool ShipSelected) layoutKey;
        private bool layoutCached;
        private Rect viewportRect;

        // 列表缓冲（追加式，免每帧分配；[M3] 硬规则：不用 IEnumerable）
        private readonly List<Contact> contactBuf = new();
        private readonly List<SpaceEntityGroup> groupListBuf = new();

        // 姿态球圆盘贴图（程序化生成一次；RimWorld 纪律：不用外部贴图资源）
        private static Texture2D? pfvDiscTex;

        // 中键/Shift+右键平移拖拽中
        private bool isDraggingPan => dragState.Pan;

        private void ComputeLayout(Rect inRect, bool shipSelected)
        {
            viewportRect = inRect;
            var tab = drawerTab == PFViewDrawerTab.None && WantsInspector ? PFViewDrawerTab.Details : drawerTab;
            var key = (Width: inRect.width, Height: inRect.height, Details: WantsInspector,
                Visibility: instrumentVisibility, Piloting: SteeringController.IsOpen, Drawer: tab,
                Alert: cockpitAlerts.Count > 0, ShipSelected: shipSelected);
            if (!layoutCached || key != layoutKey)
            {
                uiLayout = PFViewUiLayoutCore.Compute(key.Width, key.Height, key.Details,
                    key.Visibility, key.Piloting, key.Drawer, key.Alert, key.ShipSelected);
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
            DrawPauseAndTimeStatus(time);
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


        private static void DrawPauseAndTimeStatus(Rect rect)
        {
            var ticks = Find.TickManager;
            var pause = new Rect(rect.x, rect.y, 30f, rect.height);
            string label = ticks.CurTimeSpeed == TimeSpeed.Paused ? "▸" : "II";
            if (ToolbarButton(pause, label, "PFV_TogglePause".Translate()))
                ticks.TogglePaused();

            var clock = Current.Game?.GetComponent<TimeScaleManager>();
            if (clock == null) return;
            var status = new Rect(pause.xMax + 6f, rect.y, rect.width - 36f, rect.height);
            TooltipHandler.TipRegion(status, clock.StatusTip);
            Widgets.Label(status, clock.StatusLabel);
        }

        // ════════════════════════════════════════════════════
        //  左侧文字堆：编队 / 接触 / 选中组读数
        // ════════════════════════════════════════════════════

        private static bool IsIntelContact(in Contact c, SpaceGameComp sg)
        {
            if (c.IsForged) return true;
            var g = sg.GetGroupById(c.targetGroupId);
            return g != null && g.IsStealth;
        }

        /// <summary>接触与选中编队在同一中心天体（相距才有意义——两边都是"系统系"坐标）。</summary>
        private static bool ContactBodyMatches(in Contact c, SpaceEntityGroup sel, SpaceGameComp sg)
        {
            var g = sg.GetGroupById(c.targetGroupId);
            return g != null && g.CelestialBodyId == sel.CelestialBodyId;
        }

        private static int EntityCountOf(SpaceEntityGroup g, SpaceGameComp sg)
        {
            var ids = g.GetEntityIds();
            return ids != null ? ids.Count : 0;
        }

        // ════════════════════════════════════════════════════
        //  右侧雷达（以操作舰或所选己方编队为原点）
        // ════════════════════════════════════════════════════

        private void DrawRadarPanel(SpaceEntityGroup? g)
        {
            var r = rRadar;
            Widgets.DrawBoxSolid(r, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(r, PFViewPalette.Border);

            bool targeting = operationContext.WeaponsOpen && g != null && ResolveOperationGroup()?.GroupId == g.GroupId;
            MiniLabel(new Rect(r.x + 8f, r.y + 4f, r.width - 16f, 20f),
                "PFV_RadarSource".Translate(g != null ? "G" + g.GroupId : "—"), PFViewPalette.TextSecondary);

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

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (g == null || sensor == null || spaceGame == null)
            {
                MiniLabel(new Rect(r.x + 8f, r.yMax - 22f, r.width - 16f, 16f), "PFV_NoSelection".Translate(), PFViewPalette.TextSecondary, TextAnchor.MiddleCenter);
                return;
            }

            var player = Faction.OfPlayer;
            int playerFid = player?.loadID ?? 0;

            // 选中编队的轨道系基：R = 径向外、N = 轨道法向、T = 顺行（= N × R）
            DVector3 center = GetGroupRelPos(g);
            DVector3 radial = center.sqrMagnitude > 1.0 ? center.normalized : new DVector3(1, 0, 0);
            DVector3 nrm = g.circularOrbitNormal.sqrMagnitude > 0.001
                ? g.circularOrbitNormal.normalized
                : DVector3.Cross(radial, g.CurrentOrbit.velocitySystem.v).normalized;
            DVector3 pro = DVector3.Cross(nrm, radial).normalized;

            contactBuf.Clear();
            sensor.AppendVisible(playerFid, contactBuf);

            // 自适应量程（含全环标注）
            float maxR = 1000f;
            for (int i = 0; i < contactBuf.Count; i++)
            {
                var cc = contactBuf[i];
                if (cc.targetGroupId == g.GroupId) continue;
                DVector3 rel = cc.position - center;
                float up = (float)DVector3.Dot(rel, pro);
                float right = (float)DVector3.Dot(rel, radial);
                maxR = Mathf.Max(maxR, Mathf.Sqrt(up * up + right * right));
            }
            float scale = radius * 0.92f / maxR;

            // 扫描线 + 尾迹（2026-10-18）：真实波束联动；**先于接触点**绘制（余辉要读本帧扫过的区间）
            DrawRadarSweep(c, radius, g, radial, pro);

            bool pick = targeting && Event.current.type == EventType.MouseDown && Event.current.button == 0 && r.Contains(Event.current.mousePosition);
            int picked = -1;
            float bestPickDistance = 12f * 12f;
            // 接触 blip（灰度：形状/明度表态，不靠色相）
            for (int i = 0; i < contactBuf.Count; i++)
            {
                var cc = contactBuf[i];
                if (cc.targetGroupId == g.GroupId) continue;

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

        private void DrawNavballPanel(SpaceEntityGroup? g)
        {
            var r = rNavball;
            bool docked = r.height <= PFViewUiLayoutCore.DockNavballHeight;
            bool withReadouts = docked && r.width >= PFViewUiLayoutCore.DockNavballReadoutsWidth;
            var title = docked
                ? new Rect(withReadouts ? r.x + 88f : r.x + 5f, r.y + 4f, withReadouts ? r.width - 94f : r.width - 10f, 16f)
                : new Rect(r.x, r.y - 24f, r.width, 24f);
            Widgets.DrawBoxSolid(r, PFViewPalette.BarBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(r, PFViewPalette.Border);
            string source = "PFV_NavballSource".Translate(g != null ? "G" + g.GroupId : "—");
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

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (g == null || spaceGame == null)
            {
                MiniLabel(new Rect(c.x - radius, c.y - 8f, radius * 2, 16f), docked ? "—" : "PFV_NoSelection".Translate().ToString(),
                    PFViewPalette.TextSecondary, TextAnchor.MiddleCenter);
                return;
            }

            // 球面固定网格 = 船体系；屏幕 +Y 上 = 船体 +Y，+Z 朝向观察者
            DrawNavballGrid(c, radius);

            Quaternion qi = Quaternion.Inverse(g.CurrentOrbit.rotation.ToQuaternion());

            Vector3 vel = (Vector3)g.CurrentOrbit.velocitySystem.v;
            Vector3 pos = (Vector3)g.CurrentOrbit.positionSystem.v;
            Vector3 nrm = (Vector3)g.circularOrbitNormal;

            DrawNavballMarker(c, radius, qi, vel, "P", PFViewPalette.Accent);
            DrawNavballMarker(c, radius, qi, -vel, "A", PFViewPalette.TextSecondary);
            DrawNavballMarker(c, radius, qi, pos, "R", PFViewPalette.TextSecondary);
            DrawNavballMarker(c, radius, qi, -pos, "r", PFViewPalette.TextSecondary);
            if (nrm.sqrMagnitude > 0.001f)
            {
                DrawNavballMarker(c, radius, qi, nrm, "N", PFViewPalette.TextSecondary);
                DrawNavballMarker(c, radius, qi, -nrm, "S", PFViewPalette.TextSecondary);
            }

            DVector3 thrust = spaceGame.GetMainThrustAxisSystem(g);
            if (thrust.sqrMagnitude > 1e-12)
                DrawNavballMarker(c, radius, qi, (Vector3)thrust.normalized, "T", PFViewPalette.Accent);

            // 左右文字区（左：姿态/主推轴；右：速度 + 记号图例）——球放大 1.5× 后放两侧
            if (withReadouts)
            {
                Vector3 euler = g.CurrentOrbit.rotation.ToQuaternion().eulerAngles;
                float x = title.x, w = title.width, y = title.yMax + 5f;
                MiniLabel(new(x, y, w, 16f), $"V {vel.magnitude:F0} m/s", PFViewPalette.TextPrimary);
                MiniLabel(new(x, y + 17f, w, 16f), $"Y {euler.y:F0}° · P {euler.x:F0}°", PFViewPalette.TextSecondary);
                MiniLabel(new(x, y + 34f, w, 16f), $"R {euler.z:F0}°", PFViewPalette.TextSecondary);
            }
            else if (!docked && r.width >= 500f) DrawNavballFlanks(r, c, radius, g, spaceGame, vel);
        }

        /// <summary>姿态球两侧文字读数（V-1.1）：左 = 姿态/角速度/主推轴；右 = 速度 + 记号图例。</summary>
        private void DrawNavballFlanks(Rect r, Vector2 c, float radius, SpaceEntityGroup g, SpaceGameComp spaceGame, Vector3 vel)
        {
            float sideW = (r.width - radius * 2f) * 0.5f;
            float lx = r.x + 8f;
            float rx = c.x + radius + 8f;
            const float lh = 16f;

            // 左：姿态 + 主推轴（与姿态球上的 T 记号同一份数据）
            float y = r.y + 10f;
            MiniLabel(new Rect(lx, y, sideW - 16f, lh), "姿态", PFViewPalette.TextSecondary);
            y += lh;
            Vector3 euler = g.CurrentOrbit.rotation.ToQuaternion().eulerAngles;
            MiniLabel(new Rect(lx, y, sideW - 16f, lh), $"yaw {euler.y:F0}° pitch {euler.x:F0}° roll {euler.z:F0}°", PFViewPalette.TextPrimary);
            y += lh;
            MiniLabel(new Rect(lx, y, sideW - 16f, lh), $"角速度 {g.angularVelocity.magnitude * Mathf.Rad2Deg:F1}°/s", PFViewPalette.TextPrimary);
            y += lh + 6f;

            MiniLabel(new Rect(lx, y, sideW - 16f, lh), "主推轴（T 记号）", PFViewPalette.TextSecondary);
            y += lh;
            DVector3 thrustAxis = spaceGame.GetMainThrustAxisSystem(g);
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
            string source = ResolveOperationGroup() is { } group ? "G" + group.GroupId : "—";
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
