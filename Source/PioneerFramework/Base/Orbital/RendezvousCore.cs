using System;
using System.Collections.Generic;
using MechJebLib.Lambert;

namespace PioneerFramework
{
    /// <summary>
    /// 交会"恢复/兜底"的纯数学核心（2026-09-16，对齐 MechJeb 的四点：时间当自由变量、
    /// 修正放节点、饱和执行、失败就重规划）。纯函数：不碰 RimWorld / UnityEngine，
    /// 由纯测试（RendezvousCoreTests）钉住行为；游戏侧（SpaceGameComp 引导 / RendezvousPlanner）
    /// 只负责取状态、传委托、用结果。
    ///
    /// ⚠ 参考系约定与引导一致：全部量都在**中心天体参考系**里（r/v 用 group.positionBody/velocityBody，
    /// 目标状态由调用方的 predict 委托给出同系值）。对绕月这类子天体，这是标准 SOI 拼接近似
    /// （与既有引导的 Lambert 用法同口径，不是本类引入的新假设）。
    /// </summary>
    public static class RendezvousCore
    {
        /// <summary>替换计划（S1）：从当前状态重新规划一次交会（等待 + 转移两段）。</summary>
        public struct RecoveryPlan
        {
            public bool Valid;
            /// <summary>从这里到出发点火要等多久（秒）——等待=沿当前轨道滑行，零 Δv。</summary>
            public double WaitSeconds;
            /// <summary>转移时长（秒）。</summary>
            public double TransferSeconds;
            /// <summary>出发段 Δv（在出发点的世界/中心天体参考系里，直接作为 plannedDvSystem）。</summary>
            public DVector3 DepartureDv;
            /// <summary>到达段 Δv 的模（信息用；精确交会由闭环引导做终端消速）。</summary>
            public double ArrivalDv;
            /// <summary>两段 Δv 之和（选优判据）。</summary>
            public double TotalDv;
        }

        /// <summary>相位规划（S3）：进相位轨道 → 滑行 N 圈 → 回到原轨道，用"改周期"补相位差。</summary>
        public struct PhasingPlan
        {
            public bool Valid;
            /// <summary>第一次切向 Δv（世界系，在调用点施加）。</summary>
            public DVector3 OutDv;
            /// <summary>第二次切向 Δv（N 圈后、沿相位轨道的切向反向）——量级同 |OutDv|，方向相反。</summary>
            public DVector3 BackDv;
            /// <summary>滑行圈数（越大越省 Δv、越费时间）。</summary>
            public int Revs;
            /// <summary>滑行总时长（秒）= N × 相位轨道周期。</summary>
            public double CoastSeconds;
            /// <summary>两段切向 Δv 之和（选优判据）。</summary>
            public double TotalDv;
        }

        /// <summary>
        /// 相位优先（S3，MechJeb 的核心手法）：相位差不要用一次大的弧去硬追，而是**改周期滑行 N 圈**。
        /// 圆轨近似下 `ΔT/T = 3·Δv/v`（切向烧），补 Δθ 用 N 圈 ⇒ `Δv = v·Δθ/(6πN)`：
        /// Δv 与 N 成反比（N=8 比 N=1 省 8 倍），代价只是时间。
        /// 符号约定：`deltaPhaseRad &gt; 0` = 本船需要**追**（比目标落后）⇒ 缩短周期 ⇒ 切向**逆行**烧。
        /// </summary>
        public static PhasingPlan PlanPhasing(DVector3 pos, DVector3 vel, double gm,
            double deltaPhaseRad, int maxRevs = 8)
        {
            var best = default(PhasingPlan);
            double r = pos.magnitude;
            double v = vel.magnitude;
            if (r < 1.0 || v < 1.0 || gm <= 0.0 || Math.Abs(deltaPhaseRad) < 1e-6) return best;

            // 选最省 Δv 的圈数（maxRevs），但别小到无意义的量级
            int revs = Math.Max(1, maxRevs);
            double sign = deltaPhaseRad > 0.0 ? -1.0 : 1.0;      // 追 ⇒ 逆行（缩短周期）
            double dv = v * Math.Abs(deltaPhaseRad) / (6.0 * Math.PI * revs);
            if (!(dv > 1e-3)) return best;

            DVector3 tangent = vel.normalized;
            // ⚠ **这不是轨道周期，不要收敛到 `OrbitView.period`**（《大修计划》§14.3 原先把"6 处"说成同一条算式，**那是错的**）：
            //   这里是 `2π√(r³/μ)` = **当前半径处的局部圆轨周期**（即 `2π/n`），是局部角速率的派生量，
            //   取的是**瞬时半径**而不是半长轴。本轮 V1 已逐处分类核实：6 处里 3 处是轨道周期、3 处是这个。
            double basePeriod = 2.0 * Math.PI * Math.Sqrt(r * r * r / gm);
            // 相位轨道周期 = T·(1 + ΔT/T)，ΔT/T = −3·(dv·sign)/v
            double dT = -3.0 * (dv * sign) / v * basePeriod;
            double phasePeriod = basePeriod + dT;
            if (!(phasePeriod > 1.0)) return best;

            best.Valid = true;
            best.OutDv = tangent * (dv * sign);
            best.BackDv = tangent * (-dv * sign);               // 回来时反向同量级
            best.Revs = revs;
            best.CoastSeconds = phasePeriod * revs;
            best.TotalDv = 2.0 * dv;
            return best;
        }

        /// <summary>
        /// 安全缩放上限（S2）：沿 dv 方向找最大的 k∈[0,1]，使"当前位置施加 k·dv 后的轨道"不撞行星。
        /// k=0 ⇒ 此处**完全不能**沿这个方向烧（例如在近地点做逆行：任何量都会把近地点压到地表下）。
        /// 单调性用二分：k 越小越安全（近地点抬得越高），故可二分。
        /// </summary>
        public static double MaxSafeDvScale(DVector3 pos, DVector3 vel, DVector3 dv,
            double safeRadius, double mu, int steps = 12)
        {
            if (dv.sqrMagnitude <= 0.0) return 1.0;
            if (OrbitMath.IsOrbitSafe(pos, vel + dv, safeRadius, mu)) return 1.0;

            double lo = 0.0, hi = 1.0;
            for (int i = 0; i < steps; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (OrbitMath.IsOrbitSafe(pos, vel + dv * mid, safeRadius, mu)) lo = mid;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// 重规划（S1）：从当前状态扫「等待 × 转移时长」网格，取**出发段 Δv** 最省且安全的 Lambert 解。
        ///
        /// 时间量纲说明（与 MechJeb 同思路）：
        ///  · 等待段 = 沿当前轨道滑行（零 Δv）⇒ 相位不对时"多绕几圈"是免费的，不需要为相位花 Δv；
        ///  · 转移时长按霍曼时长 `tof_H = π√(a³/μ)` 的 [0.6, 2.4] 倍做等比扫描（对圆→圆接近最优附近最密）；
        ///  · 每个候选都要过 `IsOrbitSafe`（出发后轨道不撞行星），避免"规划出来的解自己掉下去"。
        /// 只在引导判定"原计划已不可行（修正量失控/工质不够）"时调用，故允许毫秒级成本。
        /// </summary>
        /// <param name="r1">当前中心天体参考系位置（m）。</param>
        /// <param name="v1">当前中心天体参考系速度（m/s）。</param>
        /// <param name="nowTick">当前时刻（physicsTick 秒轴）。</param>
        /// <param name="predictTargetAt">给"绝对时刻(秒，physicsTick 轴)"→ 目标在同系的位置/速度（同游戏侧 TryPredictTargetAtTick 的口径）。</param>
        /// <param name="gm">中心天体 GM。</param>
        /// <param name="safeRadius">安全半径（地表+大气）。</param>
        /// <param name="selfPeriodOrbitRadius">当前轨道的等效圆轨半径（用于估周期与霍曼时长）。</param>
        /// <param name="prograde">转移方向（ShortWay/LongWay）。</param>
        /// <param name="maxDepartureDv">出发段 Δv 上限（超出直接丢弃该候选；通常给"余量 × 系数"）。</param>
        /// <param name="waitMaxPeriods">最长等待 = selfPeriod × 该值（相位窗口可能要多圈，取 2 足够覆盖一个会合周期）。</param>
        public static RecoveryPlan PlanRecovery(
            DVector3 r1, DVector3 v1, double nowTick,
            Func<double, (DVector3 pos, DVector3 vel)> predictTargetAt,
            double gm, double safeRadius, double selfPeriodOrbitRadius,
            bool prograde, double maxDepartureDv, double waitMaxPeriods = 2.0)
        {
            var best = default(RecoveryPlan);
            if (predictTargetAt == null || gm <= 0.0) return best;
            double r1Mag = r1.magnitude;
            if (r1Mag < 1.0) return best;

            // 当前轨道的"等效圆轨"周期（用给定半径，避免在异常轨道上算出无意义周期）
            double rRef = selfPeriodOrbitRadius > 1.0 ? selfPeriodOrbitRadius : r1Mag;
            // ⚠ **这不是轨道周期，不要收敛到 `OrbitView.period`**（《大修计划》§14.3 原先把"6 处"说成同一条算式，**那是错的**）：
            //   这里是 `2π√(r³/μ)` = **当前半径处的局部圆轨周期**（即 `2π/n`），是局部角速率的派生量，
            //   取的是**瞬时半径**而不是半长轴。本轮 V1 已逐处分类核实：6 处里 3 处是轨道周期、3 处是这个。
            double selfPeriod = 2.0 * Math.PI * Math.Sqrt(rRef * rRef * rRef / gm);
            if (!(selfPeriod > 1.0)) return best;

            // 霍曼时长 = π√(a³/μ)，a = (r1 + r2)/2：目标半径由"此刻的目标状态"取（同系同口径）
            var targetNow = predictTargetAt(nowTick);
            double r2Ref = targetNow.pos.sqrMagnitude > 1.0 ? targetNow.pos.magnitude : rRef;
            double aH = 0.5 * (rRef + r2Ref);
            double tofH = Math.PI * Math.Sqrt(aH * aH * aH / gm);
            if (!(tofH > 1.0)) tofH = selfPeriod;

            // 等待网格：一圈内细（相位窗口窄），再往后粗（只为多绕几圈的相位）
            const int waitFine = 16;
            const int waitCoarse = 12;
            double waitFineStep = selfPeriod / waitFine;
            double waitCoarseStep = selfPeriod / 4.0;

            TransferGeometry geom = prograde ? TransferGeometry.ShortWay : TransferGeometry.LongWay;

            // ══ 批7 #5（2026-09-30）：360 格**并行** —— 但把"会写共享态"的部分留在串行 ══
            //   ① 串行：按原来的 `break`/`continue` 规则**枚举格子**，并把三样东西**先算好**：
            //      `wait`/`tof`、出发点状态、**目标状态**（`predictTargetAt` 是调用方的委托；生产侧它走
            //      `TryPredictTargetAtTick → GetCircularPositionAtTick`，而**后者会写组上的惰性缓存**
            //      `cachedPosTick/cachedPos` ⇒ 多线程调同一个目标就是竞争 ✗）⇒ 委托**只在串行相位调**。
            //   ② 并行：每格 = 1 次 `Gooding`（三个计数器已原子化，见 §17.3/R6）+ 预算/安全判据，
            //      各写自己的槽位（**NaN = 该格无候选**）。
            //   ③ 串行：`ParallelOps.MinIndex` 归约 —— 跳过 NaN、**并列取小下标**，与原来
            //      `if (!best.Valid || total < best.TotalDv)`（严格 `<`、按 (wi,ti) 先到先得）**同语义**。
            var cWait = new List<double>();
            var cTof = new List<double>();
            var cRDep = new List<DVector3>();
            var cVDep = new List<DVector3>();
            var cR2 = new List<DVector3>();
            var cV2 = new List<DVector3>();

            for (int wi = 0; wi <= waitFine + waitCoarse * (int)Math.Max(1.0, waitMaxPeriods - 1.0); wi++)
            {
                double wait = wi <= waitFine
                    ? wi * waitFineStep
                    : selfPeriod + (wi - waitFine) * waitCoarseStep;
                if (wait > selfPeriod * waitMaxPeriods + 1.0) break;

                DVector3 rDep = OrbitMath.GetPositionAtTimeDelta(r1, v1, wait, gm);
                DVector3 vDep = OrbitMath.GetVelocityAtTimeDelta(r1, v1, wait, gm);
                if (rDep.sqrMagnitude < 1.0) continue;
                // 出发点本身要安全（等待期间不会撞行星）
                if (!OrbitMath.IsOrbitSafe(rDep, vDep, safeRadius, gm)) continue;

                for (int ti = 0; ti <= 14; ti++)
                {
                    double tof = tofH * (0.6 + 0.15 * ti);          // 0.6 … 2.7 倍霍曼时长
                    var (r2, v2) = predictTargetAt(nowTick + wait + tof);   // ⚠ 只在此串行相位调（见上）
                    if (r2.sqrMagnitude < 1.0) continue;

                    cWait.Add(wait); cTof.Add(tof);
                    cRDep.Add(rDep); cVDep.Add(vDep);
                    cR2.Add(r2); cV2.Add(v2);
                }
            }

            int nCells = cWait.Count;
            if (nCells == 0) return best;

            var rDepArr = cRDep.ToArray();
            var vDepArr = cVDep.ToArray();
            var r2Arr = cR2.ToArray();
            var v2Arr = cV2.ToArray();
            var tofArr = cTof.ToArray();
            var totals = new double[nCells];
            var dv1s = new DVector3[nCells];
            var dv2Mags = new double[nCells];

            // 单项成本量级：**本机实测**约 1.8 µs/格（`[批7 #5 对照]` 串行 0.657 ms / 360 格；真实几何里半径差更大、更贵）
            ParallelOps.For(nCells, 2e-6, k =>
            {
                totals[k] = double.NaN;   // 默认"该格无候选"
                DVector3 vi, vf;
                try
                {
                    // ⚠ try/catch **留在工作体内**（抛到 `Parallel.For` 外会变 `AggregateException` ⇒ 改控制流）
                    (vi, vf) = Gooding.Solve(gm, rDepArr[k], r2Arr[k], tofArr[k], geom);
                }
                catch (Exception)
                {
                    return;                                   // 本次无解（几何退化）→ 该格无候选
                }

                DVector3 dv1 = vi - vDepArr[k];
                double dv1Mag = dv1.magnitude;
                if (!(dv1Mag > 0.0) || dv1Mag > maxDepartureDv) return;
                if (!OrbitMath.IsOrbitSafe(rDepArr[k], vDepArr[k] + dv1, safeRadius, gm)) return;

                double dv2Mag = (v2Arr[k] - vf).magnitude;
                dv1s[k] = dv1;
                dv2Mags[k] = dv2Mag;
                totals[k] = dv1Mag + dv2Mag;
            });

            // ── 串行归约（下标 = (wi,ti) 枚举序 ⇒ 并列时选的正是原来那个"先到"的） ──
            int bestCell = ParallelOps.MinIndex(totals);
            if (bestCell >= 0)
            {
                best.Valid = true;
                best.WaitSeconds = cWait[bestCell];
                best.TransferSeconds = cTof[bestCell];
                best.DepartureDv = dv1s[bestCell];
                best.ArrivalDv = dv2Mags[bestCell];
                best.TotalDv = totals[bestCell];
            }
            return best;
        }
    }
}
