using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 天体视图的 Surface 层（2026-10-15，"反射清理"批 A-3：替代 `PlanetLayer.Visible` /
    /// `PlanetLayer.Raycastable` 两处**属性 getter** 的 Harmony Prefix）。
    ///
    /// 判据与旧补丁**同一套**：当选中的是某个**在册天体层**（`CelestialBodyLayerManager` 的注册表，
    /// 含地球 Surface）时，其他在册天体层既不可见、也不参与射线 —— 否则地球的大球壳会盖住/挡住
    /// 小半径的月球层。
    ///
    /// 挂法 = def 的 `layerType`（1.7 公开的 Type 字段，`WorldGrid.cs:327` 用
    /// `Activator.CreateInstance(layerType, 10 参)` 实例化 ⇒ 本类必须带同样的 10 参构造）：
    /// · 本模月球 def：`1.7/Defs/PlanetLayerDefs/PlanetLayers_Celestial.xml` 直接指本类；
    /// · 原版 `Surface` def（地球）：`1.7/Patches/PlanetLayerDefs_CelestialSurface.xml` 改成同一类（纯 XML）。
    /// </summary>
    public class PF_CelestialSurfaceLayer : SurfaceLayer
    {
        public PF_CelestialSurfaceLayer()
        {
        }

        public PF_CelestialSurfaceLayer(int layerId, PlanetLayerDef def, float radius, Vector3 origin,
            Vector3 viewCenter, float viewAngle, int subdivisions, float extraCameraAltitude,
            float backgroundWorldCameraOffset, float backgroundWorldCameraParallaxDistance)
            : base(layerId, def, radius, origin, viewCenter, viewAngle, subdivisions, extraCameraAltitude,
                backgroundWorldCameraOffset, backgroundWorldCameraParallaxDistance)
        {
        }

        /// <summary>"被选中天体挤掉"：选中的在册天体层 ≠ 本层，而本层也在册。</summary>
        private bool HiddenByCelestialSelection()
        {
            var selected = CelestialBodyLayerManager.CurrentCelestialBodyLayer;
            return selected != null
                && CelestialBodyLayerManager.IsCelestialBodyLayer(this)
                && !ReferenceEquals(this, selected);
        }

        public override bool Visible => !HiddenByCelestialSelection() && base.Visible;

        public override bool Raycastable => !HiddenByCelestialSelection() && base.Raycastable;
    }
}
