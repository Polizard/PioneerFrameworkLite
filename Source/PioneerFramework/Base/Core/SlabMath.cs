using System;

namespace PioneerFramework
{
    /// <summary>
    /// 射线/线段-AABB 相交（Slab 法）纯数学核心（2026-09-08 从 CombatGrid 抽出，可单测）。
    /// 抽因：该函数对**退化零长线段**（start==end）旧版恒判命中（三轴 slab 全跳过 → tNear/tFar
    /// 保持 ±∞），导致弹群场景"组刚创建首拍 8 枚全转化"——几何判据必须有单测覆盖。
    /// </summary>
    public static class SlabMath
    {
        /// <summary>
        /// 线段 start→end 与 AABB [min,max] 相交判定（参数 t ∈ [0,1] 与盒区间有交）。
        /// exitFace: 0=-X, 1=+X, 2=-Y, 3=+Y, 4=-Z, 5=+Z（最先离开的面），-1 = 无交/退化。
        /// 退化线段（dir 三分量全 0）语义 = 点-盒包含判定（tNear=tFar=0）。
        /// </summary>
        public static bool RaySlabIntersection(DVector3 start, DVector3 end, DVector3 min, DVector3 max,
            out double tNear, out double tFar, out int exitFace)
        {
            DVector3 dir = end - start;
            tNear = double.NegativeInfinity;
            tFar = double.PositiveInfinity;
            exitFace = -1;

            // 退化线段：点-盒包含（旧版恒真 → 弹群首拍误判全接触的根因）
            if (dir.x == 0 && dir.y == 0 && dir.z == 0)
            {
                tNear = 0.0;
                tFar = 0.0;
                return start.x >= min.x && start.x <= max.x
                    && start.y >= min.y && start.y <= max.y
                    && start.z >= min.z && start.z <= max.z;
            }

            // 注意：方向分量为 0 时必须做"板内检查"而不是跳过该轴——跳过会让"平行且板外"的线段
            // 误判命中（2026-09-08 单测抓出：平行 x 轴但 z 偏 100m 的段旧版恒命中）。
            if (dir.x != 0)
            {
                double t1 = (min.x - start.x) / dir.x;
                double t2 = (max.x - start.x) / dir.x;
                if (t1 > t2) { double temp = t1; t1 = t2; t2 = temp; }
                if (t1 > tNear) tNear = t1;
                if (t2 < tFar) { tFar = t2; exitFace = dir.x < 0 ? 0 : 1; }
            }
            else if (start.x < min.x || start.x > max.x) return false;

            if (dir.y != 0)
            {
                double t1 = (min.y - start.y) / dir.y;
                double t2 = (max.y - start.y) / dir.y;
                if (t1 > t2) { double temp = t1; t1 = t2; t2 = temp; }
                if (t1 > tNear) tNear = t1;
                if (t2 < tFar) { tFar = t2; exitFace = dir.y < 0 ? 2 : 3; }
            }
            else if (start.y < min.y || start.y > max.y) return false;

            if (dir.z != 0)
            {
                double t1 = (min.z - start.z) / dir.z;
                double t2 = (max.z - start.z) / dir.z;
                if (t1 > t2) { double temp = t1; t1 = t2; t2 = temp; }
                if (t1 > tNear) tNear = t1;
                if (t2 < tFar) { tFar = t2; exitFace = dir.z < 0 ? 4 : 5; }
            }
            else if (start.z < min.z || start.z > max.z) return false;

            return tNear <= tFar && tNear <= 1.0 && tFar >= 0;
        }
    }
}
