using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using PioneerFramework.Launch;
using PioneerFramework.UiKit;

namespace PioneerFramework.GroundAssembly
{
    /// <summary>
    /// 航天器管理界面 — 前端 UI。
    /// 继承 Window，使用 TabDrawer 渲染 6 个标签页：总览/设计/施工/舱内/发射/轨迹。
    /// 视觉风格：瑞士国际主义栅格 + 磁带盒未来主义暖调近黑 + 复古未来主义饱和强调色。
    /// 本期仅前端实现，所有按钮事件处理器为占位虚方法 OnXxx()，默认仅 Log.Message。
    /// 后端 Agent 通过 override 这些虚方法或订阅事件接通真实业务。
    /// </summary>
    public class Window_SpacecraftManagement : Window
    {
        private readonly Building? pad;
        private readonly CompAssemblyPad? padComp;

        private Tab currentTab = Tab.Overview;

        private enum Tab { Overview, Design, Construction, Cabin, Launch, Trajectory }

        // ── 间距常量（瑞士国际主义 8px 基线节奏） ──
        // 注：高度值已按 RimWorld GameFont 实际渲染高度（含行高 + 内边距）校准，
        // 避免 TextAnchor.UpperLeft 下文字溢出到相邻区域被背景遮挡。
        //   GameFont.Medium ≈ 24–28px 渲染 → Title  42f
        //   GameFont.Small  ≈ 18–20px 渲染 → Row    26f / Button 36f
        private const float TitleHeight = 42f;
        // TabDrawer 实际绘制：Tab 按钮底部只压住 baseRect 1px，按钮主体向上延伸 31px。
        // 因此 TabGap 必须 >= 31px；内容区从 tabRect.yMax 开始，避免按钮与内容之间出现 35px 空白。
        private const float TabGap = 34f;
        private const float TabBottomOverlap = 1f;
        private const float SectionGap = 8f;
        private const float RowHeight = 26f;
        private const float RowGap = 4f;
        private const float ButtonHeight = 36f;
        private const float ButtonGap = 12f;
        // 内容区与 Tab 栏之间的顶部间距：比卡片内边距更小，避免页面标题离 Tab 太远。
        private const float ContentTopPad = 6f;
        private const float CardPad = 12f;
        private const float LabelWidth = 180f;

        // UI 内部局部状态（不写回后端）
        private RocketLaunchMode selectedLaunchMode;
        private string altitudeInputBuffer = "400";
        private float targetOrbitAltitude = 400f;
        private bool autoCircularize = true;

        // 缓存的发射上下文（每次 DoWindowContents 重新获取以保证新鲜）
        private RocketLaunchContext? cachedContext;

        public Window_SpacecraftManagement(Building? pad)
        {
            this.pad = pad;
            padComp = pad?.GetComp<CompAssemblyPad>();
            selectedLaunchMode = padComp?.LaunchMode ?? RocketLaunchMode.PflOrbit;

            doCloseX = true;
            doCloseButton = false;
            closeOnCancel = true;
            absorbInputAroundWindow = true;
            forcePause = true;
            resizeable = true;
            draggable = true;
        }

        public override Vector2 InitialSize => new(760f, 606f);

        public override void DoWindowContents(Rect inRect)
        {
            // 缓存发射上下文（只读查询，不产生副作用）
            cachedContext = null;
            if (pad != null)
                TryResolveRocketAndContext(out _, out cachedContext);

            // 1. 窗口背景 — 暖调近黑 CRT 屏底
            SpacecraftUIDecor.DrawWindowBackground(inRect);

            // 2. 主标题（Display, 银白）— 垂直居中，避免 GameFont.Medium 文字溢出到 Tab 栏
            Rect titleRect = new Rect(inRect.x, inRect.y, inRect.width, TitleHeight);
            string title = pad != null ? $"航天器管理 — {pad.LabelCap}" : "航天器管理";
            SpacecraftUIDecor.DrawColoredLabel(titleRect, title,
                SpacecraftUIPalette.SpecTextTitle, GameFont.Medium, TextAnchor.MiddleLeft);

            // 3. Tab 栏区域（留 TabGap 间距：Tab 按钮顶部向上突出圆弧造型，
            // 紧贴主标题会覆盖主标题底部文字）
            Rect tabRect = new Rect(inRect.x, inRect.y + TitleHeight + TabGap, inRect.width, TabBottomOverlap);
            DrawTabs(tabRect);

            // 4. 内容区（含 L 形角标）— Tab 按钮底部只压住 tabRect 1px，内容区从 tabRect.yMax 开始
            float contentY = tabRect.yMax;
            Rect contentRect = new Rect(inRect.x, contentY, inRect.width, inRect.yMax - contentY);
            SpacecraftUIDecor.DrawCornerLBrackets(contentRect, SpacecraftUIPalette.ColBorder);

            switch (currentTab)
            {
                case Tab.Overview:     DrawOverviewTab(contentRect); break;
                case Tab.Design:       DrawDesignTab(contentRect); break;
                case Tab.Construction: DrawConstructionTab(contentRect); break;
                case Tab.Cabin:        DrawCabinTab(contentRect); break;
                case Tab.Launch:       DrawLaunchTab(contentRect); break;
                case Tab.Trajectory:   DrawTrajectoryTab(contentRect); break;
            }
        }

        /// <summary>
        /// 尝试解析当前发射台中心位置的火箭并获取发射上下文。
        /// 前端只读查询，不产生副作用。
        /// </summary>
        private bool TryResolveRocketAndContext(out Thing? rocket, out RocketLaunchContext? context)
        {
            rocket = null;
            context = null;
            if (pad == null || pad.Map == null || padComp == null)
                return false;

            IntVec3 center = pad.OccupiedRect().CenterCell;
            rocket = pad.Map.thingGrid.ThingsListAt(center)
                .FirstOrDefault(t => LaunchPadUtility.IsRocket(t.def));
            if (rocket == null)
                return false;

            return RocketLaunchManager.TryGetLaunchContext(rocket, out context);
        }

        private Thing? GetManagedRocket()
        {
            if (padComp?.BuiltRocket != null && padComp.BuiltRocket.Spawned)
                return padComp.BuiltRocket;

            TryResolveRocketAndContext(out Thing? rocket, out _);
            return rocket;
        }

        private Building? GetUmbilicalTower()
        {
            if (pad == null || pad.Map == null)
                return null;

            ThingDef towerDef = DefDatabase<ThingDef>.GetNamedSilentFail("PF_Building_Rocket_UmbilicalTower");
            if (towerDef == null)
                return null;

            foreach (Thing thing in pad.Map.listerThings.ThingsOfDef(towerDef))
            {
                if (thing is Building tower && tower.TryGetComp<CompUmbilicalTower>()?.AssemblyPad == pad)
                    return tower;
            }

            return null;
        }

        private void DrawTabs(Rect rect)
        {
            var tabs = new List<TabRecord>
            {
                new("总览", () => currentTab = Tab.Overview,      () => currentTab == Tab.Overview),
                new("设计", () => currentTab = Tab.Design,       () => currentTab == Tab.Design),
                new("施工", () => currentTab = Tab.Construction, () => currentTab == Tab.Construction),
                new("舱内", () => currentTab = Tab.Cabin,        () => currentTab == Tab.Cabin),
                new("发射", () => currentTab = Tab.Launch,       () => currentTab == Tab.Launch),
                new("轨迹", () => currentTab = Tab.Trajectory,    () => currentTab == Tab.Trajectory),
            };
            TabDrawer.DrawTabs(rect, tabs);
            DrawCurrentTabAccentLine(rect);
        }

        private void DrawCurrentTabAccentLine(Rect rect)
        {
            const float maxTabWidth = 200f;
            const float lineHeight = 2f;
            const int tabCount = 6;
            float totalWidth = rect.width + (tabCount - 1) * TabDrawer.TabHoriztonalOverlap;
            float tabWidth = totalWidth / tabCount;
            if (tabWidth > maxTabWidth)
                tabWidth = maxTabWidth;

            int selectedIndex = (int)currentTab;
            float x = rect.x + selectedIndex * (tabWidth - TabDrawer.TabHoriztonalOverlap);
            float y = rect.yMax - lineHeight;

            Color accent = TabAccentColor(currentTab);
            GUI.color = accent;
            Widgets.DrawBoxSolid(new Rect(x, y, tabWidth, lineHeight), accent);
            GUI.color = Color.white;
        }

        private static Color TabAccentColor(Tab tab)
        {
            return tab switch
            {
                Tab.Overview     => SpacecraftUIPalette.SpecWarmCopper,
                Tab.Design       => SpacecraftUIPalette.SpecTextTitle,
                Tab.Construction => SpacecraftUIPalette.SpecAccentMilitary,
                Tab.Cabin        => SpacecraftUIPalette.SpecColdCyan,
                Tab.Launch       => SpacecraftUIPalette.SpecWarmCopper,
                Tab.Trajectory   => SpacecraftUIPalette.SpecColdCyan,
                _                => SpacecraftUIPalette.ColTextMuted
            };
        }

        private static string TabTitle(Tab tab)
        {
            return tab switch
            {
                Tab.Overview     => "总览",
                Tab.Design       => "设计",
                Tab.Construction => "施工",
                Tab.Cabin        => "舱内",
                Tab.Launch       => "发射",
                Tab.Trajectory   => "轨迹",
                _ => "未知"
            };
        }

        // ── 4.1 总览页 ──
        private void DrawOverviewTab(Rect rect)
        {
            float y = rect.y + ContentTopPad;

            // 标签页标题
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "航天器总览", SpacecraftUIPalette.SpecTextTitle, GameFont.Medium, TextAnchor.MiddleLeft);
            y += RowHeight + SectionGap;

            // SpecBar 状态行卡片（§8.1 暖铜色左边条 + L 形角标）
            RocketDesignSpec? design = padComp?.Design;
            Rect specBarRect = new Rect(rect.x, y, rect.width, RowHeight * 4 + CardPad * 2);
            Rect specInner = SpacecraftUIDecor.DrawSpecCard(specBarRect, SpacecraftUIPalette.SpecWarmCopper);

            // 卡片内部分割线（§7.2）
            SpacecraftUIDecor.DrawDivider(specInner, specInner.y, specInner.width);
            float innerY = specInner.y + SectionGap;

            LaunchPadState overviewState = padComp?.State ?? LaunchPadState.Idle;
            DrawInfoLine(specInner, ref innerY, RowHeight, "发射台状态", StateToSymbolText(overviewState), StateColor(overviewState));
            DrawInfoLine(specInner, ref innerY, RowHeight, "设计型号", design != null ? (design.rocketDefName ?? "无设计") : "无设计");
            DrawInfoLine(specInner, ref innerY, RowHeight, "舱室类型", DeriveCabinType(design));

            int pocketMapId = padComp?.PocketMapId ?? -1;
            bool hasPocketMap = pocketMapId >= 0;
            DrawInfoLine(specInner, ref innerY, RowHeight, "舱内地图",
                hasPocketMap ? $"● 已生成 #{pocketMapId}" : "○ 未生成",
                hasPocketMap ? SpacecraftUIPalette.SpecWarmCopper : SpacecraftUIPalette.ColTextMuted);
            y = specBarRect.yMax + SectionGap;

            // 设计摘要
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "设计摘要", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + RowGap;

            if (design != null)
            {
                DrawInfoLinePair(rect, ref y, RowHeight,
                    "框架尺寸",   $"{design.frameSize}×{design.frameSize}",
                    "口袋地图",   $"{design.mapWidth}×{design.mapHeight}");
                DrawInfoLinePair(rect, ref y, RowHeight,
                    "燃料量",     $"{design.fuelAmount:0.#}",
                    "Part 总数",  $"{(design.parts?.Count ?? 0)}");
                DrawInfoLinePair(rect, ref y, RowHeight,
                    "载荷总数",   $"{(design.payloads?.Count ?? 0)}",
                    "对接口",     $"{(design.dockingPorts?.Count ?? 0)}");
            }
            else
            {
                SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + 16f, y, rect.width, RowHeight),
                    "（无设计数据）", SpacecraftUIPalette.ColTextMuted, GameFont.Small, TextAnchor.MiddleLeft);
                y += RowHeight;
            }

            y += SectionGap;
            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            string modeText = (padComp?.LaunchMode ?? RocketLaunchMode.PflOrbit) == RocketLaunchMode.VanillaTransportPod
                ? "原版 TransportPod" : "PFL 入轨";
            DrawInfoLine(rect, ref y, RowHeight, "发射方式", modeText);

            y += SectionGap;
            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            // 底部按钮区（§8.1 暖铜主操作 + 锈红警告边框）
            Rect btnRecoverable = new Rect(rect.x, y, 200f, ButtonHeight);
            Rect btnDisposable  = new Rect(rect.x + 200f + ButtonGap, y, 200f, ButtonHeight);
            Rect btnCancel      = new Rect(rect.x + (200f + ButtonGap) * 2f, y, 140f, ButtonHeight);

            // 已完工后不允许取消设计：避免清空已完成火箭但遗留已生成的航天器地图。
            bool canCancel = padComp != null
                && (padComp.State == LaunchPadState.Designing || padComp.State == LaunchPadState.Building);

            if (SpacecraftUIDecor.DrawPrimaryButton(btnRecoverable, "新建可回收火箭设计",
                    SpacecraftUIPalette.SpecWarmCopper, SpacecraftUIPalette.SpecWarmCopperHover,
                    SpacecraftUIPalette.SpecWarmWhite))
                OnNewDesign(true);
            if (SpacecraftUIDecor.DrawPrimaryButton(btnDisposable, "新建不可回收火箭设计",
                    SpacecraftUIPalette.SpecWarmCopper, SpacecraftUIPalette.SpecWarmCopperHover,
                    SpacecraftUIPalette.SpecWarmWhite))
                OnNewDesign(false);
            if (SpacecraftUIDecor.DrawOutlinedButton(btnCancel, "取消设计",
                    SpacecraftUIPalette.SpecDanger, SpacecraftUIPalette.SpecDanger, canCancel))
                OnCancelDesign();

            y += ButtonHeight + SectionGap;

        }

        // ── 4.2 设计页（纯只读） ──
        private void DrawDesignTab(Rect rect)
        {
            float y = rect.y + ContentTopPad;

            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "设计规格 — 只读展示", SpacecraftUIPalette.SpecTextTitle, GameFont.Medium, TextAnchor.MiddleLeft);
            y += RowHeight + SectionGap;

            // 运输仓类型选择（航天舱室/普通运输仓）
            RocketDesignSpec? design = padComp?.Design;
            if (design == null)
            {
                SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                    "（无设计数据，请在总览页新建设计）",
                    SpacecraftUIPalette.ColTextMuted, GameFont.Small, TextAnchor.MiddleLeft);
                return;
            }

            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "运输仓类型", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + RowGap;

            bool isNormal = design.CabinType == RocketCabinType.None;
            bool isSpacecraft = design.CabinType == RocketCabinType.Spacecraft;

            Rect normalTypeRect = new Rect(rect.x + 16f, y, rect.width - 16f, 30f);
            SpacecraftUIDecor.DrawColoredLabel(normalTypeRect,
                $"{(isNormal ? "●" : "○")}  普通运输仓（无舱内地图）",
                isNormal ? SpacecraftUIPalette.SpecWarmCopper : SpacecraftUIPalette.ColTextMuted,
                GameFont.Small, TextAnchor.MiddleLeft);
            if (Widgets.ButtonInvisible(normalTypeRect, true))
                OnSetCabinType(RocketCabinType.None);
            y += 36f;

            Rect cabinTypeRect = new Rect(rect.x + 16f, y, rect.width - 16f, 30f);
            SpacecraftUIDecor.DrawColoredLabel(cabinTypeRect,
                $"{(isSpacecraft ? "●" : "○")}  航天舱室（首次登船生成舱内地图）",
                isSpacecraft ? SpacecraftUIPalette.SpecColdCyan : SpacecraftUIPalette.ColTextMuted,
                GameFont.Small, TextAnchor.MiddleLeft);
            if (Widgets.ButtonInvisible(cabinTypeRect, true))
                OnSetCabinType(RocketCabinType.Spacecraft);
            y += 40f;

            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            // 框架尺寸编辑
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, 220f, RowHeight),
                "框架尺寸 (格)", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            Rect frameInputRect = new Rect(rect.x + 230f, y - 2f, 80f, 28f);
            string frameBuffer = Widgets.TextField(frameInputRect, design.frameSize.ToString());
            if (int.TryParse(frameBuffer, out int parsedFrame) && parsedFrame >= 1 && parsedFrame <= 20
                && design.frameSize != parsedFrame)
            {
                design.frameSize = parsedFrame;
            }
            y += RowHeight + RowGap;

            // 基础信息卡片
            Rect baseCardRect = new Rect(rect.x, y, rect.width, RowHeight * 6 + CardPad * 2);
            SpacecraftUIDecor.DrawCardBackground(baseCardRect, SpacecraftUIPalette.SpecPanelBg);
            SpacecraftUIDecor.DrawCornerLBrackets(baseCardRect, SpacecraftUIPalette.ColBorder);

            // 卡片内部分割线（§7.2）
            SpacecraftUIDecor.DrawDivider(new Rect(rect.x + CardPad, 0f, rect.width - CardPad * 2f, 1f), baseCardRect.y + CardPad, baseCardRect.width - CardPad * 2f);
            float innerY = baseCardRect.y + CardPad + SectionGap;
            DrawInfoLine(rect, ref innerY, RowHeight, "型号 DefName",      design.rocketDefName ?? "—");
            DrawInfoLine(rect, ref innerY, RowHeight, "框架尺寸",          $"{design.frameSize}×{design.frameSize}");
            DrawInfoLine(rect, ref innerY, RowHeight, "口袋地图尺寸",      $"{design.mapWidth}×{design.mapHeight}");
            DrawInfoLine(rect, ref innerY, RowHeight, "燃料量",            $"{design.fuelAmount:0.#}");
            DrawInfoLine(rect, ref innerY, RowHeight, "舱室类型",          DeriveCabinType(design));
            DrawInfoLine(rect, ref innerY, RowHeight, "是否生成舱内地图",  DeriveCabinType(design) == "航天舱室" ? "是" : "否");
            y = baseCardRect.yMax + SectionGap;

            // Part 编辑按钮
            Rect btnAddThruster = new Rect(rect.x, y, 120f, ButtonHeight);
            Rect btnAddTank = new Rect(rect.x + 132f, y, 120f, ButtonHeight);
            Rect btnRemovePart = new Rect(rect.x + 264f, y, 132f, ButtonHeight);
            bool hasParts = design.parts != null && design.parts.Count > 0;
            if (Widgets.ButtonText(btnAddThruster, "添加推进器", true))
                OnAddPart(new DesignPartSpec { defName = "PF_ShipPart_Thruster_Debug", category = "thruster", quantity = 1, installPosition = "engine" });
            if (Widgets.ButtonText(btnAddTank, "添加储罐", true))
                OnAddPart(new DesignPartSpec { defName = "PF_ShipPart_Tank_Debug", category = "tank", quantity = 1, installPosition = "body" });
            if (Widgets.ButtonText(btnRemovePart, "移除最后一个", hasParts))
                OnRemovePartLast();
            y += ButtonHeight + SectionGap;

            // Part 列表（序号标签 #01/#02）
            int partsCount = design.parts?.Count ?? 0;
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                $"Part 列表 ({partsCount} 项)", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + RowGap;

            if (design.parts != null && design.parts.Count > 0)
            {
                int i = 0;
                foreach (var part in design.parts)
                {
                    if (part == null) continue;
                    i++;
                    string partLine = $"#{i:D2}  category=[{part.category}]  defName=[{part.defName}]  qty={part.quantity}  pos={part.installPosition}";
                    SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + 16f, y, rect.width - 16f, RowHeight),
                        partLine, SpacecraftUIPalette.ColTextBody, GameFont.Tiny, TextAnchor.MiddleLeft);
                    y += RowHeight;
                }
            }
            else
            {
                SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + 16f, y, rect.width, RowHeight),
                    "（空）", SpacecraftUIPalette.ColTextMuted, GameFont.Small, TextAnchor.MiddleLeft);
                y += RowHeight;
            }

            y += SectionGap;

            // 载荷编辑按钮
            Rect btnAddPayload = new Rect(rect.x, y, 160f, ButtonHeight);
            Rect btnRemovePayload = new Rect(rect.x + 172f, y, 160f, ButtonHeight);
            bool hasPayloads = design.payloads != null && design.payloads.Count > 0;
            if (Widgets.ButtonText(btnAddPayload, "添加调试载荷", true))
                OnAddPayload(new RocketPayloadSpec { defName = "PF_Payload_Debug", installPosition = "nose", quantity = 1 });
            if (Widgets.ButtonText(btnRemovePayload, "移除最后一个", hasPayloads))
                OnRemovePayloadLast();
            y += ButtonHeight + SectionGap;

            // 载荷列表（§8.2 网点纹理背景）
            int payloadsCount = design.payloads?.Count ?? 0;
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                $"载荷列表 ({payloadsCount} 项)", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + RowGap;

            // 载荷列表内容区：卡片底 + 网点纹理背景
            int payloadRows = payloadsCount > 0 ? payloadsCount : 1;
            float payloadListHeight = payloadRows * RowHeight + CardPad * 2f;
            Rect payloadListRect = new Rect(rect.x, y, rect.width, payloadListHeight);
            SpacecraftUIDecor.DrawCardBackground(payloadListRect, SpacecraftUIPalette.SpecPanelBg);
            SpacecraftUIDecor.DrawDotGrid(payloadListRect, SpacecraftUIPalette.ColBorder, 8f);

            float payloadY = payloadListRect.y + CardPad;
            if (design.payloads != null && design.payloads.Count > 0)
            {
                int i = 0;
                foreach (var payload in design.payloads)
                {
                    if (payload == null) continue;
                    i++;
                    string payloadLine = $"#{i:D2}  defName=[{payload.defName}]  pos={payload.installPosition}  qty={payload.quantity}";
                    SpacecraftUIDecor.DrawColoredLabel(
                        new Rect(payloadListRect.x + CardPad, payloadY, payloadListRect.width - CardPad * 2f, RowHeight),
                        payloadLine, SpacecraftUIPalette.ColTextBody, GameFont.Tiny, TextAnchor.MiddleLeft);
                    payloadY += RowHeight;
                }
            }
            else
            {
                SpacecraftUIDecor.DrawColoredLabel(
                    new Rect(payloadListRect.x + CardPad, payloadY, payloadListRect.width - CardPad * 2f, RowHeight),
                    "（空）", SpacecraftUIPalette.ColTextMuted, GameFont.Small, TextAnchor.MiddleLeft);
            }
            y = payloadListRect.yMax;
        }

        // ── 4.3 施工页 ──
        private void DrawConstructionTab(Rect rect)
        {
            float y = rect.y + ContentTopPad;

            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "施工状态", SpacecraftUIPalette.SpecTextTitle, GameFont.Medium, TextAnchor.MiddleLeft);
            y += RowHeight + SectionGap;

            LaunchPadState state = padComp?.State ?? LaunchPadState.Idle;

            // 状态机路径
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "状态机: [空闲] → [设计中] → [建造中] → [已完工] → (发射)",
                SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight;

            // 当前态高亮（军绿）
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                $"          ★当前: [{StateToText(state)}]",
                SpacecraftUIPalette.SpecAccentMilitary, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + SectionGap;

            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            // SpecBar 火箭关联状态卡片（§8.3 军绿色左边条 + L 形角标）
            Thing? rocket = cachedContext?.Rocket;
            bool hasRocket = rocket != null && rocket.Spawned;

            Rect rocketBarRect = new Rect(rect.x, y, rect.width, RowHeight * 4 + CardPad * 2);
            Rect rocketInner = SpacecraftUIDecor.DrawSpecCard(rocketBarRect, SpacecraftUIPalette.SpecAccentMilitary);

            // 卡片内部分割线（§7.2）
            SpacecraftUIDecor.DrawDivider(rocketInner, rocketInner.y, rocketInner.width);
            float innerY = rocketInner.y + SectionGap;
            DrawInfoLine(rocketInner, ref innerY, RowHeight, "已关联火箭 Building", hasRocket ? "● 是" : "○ 否",
                hasRocket ? SpacecraftUIPalette.SpecWarmCopper : SpacecraftUIPalette.ColTextMuted);

            if (hasRocket && rocket != null)
            {
                DrawInfoLine(rocketInner, ref innerY, RowHeight, "  • DefName", rocket.def?.defName ?? "未知");
                DrawInfoLine(rocketInner, ref innerY, RowHeight, "  • 位置",   rocket.Position.ToString());
                DrawInfoLine(rocketInner, ref innerY, RowHeight, "  • Spawned", rocket.Spawned.ToString());
            }
            y = rocketBarRect.yMax + SectionGap;

            // 施工进度（§7.4 状态指示符：✓ 军绿 / ✗ 锈红）
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "施工进度", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + RowGap;

            DrawStatusInfoLine(rect, ref y, RowHeight, "  • 框架生成",
                state == LaunchPadState.Building || state == LaunchPadState.Completed);
            DrawStatusInfoLine(rect, ref y, RowHeight, "  • 完工",
                state == LaunchPadState.Completed);
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "  • 缺失材料清单: (后续版本接入)",
                SpacecraftUIPalette.ColTextMuted, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + SectionGap;

            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            // 按钮（§8.3 军绿确认主操作；调试发射统一收敛到发射页 dev 区）
            Rect btnConfirm = new Rect(rect.x, y, 220f, ButtonHeight);
            bool canConfirm = state == LaunchPadState.Designing;

            if (SpacecraftUIDecor.DrawPrimaryButton(btnConfirm, "确认设计并生成框架",
                    SpacecraftUIPalette.SpecMilitaryButton, SpacecraftUIPalette.SpecMilitaryButtonHover,
                    SpacecraftUIPalette.SpecWarmWhite, canConfirm))
                OnConfirmDesignAndBuild();

            y += ButtonHeight + SectionGap;

        }

        // ── 4.4 舱内页 ──
        private void DrawCabinTab(Rect rect)
        {
            float y = rect.y + ContentTopPad;

            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "舱内口袋地图", SpacecraftUIPalette.SpecTextTitle, GameFont.Medium, TextAnchor.MiddleLeft);
            y += RowHeight + SectionGap;

            Map? pocketMap = padComp?.GetPocketMap();
            int pocketMapId = padComp?.PocketMapId ?? -1;
            bool hasMap = pocketMap != null;

            // 卡片（L 形角标）
            Rect cabinCardRect = new Rect(rect.x, y, rect.width, RowHeight * 4 + CardPad * 2);
            SpacecraftUIDecor.DrawCardBackground(cabinCardRect, SpacecraftUIPalette.SpecPanelBg);
            SpacecraftUIDecor.DrawCornerLBrackets(cabinCardRect, SpacecraftUIPalette.ColBorder);

            // 卡片内部分割线（§7.2）
            SpacecraftUIDecor.DrawDivider(new Rect(rect.x + CardPad, 0f, rect.width - CardPad * 2f, 1f), cabinCardRect.y + CardPad, cabinCardRect.width - CardPad * 2f);
            float innerY = cabinCardRect.y + CardPad + SectionGap;
            DrawInfoLine(rect, ref innerY, RowHeight, "地图状态", hasMap ? "● 已生成" : "○ 未生成",
                hasMap ? SpacecraftUIPalette.SpecWarmCopper : SpacecraftUIPalette.ColTextMuted);
            DrawInfoLineNumeric(rect, ref innerY, RowHeight, "  • PocketMapId", $"#{pocketMapId}");

            RocketDesignSpec? design = padComp?.Design;
            bool isSpacecraftCabin = design != null && design.CabinType == RocketCabinType.Spacecraft;
            if (!isSpacecraftCabin)
                DrawInfoLine(rect, ref innerY, RowHeight, "  • 运输仓类型", "普通运输仓（无舱内地图）");
            if (design != null)
                DrawInfoLine(rect, ref innerY, RowHeight, "  • 尺寸", $"{design.mapWidth}×{design.mapHeight}");
            else
                DrawInfoLine(rect, ref innerY, RowHeight, "  • 尺寸", "—");

            DrawInfoLine(rect, ref innerY, RowHeight, "  • 出口绑定", "(待后端定义)");
            y = cabinCardRect.yMax + SectionGap;

            // 舱内人员/物品概况
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "舱内人员/物品概况 (只读)", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + RowGap;

            if (hasMap && pocketMap != null)
            {
                int colonists = pocketMap.mapPawns?.FreeColonistsSpawnedCount ?? 0;
                int buildings = 0, items = 0;
                if (pocketMap.listerThings != null)
                {
                    foreach (Thing t in pocketMap.listerThings.AllThings)
                    {
                        if (t is Building) buildings++;
                        else if (t is not Pawn) items++;
                    }
                }
                DrawInfoLine(rect, ref y, RowHeight, "  • 自由殖民者", colonists.ToString());
                DrawInfoLine(rect, ref y, RowHeight, "  • 建筑",       buildings.ToString());
                DrawInfoLine(rect, ref y, RowHeight, "  • 物品",       items.ToString());
            }
            else
            {
                SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + 16f, y, rect.width, RowHeight),
                    "（无舱内地图）", SpacecraftUIPalette.ColTextMuted, GameFont.Small, TextAnchor.MiddleLeft);
                y += RowHeight;
            }

            y += SectionGap;
            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            // 进入舱内按钮（§8.4 冷青主操作；仅航天舱室可见/可点）
            bool canEnter = isSpacecraftCabin && padComp?.State == LaunchPadState.Completed;
            Rect btnEnter = new Rect(rect.x, y, 220f, ButtonHeight);
            if (SpacecraftUIDecor.DrawPrimaryButton(btnEnter, "进入火箭舱内",
                    SpacecraftUIPalette.SpecColdCyan, SpacecraftUIPalette.SpecColdCyanHover,
                    SpacecraftUIPalette.SpecWarmWhite, canEnter))
                OnEnterCabin();

        }

        // ── 4.5 发射页 ──
        private void DrawLaunchTab(Rect rect)
        {
            float y = rect.y + ContentTopPad;

            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "发射管理", SpacecraftUIPalette.SpecTextTitle, GameFont.Medium, TextAnchor.MiddleLeft);
            y += RowHeight + SectionGap;

            string rocketName = cachedContext?.Rocket?.def?.label
                ?? cachedContext?.Rocket?.def?.defName
                ?? "无关联火箭";
            DrawInfoLine(rect, ref y, RowHeight, "当前火箭", rocketName);
            LaunchPadState launchState = padComp?.State ?? LaunchPadState.Idle;
            DrawInfoLine(rect, ref y, RowHeight, "发射台状态",
                StateToSymbolText(launchState), StateColor(launchState));

            y += SectionGap;
            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            // 发射方式（§7.5 语义色单选按钮：选中 ● + 语义色，未选 ○ + 蓝灰）
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "发射方式", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + RowGap;

            // 原版方式（暖铜）
            bool isVanilla = selectedLaunchMode == RocketLaunchMode.VanillaTransportPod;
            string vanillaMarker = isVanilla ? "●" : "○";
            Color vanillaColor = isVanilla
                ? SpacecraftUIPalette.SpecWarmCopper
                : SpacecraftUIPalette.ColTextMuted;
            Rect vanillaRect = new Rect(rect.x + 16f, y, rect.width - 16f, 30f);
            SpacecraftUIDecor.DrawColoredLabel(vanillaRect,
                $"{vanillaMarker}  原版 TransportPod 发射（世界地格）",
                vanillaColor, GameFont.Small, TextAnchor.MiddleLeft);
            if (Widgets.ButtonInvisible(vanillaRect, true))
                selectedLaunchMode = RocketLaunchMode.VanillaTransportPod;
            y += 36f;

            // PFL 入轨（冷青）
            bool isPfl = selectedLaunchMode == RocketLaunchMode.PflOrbit;
            string pflMarker = isPfl ? "●" : "○";
            Color pflColor = isPfl
                ? SpacecraftUIPalette.SpecColdCyan
                : SpacecraftUIPalette.ColTextMuted;
            Rect pflRect = new Rect(rect.x + 16f, y, rect.width - 16f, 30f);
            SpacecraftUIDecor.DrawColoredLabel(pflRect,
                $"{pflMarker}  PFL 入轨发射（生成轨道实体）",
                pflColor, GameFont.Small, TextAnchor.MiddleLeft);
            if (Widgets.ButtonInvisible(pflRect, true))
                selectedLaunchMode = RocketLaunchMode.PflOrbit;
            y += 40f;

            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            // 发射条件校验
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "发射条件校验", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + RowGap;

            List<string> reasons;
            if (cachedContext != null)
            {
                reasons = RocketLaunchManager.GetLaunchBlockReasons(cachedContext, selectedLaunchMode);
            }
            else
            {
                reasons = new List<string> { "无法获取发射上下文（找不到关联火箭）。" };
            }

            if (reasons.Count == 0)
            {
                SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                    "✓ 当前条件满足，可以发射。",
                    SpacecraftUIPalette.SpecAccentMilitary, GameFont.Small, TextAnchor.MiddleLeft);
                y += RowHeight;
            }
            else
            {
                foreach (string reason in reasons)
                {
                    SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                        "✗ " + reason,
                        SpacecraftUIPalette.SpecDanger, GameFont.Small, TextAnchor.MiddleLeft);
                    y += RowHeight;
                }
            }

            y += SectionGap;
            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            // 发射按钮（§8.5 按当前方式选语义色）
            bool canLaunch = reasons.Count == 0;
            Rect btnLaunch = new Rect(rect.x, y, 160f, ButtonHeight);
            Color launchBg = isPfl
                ? SpacecraftUIPalette.SpecColdCyan
                : SpacecraftUIPalette.SpecWarmCopper;
            Color launchBgHover = isPfl
                ? SpacecraftUIPalette.SpecColdCyanHover
                : SpacecraftUIPalette.SpecWarmCopperHover;
            if (SpacecraftUIDecor.DrawPrimaryButton(btnLaunch, "发射",
                    launchBg, launchBgHover, SpacecraftUIPalette.SpecWarmWhite, canLaunch))
                OnLaunch();

            y += ButtonHeight + SectionGap;

            if (Prefs.DevMode)
            {
                SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                    "── 调试 ──", SpacecraftUIPalette.ColTextMuted, GameFont.Small, TextAnchor.MiddleLeft);
                y += RowHeight + RowGap;

                Rect btnForce = new Rect(rect.x + 16f, y, 220f, ButtonHeight);
                if (SpacecraftUIDecor.DrawOutlinedButton(btnForce, "强制发射 PFL (调试)",
                        SpacecraftUIPalette.SpecAmber, SpacecraftUIPalette.SpecAmber))
                    OnForceLaunchPflDebug();
                y += ButtonHeight + SectionGap;
            }

        }

        // ── 4.6 轨迹页 ──
        private void DrawTrajectoryTab(Rect rect)
        {
            // 背景网格线（磁带未来主义线框网格感）
            SpacecraftUIDecor.DrawLightGrid(rect, SpacecraftUIPalette.ColBorder, 40f);

            float y = rect.y + ContentTopPad;

            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "轨迹规划（首版）", SpacecraftUIPalette.SpecTextTitle, GameFont.Medium, TextAnchor.MiddleLeft);
            y += RowHeight + SectionGap;

            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "PFL 入轨参数", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + RowGap;

            // 目标轨道高度输入框
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, 220f, RowHeight),
                "目标轨道高度 (km):", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            Rect inputRect = new Rect(rect.x + 230f, y - 2f, 120f, 28f);
            string newBuffer = Widgets.TextField(inputRect, altitudeInputBuffer);
            if (newBuffer != altitudeInputBuffer)
            {
                altitudeInputBuffer = newBuffer;
                if (float.TryParse(altitudeInputBuffer, out float parsed))
                    targetOrbitAltitude = parsed;
            }
            y += RowHeight + RowGap * 2f;

            // 自动圆轨化复选框
            Rect checkboxRect = new Rect(rect.x, y, rect.width, RowHeight);
            Widgets.CheckboxLabeled(checkboxRect, "自动圆轨化", ref autoCircularize);
            y += RowHeight + SectionGap * 2f;

            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            // 说明
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, rect.width, RowHeight),
                "说明", SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + RowGap;

            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + 16f, y, rect.width - 16f, RowHeight),
                "• 首版只支持近圆轨道。",
                SpacecraftUIPalette.ColTextMuted, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight;
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + 16f, y, rect.width - 16f, RowHeight),
                "• 轨道面/倾角/远地点等参数后续版本接入。",
                SpacecraftUIPalette.ColTextMuted, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight;
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + 16f, y, rect.width - 16f, RowHeight),
                "• 原版 TransportPod 模式由原版世界目标选择器处理，此页参数不适用。",
                SpacecraftUIPalette.ColTextMuted, GameFont.Small, TextAnchor.MiddleLeft);
            y += RowHeight + SectionGap;

            SpacecraftUIDecor.DrawDivider(rect, y, rect.width);
            y += SectionGap + 4f;

            // 应用轨迹参数按钮（§8.6 冷青主操作）
            bool canApply = selectedLaunchMode == RocketLaunchMode.PflOrbit;
            Rect btnApply = new Rect(rect.x, y, 160f, ButtonHeight);
            if (SpacecraftUIDecor.DrawPrimaryButton(btnApply, "应用轨迹参数",
                    SpacecraftUIPalette.SpecColdCyan, SpacecraftUIPalette.SpecColdCyanHover,
                    SpacecraftUIPalette.SpecWarmWhite, canApply))
                OnApplyTrajectory();

            y += ButtonHeight + SectionGap;

        }

        // ── 工具方法 ──

        private static string StateToText(LaunchPadState state)
        {
            return state switch
            {
                LaunchPadState.Idle      => "空闲",
                LaunchPadState.Designing => "设计中",
                LaunchPadState.Building  => "建造中",
                LaunchPadState.Completed => "已完工",
                _ => "未知"
            };
        }

        private static string StateToSymbolText(LaunchPadState state)
        {
            return state switch
            {
                LaunchPadState.Idle      => "○ 空闲",
                LaunchPadState.Designing => "● 设计中",
                LaunchPadState.Building  => "● 建造中",
                LaunchPadState.Completed => "✓ 已完工",
                _ => "○ 未知"
            };
        }

        private static Color StateColor(LaunchPadState state)
        {
            return state switch
            {
                LaunchPadState.Idle      => SpacecraftUIPalette.ColTextMuted,
                LaunchPadState.Designing => SpacecraftUIPalette.SpecWarmCopper,
                LaunchPadState.Building  => SpacecraftUIPalette.SpecWarmCopper,
                LaunchPadState.Completed => SpacecraftUIPalette.SpecAccentMilitary,
                _ => SpacecraftUIPalette.ColTextMuted
            };
        }

        private static string DeriveCabinType(RocketDesignSpec? design)
        {
            if (design == null)
                return "—";
            return design.CabinType switch
            {
                RocketCabinType.Spacecraft => "航天舱室",
                RocketCabinType.None => "普通运输仓",
                _ => "未知"
            };
        }

        /// <summary>
        /// 绘制信息行：标签（次要文字色）+ 值（主文字色）。标签固定宽度，值占剩余宽度。
        /// 数字读数右对齐。
        /// </summary>
        private static void DrawInfoLine(Rect rect, ref float y, float lineHeight, string label, string value, Color? valueColor = null)
        {
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, LabelWidth, lineHeight),
                label, SpacecraftUIPalette.ColTextMuted, GameFont.Tiny, TextAnchor.MiddleLeft);
            Color color = valueColor ?? SpacecraftUIPalette.ColTextBody;
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + LabelWidth + 10f, y, rect.width - LabelWidth - 10f, lineHeight),
                value, color, GameFont.Small, TextAnchor.MiddleLeft);
            y += lineHeight;
        }

        /// <summary>
        /// 绘制带状态指示符的信息行（§7.4）：
        /// ok=true → "✓ 是"（军绿）；ok=false → "✗ 否"（锈红）。
        /// 用于施工进度等状态字段。
        /// </summary>
        private static void DrawStatusInfoLine(Rect rect, ref float y, float lineHeight, string label, bool ok)
        {
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, LabelWidth, lineHeight),
                label, SpacecraftUIPalette.ColTextMuted, GameFont.Tiny, TextAnchor.MiddleLeft);
            string symbol = ok ? "✓" : "✗";
            string text = ok ? "是" : "否";
            Color color = ok
                ? SpacecraftUIPalette.SpecAccentMilitary
                : SpacecraftUIPalette.SpecDanger;
            SpacecraftUIDecor.DrawColoredLabel(
                new Rect(rect.x + LabelWidth + 10f, y, rect.width - LabelWidth - 10f, lineHeight),
                $"{symbol} {text}", color, GameFont.Small, TextAnchor.MiddleLeft);
            y += lineHeight;
        }

        /// <summary>
        /// 绘制数字读数信息行（§4.2/§4.3）：标签左对齐 + 值右对齐等宽 GameFont.Tiny。
        /// 用于 PocketMapId、坐标、目标高度等数字字段。
        /// </summary>
        private static void DrawInfoLineNumeric(Rect rect, ref float y, float lineHeight, string label, string value)
        {
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, LabelWidth, lineHeight),
                label, SpacecraftUIPalette.ColTextMuted, GameFont.Tiny, TextAnchor.MiddleLeft);
            // 数字读数固定宽度 120px 右对齐（§4.3）
            const float numericWidth = 120f;
            SpacecraftUIDecor.DrawMonospaceNumber(
                new Rect(rect.x + rect.width - numericWidth, y, numericWidth, lineHeight),
                value, SpacecraftUIPalette.ColTextBody);
            y += lineHeight;
        }

        private static void DrawInfoLinePair(Rect rect, ref float y, float lineHeight,
            string label1, string value1, string label2, string value2)
        {
            float halfWidth = rect.width / 2f;
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x, y, 120f, lineHeight),
                label1, SpacecraftUIPalette.ColTextMuted, GameFont.Tiny, TextAnchor.MiddleLeft);
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + 130f, y, halfWidth - 130f, lineHeight),
                value1, SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + halfWidth, y, 120f, lineHeight),
                label2, SpacecraftUIPalette.ColTextMuted, GameFont.Tiny, TextAnchor.MiddleLeft);
            SpacecraftUIDecor.DrawColoredLabel(new Rect(rect.x + halfWidth + 130f, y, halfWidth - 130f, lineHeight),
                value2, SpacecraftUIPalette.ColTextBody, GameFont.Small, TextAnchor.MiddleLeft);
            y += lineHeight;
        }

        // ── 占位事件接口（后端 Agent override 或订阅） ──
        protected virtual void OnNewDesign(bool recoverable)
        {
            if (padComp == null)
            {
                Messages.Message("发射台组件缺失，无法创建设计。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            padComp.SetDesign(LaunchService.CreateDebugDesign(recoverable));
            Messages.Message(recoverable ? "已创建可回收火箭设计。" : "已创建不可回收火箭设计。",
                MessageTypeDefOf.SilentInput, false);
        }

        protected virtual void OnConfirmDesignAndBuild()
        {
            if (padComp == null)
            {
                Messages.Message("发射台组件缺失，无法生成框架。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            // TryStartBuild 内部会对设计校验失败给出具体 RejectInput 消息。
            if (padComp.TryStartBuild())
                Messages.Message("火箭建造蓝图已生成，等待搬运建材。", MessageTypeDefOf.TaskCompletion, false);
        }

        protected virtual void OnEnterCabin()
        {
            Building? tower = GetUmbilicalTower();
            if (tower == null)
            {
                Messages.Message("发射台没有脐带塔，无法登船。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (tower is MapPortal portal)
                Find.WindowStack.Add(new Dialog_EnterPortal(portal));
            else
                Messages.Message("脐带塔没有登船入口。", MessageTypeDefOf.RejectInput, false);
        }

        protected virtual void OnLaunch()
        {
            Thing? rocket = GetManagedRocket();
            if (rocket == null)
            {
                Messages.Message("找不到可发射的火箭。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (RocketLaunchManager.TryLaunch(rocket, selectedLaunchMode, out string error))
            {
                Messages.Message("发射已启动。", MessageTypeDefOf.TaskCompletion, false);
                Close();
            }
            else
            {
                Messages.Message(error, MessageTypeDefOf.RejectInput, false);
            }
        }

        protected virtual void OnForceLaunchPflDebug()
        {
            Thing? rocket = GetManagedRocket();
            if (rocket == null)
            {
                Messages.Message("找不到可强制发射的火箭。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (RocketLaunchManager.ForceLaunchPfl(rocket, out string error))
            {
                Messages.Message("强制 PFL 发射完成。", MessageTypeDefOf.TaskCompletion, false);
                Close();
            }
            else
            {
                Messages.Message(error, MessageTypeDefOf.RejectInput, false);
            }
        }

        protected virtual void OnCancelDesign()
        {
            if (padComp == null)
            {
                Messages.Message("发射台组件缺失。", MessageTypeDefOf.RejectInput, false);
                return;
            }

            padComp.CancelDesign();
            Messages.Message("设计已取消。", MessageTypeDefOf.SilentInput, false);
        }

        protected virtual void OnSetCabinType(RocketCabinType type)
        {
            if (padComp?.Design == null)
                return;

            padComp.Design.CabinType = type;
        }

        protected virtual void OnAddPart(DesignPartSpec part)
        {
            if (padComp?.Design == null || part == null)
                return;

            padComp.Design.parts ??= new System.Collections.Generic.List<DesignPartSpec>();
            padComp.Design.parts.Add(part);
        }

        protected virtual void OnRemovePartLast()
        {
            if (padComp?.Design?.parts == null || padComp.Design.parts.Count == 0)
                return;

            int last = padComp.Design.parts.Count - 1;
            padComp.Design.parts.RemoveAt(last);
        }

        protected virtual void OnAddPayload(RocketPayloadSpec payload)
        {
            if (padComp?.Design == null || payload == null)
                return;

            padComp.Design.payloads ??= new System.Collections.Generic.List<RocketPayloadSpec>();
            padComp.Design.payloads.Add(payload);
        }

        protected virtual void OnRemovePayloadLast()
        {
            if (padComp?.Design?.payloads == null || padComp.Design.payloads.Count == 0)
                return;

            int last = padComp.Design.payloads.Count - 1;
            padComp.Design.payloads.RemoveAt(last);
        }

        protected virtual void OnApplyTrajectory()
        {
            Log.Message($"[SpacecraftUI] OnApplyTrajectory - 当前仅记录本地 UI 状态 (高度={targetOrbitAltitude}, 自动圆轨化={autoCircularize})");
        }
    }
}
