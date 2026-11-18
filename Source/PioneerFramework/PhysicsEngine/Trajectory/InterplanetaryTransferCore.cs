using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// **行星际粗转移纯核**（2026-11-18，授权件 `后端变更授权-20261118-行星际粗转移`）。
    ///
    /// 只做三件事，全部是可单测的算术：
    ///   ① 出发：给定恒星系两点的状态与转移时长，用 Gooding Lambert 解出**出发速度**；
    ///   ② 到达：给定进 SOI 时相对目标天体的状态，解出**减速捕获**所需的 Δv；
    ///   ③ 有界判定：捕获是否成功（`v² &lt; 2mu/r`）。
    ///
    /// 口径（与设计稿一致，**故意不精密**）：
    ///   · 转移时长固定取霍曼半周期，不做出发窗口/相位优化；
    ///   · 捕获目标是一条**近心点 = 指定高度**的椭圆，不圆化；
    ///   · 捕获 Δv 乘 `(1 − margin)`，偏"过烧"——宁可掉进更低轨道，也不欠烧飞掠。
    ///
    /// 本文件只依赖 `System` 与同命名空间的纯类型（`DVector3` / `Gooding` / `OrbitMath`），
    /// 由 `PioneerFramework.Pure.Tests` 链接编译，**不许**引入 Verse/Unity。
    /// </summary>
    public static class InterplanetaryTransferCore
    {
        /// <summary>捕获余量默认值：3%（速度乘 0.97 ⇒ Δv 偏大）。</summary>
        public const double DefaultCaptureMargin = 0.03;

        /// <summary>
        /// 出发时长的候选倍率（相对霍曼半周期）。**固定顺序**，扫描遇到严格更小才替换 ⇒ 结果确定。
        /// </summary>
        private static readonly double[] TimeOfFlightScales = { 0.5, 0.75, 1.0, 1.25, 1.5 };

        /// <summary>等待窗口的候选分数（0 = 立刻出发）。升序扫描 ⇒ 平局保留更早出发的候选。</summary>
        private static readonly double[] DelayFractions = { 0.0, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875 };

        /// <summary>霍曼转移半周期（秒）。参数非法返回 0。</summary>
        public static double TransferTimeOfFlight(double r1, double r2, double mu)
        {
            if (!double.IsFinite(r1) || !double.IsFinite(r2) || !double.IsFinite(mu) || rt1(r1) || rt1(r2) || !(mu > 0.0)) return 0.0;
            double a = 0.5 * (r1 + r2);
            return Math.PI * Math.Sqrt(a * a * a / mu);
        }

        private static bool rt1(double value) => !(value > 0.0);

        /// <summary>目标的脱离速度（用于诊断/边界判定）。</summary>
        public static double EscapeSpeed(double mu, double r)
            => mu > 0.0 && r > 0.0 ? Math.Sqrt(2.0 * mu / r) : 0.0;

        /// <summary>是否已被目标天体捕获（有界轨道）。全用严格小于：恰好抛物线算未捕获。</summary>
        public static bool IsBound(double mu, double r, double v)
            => mu > 0.0 && r > 0.0 && double.IsFinite(v) && v * v < 2.0 * mu / r;

        /// <summary>
        /// 出发段：解 Lambert，返回**需要施加的 Δv**（= 解出的出发速度 − 当前速度）。
        /// </summary>
        /// <param name="mu">中心天体（此处为恒星）的 GM。</param>
        /// <param name="r1">出发位置（相对恒星）。</param>
        /// <param name="v1">出发速度（相对恒星）。</param>
        /// <param name="r2">到达时刻的目标位置（相对恒星）。</param>
        /// <param name="tof">转移时长（秒）。</param>
        public static bool TryPlanDeparture(double mu, DVector3 r1, DVector3 v1, DVector3 r2, double tof,
            out DVector3 dv, out string reason)
        {
            dv = DVector3.zero;
            reason = "";
            if (!(mu > 0.0) || !double.IsFinite(mu)) { reason = "InvalidMu"; return false; }
            if (!(tof > 0.0) || !double.IsFinite(tof)) { reason = "InvalidTimeOfFlight"; return false; }
            if (!r1.IsFinite() || !v1.IsFinite() || !r2.IsFinite() || !(r1.magnitude > 0.0) || !(r2.magnitude > 0.0))
            { reason = "InvalidState"; return false; }

            try
            {
                (DVector3 departureVelocity, _) = Gooding.Solve(mu, r1, r2, tof);
                if (!departureVelocity.IsFinite()) { reason = "LambertFailed"; return false; }
                dv = departureVelocity - v1;
                if (!dv.IsFinite()) { reason = "LambertFailed"; return false; }
                return true;
            }
            catch (Exception exception)
            {
                reason = "LambertFailed:" + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// 出发段（窗口扫描版）：在「等一段时间再出发 × 若干转移时长」的候选里取 **|Δv| 最小**的组合。
        ///
        /// 为什么需要它：固定霍曼时长 + 立刻出发，只在目标相位 ≈ 180° 时便宜；相位不匹配时会解出
        /// 高能大椭圆（实测地球→火星在 120° 相位、立刻出发时 Δv 达 59 km/s）。真正的粗解不是"换个时长"，
        /// 而是**等窗口**：按窗口的若干等分延迟出发，各解一次 Lambert，取代价最小者。
        /// 8 个延迟 × 5 个时长 = 40 次 Gooding，微秒级；仍然是"一次点火、不算精确窗口"。
        ///
        /// 船在等待期按二体传播（`OrbitMath`），不引入额外状态。
        /// **确定性**：延迟与时长都按固定升序扫描，只有严格更小才替换 ⇒ 平局保留更早出发的候选。
        /// </summary>
        /// <param name="hohmannTimeOfFlight">霍曼半周期（`TransferTimeOfFlight`）。</param>
        /// <param name="windowSeconds">窗口长度（秒），通常取出发天体的会合周期或一个游戏年。</param>
        /// <param name="targetPositionAtOffset">给定 `延迟 + tof`，返回目标天体相对恒星的位置。</param>
        /// <param name="delaySeconds">输出的等待时长（0 = 立刻出发）。</param>
        public static bool TryPlanDepartureWithScan(double mu, DVector3 r1, DVector3 v1, double hohmannTimeOfFlight,
            double windowSeconds, Func<double, DVector3> targetPositionAtOffset,
            out DVector3 dv, out double delaySeconds, out double timeOfFlight, out string reason,
            IReadOnlyList<double>? timeScales = null, IReadOnlyList<double>? delayFractions = null,
            double timeBias = 0.0, double maxDeltaV = double.PositiveInfinity)
        {
            dv = DVector3.zero;
            delaySeconds = 0.0;
            timeOfFlight = 0.0;
            reason = "";
            if (!(mu > 0.0) || !double.IsFinite(mu)) { reason = "InvalidMu"; return false; }
            if (!(hohmannTimeOfFlight > 0.0) || !double.IsFinite(hohmannTimeOfFlight))
            { reason = "InvalidTimeOfFlight"; return false; }
            if (!(windowSeconds > 0.0) || !double.IsFinite(windowSeconds)) { reason = "InvalidWindow"; return false; }
            if (targetPositionAtOffset == null) { reason = "InvalidTargetProvider"; return false; }

            IReadOnlyList<double> scales = timeScales ?? TimeOfFlightScales;
            IReadOnlyList<double> fractions = delayFractions ?? DelayFractions;
            string lastReason = "NoSolution";
            bool found = false;

            // 候选收集（2026-11-18：滑条需要"按偏好选解"）。timeBias=0 ⇒ 最省 Δv（旧行为）；
            // timeBias=1 ⇒ 时长最短（在 Δv 预算内）。归一化后再加权，避免量纲混用。
            double bias = double.IsFinite(timeBias) ? Math.Clamp(timeBias, 0.0, 1.0) : 0.0;
            var candidates = new List<(double Speed, double Delay, double Tof, DVector3 Dv)>();

            for (int delayIndex = 0; delayIndex < fractions.Count; delayIndex++)
            {
                double fraction = fractions[delayIndex];
                if (!(fraction >= 0.0) || !double.IsFinite(fraction)) continue;
                double delay = windowSeconds * fraction;
                DVector3 chaserPosition = r1, chaserVelocity = v1;
                if (delay > 0.0)
                {
                    try
                    {
                        chaserPosition = OrbitMath.GetPositionAtTimeDelta(r1, v1, delay, mu);
                        chaserVelocity = OrbitMath.GetVelocityAtTimeDelta(r1, v1, delay, mu);
                    }
                    catch { continue; }
                    if (!chaserPosition.IsFinite() || !chaserVelocity.IsFinite()) continue;
                }

                for (int scaleIndex = 0; scaleIndex < scales.Count; scaleIndex++)
                {
                    double scale = scales[scaleIndex];
                    if (!(scale > 0.0) || !double.IsFinite(scale)) continue;
                    double candidateTime = hohmannTimeOfFlight * scale;
                    DVector3 targetPosition;
                    try { targetPosition = targetPositionAtOffset(delay + candidateTime); }
                    catch { continue; }

                    if (!TryPlanDeparture(mu, chaserPosition, chaserVelocity, targetPosition, candidateTime,
                        out DVector3 candidate, out string candidateReason))
                    { lastReason = candidateReason; continue; }

                    double speed = candidate.magnitude;
                    if (!double.IsFinite(speed)) continue;
                    candidates.Add((speed, delay, candidateTime, candidate));
                }
            }

            // 预算过滤 + 归一化加权选解
            double cheapest = double.PositiveInfinity, priciest = 0.0, shortest = double.PositiveInfinity, longest = 0.0;
            foreach (var c in candidates)
            {
                if (c.Speed > maxDeltaV) continue;
                found = true;
                cheapest = Math.Min(cheapest, c.Speed); priciest = Math.Max(priciest, c.Speed);
                shortest = Math.Min(shortest, c.Tof); longest = Math.Max(longest, c.Tof);
            }
            if (found)
            {
                double bestScore = double.PositiveInfinity;
                double speedSpan = Math.Max(priciest - cheapest, 1e-9);
                double timeSpan = Math.Max(longest - shortest, 1e-9);
                foreach (var c in candidates)
                {
                    if (c.Speed > maxDeltaV) continue;
                    double score = (1.0 - bias) * ((c.Speed - cheapest) / speedSpan)
                                 + bias * ((c.Tof - shortest) / timeSpan);
                    if (!(score < bestScore)) continue;   // 严格小于 ⇒ 平局保留更早的候选
                    bestScore = score;
                    dv = c.Dv;
                    delaySeconds = c.Delay;
                    timeOfFlight = c.Tof;
                }
            }

            if (!found)
            {
                dv = DVector3.zero;
                delaySeconds = 0.0;
                timeOfFlight = 0.0;
                reason = lastReason;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 到达段：进 SOI 时相对目标天体的状态 → 减速捕获所需 Δv（沿相对速度反向施加）。
        /// </summary>
        /// <param name="mu">目标天体的 GM。</param>
        /// <param name="r">进 SOI 时的中心距（米）。</param>
        /// <param name="v">进 SOI 时的相对速度大小（m/s）。</param>
        /// <param name="periapsisRadius">捕获椭圆的近心点半径（米），必须小于 r。</param>
        /// <param name="margin">余量（0–1，默认 0.03）：速度乘 (1−margin)，使 Δv 偏大。</param>
        /// <remarks>已经是有界轨道时返回 <c>true</c>、<c>dv = 0</c>、<c>reason = "AlreadyBound"</c>——调用方据此**不点火**。</remarks>
        public static bool TryCaptureDv(double mu, double r, double v, double periapsisRadius, double margin,
            out double dv, out string reason)
        {
            dv = 0.0;
            reason = "";
            if (!(mu > 0.0) || !double.IsFinite(mu) || !(r > 0.0) || !double.IsFinite(r) || !(v >= 0.0) || !double.IsFinite(v))
            { reason = "InvalidState"; return false; }
            if (!(periapsisRadius > 0.0) || !double.IsFinite(periapsisRadius) || !(periapsisRadius < r))
            { reason = "InvalidPeriapsis"; return false; }
            if (!(margin >= 0.0) || !(margin < 1.0) || !double.IsFinite(margin))
            { reason = "InvalidMargin"; return false; }

            if (IsBound(mu, r, v))
            {
                reason = "AlreadyBound";
                return true;
            }

            double semiMajor = 0.5 * (r + periapsisRadius);
            double required = Math.Sqrt(mu * (2.0 / r - 1.0 / semiMajor)) * (1.0 - margin);
            dv = v - required;
            if (!(dv > 0.0))
            {
                // 余量把所需速度压到比当前还低 ⇒ 当作已捕获，别给出负 Δv。
                dv = 0.0;
                reason = "AlreadyBound";
            }
            return true;
        }
    }
}
