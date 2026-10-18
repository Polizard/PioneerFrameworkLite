using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 手动操舵 HUD 窗口（KSP 式）：绑定固定键位控制飞船平移/旋转/动作组/姿态对准。
    /// 打开时读键（Input.GetKey 保持态 + Event.current 离散态），状态写 SteeringController，
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
        // 内容高度预算（2026-10-17 用户报障"UI 太短够不着按钮"）：
        // 绘制顺序累加 ≈ 466px（姿态 42 + 平移 140 + 旋转 62 + 惯量 82 + 主推 46 + 动作组 46 + SAS 44 + 起始 4）
        // + 键位提示 24 ≈ 490；Window 的 DoWindowContents 拿到的是 **windowRect 收缩 18px 边框**后的矩形
        // ⇒ 窗口高度必须 ≥ 490 + 36 ≈ 526。取 560 留余量（新增一行读数不会再次裁掉底部按钮）。
        // 宽度：主推轴/动作组/SAS 三排按钮最多 7 个（带间距 ≈ 370px）⇒ 取 420（内容 384）并让按钮宽度自适应，
        // 不再出现"最右一个按钮被窗口右边缘裁掉"（旧 372 宽时 SAS 的最后一枚要 364px、实际只有 336px）。
        public override Vector2 InitialSize => new(420f, 560f);

        private const float RowH = 24f;
        /// <summary>键位提示固定画在**内容之后**（旧版锚在 yMax−20，内容一长就压住 SAS 按钮）。</summary>
        private static readonly Color bgColor = new(0.05f, 0.05f, 0.08f, 0.55f);

        public Window_Steering(bool absorbAroundWindow = true)
        {
            closeOnAccept = false;
            closeOnCancel = false;
            doCloseX = true;
            // true（默认，船上操舵台那条路）= 窗口以外的点击也吃掉，操舵时不误点地图下命令；
            // false（PFView 那条路）= 鼠标留给 3D 相机（旋转/平移/缩放）。
            absorbInputAroundWindow = absorbAroundWindow;
            forcePause = false;          // 操舵时游戏保持运行（物理推进/转动）
            preventCameraMotion = false;
            draggable = true;
            resizeable = false;
        }

        public override void PostClose()
        {
            base.PostClose();
            SteeringController.Close();
            GetCurrentMapComp()?.SetThrustersBurning(false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.DrawBoxSolid(inRect, bgColor);

            // 有效性：窗口开着但 group 失效 / 船上操舵台被拆 → 自动关闭。
            // 准入判据 = **船自己的图**上有完好操舵台（2026-10-02 起 PFView 左栏也能操舵，
            // 玩家不必人在船上；旧判据看的是"玩家当前所在图"，在 PFView 里必然为空 ⇒ 窗口一开就自关）。
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var group = spaceGame != null ? spaceGame.GetGroupById(SteeringController.GroupId) : null;
            var mapComp = group?.GetLinkedMapComp();
            if (group == null || !group.IsActive || mapComp == null || !CompSteeringConsole.HasConsole(mapComp.map))
            {
                SteeringController.Close();
                Close();
                return;
            }

            // ── 读键：平移/旋转保持态（每帧刷新）；动作组/ESC 离散态 ──
            ReadKeys();

            float y = 4f;
            Text.Font = GameFont.Small;

            // ── 姿态区 ──
            DrawSectionTitle(inRect, ref y, "姿态");
            DrawAttitudeInfo(inRect, ref y, group);

            // ── 平移区 ──
            DrawSectionTitle(inRect, ref y, "平移（W/S/A/D + R/F，按住持续）");
            DrawAxisThrusts(inRect, ref y, mapComp);

            // ── 旋转区 ──
            DrawSectionTitle(inRect, ref y, "旋转（Q/E 滚转 · 方向键 偏航/俯仰）");
            DrawRotationInfo(inRect, ref y, group);

            // ── 惯量区（批 0 底座：读数；不参与任何控制——限速 I 化在批 1）──
            DrawSectionTitle(inRect, ref y, "惯量（批 0：绕质心主轴 / 质心偏移 / 绕原点）");
            DrawInertiaInfo(inRect, ref y, group);

            // ── 主推进方向 ──
            DrawSectionTitle(inRect, ref y, "主推进方向");
            DrawMainAxisSelector(inRect, ref y, mapComp);

            // ── 动作组 ──
            DrawSectionTitle(inRect, ref y, "动作组（1-9 切换武装，只影响手动指令）");
            DrawActionGroups(inRect, ref y, mapComp);

            // ── SAS ──
            DrawSectionTitle(inRect, ref y, "姿态对准 SAS（主推进轴 → 目标）");
            DrawSasButtons(inRect, ref y);

            // 键位提示（内容之后；不再锚在 yMax ⇒ 不会被新增读数压住）
            Text.Font = GameFont.Tiny;
            y += 6f;
            Widgets.Label(new Rect(inRect.x + 6f, y, inRect.width - 12f, 18f),
                "ESC 关闭 · 任意操舵输入会清空机动队列（手动接管）");
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
            Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH),
                $"角速度: {w * Mathf.Rad2Deg:F2} °/s  姿态(欧拉): ({group.CurrentOrbit.rotation.ToQuaternion().eulerAngles.x:F0}, {group.CurrentOrbit.rotation.ToQuaternion().eulerAngles.y:F0}, {group.CurrentOrbit.rotation.ToQuaternion().eulerAngles.z:F0})");
            y += RowH - 2f;
        }

        private void DrawAxisThrusts(Rect inRect, ref float y, SpaceMapComp mapComp)
        {
            Text.Font = GameFont.Tiny;
            string[] names = ["F 前", "B 后", "L 左", "R 右", "U 上", "D 下"];
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
                    $"动量轮限速: {slow:F1} °/s（def 覆盖，各轴同限）");
                y += RowH - 2f;
                return;
            }
            // 批 1：I 加权——最慢轴 + 三根手操轴各自的上限（大船可能全压在地板上）
            float roll = group.MaxAngularRateAboutBodyAxis(0, 0, 1) * Mathf.Rad2Deg;
            float yaw = group.MaxAngularRateAboutBodyAxis(0, 1, 0) * Mathf.Rad2Deg;
            float pitch = group.MaxAngularRateAboutBodyAxis(1, 0, 0) * Mathf.Rad2Deg;
            Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH),
                $"动量轮限速（I 加权）: 最慢轴 {slow:F1} °/s");
            y += RowH - 4f;
            Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH),
                $"滚 {roll:F1} · 偏 {yaw:F1} · 俯 {pitch:F1} °/s");
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
            string[] names = ["自动", "F 前", "B 后", "L 左", "R 右", "U 上", "D 下"];
            const float gap = 6f;
            float bw = (inRect.width - 12f - gap * (names.Length - 1)) / names.Length;   // 自适应：最右一枚不出界
            for (int i = 0; i < names.Length; i++)
            {
                int sel = mapComp.mainThrustAxisIndex + 1; // 0=自动, 1..6=六向
                bool isSel = sel == i;
                var rect = new Rect(inRect.x + 6f + i * (bw + gap), y, bw, RowH - 4f);
                var prev = GUI.color;
                GUI.color = isSel ? new Color(0.2f, 0.9f, 0.4f) : prev;
                if (Widgets.ButtonText(rect, names[i]))
                {
                    mapComp.mainThrustAxisIndex = i - 1; // 自动=-1，六向 0..5
                    if (i > 0)
                        Messages.Message("主推进方向 → " + names[i], MessageTypeDefOf.NeutralEvent);
                    else
                        Messages.Message("主推进方向 → 自动（推力最大轴向）", MessageTypeDefOf.NeutralEvent);
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
                GUI.color = armed ? new Color(0.2f, 0.9f, 0.4f) : new Color(0.4f, 0.4f, 0.4f, 0.9f);
                if (Widgets.ButtonText(rect, (g + 1).ToString()))
                {
                    mapComp.armedGroupsMask ^= (1 << g);
                    Messages.Message("动作组 " + (g + 1) + (armed ? " 解除武装" : " 武装"), MessageTypeDefOf.NeutralEvent);
                }
                GUI.color = prev;
            }
            y += RowH + 2f;
        }

        private void DrawSasButtons(Rect inRect, ref float y)
        {
            string[] names = ["关", "顺向", "逆向", "法向", "反法向", "径外", "径内"];
            const float gap = 6f;
            float bw = (inRect.width - 12f - gap * (names.Length - 1)) / names.Length;   // 自适应
            for (int i = 0; i < names.Length; i++)
            {
                bool isSel = (int)SteeringController.SasKind == i;
                var rect = new Rect(inRect.x + 6f + i * (bw + gap), y, bw, RowH - 4f);
                var prev = GUI.color;
                GUI.color = isSel ? new Color(0.2f, 0.9f, 0.4f) : prev;
                if (Widgets.ButtonText(rect, names[i]))
                    SteeringController.SasKind = (SasTargetKind)i;
                GUI.color = prev;
            }
        }

        private void ReadKeys()
        {
            // 平移（保持态）
            SteeringController.KeyForward = Input.GetKey(KeyCode.W);
            SteeringController.KeyBack = Input.GetKey(KeyCode.S);
            SteeringController.KeyLeft = Input.GetKey(KeyCode.A);
            SteeringController.KeyRight = Input.GetKey(KeyCode.D);
            SteeringController.KeyUp = Input.GetKey(KeyCode.R);
            SteeringController.KeyDown = Input.GetKey(KeyCode.F);
            // 旋转（保持态）
            SteeringController.KeyRollL = Input.GetKey(KeyCode.Q);
            SteeringController.KeyRollR = Input.GetKey(KeyCode.E);
            SteeringController.KeyYawL = Input.GetKey(KeyCode.LeftArrow);
            SteeringController.KeyYawR = Input.GetKey(KeyCode.RightArrow);
            SteeringController.KeyPitchU = Input.GetKey(KeyCode.UpArrow);
            SteeringController.KeyPitchD = Input.GetKey(KeyCode.DownArrow);

            // 离散态
            if (Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Escape)
                {
                    Close();
                    Event.current.Use();
                    return;
                }
                for (int g = 0; g < 9; g++)
                {
                    if (Event.current.keyCode == KeyCode.Alpha1 + g)
                    {
                        var mapComp = GetCurrentMapComp();
                        if (mapComp != null)
                            mapComp.armedGroupsMask ^= (1 << g);
                        Event.current.Use();
                        break;
                    }
                }
            }
        }

        private SpaceMapComp? GetCurrentMapComp()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var group = spaceGame != null ? spaceGame.GetGroupById(SteeringController.GroupId) : null;
            return group != null ? group.GetLinkedMapComp() : null;
        }
    }
}
