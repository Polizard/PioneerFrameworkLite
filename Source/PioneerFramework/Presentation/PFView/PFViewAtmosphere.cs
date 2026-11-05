using UnityEngine;

namespace PioneerFramework
{
    /// <summary>
    /// 大气层的**视觉**（2026-10-10 用户口径，三问三答）：
    /// ① 「**摄像机越深入天空盒越蔚蓝**」+「按你说的搞日照」⇒ 天空浓淡 = **该高度处的相对密度** `ρ(h)/ρ₀ = exp(−h/H)`
    ///    再乘**日照因子**（相机所在处是否受光）；
    /// ② 「**壳画在 XML 定义的大气层厚度的一半**」⇒ 半透明壳的半径 = `R + 厚度/2`（地球 100 km ⇒ 50 km，半径 ×1.01）；
    /// ③ 「**分层：天空吃满 α，物体吃 α × 0.3 的雾**」⇒ 天空 = 相机 clear color 吃满 α（PFView 的"天空盒"就是它），
    ///    物体 = 整帧叠一层 `α × 0.3` 的雾。
    ///
    /// ⚠ 与大气线（L1）**同一条 ρ(h)**：天空的浓淡就是阻力/包线用的那条密度，不另画一条曲线
    ///   （"两条真相"是这个仓库反复踩过的坑）；并且**缺席即零**——`厚度/ρ₀/H` 任一非正 ⇒ α = 0，
    ///   与 `AtmosphereModel` 的惰性口径一致（月球没有大气 ⇒ 没有蓝天空、没有壳）。
    ///
    /// 本文件只有**纯算术**（可 headless 测）：α 曲线、壳半径比、分层系数。渲染那一半在
    /// `MainTabWindow_PFView.Atmosphere.cs`。
    /// </summary>
    internal static class PFViewAtmosphere
    {
        /// <summary>物体（地面/飞船/山）吃到的雾 = 天空 α × 它（用户口径 0.3）。</summary>
        public const float ObjectHazeFactor = 0.3f;

        /// <summary>壳的**基础**不透明度（乘性色 alpha；正对镜头看得见、不糊住地表）。</summary>
        public const float ShellAlpha = 0.18f;

        /// <summary>
        /// **夜半边**壳的不透明度。
        /// 2026-10-10 用户口径："我觉得**半透明大气壳覆盖全球**也对" ⇒ 壳恢复成**全球覆盖**（受光半球 + 夜半球都画），
        /// 但夜侧只留一层淡淡的余辉（真实地球的夜缘也有气辉），不是与昼侧同亮 —— 这样"覆盖全球"与
        /// 之前那条"夜面不亮/不假亮"同时成立。
        /// 旋钮：给 0 = 夜侧完全没有壳；给 `ShellAlpha` = 不分昼夜。
        /// </summary>
        public const float NightShellAlpha = 0.06f;

        /// <summary>
        /// 壳的半径比 = `1 + (厚度/2) / 半径`。
        /// 用**比值**而不是绝对米数 ⇒ 远景等比挪位（`FarClip` 外的天体被挪近并等比缩放）时自动跟随，不用另算一套。
        /// 地球：1 + 50 km / 5000 km = **1.01**；从外面看，球缘那圈投影宽 ≈ √(2Rt) ≈ 707 km（很显眼的一圈）。
        /// </summary>
        public static float ShellRadiusFactor(double bodyRadiusMeters, double thicknessMeters)
        {
            if (bodyRadiusMeters <= 0.0 || thicknessMeters <= 0.0) return 1f;
            return (float)(1.0 + (thicknessMeters * 0.5) / bodyRadiusMeters);
        }

        /// <summary>
        /// 天空浓淡：`α = ρ(h)/ρ₀ × 日照 = exp(−h/H) × sunlit`，夹到 [0,1]。
        /// · `altitudeMeters` ≤ 0（贴地/钻到地下）按 h = 0 处理（最浓）；
        /// · `thickness ≤ 0 || rho0 ≤ 0 || H ≤ 0` ⇒ 0（**缺席即零**，与 `AtmosphereModel` 同一条）；
        /// · `sunlit` ≤ 0（相机在夜侧）⇒ 0 —— **夜面不许蓝**（用户口径"搞日照"）。
        /// 实测值（地球 H=8 km，日照=1）：0 km → 100% · 8 km → 36.8% · 24 km → 5.0% · 50 km（壳）→ 0.19% · 76 km → 0.008%。
        /// 即"天空在 35–45 km 之间自然黑掉，而壳正好落在天空刚黑的位置"—— 两条口径天然咬合，不是巧合。
        /// </summary>
        public static float SkyAlpha(double altitudeMeters, double thicknessMeters, double rho0,
            double scaleHeightMeters, float sunlit)
        {
            if (thicknessMeters <= 0.0 || rho0 <= 0.0 || scaleHeightMeters <= 0.0) return 0f;
            if (sunlit <= 0f) return 0f;

            double h = altitudeMeters > 0.0 ? altitudeMeters : 0.0;
            double a = System.Math.Exp(-h / scaleHeightMeters) * sunlit;
            if (a <= 0.0) return 0f;
            return a >= 1.0 ? 1f : (float)a;
        }

        /// <summary>
        /// 壳要不要**隐藏**（用户 2026-10-10：「大气那个薄层不应该在观察的 Group 位于薄层下时显示，
        /// 只需要按照高度调整天空盒的颜色就好了」）：
        /// 判据 = **被观察编队的高度 &lt; 壳的高度（= 大气厚度的一半）** ⇒ 隐藏（人已经在气里了，不该再套一个球壳）。
        /// 厚度 ≤ 0（无大气）⇒ 也没有壳可隐藏（返回 false）。
        /// </summary>
        public static bool ShellHiddenForGroupAltitude(double groupAltitudeMeters, double thicknessMeters)
        {
            if (thicknessMeters <= 0.0) return false;
            return groupAltitudeMeters < thicknessMeters * 0.5;
        }

        /// <summary>
        /// **深度缓冲在该距离处的分辨率** `δz ≈ d²/(near·2²⁴)`（与 `distance` 同单位）。
        /// 用途：判断"大气壳/地表补丁会不会和地表抢深度"——两者离地只有几十 km，
        /// 而盯着船看时 `near` 很小（相机离焦点 300 m~3 km）⇒ 行星距离处 δz 会到 km~十 km 量级。
        /// 2026-10-10"壳一闪一闪"就是这条算出来的：0.05×camDistance 那版余量 < 1（必然翻面），
        /// 改成 0.35× 之后余量 > 3。24 位深度缓冲 ⇒ 常数 2²⁴。
        /// </summary>
        public static float DepthResolution(float distance, float nearPlane)
        {
            if (nearPlane <= 1e-6f) return float.MaxValue;
            return distance * distance / (nearPlane * 16777216f);
        }

        /// <summary>
        /// 日照因子：`up`（天体中心 → 相机）与 `toSun`（相机 → 太阳）的夹角余弦，在晨昏线附近给一段渐变
        /// （`dot` 从 0 升到 `TwilightDot` 期间从 0 升到 1），免得昼夜切换是硬边。
        /// ⚠ `toSun` 必须来自 PFView 场景里**那盏方向光**（与星球自身的昼夜同一出处）—— 绝不另算一个太阳。
        /// </summary>
        public const float TwilightDot = 0.15f;

        public static float SunlitFactor(Vector3 up, Vector3 toSun)
        {
            if (up.sqrMagnitude < 1e-9f) return 0f;
            float d = Vector3.Dot(up.normalized, toSun.normalized);
            if (d <= 0f) return 0f;
            return d >= TwilightDot ? 1f : d / TwilightDot;
        }
    }
}
