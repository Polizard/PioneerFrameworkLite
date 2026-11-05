using System;

namespace PioneerFramework
{
    /// <summary>
    /// **轨道规划门面**（2026-10-28）：规划语境的统一入口名。
    ///
    /// 只做分派，不重写数学：
    /// · 霍曼 → `HohmannCore`（纯核，2026-10-27 抽出）；
    /// · 相位 → `RendezvousCore.PlanPhasing`（纯核）；
    /// · 重规划（等待 + 转移）→ `RendezvousCore.PlanRecovery`（纯核）。
    ///
    /// 目的：窗口/引导不再各自记"该调哪个类"，将来接 Lambert/连续律时也只改这一层。
    /// 所有方法都是纯函数（不碰 RimWorld），可被纯测试工程链接。
    /// </summary>
    public static class OrbitPlanner
    {
        /// <summary>霍曼转移（含到达平面变化）。语义见 <see cref="HohmannCore.Compute" />。</summary>
        public static HohmannResult PlanHohmann(DVector3 r1, DVector3 v1, double mu,
            double targetRadius, DVector3 targetNormal)
            => HohmannCore.Compute(r1, v1, mu, targetRadius, targetNormal);

        /// <summary>相位规划（改周期补相位差）。语义见 <see cref="RendezvousCore.PlanPhasing" />。</summary>
        public static RendezvousCore.PhasingPlan PlanPhasing(DVector3 r, DVector3 v, double mu, double phaseErrorRad)
            => RendezvousCore.PlanPhasing(r, v, mu, phaseErrorRad);

        /// <summary>交会重规划（等待 + 转移，时间当自由变量）。语义见 <see cref="RendezvousCore.PlanRecovery" />。</summary>
        public static RendezvousCore.RecoveryPlan PlanRecovery(DVector3 r1, DVector3 v1, double nowTick,
            Func<double, (DVector3 pos, DVector3 vel)> predictTargetAt,
            double gm, double safeRadius, double selfPeriodOrbitRadius,
            bool prograde, double maxDepartureDv, double waitMaxPeriods = 2.0)
            => RendezvousCore.PlanRecovery(r1, v1, nowTick, predictTargetAt, gm, safeRadius,
                selfPeriodOrbitRadius, prograde, maxDepartureDv, waitMaxPeriods);
    }
}
