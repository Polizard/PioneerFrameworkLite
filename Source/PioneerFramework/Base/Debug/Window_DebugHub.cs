using System;
using UnityEngine;
using Verse;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    /// <summary>
    /// PF 统一调试面板（2026-09-06）：收编 CompDebugShipSpawner 原有的全部单场景 Gizmo，
    /// 按玩法/系统分类为按钮列表 + 日志开关。主 Comp 仅保留「打开 PF 调试面板」入口。
    /// 保留可互相打开的功能面板：Window_BoardingTest / Window_LaunchTest（导弹面板 m3+ 接入）。
    /// </summary>
    public class Window_DebugHub : Window
    {
        private readonly CompDebugShipSpawner comp;
        private Vector2 scroll;

        private const float WindowWidth = 560f;
        private const float WindowHeight = 640f;

        public Window_DebugHub(CompDebugShipSpawner debugComp)
        {
            comp = debugComp;
            forcePause = false;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
            absorbInputAroundWindow = false;
        }

        public override Vector2 InitialSize => new Vector2(WindowWidth, WindowHeight);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Rect body = new Rect(0f, 0f, inRect.width, inRect.height);
            Widgets.BeginScrollView(body, ref scroll, new Rect(0f, 0f, inRect.width - 24f, ScrollHeight()));
            float y = 0f;

            if (comp == null || comp.parent == null || comp.parent.Destroyed)
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0, y, inRect.width - 24f, 26f), "调试台已销毁，关闭面板。");
                Widgets.EndScrollView();
                Close();
                return;
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width - 24f, 26f), "PF 统一调试面板");
            y += 30f;

            DrawSectionHeader(inRect, ref y, "轨道 / 空间站");
            RowButtons(inRect, ref y, new (string, Action)[]
            {
                ("部署空间站", () => comp.SpawnTestStation()),
                ("双引擎测试", () => comp.SpawnDualEngineTest()),
                ("架构验证测试", () => BurnArchitectureTest.Run()),
            });

            DrawSectionHeader(inRect, ref y, "炮塔（动能炮）");
            RowButtons(inRect, ref y, new (string, Action)[]
            {
                ("对射", () => comp.SpawnTurretTest()),
                ("全方位六方位", () => comp.SpawnTurretOmniTest()),
                ("精确炮击", () => comp.SpawnTurretPrecisionTest()),
            });

            DrawSectionHeader(inRect, ref y, "炮塔（定向能）");
            RowButtons(inRect, ref y, new (string, Action)[]
            {
                ("对射", () => comp.SpawnRayTurretTest()),
                ("遮挡验证", () => comp.SpawnRayOcclusionTest()),
                ("旋转命中", () => comp.SpawnRayRotatedTest()),
                ("多目标齐射", () => comp.SpawnRayMultiTest()),
            });

            DrawSectionHeader(inRect, ref y, "无 Map 实体 / 姿态");
            RowButtons(inRect, ref y, new (string, Action)[]
            {
                ("无Map碰撞测试", () => comp.SpawnCollisionTest()),
                ("主推轴向+RCS 燃烧", () => comp.SpawnAxialThrustTest()),
                ("操舵测试场景", () => comp.SpawnSteeringTest()),
            });

            DrawSectionHeader(inRect, ref y, "跳帮");
            RowButtons(inRect, ref y, new (string, Action)[]
            {
                ("单兵 EVA", () => comp.SpawnBoardingTest()),
                ("打开跳帮测试面板", () => Find.WindowStack.Add(new Window_BoardingTest())),
            });

            DrawSectionHeader(inRect, ref y, "发射");
            RowButtons(inRect, ref y, new (string, Action)[]
            {
                ("打开发射测试面板", () => Find.WindowStack.Add(new Window_LaunchTest())),
            });

            DrawSectionHeader(inRect, ref y, "导弹（自主武器平台）");
            RowButtons(inRect, ref y, new (string, Action)[]
            {
                ("发射测试(20km)", () => comp.SpawnMissileLaunchTest()),
                ("撞击测试(1.5km)", () => comp.SpawnMissileImpactTest()),
                ("巡航→终末", () => comp.SpawnMissileCruiseTest()),
                ("拦截对撞", () => comp.SpawnMissileInterceptTest()),
            });
            RowButtons(inRect, ref y, new (string, Action)[]
            {
                ("弹群齐射→解离(8)", () => comp.SpawnVolleyImpactTest()),
                ("弹群合并期被撞", () => comp.SpawnVolleyMergedHitTest()),
                ("长距制导(60km)", () => comp.SpawnMissileLongRangeTest()),
                ("销毁来袭弹(测中止)", CompDebugShipSpawner.DestroyIncomingMissiles),
            });
            RowButtons(inRect, ref y, new (string, Action)[]
            {
                ("导弹控制面板", () => Find.WindowStack.Add(new Window_MissileControl())),
            });

            DrawSectionHeader(inRect, ref y, "调试日志");
            bool detailedCombat = CombatManager.DebugDetailedCombat;
            Widgets.CheckboxLabeled(new Rect(0, y, inRect.width - 24f, 22f), "详细战斗日志（战斗全链 + 射弹探针）", ref detailedCombat);
            if (detailedCombat != CombatManager.DebugDetailedCombat)
            {
                CombatManager.DebugDetailedCombat = detailedCombat;
                CombatManager.DebugProjectileProbeInterval = detailedCombat ? 30 : 0;
            }
            y += 26f;
            bool detailedSteer = SpaceGameComp.DebugDetailedSteering;
            Widgets.CheckboxLabeled(new Rect(0, y, inRect.width - 24f, 22f), "详细操舵日志（[Steer] / [Align]）", ref detailedSteer);
            if (detailedSteer != SpaceGameComp.DebugDetailedSteering)
                SpaceGameComp.DebugDetailedSteering = detailedSteer;
            y += 26f;
            bool detailedThrust = SpaceGameComp.DebugDetailedThrust;
            Widgets.CheckboxLabeled(new Rect(0, y, inRect.width - 24f, 22f), "详细推进日志（无Map主推/RCS [Thrust]）", ref detailedThrust);
            if (detailedThrust != SpaceGameComp.DebugDetailedThrust)
                SpaceGameComp.DebugDetailedThrust = detailedThrust;
            y += 26f;
            bool detailedGuidance = AutonomousVehicleManager.DebugDetailedGuidance;
            Widgets.CheckboxLabeled(new Rect(0, y, inRect.width - 24f, 22f), "详细导弹制导日志（[导弹制导] 距离/接近/dv/燃料）", ref detailedGuidance);
            if (detailedGuidance != AutonomousVehicleManager.DebugDetailedGuidance)
                AutonomousVehicleManager.DebugDetailedGuidance = detailedGuidance;
            y += 26f;

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(0, y, inRect.width - 24f, 20f), "提示：部署类场景会自动解除暂停（1x）；详细日志可直接在游戏日志看 [Debug] 前缀。");
            y += 22f;

            Widgets.EndScrollView();
        }

        private float ScrollHeight()
        {
            return 560f;
        }

        private void DrawSectionHeader(Rect inRect, ref float y, string title)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, y, inRect.width - 24f, 22f), title);
            y += 22f;
        }

        private void RowButtons(Rect inRect, ref float y, (string label, Action action)[] buttons)
        {
            float bw = (inRect.width - 24f - (buttons.Length - 1) * 6f) / buttons.Length;
            for (int i = 0; i < buttons.Length; i++)
            {
                Rect r = new Rect(i * (bw + 6f), y, bw, 28f);
                bool enabled = buttons[i].action != null;
                Color prev = GUI.color;
                if (!enabled)
                    GUI.color = new Color(0.55f, 0.55f, 0.55f, 1f);
                if (Widgets.ButtonText(r, buttons[i].label, true, false, enabled))
                    buttons[i].action?.Invoke();
                GUI.color = prev;
            }
            y += 34f;
        }
    }
}
