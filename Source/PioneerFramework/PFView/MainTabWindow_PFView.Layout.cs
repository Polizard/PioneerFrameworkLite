using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 布局与仪表（V-1，2026-10-02 用户裁定①）：
    ///   顶部工具条 / 左侧文字堆（编队列表 + 接触列表 + 选中组读数）/ 下边中央姿态球（KSP 式）/
    ///   右侧雷达（以选中编队为中心，点击观测接触可设为武器目标）。
    ///
    /// 视觉纪律（裁定③）：全套灰度走 <see cref="PFViewPalette"/>——层级靠明度与线宽、状态靠符号，
    /// 不用色相。面板/角标装饰复用 <c>UiKit.SpacecraftUIDecor</c>（同一份程序化画笔）。
    ///
    /// 数据准入（红线）：列表与雷达**只读认知层**（`PlayerCanSee` / `SensorManager.AppendVisible`），
    /// 我方可读取自身遥测；其他实体不得读取隐藏真值。dev 真值对照仍走 `DebugShowGroundTruth` 开关。
    /// </summary>
    public partial class MainTabWindow_PFView : MainTabWindow
    {
        // ── V-1 布局常量 ──
        private const float ToolbarH = 30f;
        private const float LeftColW = 252f;
        private const float RadarW = 260f;
        private const float NavballD = 222f;        // 球直径（V-1.1：用户 2026-10-02 要求放大到 1.5×）
        private const float NavballPanelW = 522f;   // 面板宽 = 左文字 150 + 球 222 + 右文字 150
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
        private (float width, float height, bool details, PFViewUiVisibility visibility, bool piloting) layoutKey;
        private bool layoutCached;
        private Rect viewportRect;

        // 列表缓冲（追加式，免每帧分配；[M3] 硬规则：不用 IEnumerable）
        private readonly List<Contact> contactBuf = new();
        private readonly List<SpaceEntityGroup> groupListBuf = new();

        // 姿态球圆盘贴图（程序化生成一次；RimWorld 纪律：不用外部贴图资源）
        private static Texture2D? pfvDiscTex;

        // 中键/Shift+右键平移拖拽中
        private bool isDraggingPan => dragState.Pan;

        /// <summary>算出本帧五分区并登记占位矩形（挡 3D 输入）。</summary>
        private void ComputeLayout(Rect inRect)
        {
            viewportRect = inRect;
            var key = (inRect.width, inRect.height, WantsInspector, instrumentVisibility, SteeringController.IsOpen);
            if (!layoutCached || key != layoutKey)
            {
                uiLayout = PFViewUiLayoutCore.Compute(key.width, key.height, key.Item3, instrumentVisibility, key.Item5);
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
            RegisterUiRect(rToolbar);
            RegisterUiRect(rLeftCol);
            RegisterUiRect(rInspector);
            RegisterUiRect(rRadar);
            RegisterUiRect(rNavball);
            if (rNavball.width > 0) RegisterUiRect(new(rNavball.x, rNavball.y - 24f, rNavball.width, 24f));
            RegisterUiRect(rStatus);
            RegisterUiRect(rSteeringStrip);
            RegisterUiRect(rPilotPanel);
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
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Group), "PFV_SelectGroup".Translate(), "PFV_SelectGroupTip".Translate()))
                ShowGroupMenu();
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
            DrawTimeSpeedButtons(time.x, time.y, time.height);
            var source = ResolveOperationGroup();
            if (source != null && ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Weapons),
                    "PFV_Weapons".Translate("G" + source.GroupId), "PFV_OpenOperationsTip".Translate()))
                TryOpenWeaponsFor(source.GroupId);
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Help), "?", "PFV_Help".Translate()))
                Find.WindowStack.Add(new Window_PFViewHelp());
            if (ToolbarButton(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Debug), "PFV_Debug".Translate(), "PFV_DebugTip".Translate()))
                Window_PFViewDebug.Toggle(this);
            if (uiLayout.RequiresInstrumentDrawer)
            {
                if (instrumentVisibility.ShowNavball && Widgets.ButtonText(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Navball), "PFV_Navball".Translate()))
                    OpenInstrumentWindow(PFViewUiInstrument.Navball);
                if (instrumentVisibility.ShowRadar && Widgets.ButtonText(NextToolbarRect(ref x, ref by, PFViewToolbarItem.Radar), "PFV_Radar".Translate()))
                    OpenInstrumentWindow(PFViewUiInstrument.Radar);
            }
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
                new((debugSettlements ? "✓ " : "   ") + "定居点（含夜灯）", () => debugSettlements = !debugSettlements),
                new("PFV_Navball".Translate() + " ▸", () => ShowInstrumentModeMenu(PFViewUiInstrument.Navball)),
                new("PFV_Radar".Translate() + " ▸", () => ShowInstrumentModeMenu(PFViewUiInstrument.Radar)),
                new("PFV_NavballReadouts".Translate(), () => OpenInstrumentWindow(PFViewUiInstrument.Navball)),
            };
            Find.WindowStack.Add(new FloatMenu(opts));
        }


        /// <summary>
        /// PFView 内的游戏时间速度控制（2026-10-18 用户：视图内仍要能控制时间流逝）。
        /// 宿主 HUD 的时间档按钮被 PFView 的 HUD 隔离补丁挡住（视觉上也被全屏视图盖住），
        /// 按钮通过时间服务设置宿主速度，档位/倍率与原版一致，右侧显示空间时间与精度限制。
        /// </summary>
        private void DrawTimeSpeedButtons(float x, float by, float bh)
        {
            const float w = 30f;
            const float gap = 2f;
            DrawTimeSpeedButton(new Rect(x, by, w, bh), "II", TimeSpeed.Paused, "暂停");
            x += w + gap;
            DrawTimeSpeedButton(new Rect(x, by, w, bh), "1×", TimeSpeed.Normal, "正常速度（1×）");
            x += w + gap;
            DrawTimeSpeedButton(new Rect(x, by, w, bh), "3×", TimeSpeed.Fast, "快速（3×）");
            x += w + gap;
            DrawTimeSpeedButton(new Rect(x, by, w, bh), "6×", TimeSpeed.Superfast, "超快（6×；无地图时可到 12/18×）");
            x += w + gap;
            DrawTimeSpeedButton(new Rect(x, by, w, bh), "15×", TimeSpeed.Ultrafast, "极速（15×；无地图时可到 150×）");
            var clock = Current.Game?.GetComponent<TimeScaleManager>();
            if (clock != null)
            {
                var status = new Rect(x + w + gap + 4f, by, 164f, bh);
                TooltipHandler.TipRegion(status, clock.StatusTip);
                Widgets.Label(status, clock.StatusLabel);
            }
        }

        private static void DrawTimeSpeedButton(Rect rr, string label, TimeSpeed speed, string tip)
        {
            TooltipHandler.TipRegion(rr, tip);
            bool active = Find.TickManager.CurTimeSpeed == speed;
            Color prev = GUI.color;
            GUI.color = active ? Color.white : new Color(0.62f, 0.62f, 0.62f);
            if (Widgets.ButtonText(rr, label))
                SetGameSpeed(speed);
            GUI.color = prev;
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
        //  右侧雷达（只显示：以选中编队为中心，不做交互）
        // ════════════════════════════════════════════════════

        private void DrawRadarPanel(SpaceEntityGroup? g)
        {
            var r = rRadar;
            Widgets.DrawBoxSolid(r, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(r, PFViewPalette.Border);

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
            MiniLabel(new Rect(c.x - 24f, c.y - radius - 14f, 48f, 12f), "顺行", PFViewPalette.TextMuted, TextAnchor.MiddleCenter);
            MiniLabel(new Rect(c.x + radius + 2f, c.y - 8f, 40f, 12f), "径外向", PFViewPalette.TextMuted, TextAnchor.MiddleLeft);

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

            bool pick = Event.current.type == EventType.MouseDown && Event.current.button == 0 && r.Contains(Event.current.mousePosition);
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
                    ContactDisplayLabel(cc) + "\n" + "PFV_RadarTargetHint".Translate());
                if (weaponTarget.IsSet && weaponTarget.GroupId == cc.targetGroupId && weaponTarget.DecoyId == cc.decoyId)
                {
                    Widgets.DrawBox(new Rect(p.x - 6, p.y - 6, 12, 12));
                    MiniLabel(new Rect(p.x + 9, p.y - 10, 110, 20), ContactDisplayLabel(cc), PFViewPalette.TextPrimary);
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
            var title = new Rect(r.x, r.y - 24f, r.width, 24f);
            Widgets.DrawBoxSolid(title, PFViewPalette.PanelBg);
            Widgets.Label(title, "PFV_NavballSource".Translate(g != null ? "G" + g.GroupId : "—"));
            Widgets.DrawBoxSolid(r, PFViewPalette.BarBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(r, PFViewPalette.Border);

            float radius = Mathf.Min(r.width, r.height) * 0.5f - 12f;
            Vector2 c = r.center;

            EnsureDiscTexture();
            if (pfvDiscTex == null) return;   // 贴图生成失败：不画球（防御，正常不会发生）
            GUI.color = PFViewPalette.PanelBg;
            GUI.DrawTexture(new Rect(c.x - radius, c.y - radius, radius * 2f, radius * 2f), pfvDiscTex);
            GUI.color = Color.white;

            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            if (g == null || spaceGame == null)
            {
                MiniLabel(new Rect(r.x, c.y - 8f, r.width, 16f), "PFV_NoSelection".Translate(), PFViewPalette.TextSecondary, TextAnchor.MiddleCenter);
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
            if (r.width >= 500f) DrawNavballFlanks(r, c, radius, g, spaceGame, vel);
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
            Widgets.DrawBoxSolid(new Rect(p.x - 2.5f, p.y - 2.5f, 5f, 5f), col);
            UiKit.SpacecraftUIDecor.DrawColoredLabel(new Rect(p.x + 4f, p.y - 8f, 22f, 14f), label,
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
            string target = targetGroupId >= 0 ? "G" + targetGroupId : "—";
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
