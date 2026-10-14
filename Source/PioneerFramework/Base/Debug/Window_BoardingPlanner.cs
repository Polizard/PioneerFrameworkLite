using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;

namespace PioneerFramework
{
    /// <summary>
    /// 落点高亮标记（MapComponent）：布置跳帮界面在目标空间站 Map 上绘制已选落点（绿）与
    /// 当前指向格（黄）。窗口关闭时移除。
    /// </summary>
    public class BoardingPlannerMarker : MapComponent
    {
        public readonly List<IntVec3> cells = new();
        public IntVec3 currentCell = IntVec3.Invalid;

        private static readonly Color ColorAssigned = new(0.2f, 1f, 0.4f, 0.85f);
        private static readonly Color ColorCurrent = new(1f, 0.85f, 0.2f, 0.9f);

        public BoardingPlannerMarker(Map map) : base(map) { }

        public override void MapComponentOnGUI()
        {
            if (map != Find.CurrentMap) return;
            if (cells.Count > 0)
                GenDraw.DrawFieldEdges(cells, ColorAssigned);
            if (currentCell.IsValid && currentCell.InBounds(map))
                GenDraw.DrawFieldEdges(new List<IntVec3> { currentCell }, ColorCurrent);
        }
    }

    /// <summary>
    /// 布置跳帮界面（设计 §7）：
    /// 选单位（EVA pawn 复选框 / 穿梭机复选框）→ 选目标空间站 → 进入其内部 Map →
    /// 逐单位点击可站立格选落点（存目标空间站局部系偏移）→ 确认出发（LaunchEva / LaunchShuttle）。
    /// 穿梭机乘客 = 选中 pawn 中停靠穿梭机 4 格内的（其余选中 pawn 保持 EVA）。
    /// </summary>
    public class Window_BoardingPlanner : Window
    {
        private class PlannerUnit
        {
            public Pawn pawn = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）;        // 非空 = EVA 单兵
            public Thing shuttle = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）;    // 非空 = 穿梭机
            public DVector3? offset; // 落点局部系偏移（格 → 米）

            public string Label => pawn != null
                ? "EVA " + pawn.LabelShort
                : "穿梭机 Shuttle" + (shuttle?.TryGetComp<CompShuttleDock>()?.shuttleId ?? -1);
        }

        private List<Pawn> candidatePawns = new();
        private List<Thing> candidateShuttles = new();
        private readonly HashSet<Pawn> selectedPawns = new();
        private readonly HashSet<Thing> selectedShuttles = new();

        private List<SpaceEntityGroup> targetStations = new();
        private int targetIndex = -1;

        private List<PlannerUnit> units = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）;  // 进入选落点阶段时冻结的单位列表
        private bool picking;
        private int pickingIndex;
        private Map pickingMap = null!;   // null!：引擎/Def/存档绑定（N2 可空族家规）

        private bool departed;

        private const float WindowWidth = 520f;
        private const float WindowHeight = 620f;

        public Window_BoardingPlanner(Pawn? preselectPawn = null, List<Thing>? preselectShuttles = null)
        {
            forcePause = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;

            if (preselectPawn != null)
                selectedPawns.Add(preselectPawn);
            if (preselectShuttles != null)
                foreach (var s in preselectShuttles)
                    selectedShuttles.Add(s);
        }

        public override Vector2 InitialSize => new(WindowWidth, WindowHeight);

        public override void PreOpen()
        {
            base.PreOpen();
            var map = Find.CurrentMap;
            if (map != null)
            {
                candidatePawns = map.mapPawns.AllPawnsSpawned
                    .Where(p => p.RaceProps.Humanlike && p.Faction == Faction.OfPlayer)
                    .ToList();
                candidateShuttles = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial)
                    .Where(t => t.TryGetComp<CompShuttleDock>() != null)
                    .ToList();
            }
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg != null)
            {
                targetStations = sg.GetAllGroups()
                    .Where(g => g.IsActive && g.GetEntityIds().Any(id => sg.GetEntityById(id)?.HasMap == true))
                    .ToList();
                if (targetIndex < 0 && targetStations.Count > 0)
                    targetIndex = 0;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            float y = 0f;

            if (picking)
                DrawPickingMode(inRect, ref y);
            else
                DrawSetupMode(inRect, ref y);
        }

        // ════════════════════════════════════
        //  阶段一：设置（选单位 + 选目标站）
        // ════════════════════════════════════

        private void DrawSetupMode(Rect inRect, ref float y)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width, 28f), "布置跳帮");
            y += 34f;
            Text.Font = GameFont.Small;

            // EVA 单位
            Widgets.Label(new Rect(0, y, inRect.width, 22f), "突击兵（EVA，各选落点）");
            y += 26f;
            foreach (var p in candidatePawns)
            {
                bool sel = selectedPawns.Contains(p);
                Widgets.CheckboxLabeled(new Rect(4, y, inRect.width - 8, 22f), p.LabelShort, ref sel);
                if (sel != selectedPawns.Contains(p))
                {
                    if (sel) selectedPawns.Add(p); else selectedPawns.Remove(p);
                }
                y += 22f;
            }
            if (candidatePawns.Count == 0)
            {
                Widgets.Label(new Rect(4, y, inRect.width - 8, 20f), "（本 Map 无玩家人类 pawn）");
                y += 22f;
            }
            y += 6f;

            // 穿梭机单位
            Widgets.Label(new Rect(0, y, inRect.width, 22f), "穿梭机（乘客 = 4 格内选中的 EVA pawn）");
            y += 26f;
            foreach (var s in candidateShuttles)
            {
                bool sel = selectedShuttles.Contains(s);
                string label = "穿梭机 Shuttle" + (s.TryGetComp<CompShuttleDock>()?.shuttleId ?? -1);
                Widgets.CheckboxLabeled(new Rect(4, y, inRect.width - 8, 22f), label, ref sel);
                if (sel != selectedShuttles.Contains(s))
                {
                    if (sel) selectedShuttles.Add(s); else selectedShuttles.Remove(s);
                }
                y += 22f;
            }
            if (candidateShuttles.Count == 0)
            {
                Widgets.Label(new Rect(4, y, inRect.width - 8, 20f), "（本 Map 无停靠穿梭机）");
                y += 22f;
            }
            y += 6f;

            // 目标空间站
            Widgets.Label(new Rect(0, y, inRect.width, 22f), "目标空间站");
            y += 26f;
            for (int i = 0; i < targetStations.Count; i++)
            {
                var g = targetStations[i];
                var e = FirstStationEntity(g);
                string name = "站";
                if (e != null)
                    name = SpaceMapGenerator.GetEntityMap(e)?.GetComponent<SpaceMapComp>()?.StationName ?? "站";
                if (Widgets.RadioButtonLabeled(new Rect(4, y, inRect.width - 8, 22f),
                    $"G{g.GroupId} · {name}", i == targetIndex))
                    targetIndex = i;
                y += 22f;
            }
            if (targetStations.Count == 0)
            {
                Widgets.Label(new Rect(4, y, inRect.width - 8, 20f), "（无空间站目标）");
                y += 22f;
            }
            y += 8f;

            bool canStart = targetIndex >= 0 && targetIndex < targetStations.Count
                && (selectedPawns.Count > 0 || selectedShuttles.Count > 0);
            if (canStart)
            {
                if (Widgets.ButtonText(new Rect(0, y, inRect.width, 34f), "进入目标站并选落点"))
                    BeginPicking();
                y += 40f;
            }
            else
            {
                Widgets.Label(new Rect(0, y, inRect.width, 22f),
                    "请至少选择一个单位（EVA 兵 / 穿梭机）与目标空间站");
                y += 26f;
            }

            // 选落点完成后的摘要 + 出发
            if (units != null)
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0, y, inRect.width, 26f), "落点确认");
                y += 30f;
                Text.Font = GameFont.Small;
                foreach (var u in units)
                {
                    Widgets.Label(new Rect(4, y, inRect.width - 8, 22f),
                        u.Label + (u.offset.HasValue ? " → 落点已选" : " → 未选"));
                    y += 22f;
                }
                y += 6f;
                if (units.All(u => u.offset.HasValue))
                {
                    if (Widgets.ButtonText(new Rect(0, y, inRect.width, 34f), "出发！"))
                        Depart();
                    y += 40f;
                }
            }
        }

        // ════════════════════════════════════
        //  阶段二：点击选落点
        // ════════════════════════════════════

        private void DrawPickingMode(Rect inRect, ref float y)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, y, inRect.width, 26f),
                $"为「{units[pickingIndex].Label}」选择落点（{pickingIndex + 1}/{units.Count}）");
            y += 30f;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0, y, inRect.width, 40f),
                "点击目标空间站地图上的可站立格。\n绿色 = 已选落点，黄色 = 当前指向。");
            y += 44f;

            for (int i = 0; i < units.Count; i++)
            {
                Text.Font = i == pickingIndex ? GameFont.Medium : GameFont.Small;
                Widgets.Label(new Rect(0, y, inRect.width, 22f),
                    units[i].Label + (units[i].offset.HasValue ? " → 已选" : " ← 待选"));
                y += 24f;
            }
            Text.Font = GameFont.Small;

            // 地图点击捕获
            var marker = pickingMap.GetComponent<BoardingPlannerMarker>();
            IntVec3 hover = UI.MouseCell();
            marker.currentCell = hover.InBounds(pickingMap) && hover.Walkable(pickingMap)
                ? hover : IntVec3.Invalid;

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0
                && !inRect.Contains(Event.current.mousePosition)
                && hover.InBounds(pickingMap) && hover.Walkable(pickingMap))
            {
                units[pickingIndex].offset = (DVector3)MapCoordMapper.MapCellToShip(pickingMap, hover);
                marker.cells.Add(hover);
                Log.Message($"[Boarding] {units[pickingIndex].Label} 落点格 {hover} 偏移({units[pickingIndex].offset.Value.x:F1},{units[pickingIndex].offset.Value.y:F1},{units[pickingIndex].offset.Value.z:F1})m");
                pickingIndex++;
                if (pickingIndex >= units.Count)
                {
                    picking = false;
                    marker.currentCell = IntVec3.Invalid;
                }
                Event.current.Use();
            }
        }

        private void BeginPicking()
        {
            var target = targetStations[targetIndex];
            var targetEntity = FirstStationEntity(target);
            if (targetEntity == null) return;
            var m = SpaceMapGenerator.GetEntityMap(targetEntity);
            if (m == null) return;

            units = new List<PlannerUnit>();
            foreach (var p in selectedPawns)
                units.Add(new PlannerUnit { pawn = p });
            foreach (var s in selectedShuttles)
                units.Add(new PlannerUnit { shuttle = s });

            pickingMap = m;
            picking = true;
            pickingIndex = 0;
            SpaceMapGenerator.EnterPocketMap(m);

            if (pickingMap.GetComponent<BoardingPlannerMarker>() == null)
                pickingMap.components.Add(new BoardingPlannerMarker(pickingMap));
        }

        private void Depart()
        {
            if (departed) return;
            departed = true;
            var boarding = Current.Game?.GetComponent<BoardingManager>();
            if (boarding == null || targetIndex < 0 || targetIndex >= targetStations.Count)
            {
                Close();
                return;
            }

            var target = targetStations[targetIndex];
            var targetEntity = FirstStationEntity(target);
            if (targetEntity == null || targetEntity.Parts.Count == 0)
            {
                Close();
                return;
            }
            int targetGroupId = target.GroupId;
            int targetPartId = targetEntity.Parts[0].partId;

            // 穿梭机乘客：选中 pawn 中停靠穿梭机 4 格内的（其余保持 EVA）
            var evaPawns = new HashSet<Pawn>(selectedPawns);
            foreach (var unit in units)
            {
                if (unit.shuttle == null || unit.offset == null || unit.shuttle.Destroyed) continue;
                var passengers = new List<Pawn>();
                foreach (var p in new List<Pawn>(evaPawns))
                {
                    if (p != null && !p.Destroyed && p.Spawned
                        && p.Position.DistanceTo(unit.shuttle.Position) <= 4f)
                    {
                        passengers.Add(p);
                        evaPawns.Remove(p);
                    }
                }
                boarding.LaunchShuttle(unit.shuttle, passengers, targetGroupId, targetPartId, unit.offset.Value);
            }
            foreach (var unit in units)
            {
                if (unit.pawn == null || unit.offset == null || unit.pawn.Destroyed) continue;
                if (evaPawns.Contains(unit.pawn))
                    boarding.LaunchEva(unit.pawn, targetGroupId, targetPartId, unit.offset.Value);
            }
            Close();
        }

        private static SpaceEntity? FirstStationEntity(SpaceEntityGroup g)
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return null;
            foreach (int eid in g.GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e != null && e.HasMap) return e;
            }
            return null;
        }

        public override void PreClose()
        {
            base.PreClose();
            if (pickingMap != null)
            {
                var marker = pickingMap.GetComponent<BoardingPlannerMarker>();
                if (marker != null)
                    pickingMap.components.Remove(marker);
            }
        }
    }
}
