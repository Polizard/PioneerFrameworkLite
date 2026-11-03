using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// **火箭组装器窗口**（2026-10-28，第一步 + 接发射）。
    ///
    /// 布局：**左侧 = 火箭侧视示意图**（按目录几何 1:1 缩放，自上而下 = spec 顺序 = 上面级 → 主引擎），
    /// 右侧上 = 状态/进度，中 = 目录选件，下 = 当前设计清单，底部 = 建造/发射按钮。
    ///
    /// 2026-10-28 用户意见后的改动：
    /// · 窗口加宽（1180×720）且**可拖拽改大小**——原来右侧会被裁掉；
    /// · 目录滚动高度改为"按行数 + 分类头"实算——原来少算分类头，滚不到主引擎；
    /// · 零件名改为画在**左侧标注栏**（带引导线），不再压在色块上；
    /// · 左图支持**拖动平移 + 滚轮缩放 + 重置视图**，为以后"超级大火箭"留出空间。
    ///
    /// 2026-10-29（并联 / 引擎位置 / 顺序）：
    /// · 目录头加 **并联 1…7** 选择器：点 + = 加 1 站 × N 台，按环对称分布（`ParallelLayout`）；
    /// · 设计清单每行加 **↑/↓**（堆叠顺序前后移动）+ **点件名选中**，用来把引擎摆到本级末端；
    /// · 引擎位置违规行标红、状态条红字说明、建造/发射按钮同步置灰（判据在 `RocketDesignRules`）；
    /// · 左图按并联环画多块（按 Y 深度排序 + 明暗），tooltip 写清 R 与整站跨度。
    /// </summary>
    public class Window_RocketAssembler : Window
    {
        private readonly CompRocketAssembler comp;
        private Vector2 catalogScroll;
        private Vector2 designScroll;
        private List<ShipPartDef> catalogCache = new();

        /// <summary>设计清单里"选中"的那一行（↑/↓ 移动的就是它；2026-10-29 用户令加的顺序调整）。</summary>
        private int selectedRow = -1;

        /// <summary>违规行下标：引擎排在非引擎件之前的那几行（每帧重算，纯读）。</summary>
        private readonly List<int> placementOffenders = new();

        /// <summary>违规说明（每帧算一次，供状态条与按钮 tooltip 共用，避免 GUI 里反复拼串）。</summary>
        private string placementError = "";

        /// <summary>违规行下标：级行被别的级隔开的那几行（2026-10-30，抛级要求同级连成一段）。</summary>
        private readonly List<int> contiguityOffenders = new();

        /// <summary>级行不连续的说明（每帧算一次）。</summary>
        private string contiguityError = "";

        /// <summary>画并联环时的深度排序缓存（避免每帧 new；最多 7 台）。</summary>
        private readonly int[] clusterOrder = new int[ParallelLayout.MaxCount];

        private Vector2 schematicPan;
        private float schematicZoom = 1f;
        private bool dragging;
        private Vector2 dragAnchor;

        private static readonly Color Bg = new(0.055f, 0.062f, 0.078f);
        private static readonly Color Panel = new(0.105f, 0.118f, 0.145f);
        private static readonly Color RowBg = new(0.135f, 0.150f, 0.180f);
        private static readonly Color Accent = new(0.36f, 0.82f, 0.95f);
        private static readonly Color EngineColor = new(0.88f, 0.46f, 0.20f);
        private static readonly Color TankColor = new(0.32f, 0.55f, 0.72f);
        private static readonly Color OtherColor = new(0.46f, 0.47f, 0.52f);
        private static readonly Color BarBg = new(0.04f, 0.05f, 0.06f, 0.85f);
        private static readonly Color Muted = new(0.62f, 0.66f, 0.72f);

        private const float RowH = 26f;
        private const float LabelGutter = 136f;
        private const float CatHeaderH = 18f;

        public Window_RocketAssembler(CompRocketAssembler comp)
        {
            this.comp = comp;
            forcePause = false;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
        }

        public override Vector2 InitialSize => new(1180f, 720f);

        public override void DoWindowContents(Rect inRect)
        {
            if (comp?.parent == null || comp.parent.Destroyed) { Close(); return; }

            // 违规行每帧重算（O(级 × 行)，不用缓存也不用事件）
            placementOffenders.Clear();
            placementError = comp.EnginePlacementError(placementOffenders);
            contiguityOffenders.Clear();
            contiguityError = comp.StageContiguityError(contiguityOffenders);
            if (selectedRow >= comp.Design.parts.Count) selectedRow = comp.Design.parts.Count - 1;

            Widgets.DrawBoxSolid(inRect, Bg);

            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(4f, 0f, inRect.width - 40f, 30f), "火箭组装器");
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(new Rect(inRect.width - 28f, 2f, 26f, 24f), "×")) { Close(); return; }

            GUI.color = Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(110f, 8f, inRect.width - 160f, 20f),
                "左侧：自上而下 = 上面级 → 主引擎；鼠标左键拖动平移，滚轮缩放");
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            float top = 34f;
            float contentH = inRect.height - top - 4f;
            float gap = 12f;
            float leftW = Mathf.Round((inRect.width - gap) * 0.46f);
            var leftRect = new Rect(0f, top, leftW, contentH);
            var rightRect = new Rect(leftW + gap, top, inRect.width - leftW - gap, contentH);

            DrawSchematic(leftRect);
            DrawRight(rightRect);
        }

        // ═══════════════════════════════════════════════════════════
        //  左：火箭侧视图（可拖动 / 可缩放）
        // ═══════════════════════════════════════════════════════════

        private void HandleSchematicInput(Rect view)
        {
            Event e = Event.current;
            if (e == null) return;

            if (Mouse.IsOver(view))
            {
                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    dragging = true;
                    dragAnchor = e.mousePosition;
                    e.Use();
                }
                else if (e.type == EventType.ScrollWheel)
                {
                    schematicZoom = Mathf.Clamp(schematicZoom * (e.delta.y > 0f ? 1.1f : 0.909f), 0.25f, 8f);
                    e.Use();
                }
            }

            if (dragging)
            {
                if (e.type == EventType.MouseDrag && e.button == 0)
                {
                    schematicPan += e.mousePosition - dragAnchor;
                    dragAnchor = e.mousePosition;
                    e.Use();
                }
                else if (e.type == EventType.MouseUp)
                {
                    dragging = false;
                }
            }
        }

        private void DrawSchematic(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, Panel);
            Widgets.BeginGroup(rect);

            var view = new Rect(8f, 6f, rect.width - 16f, rect.height - 12f);
            HandleSchematicInput(view);

            // 重置视图（右上角）
            if (Widgets.ButtonText(new Rect(view.xMax - 86f, view.y, 84f, 22f), "重置视图"))
            {
                schematicPan = Vector2.zero;
                schematicZoom = 1f;
            }

            var s = RocketSchematicBuilder.Build(comp.Design,
                stageGapMeters: RocketDesignRules.StageGapMeters);
            if (s.pieces.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(view.x, view.y, view.width, view.height),
                    "还没有零件。\n右侧点 [+]，这里的火箭会按真实尺寸长出来。");
                Text.Anchor = TextAnchor.UpperLeft;
                Widgets.EndGroup();
                return;
            }

            float usableH = Mathf.Max(40f, view.height - 96f);
            float usableW = Mathf.Max(40f, view.width - LabelGutter - 32f);
            float fit = Mathf.Min(usableW / Mathf.Max(0.1f, s.maxWidthM),
                                  usableH / Mathf.Max(0.1f, s.totalHeightM));
            fit = Mathf.Min(fit, 10f);
            float scale = Mathf.Max(0.05f, fit * schematicZoom);

            float cx = view.x + LabelGutter + (view.width - LabelGutter) * 0.5f + schematicPan.x;
            float y = view.y + 50f + schematicPan.y;

            // 整流罩（阶梯锥，纯装饰）
            float topW = s.pieces[0].widthM * scale;
            for (int i = 0; i < 5; i++)
            {
                float w = topW * (1f - i * 0.17f);
                var c = new Rect(cx - w * 0.5f, y - (5 - i) * 6f, w, 6f);
                Widgets.DrawBoxSolid(c, new Color(0.58f, 0.60f, 0.66f, 0.9f));
            }

            float bodyTop = y;
            float lastLabelY = float.NegativeInfinity;

            for (int i = 0; i < s.pieces.Count; i++)
            {
                SchematicPiece p = s.pieces[i];

                // 级间隔线（0 = 上面级，越大越靠下、越早抛）
                if (i > 0 && p.stageId != s.pieces[i - 1].stageId)
                {
                    // 级间空隙（2026-10-30）：先把 y 让出这段空当，分隔线画在空当中间
                    float gapPx = Mathf.Max(0f, s.stageGapM * scale);
                    if (gapPx > 0f) y += gapPx;
                    Widgets.DrawBoxSolid(new Rect(view.x + LabelGutter, y - 1f,
                        view.width - LabelGutter - 8f, 2f), new Color(Accent.r, Accent.g, Accent.b, 0.55f));
                    Text.Font = GameFont.Tiny;
                    GUI.color = Accent;
                    Widgets.Label(new Rect(view.xMax - 70f, y - 9f, 66f, 14f), $"级 {p.stageId}");
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }

                // 并联站：跨度 = 2R + w；单件时跨度 = 件宽（与旧口径逐位一致）
                float spanW = Mathf.Max(2f, p.widthM * scale);
                float partW = Mathf.Max(2f, p.partWidthM * scale);
                float h = Mathf.Max(3f, p.heightM * scale);
                float drawH = Mathf.Max(2f, h - 1f);
                float stationX = cx - spanW * 0.5f;

                bool visible = y + drawH >= view.y && y <= view.yMax;
                if (visible)
                {
                    DrawStation(y, drawH, p, cx, partW, scale);

                    // 名称画在左侧标注栏（带引导线），不再压在色块上
                    float centerY = y + drawH * 0.5f;
                    if (h >= 9f && centerY - lastLabelY >= 13f)
                    {
                        float lineX = view.x + LabelGutter - 8f;
                        float lineW = Mathf.Max(0f, stationX - lineX - 2f);
                        Widgets.DrawBoxSolid(new Rect(lineX, centerY, lineW, 1f),
                            new Color(1f, 1f, 1f, 0.22f));

                        string name = p.label ?? p.defName;
                        if (p.count > 1) name += $" ×{p.count}";

                        Text.Font = GameFont.Tiny;
                        Text.Anchor = TextAnchor.MiddleRight;
                        GUI.color = new Color(0.86f, 0.89f, 0.94f);
                        Widgets.Label(new Rect(view.x + 2f, centerY - 7f, LabelGutter - 14f, 14f), name);
                        GUI.color = Color.white;
                        Text.Anchor = TextAnchor.UpperLeft;
                        Text.Font = GameFont.Small;
                        lastLabelY = centerY;
                    }

                    string tip = $"{p.label ?? p.defName}"
                        + (p.count > 1
                            ? $"\n并联 {p.count} 台：" + ParallelLayout.Describe(p.count, p.partWidthM)
                              + $"\n整站跨度 {p.widthM:0.##} m · 单件 {p.partWidthM:0.##} × {p.heightM:0.##} m"
                            : $"\n{p.widthM:0.#} × {p.heightM:0.#} m")
                        + $"\n干重 {p.dryMassKg / 1000f:0.###} t × {Mathf.Max(1, p.count)}"
                        + (p.propellantKg > 0f
                            ? $" · 推进剂 {p.propellantKg / 1000f:0.###} t × {Mathf.Max(1, p.count)}"
                            : "");
                    TooltipHandler.TipRegion(new Rect(stationX, y, spanW, drawH), tip);
                }

                y += h;
            }

            // 中轴线
            Widgets.DrawBoxSolid(new Rect(cx - 0.5f, bodyTop, 1f, Mathf.Max(0f, y - bodyTop)),
                new Color(1f, 1f, 1f, 0.10f));

            // 底部读数（三行：几何 / 质量与加注 / 推力与 TWR）
            Text.Font = GameFont.Tiny;
            GUI.color = Muted;
            Widgets.Label(new Rect(view.x, view.yMax - 51f, view.width, 14f),
                $"总高 {s.totalHeightM:F1} m · 最大直径 {s.maxWidthM:F1} m · 比例 1 m ≈ {scale:F1}px"
                + $" · 缩放 {schematicZoom * 100f:F0}%");
            Widgets.Label(new Rect(view.x, view.yMax - 36f, view.width, 14f),
                $"干重 {s.dryMassKg / 1000.0:F2} t · 工质容量 {s.capacityKg / 1000.0:F2} t"
                + $"（已加注 {comp.Design.fuelAmount / 1000.0:F2} t）· {s.stageCount} 级");
            int activeStage = RocketSchematicBuilder.ActiveStage(comp.Design);
            float thrustKn = RocketSchematicBuilder.ThrustN(comp.Design, null, activeStage) / 1000f;
            float weightKn = s.TotalMassKg * 8f / 1000f;
            string twr = weightKn > 0.01f ? $" · TWR≈{thrustKn / weightKn:F2}" : "";
            bool notReady = !comp.ActiveStageReady;
            GUI.color = notReady || (twr != "" && thrustKn <= weightKn)
                ? new Color(1f, 0.55f, 0.45f) : Muted;
            Widgets.Label(new Rect(view.x, view.yMax - 21f, view.width, 14f),
                notReady
                    ? $"⚠ 活动级 {activeStage} 不完整（{comp.EngineCountInStage(activeStage)} 机 / "
                      + $"{comp.TankCountInStage(activeStage)} 罐）：每级要同时有引擎和工质箱 · 当前 {comp.StageDiagnosis()}"
                    : $"活动级 {activeStage} 推力 {thrustKn:F0} kN{twr}（抛级后自动切下一级）"
                      + (s.hasPlaceholder ? " · ⚠ 有件未收录目录" : ""));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            DrawLegend(new Rect(view.x, view.y + 4f, 100f, 54f));
            Widgets.EndGroup();
        }

        /// <summary>
        /// 画一个**轴向站**（2026-10-29）：并联 = 1 时就是原来那一块；并联 &gt; 1 时把 N 件按
        /// 船体系 X 偏移横排，按 Y（深度）从后往前画并做明暗渐变 ⇒ 侧视图能看出"这里不止一件"。
        /// 位置/偏移全部来自 `RocketSchematicBuilder`（纯核），这里只管画。
        /// </summary>
        private void DrawStation(float y, float h, in SchematicPiece p, float cx, float partW, float scale)
        {
            float[]? ox = p.offsetX;
            float[]? oy = p.offsetY;
            int n = p.count;
            if (n <= 1 || ox == null || oy == null)
            {
                DrawPieceBlock(new Rect(cx - partW * 0.5f, y, partW, h), p, 1f);
                return;
            }

            // 深度排序：Y 小（远）先画，Y 大（近）后画 ⇒ 遮挡关系正确
            for (int i = 0; i < n; i++) clusterOrder[i] = i;
            for (int i = 1; i < n; i++)
            {
                int cur = clusterOrder[i];
                int j = i - 1;
                while (j >= 0 && oy[clusterOrder[j]] > oy[cur])
                {
                    clusterOrder[j + 1] = clusterOrder[j];
                    j--;
                }
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
                var r = new Rect(cx + ox[k] * scale - partW * 0.5f, y, partW, h);
                DrawPieceBlock(r, p, shade);
            }
        }

        /// <summary>画一块零件（填充 + 边框 + 引擎喷口）；圆柱件（罐/引擎，2026-10-29）画圆头截面。</summary>
        private static void DrawPieceBlock(Rect r, in SchematicPiece p, float shade)
        {
            Color fill = p.kind switch
            {
                SchematicKind.Engine => EngineColor,
                SchematicKind.Tank => TankColor,
                _ => OtherColor,
            };
            fill *= new Color(shade, shade, shade, 0.92f);

            if (p.shape == InertiaTensor.Shape.Cylinder)
            {
                DrawCylinderBlock(r, fill);   // 圆头已经"自带外轮廓"，不再叠矩形描边
            }
            else
            {
                Widgets.DrawBoxSolid(r, fill);
                Widgets.DrawBox(r, 1);
            }

            if (p.kind == SchematicKind.Engine && r.height > 10f)
            {
                var nozzle = new Rect(r.x + r.width * 0.14f, r.yMax - Mathf.Min(9f, r.height * 0.32f),
                    r.width * 0.72f, Mathf.Min(8f, r.height * 0.30f));
                Widgets.DrawBoxSolid(nozzle, new Color(0.20f, 0.18f, 0.17f, 0.95f));
            }
        }

        /// <summary>
        /// 圆柱件的侧视截面：中间直筒 + 上下**椭圆穹顶**（各 5 条横带，半宽 = half·√(1−(1−u)²)）。
        /// 只影响"看起来像罐"，几何/质量/惯量仍走目录与装配器（这里不参与判据）。
        /// </summary>
        private static void DrawCylinderBlock(Rect r, Color fill)
        {
            float cap = Mathf.Min(r.width * 0.32f, r.height * 0.30f);
            if (cap < 2f || r.width < 4f)
            {
                Widgets.DrawBoxSolid(r, fill);
                return;
            }

            Widgets.DrawBoxSolid(new Rect(r.x, r.y + cap, r.width, r.height - cap * 2f), fill);

            const int strips = 5;
            float stripH = cap / strips;
            float halfMax = r.width * 0.5f;
            for (int i = 0; i < strips; i++)
            {
                float u = (i + 0.5f) / strips;          // 0 = 端口（尖），1 = 直筒边（满宽）
                float k = 1f - u;
                float half = halfMax * Mathf.Sqrt(Mathf.Max(0f, 1f - k * k));
                float w = Mathf.Max(1f, half * 2f);
                float x = r.center.x - w * 0.5f;
                Widgets.DrawBoxSolid(new Rect(x, r.y + i * stripH, w, stripH + 0.6f), fill);
                Widgets.DrawBoxSolid(new Rect(x, r.yMax - (i + 1) * stripH, w, stripH + 0.6f), fill);
            }
        }

        private static void DrawLegend(Rect r)
        {
            (Color c, string t)[] items =
            {
                (EngineColor, "引擎"),
                (TankColor, "工质箱"),
                (OtherColor, "其他件"),
            };
            Text.Font = GameFont.Tiny;
            for (int i = 0; i < items.Length; i++)
            {
                var row = new Rect(r.x, r.y + i * 17f, r.width, 14f);
                Widgets.DrawBoxSolid(new Rect(row.x, row.y + 3f, 10f, 9f), items[i].c);
                GUI.color = Muted;
                Widgets.Label(new Rect(row.x + 14f, row.y, row.width - 14f, 14f), items[i].t);
                GUI.color = Color.white;
            }
            Text.Font = GameFont.Small;
        }

        // ═══════════════════════════════════════════════════════════
        //  右：状态 / 目录 / 设计清单 / 按钮
        // ═══════════════════════════════════════════════════════════

        private void DrawRight(Rect rect)
        {
            float y = rect.y;

            var status = new Rect(rect.x, y, rect.width, 50f);
            DrawStatus(status);
            y += status.height + 8f;

            float footerH = 40f;
            float listH = rect.yMax - y - footerH - 8f;
            float catalogH = Mathf.Round(listH * 0.52f);
            var catalogRect = new Rect(rect.x, y, rect.width, catalogH);
            DrawCatalog(catalogRect);
            y += catalogH + 8f;

            var designRect = new Rect(rect.x, y, rect.width, Mathf.Max(60f, rect.yMax - y - footerH - 4f));
            DrawDesignList(designRect);

            DrawFooter(new Rect(rect.x, rect.yMax - footerH, rect.width, footerH - 4f));
        }

        private void DrawStatus(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, Panel);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 3f, rect.height), Accent);

            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 3f, rect.width - 20f, 20f),
                $"状态：{comp.StateText}　{comp.Design.parts.Count} 行 / {comp.TotalPartCount} 件");

            // 设计门违规（2026-10-29 引擎位置 / 2026-10-30 级行不连续）：任何状态下都先说，
            // 因为它们会挡住建造/发射门。建造/倒计时中设计改不了（新建时已过门），保留进度条。
            if ((placementOffenders.Count > 0 || contiguityOffenders.Count > 0)
                && comp.State != AssemblerState.Building && comp.State != AssemblerState.Launching)
            {
                GUI.color = new Color(1f, 0.55f, 0.45f);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rect.x + 10f, rect.y + 25f, rect.width - 20f, 20f),
                    placementOffenders.Count > 0 ? ShortPlacementError() : ShortContiguityError());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                return;
            }

            if (comp.State == AssemblerState.Building)
            {
                var bar = new Rect(rect.x + 10f, rect.y + 26f, rect.width - 150f, 14f);
                Widgets.DrawBoxSolid(bar, BarBg);
                Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * comp.Progress01, bar.height), Accent);
                Widgets.DrawBox(bar, 1);
                Widgets.Label(new Rect(bar.xMax + 8f, rect.y + 23f, 130f, 18f),
                    $"{comp.Progress01:P0}（{comp.WorkDone:F0}/{comp.WorkRequired:F0}）");
            }
            else if (comp.State == AssemblerState.Launching)
            {
                float f = comp.LaunchSecondsTotal > 0.01f
                    ? 1f - Mathf.Clamp01(comp.LaunchSecondsLeft / comp.LaunchSecondsTotal)
                    : 1f;
                var bar = new Rect(rect.x + 10f, rect.y + 26f, rect.width - 150f, 14f);
                Widgets.DrawBoxSolid(bar, BarBg);
                Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * f, bar.height), EngineColor);
                Widgets.DrawBox(bar, 1);
                Widgets.Label(new Rect(bar.xMax + 8f, rect.y + 23f, 130f, 18f),
                    $"{comp.LaunchSecondsLeft:0.0} 游戏秒");
            }
            else if (comp.State == AssemblerState.Complete)
            {
                if (!comp.ActiveStageReady)
                {
                    GUI.color = new Color(1f, 0.55f, 0.45f);
                    Widgets.Label(new Rect(rect.x + 10f, rect.y + 25f, rect.width - 20f, 20f),
                        $"⚠ 活动级 {comp.ActiveStage} 缺引擎/工质箱（{comp.StageDiagnosis()}）—— 点「下一枚」改设计重建");
                }
                else
                {
                    GUI.color = Accent;
                    Widgets.Label(new Rect(rect.x + 10f, rect.y + 25f, rect.width - 20f, 20f),
                        "组装完成 —— 可发射（倒计时按游戏秒，不是物理秒）。");
                }
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = Muted;
                Widgets.Label(new Rect(rect.x + 10f, rect.y + 25f, rect.width - 20f, 20f),
                    $"工时预算 {CompRocketAssembler.WorkFor(comp.Design):F0}"
                    + $"（建造时需工人在 {comp.Props.workerRadius:F0} 格内）· 第一步不耗材料、无科技门槛");
                GUI.color = Color.white;
            }
        }

        /// <summary>一行能塞下的违规说明（完整原因在 `RocketDesignRules` 里，列表行会标红）。</summary>
        private string ShortPlacementError()
        {
            if (placementOffenders.Count == 0) return "";
            int idx = placementOffenders[0];
            DesignPartSpec? e = idx >= 0 && idx < comp.Design.parts.Count ? comp.Design.parts[idx] : null;
            string name = e?.defName ?? "";
            ShipPartDef? def = e != null ? DefDatabase<ShipPartDef>.GetNamedSilentFail(e.defName) : null;
            return $"⚠ 引擎必须在它那一级的末端：第 {idx + 1} 行「{def?.label ?? name}」把引擎压在上面了"
                + $"（共 {placementOffenders.Count} 行）—— 用 ↑/↓ 把引擎挪到该级最后";
        }

        /// <summary>级行不连续的短说明（2026-10-30；完整原因在 `RocketDesignRules`）。</summary>
        private string ShortContiguityError()
        {
            if (contiguityOffenders.Count == 0) return "";
            int idx = contiguityOffenders[0];
            DesignPartSpec? e = idx >= 0 && idx < comp.Design.parts.Count ? comp.Design.parts[idx] : null;
            string name = e?.defName ?? "";
            ShipPartDef? def = e != null ? DefDatabase<ShipPartDef>.GetNamedSilentFail(e.defName) : null;
            return $"⚠ 同一级的零件必须连成一段：级 {e?.stageId ?? 0} 的第 {idx + 1} 行「{def?.label ?? name}」"
                + $"被别的级隔开了（共 {contiguityOffenders.Count} 行）—— 用 ↑/↓ 把同级零件挪到一起";
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

        /// <summary>目录内容总高：**行数 × 行高 + 分类头**（2026-10-28 修：原来漏算分类头，滚不到主引擎）。</summary>
        private float CatalogContentHeight()
        {
            float h = 0f;
            string last = null!;
            for (int i = 0; i < catalogCache.Count; i++)
            {
                string cat = catalogCache[i].category ?? "";
                if (cat != last) { h += CatHeaderH; last = cat; }
                h += RowH;
            }
            return h;
        }

        private void DrawCatalog(Rect rect)
        {
            EnsureCatalog();
            Widgets.DrawBoxSolid(rect, Panel);

            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 4f, 92f, 20f), "零件目录");

            // 并联台数选择器（2026-10-29 用户令：加件时可选并联 1…7 台）
            GUI.color = Muted;
            Widgets.Label(new Rect(rect.x + 96f, rect.y + 4f, 40f, 20f), "并联");
            GUI.color = Color.white;
            for (int n = ParallelLayout.MinCount; n <= ParallelLayout.MaxCount; n++)
            {
                var b = new Rect(rect.x + 136f + (n - ParallelLayout.MinCount) * 30f, rect.y + 3f, 28f, 22f);
                bool on = comp.CurrentParallel == n;
                if (on) Widgets.DrawBoxSolid(b, new Color(0.20f, 0.34f, 0.44f, 0.95f));
                if (Widgets.ButtonText(b, n.ToString(CultureInfo.InvariantCulture),
                        drawBackground: !on, doMouseoverSound: true, active: comp.CanEditDesign))
                    comp.SetCurrentParallel(n);
                TooltipHandler.TipRegion(b, ParallelTips[n]);
            }

            var outRect = new Rect(rect.x + 6f, rect.y + 26f, rect.width - 12f, rect.height - 32f);
            float viewH = Mathf.Max(outRect.height, CatalogContentHeight() + 4f);
            Widgets.BeginScrollView(outRect, ref catalogScroll, new Rect(0f, 0f, outRect.width - 18f, viewH));

            // 每帧拼一次，所有 + 共用（140 行 × 每帧拼串太浪费）
            string addTip = $"加入 1 站 × 并联 {comp.CurrentParallel} 台 = {comp.CurrentParallel} 件"
                + "\n" + ParallelTips[ParallelLayout.Clamp(comp.CurrentParallel)];

            string lastCat = null!;
            float y = 0f;
            for (int i = 0; i < catalogCache.Count; i++)
            {
                ShipPartDef def = catalogCache[i];
                string cat = def.category ?? "";
                if (cat != lastCat)
                {
                    lastCat = cat;
                    GUI.color = Muted;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(0f, y + 2f, outRect.width, 16f), $"— {CategoryLabel(cat)} —");
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                    y += CatHeaderH;
                }

                var row = new Rect(0f, y, outRect.width, RowH - 2f);
                if (Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, RowBg);

                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 4f, row.y, 132f, row.height), def.label ?? def.defName);
                GUI.color = Muted;
                Widgets.Label(new Rect(row.x + 140f, row.y, row.width - 140f - 34f, row.height), SpecText(def));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                if (Widgets.ButtonText(new Rect(row.xMax - 30f, row.y + 2f, 28f, RowH - 8f),
                        "+", drawBackground: true, doMouseoverSound: true, active: comp.CanEditDesign))
                    comp.AddPart(def, 1);
                TooltipHandler.TipRegion(new Rect(row.xMax - 30f, row.y + 2f, 28f, RowH - 8f), addTip);

                y += RowH;
            }

            Widgets.EndScrollView();
        }

        private void DrawDesignList(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, Panel);

            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 3f, 76f, 20f), "当前设计");
            GUI.color = Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(rect.x + 82f, rect.y + 5f, rect.width - 92f, 18f),
                $"{comp.Design.parts.Count} 行 / {comp.TotalPartCount} 件 · 点件名选中，↑/↓ 调顺序，− 减一件"
                + "（顺序 = 自上而下 = 上面级 → 主引擎）");
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            // 加入级选择器：0 = 上面级（最后点火），数字越大越早抛
            GUI.color = Muted;
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 25f, 58f, 20f), "加入级：");
            GUI.color = Color.white;
            for (int st = 0; st <= CompRocketAssembler.MaxStageId; st++)
            {
                var b = new Rect(rect.x + 70f + st * 34f, rect.y + 24f, 32f, 22f);
                bool on = comp.CurrentStage == st;
                if (on) Widgets.DrawBoxSolid(b, new Color(0.20f, 0.34f, 0.44f, 0.95f));
                if (Widgets.ButtonText(b, st.ToString(CultureInfo.InvariantCulture),
                        drawBackground: !on, doMouseoverSound: true, active: comp.CanEditDesign))
                    comp.SetCurrentStage(st);
                TooltipHandler.TipRegion(b, (st == 0 ? "级 0 = 上面级（最后点火）"
                        : $"级 {st}（比 {st - 1} 更靠下，更早抛）")
                    + $"\n本级：{comp.EngineCountInStage(st)} 引擎 / {comp.TankCountInStage(st)} 工质箱"
                    + (comp.EngineCountInStage(st) == 0 ? "\n⚠ 没有引擎 —— 若它是活动级（最大级号），推力会是 0" : ""));
            }

            // 一键反转级号（"上面级编成 1、一级编成 0" 这种建反的情况）
            var reverse = new Rect(rect.x + 70f + (CompRocketAssembler.MaxStageId + 1) * 34f + 4f,
                rect.y + 24f, 46f, 22f);
            if (Widgets.ButtonText(reverse, "反转", drawBackground: true, doMouseoverSound: true,
                    active: comp.CanEditDesign))
                comp.ReverseStages();
            TooltipHandler.TipRegion(reverse, "把整份设计的级号翻转（0 ↔ 最大级）——建反了的时候一键修正。");

            var parts = comp.Design.parts;
            var outRect = new Rect(rect.x + 6f, rect.y + 50f, rect.width - 12f, rect.height - 56f);
            float viewH = Mathf.Max(outRect.height, parts.Count * RowH + 4f);
            Widgets.BeginScrollView(outRect, ref designScroll, new Rect(0f, 0f, outRect.width - 18f, viewH));

            for (int i = 0; i < parts.Count; i++)
            {
                DesignPartSpec? e = parts[i];
                if (e == null) continue;
                ShipPartDef? def = DefDatabase<ShipPartDef>.GetNamedSilentFail(e.defName);

                int stations = RocketDesignRules.StationCountOf(e);
                int parallel = RocketDesignRules.ParallelOf(e);
                int totalParts = stations * parallel;
                bool offender = placementOffenders.Contains(i) || contiguityOffenders.Contains(i);
                bool selected = i == selectedRow;

                var row = new Rect(0f, i * RowH, outRect.width, RowH - 2f);
                if (selected) Widgets.DrawBoxSolid(row, new Color(0.18f, 0.30f, 0.40f, 0.95f));
                else if (offender) Widgets.DrawBoxSolid(row, new Color(0.34f, 0.14f, 0.12f, 0.75f));
                else if (Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, RowBg);
                if (selected) Widgets.DrawBoxSolid(new Rect(row.x, row.y, 3f, row.height), Accent);

                // 点件名选中（只盖文字区，不和右侧按钮抢点击）
                if (Widgets.ButtonInvisible(new Rect(row.x + 4f, row.y, 192f, row.height), false))
                    selectedRow = i;

                Text.Font = GameFont.Tiny;
                GUI.color = offender ? new Color(1f, 0.72f, 0.66f) : Color.white;
                string rowTitle = (def?.label ?? e.defName) + $" ×{totalParts}"
                    + (parallel > 1 ? $"〔{parallel}联〕" : "");
                Widgets.Label(new Rect(row.x + 6f, row.y, 190f, row.height), rowTitle);
                GUI.color = Muted;
                Widgets.Label(new Rect(row.x + 198f, row.y, Mathf.Max(40f, row.width - 198f - 206f), row.height),
                    def != null ? SpecText(def) : "（目录未收录）");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                TooltipHandler.TipRegion(new Rect(row.x + 4f, row.y, 192f, row.height),
                    $"{def?.label ?? e.defName}\n{stations} 站"
                    + (parallel > 1
                        ? $" × 并联 {parallel} 台 = {totalParts} 件"
                          + (def != null ? "（" + ParallelLayout.Describe(parallel, def.boundsX) + "）" : "")
                        : " × 1 件（在轴上）")
                    + $"\n级 {e.stageId}" + (e.stageId == 0 ? "（上面级，最后点火）" : "（越大越早抛）")
                    + "\n点这一行选中，再用右侧 ↑/↓ 调整堆叠顺序。");

                // ↑/↓：在堆叠顺序里前后移动（引擎要挪到本级最后一行）
                bool canUp = comp.CanEditDesign && i > 0;
                if (Widgets.ButtonText(new Rect(row.xMax - 200f, row.y + 2f, 24f, RowH - 8f), "↑",
                        drawBackground: true, doMouseoverSound: true, active: canUp))
                {
                    selectedRow = comp.MovePart(i, -1) ? i - 1 : i;
                    break;   // 顺序已改，本帧停止遍历
                }
                TooltipHandler.TipRegion(new Rect(row.xMax - 200f, row.y + 2f, 24f, RowH - 8f),
                    "上移（靠机头）。\n引擎必须在它那一级的**最后**一行（喷口朝下）。");

                bool canDown = comp.CanEditDesign && i < parts.Count - 1;
                if (Widgets.ButtonText(new Rect(row.xMax - 172f, row.y + 2f, 24f, RowH - 8f), "↓",
                        drawBackground: true, doMouseoverSound: true, active: canDown))
                {
                    selectedRow = comp.MovePart(i, +1) ? i + 1 : i;
                    break;   // 顺序已改，本帧停止遍历
                }
                TooltipHandler.TipRegion(new Rect(row.xMax - 172f, row.y + 2f, 24f, RowH - 8f),
                    "下移（靠喷口）。引擎应该在它那一级的最后一行。");

                // 级号徽标 + 上下挪级（0 = 上面级，数字越大越早抛）
                var badge = new Rect(row.xMax - 142f, row.y + 4f, 30f, RowH - 10f);
                Widgets.DrawBoxSolid(badge, new Color(0.20f, 0.32f, 0.40f, 0.9f));
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(badge, e.stageId.ToString(CultureInfo.InvariantCulture));
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(badge, $"级 {e.stageId}"
                    + (e.stageId == 0 ? "（上面级，最后点火）" : "（越大越早抛）"));

                if (Widgets.ButtonText(new Rect(row.xMax - 108f, row.y + 2f, 34f, RowH - 8f), "+级",
                        drawBackground: true, doMouseoverSound: true, active: comp.CanEditDesign))
                    comp.ShiftPartStage(i, +1);
                if (Widgets.ButtonText(new Rect(row.xMax - 74f, row.y + 2f, 34f, RowH - 8f), "−级",
                        drawBackground: true, doMouseoverSound: true, active: comp.CanEditDesign))
                    comp.ShiftPartStage(i, -1);

                if (Widgets.ButtonText(new Rect(row.xMax - 38f, row.y + 2f, 32f, RowH - 8f),
                        "−", drawBackground: true, doMouseoverSound: true, active: comp.CanEditDesign))
                {
                    comp.RemovePartAt(i, 1);
                    selectedRow = -1;
                    break;   // 集合已改，本帧停止遍历
                }
            }

            Widgets.EndScrollView();

            if (parts.Count == 0)
            {
                GUI.color = Muted;
                Widgets.Label(new Rect(rect.x + 12f, rect.y + 56f, rect.width - 24f, 40f),
                    "空设计。至少 1 个引擎 + 1 个工质箱才能开始建造。");
                GUI.color = Color.white;
            }
        }

        /// <summary>并联选择器的 tooltip 表（1…7 的摆法逐一说明；静态建一次，不在 GUI 里反复拼串）。</summary>
        private static readonly string[] ParallelTips = BuildParallelTips();

        private static string[] BuildParallelTips()
        {
            var tips = new string[ParallelLayout.MaxCount + 1];
            for (int n = ParallelLayout.MinCount; n <= ParallelLayout.MaxCount; n++)
            {
                if (n <= 1)
                {
                    tips[n] = "1 = 不并联：一件放在轴上。";
                    continue;
                }
                string shape = n switch
                {
                    2 => "两件在 ±X（相切于轴）",
                    3 => "三件环（120°）",
                    4 => "四件十字（90°）",
                    5 => "五件环（72°）",
                    6 => "六件环（60°）",
                    _ => "中心 1 件 + 六件环",
                };
                tips[n] = $"并联 {n} 台：{shape}，绕主推轴对称分布，相邻两件恰好相切。\n"
                    + $"加一件 = 加 1 站 × {n} 台 = {n} 件（台数 × 站数 = 总件数）。\n"
                    + "对称 = 合力矩为 0（偏心推力会逼动量轮一直顶）。";
            }
            return tips;
        }

        private void DrawFooter(Rect rect)
        {
            bool canEdit = comp.CanEditDesign;
            bool valid = comp.Design.IsValid(out string reason);
            string engineError = placementError.Length > 0 ? placementError : contiguityError;
            bool canStart = comp.State == AssemblerState.Designing && valid && engineError.Length == 0;
            bool complete = comp.State == AssemblerState.Complete;
            bool launching = comp.State == AssemblerState.Launching;

            const float bw = 100f;
            const float gap = 6f;
            float x = rect.x;

            if (Widgets.ButtonText(new Rect(x, rect.y, bw, 30f), "开始建造",
                    drawBackground: true, doMouseoverSound: true, active: canStart))
            {
                if (!comp.StartBuild(out string why))
                    Messages.Message(why, MessageTypeDefOf.RejectInput, false);
            }
            if (!canStart)
                TooltipHandler.TipRegion(new Rect(x, rect.y, bw, 30f),
                    launching || comp.State == AssemblerState.Building || complete
                        ? "当前阶段不能重开建造。"
                        : !valid ? reason
                        : engineError.Length > 0 ? engineError
                        : "先选件。");
            x += bw + gap;

            if (Widgets.ButtonText(new Rect(x, rect.y, bw, 30f), "取消建造",
                    drawBackground: true, doMouseoverSound: true, active: comp.State == AssemblerState.Building))
                comp.CancelBuild();
            x += bw + gap;

            if (launching)
            {
                if (Widgets.ButtonText(new Rect(x, rect.y, bw, 30f), "取消发射"))
                    comp.CancelLaunch();
                x += bw + gap;
            }
            else if (complete)
            {
                bool canLaunch = comp.ActiveStageReady && engineError.Length == 0;
                if (Widgets.ButtonText(new Rect(x, rect.y, bw, 30f), "发射",
                        drawBackground: true, doMouseoverSound: true, active: canLaunch))
                {
                    if (!comp.BeginLaunch(out string why))
                        Messages.Message(why, MessageTypeDefOf.RejectInput, false);
                }
                TooltipHandler.TipRegion(new Rect(x, rect.y, bw, 30f),
                    engineError.Length > 0 ? engineError + "用「下一枚」改设计重建。"
                        : canLaunch
                            ? $"倒计时 {comp.Props.launchDelayGameSeconds:0.#} 游戏秒（60 tick/秒），满罐燃料，走真实入轨制导。"
                            : $"活动级 {comp.ActiveStage} 没有引擎（推力 0），发不出去。");
                x += bw + gap;

                if (Widgets.ButtonText(new Rect(x, rect.y, bw, 30f), "下一枚"))
                    comp.StartNewRocket();
                x += bw + gap;
            }
            else
            {
                if (Widgets.ButtonText(new Rect(x, rect.y, bw, 30f), "清空设计",
                        drawBackground: true, doMouseoverSound: true, active: canEdit))
                    comp.ClearDesign();
                x += bw + gap;

                // 2026-10-28 用户令："加一个按钮，立即组装一个分级火箭（我点麻烦了）"
                if (Widgets.ButtonText(new Rect(x, rect.y, bw, 30f), "示范火箭",
                        drawBackground: true, doMouseoverSound: true, active: canEdit))
                {
                    if (!comp.QuickAssembleDemoRocket(out string whyDemo))
                        Messages.Message(whyDemo, MessageTypeDefOf.RejectInput, false);
                }
                TooltipHandler.TipRegion(new Rect(x, rect.y, bw, 30f),
                    "一键：两级示范火箭（上面级 控制核心 + 100 t 罐 + 1 机；一级 400 t 罐 + 12 机 = 并联 6 × 2 站），"
                    + "每级都是「先罐后引擎」⇒ 引擎在本级末端；并立即完工（含满罐加注），直接可发射。");
                x += bw + gap;
            }

            if (Prefs.DevMode && comp.State == AssemblerState.Building
                && Widgets.ButtonText(new Rect(x, rect.y, bw, 30f), "（调试）完工"))
                comp.ForceComplete();
        }

        private static string CategoryLabel(string cat) => cat switch
        {
            "thruster" => "引擎",
            "tank" => "工质箱",
            "rcs" => "RCS 推进器",
            "rcs-tank" => "RCS 工质箱",
            "stage" => "级间结构",
            "hull" => "结构",
            "cabin" => "舱段",
            "reactor" => "反应堆",
            "battery" => "电池",
            "radiator" => "散热器",
            "cargo" => "货舱",
            "weapon" => "武器挂点",
            "control" => "控制核心",
            "payload-bay" => "载荷舱",
            _ => string.IsNullOrEmpty(cat) ? "未分类" : cat,
        };

        private static string SpecText(ShipPartDef def)
        {
            if (def == null) return "";
            if (def.category == "thruster" || def.category == "rcs")
            {
                double thrustKn = def.exhaustVelocity * def.massFlowRate / 1000.0;
                return $"ve {def.exhaustVelocity:F0} · {thrustKn:F0} kN · {def.massKg / 1000.0:F2} t";
            }
            if (def.category == "tank" || def.category == "rcs-tank")
                return $"容量 {def.tankCapacityKg / 1000.0:F1} t · 件重 {def.massKg / 1000.0:F2} t";
            return $"{def.massKg:F0} kg";
        }
    }
}
