using System;

namespace PioneerFramework
{
    /// <summary>
    /// **区制判据（B2.1，计划稿 §6 的 B2 首项）**：一个机动该走**准脉冲**（一次烧完）还是**连续**（缓释/螺旋）。
    ///
    /// ## 为什么必须显式判，而不是"哪套机制有空就上哪套"
    /// A4 判决（2026-09-20）说"阶梯的两轴全面占优"—— 但那条判决是在**准脉冲区**里量的：
    /// 阶梯产出的是**脉冲解**（`Σ|dV|` 一次打完），对**低推力船**不可执行 ✗（霍曼那 1726 m/s 要烧几十分钟，
    /// 中途轨道早跑偏了）。⇒ 判决可落地的形式不是"删掉连续律"，而是**按区制分派**：
    ///   · 准脉冲（默认船就在这里，实测 Θ≈4.5°）⇒ **阶梯**；
    ///   · 连续（低推力 / 大 Δv）⇒ **连续律**（形状通道的螺旋）。
    /// 于是"相位通道"在准脉冲区退休、在连续区保留 —— **能力不删，权威只留一个（本判据）**。
    ///
    /// ## 判据（设计稿 §7.3/§7.5，都是无量纲量）
    /// $$\Theta = \omega\,t_b = \frac{\Delta v}{v_c}\cdot\frac{g_{local}}{a_{avg}}$$
    /// （把 Δv 摊成时长 $t_b$ 后，**轨道转过的角度**；Θ 小 = 脉冲近似成立）
    /// $$\alpha = a_{max}/g_{local}$$（推力/重力比；α 小 = 连续推力近似成立）
    /// 阈值为 **Θ ≲ 0.2 rad**（≈11.5°）⇒ 准脉冲。取 0.2 的依据：设计稿实测失败工况 Θ≈1.82 rad（104°）
    /// 与现配置 Θ≈0.18 rad（10.4°）分别在阈值两侧两个数量级，**阈值不在敏感带上**。
    ///
    /// ⚠ 本类**只判区制，不算 Δv**（Δv 由计划层给）；也**不管态**（无状态、纯函数 ⇒ 每步都能调）。
    /// ⚠ 2026-10-27：**本类是区制公式与阈值的唯一权威**；`BurnRegime`（MJ 测试用的 struct 形状）转发到这里。
    /// </summary>
    public static class ManeuverRegime
    {
        /// <summary>区制。</summary>
        public enum Kind
        {
            /// <summary>准脉冲：Θ ≤ 阈值 ⇒ 可以按"一次烧完"规划（阶梯的脉冲解可执行）。</summary>
            QuasiImpulsive,
            /// <summary>连续：Θ &gt; 阈值 ⇒ 必须按缓释/螺旋规划（阶梯的脉冲解不可执行）。</summary>
            Continuous,
        }

        /// <summary>阈值（rad）：Θ ≤ 它 ⇒ 准脉冲。0.2 rad ≈ 11.5°。</summary>
        public const double ThetaThreshold = 0.2;

        /// <summary>
        /// 分类。<paramref name="dv"/> = 机动总量（m/s）、<paramref name="vCircular"/> = 本处圆轨速度、
        /// <paramref name="gLocal"/> = 本处重力加速度、<paramref name="aMax"/> = 可用推力加速度。
        /// 任何非正输入 ⇒ 判**连续**（保守：宁可走那条总能执行的）。
        /// </summary>
        public static Kind Classify(double dv, double vCircular, double gLocal, double aMax,
            out double theta, out double alpha)
        {
            theta = double.NaN;
            alpha = double.NaN;
            if (!(dv > 0.0) || !(vCircular > 0.0) || !(gLocal > 0.0) || !(aMax > 0.0))
                return Kind.Continuous;

            theta = (dv / vCircular) * (gLocal / aMax);
            alpha = aMax / gLocal;
            return theta <= ThetaThreshold ? Kind.QuasiImpulsive : Kind.Continuous;
        }

        /// <summary>便利重载：Θ 用 `ω·t_b` 的等价写法（`ω = v_c/r`、`t_b = dv/aMax`）。</summary>
        public static Kind ClassifyByOmega(double dv, double radius, double mu, double aMax)
        {
            double vc = Math.Sqrt(mu / radius);
            double g = mu / (radius * radius);
            return Classify(dv, vc, g, aMax, out _, out _);
        }
    }
}
