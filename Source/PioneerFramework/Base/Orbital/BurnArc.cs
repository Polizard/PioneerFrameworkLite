using System;
using MechJebLib.Functions;
using MechJebLib.Primitives;

namespace PioneerFramework
{
    /// <summary>
    /// **有限燃烧弧的前向模型**（《MechJeb参考大修计划》B2.2 的第一步；2026-09-19）。
    ///
    /// ## 它算什么
    /// 给一条**脉冲计划**（`Δv` + 节点时刻）+ **规定的平均加速度** `a_mean`，算出
    /// "**用有限燃烧真正执行它，会到达哪条轨道**"（含残留 `e`）。
    ///
    /// ## 为什么是"规定加速度"而不是模型质量（用户 2026-09-19 的裁定）
    /// 计划给的是 `Δv` 与时刻，**不含质量**；"烧多久"是执行时的事实（`a = F/m(t)`）。
    /// 而**时间平均**的 `a_mean = Δv / t_b` 恰好让总 `Δv` 与总时长都对上
    /// ⇒ 弧模型**不需要质量**（也就不会像"计划里的 `t_b`"那样因轨道保持扣工质而过期 ✗）。
    ///
    /// ## 两条被实测校准过的事实（见 `BurnArcTests`）
    /// ① **必须居中**：点火 = 节点 − `t_b/2`。单侧燃烧留 `~5e-3` 残差；居中压到 `~1.4e-4`（20–40 倍）。
    /// ② **方向律** = "解不动、方向按时间取"：每步把计划的 RTN 分量在**当前** RTN 里取方向
    ///    （与执行器 B2.3 的行为一致）。
    ///
    /// ⚠ **一处诚实的空白**：居中的模型仍是一阶残差（`e ∝ t_b`），而"对称区间一阶矩为零"曾
    /// 让我以为该是二阶 —— **数据不支持，照数据记**。
    ///
    /// ## 用途
    /// · 规划器：显示"这条计划执行完预计 e=…"（玩家可见）；
    /// · 真飞验收：**预测 vs 实测**逐条比 ⇒ 执行器与模型的分歧无处可藏。
    /// </summary>
    public static class BurnArc
    {
        /// <summary>RK4 步数。**判据 = `BurnArcPhaseChoiceTests`（2026-09-30 末）**：这个数决定的是
        /// "相位扫描选哪一档会不会变"，所以判据立成"7 档 argmin 与高分辨率参考解一致 + 7 档 e 的误差 ≤ 判别余量/100"，
        /// 跨"几何 × 燃烧时长（×0.25…×16）"共 20 格扫过。
        ///
        /// ⚠ **决定精度的是 `dt/T`**（步大小 ÷ 轨道周期），不是步数本身（燃烧更长 ⇒ 同步数下 dt 更大 ⇒ 误差更大）。
        /// 实测（Release；参考解 6400 步；判定尺度 = 7 档"第一名−第二名"之差，即判别余量）：
        /// · **原值 400 严重过度解析**：Δe ≈ 1e-14，余量 1.7e-5 ~ 2.9e-4 ⇒ 差 **9–10 个数量级**；
        /// · 100 步：Δe 仍 ~1e-14（16× 长燃烧下 2.5e-10 = 余量的 1/2e5）⇒ 判据 20 格全绿（最差 比 ≈ 1e-9）；
        /// · 成本 **1/3.7**（单次预测 180.5 → 49.4 µs）⇒ 7 档扫描 1.07 → ~0.29 ms。
        /// ⇒ 2026-09-30 取 **100**。
        /// ⚠ `internal`（而不是 `private`）**只为可测**：判据要读它，免得测试里再抄一份常数（抄了必然走岔）。</summary>
        /// · **50 步（2026-10-15）：精度账（net10 实测）Δe 1.43e-13 = 判别余量的 1.2e-10（长燃 LEO）/ 9.9e-16（月球）**；
        ///   相位 argmin 在 400/200/100/50 四档下 7 组几何全同（2026-09-30 台架 + net10 判据复跑）；成本再减半。
        ///   （同轮另判：泰勒 V2 判过不接——等效成本下比 RK4 精度差 ~5 个数量级，见总纲批 E。）
        /// · **KSA-②（2026-10-17）：50 变成下限**——步长按**局部轨道周期**归一：
        ///   `steps = clamp(ceil(970·t_b/T_loc), 50, 400)`，`T_loc = 2π√(r³/μ)`（r = 点火时刻半径）。
        ///   短燃（典型 300 s LEO）仍 50 步 ⇒ 既有判据口径不变；长燃按 `(t_b/T)⁴` 放大的误差被抵消；
        ///   上限 400（= 优化前旧值）防成本失控。
        internal const int Steps = 50;
        /// <summary>步数上限（= 2026-09-30 优化前的旧默认 400）；长燃成本封顶。</summary>
        internal const int MaxSteps = 400;
        /// <summary>KSA-② 参考密度（步/局部周期）：970 ⇒ 典型 300 s LEO（r≈7e6，T_loc≈5.83e3 s）恰 50 步。</summary>
        internal const double ReferenceStepDensity = 970.0;

        /// <summary>一条燃烧弧的预测结果。</summary>
        public readonly struct Result
        {
            public readonly OrbitView achieved;
            public readonly double startUt;
            public readonly double endUt;
            public readonly double burnSeconds;
            public readonly double eccentricity;
            public readonly double sma;

            /// <summary>**最后一步所用的推力方向**（适配层参考系）。用于与执行器的 `burnDirectionSystem` 对照
            /// ——"船跟着指令走"不等于"指令本身对"，这个夹角才是那个判据。
            ///
            /// ⚠ 省略（`default` = 零矢量）表示"未提供方向"，消费者按 `sqrMagnitude > 0` 判（见 `SpaceGameComp` 的 `dirTxt`）。
            /// 批6 S3-3b 判定：这是**有意哨兵、两端都带判据** ⇒ 不改成必需（改了只是 8 处噪声，隐患不消）。</summary>
            public readonly V3 finalDir;

            public Result(OrbitView achieved, double startUt, double endUt, double burnSeconds, V3 finalDir = default)
            {
                this.finalDir = finalDir;
                this.achieved = achieved;
                this.startUt = startUt;
                this.endUt = endUt;
                this.burnSeconds = burnSeconds;
                eccentricity = achieved.eccentricity;
                sma = achieved.semiMajorAxis;
            }

            public override string ToString() =>
                $"燃烧弧 {burnSeconds:F0}s（{startUt:F0}→{endUt:F0}）⇒ e={eccentricity:E3}、a={sma / 1000.0:F1}km";
        }

        /// <summary>
        /// **部分弧**预测：只积分到 `elapsedSeconds`（同一套积分的**前缀**，不是第二个模型）。
        /// 用途：燃烧**进行中**把"预测此刻应在哪"与"实测此刻在哪"并排放 ⇒ 曲线一平移 = 冲量施加时机错；
        /// 斜率不对 = 每步幅值错；形变 = 方向内容错（三者可分）。
        /// </summary>
        public static bool TryPredictPartial(OrbitView o, double nodeUt, V3 dv, double burnSeconds,
            double elapsedSeconds, out Result result, out string reason)
            => TryPredictPartial(o, nodeUt, dv, burnSeconds, elapsedSeconds, 0.0, out result, out reason);

        /// <summary>带点火相位的部分弧（诊断用：让"预测"反映**实际命令的**窗口，而不是名义居中档）。</summary>
        public static bool TryPredictPartial(OrbitView o, double nodeUt, V3 dv, double burnSeconds,
            double elapsedSeconds, double ignitionShiftSeconds, out Result result, out string reason)
        {
            double frac = Math.Max(0.0, Math.Min(1.0, elapsedSeconds / Math.Max(burnSeconds, 1e-9)));
            return Predict(o, nodeUt, dv, burnSeconds, frac, ignitionShiftSeconds,
                out result, out reason);
        }

        /// <summary>
        /// **执行延迟**（s）：游戏里 `HasActiveBurn` 在**机动阶段**置位，而 Δv 累加在**物理阶段**，同 tick 物理先跑
        /// ⇒ **首次交付比点火晚一个物理步**。实测铁证（2026-09-19）：模型预测与实测 |v| **全程恒差 13.5 m/s**
        /// （≈ 一步的 14.93 ✓）—— 同形、差整一步。
        /// ⇒ **模型的时间轴要加上这一步**（否则它挑相位/报预测都错一整档；2026-09-19 的补偿失效即此）。
        /// ⚠ 教训：我起初想靠"把点火提前一步"来消它 ✗ —— 那会同时移动**执行**与**模型的假设** ⇒ 差值不变 ✓✓；
        ///   正确的做法是**只把这条延迟告诉模型**（执行器不动）。
        /// </summary>
        public const double ExecutionDelaySeconds = 2.0;

        /// <summary>
        /// 预测有限燃烧的落点。
        /// </summary>
        /// <param name="o">规划时刻的轨道。</param>
        /// <param name="nodeUt">计划的节点时刻（冲量模型里那个"瞬间"）。</param>
        /// <param name="dv">计划的 Δv（**适配层参考系**，与 `PlanNode.dV` 同口径）。</param>
        /// <param name="burnSeconds">燃烧时长（&gt;0）。</param>
        public static bool TryPredict(OrbitView o, double nodeUt, V3 dv, double burnSeconds, out Result result, out string reason)
            => Predict(o, nodeUt, dv, burnSeconds, Steps, 0.0, out result, out reason);

        /// <summary>
        /// 带**点火相位**的预测（B2.2 燃烧弧补偿第一半）：`ignitionShiftSeconds` 把整段燃烧窗口相对
        /// "居中"平移 —— 用来扫"点火提前/推迟哪一步最省 e"。
        /// ⚠ 为什么需要它：实测点燃相位 ±2 s 就能让 e 摆动 **5.5 倍**（台架：2.67e-4 ~ 1.47e-3），
        ///   而游戏的点火落在"首个 ≥ 目标 tick 的物理步"上 ⇒ 相位本身就在抖 ⇒ **相位是 e 的主项，不是各种政策**。
        /// </summary>
        public static bool TryPredict(OrbitView o, double nodeUt, V3 dv, double burnSeconds,
            double ignitionShiftSeconds, out Result result, out string reason)
            => Predict(o, nodeUt, dv, burnSeconds, 1.0, ignitionShiftSeconds, out result, out reason);

        private static bool Predict(OrbitView o, double nodeUt, V3 dv, double burnSeconds, double fraction,
            double ignitionShiftSeconds, out Result result, out string reason)
        {
            result = default;
            reason = "";

            if (!(burnSeconds > 0.0))
            {
                reason = "燃烧时长非正（这是脉冲执行的输入，不是有限燃烧）";
                return false;
            }

            if (dv.sqrMagnitude <= 0.0)
            {
                reason = "Δv 为零";
                return false;
            }

            double aMean = dv.magnitude / burnSeconds; // 规定的平均加速度（模型里不含质量）

            // ① 居中（+ 可选相位偏移）：从节点前半程处开始烧
            double startUt = nodeUt - 0.5 * burnSeconds + ignitionShiftSeconds;
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(startUt);

            // 计划的 RTN 分量在**节点**处取（这就是"解"，燃烧期间不变）
            (V3 rNode, V3 vNode) = o.RightHandedStateVectorsAtUT(nodeUt);
            V3 rhat0 = rNode.normalized;
            V3 hhat0 = V3.Cross(rNode, vNode).normalized;
            V3 that0 = V3.Cross(hhat0, rhat0);
            double rad = V3.Dot(dv, rhat0);
            double tan = V3.Dot(dv, that0);
            double nor = V3.Dot(dv, hhat0);

            // ② 积分（方向按当前 RTN 取 ⇒ "解不动、方向按时间取"）
            // KSA-②：总步数按局部轨道周期归一（下限 Steps=50；上限 400）；短燃与旧版逐位相同。
            int totalSteps = StepCountFor(o, startUt, burnSeconds);
            int stepsDone = (int)Math.Round(totalSteps * Math.Max(0.0, Math.Min(1.0, fraction)));
            if (stepsDone > totalSteps) stepsDone = totalSteps;
            double dt = burnSeconds / totalSteps;
            V3 dirLast = default;
            for (int i = 0; i < stepsDone; i++)
            {
                dirLast = CurrentDir(r, v, rad, tan, nor);
                (r, v) = Rk4(o.mu, r, v, dt, aMean, rad, tan, nor);
            }

            double nowUt = startUt + stepsDone * dt;   // 部分弧 ⇒ 终结时刻 = 当前时刻
            result = new Result(OrbitView.FromStateVectors(r, v, o.mu, nowUt, o.equatorialAxis, o.bodyRadius,
                o.soiRadiusApprox, o.angularVelocity), startUt, nodeUt + 0.5 * burnSeconds, burnSeconds, dirLast);
            return true;
        }

        /// <summary>该弧的**总步数**（KSA-②：按点火时刻的局部轨道周期归一；下限 50、上限 400）。</summary>
        internal static int StepCountFor(OrbitView o, double nodeUt, double burnSeconds, double ignitionShiftSeconds)
            => StepCountFor(o, nodeUt - 0.5 * burnSeconds + ignitionShiftSeconds, burnSeconds);

        /// <summary>按**点火时刻**的状态取局部周期 `T_loc = 2π√(r³/μ)`，再 `steps = clamp(ceil(970·t_b/T_loc), 50, 400)`。
        /// 参考密度 970 让典型 300 s LEO 燃恰为 50 步 ⇒ 短燃与旧版逐位相同；长燃自动加密。</summary>
        internal static int StepCountFor(OrbitView o, double startUt, double burnSeconds)
        {
            (V3 r, V3 _) = o.RightHandedStateVectorsAtUT(startUt);
            double rmag = r.magnitude;
            double mu = o.mu;
            if (!(mu > 0.0) || !(rmag > 0.0) || !(burnSeconds > 0.0)) return Steps;
            double localPeriod = 2.0 * Math.PI * Math.Sqrt(rmag * rmag * rmag / mu);
            double n = ReferenceStepDensity * burnSeconds / localPeriod;
            if (!(n > Steps)) return Steps;          // NaN 也走这里（保守）
            if (n >= MaxSteps) return MaxSteps;
            return (int)Math.Ceiling(n);
        }

        private static (V3 r, V3 v) Rk4(double mu, V3 r, V3 v, double dt, double a, double rad, double tan, double nor)        {
            (V3 dr1, V3 dv1) = Deriv(mu, r, v, a, rad, tan, nor);
            (V3 dr2, V3 dv2) = Deriv(mu, r + 0.5 * dt * dr1, v + 0.5 * dt * dv1, a, rad, tan, nor);
            (V3 dr3, V3 dv3) = Deriv(mu, r + 0.5 * dt * dr2, v + 0.5 * dt * dv2, a, rad, tan, nor);
            (V3 dr4, V3 dv4) = Deriv(mu, r + dt * dr3, v + dt * dv3, a, rad, tan, nor);

            return (r + (dt / 6.0) * (dr1 + 2.0 * dr2 + 2.0 * dr3 + dr4),
                    v + (dt / 6.0) * (dv1 + 2.0 * dv2 + 2.0 * dv3 + dv4));
        }

        /// <summary>当前 RTN 下"计划分量"指向的单位方向（模型与执行器 B2.3 声明的同一条律）。</summary>
        private static V3 CurrentDir(V3 r, V3 v, double rad, double tan, double nor)
        {
            V3 rhat = r.normalized;
            V3 hhat = V3.Cross(r, v).normalized;
            V3 that = V3.Cross(hhat, rhat);
            V3 dir = rad * rhat + tan * that + nor * hhat;
            return dir.sqrMagnitude > 0.0 ? dir.normalized : dir;
        }

        private static (V3 dr, V3 dv) Deriv(double mu, V3 r, V3 v, double a, double rad, double tan, double nor)
        {
            double rmag = r.magnitude;
            V3 rhat = r / rmag;
            V3 hhat = V3.Cross(r, v).normalized;
            V3 that = V3.Cross(hhat, rhat);

            V3 dir = rad * rhat + tan * that + nor * hhat;
            if (dir.sqrMagnitude > 0.0) dir = dir.normalized;

            return (v, -(mu / (rmag * rmag)) * rhat + a * dir);
        }
    }
}
