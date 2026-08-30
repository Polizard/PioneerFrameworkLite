using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 跳帮测试面板（调试 Gizmo）：
    /// - 场景部署：EVA 单兵 / EVA 多兵(3) / 穿梭机(2 乘客) / 高速撞击（应爆炸）
    /// - 调试开关：详细日志（DebugDetailedBoarding）+ 状态探针间隔
    /// - 实时单位状态表（距离落点/相对速度/引导/击毁）
    /// - 结算历史（最近 20 条）+ 着陆验证（成员 pawn 是否已 Spawn 进 Map）
    /// </summary>
    public class Window_BoardingTest : Window
    {
        private Vector2 unitScroll;
        private Vector2 resultScroll;
        private int probeOption; // 0=关 1=30 2=60 3=120

        private const float WindowWidth = 680f;
        private const float WindowHeight = 640f;

        public Window_BoardingTest()
        {
            forcePause = false;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
            probeOption = BoardingManager.ProbeInterval == 30 ? 1
                : BoardingManager.ProbeInterval == 120 ? 3
                : BoardingManager.ProbeInterval > 0 ? 2 : 0;
        }

        public override Vector2 InitialSize => new Vector2(WindowWidth, WindowHeight);

        public override void DoWindowContents(Rect inRect)
        {
            var boarding = Current.Game?.GetComponent<BoardingManager>();
            Text.Font = GameFont.Small;
            float y = 0f;

            // ── 标题 + 统计 ──
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width, 26f), "跳帮测试面板");
            y += 30f;
            Text.Font = GameFont.Small;
            if (boarding != null)
                Widgets.Label(new Rect(0, y, inRect.width, 22f),
                    string.Format("统计：发射 {0} ｜ 着陆 {1} ｜ 失败/击毁 {2} ｜ 飞行中 {3}",
                        boarding.TotalLaunched, boarding.TotalLanded, boarding.TotalDestroyed,
                        boarding.Units.Count(u => u.state == BoardingState.Flying)));
            else
                Widgets.Label(new Rect(0, y, inRect.width, 22f), "BoardingManager 未找到");
            y += 28f;

            // ── 场景部署 ──
            DrawSectionHeader(inRect, ref y, "场景部署");
            float bw = (inRect.width - 12f) / 4f;
            if (Widgets.ButtonText(new Rect(0, y, bw, 30f), "EVA 单兵"))
                BoardingTestScenarios.DeployEvaTest(1);
            if (Widgets.ButtonText(new Rect(bw + 4, y, bw, 30f), "EVA 多兵 ×3"))
                BoardingTestScenarios.DeployEvaTest(3);
            if (Widgets.ButtonText(new Rect(2 * (bw + 4), y, bw, 30f), "穿梭机 ×2"))
                BoardingTestScenarios.DeployShuttleTest(2);
            if (Widgets.ButtonText(new Rect(3 * (bw + 4), y, bw, 30f), "高速撞击"))
                BoardingTestScenarios.DeployHighSpeedCrashTest();
            y += 36f;

            // ── 调试开关 ──
            DrawSectionHeader(inRect, ref y, "调试日志");
            bool detailed = BoardingManager.DebugDetailedBoarding;
            Widgets.CheckboxLabeled(new Rect(0, y, inRect.width, 22f),
                "详细日志（每物理步打印单位状态：dist/relv/aim/引导）", ref detailed);
            if (detailed != BoardingManager.DebugDetailedBoarding)
            {
                BoardingManager.DebugDetailedBoarding = detailed;
                Log.Message("[BoardingTest] 详细日志 " + (detailed ? "开启" : "关闭"));
            }
            y += 26f;

            Widgets.Label(new Rect(0, y, inRect.width, 22f), "状态探针间隔（[Boarding探针] 日志）：");
            y += 22f;
            string[] labels = { "关闭", "30 tick", "60 tick", "120 tick" };
            for (int i = 0; i < labels.Length; i++)
            {
                bool sel = probeOption == i;
                if (Widgets.RadioButtonLabeled(new Rect(i * 130f, y, 126f, 22f), labels[i], sel))
                {
                    probeOption = i;
                    BoardingManager.ProbeInterval = new[] { 0, 30, 60, 120 }[i];
                }
            }
            y += 28f;

            // ── 活动单位状态表 ──
            DrawSectionHeader(inRect, ref y, "飞行中单位");
            if (boarding != null && boarding.Units.Count > 0)
            {
                float listH = 120f;
                var view = new Rect(0, y, inRect.width, listH);
                var content = new Rect(0, 0, inRect.width - 20f,
                    boarding.Units.Count * 22f + 2f);
                Widgets.BeginScrollView(view, ref unitScroll, content);
                float ly = 0f;
                foreach (var u in boarding.Units)
                {
                    var st = boarding.GetStatus(u);
                    string member = u.type == VesselType.Eva
                        ? (u.members.Count > 0 && u.members[0].pawn != null ? u.members[0].pawn.LabelShort : "?")
                        : u.members.Count + " 乘客";
                    Widgets.Label(new Rect(0, ly, content.width, 20f),
                        string.Format("U{0} {1} [{2}] {3} ｜ dist={4:F0}m relv={5:F1}m/s 引导={6} 位=({7:F0},{8:F0},{9:F0})km",
                            u.id, u.type == VesselType.Eva ? "EVA" : "穿梭机", u.state, member,
                            st.distToAim, st.relVel, st.guidanceActive ? "on" : "off",
                            st.unitPos.x / 1000.0, st.unitPos.y / 1000.0, st.unitPos.z / 1000.0));
                    ly += 22f;
                }
                Widgets.EndScrollView();
                y += listH + 4f;
            }
            else
            {
                Widgets.Label(new Rect(0, y, inRect.width, 20f), "（无飞行中单位）");
                y += 24f;
            }

            // ── 结算历史 + 着陆验证 ──
            DrawSectionHeader(inRect, ref y, "结算历史（最近 " +
                (boarding != null ? boarding.RecentResults.Count : 0) + " 条）");
            if (boarding != null && boarding.RecentResults.Count > 0)
            {
                if (Widgets.ButtonText(new Rect(0, y, inRect.width, 26f),
                    "验证着陆：检查最近 Landed 记录中成员 pawn 是否已 Spawn 进目标站 Map"))
                    VerifyLandings(boarding);
                y += 32f;

                float listH = 150f;
                var view = new Rect(0, y, inRect.width, listH);
                var content = new Rect(0, 0, inRect.width - 20f,
                    boarding.RecentResults.Count * 20f + 2f);
                Widgets.BeginScrollView(view, ref resultScroll, content);
                float ly = 0f;
                for (int i = boarding.RecentResults.Count - 1; i >= 0; i--)
                {
                    var r = boarding.RecentResults[i];
                    Widgets.Label(new Rect(0, ly, content.width, 18f),
                        string.Format("t{0} U{1} {2} [{3}] {4}（{5} 成员）",
                            r.tick, r.unitId, r.type == VesselType.Eva ? "EVA" : "穿梭机",
                            r.outcome, r.detail, r.memberCount));
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

        /// <summary>验证所有 Landed 记录的成员 pawn 已 Spawn（应位于目标站 Map）。结果打到日志。</summary>
        private void VerifyLandings(BoardingManager boarding)
        {
            int verified = 0, failed = 0;
            foreach (var r in boarding.RecentResults)
            {
                if (r.outcome != BoardingState.Landed || r.memberPawns == null) continue;
                foreach (var p in r.memberPawns)
                {
                    if (p == null || p.Destroyed)
                    {
                        Log.Message(string.Format("[BoardingTest] 验证失败：U{0} 成员已死亡/销毁", r.unitId));
                        failed++;
                        continue;
                    }
                    if (p.Spawned && p.Map != null)
                    {
                        Log.Message(string.Format("[BoardingTest] 验证通过：U{0} 成员 {1} 已登陆 Map {2} 格 {3}",
                            r.unitId, p.LabelShort, p.Map.uniqueID, p.Position));
                        verified++;
                    }
                    else
                    {
                        Log.Message(string.Format("[BoardingTest] 验证失败：U{0} 成员 {1} 未 Spawn（Spawned={2}）",
                            r.unitId, p.LabelShort, p.Spawned));
                        failed++;
                    }
                }
            }
            Messages.Message(string.Format("着陆验证：通过 {0} ｜ 失败 {1}", verified, failed),
                failed > 0 ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.TaskCompletion);
        }
    }
}
