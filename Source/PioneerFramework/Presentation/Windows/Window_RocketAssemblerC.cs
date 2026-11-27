using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>游戏内火箭组装概览与三维设计窗口。</summary>
    public partial class Window_RocketAssemblerC : Window
    {
        private readonly CompRocketAssembler comp;
        private bool editMode;
        private int selectedRow = -1;
        private ShipPartDef? armedDef;
        private int shelfCategory;
        private Window_AssemblerChecksC? checksWindow;

        private readonly List<int> placementOffenders = new();
        private readonly List<int> contiguityOffenders = new();
        private string placementError = "";
        private string contiguityError = "";
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
        private const float TitleH = 44f;
        private const float BodyY = 60f;
        private const float LeftX = 16f;
        private const float Col1W = 90f;
        private const float CanvasX = 114f;
        private const float RailW = 376f;
        private const float BtnH = 44f;

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

        public override Vector2 InitialSize => new(
            Mathf.Min(1280f, UI.screenWidth - 24f), Mathf.Min(780f, UI.screenHeight - 42f));
        protected override float Margin => 0f;

        public override void DoWindowContents(Rect inRect)
        {
            if (comp?.parent == null || comp.parent.Destroyed) { Close(); return; }
            Recompute();
            Widgets.DrawBoxSolid(inRect, Bg);
            Border(inRect, BorderColor);
            if (!comp.CanEditDesign && editMode) ShowView();
            BeginAssemblyFrame();
            HandleAssemblyDeleteInput();
            DrawTitleBar(inRect);
            if (!IsOpen) return;
            if (editMode) DrawWorkbench(inRect); else DrawView(inRect);
            EndAssemblyFrame();
        }

        public override void PreClose()
        {
            assemblyViewport.Dispose();
            if (checksWindow != null && checksWindow.IsOpen) checksWindow.Close();
            base.PreClose();
        }

        private void Recompute()
        {
            placementOffenders.Clear();
            placementError = comp.EnginePlacementError(placementOffenders);
            contiguityOffenders.Clear();
            contiguityError = comp.StageContiguityError(contiguityOffenders);
            schematic = RocketSchematicBuilder.Build(comp.Design,
                stageGapMeters: RocketDesignRules.StageGapMeters);
        }

        private struct Layout
        {
            public Rect Left;
            public Rect Canvas;
            public Rect Rail;
        }

        private Layout GetLayout(Rect inRect)
        {
            float w = inRect.width;
            float h = inRect.height;
            float top = BodyY;
            float bottom = h - 16f;
            float railW = Mathf.Min(RailW, Mathf.Max(220f, w * 0.40f));
            float railX = w - 16f - railW;
            float canvasX = CanvasX;
            float canvasW = railX - 8f - canvasX;
            return new Layout
            {
                Left = new Rect(LeftX, top, Col1W, bottom - top),
                Canvas = new Rect(canvasX, top, canvasW, bottom - top),
                Rail = new Rect(railX, top, railW, bottom - top)
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
            Title(new Rect(20, 0, Mathf.Max(120f, inRect.width - 355f), TitleH),
                editMode ? "火箭组装器 · 设计" : "火箭组装器");
            float x = inRect.width - 33f;
            if (Micro(new Rect(x, 8f, 24f, 28f), "×"))
            {
                Close();
                return;
            }
            if (editMode)
            {
                x -= 68f;
                if (Btn(new Rect(x, 8f, 64f, 28f), "概览"))
                    ShowView();
            }
            x -= 94f;
            if (Btn(new Rect(x, 8f, 90f, 28f), showSideView ? "收起二维" : "展开二维"))
                showSideView = !showSideView;
            x -= 110f;
            if (comp.CanEditDesign && Btn(new Rect(x, 8f, 106f, 28f), "载入 R-7"))
                RequestEditablePreset();
        }

        private void DrawView(Rect inRect)
        {
            Layout layout = GetLayout(inRect);
            DrawDimension(layout.Left, layout);
            DrawAssemblyViews(layout.Canvas);
            DrawL0Rail(layout.Rail);
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
                    if (Prefs.DevMode) links.Add("立即完工");
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
            else if (link == "立即完工" && Prefs.DevMode && comp.State == AssemblerState.Building)
                comp.ForceComplete();
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
            // 2026-11-26：`Dialog_MessageBox.CreateConfirmation` 只**构造**对话框、不入栈
            // （1.7 的 IL 里没有 WindowStack.Add）⇒ 必须自己 Add，否则点了没有任何反应。
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("下一枚会丢弃当前设计，确定？", () =>
            {
                comp.StartNewRocket();
                SelectRow(-1);
                ShowView();
            }, true));
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
            SelectRow(-1);
            armedDef = null;
            editMode = true;
        }

        private static readonly string[] ShelfCategories = { "全部", "引擎", "工质箱", "结构", "控制", "载荷" };

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

        internal enum BuildCheckKind { Other, Editability, Design, Control, EnginePlacement, StageContiguity, ActiveStage, Work }

        internal readonly struct CheckRow
        {
            public readonly string Label;
            public readonly string Detail;
            public readonly bool Pass;
            public readonly BuildCheckKind Kind;
            public CheckRow(string label, string detail, bool pass, BuildCheckKind kind = BuildCheckKind.Other)
            {
                Label = label;
                Detail = detail;
                Pass = pass;
                Kind = kind;
            }
        }

        internal List<CheckRow> GetBuildChecks()
        {
            var list = new List<CheckRow>();
            list.Add(new CheckRow("可编辑", comp.CanEditDesign ? "是" : "否", comp.CanEditDesign, BuildCheckKind.Editability));
            bool valid = comp.Design.IsValid(out string reason);
            list.Add(new CheckRow("推进器 / 储罐", valid ? "满足" : reason, valid, BuildCheckKind.Design));
            list.Add(new CheckRow("控制核心", $"{comp.ControlCount} 个", comp.ControlCount > 0, BuildCheckKind.Control));
            list.Add(new CheckRow("引擎位置", placementError.Length == 0 ? "通过" : placementError, placementError.Length == 0, BuildCheckKind.EnginePlacement));
            list.Add(new CheckRow("级连续性", contiguityError.Length == 0 ? "通过" : contiguityError, contiguityError.Length == 0, BuildCheckKind.StageContiguity));
            list.Add(new CheckRow("活动级", comp.ActiveStageReady ? "就绪" : comp.StageDiagnosis(), comp.ActiveStageReady, BuildCheckKind.ActiveStage));
            float work = CompRocketAssembler.WorkFor(comp.Design);
            list.Add(new CheckRow("工时", $"{work:F0}", work > 0f, BuildCheckKind.Work));
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
            var rows = comp.Design.parts;
            var part = index >= 0 && index < rows.Count ? rows[index] : null;
            if (!ReferenceEquals(selectedAssemblyPart, part))
            {
                ghostParts.Clear();
                hoveredMount = null;
            }
            selectedAssemblyPart = part;
            selectedRow = part == null ? -1 : index;
            if (selectedRow >= 0) RevealWorkbenchRow(selectedRow);
        }

        internal int SelectedRow => selectedRow;

        internal void ShowView()
        {
            editMode = false;
            armedDef = null;
            ghostParts.Clear();
            hoveredMount = null;
        }

        private void OpenChecks(bool launch)
        {
            if (checksWindow != null && checksWindow.IsOpen) return;
            checksWindow = new Window_AssemblerChecksC(comp, launch, this);
            Find.WindowStack.Add(checksWindow);
        }
    }

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
            // 2026-11-26：同 L2 —— doWindowBackground=false 时必须自铺不透明底。
            Widgets.DrawBoxSolid(inRect, Widgets.WindowBGFillColor);
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
