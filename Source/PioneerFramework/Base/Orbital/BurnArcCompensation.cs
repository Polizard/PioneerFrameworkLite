using System;
using MechJebLib.Primitives;
using PioneerFramework.Burn;

namespace PioneerFramework
{
    /// <summary>
    /// **B2.2 弧补偿（开环/预测侧）**：把"脉冲计划"换算成"有限燃烧也能落到目标"的执行参数。
    ///
    /// ## 为什么需要
    /// 计划是**脉冲**的（Δv 在节点瞬间施加），执行是**有限**的（`t_b` 内摊开 ⇒ 窗口扫过 ±ω·t_b/2 的弧）。
    /// 两者的差就是"残留 e 与半长轴偏差"——实测（4 条轨道）台架 1~2 km / 游戏 8~32 km。
    /// 本类解的就是这个差：**给两个自由度**——Δv 的**缩放**与**点火相位**——
    /// 让 `BurnArc`（前向模型，已与执行器共用算式与方向律 ✓）预测出来的终态**落在目标上**。
    ///
    /// ## 与"闭环修正"的分工
    /// · **开环**（本类）：计划期就能算，无需测量；但**要求模型绝对准** ⇒ 模型差多少，补偿就差多少；
    /// · **闭环**（下一步）：烧完**实测**终态 → 与目标比 → 一个小修正烧 ⇒ **对未知项免疫**。
    ///   两者叠加才是 MJ 式的做法（它两者都有）。
    ///
    /// ## 解法定型（2026-10-17 更新：KSA-① 牛顿搜索体）
    /// 两个自由度、目标 =（e → 0，a → 目标）。
    /// **搜索体 = `BurnRefineNewton`**（2 自由度牛顿 + 半步回溯；miss = **速度口径**
    /// `v_final − v_circ(r_final)`，与 KSA `CircularOrbitGoal` 同语义；调用 5~8 次 vs 网格 156 次）。
    /// **五护栏**在候迁件内（Δv 界 / 收敛状态 / 奇异 / 越界不静默钳位 / 只做搜索）。
    /// **只接受 `Converged`**；越界/奇异/不收敛/预测失败一律回退到**原 9×9 + 3×5×5 网格**
    /// （保留"最近可行解"语义——擂台 G2 实测界挡住时网格的 best-so-far 更好）。
    /// </summary>
    public static class BurnArcCompensation
    {
        /// <summary>解出的执行参数。</summary>
        public readonly struct Solution
        {
            /// <summary>Δv 缩放（1 = 按计划；乘在计划的 Δv 上）。</summary>
            public readonly double DvScale;
            /// <summary>点火相位偏移（s；加在"节点 − t_b/2"上）。</summary>
            public readonly double ShiftSeconds;
            /// <summary>预测终态：偏心率。</summary>
            public readonly double Ecc;
            /// <summary>预测终态：半长轴（m）。</summary>
            public readonly double Sma;
            /// <summary>目标半长轴（m）。</summary>
            public readonly double TargetSma;
            /// <summary>代价（越小越好；= e + |Δa|/a）。</summary>
            public readonly double Cost;

            public Solution(double dvScale, double shiftSeconds, double ecc, double sma, double targetSma, double cost)
            {
                DvScale = dvScale; ShiftSeconds = shiftSeconds; Ecc = ecc; Sma = sma;
                TargetSma = targetSma; Cost = cost;
            }

            public override string ToString() =>
                $"Δv×{DvScale:F5}、点火 {ShiftSeconds:+0.0;-0.0;0.0}s ⇒ e={Ecc:E3}、a={Sma / 1000.0:F2}km（目标 {TargetSma / 1000.0:F2}km）";
        }

        /// <summary>
        /// 解"有限燃烧也能落到目标"的 (Δv 缩放, 点火相位)。
        /// </summary>
        /// <param name="o">**点火前**的轨道（`BurnArc` 的基准：喂"当前轨道"会让预测随已烧 Δv 漂移 ✗）</param>
        /// <param name="nodeUt">计划节点</param>
        /// <param name="dvPlan">计划的 Δv（**适配层系**，与 `PlanNode.dV` 同口径）</param>
        /// <param name="burnSeconds">计划燃烧时长（= `Δv / a_cmd`）</param>
        /// <param name="targetSma">目标半长轴（m；通常 = 节点处半径）</param>
        /// <param name="dvScaleRange">Δv 缩放搜索范围（相对量，默认 ±1%）</param>
        /// <param name="shiftRangeSeconds">点火相位搜索范围（s，默认 ±8）</param>
        public static bool TrySolve(OrbitView o, double nodeUt, V3 dvPlan, double burnSeconds, double targetSma,
            out Solution result, out string reason,
            double dvScaleRange = 0.01, double shiftRangeSeconds = 8.0)
        {
            result = default;
            reason = "";

            if (!(burnSeconds > 0.0)) { reason = "燃烧时长非正"; return false; }
            if (dvPlan.sqrMagnitude <= 0.0) { reason = "Δv 为零"; return false; }
            if (!(targetSma > 0.0)) { reason = "目标半长轴非正"; return false; }

            // ── ① 牛顿搜索（KSA-①，2026-10-17）：速度口径 miss；只接受 Converged，其余回退旧网格 ──
            // 初轨法向 = 目标圆轨的"顺行切向"基准（ĥ × r̂）；退化时取 +Z（与既有 OrbitView 约定一致）。
            V3 h0 = V3.Cross(o.r, o.v);
            double h0mag = h0.magnitude;
            V3 normal0 = h0mag > 0.0 ? h0 / h0mag : new V3(0.0, 0.0, 1.0);

            void Miss(double scale, double shift, ref Miss3 miss)
            {
                if (!BurnArc.TryPredict(o, nodeUt, dvPlan * scale, burnSeconds, shift,
                        out BurnArc.Result r, out _))
                {
                    miss.X = miss.Y = miss.Z = 1e9;   // 预测失败 ⇒ 当作极差解（搜索会回溯/放弃）
                    return;
                }
                var p = r.achieved;
                double rmag = p.r.magnitude;
                if (!(rmag > 0.0) || double.IsNaN(rmag) || double.IsInfinity(rmag))
                {
                    miss.X = miss.Y = miss.Z = 1e9;
                    return;
                }
                V3 rhat = p.r / rmag;
                V3 that = V3.Cross(normal0, rhat);       // 顺行切向
                double vCirc = Math.Sqrt(p.mu / rmag);   // 圆轨速度（在该终态半径上）
                V3 d = p.v - that * vCirc;
                miss.X = d.x; miss.Y = d.y; miss.Z = d.z;
            }

            var refine = BurnRefineNewton.Refine(Miss, 1.0, 0.0,
                BurnRefineNewton.DefaultTolerance, dvScaleRange, shiftRangeSeconds);
            if (refine.Converged
                && Evaluate(o, nodeUt, dvPlan, burnSeconds, targetSma, refine.Scale, refine.ShiftSeconds,
                    out double ne, out double na))
            {
                result = new Solution(refine.Scale, refine.ShiftSeconds, ne, na, targetSma,
                    ne + Math.Abs(na - targetSma) / targetSma);
                return true;
            }

            // ── ② 兜底：原网格（语义与旧版逐条一致；README 迁移契约第 2 条）──
            double bestCost = double.MaxValue, bestScale = 1.0, bestShift = 0.0, bestE = 1.0, bestA = 0.0;

            // ① 粗网格（9×9）：Δv 缩放 × 点火相位（**兜底路径**，见上方牛顿块）
            for (int i = -4; i <= 4; i++)
            {
                double scale = 1.0 + dvScaleRange * i / 4.0;
                for (int j = -4; j <= 4; j++)
                {
                    double shift = shiftRangeSeconds * j / 4.0;
                    if (!Evaluate(o, nodeUt, dvPlan, burnSeconds, targetSma, scale, shift, out double e, out double a))
                        continue;
                    double cost = e + Math.Abs(a - targetSma) / targetSma;
                    if (cost < bestCost) { bestCost = cost; bestScale = scale; bestShift = shift; bestE = e; bestA = a; }
                }
            }
            if (bestCost == double.MaxValue) { reason = "网格内没有一个预测可用"; return false; }

            // ② 局部细化（3 轮，步长各缩 1/4）
            double stepScale = dvScaleRange / 4.0, stepShift = shiftRangeSeconds / 4.0;
            for (int round = 0; round < 3; round++)
            {
                for (int i = -2; i <= 2; i++)
                    for (int j = -2; j <= 2; j++)
                    {
                        double scale = bestScale + stepScale * i / 2.0;
                        double shift = bestShift + stepShift * j / 2.0;
                        if (!Evaluate(o, nodeUt, dvPlan, burnSeconds, targetSma, scale, shift, out double e, out double a))
                            continue;
                        double cost = e + Math.Abs(a - targetSma) / targetSma;
                        if (cost < bestCost) { bestCost = cost; bestScale = scale; bestShift = shift; bestE = e; bestA = a; }
                    }
                stepScale /= 4.0; stepShift /= 4.0;
            }

            result = new Solution(bestScale, bestShift, bestE, bestA, targetSma, bestCost);
            return true;
        }

        private static bool Evaluate(OrbitView o, double nodeUt, V3 dvPlan, double burnSeconds,
            double targetSma, double scale, double shift, out double ecc, out double sma)
        {
            ecc = 1.0; sma = 0.0;
            if (!BurnArc.TryPredict(o, nodeUt, dvPlan * scale, burnSeconds, shift, out BurnArc.Result r, out _))
                return false;
            ecc = r.achieved.eccentricity;
            sma = r.achieved.semiMajorAxis;
            return true;
        }
    }
}
