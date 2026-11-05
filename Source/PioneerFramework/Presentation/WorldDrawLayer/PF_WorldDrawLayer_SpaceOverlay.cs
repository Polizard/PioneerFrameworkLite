using RimWorld.Planet;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 世界地图的**我方叠加层**（卫星图标 / 轨道线 / 波束 / 标记…）。
    /// 2026-10-15，"反射清理"批 **A-2**：替代原先挂在 `WorldRenderer.DrawWorldLayers` 上的 Harmony Postfix。
    ///
    /// 机制：`GlobalWorldDrawLayerDef.worldDrawLayer`（Type 字段）⇒ 本层由 `WorldGrid.InitializeGlobalLayers`
    /// 随其他全局层一起实例化（新档 + 每次读档重建），并由 `WorldRenderer.DrawWorldLayers` 在**同一处**
    /// 调用 `Render()`——与旧 postfix 的调用点**逐字相同**（同帧、同阶段）。绘制内容仍是既有主体
    /// （`SpaceGroupVisualManager.RenderSpaceOverlayFrame()`，含帧耗时探针）。
    ///
    /// ⚠ 两条纪律：
    /// · **构造里 `dirty = false`**：本层不走子网格批处理（船每帧都在动），没有可"重建"的批网格——
    ///   别让它进 `base.Render` 的 `ShouldRegenerate` 路径空跑（该路径的长事件只对
    ///   `WorldDrawLayer_Terrain` 生效——`WorldRenderer.ShouldRegenerateDirtyLayersInLongEvent`
    ///   只查行星层里的 Terrain 层，2026-10-15 核对）；
    /// · `Visible` 沿用旧 postfix 的自查条件（世界视图 + 玩家开关），避免空转。
    /// </summary>
    public class PF_WorldDrawLayer_SpaceOverlay : WorldDrawLayerBase
    {
        public PF_WorldDrawLayer_SpaceOverlay()
        {
            dirty = false;
        }

        public override bool Visible =>
            WorldRendererUtility.WorldSelected && SpaceGroupVisualManager.EnableVisualization;

        public override void Render()
        {
            SpaceGroupVisualManager.RenderSpaceOverlayFrame();
        }
    }
}
