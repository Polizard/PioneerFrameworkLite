using UnityEngine;
using Verse;
using PioneerFramework.Combat;

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
            return MapCellToLocal3D(map.Center, cell);
        }

        /// <summary>纯逻辑重载（不依赖 Map 实例，便于单元测试）：中心格 + 目标格 → 局部米。</summary>
        public static Vector3 MapCellToLocal3D(IntVec3 center, IntVec3 cell)
        {
            return new Vector3(cell.x - center.x, 0f, cell.z - center.z);
        }

        /// <summary>船体局部米坐标 → 地图格（xz 截断取整，越界收敛到边界）。跳帮落点用。</summary>
        public static IntVec3 LocalToMapCell(Map map, Vector3 localMeters)
        {
            return LocalToMapCell(map.Center, localMeters, new IntVec2(map.Size.x, map.Size.z));
        }

        /// <summary>
        /// 纯逻辑重载（不依赖 Map 实例，便于单元测试）：中心格 + 局部米偏移 → 目标格。
        /// 1 格 = 1 米，局部米 x/z 直接叠加到中心格并取整，越界收敛到 [0, size-1]。
        /// </summary>
        public static IntVec3 LocalToMapCell(IntVec3 center, Vector3 localMeters, IntVec2 mapSize)
        {
            int x = center.x + (int)System.Math.Round(localMeters.x);
            int z = center.z + (int)System.Math.Round(localMeters.z);
            x = Mathf.Clamp(x, 0, mapSize.x - 1);
            z = Mathf.Clamp(z, 0, mapSize.z - 1);
            return new IntVec3(x, 0, z);
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

        /// <summary>
        /// 船体局部系射线-盒求交（F2 出膛点）：起点在盒内、dir 为单位方向，
        /// 返回射线穿出六面盒面的出射点（碰撞点，局部米）。六面编号：
        /// 0=x- 1=x+ 2=y- 3=y+ 4=z- 5=z+（与 CombatGrid.RaySlabIntersection 一致）。
        /// 与 F3 命中检测共用 slab 求交，保证开火/命中两侧几何对称。
        /// </summary>
        public static Vector3 RayExitAabb(Vector3 origin, Vector3 dir, Vector3 min, Vector3 max, out int exitFace)
        {
            exitFace = -1;
            if (dir.sqrMagnitude < 1e-9f) return origin; // 零方向防御
            Vector3 end = origin + dir.normalized;      // 单位长度线段：t 即沿 dir 的米数
            bool hit = CombatGrid.RaySlabIntersection(
                (DVector3)origin, (DVector3)end, (DVector3)min, (DVector3)max,
                out double tNear, out double tFar, out exitFace);
            // 起点在盒内时 tNear<0、tFar>0 恒命中；未命中仅可能因数值误差 → 兜底返回起点
            if (!hit || tFar <= 0.0) return origin;
            return origin + dir.normalized * (float)tFar;
        }
    }
}
