using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 火箭组装器 C+ 窗口（2026-11-24）。
    ///
    /// 设计依据：`DesignNotes/ui-reset-20261122/cplus-*.svg`。
    /// · L0：1+7+4 分栏；状态 + 一句话 + 一个主按钮 + 查看全部检查 + meta。
    /// · L1：级轨 + 画布 + 检视栏 + 零件带；建造条固定在检视栏底部。
    /// · L2：独立 Window（输入隔离）；620 高 / 44 行 / 4 列；画布区居中。
    /// · 检查窗：独立小 Window；Launch 4/6 行、Build 7 行。
    ///
    /// 本文件在 Presentation 下，不修改 CompRocketAssembler / 旧窗口。
    /// “已完工 → 编辑设计” 走后端 `CompRocketAssembler.ReopenDesign()`（2026-11-24 起，授权件
    /// `DesignNotes/后端变更授权-20261124-组装器C+UI.md`）：设计原样保留，只清工时/倒计时/加油。
    /// </summary>
    public class Window_RocketAssemblerC : Window
    {
        private readonly CompRocketAssembler comp;
        private bool editMode;
        private int selectedRow = -1;
        private ShipPartDef? armedDef;
        private readonly List<PieceHit> pieceHits = new();
        private struct PieceHit { public int designRow; public Rect rect; }

        private int shelfCategory;
        private int shelfPage;
        private Vector2 schematicPan;
        private float schematicZoom = 1f;
        private bool pressCandidate;
        private bool dragged;
        private Vector2 pressPos;
        private Vector2 dragAnchor;
        private Window_RocketAssemblerListC? listWindow;
        private Window_AssemblerChecksC? checksWindow;

        private readonly List<int> placementOffenders = new();
        private readonly List<int> contiguityOffenders = new();
        private string placementError = "";
        private string contiguityError = "";
        private readonly int[] clusterOrder = new int[ParallelLayout.MaxCount];
        private List<ShipPartDef> catalogCache = new();
        private RocketSchematic schematic = new();

        private static readonly Color Bg = Widgets.WindowBGFillColor;
        private static readonly Color BorderColor = new Color32(97, 108, 122, 255);
        private static readonly Color PanelFill = new Color(1f, 1f, 1f, 0.04f);
        private static readonly Color PanelHigh = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color Line = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color Hover = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color Dim = new Color(1f, 1f, 1f, 0.66f);
        private static readonly Color Faint = new Color(1f, 1f, 1f, 0.44f);
        private static readonly Color Gold = ColorLibrary.Gold;
        private static readonly Color Red = ColorLibrary.RedReadable;
        private static readonly Color TankFill = new Color(0.14f, 0.16f, 0.18f);
        private static readonly Color TankEdge = new Color(1f, 1f, 1f, 0.20f);
        private static readonly Color EngFill = new Color(0.18f, 0.16f, 0.11f);
        private static readonly Color EngEdge = new Color(1f, 1f, 1f, 0.20f);
        private static readonly Color OtherFill = new Color(0.12f, 0.13f, 0.15f);
        private static readonly Color OtherEdge = new Color(1f, 1f, 1f, 0.14f);

        private const float TitleH = 44f;
        private const float BodyY = 60f;
        private const float LeftX = 16f;
        private const float Col1W = 90f;
        private const float CanvasX = 114f;
        private const float RailW = 376f;
        private const float ShelfH = 104f;
        private const float ActionH = 28f;
        private const float BtnH = 44f;
        private const float RowH = 44f;

        public Window_RocketAssemblerC(CompRocketAssembler comp)
        {
            this.comp = comp;
            forcePause = false;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            doWindowBackground = false;
            doCloseX = false;
        }

        public override Vector2 InitialSize => new(1200f, 720f);
        protected override float Margin => 0f;

        public override void DoWindowContents(Rect inRect)
        {
            if (comp?.parent == null || comp.parent.Destroyed) { Close(); return; }
            Recompute();
            Widgets.DrawBoxSolid(inRect, Bg);
            Border(inRect, BorderColor);
            DrawTitleBar(inRect);
            if (editMode) DrawEdit(inRect); else DrawView(inRect);
        }

        public override void PreClose()
        {
            if (listWindow != null && listWindow.IsOpen) listWindow.Close();
            if (checksWindow != null && checksWindow.IsOpen) checksWindow.Close();
            base.PreClose();
        }

        private void Recompute()
        {
            placementOffenders.Clear();
            placementError = comp.EnginePlacementError(placementOffenders);
            contiguityOffenders.Clear();
            contiguityError = comp.StageContiguityError(contiguityOffenders);
            if (selectedRow >= comp.Design.parts.Count) selectedRow = comp.Design.parts.Count - 1;
            schematic = RocketSchematicBuilder.Build(comp.Design,
                stageGapMeters: RocketDesignRules.StageGapMeters);
        }

        private struct Layout
        {
            public Rect Left;
            public Rect Canvas;
            public Rect Rail;
            public Rect Shelf;
        }

        private Layout GetLayout(Rect inRect, bool withShelf)
        {
            float w = inRect.width;
            float h = inRect.height;
            float top = BodyY;
            float bottom = withShelf ? h - ShelfH : h - 16f;
            float railW = RailW;
            float railX = w - 16f - railW;
            float canvasX = CanvasX;
            float canvasW = railX - 8f - canvasX;
            if (canvasW < 320f)
            {
                canvasW = 320f;
                railX = canvasX + canvasW + 8f;
            }
            return new Layout
            {
                Left = new Rect(LeftX, top, Col1W, bottom - top),
                Canvas = new Rect(canvasX, top, canvasW, bottom - top),
                Rail = new Rect(railX, top, railW, bottom - top),
                Shelf = withShelf ? new Rect(0, h - ShelfH, w, ShelfH) : Rect.zero
            };
        }

        // ── 基础绘制 ─────────────────────────────────────────────

        private static void Label(Rect r, string s, GameFont font, Color color, TextAnchor anchor = TextAnchor.UpperLeft)
        {
            GameFont oldFont = Text.Font;
            Color oldColor = GUI.color;
            TextAnchor oldAnchor = Text.Anchor;
            Text.Font = font;
            GUI.color = color;
            Text.Anchor = anchor;
            Widgets.Label(r, s);
            Text.Font = oldFont;
            GUI.color = oldColor;
            Text.Anchor = oldAnchor;
        }

        private static void Border(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            Widgets.DrawBox(r, 1);
            GUI.color = old;
        }

        private static void Panel(Rect r)
        {
            Widgets.DrawBoxSolid(r, PanelFill);
            Border(r, Line);
        }

        private static void DrawLine(float x1, float y1, float x2, float y2, Color c, float w = 1f)
        {
            if (Mathf.Abs(x2 - x1) < Mathf.Abs(y2 - y1))
                Widgets.DrawBoxSolid(new Rect(x1, Mathf.Min(y1, y2), w, Mathf.Abs(y2 - y1)), c);
            else
                Widgets.DrawBoxSolid(new Rect(Mathf.Min(x1, x2), y1, Mathf.Abs(x2 - x1), w), c);
        }

        private static void Corner(Rect r, Color c)
        {
            DrawLine(r.x, r.y, r.x + 14f, r.y, c, 2f);
            DrawLine(r.x, r.y, r.x, r.y + 14f, c, 2f);
        }

        private static bool Btn(Rect r, string label, bool enabled = true, bool danger = false)
        {
            GameFont old = Text.Font;
            Text.Font = GameFont.Small;
            bool clicked = danger
                ? Widgets.ButtonText(r, label, true, true, ColorLibrary.RedReadable, enabled)
                : Widgets.ButtonText(r, label, true, true, enabled);
            Text.Font = old;
            if (!enabled) Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, 0.35f));
            return clicked;
        }

        private static bool Chip(Rect r, string label, bool active)
        {
            if (Mouse.IsOver(r)) Widgets.DrawBoxSolid(r, Hover);
            if (active)
            {
                Widgets.DrawBoxSolid(r, PanelHigh);
                Border(r, new Color(1f, 1f, 1f, 0.30f));
            }
            else Border(r, new Color(1f, 1f, 1f, 0.12f));
            Label(r, label, GameFont.Tiny, active ? Color.white : Dim, TextAnchor.MiddleCenter);
            return Widgets.ButtonInvisible(r);
        }

        private static bool Micro(Rect r, string label)
        {
            bool over = Mouse.IsOver(r);
            if (over) Widgets.DrawBoxSolid(r, Hover);
            Label(r, label, GameFont.Small, over ? Color.white : Dim, TextAnchor.MiddleCenter);
            return Widgets.ButtonInvisible(r);
        }

        private static void Title(Rect r, string text)
        {
            Label(r, text, GameFont.Medium, Color.white, TextAnchor.MiddleLeft);
        }

        private void DrawTitleBar(Rect inRect)
        {
            Widgets.DrawBoxSolid(new Rect(0, 0, inRect.width, TitleH), new Color(0f, 0f, 0f, 0.20f));
            DrawLine(0, TitleH - 1, inRect.width, TitleH - 1, Line);
            Title(new Rect(20, 0, 500, TitleH), "火箭组装器");
            if (editMode)
            {
                Chip(new Rect(230, 10, 46, 24), "设计", true);
                if (Btn(new Rect(inRect.width - 192f, 8, 64, 28), "清单")) OpenList();
                if (Btn(new Rect(inRect.width - 120f, 8, 80, 28), "完成")) editMode = false;
                if (Micro(new Rect(inRect.width - 32f, 8, 24, 28), "×")) Close();
            }
            else
            {
                Label(new Rect(250, 0, 200, TitleH), "装配台 01", GameFont.Tiny, Faint, TextAnchor.MiddleLeft);
                if (Micro(new Rect(inRect.width - 34f, 8, 24, 28), "×")) Close();
            }
        }

        private void DrawView(Rect inRect)
        {
            Layout l = GetLayout(inRect, false);
            DrawDimension(l.Left, l);
            DrawSchematic(l.Canvas, false, false);
            DrawL0Rail(l.Rail);
        }

        private void DrawEdit(Rect inRect)
        {
            Layout l = GetLayout(inRect, true);
            DrawStageRail(l.Left);
            DrawSchematic(l.Canvas, true, true);
            DrawInspector(l.Rail);
            DrawShelf(l.Shelf);
        }

        private void DrawDimension(Rect left, in Layout l)
        {
            float x = left.x + 56f;
            float y0 = l.Canvas.y + 52f;
            float y1 = l.Canvas.yMax - 52f;
            DrawLine(x, y0, x, y1, Dim, 1f);
            DrawLine(x - 8f, y0, x + 8f, y0, Dim, 1f);
            DrawLine(x - 8f, y1, x + 8f, y1, Dim, 1f);
            Label(new Rect(left.x, left.y + left.height * 0.5f - 8f, left.width, 16f),
                $"{schematic.totalHeightM:F1} m", GameFont.Tiny, Faint, TextAnchor.MiddleCenter);
        }

        // ── 画布 ────────────────────────────────────────────────

        private void DrawSchematic(Rect rect, bool interactive, bool showMeta)
        {
            Panel(rect);
            Widgets.BeginGroup(rect);
            Rect view = new Rect(8f, 8f, rect.width - 16f, rect.height - (showMeta ? 44f : 16f));
            pieceHits.Clear();

            if (Micro(new Rect(view.xMax - 46f, view.y, 46f, 22f), "重置"))
            {
                schematicPan = Vector2.zero;
                schematicZoom = 1f;
            }

            if (comp.Design.parts.Count == 0)
            {
                Label(new Rect(view.x, view.center.y - 24f, view.width, 20f), "还没有零件",
                    GameFont.Small, Dim, TextAnchor.MiddleCenter);
                Label(new Rect(view.x, view.center.y, view.width, 18f),
                    "从下方零件带选件，点画布放置", GameFont.Tiny, Faint, TextAnchor.MiddleCenter);
                Widgets.EndGroup();
                return;
            }

            if (schematic.pieces.Count == 0)
            {
                Label(new Rect(view.x, view.center.y - 10f, view.width, 20f),
                    "设计里没有可显示的零件", GameFont.Small, Dim, TextAnchor.MiddleCenter);
                Widgets.EndGroup();
                return;
            }

            float usableH = Mathf.Max(40f, view.height - 24f);
            float usableW = Mathf.Max(40f, view.width - 32f);
            float fit = Mathf.Min(usableW / Mathf.Max(0.1f, schematic.maxWidthM),
                                  usableH / Mathf.Max(0.1f, schematic.totalHeightM));
            fit = Mathf.Min(fit, 12f);
            float scale = Mathf.Max(0.05f, fit * schematicZoom);
            float cx = view.center.x + schematicPan.x;
            float top = view.y + 16f + schematicPan.y;
            float bodyBottom = top + schematic.totalHeightM * scale;

            if (armedDef != null && Event.current != null)
            {
                Vector2 mp = Event.current.mousePosition;
                float gw = Mathf.Max(4f, (armedDef.boundsX > 0f ? armedDef.boundsX : 2f) * scale);
                float gh = Mathf.Max(4f, RocketSchematicBuilder.StackHeightOf(armedDef) * scale);
                Rect ghost = new(mp.x - gw * 0.5f, mp.y - gh * 0.5f, gw, gh);
                Widgets.DrawBoxSolid(ghost, new Color(Gold.r, Gold.g, Gold.b, 0.10f));
                Border(ghost, Gold);
            }

            int prevStage = int.MinValue;
            for (int i = 0; i < schematic.pieces.Count; i++)
            {
                SchematicPiece p = schematic.pieces[i];
                float spanW = Mathf.Max(2f, p.widthM * scale);
                float partW = Mathf.Max(2f, p.partWidthM * scale);
                float h = Mathf.Max(3f, p.heightM * scale);
                float stationX = cx + p.centerXM * scale - spanW * 0.5f;
                float screenY = top + (schematic.maxZM - (p.centerZM + p.heightM * 0.5f)) * scale;
                Rect pieceRect = new(stationX, screenY, spanW, Mathf.Max(2f, h - 1f));
                pieceHits.Add(new PieceHit { designRow = p.designRow, rect = pieceRect });

                if (p.stageId != prevStage)
                {
                    bool activeStage = p.stageId == comp.ActiveStage;
                    DrawLine(view.x + 6f, screenY - 1f, view.xMax - 6f, screenY - 1f,
                        activeStage ? new Color(Gold.r, Gold.g, Gold.b, 0.55f) : new Color(1f, 1f, 1f, 0.22f), 1f);
                    Label(new Rect(view.xMax - 54f, screenY - 15f, 46f, 14f),
                        $"级 {p.stageId}", GameFont.Tiny,
                        activeStage ? Gold : Faint, TextAnchor.MiddleRight);
                    prevStage = p.stageId;
                }

                bool selected = armedDef == null && selectedRow == p.designRow;
                DrawStation(screenY, h, p, cx + p.centerXM * scale, partW, scale, p.stageId == comp.ActiveStage);
                if (selected)
                {
                    Border(pieceRect, Gold);
                    Corner(pieceRect, Gold);
                }

                string tip = $"{p.label ?? p.defName}"
                    + (p.count > 1
                        ? $"\n并联 {p.count} 台：" + ParallelLayout.Describe(p.count, p.partWidthM)
                          + $"\n整站跨度 {p.widthM:0.##} m · 单件 {p.partWidthM:0.##} × {p.heightM:0.##} m"
                        : $"\n{p.widthM:0.#} × {p.heightM:0.#} m")
                    + $"\n干重 {p.dryMassKg / 1000f:0.###} t × {Mathf.Max(1, p.count)}"
                    + (p.propellantKg > 0f
                        ? $"\n推进剂 {p.propellantKg / 1000f:0.###} t × {Mathf.Max(1, p.count)}"
                        : "");
                TooltipHandler.TipRegion(pieceRect, tip);
            }

            if (showMeta) DrawMeta(new Rect(8f, rect.height - 36f, rect.width - 16f, 28f), 4);

            if (interactive)
            {
                HandleSchematicInput(view);
                HandlePlacementClick(view);
            }
            else
            {
                HandleScrollZoom(view);
            }
            Widgets.EndGroup();
        }

        private void HandleScrollZoom(Rect view)
        {
            Event e = Event.current;
            if (e == null || !Mouse.IsOver(view)) return;
            if (e.type == EventType.ScrollWheel)
            {
                schematicZoom = Mathf.Clamp(schematicZoom * (e.delta.y > 0f ? 1.1f : 0.909f), 0.25f, 8f);
                e.Use();
            }
        }

        private void HandleSchematicInput(Rect view)
        {
            Event e = Event.current;
            if (e == null || armedDef != null) return;
            if (Mouse.IsOver(view))
            {
                if (e.type == EventType.ScrollWheel)
                {
                    schematicZoom = Mathf.Clamp(schematicZoom * (e.delta.y > 0f ? 1.1f : 0.909f), 0.25f, 8f);
                    e.Use();
                    return;
                }
                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    pressCandidate = true;
                    dragged = false;
                    pressPos = e.mousePosition;
                    dragAnchor = e.mousePosition;
                }
            }
            if (!pressCandidate) return;
            if (e.type == EventType.MouseDrag && e.button == 0)
            {
                if (!dragged && (e.mousePosition - pressPos).sqrMagnitude > 16f) dragged = true;
                if (dragged)
                {
                    schematicPan += e.mousePosition - dragAnchor;
                    dragAnchor = e.mousePosition;
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                if (!dragged && view.Contains(e.mousePosition)) HandleCanvasClick(e.mousePosition);
                pressCandidate = false;
                dragged = false;
            }
        }

        private void HandleCanvasClick(Vector2 p)
        {
            for (int i = pieceHits.Count - 1; i >= 0; i--)
            {
                if (pieceHits[i].rect.Contains(p))
                {
                    SelectRow(pieceHits[i].designRow);
                    Event.current.Use();
                    return;
                }
            }
            selectedRow = -1;
            Event.current.Use();
        }

        private void HandlePlacementClick(Rect view)
        {
            Event e = Event.current;
            if (e == null || armedDef == null) return;
            if (e.type != EventType.MouseDown || e.button != 0 || !view.Contains(e.mousePosition)) return;

            PieceHit? hit = null;
            for (int i = pieceHits.Count - 1; i >= 0; i--)
                if (pieceHits[i].rect.Contains(e.mousePosition)) { hit = pieceHits[i]; break; }

            if (hit == null) comp.AddPart(armedDef, 1);
            else if (IsSideBand(hit.Value.rect, e.mousePosition) && comp.CurrentParallel >= 2)
                comp.AddPartRadial(armedDef, comp.CurrentParallel, hit.Value.designRow);
            else
            {
                var parent = hit.Value.designRow < comp.Design.parts.Count
                    ? comp.Design.parts[hit.Value.designRow] : null;
                bool below = e.mousePosition.y > hit.Value.rect.center.y;
                if (parent != null && parent.useExplicitPose)
                    comp.AddPartAxialTo(armedDef, hit.Value.designRow, below);
                else comp.AddPart(armedDef, 1);
            }
            e.Use();
        }

        private static bool IsSideBand(Rect r, Vector2 p)
            => p.x < r.x + r.width * 0.28f || p.x > r.xMax - r.width * 0.28f;

        private void DrawStation(float y, float h, in SchematicPiece p, float cx, float partW, float scale, bool active)
        {
            float[]? ox = p.offsetX;
            float[]? oy = p.offsetY;
            int n = p.count;
            if (n <= 1 || ox == null || oy == null)
            {
                DrawPieceBlock(new Rect(cx - partW * 0.5f, y, partW, h), p, 1f, active);
                return;
            }
            for (int i = 0; i < n; i++) clusterOrder[i] = i;
            for (int i = 1; i < n; i++)
            {
                int cur = clusterOrder[i];
                int j = i - 1;
                while (j >= 0 && oy[clusterOrder[j]] > oy[cur]) { clusterOrder[j + 1] = clusterOrder[j]; j--; }
                clusterOrder[j + 1] = cur;
            }
            float minY = oy[clusterOrder[0]];
            float maxY = oy[clusterOrder[n - 1]];
            float range = maxY - minY;
            for (int i = 0; i < n; i++)
            {
                int k = clusterOrder[i];
                float t = range > 1e-4f ? (oy[k] - minY) / range : 1f;
                float shade = Mathf.Lerp(0.70f, 1f, t);
                DrawPieceBlock(new Rect(cx + ox[k] * scale - partW * 0.5f, y, partW, h), p, shade, active);
            }
        }

        private static void DrawPieceBlock(Rect r, in SchematicPiece p, float shade, bool active)
        {
            Color fill = p.kind switch
            {
                SchematicKind.Engine => EngFill,
                SchematicKind.Tank => TankFill,
                _ => OtherFill,
            };
            fill *= new Color(shade, shade, shade, 0.92f);
            if (p.shape == InertiaTensor.Shape.Cylinder)
                DrawCylinderBlock(r, fill);
            else
            {
                Widgets.DrawBoxSolid(r, fill);
                Border(r, p.kind == SchematicKind.Engine ? EngEdge : TankEdge);
            }
            if (p.kind == SchematicKind.Engine && r.height > 10f)
            {
                var nozzle = new Rect(r.x + r.width * 0.14f, r.yMax - Mathf.Min(9f, r.height * 0.32f),
                    r.width * 0.72f, Mathf.Min(8f, r.height * 0.30f));
                Widgets.DrawBoxSolid(nozzle, new Color(0.12f, 0.11f, 0.10f, 0.95f));
                Color c = active ? Gold : EngEdge;
                DrawLine(nozzle.x, nozzle.yMax, nozzle.xMax, nozzle.yMax, c, 1f);
            }
        }

        private static void DrawCylinderBlock(Rect r, Color fill)
        {
            float cap = Mathf.Min(r.width * 0.32f, r.height * 0.30f);
            if (cap < 2f || r.width < 4f) { Widgets.DrawBoxSolid(r, fill); return; }
            Widgets.DrawBoxSolid(new Rect(r.x, r.y + cap, r.width, r.height - cap * 2f), fill);
            const int strips = 5;
            float stripH = cap / strips;
            float halfMax = r.width * 0.5f;
            for (int i = 0; i < strips; i++)
            {
                float u = (i + 0.5f) / strips;
                float k = 1f - u;
                float half = halfMax * Mathf.Sqrt(Mathf.Max(0f, 1f - k * k));
                float w = Mathf.Max(1f, half * 2f);
                float x = r.center.x - w * 0.5f;
                Widgets.DrawBoxSolid(new Rect(x, r.y + i * stripH, w, stripH + 0.6f), fill);
                Widgets.DrawBoxSolid(new Rect(x, r.yMax - (i + 1) * stripH, w, stripH + 0.6f), fill);
            }
        }

        private void DrawMeta(Rect r, int columns)
        {
            string[] labels;
            string[] values;
            if (columns == 3)
            {
                labels = new[] { "总高", "干重", "级数" };
                values = new[]
                {
                    $"{schematic.totalHeightM:F1} m",
                    $"{schematic.dryMassKg / 1000f:F1} t",
                    $"{comp.StageCount}"
                };
            }
            else
            {
                labels = new[] { "总高", "干重", "TWR", "级数" };
                values = new[]
                {
                    $"{schematic.totalHeightM:F1} m",
                    $"{schematic.dryMassKg / 1000f:F1} t",
                    $"{Twr():0.00}",
                    $"{comp.StageCount}"
                };
            }
            float cw = (r.width - (columns - 1) * 8f) / columns;
            for (int i = 0; i < columns; i++)
            {
                float x = r.x + i * (cw + 8f);
                Label(new Rect(x, r.y, cw, 14f), labels[i], GameFont.Tiny, Faint);
                Label(new Rect(x, r.y + 15f, cw, 18f), values[i], GameFont.Small, Color.white);
            }
        }

        // ── L0 ──────────────────────────────────────────────────

        private enum SurfaceState
        {
            IdleEmpty,
            IdleWithDesign,
            Designing,
            Building,
            LaunchReady,
            LaunchBlocked,
            Launching
        }

        private SurfaceState ResolveSurface()
        {
            if (comp.State == AssemblerState.Building) return SurfaceState.Building;
            if (comp.State == AssemblerState.Launching) return SurfaceState.Launching;
            if (comp.State == AssemblerState.Complete)
                return GetLaunchChecks().All(c => c.Pass) ? SurfaceState.LaunchReady : SurfaceState.LaunchBlocked;
            if (comp.Design.parts.Count == 0) return SurfaceState.IdleEmpty;
            if (comp.State == AssemblerState.Designing) return SurfaceState.Designing;
            return SurfaceState.IdleWithDesign;
        }

        private void DrawL0Rail(Rect rail)
        {
            Panel(rail);
            SurfaceState state = ResolveSurface();
            Color accent = state switch
            {
                SurfaceState.LaunchBlocked => Red,
                SurfaceState.IdleEmpty or SurfaceState.IdleWithDesign => Faint,
                _ => Gold
            };
            string word = state switch
            {
                SurfaceState.IdleEmpty => "空闲",
                SurfaceState.IdleWithDesign => "空闲",
                SurfaceState.Designing => "设计中",
                SurfaceState.Building => "建造中",
                SurfaceState.LaunchReady => "可发射",
                SurfaceState.LaunchBlocked => "受阻",
                _ => "倒计时"
            };
            Widgets.DrawBoxSolid(new Rect(rail.x, rail.y, 3f, 90f), accent);
            Label(new Rect(rail.x + 24f, rail.y + 14f, rail.width - 32f, 34f), word, GameFont.Medium, Color.white,
                TextAnchor.MiddleLeft);

            string sentence;
            string check;
            string primary;
            string? sub = null;
            var links = new List<string>();
            switch (state)
            {
                case SurfaceState.IdleEmpty:
                    sentence = "还没有设计。先放一个引擎和一个工质箱。";
                    check = "";
                    primary = "开始设计";
                    break;
                case SurfaceState.IdleWithDesign:
                    sentence = "台子空着；这份设计还能再用一次。";
                    check = "";
                    primary = "继续设计";
                    break;
                case SurfaceState.Designing:
                {
                    bool ready = GetBuildChecks().All(c => c.Pass);
                    var first = GetBuildChecks().FirstOrDefault(c => !c.Pass);
                    sentence = ready ? "设计有效，可以开始建造。" : $"还差 1 项：{first.Label}";
                    check = ready ? "✓ 建造检查通过" : $"× {first.Label}　{GetBuildChecks().Count(c => c.Pass)} 项通过";
                    primary = "继续设计";
                    links.Add("查看全部检查");
                    break;
                }
                case SurfaceState.Building:
                    sentence = $"建造中 {comp.Progress01:P0} · {comp.WorkDone:F0} / {comp.WorkRequired:F0} 工时";
                    check = comp.WorkDone <= 0.01f ? "等待工人" : "";
                    primary = "取消建造";
                    break;
                case SurfaceState.LaunchReady:
                    sentence = $"可以发射。倒计时 {comp.Props.launchDelayGameSeconds:0.#} 游戏秒。";
                    check = "✓ 4 项检查通过";
                    primary = "发射";
                    sub = "改设计需重新建造";
                    links.Add("编辑设计（需重新建造）");
                    links.Add("下一枚");
                    links.Add("查看全部检查");
                    break;
                case SurfaceState.LaunchBlocked:
                {
                    var first = GetLaunchChecks().FirstOrDefault(c => !c.Pass);
                    sentence = $"还差 1 项：{first.Label} {first.Detail}";
                    check = $"× {first.Label}　{GetLaunchChecks().Count(c => c.Pass)} 项通过";
                    primary = "编辑设计";
                    sub = "改设计需重新建造";
                    links.Add("下一枚");
                    links.Add("查看全部检查");
                    break;
                }
                default:
                    sentence = $"{comp.LaunchSecondsLeft:0.0} 秒后发射。";
                    check = "取消后回到已完工";
                    primary = "取消发射";
                    break;
            }

            Label(new Rect(rail.x + 24f, rail.y + 60f, rail.width - 32f, 40f), sentence, GameFont.Small,
                state == SurfaceState.LaunchBlocked ? Red : Dim);
            if (!string.IsNullOrEmpty(check))
                Label(new Rect(rail.x + 24f, rail.y + 120f, rail.width - 32f, 18f), check, GameFont.Tiny,
                    state == SurfaceState.LaunchBlocked ? Red : Dim);
            if (Btn(new Rect(rail.x + 16f, rail.y + 170f, rail.width - 32f, BtnH), primary))
                HandlePrimary(state);
            float y = rail.y + 232f;
            if (sub != null)
            {
                Label(new Rect(rail.x + 16f, y, rail.width - 32f, 16f), sub, GameFont.Tiny, Faint);
                y += 24f;
            }
            foreach (string link in links)
            {
                if (BtnLink(new Rect(rail.x + 16f, y, rail.width - 32f, 18f), link))
                    HandleL0Link(link, state);
                y += 24f;
            }
            DrawMeta(new Rect(rail.x + 16f, rail.yMax - 84f, rail.width - 32f, 40f), 3);
        }

        private void HandlePrimary(SurfaceState state)
        {
            switch (state)
            {
                case SurfaceState.IdleEmpty:
                case SurfaceState.IdleWithDesign:
                case SurfaceState.Designing:
                    editMode = true;
                    break;
                case SurfaceState.Building:
                    comp.CancelBuild();
                    break;
                case SurfaceState.LaunchReady:
                    if (!comp.BeginLaunch(out string why)) Messages.Message(why, MessageTypeDefOf.RejectInput, false);
                    break;
                case SurfaceState.LaunchBlocked:
                    ReopenDesign();
                    break;
                case SurfaceState.Launching:
                    comp.CancelLaunch();
                    break;
            }
        }

        private void HandleL0Link(string link, SurfaceState state)
        {
            if (link.StartsWith("编辑设计", StringComparison.Ordinal)) ReopenDesign();
            else if (link == "下一枚") ConfirmNextRocket();
            else if (link == "查看全部检查")
            {
                bool launch = state is SurfaceState.LaunchReady or SurfaceState.LaunchBlocked;
                OpenChecks(launch);
            }
        }

        private static bool BtnLink(Rect r, string label)
        {
            bool over = Mouse.IsOver(r);
            Label(r, label, GameFont.Tiny, over ? Color.white : Dim, TextAnchor.MiddleLeft);
            if (over) DrawLine(r.x, r.yMax - 2f, r.x + Mathf.Min(r.width, label.Length * 9f), r.yMax - 2f, Dim, 1f);
            return Widgets.ButtonInvisible(r);
        }

        private void ConfirmNextRocket()
        {
            Dialog_MessageBox.CreateConfirmation("下一枚会丢弃当前设计，确定？", () =>
            {
                comp.StartNewRocket();
                selectedRow = -1;
                armedDef = null;
                editMode = false;
            }, true);
        }

        /// <summary>
        /// 完工态回到设计：调后端 <see cref="CompRocketAssembler.ReopenDesign"/>（设计原样保留，只清工时/倒计时/加油）。
        /// 级号归 0 是纯 UI 决定 —— 否则「加入级」会停在旧级上。
        /// </summary>
        private void ReopenDesign()
        {
            if (!comp.ReopenDesign())
            {
                Messages.Message("当前状态不能回到设计。", MessageTypeDefOf.RejectInput, false);
                return;
            }
            comp.SetCurrentStage(0);
            selectedRow = -1;
            armedDef = null;
            editMode = true;
        }

        // ── L1 ──────────────────────────────────────────────────

        private void DrawStageRail(Rect rail)
        {
            Panel(rail);
            Label(new Rect(rail.x + 8f, rail.y + 20f, rail.width - 8f, 16f), "新件", GameFont.Tiny, Faint);
            Label(new Rect(rail.x + 8f, rail.y + 34f, rail.width - 8f, 16f), "加入", GameFont.Tiny, Faint);
            var stages = RocketDesignRules.StagesOf(comp.Design);
            if (stages.Count == 0) stages.Add(0);
            if (!stages.Contains(comp.CurrentStage)) { stages.Add(comp.CurrentStage); stages.Sort(); }
            int shown = 0;
            foreach (int st in stages)
            {
                if (shown >= 5) break;
                var r = new Rect(rail.x + 8f, rail.y + 52f + shown * 40f, 64f, 32f);
                if (Chip(r, $"级 {st}", comp.CurrentStage == st)) comp.SetCurrentStage(st);
                shown++;
            }
            if (shown < 5)
            {
                var plus = new Rect(rail.x + 8f, rail.y + 52f + shown * 40f, 64f, 32f);
                if (Chip(plus, "+", false))
                {
                    for (int st = 0; st <= CompRocketAssembler.MaxStageId; st++)
                        if (!stages.Contains(st)) { comp.SetCurrentStage(st); break; }
                }
            }
        }

        private void DrawInspector(Rect rail)
        {
            Panel(rail);
            var part = selectedRow >= 0 && selectedRow < comp.Design.parts.Count ? comp.Design.parts[selectedRow] : null;
            if (armedDef != null) DrawArmed(rail);
            else if (part != null) DrawSelected(rail, part);
            else DrawNone(rail);
            DrawBuildBar(rail);
        }

        private void DrawSelected(Rect rail, DesignPartSpec part)
        {
            ShipPartDef? def = DefDatabase<ShipPartDef>.GetNamedSilentFail(part.defName);
            Widgets.DrawBoxSolid(new Rect(rail.x, rail.y, 3f, 44f), Gold);
            Label(new Rect(rail.x + 16f, rail.y + 8f, rail.width - 32f, 16f), "选中件", GameFont.Tiny, Faint);
            Label(new Rect(rail.x + 16f, rail.y + 26f, rail.width - 32f, 22f),
                def?.label ?? part.defName, GameFont.Small, Color.white);
            Label(new Rect(rail.x + 16f, rail.y + 52f, 60f, 16f), $"级 {part.stageId}", GameFont.Tiny, Dim);
            Label(new Rect(rail.x + 84f, rail.y + 52f, 100f, 16f),
                $"{RocketDesignRules.PartCountOf(part)} 件", GameFont.Tiny, Dim);

            string[] labels;
            string[] values;
            if (def != null && (def.category == "tank" || def.category == "rcs-tank"))
            {
                labels = new[] { "容量", "件重", "高度" };
                values = new[]
                {
                    $"{def.tankCapacityKg / 1000f:0.#} t",
                    $"{def.massKg / 1000f:0.00} t",
                    $"{RocketSchematicBuilder.StackHeightOf(def):0.0} m"
                };
            }
            else if (def != null && (def.category == "thruster" || def.category == "rcs"))
            {
                labels = new[] { "推力", "件重", "高度" };
                values = new[]
                {
                    $"{def.exhaustVelocity * def.massFlowRate / 1000.0:0} kN",
                    $"{def.massKg / 1000f:0.00} t",
                    $"{RocketSchematicBuilder.StackHeightOf(def):0.0} m"
                };
            }
            else
            {
                labels = new[] { "件重", "高度", "类别" };
                values = new[]
                {
                    $"{(def?.massKg ?? 0f) / 1000f:0.00} t",
                    $"{(def != null ? RocketSchematicBuilder.StackHeightOf(def) : 0f):0.0} m",
                    def?.category ?? part.category ?? ""
                };
            }
            for (int i = 0; i < 3; i++)
            {
                float y = rail.y + 84f + i * 26f;
                Label(new Rect(rail.x + 16f, y, 100f, 16f), labels[i], GameFont.Tiny, Faint);
                Label(new Rect(rail.x + 16f, y, rail.width - 32f, 16f), values[i], GameFont.Small,
                    Color.white, TextAnchor.MiddleRight);
                DrawLine(rail.x + 16f, y + 18f, rail.x + rail.width - 16f, y + 18f, Line);
            }

            if (Btn(new Rect(rail.x + 16f, rail.y + 170f, rail.width - 32f, ActionH), "↑ 上移"))
                MoveSelected(-1);
            if (Btn(new Rect(rail.x + 16f, rail.y + 204f, rail.width - 32f, ActionH), "↓ 下移"))
                MoveSelected(1);
            if (Btn(new Rect(rail.x + 16f, rail.y + 238f, rail.width - 32f, ActionH), "换级"))
                OpenStagePicker();
            if (Btn(new Rect(rail.x + 16f, rail.y + 272f, rail.width - 32f, ActionH), "删除", true, true))
                DeleteSelected();
            if (BtnLink(new Rect(rail.x + 16f, rail.y + 306f, rail.width - 32f, 18f), "径向复制 ×4"))
            {
                if (comp.RadialCopy(selectedRow, 4)) selectedRow = -1;
            }
        }

        private void DrawArmed(Rect rail)
        {
            Widgets.DrawBoxSolid(new Rect(rail.x, rail.y, 3f, 44f), Gold);
            Label(new Rect(rail.x + 16f, rail.y + 8f, rail.width - 32f, 16f), "待放置", GameFont.Tiny, Gold);
            Label(new Rect(rail.x + 16f, rail.y + 26f, rail.width - 32f, 22f),
                armedDef?.label ?? armedDef?.defName ?? "", GameFont.Small, Color.white);
            Label(new Rect(rail.x + 16f, rail.y + 52f, 100f, 16f),
                $"级 {comp.CurrentStage}", GameFont.Tiny, Dim);

            Label(new Rect(rail.x + 16f, rail.y + 82f, 60f, 16f), "并联", GameFont.Tiny, Faint);
            for (int i = 0; i < 7; i++)
            {
                var r = new Rect(rail.x + 52f + i * 40f, rail.y + 78f, 36f, 24f);
                if (Chip(r, (i + 1).ToString(CultureInfo.InvariantCulture), comp.CurrentParallel == i + 1))
                    comp.SetCurrentParallel(i + 1);
            }
            Label(new Rect(rail.x + 16f, rail.y + 116f, rail.width - 32f, 16f), "空白 = 追加", GameFont.Tiny, Dim);
            Label(new Rect(rail.x + 16f, rail.y + 134f, rail.width - 32f, 16f),
                $"侧面 = 径向 ×{comp.CurrentParallel}", GameFont.Tiny, Dim);
            Label(new Rect(rail.x + 16f, rail.y + 152f, rail.width - 32f, 16f), "再点一次 = 取消", GameFont.Tiny, Dim);
        }

        private void DrawNone(Rect rail)
        {
            Label(new Rect(rail.x + 16f, rail.y + 24f, rail.width - 32f, 22f),
                "点火箭上的零件", GameFont.Small, Dim);
            bool ready = GetBuildChecks().All(c => c.Pass);
            var first = GetBuildChecks().FirstOrDefault(c => !c.Pass);
            Label(new Rect(rail.x + 16f, rail.y + 54f, rail.width - 32f, 18f),
                ready ? "设计有效" : $"还差 1 项：{first.Label}", GameFont.Tiny,
                ready ? Dim : Red);
        }

        private void DrawBuildBar(Rect rail)
        {
            float top = rail.y + 360f;
            DrawLine(rail.x + 16f, top, rail.x + rail.width - 16f, top, Line);
            bool ready = GetBuildChecks().All(c => c.Pass);
            var first = GetBuildChecks().FirstOrDefault(c => !c.Pass);
            Label(new Rect(rail.x + 16f, top + 8f, rail.width - 32f, 16f),
                ready ? "✓ 设计有效" : $"× {first.Label}", GameFont.Tiny,
                ready ? Color.white : Red);
            if (Btn(new Rect(rail.x + 16f, top + 28f, rail.width - 32f, BtnH), "开始建造", ready))
            {
                if (!comp.StartBuild(out string why)) Messages.Message(why, MessageTypeDefOf.RejectInput, false);
                else editMode = false;
            }
            if (BtnLink(new Rect(rail.x + 16f, top + 78f, rail.width - 32f, 18f), "查看全部检查"))
                OpenChecks(false);
        }

        private void MoveSelected(int delta)
        {
            if (selectedRow < 0 || selectedRow >= comp.Design.parts.Count) return;
            int from = selectedRow;
            if (comp.MovePart(from, delta)) selectedRow = from + delta;
        }

        private void DeleteSelected()
        {
            if (selectedRow < 0 || selectedRow >= comp.Design.parts.Count) return;
            var part = comp.Design.parts[selectedRow];
            int qty = Math.Max(1, part?.quantity ?? 1);
            if (comp.RemovePartAt(selectedRow, qty)) selectedRow = -1;
        }

        private void OpenStagePicker()
        {
            int index = selectedRow;
            if (index < 0 || index >= comp.Design.parts.Count) return;
            var options = new List<FloatMenuOption>();
            for (int st = 0; st <= CompRocketAssembler.MaxStageId; st++)
            {
                int stage = st;
                options.Add(new FloatMenuOption($"级 {stage}", () => comp.SetPartStage(index, stage)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        // ── 零件带 ──────────────────────────────────────────────

        private static readonly string[] ShelfCategories = { "全部", "引擎", "工质箱", "结构", "控制", "载荷" };

        private void DrawShelf(Rect shelf)
        {
            Widgets.DrawBoxSolid(shelf, new Color(0f, 0f, 0f, 0.18f));
            DrawLine(0, shelf.y, shelf.width, shelf.y, Line);
            Label(new Rect(16f, shelf.y + 24f, 40f, 16f), "零件", GameFont.Tiny, Faint);
            for (int i = 0; i < ShelfCategories.Length; i++)
            {
                var r = new Rect(56f + i * 76f, shelf.y + 8f, 68f, 28f);
                if (Chip(r, ShelfCategories[i], shelfCategory == i))
                {
                    shelfCategory = i;
                    shelfPage = 0;
                    armedDef = null;
                }
            }
            DrawLine(0, shelf.y + 52f, shelf.width, shelf.y + 52f, Line);

            List<ShipPartDef> list = FilteredCatalog();
            int start = shelfPage * 6;
            for (int i = 0; i < 6 && start + i < list.Count; i++)
            {
                ShipPartDef def = list[start + i];
                var r = new Rect(16f + i * 178f, shelf.y + 64f, 170f, 32f);
                bool armed = armedDef == def;
                Widgets.DrawBoxSolid(r, armed ? new Color(Gold.r, Gold.g, Gold.b, 0.04f) : new Color(1f, 1f, 1f, 0.02f));
                Border(r, armed ? Gold : new Color(1f, 1f, 1f, 0.14f));
                Label(r, def.label ?? def.defName, GameFont.Small, armed ? Gold : Color.white, TextAnchor.MiddleCenter);
                if (Widgets.ButtonInvisible(r))
                {
                    armedDef = armed ? null : def;
                    if (armedDef != null) selectedRow = -1;
                }
            }
            if ((shelfPage + 1) * 6 < list.Count)
            {
                if (BtnLink(new Rect(shelf.width - 72f, shelf.y + 72f, 56f, 18f), "更多 ›"))
                    shelfPage++;
            }
            else if (shelfPage > 0)
            {
                if (BtnLink(new Rect(shelf.width - 72f, shelf.y + 72f, 56f, 18f), "‹ 上页"))
                    shelfPage--;
            }
        }

        private void EnsureCatalog()
        {
            if (catalogCache.Count > 0) return;
            catalogCache = DefDatabase<ShipPartDef>.AllDefsListForReading
                .Where(d => d != null)
                .OrderBy(d => d.category ?? "")
                .ThenBy(d => d.label ?? d.defName)
                .ToList();
        }

        private List<ShipPartDef> FilteredCatalog()
        {
            EnsureCatalog();
            return catalogCache.Where(d => shelfCategory switch
            {
                1 => d.category == "thruster" || d.category == "rcs",
                2 => d.category == "tank" || d.category == "rcs-tank",
                3 => d.category == "stage" || d.category == "hull",
                4 => d.category == "control",
                5 => d.category is "cabin" or "reactor" or "battery" or "radiator" or "cargo" or "weapon" or "payload-bay",
                _ => true
            }).ToList();
        }

        // ── 检查口径 ────────────────────────────────────────────

        internal readonly struct CheckRow
        {
            public readonly string Label;
            public readonly string Detail;
            public readonly bool Pass;
            public CheckRow(string label, string detail, bool pass)
            {
                Label = label;
                Detail = detail;
                Pass = pass;
            }
        }

        internal List<CheckRow> GetBuildChecks()
        {
            var list = new List<CheckRow>();
            list.Add(new CheckRow("可编辑", comp.CanEditDesign ? "是" : "否", comp.CanEditDesign));
            bool valid = comp.Design.IsValid(out string reason);
            list.Add(new CheckRow("推进器 / 储罐", valid ? "满足" : reason, valid));
            list.Add(new CheckRow("控制核心", $"{comp.ControlCount} 个", comp.ControlCount > 0));
            list.Add(new CheckRow("引擎位置", placementError.Length == 0 ? "通过" : placementError, placementError.Length == 0));
            list.Add(new CheckRow("级连续性", contiguityError.Length == 0 ? "通过" : contiguityError, contiguityError.Length == 0));
            list.Add(new CheckRow("活动级", comp.ActiveStageReady ? "就绪" : comp.StageDiagnosis(), comp.ActiveStageReady));
            float work = CompRocketAssembler.WorkFor(comp.Design);
            list.Add(new CheckRow("工时", $"{work:F0}", work > 0f));
            return list;
        }

        internal List<CheckRow> GetLaunchChecks()
        {
            var list = new List<CheckRow>();
            float twr = Twr();
            list.Add(new CheckRow("推重比", $"{twr:0.00}　需要 >1", twr > 1f));
            var active = comp.ActiveStages;
            bool engines = active.Count > 0;
            foreach (int st in active) engines &= comp.EngineCountInStage(st) > 0;
            string engineText = string.Join("　", active.OrderBy(s => s).Select(s => $"级{s}:{comp.EngineCountInStage(s)}机"));
            list.Add(new CheckRow("推进器", engineText, engines));
            bool tanks = active.Count > 0;
            foreach (int st in active) tanks &= comp.TankCountInStage(st) > 0;
            string tankText = string.Join("　", active.OrderBy(s => s).Select(s => $"级{s}:{comp.TankCountInStage(s)}罐"));
            list.Add(new CheckRow("燃料", tankText, tanks));
            list.Add(new CheckRow("控制核心", $"{comp.ControlCount} 个", comp.ControlCount > 0));
            if (placementError.Length > 0) list.Add(new CheckRow("引擎位置", placementError, false));
            if (contiguityError.Length > 0) list.Add(new CheckRow("级连续性", contiguityError, false));
            return list;
        }

        internal float Twr()
        {
            float thrust = RocketSchematicBuilder.ThrustN(comp.Design, null, comp.ActiveStages);
            float mass = schematic.dryMassKg + schematic.capacityKg;
            float g = SiteGravity();
            float weight = mass * g;
            return weight > 0.01f ? thrust / weight : 0f;
        }

        private float SiteGravity()
        {
            if (comp.parent?.Map != null && LaunchSite.TryResolve(comp.parent.Map, out LaunchSite site, out _)
                && site.Radius > 0.0)
                return (float)(site.Mu / (site.Radius * site.Radius));
            return 8f;
        }

        internal bool IsOffender(int index)
            => placementOffenders.Contains(index) || contiguityOffenders.Contains(index);

        internal void SelectRow(int index)
        {
            selectedRow = index;
            if (index >= 0) armedDef = null;
        }

        internal int SelectedRow => selectedRow;

        private void OpenList()
        {
            if (listWindow != null && listWindow.IsOpen) return;
            listWindow = new Window_RocketAssemblerListC(comp, this);
            Find.WindowStack.Add(listWindow);
        }

        private void OpenChecks(bool launch)
        {
            if (checksWindow != null && checksWindow.IsOpen) return;
            checksWindow = new Window_AssemblerChecksC(comp, launch, this);
            Find.WindowStack.Add(checksWindow);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  L2：清单 Window
    // ═══════════════════════════════════════════════════════════

    internal sealed class Window_RocketAssemblerListC : Window
    {
        private readonly CompRocketAssembler comp;
        private readonly Window_RocketAssemblerC parent;
        private Vector2 scroll;
        private int selectedRow = -1;
        private int dragFrom = -1;
        private float dragY;
        private bool dragging;
        private readonly List<Rect> rowRects = new();

        public override Vector2 InitialSize => new(640f, 620f);
        protected override float Margin => 0f;

        internal Window_RocketAssemblerListC(CompRocketAssembler comp, Window_RocketAssemblerC parent)
        {
            this.comp = comp;
            this.parent = parent;
            selectedRow = parent.SelectedRow;
            forcePause = false;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = false;
            resizeable = false;
            absorbInputAroundWindow = true;
            doWindowBackground = false;
            doCloseX = false;
        }

        protected override void SetInitialSizeAndPosition()
        {
            Rect p = parent.windowRect;
            windowRect = new Rect(p.x + 137f, p.y + 50f, 640f, 620f);
        }

        public override void ExtraOnGUI()
        {
            Widgets.DrawBoxSolid(parent.windowRect, new Color(0f, 0f, 0f, 0.80f));
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (comp?.parent == null || comp.parent.Destroyed) { Close(); return; }
            Widgets.DrawBoxSolid(inRect, new Color(1f, 1f, 1f, 0.04f));
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.10f);
            Widgets.DrawBox(inRect, 1);
            GUI.color = old;

            GameFont oldFont = Text.Font;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(24f, 28f, 400f, 34f), "装配清单");
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.44f);
            TextAnchor oldAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(inRect.width - 220f, 34f, 140f, 18f),
                $"{comp.Design.parts.Count} 行 / {comp.TotalPartCount} 件");
            Text.Anchor = oldAnchor;
            GUI.color = Color.white;
            Text.Font = oldFont;

            if (Micro(new Rect(inRect.width - 64f, 28f, 24f, 24f), "⋯")) OpenMenu();
            if (Micro(new Rect(inRect.width - 32f, 28f, 24f, 24f), "×")) Close();
            DrawLine(24f, 64f, inRect.width - 24f, 64f, new Color(1f, 1f, 1f, 0.10f));

            var parts = comp.Design.parts;
            Rect outRect = new Rect(24f, 76f, inRect.width - 48f, inRect.height - 156f);
            float viewH = Mathf.Max(outRect.height, parts.Count * 44f + 4f);
            rowRects.Clear();
            Widgets.BeginScrollView(outRect, ref scroll, new Rect(0f, 0f, outRect.width - 18f, viewH));
            for (int i = 0; i < parts.Count; i++)
            {
                DesignPartSpec? p = parts[i];
                if (p == null) continue;
                ShipPartDef? def = DefDatabase<ShipPartDef>.GetNamedSilentFail(p.defName);
                bool selected = i == selectedRow;
                bool bad = parent.IsOffender(i);
                var row = new Rect(0f, i * 44f, outRect.width - 18f, 44f);
                rowRects.Add(row);
                if (selected)
                {
                    Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.07f));
                    Widgets.DrawBoxSolid(new Rect(row.x, row.y, 3f, row.height), ColorLibrary.Gold);
                }
                else if (bad)
                {
                    Widgets.DrawBoxSolid(row, new Color(1f, 0.2f, 0.2f, 0.08f));
                    Widgets.DrawBoxSolid(new Rect(row.x, row.y, 3f, row.height), ColorLibrary.RedReadable);
                }
                DrawText(new Rect(24f, row.y + 14f, 30f, 18f), $"{(i + 1):D2}", GameFont.Tiny, new Color(1f, 1f, 1f, 0.44f));
                DrawText(new Rect(56f, row.y + 13f, 22f, 18f), p.stageId.ToString(CultureInfo.InvariantCulture),
                    GameFont.Tiny, new Color(1f, 1f, 1f, 0.66f));
                int count = RocketDesignRules.PartCountOf(p);
                string name = (def?.label ?? p.defName) + (count > 1 ? $"  ×{count}" : "");
                DrawText(new Rect(88f, row.y + 13f, outRect.width - 160f, 18f), name, GameFont.Small, Color.white);
                DrawText(new Rect(row.width - 46f, row.y + 13f, 30f, 18f), "≡", GameFont.Small,
                    new Color(1f, 1f, 1f, 0.66f), TextAnchor.MiddleRight);
                DrawLine(0f, row.y + 44f, row.width, row.y + 44f, new Color(1f, 1f, 1f, 0.08f));
                if (Widgets.ButtonInvisible(new Rect(0f, row.y, row.width - 64f, row.height)))
                {
                    selectedRow = i;
                    parent.SelectRow(i);
                }
                if (Event.current != null && Event.current.type == EventType.MouseDown && Event.current.button == 0
                    && Event.current.mousePosition.x >= row.width - 58f && Event.current.mousePosition.y >= row.y
                    && Event.current.mousePosition.y <= row.yMax)
                {
                    dragFrom = i;
                    dragging = true;
                    dragY = Event.current.mousePosition.y;
                    Event.current.Use();
                }
            }
            if (dragging && dragFrom >= 0)
            {
                DrawLine(0f, dragY, outRect.width - 18f, dragY, ColorLibrary.Gold, 2f);
                Event? e = Event.current;
                if (e != null && e.type == EventType.MouseDrag) { dragY = e.mousePosition.y; e.Use(); }
                else if (e != null && e.type == EventType.MouseUp)
                {
                    int target = Mathf.Clamp(Mathf.RoundToInt((dragY + scroll.y) / 44f), 0, parts.Count - 1);
                    if (target != dragFrom) comp.MovePart(dragFrom, target - dragFrom);
                    parent.SelectRow(target);
                    selectedRow = target;
                    dragging = false;
                    dragFrom = -1;
                    e.Use();
                }
            }
            Widgets.EndScrollView();

            DrawLine(24f, inRect.height - 80f, inRect.width - 24f, inRect.height - 80f, new Color(1f, 1f, 1f, 0.10f));
            if (Btn(new Rect(24f, inRect.height - 68f, 96f, 30f), "↑ 上移")) Move(-1);
            if (Btn(new Rect(128f, inRect.height - 68f, 96f, 30f), "↓ 下移")) Move(1);
            if (Btn(new Rect(232f, inRect.height - 68f, 96f, 30f), "换级")) OpenStagePicker();
            if (Btn(new Rect(336f, inRect.height - 68f, 96f, 30f), "删除", true, true)) DeleteSelected();
            DrawText(new Rect(24f, inRect.height - 30f, inRect.width - 48f, 18f),
                "点行选中　拖 ≡ 调序　× 关闭", GameFont.Tiny, new Color(1f, 1f, 1f, 0.44f));
        }

        private static void DrawText(Rect r, string s, GameFont font, Color color, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            GameFont oldFont = Text.Font;
            Color oldColor = GUI.color;
            TextAnchor oldAnchor = Text.Anchor;
            Text.Font = font;
            GUI.color = color;
            Text.Anchor = anchor;
            Widgets.Label(r, s);
            Text.Font = oldFont;
            GUI.color = oldColor;
            Text.Anchor = oldAnchor;
        }

        private static void DrawLine(float x1, float y1, float x2, float y2, Color c, float w = 1f)
        {
            Widgets.DrawBoxSolid(new Rect(Mathf.Min(x1, x2), y1, Mathf.Abs(x2 - x1), w), c);
        }

        private static bool Btn(Rect r, string label, bool enabled = true, bool danger = false)
        {
            GameFont old = Text.Font;
            Text.Font = GameFont.Tiny;
            bool clicked = danger
                ? Widgets.ButtonText(r, label, true, true, ColorLibrary.RedReadable, enabled)
                : Widgets.ButtonText(r, label, true, true, enabled);
            Text.Font = old;
            return clicked;
        }

        private static bool Micro(Rect r, string label)
        {
            bool over = Mouse.IsOver(r);
            DrawText(r, label, GameFont.Small, over ? Color.white : new Color(1f, 1f, 1f, 0.66f), TextAnchor.MiddleCenter);
            return Widgets.ButtonInvisible(r);
        }

        private void Move(int delta)
        {
            if (selectedRow < 0 || selectedRow >= comp.Design.parts.Count) return;
            int from = selectedRow;
            if (comp.MovePart(from, delta))
            {
                selectedRow = from + delta;
                parent.SelectRow(selectedRow);
            }
        }

        private void DeleteSelected()
        {
            if (selectedRow < 0 || selectedRow >= comp.Design.parts.Count) return;
            var part = comp.Design.parts[selectedRow];
            int qty = Math.Max(1, part?.quantity ?? 1);
            if (comp.RemovePartAt(selectedRow, qty))
            {
                selectedRow = -1;
                parent.SelectRow(-1);
            }
        }

        private void OpenStagePicker()
        {
            int index = selectedRow;
            if (index < 0 || index >= comp.Design.parts.Count) return;
            var options = new List<FloatMenuOption>();
            for (int st = 0; st <= CompRocketAssembler.MaxStageId; st++)
            {
                int stage = st;
                options.Add(new FloatMenuOption($"级 {stage}", () => comp.SetPartStage(index, stage)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void OpenMenu()
        {
            var options = new List<FloatMenuOption>
            {
                new("清空设计", () => Dialog_MessageBox.CreateConfirmation("清空当前设计？", () =>
                {
                    comp.ClearDesign();
                    parent.SelectRow(-1);
                    Close();
                }, true)),
                new("示范火箭", () => Dialog_MessageBox.CreateConfirmation("示范火箭会替换当前设计，确定？", () =>
                {
                    comp.QuickAssembleDemoRocket(out _);
                    parent.SelectRow(-1);
                    Close();
                }, true)),
                new("反转级号", () => comp.ReverseStages()),
            };
            if (selectedRow >= 0 && selectedRow < comp.Design.parts.Count)
            {
                int stage = comp.Design.parts[selectedRow]?.stageId ?? 0;
                options.Add(new FloatMenuOption("级设置：不抛弃", () => comp.SetStageKeep(stage, !comp.IsStageKeep(stage))));
                options.Add(new FloatMenuOption("级设置：可回收", () => comp.SetStageRecoverable(stage, !comp.IsStageRecoverable(stage))));
                options.Add(new FloatMenuOption("级设置：并联点火", () => comp.SetStageParallel(stage, !comp.IsStageParallel(stage))));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  检查 Window
    // ═══════════════════════════════════════════════════════════

    internal sealed class Window_AssemblerChecksC : Window
    {
        private readonly CompRocketAssembler comp;
        private readonly bool launch;
        private readonly Window_RocketAssemblerC parent;
        private readonly List<Window_RocketAssemblerC.CheckRow> rows;

        public override Vector2 InitialSize => new(420f, launch ? 260f : 340f);
        protected override float Margin => 0f;

        internal Window_AssemblerChecksC(CompRocketAssembler comp, bool launch, Window_RocketAssemblerC parent)
        {
            this.comp = comp;
            this.launch = launch;
            this.parent = parent;
            rows = launch ? parent.GetLaunchChecks() : parent.GetBuildChecks();
            forcePause = false;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = true;
            draggable = false;
            resizeable = false;
            absorbInputAroundWindow = false;
            doWindowBackground = false;
            doCloseX = false;
        }

        protected override void SetInitialSizeAndPosition()
        {
            Rect p = parent.windowRect;
            windowRect = new Rect(p.x + 390f, p.y + 180f, InitialSize.x, InitialSize.y);
        }

        private static void DrawLine(float x1, float y1, float x2, float y2, Color c, float w = 1f)
            => Widgets.DrawBoxSolid(new Rect(Mathf.Min(x1, x2), y1, Mathf.Abs(x2 - x1), w), c);

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.DrawBoxSolid(inRect, new Color(1f, 1f, 1f, 0.04f));
            Color old = GUI.color;
            GUI.color = rows.All(r => r.Pass) ? ColorLibrary.Gold : ColorLibrary.RedReadable;
            Widgets.DrawBoxSolid(new Rect(0f, 0f, 3f, inRect.height), GUI.color);
            GUI.color = old;

            GameFont oldFont = Text.Font;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(20f, 14f, inRect.width - 40f, 22f), launch ? "发射检查" : "建造检查");
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.44f);
            Widgets.Label(new Rect(20f, 36f, inRect.width - 40f, 16f),
                launch ? "按设计口径估算；实际发射由 LaunchService 复核。" : "与 StartBuild 同序同口径。");
            GUI.color = Color.white;
            Text.Font = oldFont;
            DrawLine(0f, 58f, inRect.width, 58f, new Color(1f, 1f, 1f, 0.10f));

            float y = 72f;
            foreach (var row in rows)
            {
                Color mark = row.Pass ? new Color(1f, 1f, 1f, 0.66f) : ColorLibrary.RedReadable;
                Text.Font = GameFont.Small;
                GUI.color = mark;
                Widgets.Label(new Rect(20f, y, 20f, 18f), row.Pass ? "✓" : "×");
                GUI.color = new Color(1f, 1f, 1f, 0.44f);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(44f, y + 3f, 110f, 16f), row.Label);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                TextAnchor oldAnchor = Text.Anchor;
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(150f, y, inRect.width - 170f, 18f), row.Detail);
                Text.Anchor = oldAnchor;
                if (!row.Pass && row.Detail.Length > 26)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.44f);
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(44f, y + 16f, inRect.width - 64f, 16f), row.Detail);
                    GUI.color = Color.white;
                }
                y += 34f;
            }
        }
    }
}
