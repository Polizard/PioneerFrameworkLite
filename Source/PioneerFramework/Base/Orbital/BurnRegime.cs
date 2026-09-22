using System;

namespace PioneerFramework
{
    /// <summary>
    /// **燃烧区制判据**（《MechJeb参考大修计划》B2.1；设计稿 §7.3/§7.5）。
    ///
    /// 一句话：**这次燃烧能不能当"瞬间点一下"来算**。
    /// 判据是**燃烧扫过角** `Θ = ω·t_b`（燃烧期间轨道转过的角度）：
    /// · `Θ = ω·Δv/a_t`（本文件用的形式）；
    /// · 等价写法 `Θ = (Δv/v_c)·(g_local/a_t)`（计划稿 B2.1 的形式）——
    ///   二者恒等：`ω = v_c/r`、`g_local = v_c²/r` ⇒ `(Δv/v_c)·(g_local/a_t) = Δv·v_c/(r·a_t) = ω·Δv/a_t` ✓
    ///   （判据 `Theta_MatchesThePlansAlternativeForm` 把这条恒等式钉住）。
    ///
    /// `Θ ≲ 0.2` ⇒ **准脉冲**（可以用"在某一刻瞬间给 Δv"的模型：居中点火 + 短烧计划）；
    /// 否则 ⇒ **连续推力**（必须当燃烧弧处理，方向律也不同——那是 B2.3）。
    ///
    /// **锚点（来自既有实测）**：本 mod 当前默认船在 LEO→GEO 档是 `Θ ≈ 10° = 0.175 rad` ⇒ **正好是准脉冲**
    /// （记忆里那句"游戏当前默认船是高推档 Θ≈10°"就是它）⇒ 判据必须把它判成准脉冲，否则会把现有行为改坏。
    /// </summary>
    public readonly struct BurnRegime
    {
        /// <summary>`Θ ≲ 0.2` 算准脉冲（设计稿 §7.3/§7.5 的门）。</summary>
        public const double QuasiImpulseThetaMax = 0.2;

        /// <summary>燃烧扫过角（rad）——燃烧期间轨道转过的角度。</summary>
        public readonly double theta;

        /// <summary>`α = a_max/g_local`（推重比档位：≫1 = 高推、≪1 = 低推）。</summary>
        public readonly double alpha;

        /// <summary>燃烧时长（s）。</summary>
        public readonly double burnSeconds;

        public BurnRegime(double theta, double alpha, double burnSeconds)
        {
            this.theta = theta;
            this.alpha = alpha;
            this.burnSeconds = burnSeconds;
        }

        /// <summary>能不能当"瞬间脉冲"处理。</summary>
        public bool QuasiImpulse => theta <= QuasiImpulseThetaMax;

        /// <summary>
        /// 按烧点半径与推力加速度分类。
        /// </summary>
        /// <param name="dv">本次燃烧的 Δv 幅值（m/s）。</param>
        /// <param name="radius">烧点半径（m，**从中心量起**）。</param>
        /// <param name="mu">天体引力参数（m³/s²）。</param>
        /// <param name="aThrust">推力加速度（m/s²，取燃烧段的**峰**值 ⇒ `α` 才有"档位"的含义）。</param>
        public static BurnRegime Classify(double dv, double radius, double mu, double aThrust)
        {
            if (!(radius > 0.0) || !(mu > 0.0) || !(aThrust > 0.0) || dv < 0.0)
                return new BurnRegime(double.NaN, double.NaN, double.NaN);

            double omega = Math.Sqrt(mu / (radius * radius * radius));
            double gLocal = mu / (radius * radius);
            double burnSeconds = dv / aThrust;

            return new BurnRegime(omega * burnSeconds, aThrust / gLocal, burnSeconds);
        }

        public string Describe() =>
            $"Θ={theta:F3} rad（{theta * 180.0 / Math.PI:F1}°）｜α={alpha:F2}｜t_b={burnSeconds:F1}s" +
            $" ⇒ {(QuasiImpulse ? "准脉冲" : "连续推力段")}";
    }
}
