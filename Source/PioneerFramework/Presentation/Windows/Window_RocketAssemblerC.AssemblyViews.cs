using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    public partial class Window_RocketAssemblerC
    {
        private enum MountKind { Head, After, Radial, Above, Below }

        private readonly record struct MountAction(MountKind Kind, DesignPartSpec? Target);

        private readonly record struct GeometryEntry(DesignPartSpec? Row, string? DefName, string? Category,
            int Quantity, int Parallel, int Stage, int Block, int Parent, bool Explicit, DVector3 Position,
            DQuaternion Rotation, bool OverrideShape, InertiaTensor.Shape Shape)
        {
            internal static GeometryEntry Read(DesignPartSpec? row) => row == null ? default : new(row,
                row.defName, row.category, row.quantity, row.parallelCount, row.stageId, row.blockId,
                row.parentIndex, row.useExplicitPose, row.localPosition, row.localRotation,
                row.useShapeOverride, row.shapeOverride);
        }

        private readonly RocketAssemblyViewport assemblyViewport = new();
        private readonly List<GeometryEntry> geometryEntries = new();
        private readonly Dictionary<DesignPartSpec, MountAction> placementNodes = new();
        private readonly List<DesignPartSpec> recentAssemblyParts = new();
        private readonly List<int> recentAssemblyRows = new();
        private readonly List<MountAction> activeMounts = new(3);
        private List<RocketAssemblyViewport.Part> assemblyParts = new();
        private List<RocketAssemblyViewport.Part> ghostParts = new();
        private List<RocketAssemblyViewport.Part>? shiftedPreview;
        private RocketDesignSpec? displayedAssembly;
        private DesignPartSpec? selectedAssemblyPart;
        private MountAction? hoveredMount;
        private MountAction? mountUnderMouse;
        private ShipPartDef? ghostDef;
        private int ghostStage;
        private int ghostParallel;
        private bool showSideView = true;
        private string assemblyNotice = "";

        private static ShipPartDef? FindAssemblyDef(string name)
            => string.IsNullOrEmpty(name) ? null : DefDatabase<ShipPartDef>.GetNamedSilentFail(name);

        private static string AssemblyPartName(DesignPartSpec part)
            => FindAssemblyDef(part.defName)?.LabelCap.ToString() ?? part.defName ?? "未知零件";

        private static List<RocketAssemblyViewport.Part> BuildAssemblyGeometry(RocketDesignSpec design)
        {
            var definitions = new Dictionary<string, ShipPartDef?>(StringComparer.Ordinal);
            ShipPartDef? Lookup(string name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                if (!definitions.TryGetValue(name, out var def))
                    definitions[name] = def = FindAssemblyDef(name);
                return def;
            }

            var built = DesignPartAssembler.BuildParts(design, design.mapWidth, design.mapHeight, out _,
                defLookup: Lookup, includeUnknownDefs: false, includeCabin: false,
                stageGapMeters: RocketDesignRules.StageGapMeters);
            var parts = new List<RocketAssemblyViewport.Part>(built.Count);
            var rowByPartId = new Dictionary<int, int>(built.Count);
            int cursor = 0;
            // BuildParts emits full stations in input order and skips missing definitions.
            // Recover row ownership here so the viewport uses the flight geometry itself.
            for (int rowIndex = 0; rowIndex < design.parts.Count && cursor < built.Count; rowIndex++)
            {
                var row = design.parts[rowIndex];
                if (row == null || Lookup(row.defName) == null) continue;
                int parallel = RocketDesignRules.ParallelOf(row);
                int stations = Math.Min(Math.Clamp(row.quantity, 1, DesignPartAssembler.MaxPartsPerEntry),
                    (DesignPartAssembler.MaxTotalParts - cursor) / parallel);
                int end = Math.Min(built.Count, cursor + stations * parallel);
                string appearance = AssemblyAppearance(design.parts, row);
                for (; cursor < end; cursor++)
                {
                    var part = built[cursor];
                    int parentRow = rowByPartId.TryGetValue(part.parentId, out int owner) ? owner : -1;
                    rowByPartId[part.partId] = rowIndex;
                    parts.Add(new RocketAssemblyViewport.Part(rowIndex, (Vector3)part.positionShip,
                        part.rotationShip.ToQuaternion(), (Vector3)part.boundsSizeShip, part.shape,
                        row.category ?? "", appearance, parentRow));
                }
            }
            return parts;
        }

        private static string AssemblyAppearance(List<DesignPartSpec> rows, DesignPartSpec row)
        {
            if (row.defName == "PF_ShipPart_ControlCore_Debug") return "fairing";
            if (row.defName == "PF_ShipPart_Tank_Kerolox_Medium" && row.useExplicitPose
                && row.parentIndex >= 0 && row.parentIndex < rows.Count
                && rows[row.parentIndex]?.defName == "PF_ShipPart_Tank_Kerolox_Heavy") return "booster";
            return "";
        }

        private void BeginAssemblyFrame()
        {
            mountUnderMouse = null;
            var design = comp.Design;
            bool changed = displayedAssembly != design || geometryEntries.Count != design.parts.Count;
            if (!changed)
            {
                for (int i = 0; i < geometryEntries.Count; i++)
                {
                    if (geometryEntries[i] == GeometryEntry.Read(design.parts[i])) continue;
                    changed = true;
                    break;
                }
            }

            if (changed)
            {
                bool newDesign = displayedAssembly != design;
                displayedAssembly = design;
                geometryEntries.Clear();
                foreach (var row in design.parts)
                    geometryEntries.Add(GeometryEntry.Read(row));
                assemblyParts = BuildAssemblyGeometry(design);
                ghostParts.Clear();
                hoveredMount = null;
                if (newDesign)
                {
                    placementNodes.Clear();
                    assemblyViewport.Fit();
                }
                selectedRow = selectedAssemblyPart == null ? -1 : design.parts.IndexOf(selectedAssemblyPart);
                if (selectedRow < 0) selectedAssemblyPart = null;
                recentAssemblyRows.Clear();
                foreach (var part in recentAssemblyParts)
                {
                    int index = design.parts.IndexOf(part);
                    if (index >= 0) recentAssemblyRows.Add(index);
                }
            }
            else if (ghostDef != armedDef || ghostStage != comp.CurrentStage || ghostParallel != comp.CurrentParallel)
            {
                ghostParts.Clear();
                hoveredMount = null;
            }
            FillMountActions();
        }

        private void EndAssemblyFrame()
        {
            if (pointerObscured || armedDef == null) mountUnderMouse = null;
            if (hoveredMount == mountUnderMouse) return;
            hoveredMount = mountUnderMouse;
            ghostDef = armedDef;
            ghostStage = comp.CurrentStage;
            ghostParallel = comp.CurrentParallel;
            shiftedPreview = null;
            ghostParts = hoveredMount is { } action ? BuildGhostGeometry(action) : new();
        }

        private void SetArmedPart(ShipPartDef? def)
        {
            armedDef = def == armedDef ? null : def;
            SelectRow(-1);
            recentAssemblyParts.Clear();
            recentAssemblyRows.Clear();
            assemblyNotice = "";
            FillMountActions();
        }

        private void FillMountActions()
        {
            activeMounts.Clear();
            if (armedDef == null || !comp.CanEditDesign) return;
            var target = selectedAssemblyPart;
            if (target == null)
            {
                activeMounts.Add(new(MountKind.Head, null));
                var last = comp.Design.parts.FindLast(part => part != null && !part.useExplicitPose);
                if (last != null) activeMounts.Add(new(MountKind.After, last));
            }
            else if (!target.useExplicitPose)
            {
                activeMounts.Add(new(MountKind.After, target));
                if (comp.CurrentParallel > 1) activeMounts.Add(new(MountKind.Radial, target));
            }
            else
            {
                activeMounts.Add(new(MountKind.Above, target));
                activeMounts.Add(new(MountKind.Below, target));
            }
        }

        private (int Index, DesignPartSpec? Merge) StackSlot(MountAction action)
        {
            var rows = comp.Design.parts;
            int index = 0;
            if (action.Kind == MountKind.After)
            {
                index = rows.IndexOf(action.Target!);
                if (index < 0) return (-1, null);
                index++;
            }
            while (index < rows.Count && (rows[index] == null || rows[index].useExplicitPose)) index++;
            // Keep new main-stack rows with their predecessor, before any later hanging parts.
            if (index == rows.Count && action.Target != null)
                index = rows.IndexOf(action.Target) + 1;

            DesignPartSpec? merge = index < rows.Count ? rows[index] : null;
            if (merge == null || merge.useExplicitPose || !placementNodes.TryGetValue(merge, out var node)
                || node != action) merge = null;
            return (index, merge);
        }

        private string MountLabel(MountAction action) => action.Kind switch
        {
            MountKind.Head => comp.Design.parts.Count == 0 ? "放置首件" : "机头插入",
            MountKind.After => selectedAssemblyPart == null ? "箭尾追加" : "在此件下方插入",
            MountKind.Radial => $"径向 ×{comp.CurrentParallel}",
            MountKind.Above => "挂到上端",
            _ => "挂到下端"
        };

        private string MountTip(MountAction action)
        {
            string name = action.Target == null ? "" : $" · {AssemblyPartName(action.Target)}";
            int stage = action.Kind is MountKind.Above or MountKind.Below
                ? action.Target!.stageId : comp.CurrentStage;
            string block = action.Kind is MountKind.Above or MountKind.Below ? $" · 块 {action.Target!.blockId}" : "";
            return $"{MountLabel(action)}{name} → {armedDef?.LabelCap} · 级 {stage}{block}";
        }

        private void PlaceAtMount(MountAction action)
        {
            var def = armedDef;
            if (def == null || !comp.CanEditDesign) return;
            var rows = comp.Design.parts;
            int targetIndex = action.Target == null ? -1 : rows.IndexOf(action.Target);
            if (action.Target != null && targetIndex < 0) return;

            int before = rows.Count;
            int added = -1;
            bool success;
            string reason = "";
            switch (action.Kind)
            {
                case MountKind.Head:
                case MountKind.After:
                    var slot = StackSlot(action);
                    success = comp.TryInsertPartAt(def, slot.Index, slot.Merge, out added, out reason);
                    if (success) placementNodes[rows[added]] = action;
                    break;
                case MountKind.Radial:
                    success = comp.AddPartRadial(def, comp.CurrentParallel, targetIndex);
                    if (success) added = before;
                    break;
                default:
                    success = comp.AddPartAxialTo(def, targetIndex, action.Kind == MountKind.Below);
                    if (success) added = before;
                    break;
            }

            if (!success)
            {
                assemblyNotice = reason.Length == 0 ? "当前挂点无法放置零件。" : reason;
                return;
            }

            if (selectedAssemblyPart == null) SelectRow(added);
            recentAssemblyParts.Clear();
            recentAssemblyRows.Clear();
            recentAssemblyParts.Add(rows[added]);
            recentAssemblyRows.Add(added);
            if (action.Kind == MountKind.Radial)
            {
                for (int i = added + 1; i < rows.Count; i++)
                {
                    recentAssemblyParts.Add(rows[i]);
                    recentAssemblyRows.Add(i);
                }
            }
            int count = action.Kind is MountKind.Above or MountKind.Below ? 1 : comp.CurrentParallel;
            string target = action.Target == null ? "" : $" · {AssemblyPartName(action.Target)}";
            assemblyNotice = $"已放置 {def.LabelCap} ×{count} · 级 {rows[added].stageId}{target}";
            ghostParts.Clear();
            hoveredMount = null;
            RevealWorkbenchRow(added);
            RefreshDesignAfterAction();
        }

        private static DesignPartSpec CopyForPreview(DesignPartSpec row) => new()
        {
            defName = row.defName,
            category = row.category,
            quantity = row.quantity,
            parallelCount = row.parallelCount,
            installPosition = row.installPosition,
            stageId = row.stageId,
            blockId = row.blockId,
            useExplicitPose = row.useExplicitPose,
            localPosition = row.localPosition,
            localRotation = row.localRotation,
            parentIndex = row.parentIndex,
            useShapeOverride = row.useShapeOverride,
            shapeOverride = row.shapeOverride
        };

        private List<RocketAssemblyViewport.Part> BuildGhostGeometry(MountAction action)
        {
            var def = armedDef;
            if (def == null) return new();
            var design = comp.Design;
            int target = action.Target == null ? -1 : design.parts.IndexOf(action.Target);
            if (action.Target != null && target < 0) return new();
            var rows = new List<DesignPartSpec>(design.parts);
            int start;
            int skip = 0;
            int rowCount = 1;
            bool stack = action.Kind is MountKind.Head or MountKind.After;
            bool inserted = false;
            if (stack)
            {
                var slot = StackSlot(action);
                start = slot.Index;
                if (start < 0) return new();
                var candidate = slot.Merge;
                if (candidate != null && candidate.defName == def.defName && candidate.category == def.category
                    && candidate.stageId == comp.CurrentStage && RocketDesignRules.ParallelOf(candidate) == comp.CurrentParallel)
                {
                    if (candidate.quantity >= 64) return new();
                    var copy = CopyForPreview(candidate);
                    skip = Math.Max(1, copy.quantity) * comp.CurrentParallel;
                    copy.quantity++;
                    rows[start] = copy;
                }
                else
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        if (row == null || row.parentIndex < start || row.parentIndex == int.MaxValue) continue;
                        var copy = CopyForPreview(row);
                        copy.parentIndex++;
                        rows[i] = copy;
                    }
                    rows.Insert(start, new DesignPartSpec
                    {
                        defName = def.defName,
                        category = def.category,
                        installPosition = def.category,
                        stageId = comp.CurrentStage,
                        parallelCount = comp.CurrentParallel
                    });
                    inserted = true;
                }
            }
            else
            {
                start = rows.Count;
                if (action.Kind == MountKind.Radial)
                {
                    var additions = comp.PreviewRadialParts(def, comp.CurrentParallel, target);
                    rowCount = additions.Count;
                    if (rowCount == 0) return new();
                    rows.AddRange(additions);
                }
                else
                {
                    var addition = comp.PreviewAxialPart(def, target, action.Kind == MountKind.Below);
                    if (addition == null) return new();
                    rows.Add(addition);
                }
            }

            var preview = new RocketDesignSpec
            {
                parts = rows,
                mapWidth = design.mapWidth,
                mapHeight = design.mapHeight,
                keepStageIds = design.keepStageIds,
                recoverableStageIds = design.recoverableStageIds,
                parallelIgnitionStageIds = design.parallelIgnitionStageIds
            };
            var ghosts = new List<RocketAssemblyViewport.Part>();
            if (stack) shiftedPreview = new List<RocketAssemblyViewport.Part>();
            foreach (var part in BuildAssemblyGeometry(preview))
            {
                if (part.Row >= start && part.Row < start + rowCount && skip-- <= 0)
                {
                    ghosts.Add(part with { ParentRow = target });
                }
                else if (stack)
                {
                    shiftedPreview!.Add(part with
                    {
                        Row = inserted && part.Row > start ? part.Row - 1 : part.Row,
                        ParentRow = inserted && part.ParentRow > start ? part.ParentRow - 1 : part.ParentRow
                    });
                }
            }
            return ghosts;
        }

        private void DrawAssemblyViews(Rect rect)
        {
            const float gap = 6f;
            float y = rect.y;
            bool wide = showSideView && rect.width >= 480f;
            float mainWidth = wide ? Mathf.Floor((rect.width - gap) * 0.65f) : rect.width;
            float viewbarH = rect.height >= 150f ? 28f : 24f;
            float mainHeight = showSideView && !wide ? Mathf.Floor((rect.height - gap) * 0.61f) : rect.height;
            var main = new Rect(rect.x, y, mainWidth, mainHeight);
            DrawAssemblyViewHeader(new Rect(main.x, main.y, main.width, viewbarH), false);
            DrawOneAssemblyView(new Rect(main.x, main.y + viewbarH, main.width, Mathf.Max(1, main.height - viewbarH)), false);
            if (!showSideView) return;
            var side = wide
                ? new Rect(main.xMax + gap, y, rect.width - main.width - gap, rect.height)
                : new Rect(rect.x, main.yMax + gap, rect.width, rect.yMax - main.yMax - gap);
            DrawAssemblyViewHeader(new Rect(side.x, side.y, side.width, viewbarH), true);
            DrawOneAssemblyView(new Rect(side.x, side.y + viewbarH, side.width, Mathf.Max(1, side.height - viewbarH)), true);
        }

        private void DrawAssemblyViewHeader(Rect rect, bool planar)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.15f, 0.20f, 0.23f));
            Label(new Rect(rect.x + 6f, rect.y, rect.width - 8f, rect.height), planar ? "二维侧视图" : "三维视口",
                GameFont.Tiny, Color.white, TextAnchor.MiddleLeft);
            if (planar)
            {
                if (Btn(new Rect(rect.xMax - 54f, rect.y + 2f, 52f, rect.height - 4f), "收起"))
                    showSideView = false;
                return;
            }

            float width = rect.width >= 360f ? 64f : 46f;
            float x = rect.xMax - 2f - width;
            Rect ButtonRect()
            {
                var button = new Rect(x, rect.y + 2f, width - 2f, rect.height - 4f);
                x -= width;
                return button;
            }
            if (Btn(ButtonRect(), "看全箭")) assemblyViewport.Fit();
            if (Btn(ButtonRect(), "沿轴")) assemblyViewport.SetView(RocketAssemblyViewport.CameraMode.Axial);
            if (Btn(ButtonRect(), "侧视")) assemblyViewport.SetView(RocketAssemblyViewport.CameraMode.Side);
            if (Btn(ButtonRect(), "斜视")) assemblyViewport.SetView(RocketAssemblyViewport.CameraMode.Oblique);
        }

        private void DrawOneAssemblyView(Rect rect, bool planar)
        {
            if (rect.width < 8f || rect.height < 8f) return;
            var solidParts = hoveredMount != null && shiftedPreview != null ? shiftedPreview : assemblyParts;
            assemblyViewport.Draw(rect, planar, solidParts, ghostParts, selectedRow, recentAssemblyRows, assemblyParts);
            if (editMode && assemblyParts.Count == 0 && armedDef == null)
                Label(rect, comp.Design.parts.Count == 0 ? "从目录选件，或载入 R-7" : "设计中没有可显示的目录件",
                    GameFont.Tiny, Dim, TextAnchor.MiddleCenter);
            if (!planar && (!pointerObscured || assemblyViewport.IsCameraDragging)
                && assemblyViewport.HandleCameraInput(rect)) return;
            if (!editMode) return;
            DrawMountMarkers(rect, planar);
            if (pointerObscured || Event.current.type != EventType.MouseDown || Event.current.button != 0 || !Mouse.IsOver(rect)) return;
            if (assemblyViewport.TryPick(rect, planar, Event.current.mousePosition, out int index))
                SelectRow(index);
            else
                SelectRow(-1);
            FillMountActions();
            Event.current.Use();
        }

        private void DrawMountMarkers(Rect viewRect, bool planar)
        {
            const float markerWidth = 52f;
            const float markerHeight = 22f;
            if (armedDef == null || viewRect.width < markerWidth || viewRect.height < markerHeight) return;
            Rect previous = Rect.zero;
            for (int i = 0; i < activeMounts.Count; i++)
            {
                MountAction action = activeMounts[i];
                Vector2 point;
                if (action.Target == null && assemblyParts.Count == 0)
                    point = viewRect.center;
                else if (!TryMountPosition(action, out Vector3 position)
                    || !assemblyViewport.TryProject(viewRect, planar, position, out point))
                    continue;

                string label = action.Kind switch
                {
                    MountKind.Head => assemblyParts.Count == 0 ? "＋ 首件" : "＋ 机头",
                    MountKind.After => "＋ 下",
                    MountKind.Radial => "径向",
                    MountKind.Above => "＋ 上",
                    _ => "＋ 下"
                };
                var marker = new Rect(
                    Mathf.Clamp(point.x - markerWidth * 0.5f, viewRect.xMin, viewRect.xMax - markerWidth),
                    Mathf.Clamp(point.y - markerHeight * 0.5f, viewRect.yMin, viewRect.yMax - markerHeight),
                    markerWidth, markerHeight);
                if (previous.width > 0 && marker.Overlaps(previous))
                {
                    if (previous.xMax + markerWidth + 2f <= viewRect.xMax)
                        marker.x = previous.xMax + 2f;
                    else if (previous.xMin - markerWidth - 2f >= viewRect.xMin)
                        marker.x = previous.xMin - markerWidth - 2f;
                    else if (previous.yMax + markerHeight + 2f <= viewRect.yMax)
                        marker.y = previous.yMax + 2f;
                    else if (previous.yMin - markerHeight - 2f >= viewRect.yMin)
                        marker.y = previous.yMin - markerHeight - 2f;
                    else
                        continue;
                }
                previous = marker;
                if (DrawMountButton(marker, label, action)) return;
            }
        }

        private bool TryMountPosition(MountAction action, out Vector3 position)
        {
            position = Vector3.zero;
            if (action.Target == null)
            {
                if (assemblyParts.Count == 0) return false;
                var first = assemblyParts[0];
                position = first.Center + Vector3.forward * (first.Size.z * 0.5f + 1.6f);
                return true;
            }
            int index = comp.Design.parts.IndexOf(action.Target);
            int firstIndex = assemblyParts.FindIndex(part => part.Row == index);
            if (firstIndex < 0) return false;
            var picked = assemblyParts[firstIndex];
            if (action.Kind == MountKind.After)
            {
                int last = assemblyParts.FindLastIndex(part => part.Row == index);
                picked = assemblyParts[last];
            }
            position = picked.Center + (action.Kind == MountKind.Radial
                ? Vector3.right * (picked.Size.x * 0.5f + 2f)
                : Vector3.forward * ((picked.Size.z * 0.5f + 1.2f) * (action.Kind == MountKind.Above ? 1f : -1f)));
            return true;
        }

        private bool DrawMountButton(Rect rect, string label, MountAction action)
        {
            if (!pointerObscured && Mouse.IsOver(rect)) mountUnderMouse = action;
            TooltipHandler.TipRegion(rect, MountTip(action));
            if (!Btn(rect, label, !pointerObscured)) return false;
            PlaceAtMount(action);
            return true;
        }

        private void DrawAssemblyMountBar(Rect rect)
        {
            Panel(rect);
            float x = rect.x + 7f;
            float width = rect.width - 14f;
            string selection = selectedAssemblyPart == null ? "点击部件锁定挂点" : AssemblyPartName(selectedAssemblyPart);
            if (selectedAssemblyPart is { parentIndex: >= 0 } && !assemblyParts.Any(part => part.Row == selectedRow && part.ParentRow >= 0))
                selection += " · 父件不可用";
            string heading = armedDef == null ? selection : $"待放置 {armedDef.LabelCap}  ·  {selection}";
            var title = new Rect(x, rect.y + 3f, width, 20f);
            Label(title, heading, GameFont.Tiny, armedDef == null ? Dim : Gold, TextAnchor.MiddleLeft);
            TooltipHandler.TipRegion(title, heading);
            if (armedDef != null)
            {
                float gap = 3f;
                float buttonWidth = Mathf.Min(119f, (width - activeMounts.Count * gap) / (activeMounts.Count + 1));
                float bx = x;
                for (int i = 0; i < activeMounts.Count; i++)
                {
                    MountAction action = activeMounts[i];
                    if (DrawMountButton(new Rect(bx, rect.y + 24f, buttonWidth, 25f), MountLabel(action), action)) break;
                    bx += buttonWidth + gap;
                }
                if (Btn(new Rect(rect.xMax - 7f - buttonWidth, rect.y + 24f, buttonWidth, 25f), "取消放置", !pointerObscured))
                    SetArmedPart(null);
            }
            else if (armedDef == null)
            {
                Label(new Rect(x, rect.y + 24f, width, 21f), "右键旋转 · 中键平移 · 滚轮缩放 · Delete 删除",
                    GameFont.Tiny, Faint);
            }
            int designed = comp.TotalPartCount;
            bool incomplete = assemblyParts.Count < designed;
            string notice = incomplete ? $"可派生 {assemblyParts.Count}/{designed} 件 · {assemblyNotice}" : assemblyNotice;
            var footer = new Rect(x, rect.y + 52f, width, 22f);
            Label(footer, notice, GameFont.Tiny, incomplete ? Red : Dim);
            if (incomplete)
                TooltipHandler.TipRegion(footer, "部分零件超过派生上限，或缺少目录定义。\n" + notice);
        }

        private bool pointerObscured;

        private bool HasAssemblyKeyboardFocus()
        {
            var windows = Find.WindowStack.Windows;
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                var window = windows[i];
                if (ReferenceEquals(window, this)) return true;
                if (window.focusWhenOpened || window is FloatMenu or Dialog_MessageBox) return false;
            }
            return false;
        }

        private void HandleAssemblyDeleteInput()
        {
            var stack = Find.WindowStack;
            bool acceptsInput = stack.GetsInput(this);
            pointerObscured = !acceptsInput || stack.MouseObscuredNow;
            if (!acceptsInput) assemblyViewport.ReleaseGesture();
            var ev = Event.current;
            if (!editMode || !acceptsInput || ev.type != EventType.KeyDown || ev.keyCode != KeyCode.Delete
                || !HasAssemblyKeyboardFocus()
                || !string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()) || GUIUtility.keyboardControl != 0
                || selectedRow < 0 || !comp.CanEditDesign) return;
            RequestDeletion(selectedRow);
            ev.Use();
        }

        private void RequestDeletion(int index)
        {
            var rows = comp.Design.parts;
            if (!comp.CanEditDesign || index < 0 || index >= rows.Count || rows[index] is not { } part) return;
            var removal = comp.GetDeletionGroup(part);
            if (removal.Count == 0) return;
            var confirmed = removal.Select(GeometryEntry.Read).ToArray();
            int attachments = removal.Where(row => !ReferenceEquals(row, part)).Sum(RocketDesignRules.PartCountOf);
            void Remove()
            {
                if (comp.parent.Destroyed
                    || !comp.GetDeletionGroup(part).Select(GeometryEntry.Read).SequenceEqual(confirmed)
                    || !comp.TryDeleteGroup(part, removal))
                {
                    Messages.Message("设计已变化，请重新选择部件再删除。", MessageTypeDefOf.RejectInput, false);
                    return;
                }
                foreach (var removed in removal) placementNodes.Remove(removed);
                SelectRow(-1);
                recentAssemblyParts.Clear();
                recentAssemblyRows.Clear();
                assemblyNotice = $"已删除 {AssemblyPartName(part)}" + (attachments > 0 ? $"及 {attachments} 件挂件。" : "。");
                RefreshDesignAfterAction();
            }
            if (attachments > 0)
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    $"删除「{AssemblyPartName(part)}」会一并删除 {attachments} 件挂件。确定？", Remove, true));
            else
                Remove();
        }

        private void RequestEditablePreset() => RequestPreset(false);

        private void RequestDevPreset() => RequestPreset(true);

        private void RequestPreset(bool finish)
        {
            if (!comp.CanEditDesign || (finish && !Prefs.DevMode)) return;
            var current = comp.Design;
            var before = current.parts.Select(GeometryEntry.Read).ToArray();
            void Apply()
            {
                if (comp.parent.Destroyed || !comp.CanEditDesign || (finish && !Prefs.DevMode)
                    || comp.Design != current || !current.parts.Select(GeometryEntry.Read).SequenceEqual(before))
                {
                    Messages.Message("设计已变化，请重新确认载入。", MessageTypeDefOf.RejectInput, false);
                    return;
                }
                bool ok = finish ? comp.QuickAssembleDemoRocket(out string reason) : comp.TryLoadEditableR7(out reason);
                if (!ok)
                {
                    Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                    return;
                }
                armedDef = null;
                SelectRow(-1);
                recentAssemblyParts.Clear();
                recentAssemblyRows.Clear();
                assemblyViewport.SetView(RocketAssemblyViewport.CameraMode.Oblique);
                assemblyNotice = finish ? "R-7 已完工。" : "已载入可编辑 R-7 · 四组径向助推器";
                editMode = !finish;
                RefreshDesignAfterAction();
            }
            if (before.Length == 0)
                Apply();
            else
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    finish ? "生成并完工 R-7 会覆盖当前设计。确定？" : "载入 R-7 会覆盖当前设计。确定？", Apply, true));
        }

        private void RequestClearDesign()
        {
            if (!comp.CanEditDesign || comp.Design.parts.Count == 0) return;
            var current = comp.Design;
            var before = current.parts.Select(GeometryEntry.Read).ToArray();
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("清空当前设计？", () =>
            {
                if (comp.parent.Destroyed || !comp.CanEditDesign || comp.Design != current
                    || !current.parts.Select(GeometryEntry.Read).SequenceEqual(before)) return;
                comp.ClearDesign();
                SelectRow(-1);
                armedDef = null;
                recentAssemblyParts.Clear();
                recentAssemblyRows.Clear();
                assemblyNotice = "";
                RefreshDesignAfterAction();
            }, true));
        }

        private void RefreshDesignAfterAction()
        {
            Recompute();
            if (editMode) BeginAssemblyFrame();
        }
    }
}
