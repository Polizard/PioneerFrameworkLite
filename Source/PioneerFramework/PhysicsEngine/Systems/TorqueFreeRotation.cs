using System;

namespace PioneerFramework
{
    /// <summary>
    /// **无外力矩刚体转动**（批 2 物理档的前哨；仅 `SpaceEntityGroup.torqueFreePhysics=true` 的演示组使用）。
    ///
    /// 输入是组的多part 惯量张量（`InertiaResult`：p1≥p2≥p3 + 主轴方向）与当前姿态/角速度；
    /// 在**主轴系**积分 Euler 方程：
    ///   ω̇1 = (p2−p3)/p1 · ω2ω3
    ///   ω̇2 = (p3−p1)/p2 · ω3ω1
    ///   ω̇3 = (p1−p2)/p3 · ω1ω2
    /// 同时把姿态按当前世界角速度积分（body→world 四元数左乘）。
    /// 全部 RK4 子步（每 10 ms 一个子步），**不手写动画、不直接摆姿态**；
    /// 中间主轴的符号翻转是方程的自然结果（贾尼别科夫效应）。
    /// </summary>
    internal static class TorqueFreeRotation
    {
        /// <summary>推进一个物理步；返回 false = 该组没有可用惯量/时间步。</summary>
        public static bool Step(SpaceEntityGroup group, double dt,
            out DVector3 newOmegaWorld, out DQuaternion newRotation)
        {
            newOmegaWorld = group.angularVelocity;
            newRotation = group.CurrentOrbit.rotation;
            return StepRaw(group.InertiaResult, newRotation, group.angularVelocity, dt,
                out newRotation, out newOmegaWorld);
        }

        /// <summary>纯数学入口（单测用）：给定惯量/姿态/世界 ω，推进 dt。</summary>
        internal static bool StepRaw(in InertiaTensor.InertiaResult r, DQuaternion q0,
            DVector3 omegaWorld, double dt, out DQuaternion newRotation, out DVector3 newOmegaWorld)
        {
            newRotation = q0;
            newOmegaWorld = omegaWorld;
            if (!r.HasData || !(r.p1 > 0.0) || !(r.p2 > 0.0) || !(r.p3 > 0.0)) return false;
            if (!(dt > 0.0)) return false;

            DQuaternion q = q0;
            DVector3 bodyOmega = DVector3.Rotate(omegaWorld, DQuaternion.Inverse(q));
            DVector3 w = PrincipalFromBody(r, bodyOmega);

            // dt=2 s 时 200 子步；Combat 档 dt≈0.033 s 时至少 4 子步。上限防极端配置。
            int steps = Math.Max(1, Math.Min(1000, (int)Math.Ceiling(dt / 0.01)));
            double h = dt / steps;

            for (int i = 0; i < steps; i++)
            {
                DVector3 body = BodyFromPrincipal(r, w);
                DVector3 world = DVector3.Rotate(body, q);
                double mag = world.magnitude;
                if (mag > 1e-12)
                    q = DQuaternion.AngleAxis(mag * h, world / mag) * q;

                EulerStep(ref w, r, h);
            }

            newRotation = q;
            newOmegaWorld = DVector3.Rotate(BodyFromPrincipal(r, w), q);
            return true;
        }

        private static DVector3 PrincipalFromBody(in InertiaTensor.InertiaResult r, DVector3 b)
            => new(
                r.r00 * b.x + r.r10 * b.y + r.r20 * b.z,
                r.r01 * b.x + r.r11 * b.y + r.r21 * b.z,
                r.r02 * b.x + r.r12 * b.y + r.r22 * b.z);

        /// <summary>主轴系 → 体轴系（r 的列 = 主轴在体轴中的方向）。</summary>
        private static DVector3 BodyFromPrincipal(in InertiaTensor.InertiaResult r, DVector3 p)
            => new(
                r.r00 * p.x + r.r01 * p.y + r.r02 * p.z,
                r.r10 * p.x + r.r11 * p.y + r.r12 * p.z,
                r.r20 * p.x + r.r21 * p.y + r.r22 * p.z);

        private static DVector3 EulerDeriv(in InertiaTensor.InertiaResult r, DVector3 w)
            => new(
                (r.p2 - r.p3) / r.p1 * w.y * w.z,
                (r.p3 - r.p1) / r.p2 * w.z * w.x,
                (r.p1 - r.p2) / r.p3 * w.x * w.y);

        private static void EulerStep(ref DVector3 w, in InertiaTensor.InertiaResult r, double h)
        {
            DVector3 k1 = EulerDeriv(r, w);
            DVector3 k2 = EulerDeriv(r, w + k1 * (h * 0.5));
            DVector3 k3 = EulerDeriv(r, w + k2 * (h * 0.5));
            DVector3 k4 = EulerDeriv(r, w + k3 * h);
            w += (k1 + k2 * 2.0 + k3 * 2.0 + k4) * (h / 6.0);
        }
    }
}
