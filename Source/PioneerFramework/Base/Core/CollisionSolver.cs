using System;

namespace PioneerFramework
{
    /// <summary>
    /// **碰撞求解第一批核心**（2026-10-18）：接触点 + 角速度 + 顺序冲量 + 摩擦。
    ///
    /// 这是纯数学层：不碰 SpaceEntityGroup/CombatManager，便于先钉住“一般性”的算式，
    /// 再由生产路径显式接入。当前生产仍走原有单接触路径；接入前必须过本文件的台架。
    ///
    /// 边界（有意明确）：
    /// · 刚体复合体；接触点由调用方的几何层生成（盒/OBB/解析形状均可）；
    /// · 顺序冲量是**迭代近似**，不做全局 LCP；接触点上限由调用方控制；
    /// · 摩擦为库仑锥的逐接触近似（法向冲量 × μ），不做扭转摩擦；
    /// · 位置修正不在此层（生产仍负责推出重叠）。
    /// </summary>
    public struct CollisionBodyState
    {
        public double Mass;
        public InertiaTensor.InertiaResult Inertia;
        public DQuaternion Rotation;
        public DVector3 Position;        // 群原点（世界系）
        public DVector3 Velocity;        // 质心线速度（世界系）
        public DVector3 AngularVelocity; // 世界系角速度

        public DVector3 CenterOfMassWorld
            => Position + DVector3.Rotate(new DVector3(Inertia.comX, Inertia.comY, Inertia.comZ), Rotation);
    }

    /// <summary>一个接触点（世界系）：位置、A→B 法线、穿透深度。法线必须单位化。</summary>
    public struct CollisionContactPoint
    {
        public DVector3 Point;
        public DVector3 Normal;
        public double Penetration;
    }

    public static class CollisionSolver
    {
        /// <summary>
        /// P3（2026-10-19）：世界系逆惯量（对称 3×3），每次 `Resolve` 每 body 只算一次。
        /// 旧实现每次冲量都做 `Inverse(rotation)` + 两次四元数旋转 + 主轴乘；
        /// 预计算后每个冲量只剩矩阵-向量乘。数值差约 1e-15（台架 2000 随机例）。
        /// </summary>
        private struct WorldInverseInertia
        {
            public bool HasData;
            public double m00, m01, m02, m11, m12, m22;

            public DVector3 Multiply(in DVector3 v) => new(
                m00 * v.x + m01 * v.y + m02 * v.z,
                m01 * v.x + m11 * v.y + m12 * v.z,
                m02 * v.x + m12 * v.y + m22 * v.z);
        }

        private static WorldInverseInertia ComputeWorldInverseInertia(in CollisionBodyState body)
        {
            ref readonly InertiaTensor.InertiaResult r = ref body.Inertia;
            if (!r.HasData || !(r.p1 > 0.0) || !(r.p2 > 0.0) || !(r.p3 > 0.0))
                return default;

            PartGeometry.Axes(body.Rotation, out DVector3 ax, out DVector3 ay, out DVector3 az);
            // 主轴矩阵的列 = r 的列（r00/r10/r20 = 第 0 列）
            DVector3 px = new(r.r00, r.r10, r.r20);
            DVector3 py = new(r.r01, r.r11, r.r21);
            DVector3 pz = new(r.r02, r.r12, r.r22);

            // 世界系主轴列 = R_body · 主轴列
            DVector3 wx = ax * px.x + ay * px.y + az * px.z;
            DVector3 wy = ax * py.x + ay * py.y + az * py.z;
            DVector3 wz = ax * pz.x + ay * pz.y + az * pz.z;

            double i1 = 1.0 / r.p1, i2 = 1.0 / r.p2, i3 = 1.0 / r.p3;
            return new WorldInverseInertia
            {
                HasData = true,
                m00 = i1 * wx.x * wx.x + i2 * wy.x * wy.x + i3 * wz.x * wz.x,
                m01 = i1 * wx.x * wx.y + i2 * wy.x * wy.y + i3 * wz.x * wz.y,
                m02 = i1 * wx.x * wx.z + i2 * wy.x * wy.z + i3 * wz.x * wz.z,
                m11 = i1 * wx.y * wx.y + i2 * wy.y * wy.y + i3 * wz.y * wz.y,
                m12 = i1 * wx.y * wx.z + i2 * wy.y * wy.z + i3 * wz.y * wz.z,
                m22 = i1 * wx.z * wx.z + i2 * wy.z * wy.z + i3 * wz.z * wz.z,
            };
        }

        /// <summary>
        /// 对给定接触点做顺序冲量求解；直接修改两个 body 的线速度/角速度。
        /// 约定：法线 A→B；`restitution` 只作用于法向；`friction` 为库仑系数（0 = 无摩擦）。
        /// </summary>
        public static void Resolve(ref CollisionBodyState a, ref CollisionBodyState b,
            ReadOnlySpan<CollisionContactPoint> contacts, double restitution, double friction, int iterations)
        {
            if (contacts.Length == 0 || iterations <= 0) return;
            if (restitution < 0.0) restitution = 0.0;
            if (friction < 0.0) friction = 0.0;

            double invMassA = a.Mass > 0.0 ? 1.0 / a.Mass : 0.0;
            double invMassB = b.Mass > 0.0 ? 1.0 / b.Mass : 0.0;
            if (invMassA <= 0.0 && invMassB <= 0.0) return;

            WorldInverseInertia ia = ComputeWorldInverseInertia(a);
            WorldInverseInertia ib = ComputeWorldInverseInertia(b);

            int n = contacts.Length;
            Span<double> normalImpulse = stackalloc double[n];
            Span<double> tangentImpulse = stackalloc double[n];
            Span<double> normalMass = stackalloc double[n];
            Span<double> tangentMass = stackalloc double[n];
            Span<DVector3> rA = stackalloc DVector3[n];
            Span<DVector3> rB = stackalloc DVector3[n];
            Span<DVector3> normals = stackalloc DVector3[n];
            Span<DVector3> tangents = stackalloc DVector3[n];

            DVector3 comA = a.CenterOfMassWorld;
            DVector3 comB = b.CenterOfMassWorld;

            // 预计算几何量（每个接触点固定）：r、法线、切线、有效质量。
            for (int i = 0; i < n; i++)
            {
                DVector3 normal = contacts[i].Normal;
                double len2 = normal.sqrMagnitude;
                normal = len2 > 1e-18 ? normal * (1.0 / Math.Sqrt(len2)) : DVector3.up;
                normals[i] = normal;
                rA[i] = contacts[i].Point - comA;
                rB[i] = contacts[i].Point - comB;

                DVector3 tangent = TangentialDirection(rA[i], rB[i], normal, a, b);
                tangents[i] = tangent;
                normalMass[i] = EffectiveMass(ia, ib, invMassA, invMassB, rA[i], rB[i], normal);
                tangentMass[i] = tangent.sqrMagnitude > 0.5
                    ? EffectiveMass(ia, ib, invMassA, invMassB, rA[i], rB[i], tangent)
                    : 0.0;
            }

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int i = 0; i < n; i++)
                {
                    DVector3 normal = normals[i];
                    if (!(normalMass[i] > 1e-18)) continue;

                    DVector3 rel = RelativeVelocity(a, b, rA[i], rB[i]);
                    double vn = DVector3.Dot(rel, normal);

                    // 法向：只在接近（vn<0）时施加；含恢复系数。
                    if (vn < 0.0)
                    {
                        double target = -restitution * vn;
                        double jn = (target - vn) / normalMass[i];
                        double old = normalImpulse[i];
                        double acc = Math.Max(old + jn, 0.0);
                        jn = acc - old;
                        normalImpulse[i] = acc;
                        if (jn != 0.0)
                            ApplyImpulse(ref a, ref b, ia, ib, rA[i], rB[i], normal * jn,
                                invMassA, invMassB);
                    }

                    // 切向摩擦：库仑锥，逐接触近似。
                    if (friction > 0.0 && tangentMass[i] > 1e-18)
                    {
                        rel = RelativeVelocity(a, b, rA[i], rB[i]);
                        DVector3 tangent = tangents[i];
                        double vt = DVector3.Dot(rel, tangent);
                        double jt = -vt / tangentMass[i];
                        double max = friction * normalImpulse[i];
                        double old = tangentImpulse[i];
                        double acc = Math.Max(-max, Math.Min(max, old + jt));
                        jt = acc - old;
                        tangentImpulse[i] = acc;
                        if (jt != 0.0)
                            ApplyImpulse(ref a, ref b, ia, ib, rA[i], rB[i], tangent * jt,
                                invMassA, invMassB);
                    }
                }
            }
        }

        private static DVector3 RelativeVelocity(in CollisionBodyState a, in CollisionBodyState b,
            DVector3 rA, DVector3 rB)
        {
            DVector3 vA = a.Velocity + DVector3.Cross(a.AngularVelocity, rA);
            DVector3 vB = b.Velocity + DVector3.Cross(b.AngularVelocity, rB);
            return vB - vA;
        }

        private static void ApplyImpulse(ref CollisionBodyState a, ref CollisionBodyState b,
            in WorldInverseInertia ia, in WorldInverseInertia ib,
            DVector3 rA, DVector3 rB, DVector3 impulse, double invMassA, double invMassB)
        {
            a.Velocity -= impulse * invMassA;
            b.Velocity += impulse * invMassB;
            a.AngularVelocity -= ia.Multiply(DVector3.Cross(rA, impulse));
            b.AngularVelocity += ib.Multiply(DVector3.Cross(rB, impulse));
        }

        private static double EffectiveMass(in WorldInverseInertia ia, in WorldInverseInertia ib,
            double invMassA, double invMassB, DVector3 rA, DVector3 rB, DVector3 axis)
        {
            double k = invMassA + invMassB;
            if (ia.HasData)
            {
                DVector3 i = ia.Multiply(DVector3.Cross(rA, axis));
                k += DVector3.Dot(DVector3.Cross(i, rA), axis);
            }
            if (ib.HasData)
            {
                DVector3 i = ib.Multiply(DVector3.Cross(rB, axis));
                k += DVector3.Dot(DVector3.Cross(i, rB), axis);
            }
            return k;
        }

        private static DVector3 TangentialDirection(DVector3 rA, DVector3 rB, DVector3 normal,
            in CollisionBodyState a, in CollisionBodyState b)
        {
            DVector3 rel = RelativeVelocity(a, b, rA, rB);
            DVector3 tangent = rel - normal * DVector3.Dot(rel, normal);
            if (tangent.sqrMagnitude > 1e-18) return tangent.normalized;

            // 初始无切向速度：任取一个与法线垂直的稳定轴，避免除零/跳变。
            DVector3 axis = Math.Abs(DVector3.Dot(normal, DVector3.up)) < 0.9 ? DVector3.up : DVector3.right;
            tangent = DVector3.Cross(normal, axis);
            return tangent.sqrMagnitude > 1e-18 ? tangent.normalized : DVector3.right;
        }
    }
}
