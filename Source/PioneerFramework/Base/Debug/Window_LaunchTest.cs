using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 火箭发射/爬升段测试面板（调试 Gizmo）：
    /// - 场景部署：爬升测试（赤道顺行，纯物理火箭带大储罐绕过 debug spec 燃料限制）+
    ///   指定经纬度爬升
    /// - 调试开关：详细日志（DebugDetailedLaunch）+ 状态探针间隔
    /// - 实时活动爬升状态表（r/ΔR/v/|dv|/燃料/燃烧/到达）
    /// - 结算历史（Orbited/Destroyed/CircularizeFailed）+ 入轨验证（实轨检查朝向=圆轨、能量循环）
    /// </summary>
    public class Window_LaunchTest : Window
    {
        private Vector2 activeScroll;
        private Vector2 logScroll;
        private int probeOption; // 0=关 1=30 2=60 3=120

        private const float WindowWidth = 700f;
        private const float WindowHeight = 660f;

        public Window_LaunchTest()
        {
            forcePause = false;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
            probeOption = RocketLaunchController.ProbeInterval == 30 ? 1
                : RocketLaunchController.ProbeInterval == 120 ? 3
                : RocketLaunchController.ProbeInterval > 0 ? 2 : 0;
        }

        public override Vector2 InitialSize => new(WindowWidth, WindowHeight);

        public override void DoWindowContents(Rect inRect)
        {
            var ctrl = Current.Game?.GetComponent<RocketLaunchController>();
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            Text.Font = GameFont.Small;
            float y = 0f;

            // ── 标题 + 统计 ──
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width, 26f), "火箭发射测试面板");
            y += 30f;
            Text.Font = GameFont.Small;
            if (ctrl != null)
                Widgets.Label(new Rect(0, y, inRect.width, 22f),
                    $"统计：活动爬升 {ctrl.ActiveLaunches.Count} ｜ 结算历史 {ctrl.RecentLog.Count} 条");
            else
                Widgets.Label(new Rect(0, y, inRect.width, 22f), "RocketLaunchController 未找到");
            y += 28f;

            // ── 场景部署 ──
            DrawSectionHeader(inRect, ref y, "场景部署");
            float bw = (inRect.width - 8f) / 3f;
            if (Widgets.ButtonText(new Rect(0, y, bw, 30f), "爬升测试（当前 Map）"))
            {
                bool wasPaused = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                if (wasPaused)
                    Log.Message("[LaunchTest] 检测到暂停，已自动解除（TimeSpeed.Normal）");
                Log.Message($"[LaunchTest] 将从当前地图起飞：CurrentMap={(Find.CurrentMap != null ? Find.CurrentMap.info.parent?.def?.defName ?? "?" : "null")} (uid {(Find.CurrentMap != null ? Find.CurrentMap.uniqueID : -1)}, tile {(Find.CurrentMap != null ? Find.CurrentMap.Tile : -1)})");
                LaunchTestScenarios.RunAscentTest(Find.CurrentMap, 0.0, 0.0);
            }
            if (Widgets.ButtonText(new Rect(bw + 4, y, bw, 30f), "爬升测试（高纬 45°）"))
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                LaunchTestScenarios.RunAscentTest(null, 45.0 * Mathf.Deg2Rad, 0.0);
                Log.Message("[LaunchTest] 提示：高纬入倾斜圆轨（控制律无面外项），用于观察面外分量残留");
            }
            if (Widgets.ButtonText(new Rect(2 * (bw + 4), y, bw, 30f), "清除视图 + 探针"))
            {
                RocketLaunchController.DebugDetailedLaunch = false;
                RocketLaunchController.ProbeInterval = 0;
                probeOption = 0;
                Log.Message("[LaunchTest] 已清除详细日志 / 探针");
            }
            y += 36f;

            // ── 调试开关 ──
            DrawSectionHeader(inRect, ref y, "调试日志");
            bool detailed = RocketLaunchController.DebugDetailedLaunch;
            Widgets.CheckboxLabeled(new Rect(0, y, inRect.width, 22f),
                "详细日志（每物理步打印引导状态：r/v/|dv|/arrived）", ref detailed);
            if (detailed != RocketLaunchController.DebugDetailedLaunch)
            {
                RocketLaunchController.DebugDetailedLaunch = detailed;
                Log.Message("[LaunchTest] 详细日志 " + (detailed ? "开启" : "关闭"));
            }
            y += 26f;

            Widgets.Label(new Rect(0, y, inRect.width, 22f), "状态探针间隔（[RocketLaunch探针] 日志）：");
            y += 22f;
            string[] labels = ["关闭", "30 tick", "60 tick", "120 tick"];
            for (int i = 0; i < labels.Length; i++)
            {
                bool sel = probeOption == i;
                if (Widgets.RadioButtonLabeled(new Rect(i * 130f, y, 126f, 22f), labels[i], sel))
                {
                    probeOption = i;
                    RocketLaunchController.ProbeInterval = new[] { 0, 30, 60, 120 }[i];
                }
            }
            y += 28f;

            // ── 活动爬升状态表 ──
            DrawSectionHeader(inRect, ref y, "活动爬升");
            if (ctrl != null && ctrl.ActiveLaunches.Count > 0)
            {
                float listH = 120f;
                var view = new Rect(0, y, inRect.width, listH);
                var content = new Rect(0, 0, inRect.width - 20f,
                    ctrl.ActiveLaunches.Count * 22f + 2f);
                Widgets.BeginScrollView(view, ref activeScroll, content);
                float ly = 0f;
                foreach (var st in ctrl.ActiveLaunches)
                {
                    var p = ctrl.ProbeState(st);
                    Widgets.Label(new Rect(0, ly, content.width, 20f),
                        $"G{p.groupId} alt={(p.r - st.ToParams().planetRadius) / 1000.0:F0}km r={p.r / 1000.0:F0}km v={p.vMag:F1}m/s vRad={p.vRad:F1} |dv|={p.dvMag:F1}m/s 燃={p.fuelKg:F0}kg 烧={(p.burning ? "on" : "off")} 到达={p.arrived}");
                    ly += 22f;
                }
                Widgets.EndScrollView();
                y += listH + 4f;
            }
            else
            {
                Widgets.Label(new Rect(0, y, inRect.width, 20f), "（无活动爬升，先在「场景部署」部署一枚测试火箭）");
                y += 24f;
            }

            // ── 结算历史 + 入轨验证 ──
            DrawSectionHeader(inRect, ref y, "结算历史（最近 " +
                (ctrl != null ? ctrl.RecentLog.Count : 0) + " 条）");
            if (ctrl != null && ctrl.RecentLog.Count > 0)
            {
                if (Widgets.ButtonText(new Rect(0, y, inRect.width, 26f),
                    "验证入轨：检查最近 Orbited 群组的实体仍在轨（圆轨/速度一致性）"))
                    VerifyOrbits(spaceGame);
                y += 32f;

                float listH = 150f;
                var view = new Rect(0, y, inRect.width, listH);
                var content = new Rect(0, 0, inRect.width - 20f,
                    ctrl.RecentLog.Count * 20f + 2f);
                Widgets.BeginScrollView(view, ref logScroll, content);
                float ly = 0f;
                for (int i = 0; i < ctrl.RecentLog.Count; i++)
                {
                    var e = ctrl.RecentLog[i];
                    Widgets.Label(new Rect(0, ly, content.width, 18f),
                        $"t{e.tick} G{e.groupId} [{e.outcome}] {e.detail}");
                    ly += 20f;
                }
                Widgets.EndScrollView();
                y += listH;
            }
            else
            {
                Widgets.Label(new Rect(0, y, inRect.width, 20f), "（无结算记录）");
                y += 24f;
            }
        }

        private void DrawSectionHeader(Rect inRect, ref float y, string title)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width, 24f), title);
            y += 28f;
            Text.Font = GameFont.Small;
        }

        /// <summary>验证所有 Orbited 记录对应的群组实体仍在轨：重力和位置一致、速度快于第一宇宙速度、轨道能量为束缚态。</summary>
        private void VerifyOrbits(SpaceGameComp? spaceGame)
        {
            if (spaceGame == null)
            {
                Log.Message("[LaunchTest] SpaceGameComp 缺失，跳过入轨验证");
                return;
            }

            int verified = 0, failed = 0;
            foreach (var e in Current.Game.GetComponent<RocketLaunchController>().RecentLog)
            {
                if (e.outcome != "Orbited") continue;
                var g = spaceGame.GetGroupById(e.groupId);
                if (g == null || !g.IsActive)
                {
                    Log.Message($"[LaunchTest] 验证失败：G{e.groupId} 已销毁/失联");
                    failed++;
                    continue;
                }

                DVector3 pos = g.CurrentOrbit.positionSystem.v;
                DVector3 vel = g.CurrentOrbit.velocitySystem.v;
                double r = pos.magnitude;
                double gm = spaceGame.GetBodyGM(g.CelestialBodyId);
                double v = vel.magnitude;
                double vCirc = System.Math.Sqrt(gm / r);          // 该半径圆轨速度
                double vEsc = System.Math.Sqrt(2.0 * gm / r);     // 逃逸速度
                double energy = v * v / 2.0 - gm / r;             // 比机械能
                bool bound = energy < 0.0 && r > gm * (1e-3);      // 束缚椭圆/圆
                bool speedOk = bound && v > vCirc * 0.95 && v < vEsc * 0.98;

                Log.Message($"[LaunchTest] 验证 G{e.groupId}：r={r / 1000.0:F0}km v={v:F1}m/s（vC={vCirc:F1} vEsc={vEsc:F1}）能量={energy / 1e6:F1} → {((bound && speedOk) ? "在轨 ✅" : "异常 ❌")}");
                if (bound && speedOk) verified++; else failed++;
            }
            Messages.Message($"入轨验证：通过 {verified} ｜ 异常 {failed}",
                failed > 0 ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.TaskCompletion);
        }
    }
}