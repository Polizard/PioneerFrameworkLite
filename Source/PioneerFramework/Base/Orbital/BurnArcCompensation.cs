using System;
using MechJebLib.Primitives;

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
    /// ## 解法定型
    /// 两个自由度、目标 =（e → 0，a → 目标）：**网格 + 局部细化**（不引第三方优化器：本模块的规模
    /// 与 MJ 的 18 个 Operation 不同，不需要 alglib 那一档 ✗）。`TryPredict` 是验证器也是目标函数 ✓。
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

            double bestCost = double.MaxValue, bestScale = 1.0, bestShift = 0.0, bestE = 1.0, bestA = 0.0;

            // ① 粗网格（9×9）：Δv 缩放 × 点火相位
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
