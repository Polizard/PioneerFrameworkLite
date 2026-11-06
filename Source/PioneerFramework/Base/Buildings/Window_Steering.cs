using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 手动操舵详细窗口：通过共享输入适配器控制飞船平移/旋转/动作组/姿态对准。
    /// 打开时发布可配置按键与指针保持态，状态写 SteeringController，
    /// 由 SpaceGameComp 每物理步应用。ESC 关闭。
    ///
    /// 两个打开入口（同一条路：`SteeringController.Open` + 本窗口）：
    ///   · 船上操舵台的 Gizmo（`CompSteeringConsole`，玩家在船图里）；
    ///   · PFView 左栏"手动操舵"开关（2026-10-02 用户裁定：按下进入、再按解除）。
    ///
    /// 目录说明（2026-09-14 D1）：本文件原在 `Base/Debug/`，但它是**玩家可达的正式 UI**
    /// （由 `CompSteeringConsole` 的 Gizmo 打开，非 dev-only），放在 Debug 目录会误导
    /// （让人以为"仅开发者可见"而低估其开销与回归面）。现与开它的 Comp 同目录——
    /// 同惯例：`Window_VolleyCountSlider` 就在 `CompMissileLauncher.cs` 内。
    /// </summary>
    public class Window_Steering : Window
    {
        public override Vector2 InitialSize => new(Mathf.Min(540f, UI.screenWidth - 32f), Mathf.Min(650f, UI.screenHeight - 32f));

        private const float RowH = 24f;
        /// <summary>键位提示固定画在**内容之后**（旧版锚在 yMax−20，内容一长就压住 SAS 按钮）。</summary>
        private static readonly Color bgColor = new(0.05f, 0.05f, 0.08f, 0.55f);
        private readonly bool ownsControl;
        private Vector2 detailsScroll;
        private float detailsHeight = 820f;

        public Window_Steering(bool absorbAroundWindow = true) : this(absorbAroundWindow, true) { }

        internal Window_Steering(bool absorbAroundWindow, bool ownsControl)
        {
            this.ownsControl = ownsControl;
            closeOnAccept = false;
            closeOnCancel = false;
            doCloseX = true;
            // true（默认，船上操舵台那条路）= 窗口以外的点击也吃掉，操舵时不误点地图下命令；
            // false（PFView 那条路）= 鼠标留给 3D 相机（旋转/平移/缩放）。
            absorbInputAroundWindow = absorbAroundWindow;
            forcePause = false;          // 操舵时游戏保持运行（物理推进/转动）
            preventCameraMotion = true;
            draggable = true;
            resizeable = false;
        }

        public override void PostClose()
        {
            base.PostClose();
            if (ownsControl) StopControl();
            else ClearHeldKeys();
            SteeringInputHost.Rearm();
        }

        internal static void StopControl()
        {
            var mapComp = SteeringController.IsOpen ? GetCurrentMapComp() : null;
            SteeringController.Close();
            mapComp?.SetThrustersBurning(false);
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            SteeringInputHost.UpdatePointer(Input.GetMouseButton(0), CanReadKeyboard());
        }

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.DrawBoxSolid(inRect, PFViewPalette.PanelBg);
            if (!SteeringController.IsOpen) { Close(); return; }

            // 有效性：窗口开着但 group 失效 / 船上操舵台被拆 → 自动关闭。
            // 准入判据 = **船自己的图**上有完好操舵台（2026-10-02 起 PFView 左栏也能操舵，
            // 玩家不必人在船上；旧判据看的是"玩家当前所在图"，在 PFView 里必然为空 ⇒ 窗口一开就自关）。
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var group = spaceGame != null ? spaceGame.GetGroupById(SteeringController.GroupId) : null;
            var mapComp = group?.GetLinkedMapComp();
            if (group == null || !group.IsActive || mapComp == null || !CompSteeringConsole.HasConsole(mapComp.map))
            {
                Close();
                return;
            }

            // ── 读键：平移/旋转保持态（每帧刷新）；动作组/ESC 离散态 ──
            SteeringInputHost.UpdatePointer(Input.GetMouseButton(0), CanReadKeyboard());
            ReadKeys();
            if (!SteeringController.IsOpen) return;

            var content = new Rect(0, 0, inRect.width - 18f, detailsHeight);
            Widgets.BeginScrollView(inRect, ref detailsScroll, content);
            inRect = content;

            float y = 4f;
            Text.Font = GameFont.Small;

            // ── 姿态区 ──
            DrawSectionTitle(inRect, ref y, "PFV_Pilot_Details".Translate());
            DrawAttitudeInfo(inRect, ref y, group);
            DrawHint(inRect, ref y, DrivingHelp());
            DrawHint(inRect, ref y, TranslationHelp());
            DrawPointerButtons(inRect, ref y);

            // ── 平移区 ──
            DrawSectionTitle(inRect, ref y, "PFV_Pilot_AvailableThrust".Translate());
            DrawAxisThrusts(inRect, ref y, mapComp);

            // ── 旋转区 ──
            DrawRotationInfo(inRect, ref y, group);

            // ── 惯量区（批 0 底座：读数；不参与任何控制——限速 I 化在批 1）──
            DrawSectionTitle(inRect, ref y, "惯量（批 0：绕质心主轴 / 质心偏移 / 绕原点）");
            DrawInertiaInfo(inRect, ref y, group);

            // ── 主推进方向 ──
            DrawSectionTitle(inRect, ref y, "PFV_Pilot_Axis".Translate());
            DrawMainAxisSelector(inRect, ref y, mapComp);

            // ── 动作组 ──
            DrawSectionTitle(inRect, ref y, "PFV_Pilot_Groups".Translate());
            DrawActionGroups(inRect, ref y, mapComp);

            // ── SAS ──
            DrawSectionTitle(inRect, ref y, "PFV_Pilot_SasTitle".Translate());
            DrawSasButtons(inRect, ref y);
            if (SteeringController.ThrustUnavailable) DrawHint(inRect, ref y, "PFV_Pilot_NoThrust".Translate());
            DrawHint(inRect, ref y, (ownsControl ? "PFV_Pilot_ExitHint" : "PFV_Pilot_PanelHint").Translate());
            detailsHeight = y + 12f;
            Text.Font = GameFont.Small;
            Widgets.EndScrollView();
        }

        internal static string DrivingHelp() => "PFV_Pilot_DriveKeys".Translate(
            SteeringInputHost.Label(SteeringAction.PitchUp), SteeringInputHost.Label(SteeringAction.PitchDown),
            SteeringInputHost.Label(SteeringAction.YawLeft), SteeringInputHost.Label(SteeringAction.YawRight),
            SteeringInputHost.Label(SteeringAction.RollLeft), SteeringInputHost.Label(SteeringAction.RollRight),
            PrecisionHelp());

        internal static string PrecisionHelp()
        {
            bool toggle = SteeringController.IsOpen ? SteeringController.PrecisionToggle
                : PioneerFrameworkLiteMod.Settings?.pfviewPrecisionToggle ?? false;
            return (toggle ? "PFV_Pilot_PrecisionToggleHelp" : "PFV_Pilot_PrecisionHoldHelp")
                .Translate(SteeringInputHost.Label(SteeringAction.Precision));
        }

        internal static string PrecisionStatus() => SteeringController.PrecisionHeld
            ? "PFV_Pilot_PrecisionOn".Translate((SteeringController.PrecisionScale * 100).ToString("F0"))
            : "PFV_Pilot_PrecisionOff".Translate();

        internal static string TranslationHelp() => "PFV_Pilot_TranslateKeys".Translate(
            SteeringInputHost.Label(SteeringAction.Forward), SteeringInputHost.Label(SteeringAction.Back),
            SteeringInputHost.Label(SteeringAction.Left), SteeringInputHost.Label(SteeringAction.Right),
            SteeringInputHost.Label(SteeringAction.Up), SteeringInputHost.Label(SteeringAction.Down));

        internal static string SasLabel(SasTargetKind kind) => ("PFV_Pilot_Sas_" + kind).Translate();

        private static void DrawHint(Rect rect, ref float y, string text)
        {
            Text.Font = GameFont.Small;
            float height = Text.CalcHeight(text, rect.width - 16f);
            Widgets.Label(new(rect.x + 8, y, rect.width - 16, height), text);
            y += height + 6;
        }

        private void DrawPointerButtons(Rect rect, ref float y)
        {
            float width = (rect.width - 24f) / 4;
            Text.Font = GameFont.Tiny;
            for (int i = 0; i < 12; i++)
            {
                var action = (SteeringAction)i;
                var button = new Rect(rect.x + 6 + i % 4 * (width + 4), y + i / 4 * 38, width, 34);
                var e = Event.current;
                if (GUI.enabled && e.type == EventType.MouseDown && e.button == 0 && button.Contains(e.mousePosition))
                    SteeringInputHost.BeginPointer(action);
                Widgets.ButtonText(button, PFViewBindingUtility.ActionLabel(action) + "\n" + SteeringInputHost.Label(action));
            }
            SteeringInputHost.PublishPointer();
            y += 118;
            Text.Font = GameFont.Small;
        }

        private void DrawSectionTitle(Rect inRect, ref float y, string title)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x + 6f, y, inRect.width - 12f, RowH), title);
            Text.Font = GameFont.Small;
            y += RowH - 4f;
        }

        private void DrawAttitudeInfo(Rect inRect, ref float y, SpaceEntityGroup group)
        {
            Text.Font = GameFont.Tiny;
            float w = (float)group.angularVelocity.magnitude;
            string status = PrecisionStatus() + " · " + "PFV_Pilot_Spin".Translate((w * Mathf.Rad2Deg).ToString("F2")) + " · "
                + (SteeringController.AssistedStop ? "PFV_Pilot_Assisted" : "PFV_Pilot_Inertial").Translate()
                + " · " + SasLabel(SteeringController.SasKind);
            float height = Mathf.Max(RowH - 2f, Text.CalcHeight(status, inRect.width - 20f));
            Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, height), status);
            y += height;
        }

        private void DrawAxisThrusts(Rect inRect, ref float y, SpaceMapComp mapComp)
        {
            Text.Font = GameFont.Tiny;
            string[] names = AxisLabels();
            for (int i = 0; i < 6; i++)
            {
                Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH),
                    $"{names[i]}: {mapComp.GetAxisTotalThrust(i) / 1000f:F0} kN");
                y += RowH - 4f;
            }
        }

        private void DrawRotationInfo(Rect inRect, ref float y, SpaceEntityGroup group)
        {
            Text.Font = GameFont.Tiny;
            float slow = group.MaxAngularRate * Mathf.Rad2Deg;
            if (group.maxAngularRate > 0f)
            {
                Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH),
                    "PFV_Pilot_RateLimit".Translate(slow.ToString("F1")));
                y += RowH - 2f;
                return;
            }
            // 批 1：I 加权——最慢轴 + 三根手操轴各自的上限（大船可能全压在地板上）
            float roll = group.MaxAngularRateAboutBodyAxis(0, 0, 1) * Mathf.Rad2Deg;
            float yaw = group.MaxAngularRateAboutBodyAxis(0, 1, 0) * Mathf.Rad2Deg;
            float pitch = group.MaxAngularRateAboutBodyAxis(1, 0, 0) * Mathf.Rad2Deg;
            Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH),
                "PFV_Pilot_RateLimit".Translate(slow.ToString("F1")));
            y += RowH - 4f;
            Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH),
                "PFV_Pilot_RateAxes".Translate(
                    roll.ToString("F1"), yaw.ToString("F1"), pitch.ToString("F1")));
            y += RowH - 2f;
        }

        /// <summary>惯量读数（批 0：只显示，不控制；重算# = 事件惰性自证——静止时不涨）。</summary>
        private void DrawInertiaInfo(Rect inRect, ref float y, SpaceEntityGroup group)
        {
            Text.Font = GameFont.Tiny;
            group.InertiaDebugLines(out string line1, out string line2, out string line3);
            Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH), line1);
            y += RowH - 4f;
            if (!string.IsNullOrEmpty(line2))
            {
                Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH), line2);
                y += RowH - 4f;
            }
            if (!string.IsNullOrEmpty(line3))
            {
                Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH), line3);
                y += RowH - 2f;
            }
        }

        private void DrawMainAxisSelector(Rect inRect, ref float y, SpaceMapComp mapComp)
        {
            var axes = AxisLabels();
            string[] names = ["PFV_Pilot_Axis_Auto".Translate(), axes[0], axes[1], axes[2], axes[3], axes[4], axes[5]];
            float gap = 4f, bw = (inRect.width - 12f - gap * 6) / 7;
            for (int i = 0; i < names.Length; i++)
            {
                int sel = mapComp.mainThrustAxisIndex + 1; // 0=自动, 1..6=六向
                bool isSel = sel == i;
                var rect = new Rect(inRect.x + 6f + i * (bw + gap), y, bw, RowH - 4f);
                var prev = GUI.color;
                GUI.color = isSel ? PFViewPalette.Accent : PFViewPalette.TextSecondary;
                if (Widgets.ButtonText(rect, names[i]))
                {
                    mapComp.mainThrustAxisIndex = i - 1; // 自动=-1，六向 0..5
                }
                GUI.color = prev;
            }
            y += RowH + 2f;
        }

        private void DrawActionGroups(Rect inRect, ref float y, SpaceMapComp mapComp)
        {
            Text.Font = GameFont.Tiny;
            const float gap = 5f;
            const int count = 9;
            float bw = (inRect.width - 12f - gap * (count - 1)) / count;   // 自适应
            for (int g = 0; g < count; g++)
            {
                bool armed = (mapComp.armedGroupsMask & (1 << g)) != 0;
                var rect = new Rect(inRect.x + 6f + g * (bw + gap), y, bw, RowH - 4f);
                var prev = GUI.color;
                GUI.color = armed ? PFViewPalette.Accent : PFViewPalette.TextSecondary;
                if (Widgets.ButtonText(rect, (g + 1).ToString()))
                {
                    mapComp.armedGroupsMask ^= (1 << g);
                }
                GUI.color = prev;
                TooltipHandler.TipRegion(rect, SteeringInputHost.Label(SteeringAction.Group1 + g));
            }
            y += RowH + 2f;
        }

        private void DrawSasButtons(Rect inRect, ref float y)
        {
            float gap = 4f, bw = (inRect.width - 12f - gap * 6) / 7;
            for (int i = 0; i < 7; i++)
            {
                bool isSel = (int)SteeringController.SasKind == i;
                var rect = new Rect(inRect.x + 6f + i * (bw + gap), y, bw, RowH - 4f);
                var prev = GUI.color;
                GUI.color = isSel ? PFViewPalette.Accent : PFViewPalette.TextSecondary;
                if (Widgets.ButtonText(rect, SasLabel((SasTargetKind)i)))
                    SteeringController.SasKind = (SasTargetKind)i;
                GUI.color = prev;
            }
            y += RowH + 4;
        }

        private static string[] AxisLabels() => ["PFV_Pilot_Axis_Forward".Translate(), "PFV_Pilot_Axis_Back".Translate(),
            "PFV_Pilot_Axis_Left".Translate(), "PFV_Pilot_Axis_Right".Translate(),
            "PFV_Pilot_Axis_Up".Translate(), "PFV_Pilot_Axis_Down".Translate()];

        private void ReadKeys()
        {
            if (!CanReadKeyboard())
            {
                ClearHeldKeys();
                SteeringInputHost.Poll(false);
                return;
            }
            SteeringInputHost.Poll(true);

            // 离散态
            if (Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Escape)
                {
                    Close();
                    Event.current.Use();
                    return;
                }
                if (SteeringInputHost.HandleGroupShortcut(Event.current)) return;
                var view = Find.WindowStack?.WindowOfType<MainTabWindow_PFView>();
                if (view == null || view.HandlePilotCameraShortcut(Event.current)) return;
                MainTabWindow_PFView.TryHandlePauseShortcut(Event.current);
            }
        }

        private bool CanReadKeyboard()
        {
            if (!Application.isFocused) return false;
            var stack = Find.WindowStack;
            var view = stack?.WindowOfType<MainTabWindow_PFView>();
            if (view?.HasTextInputFocus == true) return false;
            var foreground = SteeringInputHost.KeyboardForeground(stack?.Windows);
            if (foreground == null) return false;
            return ReferenceEquals(foreground, this) || (view != null && ReferenceEquals(foreground, view));
        }

        internal static void ClearHeldKeys()
        {
            // 文本输入或上层弹窗仅暂停手动按键，保留操舵对象与 SAS。
            SteeringController.ClearHeldInput();
        }

        private static SpaceMapComp? GetCurrentMapComp()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var group = spaceGame != null ? spaceGame.GetGroupById(SteeringController.GroupId) : null;
            return group != null ? group.GetLinkedMapComp() : null;
        }
    }
}
