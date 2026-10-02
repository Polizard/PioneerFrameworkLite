using System.Collections.Generic;
using UnityEngine;
using Verse;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    /// <summary>
    /// 导弹控制面板（《导弹系统设计》§9 指挥模型：Mission 制，玩家经此下发指令）。
    /// 行为模型（2026-09-06 定稿）：导弹巡航段 = 普通航天器（玩家用机动 UI 指挥，不做自动兰伯特）；
    /// Attack = 玩家在可达几何下下达的终末撞击命令（纯视线火炬直扑，追丢自动中止回 Standby）；
    /// Standby = 中止回待命；Loiter = 熄火待机意图（轨道部署由玩家机动完成）；SelfDestruct = 就地引爆。
    /// 操作流程：列表点 [攻] → 底部出现目标选择列表（全部 group，含敌对/友军——IFF 不硬拦，UI 明示）
    /// → 点目标行即下发。按钮动作先进队列、帧末统一执行（防渲染期迭代集合被销毁）。
    ///
    /// 目录说明（2026-09-14 D1）：本文件原在 `Base/Debug/`，但它是**玩家可达的正式 UI**
    /// （由可建造建筑 `PF_Building_MissileLauncher` 的 Gizmo 打开，非 dev-only），放在 Debug 目录
    /// 会误导（让人以为"仅开发者可见"而低估其每帧开销与回归面）。现与开它的 Comp 同目录——
    /// 同惯例：`Window_VolleyCountSlider` 就在 `CompMissileLauncher.cs` 内。
    /// </summary>
    public class Window_MissileControl : Window
    {
        private Vector2 scroll;
        /// <summary>正在选择攻击目标的导弹 group id（-1 = 无）。</summary>
        private int pendingAttackGroupId = -1;
        /// <summary>发射器过滤：仅显示该发射器（站 group）释放的导弹；-1 = 全部（DebugHub 入口）。</summary>
        private readonly int launcherFilterGroupId;
        private readonly List<CommandItem> pendingCommands = new List<CommandItem>();

        private struct CommandItem
        {
            public int groupId;
            public AutonomousVehicleManager.MissionType mission;
            public int targetGroupId;
        }

        private const float WindowWidth = 760f;
        private const float WindowHeight = 600f;

        public Window_MissileControl(int launcherFilterGroupId = -1)
        {
            this.launcherFilterGroupId = launcherFilterGroupId;
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
            RefreshComponentCache();
            var avm = Current.Game?.GetComponent<AutonomousVehicleManager>();
            var spaceGame = Current.Game?.GetComponent<SpaceGameComp>();
            Text.Font = GameFont.Small;
            float y = 0f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width, 26f),
                launcherFilterGroupId >= 0 ? string.Format("导弹控制（发射器 G{0} 释放）", launcherFilterGroupId) : "导弹控制（自主武器平台）");
            y += 30f;
            Text.Font = GameFont.Small;
            if (avm == null || spaceGame == null)
            {
                Widgets.Label(new Rect(0, y, inRect.width, 22f), "管理器未找到（AutonomousVehicleManager / SpaceGameComp）");
                return;
            }

            int inBook = 0;
            for (int i = 0; i < avm.Vehicles.Count; i++)
            {
                var rec = avm.Vehicles[i];
                if (launcherFilterGroupId >= 0 && rec.launcherGroupId != launcherFilterGroupId) continue;
                if (spaceGame.GetGroupById(rec.groupId) != null)
                    inBook++;
            }
            Widgets.Label(new Rect(0, y, inRect.width, 22f),
                string.Format("在册 {0} 枚（存活 {1}）｜ Mission：攻=Attack 待命=Standby 待机=Loiter 毁=SelfDestruct｜ 提示：导弹巡航段是普通航天器，请先用机动 UI 部署到位再下达撞击",
                    avm.Vehicles.Count, inBook));
            y += 28f;

            // ── 导弹列表 ──
            var vehicles = avm.Vehicles;
            float rowH = 26f;
            float listH = inRect.height - y - (pendingAttackGroupId >= 0 ? 220f : 30f);
            Widgets.BeginScrollView(new Rect(0, y, inRect.width, listH), ref scroll,
                new Rect(0, 0, inRect.width - 24f, Mathf.Max(listH, vehicles.Count * rowH + 4f)));

            float ly = 2f;
            for (int i = 0; i < vehicles.Count; i++)
            {
                var rec = vehicles[i];
                if (launcherFilterGroupId >= 0 && rec.launcherGroupId != launcherFilterGroupId) continue;
                var group = spaceGame.GetGroupById(rec.groupId);
                if (group == null) continue;

                string missionTxt = MissionLabel(rec.mission);
                string targetTxt = "—";
                string distTxt = "—";
                string velTxt = "—";
                string fuelTxt = "—";
                string burnTxt = "";
                {
                    burnTxt = group.HasActiveBurn ? "燃烧" : "熄火";
                    var src = spaceGame.GetGroupPropulsionSource(group);
                    // 弹群合并期推进源 = 群级聚合器（Σ 全枚）；燃料按单枚口径显示 = 总量 / logicalCount（§5）
                    double totalFuel = src != null ? src.TotalPropellantMass : 0.0;
                    fuelTxt = src != null
                        ? (rec.logicalCount > 1 ? totalFuel / rec.logicalCount : totalFuel).ToString("F0") + "kg"
                        : "—";
                    velTxt = cm == null ? "—" : cm.GetGroupVelocity(group).magnitude.ToString("F0") + "m/s";
                    if (rec.targetGroupId >= 0)
                    {
                        var t = spaceGame.GetGroupById(rec.targetGroupId);
                        if (t != null && cm != null)
                        {
                            DVector3 mp = cm.GetGroupPos(group);
                            DVector3 tp = cm.GetGroupPos(t);
                            double d = (tp - mp).magnitude;
                            distTxt = (d / 1000.0).ToString("F1") + "km";
                            targetTxt = "G" + rec.targetGroupId;
                        }
                        else targetTxt = "G" + rec.targetGroupId + "(失)";
                    }
                }

                Rect row = new Rect(0, ly, inRect.width - 24f, rowH);
                if (i % 2 == 1)
                    Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.03f));
                Widgets.Label(new Rect(4, ly + 3f, 430f, 22f),
                    string.Format("V{0} G{1}{8}｜{2}→{3}｜距 {4}｜速 {5}｜料 {6}｜{7}",
                        rec.vehicleId, rec.groupId,
                        rec.logicalCount > 1 ? " ×" + rec.logicalCount : "",
                        missionTxt, targetTxt, distTxt, velTxt, fuelTxt, burnTxt));

                float bx = inRect.width - 24f - 230f;
                if (Widgets.ButtonText(new Rect(bx, ly + 2f, 70f, 22f), "攻…"))
                {
                    pendingAttackGroupId = rec.groupId; // 显示目标选择列表
                }
                if (Widgets.ButtonText(new Rect(bx + 76f, ly + 2f, 70f, 22f), "待命"))
                    pendingCommands.Add(new CommandItem { groupId = rec.groupId, mission = AutonomousVehicleManager.MissionType.Standby });
                if (Widgets.ButtonText(new Rect(bx + 152f, ly + 2f, 70f, 22f), "自毁"))
                    pendingCommands.Add(new CommandItem { groupId = rec.groupId, mission = AutonomousVehicleManager.MissionType.SelfDestruct });
                ly += rowH;
            }
            Widgets.EndScrollView();
            y += listH;

            // ── 目标选择（点 [攻] 后出现）：全部 group（含友军——IFF 不硬拦，误伤是玩法，UI 明示）──
            if (pendingAttackGroupId >= 0)
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0, y, inRect.width, 24f),
                    string.Format("选择撞击目标（V{0} 的 [攻]：点击下列 group 即下达 Attack——可打任何阵营，误伤自负）", pendingAttackGroupId));
                y += 26f;
                Text.Font = GameFont.Small;
                float ty = y;
                var groups = new List<SpaceEntityGroup>();
                var sensor = Current.Game?.GetComponent<SensorManager>();   // [V3]：只列"看得见"的
                foreach (var g in spaceGame.GetAllGroups())
                {
                    if (!g.IsActive) continue; // 地表锚点（星座成员载体）不是可打目标
                    if (sensor != null && !sensor.PlayerCanSee(g)) continue;
                    groups.Add(g);
                }
                float tH = Mathf.Min(190f, groups.Count * 24f + 4f);
                Widgets.BeginScrollView(new Rect(0, ty, inRect.width, tH), ref scroll,
                    new Rect(0, 0, inRect.width - 24f, groups.Count * 24f + 4f));
                float iy = 2f;
                for (int i = 0; i < groups.Count; i++)
                {
                    var g = groups[i];
                    if (g.GroupId == pendingAttackGroupId) continue;
                    bool isMissile = avm.GetRecord(g.GroupId) != null;
                    bool hasMap = false; // 带 Map（空间站/带图船）目标可精确打击（发射器精确打击点落该格）
                    foreach (int eid in g.GetEntityIds())
                    {
                        var e = spaceGame.GetEntityById(eid);
                        if (e != null && e.MapId >= 0)
                        {
                            hasMap = true;
                            break;
                        }
                    }
                    string dTxt = "—";
                    if (cm != null)
                    {
                        var targetFor = spaceGame.GetGroupById(pendingAttackGroupId);
                        if (targetFor != null)
                        {
                            double d = (cm.GetGroupPos(g) - cm.GetGroupPos(targetFor)).magnitude;
                            dTxt = (d / 1000.0).ToString("F1") + "km";
                        }
                    }
                    Rect r = new Rect(0, iy, inRect.width - 24f, 24f);
                    string label = string.Format("G{0} F{1}{2}{3}｜距攻击弹 {4}",
                        g.GroupId, g.FactionId, isMissile ? " [导弹]" : "", hasMap ? " [有图]" : "", dTxt);
                    if (Widgets.ButtonText(r, label))
                        pendingCommands.Add(new CommandItem
                        {
                            groupId = pendingAttackGroupId,
                            mission = AutonomousVehicleManager.MissionType.Attack,
                            targetGroupId = g.GroupId
                        });
                    iy += 24f;
                }
                Widgets.EndScrollView();
            }
            else
            {
                // 无选择态时底部保留给提示
                y += 8f;
            }

            // ── 帧末统一执行指令（防渲染期迭代销毁集合）──
            if (pendingCommands.Count > 0)
            {
                for (int i = 0; i < pendingCommands.Count; i++)
                    avm.IssueCommand(pendingCommands[i].groupId, pendingCommands[i].mission, pendingCommands[i].targetGroupId);
                pendingCommands.Clear();
                pendingAttackGroupId = -1;
            }
        }

        private static string MissionLabel(AutonomousVehicleManager.MissionType m)
        {
            switch (m)
            {
                case AutonomousVehicleManager.MissionType.Attack: return "撞击";
                case AutonomousVehicleManager.MissionType.Standby: return "待命";
                case AutonomousVehicleManager.MissionType.Loiter: return "待机";
                case AutonomousVehicleManager.MissionType.SelfDestruct: return "自毁";
                case AutonomousVehicleManager.MissionType.Following: return "跟随";
                default: return m.ToString();
            }
        }

        /// <summary>
        /// B4：每帧缓存一次 CombatManager。
        /// 原为表达式体属性 `=> Current.Game?.GetComponent&lt;CombatManager&gt;()`——**每次访问**都重新
        /// `GetComponent`（RimWorld 侧是线性扫组件表），而本窗口每帧、每行要读 1–2 次 ⇒ 每帧数十次线性扫。
        /// 本窗口**玩家可达**（可建造建筑 `PF_Building_MissileLauncher` 的 Gizmo 打开，非 dev-only），
        /// 故这不是"仅开发者的开销"。每帧开头刷新一次即可（组件实例换局才变，帧内恒定）。
        /// </summary>
        private CombatManager? cm;

        private void RefreshComponentCache()
        {
            cm = Current.Game?.GetComponent<CombatManager>();
        }
    }
}
