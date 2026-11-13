using System;
using System.Collections.Generic;
using System.Globalization;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 天体信息面板（只读）。
    ///
    /// 数据来源全部是既有只读接口，不新增存档字段、不写任何模拟状态：
    /// · 天体静态事实 = <see cref="CelestialBodyInfo"/> + <see cref="CelestialBodyManager"/> 查询；
    /// · 天体 → 地表层 = <see cref="GetCelestialRenderLayer"/>（与 3D 渲染同源），
    ///   取不到再退回 <c>Launch.GetSurfaceState.GetLayerForBody</c>（宿主唯一映射口）；
    /// · 生态页签 = <see cref="PlanetLayer"/> 的 <see cref="SurfaceTile"/> 统计。
    ///
    /// 三条硬纪律：
    /// ① 面板矩形必须登记进 <c>uiBlockRects</c>（partial 可访问的私有字段）——
    ///   否则点页签/关闭会穿到 3D 视口，同时选中页签底下那艘船并拽走相机；
    /// ② 生态统计要遍历整层 tile（10 万级）⇒ **每天体每 2 秒最多一次**，结果缓存在字段里，
    ///   绝不在每帧绘制里遍历（见 <see cref="RefreshPlanetInfoEcology"/>）；
    /// ③ 所有玩家可见文字走 <c>.Translate()</c>，key 在
    ///   <c>1.7/Languages/*/Keyed/PFViewPlanetInfo.xml</c>（zh/en 一一对应）。
    ///
    /// 接线（Lead 负责）：在 <c>DrawOverlay</c> 里调 <see cref="DrawPlanetInfoPanel"/>，
    /// 在工具条/快捷键/点击处理里调 <see cref="OpenPlanetInfo"/>、<see cref="ClosePlanetInfo"/>、
    /// <see cref="TogglePlanetInfoForFocusedBody"/>。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        // ── 面板几何（视口右侧：宽 340，上 84 / 下 24 余量；视口 < 700 整体隐藏） ──
        private const float PlanetInfoPanelWidth = 340f;
        private const float PlanetInfoTopMargin = 84f;
        private const float PlanetInfoBottomMargin = 24f;
        private const float PlanetInfoMinViewportWidth = 700f;
        private const float PlanetInfoRowHeight = 20f;
        private const float PlanetInfoStatsInterval = 2f;

        private static readonly string[] PlanetInfoTabKeys =
        {
            "PFView_PlanetInfo_Tab_Summary",
            "PFView_PlanetInfo_Tab_Ecology",
            "PFView_PlanetInfo_Tab_Orbit",
        };

        // 5 档 hilliness 直方图的档位（Undefined 并入 Flat：参数化生成不写 Undefined，
        // 原生河流/湖泊/地标步骤也不改 hilliness）。
        private static readonly Hilliness[] PlanetInfoHillinessBuckets =
        {
            Hilliness.Flat, Hilliness.SmallHills, Hilliness.LargeHills, Hilliness.Mountainous, Hilliness.Impassable,
        };

        // ── 面板状态 ──
        private bool planetInfoOpen;
        private int planetInfoBodyId = -1;
        private int planetInfoTab;

        // ── 生态统计缓存（限频：每天体每 2 秒最多一次） ──
        private readonly Dictionary<BiomeDef, int> planetInfoBiomeCounts = new();
        private readonly PlanetInfoEcologySummary planetInfoEcology = new();
        private int planetInfoEcologyBodyId = -1;
        private int planetInfoEcologyLayerId = -1;
        private float planetInfoEcologyAt = -9999f;
        private bool planetInfoEcologyValid;

        /// <summary>一次生态统计的结果（刷新时整体重写；绘制只读）。字段预分配，刷新时只清值不新建。</summary>
        private sealed class PlanetInfoEcologySummary
        {
            public const int TopBiomes = 5;

            public readonly string?[] BiomeLabels = new string?[TopBiomes];
            public readonly string?[] BiomeDefNames = new string?[TopBiomes];
            public readonly int[] BiomeCounts = new int[TopBiomes];
            public readonly int[] HillinessCounts = new int[5];

            public int BiomeRows;
            public int SurfaceTiles;
            public int SampleTiles;
            public double ElevationSum;
            public double TemperatureSum;
            public double RainfallSum;
            public double AverageElevationMeters;
            public double AverageTemperatureCelsius;
            public double AverageRainfallMillimeters;

            public void Reset()
            {
                Array.Clear(BiomeLabels, 0, TopBiomes);
                Array.Clear(BiomeDefNames, 0, TopBiomes);
                Array.Clear(BiomeCounts, 0, TopBiomes);
                Array.Clear(HillinessCounts, 0, HillinessCounts.Length);
                BiomeRows = 0;
                SurfaceTiles = 0;
                SampleTiles = 0;
                ElevationSum = 0;
                TemperatureSum = 0;
                RainfallSum = 0;
                AverageElevationMeters = 0;
                AverageTemperatureCelsius = 0;
                AverageRainfallMillimeters = 0;
            }
        }

        // ════════════════════════════════════════════════════
        //  公开入口（Lead 接线用）
        // ════════════════════════════════════════════════════

        internal bool PlanetInfoOpen => planetInfoOpen;

        /// <summary>打开面板并显示指定天体。bodyId &lt; 0 时保留上一次的天体（由绘制端回退到焦点天体）。</summary>
        internal void OpenPlanetInfo(int bodyId)
        {
            planetInfoOpen = true;
            if (bodyId >= 0) planetInfoBodyId = bodyId;
            planetInfoEcologyValid = false;   // 换天体后第一次绘制立即统计
        }

        internal void ClosePlanetInfo()
        {
            planetInfoOpen = false;
        }

        /// <summary>
        /// 工具条/快捷键入口：当前焦点天体已在面板中 ⇒ 关闭，否则切到它。
        /// 用 <see cref="ResolveFocusBodyId"/>（编队 → 所在天体、Frozen 保留最后天体、失效回退当前系统天体）。
        /// </summary>
        internal void TogglePlanetInfoForFocusedBody()
        {
            int bodyId = ResolveFocusBodyId();
            if (planetInfoOpen && planetInfoBodyId == bodyId)
            {
                ClosePlanetInfo();
                return;
            }
            OpenPlanetInfo(bodyId);
        }

        /// <summary>面板绘制（Lead 在 DrawOverlay 里调用；必须在 ComputeLayout 之后、HandleGUIEvents 之前）。</summary>
        internal void DrawPlanetInfoPanel(Rect inRect)
        {
            if (!planetInfoOpen) return;
            // 视口太窄 ⇒ 整体隐藏（连 uiBlockRects 都不登记：宁可点到 3D，也不留看不见的死区）。
            if (inRect.width < PlanetInfoMinViewportWidth) return;
            float height = inRect.height - PlanetInfoTopMargin - PlanetInfoBottomMargin;
            if (height < 160f) return;

            var panel = new Rect(inRect.xMax - PlanetInfoPanelWidth - 12f, inRect.y + PlanetInfoTopMargin,
                PlanetInfoPanelWidth, height);
            // 私有字段（partial 可访问）：登记后 MouseInViewport 会把面板上的鼠标事件全部挡掉。
            uiBlockRects.Add(panel);

            Widgets.DrawBoxSolid(panel, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(panel, PFViewPalette.Border);
            UiKit.SpacecraftUIDecor.DrawDivider(panel, panel.y + 34f, panel.width, PFViewPalette.Divider);

            MiniLabel(new Rect(panel.x + 10f, panel.y + 5f, panel.width - 48f, 26f),
                "PFView_PlanetInfo_Title".Translate(), PFViewPalette.TextPrimary);

            var closeRect = new Rect(panel.xMax - 32f, panel.y + 6f, 22f, 22f);
            TooltipHandler.TipRegion(closeRect, "PFView_PlanetInfo_Close".Translate());
            if (PlanetInfoSelfButton(closeRect, "\u2715", false))
            {
                ClosePlanetInfo();
                return;
            }

            float tabY = panel.y + 40f;
            float tabWidth = (panel.width - 20f) / PlanetInfoTabKeys.Length;
            for (int index = 0; index < PlanetInfoTabKeys.Length; index++)
            {
                var tabRect = new Rect(panel.x + 10f + index * tabWidth, tabY, tabWidth - 2f, 24f);
                if (PlanetInfoSelfButton(tabRect, PlanetInfoTabKeys[index].Translate(), planetInfoTab == index))
                    planetInfoTab = index;
            }

            var content = new Rect(panel.x + 10f, tabY + 32f, panel.width - 20f,
                Mathf.Max(1f, panel.yMax - (tabY + 32f) - 8f));
            var manager = Current.Game?.GetComponent<CelestialBodyManager>();
            var body = ResolvePlanetInfoBody(manager);
            if (manager == null || body == null)
            {
                float noBodyY = content.y;
                PlanetInfoNote(content, ref noBodyY, "PFView_PlanetInfo_NoBody".Translate());
                return;
            }

            switch (planetInfoTab)
            {
                case 1: DrawPlanetInfoEcology(content, body, manager); break;
                case 2: DrawPlanetInfoOrbit(content, body); break;
                default: DrawPlanetInfoSummary(content, body, manager); break;
            }
        }

        // ════════════════════════════════════════════════════
        //  状态解析
        // ════════════════════════════════════════════════════

        private CelestialBodyInfo? ResolvePlanetInfoBody(CelestialBodyManager? manager)
        {
            if (manager == null) return null;
            if (planetInfoBodyId >= 0 && manager.GetBodyById(planetInfoBodyId) is { } cached) return cached;
            int focusBody = ResolveFocusBodyId();
            return focusBody >= 0 ? manager.GetBodyById(focusBody) : null;
        }

        /// <summary>
        /// 天体 → PlanetLayer：优先渲染快照缓存（与 3D 视图同源），取不到再退回宿主唯一映射口。
        /// 逐格检视面板（<c>MainTabWindow_PFView.SurfaceInspect.cs</c>）复用这一份解析。
        /// </summary>
        private PlanetLayer? ResolvePlanetSurfaceLayer(CelestialBodyManager manager, int bodyId)
        {
            var layer = GetCelestialRenderLayer(manager, bodyId);
            return layer ?? PioneerFramework.Launch.GetSurfaceState.GetLayerForBody(manager, bodyId);
        }

        // ════════════════════════════════════════════════════
        //  页签 1：简介
        // ════════════════════════════════════════════════════

        private void DrawPlanetInfoSummary(Rect content, CelestialBodyInfo body, CelestialBodyManager manager)
        {
            float y = content.y;
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Field_Name".Translate(), manager.GetBodyName(body.bodyId));
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Field_DefName".Translate(), body.defName);
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Field_Type".Translate(), PlanetInfoBodyTypeLabel(body.bodyType));
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Field_Radius".Translate(),
                "PFView_PlanetInfo_Value_Kilometers".Translate(FormatNumber(body.radius / 1000.0, "N0")));

            // 任务口径：质量 = μ / G，G 必须用 SpaceConstants 的既有常量。SpaceConstants 里没有万有引力常数
            // （6.67430e-11 只内联在 Celestial/Definitions 的推导里）⇒ 按「没有 G 就只显示 μ」只给引力参数。
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Field_Gravity".Translate(),
                body.gravitationalParameter > 0
                    ? "PFView_PlanetInfo_Value_GravitationalParameter".Translate(FormatNumber(body.gravitationalParameter, "N0"))
                    : "PFView_PlanetInfo_Value_None".Translate());

            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Field_Surface".Translate(),
                (body.hasSurface ? "PFView_PlanetInfo_Value_Yes" : "PFView_PlanetInfo_Value_No").Translate());

            var layer = ResolvePlanetSurfaceLayer(manager, body.bodyId);
            string layerValue = layer == null
                ? "PFView_PlanetInfo_Value_None".Translate()
                : "PFView_PlanetInfo_Value_LayerTiles".Translate(
                    layer.Def?.defName ?? layer.LayerID.ToString(CultureInfo.InvariantCulture),
                    FormatNumber(layer.TilesCount, "N0"));
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Field_Layer".Translate(), layerValue);

            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Field_Moons".Translate(),
                "PFView_PlanetInfo_Value_Count".Translate(FormatNumber(body.moonBodies?.Count ?? 0, "N0")));
        }

        // ════════════════════════════════════════════════════
        //  页签 2：生态（限频统计）
        // ════════════════════════════════════════════════════

        private void DrawPlanetInfoEcology(Rect content, CelestialBodyInfo body, CelestialBodyManager manager)
        {
            var layer = ResolvePlanetSurfaceLayer(manager, body.bodyId);
            if (layer == null)
            {
                float noLayerY = content.y;
                PlanetInfoNote(content, ref noLayerY, "PFView_PlanetInfo_Ecology_NoLayer".Translate());
                return;
            }

            RefreshPlanetInfoEcology(body, layer);
            if (!planetInfoEcologyValid)
            {
                float countingY = content.y;
                PlanetInfoNote(content, ref countingY, "PFView_PlanetInfo_Ecology_Counting".Translate());
                return;
            }

            var stats = planetInfoEcology;
            float y = content.y;
            PlanetInfoHeader(content, ref y, "PFView_PlanetInfo_Ecology_TopBiomes".Translate());
            if (stats.BiomeRows == 0)
                PlanetInfoNote(content, ref y, "PFView_PlanetInfo_Ecology_NoData".Translate());

            for (int index = 0; index < stats.BiomeRows; index++)
            {
                if (y + PlanetInfoRowHeight > content.yMax) break;
                string label = stats.BiomeLabels[index] ?? string.Empty;
                string defName = stats.BiomeDefNames[index] ?? string.Empty;
                MiniLabel(new Rect(content.x, y, content.width - 58f, PlanetInfoRowHeight),
                    "PFView_PlanetInfo_Ecology_Biome".Translate(label, defName), PFViewPalette.TextSecondary);
                double percent = stats.SurfaceTiles > 0 ? stats.BiomeCounts[index] * 100.0 / stats.SurfaceTiles : 0.0;
                MiniLabel(new Rect(content.xMax - 56f, y, 56f, PlanetInfoRowHeight),
                    "PFView_PlanetInfo_Value_Percent".Translate(FormatNumber(percent, "F1")),
                    PFViewPalette.TextMuted, TextAnchor.MiddleRight);
                y += PlanetInfoRowHeight;
            }

            y += 4f;
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Ecology_AvgElevation".Translate(),
                "PFView_PlanetInfo_Value_Meters".Translate(FormatNumber(stats.AverageElevationMeters, "N0")));
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Ecology_AvgTemperature".Translate(),
                "PFView_PlanetInfo_Value_Celsius".Translate(FormatNumber(stats.AverageTemperatureCelsius, "F2")));
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Ecology_AvgRainfall".Translate(),
                "PFView_PlanetInfo_Value_Millimeters".Translate(FormatNumber(stats.AverageRainfallMillimeters, "F1")));

            y += 4f;
            PlanetInfoHeader(content, ref y, "PFView_PlanetInfo_Ecology_Hilliness".Translate());
            int peak = 1;
            for (int index = 0; index < stats.HillinessCounts.Length; index++)
                peak = Math.Max(peak, stats.HillinessCounts[index]);

            for (int index = 0; index < PlanetInfoHillinessBuckets.Length && index < stats.HillinessCounts.Length; index++)
            {
                if (y + PlanetInfoRowHeight > content.yMax) break;
                MiniLabel(new Rect(content.x, y, 96f, PlanetInfoRowHeight),
                    PlanetInfoHillinessLabel(PlanetInfoHillinessBuckets[index]), PFViewPalette.TextMuted);

                var track = new Rect(content.x + 100f, y + 7f, Mathf.Max(8f, content.width - 156f), 6f);
                Widgets.DrawBoxSolid(track, PFViewPalette.PanelBgRaised);
                float fill = track.width * stats.HillinessCounts[index] / peak;
                if (fill > 0f)
                    Widgets.DrawBoxSolid(new Rect(track.x, track.y, fill, track.height), PFViewPalette.AccentDim);

                MiniLabel(new Rect(content.xMax - 52f, y, 52f, PlanetInfoRowHeight),
                    "PFView_PlanetInfo_Value_Count".Translate(FormatNumber(stats.HillinessCounts[index], "N0")),
                    PFViewPalette.TextSecondary, TextAnchor.MiddleRight);
                y += PlanetInfoRowHeight;
            }
        }

        /// <summary>
        /// 整层 tile 的生态统计。**限频：同一 (天体, layer) 每 2 秒最多一次**；缓存命中直接返回。
        /// 10 万格级别的遍历绝不能进每帧路径（这里只在生态页签可见时被调，且被时间门挡住）。
        /// 温度口径 = <c>CelestialClimateModel.SeasonalTemperature</c> 写进 <c>SurfaceTile.temperature</c> 的**摄氏度**；
        /// 降水 = 同一写入口的毫米值（<c>CelestialPlanetTerrain.SurfaceMoisture</c>）。
        /// </summary>
        private void RefreshPlanetInfoEcology(CelestialBodyInfo body, PlanetLayer layer)
        {
            float now = Time.realtimeSinceStartup;
            int layerId = layer.LayerID;
            if (planetInfoEcologyValid && planetInfoEcologyBodyId == body.bodyId &&
                planetInfoEcologyLayerId == layerId && now - planetInfoEcologyAt < PlanetInfoStatsInterval)
                return;

            planetInfoEcologyAt = now;
            planetInfoEcologyBodyId = body.bodyId;
            planetInfoEcologyLayerId = layerId;
            planetInfoEcologyValid = false;
            planetInfoBiomeCounts.Clear();

            int tiles = layer.TilesCount;
            if (tiles <= 0) return;

            var summary = planetInfoEcology;
            summary.Reset();
            for (int index = 0; index < tiles; index++)
            {
                if (layer[index] is not SurfaceTile tile) continue;
                summary.SurfaceTiles++;

                var biome = tile.PrimaryBiome;
                if (biome != null)
                {
                    planetInfoBiomeCounts.TryGetValue(biome, out int count);
                    planetInfoBiomeCounts[biome] = count + 1;
                }

                float elevation = tile.elevation;
                if (float.IsFinite(elevation)) summary.ElevationSum += elevation;
                float temperature = tile.temperature;
                if (float.IsFinite(temperature)) summary.TemperatureSum += temperature;
                float rainfall = tile.rainfall;
                if (float.IsFinite(rainfall)) summary.RainfallSum += rainfall;
                summary.SampleTiles++;

                summary.HillinessCounts[PlanetInfoHillinessBucket(tile.hilliness)]++;
            }

            if (summary.SampleTiles > 0)
            {
                summary.AverageElevationMeters = summary.ElevationSum / summary.SampleTiles;
                summary.AverageTemperatureCelsius = summary.TemperatureSum / summary.SampleTiles;
                summary.AverageRainfallMillimeters = summary.RainfallSum / summary.SampleTiles;

                // Top 5：同票按 defName 序数比较兜底（Dictionary 迭代顺序不保证，显示不能每 2 秒抖）。
                for (int slot = 0; slot < PlanetInfoEcologySummary.TopBiomes; slot++)
                {
                    BiomeDef? best = null;
                    int bestCount = 0;
                    foreach (var pair in planetInfoBiomeCounts)
                    {
                        int count = pair.Value;
                        if (count <= 0) continue;
                        if (best == null || count > bestCount ||
                            (count == bestCount && string.CompareOrdinal(pair.Key.defName, best.defName) < 0))
                        {
                            best = pair.Key;
                            bestCount = count;
                        }
                    }
                    if (best == null) break;

                    string label = best.label;
                    summary.BiomeLabels[slot] = string.IsNullOrEmpty(label) ? best.defName : label;
                    summary.BiomeDefNames[slot] = best.defName;
                    summary.BiomeCounts[slot] = bestCount;
                    summary.BiomeRows++;
                    planetInfoBiomeCounts.Remove(best);
                }
            }

            planetInfoEcologyValid = true;
        }

        // ════════════════════════════════════════════════════
        //  页签 3：运行
        // ════════════════════════════════════════════════════

        private void DrawPlanetInfoOrbit(Rect content, CelestialBodyInfo body)
        {
            float y = content.y;
            if (body.HasOrbit)
            {
                PlanetInfoField(content, ref y, "PFView_PlanetInfo_Orbit_Radius".Translate(),
                    "PFView_PlanetInfo_Value_Kilometers".Translate(FormatNumber(body.orbitRadius / 1000.0, "N0")));
                PlanetInfoField(content, ref y, "PFView_PlanetInfo_Orbit_Period".Translate(),
                    "PFView_PlanetInfo_Value_Seconds".Translate(FormatNumber(body.orbitPeriod, "N0")));

                var normal = body.orbitNormal;
                PlanetInfoField(content, ref y, "PFView_PlanetInfo_Orbit_Normal".Translate(),
                    "PFView_PlanetInfo_Value_Vector".Translate(
                        FormatNumber(normal.x, "F3"), FormatNumber(normal.y, "F3"), FormatNumber(normal.z, "F3")));
            }
            else
            {
                PlanetInfoNote(content, ref y, "PFView_PlanetInfo_Orbit_NoOrbit".Translate());
            }

            // 日长：dayLengthTicks 是游戏 tick（60000 tick = 1 游戏日 = 24 游戏小时）⇒ / 2500 = 小时。
            double dayHours = body.dayLengthTicks / 2500.0;
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Orbit_DayLength".Translate(),
                body.dayLengthTicks > 0
                    ? "PFView_PlanetInfo_Value_Hours".Translate(FormatNumber(dayHours, "F2"))
                    : "PFView_PlanetInfo_Value_None".Translate());

            y += 4f;
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Orbit_AtmoPhysical".Translate(),
                body.atmosphereThickness > 0
                    ? "PFView_PlanetInfo_Value_Meters".Translate(FormatNumber(body.atmosphereThickness, "N0"))
                    : "PFView_PlanetInfo_Value_None".Translate());
            PlanetInfoField(content, ref y, "PFView_PlanetInfo_Orbit_AtmoVisual".Translate(),
                body.atmosphereVisualThickness > 0
                    ? "PFView_PlanetInfo_Value_Meters".Translate(FormatNumber(body.atmosphereVisualThickness, "N0"))
                    : "PFView_PlanetInfo_Value_None".Translate());

            if (y + PlanetInfoRowHeight > content.yMax) return;
            float labelWidth = content.width * 0.42f;
            MiniLabel(new Rect(content.x, y, labelWidth, PlanetInfoRowHeight),
                "PFView_PlanetInfo_Orbit_AtmoColor".Translate(), PFViewPalette.TextMuted);
            var swatch = new Rect(content.x + labelWidth, y + 5f, 28f, 10f);
            Widgets.DrawBoxSolid(swatch, body.atmosphereColor);
            MiniLabel(new Rect(swatch.xMax + 6f, y, content.width - labelWidth - 40f, PlanetInfoRowHeight),
                "#" + ColorUtility.ToHtmlStringRGB(body.atmosphereColor), PFViewPalette.TextSecondary);
        }

        // ════════════════════════════════════════════════════
        //  绘制 / 格式化小工具
        // ════════════════════════════════════════════════════

        /// <summary>自绘页签/关闭按钮（不做 ButtonText 的默认皮肤：PFView 全灰阶自绘）。</summary>
        private static bool PlanetInfoSelfButton(Rect rect, string label, bool active)
        {
            bool hover = Mouse.IsOver(rect);
            Widgets.DrawBoxSolid(rect,
                active ? PFViewPalette.PanelBgRaised : hover ? PFViewPalette.PanelBgHover : PFViewPalette.BarBg);
            UiKit.SpacecraftUIDecor.DrawColoredLabel(rect, label,
                active ? PFViewPalette.TextPrimary : PFViewPalette.TextMuted, GameFont.Tiny, TextAnchor.MiddleCenter);

            if (!hover) return false;
            var current = Event.current;
            if (current.type != EventType.MouseDown || current.button != 0) return false;
            current.Use();
            return true;
        }

        private static void PlanetInfoField(Rect content, ref float y, string label, string value)
        {
            if (y + PlanetInfoRowHeight > content.yMax) return;
            float labelWidth = content.width * 0.42f;
            MiniLabel(new Rect(content.x, y, labelWidth, PlanetInfoRowHeight), label, PFViewPalette.TextMuted);
            MiniLabel(new Rect(content.x + labelWidth, y, content.width - labelWidth, PlanetInfoRowHeight),
                value, PFViewPalette.TextSecondary);
            y += PlanetInfoRowHeight;
        }

        private static void PlanetInfoHeader(Rect content, ref float y, string text)
        {
            if (y + PlanetInfoRowHeight > content.yMax) return;
            MiniLabel(new Rect(content.x, y, content.width, PlanetInfoRowHeight), text, PFViewPalette.TextPrimary);
            y += PlanetInfoRowHeight;
        }

        private static void PlanetInfoNote(Rect content, ref float y, string text)
        {
            if (y + PlanetInfoRowHeight > content.yMax) return;
            MiniLabel(new Rect(content.x, y, content.width, PlanetInfoRowHeight), text, PFViewPalette.TextMuted);
            y += PlanetInfoRowHeight;
        }

        private static string PlanetInfoBodyTypeLabel(CelestialBodyType bodyType) => bodyType switch
        {
            CelestialBodyType.Star => "PFView_PlanetInfo_BodyType_Star".Translate(),
            CelestialBodyType.Planet => "PFView_PlanetInfo_BodyType_Planet".Translate(),
            CelestialBodyType.Moon => "PFView_PlanetInfo_BodyType_Moon".Translate(),
            _ => "PFView_PlanetInfo_Value_None".Translate(),
        };

        /// <summary>Hilliness 档位显示名。逐格检视面板复用同一套文案（同一地形词汇，不重复维护）。</summary>
        private static string PlanetInfoHillinessLabel(Hilliness hilliness) => hilliness switch
        {
            Hilliness.Flat => "PFView_PlanetInfo_Hilliness_Flat".Translate(),
            Hilliness.SmallHills => "PFView_PlanetInfo_Hilliness_SmallHills".Translate(),
            Hilliness.LargeHills => "PFView_PlanetInfo_Hilliness_LargeHills".Translate(),
            Hilliness.Mountainous => "PFView_PlanetInfo_Hilliness_Mountainous".Translate(),
            Hilliness.Impassable => "PFView_PlanetInfo_Hilliness_Impassable".Translate(),
            _ => "PFView_PlanetInfo_Hilliness_Undefined".Translate(),
        };

        private static int PlanetInfoHillinessBucket(Hilliness hilliness) => hilliness switch
        {
            Hilliness.SmallHills => 1,
            Hilliness.LargeHills => 2,
            Hilliness.Mountainous => 3,
            Hilliness.Impassable => 4,
            _ => 0,   // Undefined / Flat
        };

        private static string FormatNumber(double value, string format)
            => value.ToString(format, CultureInfo.InvariantCulture);
    }
}
