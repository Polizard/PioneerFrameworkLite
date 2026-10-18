using System;

namespace PioneerFramework
{
    /// <summary>
    /// **B3：零件姿态几何**（2026-10-17）。零件在船体系里有自己的姿态 `rotationShip`；
    /// 这里提供三件事的唯一实现：
    /// · 世界/船体 AABB（OBB→AABB 的保守投影，广播/裁剪/包围盒用）；
    /// · 射线-OBB（命中链用；参数化与 `SlabMath` 的线段 t∈[0,1] 一致）；
    /// · OBB 的投影半尺寸（碰撞/渲染共用）。
    ///
    /// 纪律：**rotationShip == identity 时结果必须与旧轴对齐路径逐位一致**
    ///（调用方仍可走旧路径；这里的矩阵运算在 identity 下退化为同一投影）。
    /// </summary>
    public static class PartGeometry
    {
        private static DQuaternion Effective(DQuaternion q)
            => q.x == 0.0 && q.y == 0.0 && q.z == 0.0 && q.w == 0.0 ? DQuaternion.identity : q;

        /// <summary>零四元数（无效默认值）归一为 identity；其余原样返回。</summary>
        public static DQuaternion EffectiveRotation(DQuaternion q) => Effective(q);

        /// <summary>identity 或零四元数 ⇒ 该 part 可走旧轴对齐路径。</summary>
        public static bool IsIdentityOrZero(DQuaternion q)
            => q == DQuaternion.identity
            || (q.x == 0.0 && q.y == 0.0 && q.z == 0.0 && q.w == 0.0);

        /// <summary>四元数的三根体轴（世界系列向量）。</summary>
        public static void Axes(DQuaternion rotation, out DVector3 ax, out DVector3 ay, out DVector3 az)
        {
            rotation = Effective(rotation);
            double x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w;
            double m00 = 1 - 2 * (y * y + z * z), m01 = 2 * (x * y - z * w), m02 = 2 * (x * z + y * w);
            double m10 = 2 * (x * y + z * w), m11 = 1 - 2 * (x * x + z * z), m12 = 2 * (y * z - x * w);
            double m20 = 2 * (x * z - y * w), m21 = 2 * (y * z + x * w), m22 = 1 - 2 * (x * x + y * y);
            ax = new DVector3(m00, m10, m20);
            ay = new DVector3(m01, m11, m21);
            az = new DVector3(m02, m12, m22);
        }

        /// <summary>
        /// **OBB-OBB SAT**：15 轴（3+3+9 叉积），返回最小重叠轴（A→B）、穿透深度、接触点。
        /// 任一轴投影分离 ⇒ 不重叠。轴顺序固定（A 轴 → B 轴 → 叉积）⇒ 并列深度可确定。
        /// </summary>
        public static bool ObbObbSat(DVector3 centerA, DVector3 halfA, DQuaternion rotA,
            DVector3 centerB, DVector3 halfB, DQuaternion rotB,
            out DVector3 normal, out double depth, out DVector3 contact)
        {
            normal = DVector3.zero;
            depth = 0.0;
            contact = DVector3.zero;
            Axes(rotA, out DVector3 ax, out DVector3 ay, out DVector3 az);
            Axes(rotB, out DVector3 bx, out DVector3 by, out DVector3 bz);
            DVector3 delta = centerB - centerA;
            double best = double.MaxValue;
            double bestRa = 0.0, bestRb = 0.0;
            bool found = false;

            for (int k = 0; k < 15; k++)
            {
                DVector3 axis;
                switch (k)
                {
                    case 0: axis = ax; break;
                    case 1: axis = ay; break;
                    case 2: axis = az; break;
                    case 3: axis = bx; break;
                    case 4: axis = by; break;
                    case 5: axis = bz; break;
                    case 6: axis = DVector3.Cross(ax, bx); break;
                    case 7: axis = DVector3.Cross(ax, by); break;
                    case 8: axis = DVector3.Cross(ax, bz); break;
                    case 9: axis = DVector3.Cross(ay, bx); break;
                    case 10: axis = DVector3.Cross(ay, by); break;
                    case 11: axis = DVector3.Cross(ay, bz); break;
                    case 12: axis = DVector3.Cross(az, bx); break;
                    case 13: axis = DVector3.Cross(az, by); break;
                    default: axis = DVector3.Cross(az, bz); break;
                }
                double len2 = axis.sqrMagnitude;
                if (len2 < 1e-12) continue;   // 平行轴：叉积退化，跳过（不引入额外轴）
                double inv = 1.0 / Math.Sqrt(len2);
                axis *= inv;

                double ra = Math.Abs(DVector3.Dot(axis, ax)) * halfA.x
                    + Math.Abs(DVector3.Dot(axis, ay)) * halfA.y
                    + Math.Abs(DVector3.Dot(axis, az)) * halfA.z;
                double rb = Math.Abs(DVector3.Dot(axis, bx)) * halfB.x
                    + Math.Abs(DVector3.Dot(axis, by)) * halfB.y
                    + Math.Abs(DVector3.Dot(axis, bz)) * halfB.z;
                double d = DVector3.Dot(delta, axis);
                double overlap = ra + rb - Math.Abs(d);
                if (overlap <= 0.0) return false;   // 分离轴
                if (overlap < best)
                {
                    best = overlap;
                    bestRa = ra;
                    bestRb = rb;
                    normal = d >= 0.0 ? axis : -axis;
                    depth = overlap;
                    found = true;
                }
            }

            if (!found) return false;
            // 接触点 = 两支持平面沿最小重叠轴的中点
            DVector3 pA = centerA + normal * bestRa;
            DVector3 pB = centerB - normal * bestRb;
            contact = (pA + pB) * 0.5;
            return true;
        }

        /// <summary>OBB 投影到世界轴后的半尺寸：`|R|·half`（逐轴取绝对值）。</summary>
        public static DVector3 ProjectedHalfExtents(DQuaternion rotation, DVector3 half)
        {
            rotation = Effective(rotation);
            double x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w;
            double m00 = 1 - 2 * (y * y + z * z), m01 = 2 * (x * y - z * w), m02 = 2 * (x * z + y * w);
            double m10 = 2 * (x * y + z * w), m11 = 1 - 2 * (x * x + z * z), m12 = 2 * (y * z - x * w);
            double m20 = 2 * (x * z - y * w), m21 = 2 * (y * z + x * w), m22 = 1 - 2 * (x * x + y * y);
            return new DVector3(
                Math.Abs(m00) * half.x + Math.Abs(m01) * half.y + Math.Abs(m02) * half.z,
                Math.Abs(m10) * half.x + Math.Abs(m11) * half.y + Math.Abs(m12) * half.z,
                Math.Abs(m20) * half.x + Math.Abs(m21) * half.y + Math.Abs(m22) * half.z);
        }

        /// <summary>
        /// 零件在**实体体坐标系**里的 AABB（相对实体原点）。
        /// `entityRotation` 只影响零件的中心位置（entity 的旋转把 part 中心转出），
        /// 半尺寸由 `entityRotation * rotationShip` 的合成姿态投影得到。
        /// </summary>
        public static void WorldAabb(DVector3 positionShip, DVector3 boundsSizeShip,
            DQuaternion entityRotation, DQuaternion rotationShip, out DVector3 min, out DVector3 max)
        {
            entityRotation = Effective(entityRotation);
            rotationShip = Effective(rotationShip);
            DQuaternion combined = rotationShip == DQuaternion.identity
                ? entityRotation
                : entityRotation * rotationShip;
            DVector3 center = DVector3.Rotate(positionShip, entityRotation);
            DVector3 half = boundsSizeShip * 0.5;
            DVector3 rh = ProjectedHalfExtents(combined, half);
            min = center - rh;
            max = center + rh;
        }

        /// <summary>
        /// 船体系线段 vs 零件 OBB。`start`/`end` 是**船体系**坐标（与 `SlabMath` 同口径）；
        /// 返回的 `tEntry` 是线段参数（0..1，起点在内为负）。
        /// </summary>
        public static bool RayObb(DVector3 start, DVector3 end, DVector3 positionShip,
            DQuaternion rotationShip, DVector3 boundsSizeShip, out double tEntry)
        {
            rotationShip = Effective(rotationShip);
            DVector3 half = boundsSizeShip * 0.5;
            DVector3 localStart, localEnd;
            if (rotationShip == DQuaternion.identity)
            {
                localStart = start - positionShip;
                localEnd = end - positionShip;
            }
            else
            {
                DQuaternion inv = DQuaternion.Inverse(rotationShip);
                localStart = DVector3.Rotate(start - positionShip, inv);
                localEnd = DVector3.Rotate(end - positionShip, inv);
            }
            return SlabMath.RaySlabIntersection(localStart, localEnd, -half, half,
                out tEntry, out _, out _);
        }
    }
}
