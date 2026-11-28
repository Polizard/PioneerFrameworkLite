using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    public partial class Window_RocketAssemblerC
    {
        private const float WorkbenchGap = 8f;
        private const float WorkbenchRowHeight = 38f;
        private const float WorkbenchScrollBarWidth = 16f;
        private static readonly Color WorkbenchGood = new Color32(158, 213, 206, 255);
        private static readonly Color WorkbenchWarning = new Color32(255, 193, 168, 255);
        private static readonly Color WorkbenchSelection = new Color32(234, 201, 137, 32);
        private static readonly Color WorkbenchProblem = new Color32(255, 150, 125, 18);

        private Vector2 workbenchCatalogScroll;
        private Vector2 workbenchAssemblyScroll;
        private Vector2 workbenchCheckScroll;
        private bool workbenchShowPassed;
        private int workbenchRevealRow = -1;
        private DesignPartSpec? workbenchDraggedPart;

        private readonly record struct WorkbenchCheck(string Text, string Tip, bool Pass, int Row = -1);

        private void DrawWorkbench(Rect inRect)
        {
            const float margin = 12f;
            const float mountHeight = 76f;
            Rect footer = new(margin, inRect.yMax - 46f, inRect.width - margin * 2f, 38f);
            Rect body = new(margin, inRect.y + BodyY, footer.width, footer.y - inRect.y - BodyY - WorkbenchGap);
            var checks = GetWorkbenchChecks();
            if (body.width < 520f || body.height < 232f)
            {
                Label(body, "扩大窗口以显示装配区", GameFont.Small, Dim, TextAnchor.MiddleCenter);
                DrawWorkbenchFooter(footer, checks.Count(check => !check.Pass));
                return;
            }

            float catalogWidth = Mathf.Clamp(body.width * .175f, 125f, 224f);
            float railWidth = Mathf.Clamp(body.width * .255f, 184f, 316f);
            Rect catalog = new(body.x, body.y, catalogWidth, body.height);
            Rect rail = new(body.xMax - railWidth, body.y, railWidth, body.height);
            Rect center = new(catalog.xMax + WorkbenchGap, body.y,
                rail.x - catalog.xMax - 2f * WorkbenchGap, body.height);
            Rect views = new(center.x, center.y, center.width, center.height - mountHeight - WorkbenchGap);
            Rect mount = new(center.x, views.yMax + WorkbenchGap, center.width, mountHeight);

            DrawWorkbenchCatalog(catalog);
            DrawWorkbenchAssembly(rail, checks);
            DrawAssemblyViews(views);
            DrawAssemblyMountBar(mount);
            DrawWorkbenchFooter(footer, checks.Count(check => !check.Pass));
        }

        private void DrawWorkbenchCatalog(Rect panel)
        {
            Panel(panel);
            Rect content = panel.ContractedBy(8f);
            Label(new Rect(content.x, content.y, content.width, 22f), "零件目录", GameFont.Small, Color.white);

            float controlWidth = (content.width - 6f) / 2f;
            Rect stage = new(content.x, content.y + 40f, controlWidth, 24f);
            Rect parallel = new(stage.xMax + 6f, stage.y, controlWidth, stage.height);
            Label(new Rect(stage.x, stage.y - 17f, stage.width, 16f), "加入级", GameFont.Tiny, Dim);
            Label(new Rect(parallel.x, parallel.y - 17f, parallel.width, 16f), "并联", GameFont.Tiny, Dim);
            if (Btn(stage, $"级 {comp.CurrentStage}", comp.CanEditDesign)) OpenWorkbenchStageMenu(null);
            if (Btn(parallel, $"×{comp.CurrentParallel}", comp.CanEditDesign)) OpenWorkbenchParallelMenu();

            int categoryColumns = content.width >= 120f ? 3 : 2;
            float categoryWidth = (content.width + 4f) / categoryColumns - 4f;
            float categoryY = stage.yMax + 9f;
            for (int i = 0; i < ShelfCategories.Length; i++)
            {
                Rect category = new(content.x + (i % categoryColumns) * (categoryWidth + 4f),
                    categoryY + (i / categoryColumns) * 26f, categoryWidth, 22f);
                if (Chip(category, ShelfCategories[i], shelfCategory == i))
                {
                    shelfCategory = i;
                    workbenchCatalogScroll = Vector2.zero;
                }
            }

            var parts = FilteredCatalog();
            float listY = categoryY + ((ShelfCategories.Length + categoryColumns - 1) / categoryColumns) * 26f + 4f;
            Rect list = new(content.x, listY, content.width, content.yMax - listY);
            if (parts.Count == 0)
            {
                Label(list, "此分类暂无零件", GameFont.Tiny, Dim);
                return;
            }

            const float cardHeight = 48f;
            Rect scrollContent = WorkbenchScrollContent(list, parts.Count * cardHeight);
            Widgets.BeginScrollView(list, ref workbenchCatalogScroll, scrollContent);
            for (int i = 0; i < parts.Count; i++)
            {
                float y = i * cardHeight;
                if (y + cardHeight < workbenchCatalogScroll.y || y > workbenchCatalogScroll.y + list.height)
                    continue;

                ShipPartDef def = parts[i];
                bool armed = def == armedDef;
                Rect card = new(0f, y, scrollContent.width, cardHeight - 4f);
                Widgets.DrawBoxSolid(card, armed ? WorkbenchSelection : Mouse.IsOver(card) ? Hover : PanelFill);
                Border(card, armed ? Gold : Line);
                string name = def.label ?? def.defName;
                string info = WorkbenchPartReadout(def);
                Widgets.DrawBoxSolid(new Rect(card.x + 7f, card.y + 10f, 3f, 24f),
                    armed ? Gold : WorkbenchCategoryColor(def.category));
                WorkbenchSingleLine(new Rect(16f, card.y + 2f, card.width - 21f, 22f), name,
                    GameFont.Small, armed ? Gold : Color.white);
                WorkbenchSingleLine(new Rect(16f, card.y + 24f, card.width - 21f, 17f), info, GameFont.Tiny, Dim);
                TooltipHandler.TipRegion(card, $"{name}\n{info} · {def.massKg / 1000f:0.##} t\n{def.description}");
                if (comp.CanEditDesign && Widgets.ButtonInvisible(card)) SetArmedPart(armed ? null : def);
            }
            Widgets.EndScrollView();
        }

        private void DrawWorkbenchAssembly(Rect panel, List<WorkbenchCheck> checks)
        {
            Panel(panel);
            Rect content = panel.ContractedBy(8f);
            float listHeight = Mathf.Clamp((content.height - 143f) * .47f, 48f, 240f);
            Rect list = new(content.x, content.y + 28f, content.width, listHeight);
            Rect inspection = new(content.x, list.yMax + 4f, content.width, 81f);
            float checkTop = inspection.yMax + 5f;
            Rect validation = new(content.x, checkTop, content.width, content.yMax - checkTop);

            Label(new Rect(content.x, content.y, content.width - 88f, 23f), "装配清单", GameFont.Small, Color.white);
            Label(new Rect(content.xMax - 86f, content.y, 62f, 23f), $"{comp.TotalPartCount} 件",
                GameFont.Tiny, Dim, TextAnchor.MiddleRight);
            Rect menu = new(content.xMax - 20f, content.y, 20f, 22f);
            if (comp.CanEditDesign && Micro(menu, "⋯")) OpenWorkbenchOptions();
            TooltipHandler.TipRegion(menu, "装配与级设置");

            DrawWorkbenchAssemblyRows(list);
            DrawWorkbenchInspection(inspection);
            DrawWorkbenchChecks(validation, checks);
        }

        private void DrawWorkbenchAssemblyRows(Rect viewport)
        {
            List<DesignPartSpec> parts = comp.Design.parts;
            if (parts.Count == 0)
            {
                workbenchDraggedPart = null;
                Label(new Rect(viewport.x, viewport.y + 5f, viewport.width, 24f), "暂无装配件", GameFont.Tiny, Dim);
                return;
            }

            Rect scrollContent = WorkbenchScrollContent(viewport, parts.Count * WorkbenchRowHeight);
            Rect dropZone = new(viewport.x, viewport.y, scrollContent.width, viewport.height);
            int dragTarget = HandleWorkbenchReorder(dropZone);
            int dragSource = workbenchDraggedPart != null ? parts.IndexOf(workbenchDraggedPart) : -1;
            bool pointerInViewport = Mouse.IsOver(dropZone);
            if (workbenchRevealRow >= 0 && workbenchRevealRow < parts.Count)
            {
                float top = workbenchRevealRow * WorkbenchRowHeight;
                float bottom = top + WorkbenchRowHeight;
                if (top < workbenchAssemblyScroll.y) workbenchAssemblyScroll.y = top;
                else if (bottom > workbenchAssemblyScroll.y + viewport.height)
                    workbenchAssemblyScroll.y = bottom - viewport.height;
            }
            workbenchRevealRow = -1;
            workbenchAssemblyScroll.y = Mathf.Clamp(workbenchAssemblyScroll.y, 0f, scrollContent.height - viewport.height);

            Widgets.BeginScrollView(viewport, ref workbenchAssemblyScroll, scrollContent);
            for (int i = 0; i < parts.Count; i++)
            {
                float y = i * WorkbenchRowHeight;
                if (y + WorkbenchRowHeight < workbenchAssemblyScroll.y || y > workbenchAssemblyScroll.y + viewport.height)
                    continue;

                DesignPartSpec? part = parts[i];
                if (part == null) continue;

                Rect row = new(0f, y, scrollContent.width, WorkbenchRowHeight - 2f);
                bool selected = i == selectedRow;
                bool offender = IsOffender(i);
                bool recent = recentAssemblyRows.Contains(i);
                Widgets.DrawBoxSolid(row, selected || recent ? WorkbenchSelection : offender ? WorkbenchProblem
                    : Mouse.IsOver(row) ? Hover : PanelFill);
                if (selected || recent || offender)
                    Widgets.DrawBoxSolid(new Rect(0f, row.y, 2f, row.height), selected || recent ? Gold : WorkbenchWarning);

                string name = WorkbenchPartName(part);
                string detail = $"级 {part.stageId} · 分离块 {part.blockId}";
                WorkbenchSingleLine(new Rect(7f, y, row.width - 62f, 20f), name, GameFont.Small,
                    selected ? Gold : Color.white);
                WorkbenchSingleLine(new Rect(7f, y + 19f, row.width - 32f, 16f), detail, GameFont.Tiny,
                    offender ? WorkbenchWarning : Dim);
                Label(new Rect(row.width - 54f, y, 28f, 20f), $"×{RocketDesignRules.PartCountOf(part)}",
                    GameFont.Tiny, Dim, TextAnchor.MiddleRight);

                Rect select = new(row.x, row.y, row.width - 23f, row.height);
                TooltipHandler.TipRegion(select, $"{name}\n{detail}");
                if (Widgets.ButtonInvisible(select)) SelectRow(i);

                if (comp.CanEditDesign)
                {
                    Rect handle = new(row.xMax - 22f, row.y, 22f, row.height);
                    Label(handle, "≡", GameFont.Small, Mouse.IsOver(handle) ? Color.white : Dim, TextAnchor.MiddleCenter);
                    TooltipHandler.TipRegion(handle, "拖动调整装配顺序");
                    Event? mouse = Event.current;
                    if (mouse?.type == EventType.MouseDown && mouse.button == 0 && pointerInViewport && Mouse.IsOver(handle))
                    {
                        workbenchDraggedPart = part;
                        SelectRow(i);
                        mouse.Use();
                    }
                }
                if (i == dragTarget && dragSource != dragTarget)
                    Widgets.DrawBoxSolid(new Rect(row.x, dragSource < dragTarget ? row.yMax : row.y, row.width, 2f), Gold);
            }
            Widgets.EndScrollView();
        }

        private int HandleWorkbenchReorder(Rect dropZone)
        {
            if (workbenchDraggedPart == null) return -1;

            var parts = comp.Design.parts;
            int from = parts.IndexOf(workbenchDraggedPart);
            if (from < 0 || !comp.CanEditDesign)
            {
                workbenchDraggedPart = null;
                return -1;
            }

            Event? mouse = Event.current;
            if (mouse == null) return -1;
            int target = Mouse.IsOver(dropZone)
                ? Mathf.Clamp(Mathf.FloorToInt((mouse.mousePosition.y - dropZone.y + workbenchAssemblyScroll.y)
                    / WorkbenchRowHeight), 0, parts.Count - 1)
                : -1;
            if (mouse.type == EventType.MouseDown)
            {
                workbenchDraggedPart = null;
                return -1;
            }
            if (mouse.rawType != EventType.MouseUp || mouse.button != 0)
            {
                if (mouse.type == EventType.MouseDrag && mouse.button == 0) mouse.Use();
                return target;
            }

            workbenchDraggedPart = null;
            if (target >= 0 && comp.MovePart(from, target - from)) SelectRow(target);
            if (mouse.type != EventType.Used) mouse.Use();
            return -1;
        }

        private void RevealWorkbenchRow(int index)
        {
            workbenchRevealRow = index;
        }

        private void DrawWorkbenchInspection(Rect rect)
        {
            DrawLine(rect.x, rect.y, rect.xMax, rect.y, Line);
            var parts = comp.Design.parts;
            int index = selectedRow;
            DesignPartSpec? part = index >= 0 && index < parts.Count ? parts[index] : null;
            if (part == null)
            {
                Label(new Rect(rect.x, rect.y + 6f, rect.width, 42f),
                    "点击视口或清单中的部件", GameFont.Tiny, Dim);
                return;
            }

            string name = WorkbenchPartName(part);
            ShipPartDef? def = WorkbenchFindPart(part);
            Rect title = new(rect.x, rect.y + 3f, rect.width, 21f);
            WorkbenchSingleLine(title, name, GameFont.Small, Gold);
            TooltipHandler.TipRegion(title, def?.description ?? name);

            string relationship = part.parentIndex < 0 ? "独立部件"
                : part.parentIndex != index && part.parentIndex < parts.Count
                    && parts[part.parentIndex] is { } parent
                    ? $"挂接 {WorkbenchPartName(parent)}"
                    : $"无效挂接（第 {part.parentIndex + 1} 行）";
            Rect mount = new(rect.x, title.yMax, rect.width, 16f);
            WorkbenchSingleLine(mount, relationship, GameFont.Tiny, Dim);
            TooltipHandler.TipRegion(mount, relationship);

            string info = def != null ? WorkbenchPartReadout(def) : "缺少零件定义";
            string stats = $"{info} · {RocketDesignRules.StationCountOf(part)} 站 × {RocketDesignRules.ParallelOf(part)} 并联";
            Rect metrics = new(rect.x, mount.yMax, rect.width, 17f);
            WorkbenchSingleLine(metrics, stats, GameFont.Tiny, Dim);
            TooltipHandler.TipRegion(metrics, def != null ? $"{stats}\n单件质量 {def.massKg / 1000f:0.##} t" : stats);

            float actionWidth = (rect.width - 12f) / 4f;
            Rect up = new(rect.x, metrics.yMax + 2f, actionWidth, 21f);
            Rect down = new(up.xMax + 4f, up.y, actionWidth, up.height);
            Rect stage = new(down.xMax + 4f, up.y, actionWidth, up.height);
            Rect delete = new(stage.xMax + 4f, up.y, actionWidth, up.height);
            bool editable = comp.CanEditDesign;
            if (Btn(up, "上移", editable && index > 0) && comp.MovePart(index, -1)) SelectRow(index - 1);
            if (Btn(down, "下移", editable && index + 1 < parts.Count) && comp.MovePart(index, 1)) SelectRow(index + 1);
            if (Btn(stage, "换级", editable)) OpenWorkbenchStageMenu(part);
            if (Btn(delete, "删除", editable, true)) RequestDeletion(index);
            TooltipHandler.TipRegion(delete, "Delete");
        }

        private void OpenWorkbenchStageMenu(DesignPartSpec? part)
        {
            var options = new List<FloatMenuOption>();
            int current = part?.stageId ?? comp.CurrentStage;
            for (int stage = 0; stage <= CompRocketAssembler.MaxStageId; stage++)
            {
                int target = stage;
                string label = $"级 {stage}" + (stage == current ? " ✓" : "");
                options.Add(new FloatMenuOption(label, () =>
                {
                    if (!comp.CanEditDesign) return;
                    if (part == null)
                    {
                        comp.SetCurrentStage(target);
                        return;
                    }
                    int index = comp.Design.parts.IndexOf(part);
                    if (comp.SetPartStage(index, target)) SelectRow(index);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void OpenWorkbenchParallelMenu()
        {
            var options = new List<FloatMenuOption>();
            for (int count = 1; count <= ParallelLayout.MaxCount; count++)
            {
                int target = count;
                options.Add(new FloatMenuOption($"×{count}" + (count == comp.CurrentParallel ? " ✓" : ""),
                    () => comp.SetCurrentParallel(target)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void OpenWorkbenchOptions()
        {
            var options = new List<FloatMenuOption> { new("反转级号", () => comp.ReverseStages()) };
            var parts = comp.Design.parts;
            if (selectedRow >= 0 && selectedRow < parts.Count && parts[selectedRow] is { } part)
            {
                int stage = part.stageId;
                options.Add(new FloatMenuOption($"级 {stage} · 不抛弃" + (comp.IsStageKeep(stage) ? " ✓" : ""),
                    () => comp.SetStageKeep(stage, !comp.IsStageKeep(stage))));
                options.Add(new FloatMenuOption($"级 {stage} · 可回收" + (comp.IsStageRecoverable(stage) ? " ✓" : ""),
                    comp.IsStageKeep(stage) ? null : () => comp.SetStageRecoverable(stage, !comp.IsStageRecoverable(stage))));
                options.Add(new FloatMenuOption($"级 {stage} · 并联点火" + (comp.IsStageParallel(stage) ? " ✓" : ""),
                    () => comp.SetStageParallel(stage, !comp.IsStageParallel(stage))));
                options.Add(new FloatMenuOption("径向复制 ×4", () =>
                {
                    int index = comp.Design.parts.IndexOf(part);
                    if (comp.CopyRadialFromDesignWindow(index)) SelectRow(-1);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private List<WorkbenchCheck> GetWorkbenchChecks()
        {
            var checks = new List<WorkbenchCheck>();
            foreach (CheckRow check in GetBuildChecks())
            {
                switch (check.Kind)
                {
                    case BuildCheckKind.Editability:
                        checks.Add(new WorkbenchCheck(check.Pass ? "设计可编辑" : "当前无法编辑设计", check.Detail, check.Pass));
                        break;
                    case BuildCheckKind.Design:
                        int engines = comp.CountInDesign("thruster");
                        int tanks = comp.CountInDesign("tank");
                        checks.Add(new WorkbenchCheck(engines > 0 ? $"主引擎 ×{engines}" : "缺少主引擎",
                            "设计需要至少一个主引擎。", engines > 0));
                        checks.Add(new WorkbenchCheck(tanks > 0 ? $"工质箱 ×{tanks}" : "缺少工质箱",
                            "设计需要至少一个主工质箱。", tanks > 0));
                        if (!check.Pass && (comp.Design.frameSize < 1 || (engines > 0 && tanks > 0)))
                            checks.Add(new WorkbenchCheck(check.Detail, check.Detail, false));
                        break;
                    case BuildCheckKind.Control:
                        checks.Add(new WorkbenchCheck(check.Pass ? $"控制核心 {check.Detail}" : "缺少控制核心",
                            "设计需要至少一个控制核心。", check.Pass));
                        break;
                    case BuildCheckKind.EnginePlacement:
                        AddWorkbenchRowChecks(checks, check, "主引擎位于本级末端", "排在本级引擎之后", placementOffenders);
                        break;
                    case BuildCheckKind.StageContiguity:
                        AddWorkbenchRowChecks(checks, check, "同一级的堆叠条目连续", "与同级条目隔开", contiguityOffenders);
                        break;
                    case BuildCheckKind.ActiveStage:
                        AddWorkbenchStageChecks(checks);
                        break;
                    case BuildCheckKind.Work:
                        checks.Add(new WorkbenchCheck(check.Pass ? $"工时 {check.Detail}" : "没有可建造的内容",
                            check.Detail, check.Pass));
                        break;
                    default:
                        checks.Add(new WorkbenchCheck(check.Pass ? check.Label : check.Detail, check.Detail, check.Pass));
                        break;
                }
            }
            return checks;
        }

        private void AddWorkbenchStageChecks(List<WorkbenchCheck> checks)
        {
            if (comp.TotalPartCount == 0) return;
            var parts = comp.Design.parts;
            string stageSummary = comp.StageDiagnosis();
            foreach (int stage in comp.ActiveStages.OrderByDescending(stage => stage))
            {
                bool hasEngine = comp.EngineCountInStage(stage) > 0;
                bool hasTank = comp.TankCountInStage(stage) > 0;
                string reason = hasEngine ? "工质箱" : hasTank ? "主引擎" : "主引擎、工质箱";
                bool pass = hasEngine && hasTank;
                checks.Add(new WorkbenchCheck(pass ? $"级 {stage} 可以点火" : $"级 {stage} 缺少{reason}",
                    stageSummary, pass, parts.FindIndex(part => part != null && part.stageId == stage)));
            }
        }

        private void AddWorkbenchRowChecks(List<WorkbenchCheck> checks, CheckRow check, string success,
            string description, List<int> offenders)
        {
            if (check.Pass || offenders.Count == 0)
            {
                checks.Add(new WorkbenchCheck(check.Pass ? success : check.Detail, check.Detail, check.Pass));
                return;
            }

            foreach (int index in offenders)
            {
                var part = comp.Design.parts[index];
                string text = $"级 {part.stageId} · 第 {index + 1} 行{description}";
                string tip = index == offenders[0] ? check.Detail : $"{WorkbenchPartName(part)}{description}。";
                checks.Add(new WorkbenchCheck(text, tip, false, index));
            }
        }

        private void DrawWorkbenchChecks(Rect rect, List<WorkbenchCheck> checks)
        {
            DrawLine(rect.x, rect.y, rect.xMax, rect.y, Line);
            int failures = checks.Count(check => !check.Pass);
            Label(new Rect(rect.x, rect.y + 3f, 78f, 23f), "建造检查", GameFont.Small, Color.white);
            Label(new Rect(rect.x + 78f, rect.y + 3f, rect.width - 78f, 23f),
                failures > 0 ? $"{failures} 项未通过" : "全部通过", GameFont.Tiny,
                failures > 0 ? WorkbenchWarning : WorkbenchGood, TextAnchor.MiddleRight);

            Rect body = new(rect.x, rect.y + 27f, rect.width, rect.height - 27f);
            if (body.height <= 0f) return;
            if (failures == 0 && !workbenchShowPassed) workbenchCheckScroll = Vector2.zero;
            int passed = checks.Count - failures;
            bool showPassed = workbenchShowPassed;
            float width = body.width - WorkbenchScrollBarWidth;
            var heights = new float[checks.Count];
            float contentHeight = 25f;
            for (int i = 0; i < checks.Count; i++)
            {
                WorkbenchCheck check = checks[i];
                if (check.Pass && !showPassed) continue;
                heights[i] = WorkbenchCheckHeight(check, width);
                contentHeight += heights[i];
            }

            Rect scrollContent = new(0f, 0f, width, Mathf.Max(body.height, contentHeight));
            Widgets.BeginScrollView(body, ref workbenchCheckScroll, scrollContent);
            float y = 0f;
            for (int i = 0; i < checks.Count; i++)
            {
                if (!checks[i].Pass) DrawWorkbenchCheck(checks[i], width, heights[i], ref y);
            }

            Rect expand = new(0f, y, width, 24f);
            string arrow = showPassed ? "▾" : "▸";
            if (Chip(expand, $"{arrow} 已通过项（{passed}）", showPassed)) workbenchShowPassed = !showPassed;
            y += 25f;
            if (showPassed)
            {
                for (int i = 0; i < checks.Count; i++)
                {
                    if (checks[i].Pass) DrawWorkbenchCheck(checks[i], width, heights[i], ref y);
                }
            }
            Widgets.EndScrollView();
        }

        private static float WorkbenchCheckHeight(WorkbenchCheck check, float width)
        {
            GameFont font = Text.Font;
            bool wrap = Text.WordWrap;
            Text.Font = GameFont.Tiny;
            Text.WordWrap = true;
            float height = Text.CalcHeight(check.Text, width - 36f);
            Text.Font = font;
            Text.WordWrap = wrap;
            return Mathf.Max(25f, height + 6f);
        }

        private void DrawWorkbenchCheck(WorkbenchCheck check, float width, float height, ref float y)
        {
            Rect row = new(0f, y, width, height - 2f);
            bool locate = !check.Pass && check.Row >= 0;
            Color accent = check.Pass ? WorkbenchGood : WorkbenchWarning;
            if (locate && Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, Hover);
            Label(new Rect(3f, y, 15f, row.height), check.Pass ? "✓" : "!", GameFont.Tiny, accent, TextAnchor.MiddleCenter);
            Label(new Rect(20f, y, width - 36f, row.height), check.Text, GameFont.Tiny, accent, TextAnchor.MiddleLeft);
            if (locate)
            {
                Label(new Rect(width - 15f, y, 14f, row.height), "›", GameFont.Small, accent, TextAnchor.MiddleCenter);
                if (Widgets.ButtonInvisible(row)) SelectRow(check.Row);
            }
            TooltipHandler.TipRegion(row, string.IsNullOrEmpty(check.Tip) ? check.Text : check.Text + "\n" + check.Tip);
            y += height;
        }

        private void DrawWorkbenchFooter(Rect rect, int failures)
        {
            DrawLine(rect.x, rect.y - 3f, rect.xMax, rect.y - 3f, Line);
            Rect build = new(rect.xMax - 106f, rect.y + 3f, 106f, 31f);
            bool ready = failures == 0 && comp.CanEditDesign;
            if (Btn(build, "开始建造", ready))
            {
                if (comp.StartBuild(out string reason)) ShowView();
                else Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
            }
            if (ready) Border(build, Gold);
            else TooltipHandler.TipRegion(build, "查看未通过的建造检查");

            float x = build.x - 6f;
            if (Prefs.DevMode && comp.CanEditDesign)
            {
                Rect demo = new(x - 146f, build.y, 146f, build.height);
                if (Btn(demo, "生成并完工 R-7")) RequestDevPreset();
                x = demo.x - 6f;
            }
            if (comp.Design.parts.Count > 0)
            {
                Rect clear = new(x - 86f, build.y, 86f, build.height);
                if (Btn(clear, "清空设计", comp.CanEditDesign)) RequestClearDesign();
                x = clear.x - 6f;
                Rect materials = new(x - 100f, build.y, 100f, build.height);
                if (Btn(materials, "PF_AssemblyMaterials".Translate())) RocketAssemblyMaterialsUi.ShowQuote(comp);
                x = materials.x - 6f;
            }
            if (x > rect.x + 90f)
            {
                string state = failures == 0 ? "设计中 · 可以开始建造" : $"设计中 · {failures} 项未通过";
                WorkbenchSingleLine(new Rect(rect.x + 2f, build.y, x - rect.x - 2f, build.height), state,
                    GameFont.Small, failures == 0 ? WorkbenchGood : Dim);
            }
        }

        private static Rect WorkbenchScrollContent(Rect viewport, float height)
        {
            return new Rect(0f, 0f, viewport.width - (height > viewport.height ? WorkbenchScrollBarWidth : 0f),
                Mathf.Max(viewport.height, height));
        }

        private static void WorkbenchSingleLine(Rect rect, string value, GameFont font, Color color)
        {
            bool wrap = Text.WordWrap;
            Text.WordWrap = false;
            Label(rect, value, font, color, TextAnchor.MiddleLeft);
            Text.WordWrap = wrap;
        }

        private static ShipPartDef? WorkbenchFindPart(DesignPartSpec part)
        {
            return string.IsNullOrEmpty(part.defName) ? null : DefDatabase<ShipPartDef>.GetNamedSilentFail(part.defName);
        }

        private static string WorkbenchPartName(DesignPartSpec part)
        {
            return WorkbenchFindPart(part)?.label ?? part.defName ?? "未知部件";
        }

        private static string WorkbenchPartReadout(ShipPartDef def)
        {
            return def.category switch
            {
                "thruster" or "rcs" => $"{def.exhaustVelocity * def.massFlowRate / 1000f:0.#} kN 推力",
                "tank" or "rcs-tank" => $"{def.tankCapacityKg / 1000f:0.##} t 容量",
                "control" => "控制核心",
                _ => $"{RocketSchematicBuilder.StackHeightOf(def):0.##} m 长"
            };
        }

        private static Color WorkbenchCategoryColor(string category)
        {
            return category switch
            {
                "thruster" or "rcs" => Gold,
                "tank" or "rcs-tank" => WorkbenchGood,
                "stage" or "hull" => new Color32(138, 169, 156, 255),
                _ => Dim,
            };
        }
    }
}
