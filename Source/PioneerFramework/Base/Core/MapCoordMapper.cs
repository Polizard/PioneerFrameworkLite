using UnityEngine;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// 3D 轨道空间 ↔ Pocket Map 格坐标 的换算器（P1-3 基础设施）。
    /// F2 炮塔锚点 / F3 贯穿弹道 / 着陆 共用。
    ///
    /// 映射基准（与 SpaceMapGenerator.CreateStationEntity 约定一致）：
    /// - 地图中心 = 船体局部原点（空间站 Part.localPosition 为零）
    /// - 地图 1 格 = 船体 1 米（Part 尺寸 = mapWidth × 50m × mapHeight）
    /// - 地图 xz 平面 = 船体局部 xz 平面；船体局部 y = 船体厚度方向（50m）
    /// </summary>
    public static class MapCoordMapper
    {
        /// <summary>地图格 → 船体局部米坐标（y = 0，地面）。1 格 = 1 米。</summary>
        public static Vector3 MapCellToLocal3D(Map map, IntVec3 cell)
        {
            IntVec3 c = map.Center;
            return new Vector3(cell.x - c.x, 0f, cell.z - c.z);
        }

        /// <summary>船体局部米坐标 → 世界（含船姿态旋转，米）。</summary>
        public static DVector3 LocalToWorld(SpaceEntity entity, DVector3 groupPos, Vector3 localMeters)
        {
            Vector3 localUnity = localMeters * (float)SpaceConstants.UnitsPerMeter;
            Vector3 rotatedUnity = entity.Rotation * localUnity;
            DVector3 rotatedMeters = (DVector3)(rotatedUnity / (float)SpaceConstants.UnitsPerMeter);
            return groupPos + entity.Position + rotatedMeters;
        }

        /// <summary>船体局部米坐标 → Map 场景 3D 坐标（xz = 格坐标，y = 高度单位）。F3 贯穿弹道用。</summary>
        public static Vector3 ShipLocalToMapWorld(Map map, Vector3 shipLocalMeters)
        {
            IntVec3 c = map.Center;
            return new Vector3(c.x + shipLocalMeters.x, shipLocalMeters.y, c.z + shipLocalMeters.z);
        }

        /// <summary>世界 → 船体局部米坐标（含船姿态逆旋转）。</summary>
        public static Vector3 WorldToShipLocal(SpaceEntity entity, DVector3 groupPos, DVector3 worldMeters)
        {
            DVector3 delta = worldMeters - (groupPos + entity.Position);
            Vector3 deltaUnity = (Vector3)(delta * SpaceConstants.UnitsPerMeter);
            Vector3 localUnity = Quaternion.Inverse(entity.Rotation) * deltaUnity;
            return localUnity / (float)SpaceConstants.UnitsPerMeter;
        }
    }
}
