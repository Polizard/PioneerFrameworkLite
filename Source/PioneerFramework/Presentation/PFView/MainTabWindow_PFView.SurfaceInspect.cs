using System.Globalization;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// PFView 逐格地表检视面板（只读）。
    ///
    /// 显示被点选的那一格（PlanetLayer 的 <see cref="SurfaceTile"/>）：biome / 海拔 / 温度 / 降水 /
    /// hilliness / 水体 / 河流与潜在河流道路。数据全部来自既有只读接口，不新增存档字段。
    ///
    /// 纪律：
    /// · 面板矩形必须登记进 <c>uiBlockRects</c>（partial 可访问的私有字段），否则点击会穿到 3D 视口；
    /// · 放在视口左下（避开左栏：x = 左栏右缘 + 12），空间不足就不画；
    /// · **全部空值安全**：天体失效 / layer 消失 / layer 格数缩小 / 该格不是 SurfaceTile ⇒
    ///   自动清空状态（<see cref="ClearInspectedSurfaceTile"/>），下一次绘制不再显示；
    /// · 本文件不写交互：Ctrl+左键 的提示与拾取触发由 Lead 接线（Lead 只需在拾取成功后调
    ///   <see cref="SetInspectedSurfaceTile"/>）。
    ///
    /// 复用同一 partial 类的 <c>MainTabWindow_PFView.PlanetInfo.cs</c> 里的
    /// <c>ResolvePlanetSurfaceLayer</c> / <c>PlanetInfoHillinessLabel</c> / <c>FormatNumber</c>。
    /// </summary>
    public partial class MainTabWindow_PFView
    {
        private const float SurfaceInspectPanelWidth = 300f;
        private const float SurfaceInspectBottomMargin = 24f;
        private const float SurfaceInspectRowHeight = 19f;

        // 标题行 36 + 11 行字段 + 底部留白 8。
        private const float SurfaceInspectPanelHeight = 36f + 11f * SurfaceInspectRowHeight + 8f;

        private int inspectedSurfaceBodyId = -1;
        private int inspectedSurfaceTileId = -1;

        internal bool HasInspectedSurfaceTile => inspectedSurfaceBodyId >= 0 && inspectedSurfaceTileId >= 0;
        internal int InspectedSurfaceBodyId => inspectedSurfaceBodyId;
        internal int InspectedSurfaceTileId => inspectedSurfaceTileId;

        /// <summary>设定被检视格。tile &lt; 0 或 bodyId &lt; 0 = 清空。</summary>
        internal void SetInspectedSurfaceTile(int bodyId, int tile)
        {
            if (bodyId < 0 || tile < 0)
            {
                ClearInspectedSurfaceTile();
                return;
            }
            inspectedSurfaceBodyId = bodyId;
            inspectedSurfaceTileId = tile;
        }

        internal void ClearInspectedSurfaceTile()
        {
            inspectedSurfaceBodyId = -1;
            inspectedSurfaceTileId = -1;
        }

        /// <summary>面板绘制（Lead 在 DrawOverlay 里调用，需在 ComputeLayout 之后，才能拿到左栏几何）。</summary>
        internal void DrawSurfaceInspectPanel(Rect inRect)
        {
            if (!HasInspectedSurfaceTile) return;

            var manager = Current.Game?.GetComponent<CelestialBodyManager>();
            if (manager == null)
            {
                ClearInspectedSurfaceTile();
                return;
            }

            var body = manager.GetBodyById(inspectedSurfaceBodyId);
            if (body == null)
            {
                ClearInspectedSurfaceTile();
                return;
            }

            var layer = ResolvePlanetSurfaceLayer(manager, body.bodyId);
            if (layer == null || inspectedSurfaceTileId >= layer.TilesCount ||
                layer[inspectedSurfaceTileId] is not SurfaceTile tile)
            {
                // layer 卸载 / 层被重建后格数变小 / 该格不再是地表格 ⇒ 状态自动清空。
                ClearInspectedSurfaceTile();
                return;
            }

            // 左下角，避开左栏；不知道左栏几何（宽度 0）或横向放不下 ⇒ 本帧不画。
            float x = rLeftCol.xMax + 12f;
            if (rLeftCol.width <= 0f || x + SurfaceInspectPanelWidth > inRect.xMax - 12f) return;
            float y = inRect.yMax - SurfaceInspectBottomMargin - SurfaceInspectPanelHeight;
            if (y < inRect.y + 84f) return;   // 垂直空间不足（与天体信息面板同一上边距口径）

            var panel = new Rect(x, y, SurfaceInspectPanelWidth, SurfaceInspectPanelHeight);
            uiBlockRects.Add(panel);

            Widgets.DrawBoxSolid(panel, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(panel, PFViewPalette.Border);
            UiKit.SpacecraftUIDecor.DrawDivider(panel, panel.y + 31f, panel.width, PFViewPalette.Divider);
            MiniLabel(new Rect(panel.x + 10f, panel.y + 4f, panel.width - 20f, 24f),
                "PFView_TileInspect_Title".Translate(), PFViewPalette.TextPrimary);

            var content = new Rect(panel.x + 10f, panel.y + 36f, panel.width - 20f, panel.height - 42f);
            float rowY = content.y;
            var culture = CultureInfo.InvariantCulture;

            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_Body".Translate(), manager.GetBodyName(body.bodyId));
            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_Tile".Translate(),
                "PFView_TileInspect_TileFormat".Translate(inspectedSurfaceTileId.ToString(culture)));

            string biomeLabel = tile.PrimaryBiome?.label ?? string.Empty;
            string biomeDefName = tile.PrimaryBiome?.defName ?? string.Empty;
            string biomeValue = string.IsNullOrEmpty(biomeDefName)
                ? "PFView_TileInspect_Unknown".Translate()
                : "PFView_TileInspect_BiomeFormat".Translate(
                    string.IsNullOrEmpty(biomeLabel) ? biomeDefName : biomeLabel, biomeDefName);
            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_Biome".Translate(), biomeValue);

            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_Elevation".Translate(),
                "PFView_TileInspect_Value_Meters".Translate(FormatNumber(tile.elevation, "F2")));
            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_Temperature".Translate(),
                "PFView_TileInspect_Value_Celsius".Translate(FormatNumber(tile.temperature, "F2")));
            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_Rainfall".Translate(),
                "PFView_TileInspect_Value_Millimeters".Translate(FormatNumber(tile.rainfall, "F1")));
            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_Hilliness".Translate(),
                PlanetInfoHillinessLabel(tile.hilliness));
            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_Water".Translate(), SurfaceInspectBool(tile.WaterCovered));
            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_Rivers".Translate(),
                "PFView_TileInspect_Value_Count".Translate((tile.Rivers?.Count ?? 0).ToString(culture)));
            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_PotentialRivers".Translate(),
                SurfaceInspectBool(tile.potentialRivers?.Count > 0));
            SurfaceInspectField(content, ref rowY, "PFView_TileInspect_PotentialRoads".Translate(),
                SurfaceInspectBool(tile.potentialRoads?.Count > 0));
        }

        private static void SurfaceInspectField(Rect content, ref float y, string label, string value)
        {
            if (y + SurfaceInspectRowHeight > content.yMax) return;
            float labelWidth = content.width * 0.38f;
            MiniLabel(new Rect(content.x, y, labelWidth, SurfaceInspectRowHeight), label, PFViewPalette.TextMuted);
            MiniLabel(new Rect(content.x + labelWidth, y, content.width - labelWidth, SurfaceInspectRowHeight),
                value, PFViewPalette.TextSecondary);
            y += SurfaceInspectRowHeight;
        }

        private static string SurfaceInspectBool(bool value)
            => (value ? "PFView_TileInspect_Yes" : "PFView_TileInspect_No").Translate();
    }
}
