using System;

namespace PioneerFramework
{
    /// <summary>
    /// **两冲量转移的相位窗口（闭式）**——《MechJeb参考大修计划》B2.4 第 2 件。
    ///
    /// 计划稿把 B2.4 的缺口收敛成三件小事，其中唯一有算法的是这个：
    /// > τ = (Δθ₀ + n₂·t_tof − θ_trans) / (n₁ − n₂)，t_tof = t_H = π√(a_t³/μ)
    ///
    /// 于是"以 `t_tof` 为一维参数、`τ` 闭式解出"⇒ **不需要二维扫参**
    /// （设计稿 §9.1 已判定二维网格是"用离散点去撞一维解集"）。
    ///
    /// ## 推导（写下来免得下次又推一遍）
    /// 出发时刻 `τ`、转移角 `θ_trans`、到达时：
    /// 航天器角 = θ₁(0) + n₁τ + θ_trans；目标角 = θ₂(0) + n₂(τ + t_tof)
    /// 令二者相等 ⇒ `(n₁ − n₂)τ = Δθ₀ + n₂·t_tof − θ_trans`（`Δθ₀ = θ₂(0) − θ₁(0)`）✓
    ///
    /// ## 两条约定
    /// · **`Δθ₀` 用适配层的 `PhaseAngle`**（沿出发轨道的顺行方向、取 `[0,2π)`）⇒ 解出的 `τ` 是
    ///   "等到的**下一个**窗口"；窗口每**会合周期**重复一次 ⇒ 结果规范化到 `[0, syn)` ✓。
    /// · **只对同向、近圆的轨道成立**（`θ_trans = π` 是霍曼的转移角）。偏心/异面请走
    ///   `TwoImpulseTransfer`（它会自己找），本函数只负责"什么时候出发"。
    /// </summary>
    public static class PhaseWindow
    {
        /// <summary>
        /// 闭式求解"还要等多久才该出发"。
        /// </summary>
        /// <param name="deltaTheta0">当前相位角 Δθ₀（rad，目标相对航天器，沿出发方向，`[0,2π)`）。</param>
        /// <param name="n1">出发轨道的平均角速度（rad/s）。</param>
        /// <param name="n2">目标轨道的平均角速度（rad/s）。</param>
        /// <param name="thetaTransfer">转移角（霍曼 = π）。</param>
        /// <param name="tof">转移时长（s，霍曼 `t_H`）。</param>
        /// <param name="synodicPeriod">会合周期（s；≤0 时按 `2π/|n₁−n₂|` 自算）。</param>
        public static bool TrySolve(double deltaTheta0, double n1, double n2, double thetaTransfer, double tof,
            double synodicPeriod, out double tau, out string reason)
        {
            tau = 0.0;
            reason = "";

            if (!(n1 > 0.0) || !(n2 > 0.0))
            {
                reason = "平均角速度非正（轨道退化？）";
                return false;
            }

            double dn = n1 - n2;
            double syn = synodicPeriod > 0.0 ? synodicPeriod : Math.Abs(2.0 * Math.PI / dn);

            if (Math.Abs(dn) * syn < 1e-9)
            {
                reason = "两条轨道周期几乎相同 ⇒ 相位不演化、没有相位窗口（同轨交会请用调相或终端）";
                return false;
            }

            double raw = (deltaTheta0 + n2 * tof - thetaTransfer) / dn;

            // 窗口每 syn 重复一次 ⇒ 规范化到 [0, syn)
            tau = raw % syn;
            if (tau < 0.0) tau += syn;
            return true;
        }

        /// <summary>
        /// 便捷版：两条轨道 + **霍曼**转移时长（`Astro.HohmannTransferParameters` 是唯一权威）。
        /// 返回 `τ`（等待时长）与 `tof`（转移时长），**两者都从 `ut` 起算**（`ut + τ + tof` = 到达时刻）。
        /// </summary>
        public static bool TrySolveForHohmann(OrbitView dep, OrbitView tgt, double ut,
            out double tau, out double tof, out string reason)
        {
            tau = 0.0;
            tof = 0.0;
            reason = "";

            if (dep.period < 0.0 || tgt.period < 0.0)
            {
                reason = "双曲轨道没有周期 ⇒ 没有相位窗口";
                return false;
            }

            (DVector3 r1, DVector3 _) = dep.RightHandedStateVectorsAtUT(ut);
            (DVector3 r2, DVector3 _) = tgt.RightHandedStateVectorsAtUT(ut);
            (double _, double _, double tt, double _) = MechJebLib.Functions.Astro.HohmannTransferParameters(dep.mu, r1, r2);
            tof = Math.Abs(tt);

            return TrySolve(dep.PhaseAngle(tgt, ut), dep.meanMotion, tgt.meanMotion,
                Math.PI, tof, dep.SynodicPeriod(tgt), out tau, out reason);
        }
    }
}
