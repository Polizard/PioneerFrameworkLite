using UnityEngine;
using Verse;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    /// <summary>
    /// 3D 轨道空间 ↔ Pocket Map 格坐标 的换算器（P1-3 基础设施）。
    /// F2 炮塔锚点 / F3 贯穿弹道 / 着陆 共用。
    ///
    /// 映射基准：
    /// - **B6 多图**：图挂在 part 上 ⇒ 地图中心 = 该 part 的 `positionShip`（含 part 自身旋转）；
    ///   旧单图（`ownerPartId < 0`）仍 = 实体原点，逐位不变。
    /// - 地图 1 格 = 船体 1 米（Part 尺寸 = mapWidth × 50m × mapHeight）
    /// - 地图 xz 平面 = 该 part 的局部 xz 平面；part 的 y = 地图地面高度。
    /// </summary>
    public static class MapCoordMapper
    {
        /// <summary>本图对应的 part（B6；旧档未绑定/无 Game 时 null ⇒ 退回实体原点）。</summary>
        private static SpacePart? OwnerPartOf(Map map)
            => map.GetComponent<SpaceMapComp>()?.OwnerPart;

        /// <summary>地图格 → 船体局部米坐标（y = 0，地面）。1 格 = 1 米；B6 自动叠加挂图 part 的位置/旋转。</summary>
        public static Vector3 MapCellToShip(Map map, IntVec3 cell)
        {
            return MapCellToShip(map, cell, OwnerPartOf(map));
        }

        /// <summary>B6：显式指定挂图 part 的地图格 → 船体局部米（null = 实体原点基准）。</summary>
        public static Vector3 MapCellToShip(Map map, IntVec3 cell, SpacePart? ownerPart)
        {
            return MapLocalToShip(MapCellToShip(map.Center, cell), ownerPart);
        }

        /// <summary>B6 纯换算：地图局部米（中心为原点）→ 船体系绝对米（叠加 part 位置/旋转）。
        /// 与 <see cref="ShipToMapLocal"/> 互逆；part = null 时恒等。</summary>
        public static Vector3 MapLocalToShip(Vector3 localMeters, SpacePart? ownerPart)
        {
            if (ownerPart == null) return localMeters;
            DVector3 local = new DVector3(localMeters.x, localMeters.y, localMeters.z);
            DVector3 rotated = DVector3.Rotate(local, ownerPart.rotationShip);
            DVector3 ship = ownerPart.positionShip + rotated;
            return new Vector3((float)ship.x, (float)ship.y, (float)ship.z);
        }

        /// <summary>B6 纯换算：船体系绝对米 → 地图局部米（减 part 位置、逆 part 旋转）。
        /// part = null 时恒等。</summary>
        public static Vector3 ShipToMapLocal(Vector3 shipMeters, SpacePart? ownerPart)
        {
            if (ownerPart == null) return shipMeters;
            DVector3 delta = new DVector3(shipMeters.x, shipMeters.y, shipMeters.z) - ownerPart.positionShip;
            DVector3 local = DVector3.Rotate(delta, DQuaternion.Inverse(ownerPart.rotationShip));
            return new Vector3((float)local.x, (float)local.y, (float)local.z);
        }

        /// <summary>纯逻辑重载（不依赖 Map 实例，便于单元测试）：中心格 + 目标格 → 局部米。</summary>
        public static Vector3 MapCellToShip(IntVec3 center, IntVec3 cell)
        {
            return new Vector3(cell.x - center.x, 0f, cell.z - center.z);
        }

        /// <summary>船体局部米坐标 → 地图格（xz 截断取整，越界收敛到边界）。跳帮落点用；B6 自动逆 part 变换。</summary>
        public static IntVec3 ShipToMapCell(Map map, Vector3 shipMeters)
        {
            return ShipToMapCell(map, shipMeters, OwnerPartOf(map));
        }

        /// <summary>B6：显式指定挂图 part 的船体局部米 → 地图格（null = 实体原点基准）。</summary>
        public static IntVec3 ShipToMapCell(Map map, Vector3 shipMeters, SpacePart? ownerPart)
        {
            Vector3 local = ShipToMapLocal(shipMeters, ownerPart);
            return ShipToMapCell(map.Center, local, new IntVec2(map.Size.x, map.Size.z));
        }

        /// <summary>
        /// 纯逻辑重载（不依赖 Map 实例，便于单元测试）：中心格 + 局部米偏移 → 目标格。
        /// 1 格 = 1 米，局部米 x/z 直接叠加到中心格并取整，越界收敛到 [0, size-1]。
        /// </summary>
        public static IntVec3 ShipToMapCell(IntVec3 center, Vector3 shipMeters, IntVec2 mapSize)
        {
            int x = center.x + (int)System.Math.Round(shipMeters.x);
            int z = center.z + (int)System.Math.Round(shipMeters.z);
            x = Mathf.Clamp(x, 0, mapSize.x - 1);
            z = Mathf.Clamp(z, 0, mapSize.z - 1);
            return new IntVec3(x, 0, z);
        }

        /// <summary>船体局部米坐标 → 世界（含船姿态旋转，米）。</summary>
        public static DVector3 ShipToSystem(SpaceEntity entity, DVector3 groupPos, Vector3 shipMeters)
        {
            Vector3 shipUnity = shipMeters * (float)SpaceConstants.UnitsPerMeter;
            Vector3 rotatedShipUnity = entity.RotationUnity * shipUnity;
            DVector3 rotatedMeters = (DVector3)(rotatedShipUnity / (float)SpaceConstants.UnitsPerMeter);
            return groupPos + entity.Position + rotatedMeters;
        }

        /// <summary>船体局部米坐标 → Map 场景 3D 坐标（xz = 格坐标，y = 高度单位）。F3 贯穿弹道用；B6 自动逆 part 变换。</summary>
        public static Vector3 ShipToMap(Map map, Vector3 shipMeters)
        {
            return ShipToMap(map, shipMeters, OwnerPartOf(map));
        }

        /// <summary>B6：显式指定挂图 part 的船体局部米 → Map 场景 3D（null = 实体原点基准）。</summary>
        public static Vector3 ShipToMap(Map map, Vector3 shipMeters, SpacePart? ownerPart)
        {
            Vector3 local = ShipToMapLocal(shipMeters, ownerPart);
            IntVec3 c = map.Center;
            return new Vector3(c.x + local.x, local.y, c.z + local.z);
        }

        /// <summary>世界 → 船体局部米坐标（含船姿态逆旋转）。</summary>
        public static Vector3 SystemToShip(SpaceEntity entity, DVector3 groupPos, DVector3 systemMeters)
        {
            DVector3 delta = systemMeters - (groupPos + entity.Position);
            Vector3 deltaSystemUnity = (Vector3)(delta * SpaceConstants.UnitsPerMeter);
            Vector3 shipUnity = Quaternion.Inverse(entity.RotationUnity) * deltaSystemUnity;
            return shipUnity / (float)SpaceConstants.UnitsPerMeter;
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
