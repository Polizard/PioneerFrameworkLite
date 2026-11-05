namespace PioneerFramework
{
    /// <summary>
    /// **预对准可行性**（2026-09-19，真飞验收最后一环的结论）。
    ///
    /// ## 治什么
    /// 机动是**居中点火**的：实际点火 = 节点 − 半程燃烧时长（见 `PlannedManeuver.executeTick` 的注释）。
    /// 而处理/对准只从**预点火窗口**开始 ⇒ 真正能用来转姿态的时间是
    /// **`可用 = 窗口长度 − 半程燃烧时长`**，不是窗口长度本身 ✗。
    ///
    /// 实测（2026-09-19，真飞验收）：排队离节点 **60 s**、燃烧预估 **92 s**
    /// ⇒ 可用对准时间只有 **14 s** ⇒ 5°/s 最多转 **70°**，
    /// 而需要 **177°** ⇒ **带着 107° 夹角点火**（751 m/s 里 26–41 m/s 推反、残留 e=8.6e-3）。
    /// **对准逻辑与执行器都没错**——错在"没人检查这件事"。
    ///
    /// ## 用法
    /// 队列**可行性检查**用它给出人话（"这条机动需要 N 秒对准、只有 M 秒"），
    /// 而不是静默地烧歪。⚠ 不给硬拒绝：玩家可能就是要硬烧（比如方向本来差不多）。
    /// </summary>
    public static class AlignFeasibility
    {
        /// <summary>可用对准时间（s）= 窗口 − 半程燃烧；可为负（窗口还没开就该点火了）。</summary>
        public static double UsableAlignSeconds(double lookaheadSeconds, double burnSeconds) =>
            lookaheadSeconds - 0.5 * burnSeconds;

        /// <summary>转 <paramref name="angleDeg" /> 需要的秒数（按动量轮限速）。</summary>
        public static double AlignSecondsFor(double angleDeg, double maxRateDegPerS) =>
            maxRateDegPerS > 0.0 ? angleDeg / maxRateDegPerS : double.PositiveInfinity;

        /// <summary>
        /// 够不够？返回 false 时 <paramref name="reason" /> 是可读原因（照抄进日志/界面）。
        /// </summary>
        public static bool IsFeasible(double angleDeg, double maxRateDegPerS,
            double lookaheadSeconds, double burnSeconds, out string reason)
        {
            double usable = UsableAlignSeconds(lookaheadSeconds, burnSeconds);
            double need = AlignSecondsFor(angleDeg, maxRateDegPerS);

            // 本来就不需要转（夹角 ≈ 0）⇒ 可用时间是多少都不影响（含窗口还没开的情况）
            if (need <= 0.0)
            {
                reason = "";
                return true;
            }

            if (usable >= need)
            {
                reason = "";
                return true;
            }

            reason = $"预对准时间不足：可用 {usable:F0}s（窗口 {lookaheadSeconds:F0}s − 半程燃烧 {0.5 * burnSeconds:F0}s）" +
                     $"、需要 {need:F0}s（{angleDeg:F0}° ÷ {maxRateDegPerS:F1}°/s）" +
                     $" ⇒ 点火时约剩 {System.Math.Max(0.0, angleDeg - usable * maxRateDegPerS):F0}° 夹角，会烧歪。";
            return false;
        }
    }
}
