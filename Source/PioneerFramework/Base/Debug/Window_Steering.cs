using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 手动操舵 HUD 窗口（KSP 式）：绑定固定键位控制飞船平移/旋转/动作组/姿态对准。
    /// 打开时读键（Input.GetKey 保持态 + Event.current 离散态），状态写 SteeringController，
    /// 由 SpaceGameComp 每物理步应用。ESC 关闭。
    /// </summary>
    public class Window_Steering : Window
    {
        public override Vector2 InitialSize => new Vector2(372f, 470f);

        private const float RowH = 24f;
        private static readonly Color bgColor = new Color(0.05f, 0.05f, 0.08f, 0.55f);

        public Window_Steering()
        {
            closeOnAccept = false;
            closeOnCancel = false;
            doCloseX = true;
            absorbInputAroundWindow = true;
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

            // 有效性：窗口开着但 group 失效 / 船上操舵台被拆 → 自动关闭
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var group = spaceGame != null ? spaceGame.GetGroupById(SteeringController.GroupId) : null;
            var curMap = Current.Game?.CurrentMap; // 同上：全库约定是 Current.Game?.
            if (group == null || !group.IsActive || curMap == null || !CompSteeringConsole.HasConsole(curMap))
            {
                SteeringController.Close();
                Close();
                return;
            }
            var mapComp = group.GetLinkedMapComp();
            if (mapComp == null)
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

            // ── 主推进方向 ──
            DrawSectionTitle(inRect, ref y, "主推进方向");
            DrawMainAxisSelector(inRect, ref y, mapComp);

            // ── 动作组 ──
            DrawSectionTitle(inRect, ref y, "动作组（1-9 切换武装，只影响手动指令）");
            DrawActionGroups(inRect, ref y, mapComp);

            // ── SAS ──
            DrawSectionTitle(inRect, ref y, "姿态对准 SAS（主推进轴 → 目标）");
            DrawSasButtons(inRect, ref y);

            // 键位提示
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inRect.x + 6f, inRect.yMax - 20f, inRect.width - 12f, 18f),
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
                string.Format("角速度: {0:F2} °/s  姿态(欧拉): ({1:F0}, {2:F0}, {3:F0})",
                    w * Mathf.Rad2Deg,
                    group.CurrentOrbit.rotation.eulerAngles.x,
                    group.CurrentOrbit.rotation.eulerAngles.y,
                    group.CurrentOrbit.rotation.eulerAngles.z));
            y += RowH - 2f;
        }

        private void DrawAxisThrusts(Rect inRect, ref float y, SpaceMapComp mapComp)
        {
            Text.Font = GameFont.Tiny;
            string[] names = { "F 前", "B 后", "L 左", "R 右", "U 上", "D 下" };
            for (int i = 0; i < 6; i++)
            {
                Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH),
                    string.Format("{0}: {1:F0} kN", names[i], mapComp.GetAxisTotalThrust(i) / 1000f));
                y += RowH - 4f;
            }
        }

        private void DrawRotationInfo(Rect inRect, ref float y, SpaceEntityGroup group)
        {
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, RowH),
                string.Format("动量轮限速: {0:F1} °/s", SpaceEntityGroup.DefaultMaxAngularRate * Mathf.Rad2Deg));
            y += RowH - 2f;
        }

        private void DrawMainAxisSelector(Rect inRect, ref float y, SpaceMapComp mapComp)
        {
            string[] names = { "自动", "F 前", "B 后", "L 左", "R 右", "U 上", "D 下" };
            float bw = 44f, gap = 6f;
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
            float bw = 32f, gap = 5f;
            for (int g = 0; g < 9; g++)
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
            string[] names = { "关", "顺向", "逆向", "法向", "反法向", "径外", "径内" };
            float bw = 46f, gap = 6f;
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

        private SpaceMapComp GetCurrentMapComp()
        {
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            var group = spaceGame != null ? spaceGame.GetGroupById(SteeringController.GroupId) : null;
            return group != null ? group.GetLinkedMapComp() : null;
        }
    }
}
