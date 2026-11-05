namespace PioneerFramework
{
    /// <summary>
    /// **自动抛级判据纯核**（2026-10-28）。
    ///
    /// 旧判据只看"剩余油比 ≤ 阈值" ⇒ 主发还在烧时就切级，把本级最后一段 Δv 白扔
    /// （实测：2% 剩余 ≈ 300 kg、ve 3500 ⇒ 最多 ~900 m/s 被丢掉）。
    /// 新判据：油比到位 **且（已熄火 或 已空）** —— 先把本级最后一点烧完，再切。
    /// （真实火箭也是在**燃尽**时刻热分离；"还在烧就抛"不是省时间，是丢性能。）
    /// </summary>
    public static class AutoStageCore
    {
        /// <summary>
        /// 按燃料比例的抛级判据。
        /// </summary>
        /// <param name="pool">当前工质池（kg；&lt;0 = 未知）。</param>
        /// <param name="capacity">该网络容量（kg）。</param>
        /// <param name="fraction">触发油比（&gt;0，如 0.02）。</param>
        /// <param name="burning">本级是否仍在燃烧。</param>
        /// <param name="elapsed">计划已运行时间（s）。</param>
        /// <param name="minSeconds">最短运行时间（s）。</param>
        public static bool ShouldStageByFuel(double pool, double capacity, double fraction,
            bool burning, double elapsed, double minSeconds)
        {
            if (!(capacity > 0.0) || !(fraction > 0.0) || pool < 0.0) return false;
            if (elapsed < minSeconds) return false;
            if (pool > capacity * fraction) return false;
            return !burning || pool <= 0.0;   // 熄火或已空才切
        }
    }
}
