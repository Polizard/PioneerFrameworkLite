using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MechJebLib.Functions;
using MechJebLib.Primitives;

namespace PioneerFramework
{
    /// <summary>
    /// **交会阶梯**（B3 / P2 执行与决策层，2026-09-20）：把"计划"从**显示用的估计**升级为**唯一提交物**。
    ///
    /// ## 抄的是什么（以及抄错了会怎样）
    /// 上游 `MechJeb2/MechJebModuleRendezvousAutopilot.Drive()`（KSP 层，`:31-247`）是一条
    /// **首个适用者胜**的九级阶梯。⚠ 计划稿 B3 原来抄的是**外部分析报告 §3.2** 的
    /// `平面→圆化→调相→Hohmann→最接近点→滑翔→完成`——**照它实现是真 bug**：
    /// 那串是**任务分解序**，而"首个适用者胜"要求判据从**最具体**往**最根本**排；
    /// "匹配平面"的判据（`relInc > 0.05°`）几乎恒真 ⇒ 每帧都去匹配平面、永远走不到后面。
    /// 本文件用的是**本体**的顺序（计划稿已回写更正表，含每级的判据与上游成员名）。
    ///
    /// ## 与上游的对应（逐级）
    /// | 级 | 判据（首个为真者胜） | 本文件产出 | 上游 |
    /// |---|---|---|---|
    /// | （前置） | 队列里**已有**已画节点 | — | `Node.ExecuteAllNodes` |
    /// | `Done` | `dist < d·1.05+2` 且 `\|v_rel\| < 1` | 无节点 | — |
    /// | `MatchVelocitiesNow` | `dist < d·1.05+2` | 1 节点（UT = now） | `DeltaVToMatchVelocities` |
    /// | `MatchAtClosestApproach` | `dist < r_orbit/25`（且 150 s 内到最近点）**或** `最近接近距离 < a_t/25` | 1 节点（UT = 最近点，按"停在期望距离"反推） | `NextClosestApproachTime/Distance` |
    /// | `Intercept` | `dist < r_orbit/25`（不在最近点窗口内） | 1 节点（UT = now+15，到达 `dist/100` 秒后） | `DeltaVToInterceptAtTime` |
    /// | `Hohmann` | `relInc < 0.05°` 且 `e < 0.05`，相位圈数 ≤ 上限 | 2 节点 | `DeltaVAndTimeForHohmannTransfer` |
    /// | `PhasingOrbit` | 同上但相位圈数 > 上限 | 1~2 节点 | `DeltaVToChangeApo/Periapsis` + `DeltaVToCircularize` |
    /// | `Circularize` | `relInc < 0.05°` | 1 节点（离目标半径更近的拱点） | `DeltaVToCircularize` |
    /// | `MatchPlanes` | 兜底 | 1 节点（升/降交点二选一） | `DeltaVAndTimeToMatchPlanes*` |
    ///
    /// ## ⚠ 只有读本体才知道的两处（2026-09-20）
    /// · 上游第 3 级的 `VesselState.Radius` 是**离行星中心的距离**（`VesselState.cs:264` 注释），
    ///   **不是飞船尺寸** ⇒ 判据是 `dist < r_orbit/25`；
    /// · 上游第 5 级里"低/高调相轨道"的可用性检查用了大气高度，我们**没有**大气/安全半径的纯口径
    ///   ⇒ 下限由调用方以参数给出（游戏侧传 `GetBodySafeRadius`，见 <see cref="Params.MinRadius"/>）。
    ///
    /// ## 接口纪律
    /// · **纯核心**（无引擎/无 Unity 依赖），与 `RendezvousGuidanceLaw`/`TrimBurn` 同级；
    /// · 产出就是**既有**的 <see cref="PlanNode"/>（不新建计划类型 —— J2/J3）；
    /// · `PlanNode.burnSeconds`（估计 `t_b`）**不在这里填**：它要质量与推力 ⇒ 由调用方用 `BurnCore` 补
    ///   （与 `OrbitAdapter.TryQueuePlan` 的既有分工一致）；
    /// · **前置条件**：只在**队列为空**时问本函数（= 上游第 0 级"已有节点就执行"由调用方承担）。
    ///   这条前置同时提供了上游靠"节点存在"得到的**稳定性**（不需要额外的迟滞变量）。
    /// </summary>
    public static class RendezvousLadder
    {
        /// <summary>被选中的那一级（用于日志与判据：阶梯"选了哪一级"本身就是可观测量）。</summary>
        public enum Rung
        {
            Done, MatchVelocitiesNow, MatchAtClosestApproach, Intercept, Hohmann, PhasingOrbit, Circularize, MatchPlanes,
            /// ★ 2026-10-01：**近场终端律**（`TerminalRendezvousLaw`，位置+速度联合）自己排的单节点计划。
            /// 它不是阶梯选出来的级，而是"终端律接管"这个事实的标签（日志里 `⇒ Terminal` 一眼可辨）。
            Terminal,
        }

        /// <summary>参数（默认值取**上游的默认值**：`desiredDistance=100` / `maxPhasingOrbits=5` / `maxClosingSpeed=100`）。</summary>
        public sealed class Params
        {
            /// <summary>**完成容差**（m）：`dist < DesiredDistance*1.05+2 && relVel < 1` ⇒ 判定"交会完成"。
            /// 这是**验收口径**（用户裁定 2 km），**只用于完成判据** ✓。</summary>
            public double DesiredDistance = 100.0;
            /// <summary>★ **"停下/收口"的尺度**（m）——与 <see cref="DesiredDistance" /> **必须分开**（2026-09-21）。
            /// 上游只有一个数（`desiredDistance = 100`）**同时**当完成容差与停下门用，之所以没矛盾是因为
            /// **100 m 恰好与接近级的注入速度相容**：接近级按 `dist/100`（上限 `MaxClosingSpeed`）闭合
            /// ⇒ 一次闭合就能把 20 km 收到 ~100 m ⇒ **"停"的门比"接近"的结果还严 ⇒ 两者永不打架** ✓。
            /// ⚠ 我们把完成容差放宽到 2 km（用户裁定）后若**照抄同一个数**当停下门 ⇒ 门比接近尺度松
            /// **20 倍** ⇒ 2 km 就"停"、而接近级还能干 100 m/s ⇒ **停-接近互相打架 = 过冲**
            /// （实测：`Intercept` ×58 ⇄ `MatchAtClosestApproach` ×27 ⇒ Δv 8438 vs 估计 642 ✗✗）。
            /// ⇒ 本字段单独给"停"用，默认 = 上游的 100 ✓。</summary>
            public double MatchDistance = 100.0;

            /// <summary>★★ **"收尾段就地消速"的门**（m；2026-09-21，**本库已知偏离上游的第二处**）。
            ///
            /// ## 它解决什么
            /// 收尾原来只用 `Intercept`（画拦截）——**依赖计划状态**的动作："100 秒后在某个点消速"。
            /// 而船真到那儿时，位置已被**起飞段的有限燃烧（≈12 s）**、执行延迟、带角度点火挪开；
            /// 刹车却仍按**计划的靶**施加 ⇒ 一记错向冲量 ⇒ **实测一步从 1.5 km 甩到 11.2 km**（"抽"）。
            /// 而 `DeltaVToMatchVelocities` 相反：**只用当下活状态**，不预测、不飞长弧 ⇒ **对初始偏差免疫**。
            /// MJ 的收尾正是靠它，只是它的门极小（`desiredDistance*1.05`）⇒ 在那个环里永远进不去。
            ///
            /// ## 取值 25000 m 的依据（**两端都有硬约束**）
            /// · **下端**：要盖住实测那个环的振幅（1.1 ↔ 12 km）⇒ 必须 > 12 km；
            ///   否则船还没进这个门就被 `Intercept` 甩出去了（这正是它过去 0 次的原因）。
            /// · **上端**：必须**远小于** `r/25`（6300 km 轨道上 = 252 km）——那是 `Intercept` 与
            ///   `MatchAtClosestApproach` 的地盘，吃掉它会把"很远很浅的接近"提前停住（过冲）。
            ///   实测：280 km 的门会把 5.5 km / 183 km 那些 `Intercept` 场景整个吃掉（两条既有用例当场变红）。
            /// ⇒ 25 km 落在 `12 km < 门 ≪ 252 km` 之间，两端都有 2× 与 10× 余量。
            ///
            /// ## 回撤方式
            /// 把本字段设回 0（或不传）⇒ `settleGate` 退化为 `MatchDistance*1.05+2` ⇒ **恢复上游口径**。
            /// 判据：距离序列应从"1.5 ↔ 11 km 跳变"变成**单调收敛**；
            /// 若变成"停在某个距离不动" ⇒ 门松过头，往下调。
            /// </summary>
            public double SettleRadius = 25_000.0;
            public double MaxPhasingOrbits = 5.0;
            /// <summary>**接近速度上限**（m/s）。**上游默认 100**。
            ///
            /// ⚠⚠ **2026-09-22 已还原上游值 100**（曾试 30，两次）。理由有硬数字，别再改回去：
            /// 这个上限**只被 `Intercept` 那一级读**（第 214 行），而它决定**拦截的飞行时长**
            /// `closingTime = dist / min(dist/100, 上限)`。把它压到 30 之后：
            /// ```
            /// 6300 km 轨道周期 T = 4976 s
            /// dist=218.7 km：上限 100 → 飞时 2187 s = 0.44 T ✅
            ///               上限  30 → 飞时 7290 s = **1.46 T** ✗
            /// ```
            /// **飞行时长超过一个周期，Lambert 就只能给出"绕近一整圈"的解** ⇒ 实测
            /// `到达冲量 7699.74 m/s`、`Δv 合计 15587.4 m/s` ⇒ `BLOCKED (insufficient propellant, maxDV=3361.9)`
            /// —— **远场拦截整个坏掉**（低推力档一次就能复现）。
            /// ⇒ 我当初加它是为了治**近场**的往复，却挂在**远场**这一级上，属于"在基干之外改阈值"，
            ///   已按用户长期裁定（"别让基干丢在对小处的打磨里"）撤回。
            /// ⚠ 若将来还要治近场：**别动这个全局上限**，去改近场那一级的动作（或加**只在近场生效**的门），
            ///   并且**必须检查 `closingTime` 相对轨道周期的比例**（> ~0.5 T 的解不可用）。
            /// </summary>
            public double MaxClosingSpeed = 100.0;
            /// <summary>调相轨道的**半径下限**（上游用 `主体半径 + 大气高度 + 3000`；我们没有大气口径 ⇒ 外部给）。</summary>
            public double MinRadius ;

            /// <summary>
            /// 是否计算**纯诊断**的独立 Shepperd 复算（拦截级：`closingTime/5` 步传播，781 s ≈ 156 步）。
            /// 结果只写进 `status` 日志、**不参与选级/Δv 决策** ⇒ 生产默认 `false`；
            /// 台架/需要看"计划到达偏差"时置 `true`（2026-10-19 引导计算量削减第一刀）。
            /// </summary>
            public bool IncludeDiagnostics;
        }

        /// <summary>计划里所有节点的 Δv 标量和（诊断/账单用；调用方不必自己再遍历一遍）。</summary>
        public static double TotalDv(List<PlanNode> nodes)
        {
            double sum = 0.0;
            for (int i = 0; i < nodes.Count; i++) sum += nodes[i].dV.magnitude;
            return sum;
        }

        // ── 上游的阈值常量（**照搬，不是我们调的**；改它们等于改行为，必须走实验）──
        private const double DoneDistFactor = 1.05, DoneDistSlack = 2.0;    // `d*1.05 + 2`
        private const double DoneRelVel = 1.0;                              // "完成"的相对速度上限
        private const double CloseRadiusDiv = 25.0;                         // `dist < r_orbit/25`（⚠ 是**轨道半径**/25）
        private const double ClosestApproachDiv = 25.0;                     // `最近接近距离 < a_t/25`
        private const double ClosestApproachWindow = 150.0;                 // 最近点必须在 now+150s 内
        // ★ **(a) 的判据已撤回**（2026-09-21）：它把上游唯一能“一发停在最近接近点”的那一级拒在门外，
        //   实测代价是总 Δv 10318 m/s（Intercept 153 发 / 9580 m/s）✗✗；
        //   它的动机（219.6 km 假 Δv）归入“总修缮”阶段。
        private const double HotApproachSpeed = 10.0, HotLeadSeconds = 1.0; // 进场快 ⇒ 提前 1 s
        private const double PlaneTolDeg = 0.05, EccTol = 0.05;             // `relInc < 0.05°` 且 `e < 0.05`


        private const double InterceptLeadSeconds = 15.0;                   // `UT = now + 15`
        private const double CloseSpeedDiv = 100.0, CloseSpeedFloor = 0.01; // `closingSpeed = dist/100`
        private const double PhasingOrbitSlack = 1.25;                      // `(1 + 1.25/N)^(2/3)`
        private const double PhasingBurnLead = 15.0;                        // 调相第一发在 now+15
        /// <summary>解析预判的**保守余量**（圈）：只有"最早窗口也超出上限这么多圈"才跳过霍曼搜索。</summary>
        private const double PhasingSkipMarginOrbits = 0.5;
        /// <summary>解析预判只信**这个偏心以下**的轨道（圆心模型；0.005 是台架实测的安全边界，见
        /// `HohmannAnalyticWindowTests`：e=0.03 时会误判，e=0.005 时 192 组零违规）。</summary>
        private const double AnalyticMaxEccentricity = 0.005;

        /// <summary>
        /// **规划成本预热 + 成本曲线**（2026-10-21；与 `CombatManager.WarmupCollisionPaths` 同一手法）。
        ///
        /// 现象：游戏内 `[阶梯·耗时] G2 MakePlan 9.2 ms（PhasingOrbit）/ 6.9 ms（Hohmann）`，
        /// 而 net48 台架（`LadderPlanCostSlowTests`）同量级几何只要 **0.3–0.5 ms**。
        /// 首版写死地球几何预热 32 次后，游戏内复验的是：**首调 1.51 ms、中位 1.03、末 0.83（Hohmann）**，
        /// 而正式规划仍 2.2–3.3 ms 且**第 2/3 次比第 1 次还慢** ⇒ 冷启动那部分已被消掉，
        /// 剩下的 2–3 ms 是**算法本身的稳态成本**（PhasingOrbit 支把霍曼搜索跑完再被圈数上限拒掉）。
        ///
        /// 本版做两件事（都不碰行为）：
        /// 1. **几何绑到当前天体**（`mu` / `bodyRadius` 由调用方给）——写死地球档时，月球档的
        ///    周期/传播路径其实没被预热到；半径取其 1.40/1.54/1.62 倍，覆盖真实交会尺度。
        /// 2. **三条分支都过一遍**：Hohmann、PhasingOrbit 的**抬轨**支（自船低于目标）与**压轨**支
        ///    （自船高于目标）—— `PlanPhasingOrbit` 里这两支走不同的 `DeltaVToChangeApoapsis/Periapsis`。
        /// 返回成本曲线（每条支的 首/中位/末），**空串 = 预热失败**；结果全部丢弃，不碰任何状态。
        /// 判据：**首调 ≫ 末调 ⇒ 还在吃冷启动；首末接近（如 1.5 → 0.8）⇒ 冷启动已消，成本是算法的**。
        /// </summary>
        public static string Warmup(double mu, double bodyRadius, int reps = 24)
        {
            if (!(mu > 0.0) || !(bodyRadius > 0.0)) { mu = 2.0e14; bodyRadius = 5.0e6; }
            if (reps < 2) reps = 2;
            try
            {
                double rLow = 1.40 * bodyRadius;
                double rMid = 1.54 * bodyRadius;
                double rHigh = 1.62 * bodyRadius;
                var pars = new Params
                {
                    DesiredDistance = 283.0,
                    MatchDistance = 2000.0,
                    MaxClosingSpeed = 100.0,
                    MaxPhasingOrbits = 5.0,
                    MinRadius = 1.02 * bodyRadius,     // 略高于本体半径：安全半径的真实下限
                };
                var self = WarmCircular(rLow, 0.0, mu, bodyRadius);
                var hohTgt = WarmCircular(rHigh, 90.0, mu, bodyRadius);
                // PhasingOrbit 支：相位符号随造靶约定 ⇒ 不猜，逐个试到真的落该支（试的过程也算预热）。
                var phaRaiseTgt = PickPhasingTarget(self, rMid, mu, bodyRadius, pars, raise: true);
                var phaLowerSelf = WarmCircular(rHigh, 0.0, mu, bodyRadius);
                var phaLowerTgt = PickPhasingTarget(phaLowerSelf, rLow, mu, bodyRadius, pars, raise: false);

                var sw = new System.Diagnostics.Stopwatch();
                var hoh = new double[reps];
                var raise = new double[reps];
                var lower = new double[reps];
                Rung hohRung = Rung.Done, raiseRung = Rung.Done, lowerRung = Rung.Done;
                for (int i = 0; i < reps; i++)
                {
                    sw.Restart();
                    hohRung = MakePlan(self, hohTgt, 0.0, pars, out _, out _);
                    sw.Stop();
                    hoh[i] = sw.Elapsed.TotalMilliseconds;

                    sw.Restart();
                    raiseRung = MakePlan(self, phaRaiseTgt, 0.0, pars, out _, out _);
                    sw.Stop();
                    raise[i] = sw.Elapsed.TotalMilliseconds;

                    sw.Restart();
                    lowerRung = MakePlan(phaLowerSelf, phaLowerTgt, 0.0, pars, out _, out _);
                    sw.Stop();
                    lower[i] = sw.Elapsed.TotalMilliseconds;
                }

                static string Curve(string name, double[] a, Rung rung)
                {
                    var copy = (double[])a.Clone();
                    Array.Sort(copy);
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[{0}] 首 {1:F2}／中位 {2:F2}／末 {3:F2} ms（选中 {4}）",
                        name, a[0], copy[copy.Length / 2], a[a.Length - 1], rung);
                }
                string text = Curve("Hohmann", hoh, hohRung) + "｜" + Curve("Phasing抬", raise, raiseRung);
                text += "｜" + Curve("Phasing压", lower, lowerRung);
                return text;
            }
            catch (Exception)
            {
                return "";   // 预热失败不能影响读档；调用方按空串跳过
            }
        }

        /// <summary>
        /// **闭式霍曼计划**（2026-10-21 第二刀）：给定解析窗口，直接出两发节点，**不跑搜索**。
        ///
        /// 前提（调用方已判）：近圆共面、两轨 `e < AnalyticMaxEccentricity`、窗口在上限内且离边界有余量。
        /// 公式：`tof = π√((a1+a2)³/(8μ))`；烧点速度需求 `√(μ(2/r1 − 2/(r1+r2)))`、
        /// 到达点转移速度 `√(μ(2/r2 − 2/(r1+r2)))` ⇒ 两发都沿**当地速度方向**（霍曼的切向口径）。
        /// 台架对照（`HohmannFastPathComparisonTests`，240 组）：Δv 相对差 中位 0.00% / 最大 0.01%，点火时刻差 ≤10 s。
        /// </summary>
        private static bool TryBuildClosedFormHohmann(OrbitView self, OrbitView target, double now,
            double waitSeconds, double estOrbits, double maxPhasing, List<PlanNode> nodes, out string status)
        {
            status = "";
            double a1 = self.semiMajorAxis, a2 = target.semiMajorAxis, mu = self.mu;
            if (!(a1 > 0.0) || !(a2 > 0.0) || !(mu > 0.0) || !(waitSeconds >= 0.0)) return false;
            double tof = Math.PI * Math.Sqrt(Math.Pow(a1 + a2, 3.0) / (8.0 * mu));
            if (!(tof > 0.0) || double.IsNaN(tof) || double.IsInfinity(tof)) return false;
            double burnUt = now + waitSeconds;
            double arrUt = burnUt + tof;
            (V3 rb, V3 vb) = self.RightHandedStateVectorsAtUT(burnUt);
            (V3 ra, V3 va) = target.RightHandedStateVectorsAtUT(arrUt);
            double r1 = rb.magnitude, r2 = ra.magnitude;
            if (!(r1 > 0.0) || !(r2 > 0.0) || !(vb.magnitude > 0.0) || !(va.magnitude > 0.0)) return false;
            double vNeed = Math.Sqrt(mu * (2.0 / r1 - 2.0 / (r1 + r2)));
            double vArr = Math.Sqrt(mu * (2.0 / r2 - 2.0 / (r1 + r2)));
            if (double.IsNaN(vNeed) || double.IsNaN(vArr)) return false;
            V3 dv1 = vb.normalized * (vNeed - vb.magnitude);
            V3 dv2 = va.normalized * (va.magnitude - vArr);
            nodes.Add(new PlanNode(dv1, burnUt));
            nodes.Add(new PlanNode(dv2, arrUt));
            status = $"霍曼拦截（闭式）：等 {estOrbits:F2} 圈后进入窗口（上限 {maxPhasing:F0} 圈）";
            return true;
        }

        /// <summary>找一个真的会落 `PhasingOrbit` 的目标相位（抬轨/压轨两种自船各试一次）。</summary>
        private static OrbitView PickPhasingTarget(OrbitView self, double targetRadius, double mu,
            double bodyRadius, Params pars, bool raise)
        {
            OrbitView fallback = WarmCircular(targetRadius, 90.0, mu, bodyRadius);
            foreach (double ph in new[] { 90.0, -90.0, 30.0, 150.0, -30.0, -150.0 })
            {
                var cand = WarmCircular(targetRadius, ph, mu, bodyRadius);
                if (MakePlan(self, cand, 0.0, pars, out _, out _) == Rung.PhasingOrbit)
                    return cand;
            }
            return raise ? fallback : fallback;   // 试不到就退回默认相位（至少把代码路径走一遍）
        }

        /// <summary>预热用的同面圆轨（pos/vel 绕轨道法向 (0,−1,0) 转 <paramref name="phaseDeg"/>）。</summary>
        private static OrbitView WarmCircular(double r, double phaseDeg, double mu, double bodyRadius)
        {
            double vc = Math.Sqrt(mu / r);
            double ph = phaseDeg * Math.PI / 180.0;
            var pos = new V3(r * Math.Cos(ph), 0.0, r * Math.Sin(ph));
            var vel = new V3(-vc * Math.Sin(ph), 0.0, vc * Math.Cos(ph));
            return OrbitView.FromStateVectors(pos, vel, mu, 0.0, new V3(0.0, -1.0, 0.0), bodyRadius);
        }

        /// <summary>
        /// 规划一次。返回**被选中的级别**；<paramref name="nodes"/> 为要提交的节点（0~2 条，适配层系）。
        /// <paramref name="status"/> 给日志用（上游同款的状态串的意思，但**我们写数字**）。
        /// </summary>
        public static Rung MakePlan(OrbitView self, OrbitView target, double now, Params p,
            out List<PlanNode> nodes, out string status)
        {
            nodes = new List<PlanNode>(2);
            status = "";
            nodes = new List<PlanNode>(2);
            status = "";

            (V3 rSelf, V3 vSelf) = self.RightHandedStateVectorsAtUT(now);
            (V3 rTgt, V3 vTgt) = target.RightHandedStateVectorsAtUT(now);
            double dist = (rTgt - rSelf).magnitude;
            double relVel = (vTgt - vSelf).magnitude;

            // ── 1. 完成 ──
            if (dist < p.DesiredDistance * DoneDistFactor + DoneDistSlack && relVel < DoneRelVel)
            {
                status = $"完成：dist={dist:F1} m、|v_rel|={relVel:F3} m/s";
                return Rung.Done;
            }

            // ── 2. 就地匹配速度 ──
            // ★ "**就地匹配**"的门用 `MatchDistance`（停下尺度），**不用完成容差**：
            //   用 2 km 容差当门 ⇒ 在 2 km 就“停”而接近级还能干 100 m/s ⇒ 打架（过冲）。
            //
            // "很近"的气泡半径 = `r/25`（上游口径）
            double closeRadius = rSelf.magnitude / CloseRadiusDiv;
            // ★★ **已撤回：第 7 条改动（收尾段就地消速 / `SettleRadius`）**（2026-09-21）。
            //   撤回理由两条，第二条是致命的：
            //   ① **没通过它自己的回撤判据**：门 280 km→25 km 都会吃掉 `Intercept` 的地盘
            //      ⇒ 两条既有用例（5.5 km / 183 km 处应选 `Intercept`）当场变红；
            //   ② **有死结**：`DeltaVToMatchVelocities` 是**纯消速**、**不产生任何靠近**
            //      ⇒ 船在 5 km 处被消速后相对速度 = 0 ⇒ **距离冻在 5 km**，下一轮再消一次零
            //      ⇒ **停在半路**。那正是判据里预设的坏结局，**比"抽"更糟**。
            //   ⇒ **"靠近"必须由能改变距离的动作承担**（`Intercept` 的两冲量 / `MatchAtClosestApproach`
            //     的提前量）；纯消速级只能在**已经很近**时用。
            //   ⇒ 门回到上游口径。`SettleRadius` 字段保留（未使用），留作将来接"停靠式"动作的接口。
            if (dist < p.MatchDistance * DoneDistFactor + DoneDistSlack)
            {
                nodes.Add(new PlanNode(OrbitalManeuverCalculator.DeltaVToMatchVelocities(self, now, target), now));
                status = $"已进入停下尺度（{dist:F1} m ≤ {p.MatchDistance:F1} m）：就地匹配速度";
                return Rung.MatchVelocitiesNow;
            }

            // ── 3/4. 最近点匹配速度（两条入口：很近 / 已在拦截航线上）──
            // ⚠ 这两行原本是 `NextClosestApproachDistance(...)` + `NextClosestApproachTime(...)` ——
            //   而 `Distance` 的定义就是 `Separation(NextClosestApproachTime(...))` ⇒ **同一件事扫两遍** ✗
            //   （每次 ~160 点粗扫 + 细化）。这里算一次时间、量一次距离，**数值逐位相同** ✓。
            double caUt = self.NextClosestApproachTime(target, now);
            double caDist = double.IsNaN(caUt) ? double.NaN : self.Separation(target, caUt);
            bool veryClose = dist < closeRadius;
            bool onCourse = !double.IsNaN(caDist) && caDist < target.semiMajorAxis / ClosestApproachDiv;
            // ★★ **(a) 已撤回（2026-09-21，有硬数字的证伪）**：这里原来还要求"接近距离小到速度匹配有意义"
            //   （`caDist ≤ r×1e-3`）—— 它把**上游唯一能"一发把船停在最近接近点"的那一级**拒在门外 ✗✗
            //   ⇒ "远但已在接近航线上"的局面全落到 `Intercept`（每次排一发 100~300 m/s 的脉冲，
            //   而下一发计划 ~45 s 后就把它覆盖 ⇒ **从来没合成过一次到达**）。
            //   **实测代价**（同场景、先照 MJ 切掉连续律之后）：总 Δv **10318 m/s**，其中 `Intercept`
            //   **153 发 / 9580 m/s**（它自己计划了 192 次、合计 **22668 m/s**），而物理上闭合 47 km 只需 ~100 m/s ✗✗。
            //   ⇒ 恢复上游的门（只看 `CA < a_t/25`），让那一级能接手 ✓。
            //   ⚠ (a) 的动机（219.6 km 那次的假 Δv）仍然存在，但它属于**"总修缮"阶段**：
            //     先把上游跑通，不能因为它做得不够好就把它拆了 ✗（用户长期裁定："先按 MJ 的走"）。
            //   ⚠ "很近"那一支（自带 `caDist < 期望距离`）一直没动 ✓。
            if ((veryClose && caDist < p.MatchDistance && caUt < now + ClosestApproachWindow)
                || (!veryClose && onCourse))
            {
                double burnUt = AdjustClosestApproachUt(self, target, caUt, p.MatchDistance);
                // ⚠ 上游在"接近距离已经小于期望距离"时会把 UT 往前推（`UT -= √|d²−接近距离²|/接近速度`），
                //   推过头就落到**过去** ⇒ 那种节点没意义（执行器只会立刻烧）。这里**夹在 now**：
                //   与上游的唯一语义差别，记在这里（上游没夹）。
                if (burnUt < now) burnUt = now;
                nodes.Add(new PlanNode(OrbitalManeuverCalculator.DeltaVToMatchVelocities(self, burnUt, target), burnUt));
                status = $"在最近点匹配速度：接近 {self.Separation(target, caUt):F0} m（期望 {p.MatchDistance:F0} m）、T+{burnUt - now:F0} s";
                return Rung.MatchAtClosestApproach;
            }

            // ── 5. 很近但不在航线上 ⇒ 画拦截 ──
            if (veryClose)
            {
                double closingSpeed = dist / CloseSpeedDiv;
                if (closingSpeed > p.MaxClosingSpeed) closingSpeed = p.MaxClosingSpeed;
                closingSpeed = Math.Max(CloseSpeedFloor, closingSpeed);
                double closingTime = dist / closingSpeed;
                double burnUt = now + InterceptLeadSeconds;
                // ★★ **接住被丢掉的那个数**（2026-09-21 读数）：`DeltaVToInterceptAtTime` 返回的是
                //   **两冲量转移**——`Item1` = 起飞冲量（我们就用这一发），`Item2` = `vf − transferVf`
                //   = **到达那一刻把相对速度干掉所需的那一发**。上游 `Drive()` 与我们**都写 `(dV, _)` 把它扔了** ✗。
                //   ⇒ 于是"这一发到底是单冲量（飞掠）还是自带刹车"这个决定性的量，一直在日志里看不见。
                //   本行只**读**它、不改行为：它小 ⇒ 单冲量本来就够，环是别的原因；
                //   它不小 ⇒ 刹车从未发出，环就是它。**这个数决定下一步写什么码。**
                (V3 dv1, V3 dv2Arrive) = OrbitalManeuverCalculator.DeltaVToInterceptAtTime(self, burnUt, target, closingTime);
                double arriveUt = burnUt + closingTime;
                // ★★ **两发都要提交**（2026-09-21 定案）：`DeltaVToInterceptAtTime` 解的是**两冲量转移**，
                //   而"两冲量必须提交执行，否则第二冲量永不执行 ⇒ 飞越"是台账条目 9 就登记过的结论。
                //   本库的实测把它钉死了：`到达冲量 ≈ Item1`（读数实测 100.62 vs 排入 91.5 同量级）
                //   ⇒ 只交第一发就等于"每到一个会合点都带着 ~100 m/s 穿过去" ⇒ 闭合曲线（Hill 相对轨道）
                //   ⇒ 2–12 km 极限环、收尾两级（要 CA ≤ 100 m）永远够不着。
                //
                // ⚠ **第二发必须带参考轨道**：那一刻船在**转移轨道**上，而不是点火前那条
                //   （执行器的旧靶口径 = 点火前轨道@UT + Δv ⇒ 对第二发方向与幅值双双错）。
                //   两发的参考轨道由**同一份初值**构造，与计划算的东西逐位一致：
                //     · 起飞发：`self@burnUt`（= 点火前轨道在节点处的状态，与执行器的旧口径**等价** ⇒ 行为不变）；
                //     · 刹车发：转移轨道（= 打完起飞发之后的那条，±dv1）。
                (V3 rTakeoff, V3 vTakeoff) = self.RightHandedStateVectorsAtUT(burnUt);
                // ⚠ `equatorialAxis` 一律**从 `self` 取**（批4 P4）：原先这里写死 `new V3(0, 0, 1)`
                //   （+z 适配层的产物）⇒ 极轴统一后它是**第二权威**，与实际赤道轴不一致时静默算错倾角。
                OrbitView refTakeoff = OrbitView.FromStateVectors(rTakeoff, vTakeoff, self.mu, burnUt,
                    self.equatorialAxis, self.bodyRadius);
                // 转移轨道 = 起飞时刻打完 dv1 = 计划真正会飞的那条
                OrbitView afterBurn = OrbitView.FromStateVectors(rTakeoff, vTakeoff + dv1, self.mu, burnUt,
                    self.equatorialAxis, self.bodyRadius);
                nodes.Add(new PlanNode(dv1, burnUt, PlanOwner.Guidance, 0.0, refTakeoff, true));
                nodes.Add(new PlanNode(dv2Arrive, arriveUt, PlanOwner.Guidance, 0.0, afterBurn, true));
                // ★ **"这一发真会到达吗"必须成为读数**（2026-09-21）：游戏里拦截级被连续选中 **63 次**
                //   （每发 ~90 m/s，Δv 合计 7397 m/s ≈ 政策估计 642 的 11 倍 ✗✗）⇒ 若不把这个测出来，
                //   下一次还是只能猜"是规划错还是执行错"。这里用**计划自己**（打完那一发的轨道）
                //   推进到到达时刻，与目标那一刻的位置量距离 ⇒ 一行读数定方向。
                double arrivalMiss = double.NaN;
                double arrivalMissIndep = double.NaN;   // 独立积分器复算（纯诊断，`IncludeDiagnostics` 控制）
                {
                    V3 rAfter = afterBurn.RightHandedBCIPositionAtUT(arriveUt);
                    V3 rTgtAtArrival = target.RightHandedBCIPositionAtUT(arriveUt);
                    arrivalMiss = (rAfter - rTgtAtArrival).magnitude;

                    // ★★ **独立复算**（2026-09-21 读数）：上面那次"**计划到达偏差 0 m**"是**用 Gooding 验 Gooding**
                    //   ——`rAfter` 就是 Gooding 的转移弧端点 ⇒ 它恒等于 `rf`、恒 0，是个**恒等循环**，
                    //   从来没验过"这条弧真的能到达"。这里用**另一种方法**（现成的一步两体推进器
                    //   `Shepperd`，与 Gooding 完全不同的算法）从同一个初值积分到同一时刻再量一次。
                    //   ⇒ 两个数应当一致；**不一致 ⇒ 计划本身是坏的**（而不是执行坏），这条能把两者分开。
                    //
                    // ⚠⚠ **2026-10-01：这条仪器此前是坏的（"km 级假红"）** —— 原先按 `round(closingTime/5)` 步
                    //   走 5 s 定步长 ⇒ 传播总时长与 `closingTime` 差最多 **2.5 s**；而目标在轨速度 ~5.6 km/s
                    //   ⇒ **1 s 的差就是 5.6 km**。实测对得上：`781 s 后到达` ⇒ 156 步 = 780 s（差 1 s）⇒
                    //   本读数报 **4744 m** —— 而那与计划好坏无关（**两个解算器实测都在 1e-8 m 级**，
                    //   见 `InterceptSolverComparisonTests`）。⇒ 修法：**5 s 步走整段 + 余数一步**（总时长严格 = `closingTime`）。
                    //   教训：我曾拿这台读数当"计划是坏的"的证据（据此还试过一次换解算器、已撤）⇒ **红字先怀疑仪器**。
                    //
                    // ★ 2026-10-19：这段**纯诊断**（结果只进 status）在每次拦截规划要跑 `closingTime/5` 步
                    //   （781 s ≈ 156 步），生产不需要 ⇒ 由 `Params.IncludeDiagnostics` 控制（默认关）。
                    if (p.IncludeDiagnostics)
                    {
                        V3 rr = rTakeoff, vv = vTakeoff + dv1;
                        const double dtProbe = 5.0;
                        int steps = (int)Math.Floor(closingTime / dtProbe);
                        for (int si = 0; si < steps; si++)
                        {
                            var (rn, vn) = MechJebLib.TwoBody.Shepperd.Solve(self.mu, dtProbe, rr, vv);
                            rr = rn; vv = vn;
                        }
                        double tail = closingTime - steps * dtProbe;   // 余数（< 5 s）：一步补齐 ⇒ 总时长恰为 closingTime
                        if (tail > 0.0)
                        {
                            var (rn, vn) = MechJebLib.TwoBody.Shepperd.Solve(self.mu, tail, rr, vv);
                            rr = rn; vv = vn;
                        }
                        arrivalMissIndep = (rr - rTgtAtArrival).magnitude;
                    }
                }
                status = $"很近（{dist:F0} m）：画拦截（{closingTime:F0} s 后到达，接近速度 {closingSpeed:F1} m/s）｜**计划到达偏差 {arrivalMiss:F0} m**｜**到达冲量 {dv2Arrive.magnitude:F2} m/s**（= 丢掉的那一发，`vf−transferVf`）｜**CA {caDist:F0} m @ T+{caUt - now:F0} s**";
                if (p.IncludeDiagnostics)
                    status += $"｜**独立积分到达偏差 {arrivalMissIndep:F0} m**";
                return Rung.Intercept;
            }

            // ── 6/7. 平面/圆度够好 ⇒ 霍曼拦截（相位圈数超限则先建调相轨道）──
            double relIncDeg = self.RelativeInclination(target) * 180.0 / Math.PI;
            if (relIncDeg < PlaneTolDeg && self.eccentricity < EccTol)
            {
                // ⚠ **分层**（2026-09-20 的教训）：**"能不能解"归计算器**（`DeltaVAndTimeForHohmannTransfer`
                //   自己按 `T_syn > 200×转移时长` 拦下会发散的几何、返回 NaN ✓），**"值不值得等"归阶梯**
                //   （用搜索返回的**实际**窗口圈数比 `maxPhasingOrbits` ✓）。
                //   ✗ 我先前在阶梯里拿 `T_syn` 当"窗口太远"的判据 —— **错**：`T_syn` 只是窗口时刻的**上界**，
                //     实际窗口可能就在眼前（实测：7000→8100 km 那档 `T_syn` 5.09 圈 > 上限 5 而被误拒，
                //     而搜索给出的窗口圈数 < 5 ✓）。**两处都要判 ⇒ 就变成两份权威、且其中一份是错的。**
                double period = self.period;
                double maxPhasing = Math.Max(p.MaxPhasingOrbits, 5.0);   // 上游：忽略不合理的小值
                // ★ 2026-10-21：**解析预判门** —— 明显超窗就别跑昂贵搜索（实测省 ~1.8 ms/次）。
                //   背景：PhasingOrbit 那档几何里，霍曼搜索会**成功收敛到一个 6.2 圈的窗口**
                //   （dt1=56,651 s vs 周期 9,126 s），然后被下面的 `phasingOrbits < maxPhasing` 拒掉
                //   ⇒ 那 1.8 ms 是**先算完再丢**。近圆共面两轨的窗口时刻有闭式解（见
                //   `EstimateHohmannWaitSeconds`），用它先判"最早窗口也远在上限之外"⇒ 直接建调相轨道。
                //   安全性：只在**超过上限 + `PhasingSkipMarginOrbits` 圈**时才跳过（模型误差远小于 1 圈），
                //   且"凡搜索会接受的几何，解析估计也必须在上限内"这条由 `HohmannAnalyticWindowTests` 钉住。
                double estWait = EstimateHohmannWaitSeconds(self, target, now);
                if (!double.IsNaN(estWait) && period > 0.0 && !double.IsNaN(period)
                    && AnalyticGateApplies(self.eccentricity, target.eccentricity))
                {
                    double estOrbits = estWait / period;
                    if (estOrbits > maxPhasing + PhasingSkipMarginOrbits)
                    {
                        PlanPhasingOrbit(self, target, now, maxPhasing, p, nodes, out status);
                        status = $"解析预估窗口 {estOrbits:F2} 圈 > 上限 {maxPhasing:F0}+{PhasingSkipMarginOrbits:F1}" +
                                 $" ⇒ 跳过霍曼搜索｜" + status;
                        return Rung.PhasingOrbit;
                    }
                    // ★★ 2026-10-21 第二刀：**窗口在上限内 ⇒ 用闭式霍曼直接出计划，不跑搜索**。
                    //   台架对照（240 组近圆共面，`HohmannFastPathComparisonTests`）：
                    //   Δv 相对差 **中位 0.00% / 最大 0.01%**、点火时刻差 **≤10 s**、快路从不更贵。
                    //   ⚠ 不能用"固定时刻 Lambert"代替：霍曼是 **180° 转移**，Lambert 在该处近乎奇异
                    //     （实测有些相位解出 17 km/s 的另一支）—— 闭式解才是这一档的正确工具。
                    //   边界保护：只在 `estOrbits < 上限 − 余量` 时走快路；落在边界带里仍跑搜索，
                    //   让"接不接受"这个决定保持原样（那一带一天也碰不到几次）。
                    if (estOrbits < maxPhasing - PhasingSkipMarginOrbits
                        && TryBuildClosedFormHohmann(self, target, now, estWait, estOrbits, maxPhasing,
                            nodes, out status))
                        return Rung.Hohmann;
                }
                string? hohmannFail = null;   // ⚠ 批5.C：霍曼级若被**异常**兜住，把原因带出去（原先整口吞掉）
                try
                {
                    (V3 hdv1, double hut1, V3 hdv2, double hut2) =
                        OrbitalManeuverCalculator.DeltaVAndTimeForHohmannTransfer(self, target, now, coplanar: false);
                    double phasingOrbits = period > 0.0 && !double.IsNaN(period) ? (hut1 - now) / period : double.MaxValue;
                    if (!double.IsNaN(hut1) && !double.IsInfinity(hut1) && hut1 > now
                        && !double.IsNaN(hut2) && !double.IsInfinity(hut2) && phasingOrbits < maxPhasing)
                    {
                        nodes.Add(new PlanNode(hdv1, hut1));
                        nodes.Add(new PlanNode(hdv2, hut2));
                        status = $"霍曼拦截：等 {phasingOrbits:F2} 圈后进入窗口（上限 {maxPhasing:F0} 圈）";
                        return Rung.Hohmann;
                    }
                }
                catch (Exception ex)
                {
                    // 退化几何仍可能抛（Lambert 对某些输入会拒绝）⇒ 落到调相支。
                    // ⚠ 批5.C（2026-09-29）：**原先把异常整口吞掉** ⇒ 日志里"Hohmann ⇄ PhasingOrbit 反复翻转"
                    //   到底是**按几何翻转**、还是**抛异常被静默兜住**，无从分辨（用户："测来测去看不出个长短"）。
                    //   ⇒ 现在把类型与消息**带进 status**（本文件是纯核心、没有 `Log` ⇒ 由调用方打出来）。
                    hohmannFail = ex.GetType().Name + ": " + ex.Message;
                }

                PlanPhasingOrbit(self, target, now, maxPhasing, p, nodes, out status);
                if (hohmannFail != null) status += "｜⚠霍曼级异常被兜住：" + hohmannFail;
                return Rung.PhasingOrbit;
            }

            // ── 8. 平面够好但轨道不圆 ⇒ 圆化（在离目标半径更近的那个拱点）──
            if (relIncDeg < PlaneTolDeg)
            {
                double aTgt = target.semiMajorAxis;
                bool atPe = self.eccentricity > 1.0
                    || Math.Abs(self.PeR - aTgt) < Math.Abs(self.ApR - aTgt);
                double ut = atPe
                    ? Math.Max(now, self.NextPeriapsisTime(now))
                    : self.NextApoapsisTime(now);
                nodes.Add(new PlanNode(OrbitalManeuverCalculator.DeltaVToCircularize(self, ut), ut));
                status = $"圆化：在{(atPe ? "近拱点" : "远拱点")}（PeR={self.PeR / 1000.0:F0} / ApR={self.ApR / 1000.0:F0} km，目标 a={aTgt / 1000.0:F0} km）";
                return Rung.Circularize;
            }

            // ── 9. 兜底：匹配平面（升/降交点二选一）──
            {
                double utAsc = self.TimeOfAscendingNode(target, now);
                double utDesc = self.TimeOfDescendingNode(target, now);
                bool ascending = self.eccentricity > 1.0
                    ? self.AscendingNodeExists(target)
                    : utAsc <= utDesc;
                V3 dv = ascending
                    ? OrbitalManeuverCalculator.DeltaVAndTimeToMatchPlanesAscending(self, target, now, out double burnUt)
                    : OrbitalManeuverCalculator.DeltaVAndTimeToMatchPlanesDescending(self, target, now, out burnUt);
                nodes.Add(new PlanNode(dv, burnUt));
                status = $"匹配平面：相对倾角 {relIncDeg:F3}° ⇒ 在{(ascending ? "升" : "降")}交点（T+{burnUt - now:F0} s）";
                return Rung.MatchPlanes;
            }
        }

        /// <summary>
        /// **近圆共面两轨的霍曼窗口等待时间（解析）**（2026-10-21）。
        ///
        /// 模型（调用点已用 `relInc < 0.05°`、`e < 0.05` 的同一道门）：
        /// · 转移半时长 `tof = π√((a1+a2)³/(8μ))`（Hohmann 转移 = 二体半周期）；
        /// · 到达时目标必须正好在"烧点对面"：`φ_rel + (n2−n1)·t_b + n2·tof = π (mod 2π)`；
        /// · `φ_rel` = 目标相对追击方的相位（沿轨道法向的有符号夹角），`n = √(μ/a³)`。
        /// 解出最小非负 `t_b`（秒）。
        ///
        /// **只用于"判定窗口是否远在上限之外"**（合理即可，不参与生成机动）：真正的 Δv/时刻仍由
        /// `DeltaVAndTimeForHohmannTransfer` 给出。台架 `HohmannAnalyticWindowTests` 钉住
        /// "凡搜索接受的几何，解析估计必须落在上限+余量之内"。
        /// 返回 NaN = 模型不适用（双曲/退化/两轨角速度相同）。
        /// </summary>
        internal static double EstimateHohmannWaitSeconds(OrbitView self, OrbitView target, double now)
        {
            double a1 = self.semiMajorAxis, a2 = target.semiMajorAxis, mu = self.mu;
            if (!(a1 > 0.0) || !(a2 > 0.0) || !(mu > 0.0)) return double.NaN;
            double n1 = Math.Sqrt(mu / (a1 * a1 * a1));
            double n2 = Math.Sqrt(mu / (a2 * a2 * a2));
            double dn = n2 - n1;
            if (Math.Abs(dn) < 1e-12) return double.NaN;   // 同轨/同步：窗口在无穷远
            (V3 rSelf, V3 _) = self.RightHandedStateVectorsAtUT(now);
            (V3 rTgt, V3 _) = target.RightHandedStateVectorsAtUT(now);
            V3 h = self.OrbitNormal();
            double phi = Math.Atan2(V3.Dot(h, V3.Cross(rSelf, rTgt)), V3.Dot(rSelf, rTgt));
            double tof = Math.PI * Math.Sqrt(Math.Pow(a1 + a2, 3.0) / (8.0 * mu));
            double t = (Math.PI - phi - n2 * tof) / dn;
            double per = 2.0 * Math.PI / Math.Abs(dn);
            t -= Math.Floor(t / per) * per;                // 归一到 [0, per)
            return t;
        }

        /// <summary>
        /// **解析门是否适用**（2026-10-21 正确性保险，交接 §11.36-N 风险②）：
        /// 两轨偏心都**严格小于** <see cref="AnalyticMaxEccentricity" /> 才准用解析预判/闭式霍曼。
        ///
        /// 抽成谓词的唯一目的 = **边界可测**：以前这个判断内联在 <see cref="MakePlan" /> 的 `if` 里，
        /// 只有"构造出刚好落在边界附近的整条几何"才能验到，等于验不到。
        /// 语义与内联版逐字等价：
        /// · `e = 0.005` **本身不进**（严格 `&lt;`）⇒ 落到原搜索，行为不变；
        /// · `NaN`（退化轨道）⇒ `NaN &lt; x` 为 false ⇒ **不进**，同样退回搜索（安全方向）。
        /// </summary>
        internal static bool AnalyticGateApplies(double selfEccentricity, double targetEccentricity) =>
            selfEccentricity < AnalyticMaxEccentricity && targetEccentricity < AnalyticMaxEccentricity;

        /// <summary>
        /// 两条半径向量在**同一轨道面内**的夹角（度）= 需要收掉的相位差（取 0~180 的短弧）。
        /// 纯几何、无搜索 —— 这也是它能待在"每步都算得起"的那一级的原因。
        /// </summary>
        internal static double PhaseDifferenceDeg(OrbitView self, OrbitView target, double ut)
        {
            (V3 r1, V3 _) = self.RightHandedStateVectorsAtUT(ut);
            (V3 r2, V3 _) = target.RightHandedStateVectorsAtUT(ut);
            double d = V3.Dot(r1.normalized, r2.normalized);
            d = Math.Max(-1.0, Math.Min(1.0, d));
            return Math.Acos(d) * 180.0 / Math.PI;
        }

        /// <summary>
        /// 上游第 3a/4 级的 UT 修正：让"在最近点停住"**正好停在期望距离**上。
        /// 上游原式：`UT -= √|d² − 接近距离²| / 接近速度`；进场快（&gt; 10 m/s）再提前 1 s 免得撞上去。
        /// </summary>
        private static double AdjustClosestApproachUt(OrbitView self, OrbitView target, double caUt, double desired)
        {
            double approachDistance = self.Separation(target, caUt);
            (V3 _, V3 vs) = self.RightHandedStateVectorsAtUT(caUt);
            (V3 _, V3 vt) = target.RightHandedStateVectorsAtUT(caUt);
            double approachSpeed = (vt - vs).magnitude;
            if (approachSpeed <= 0.0) return caUt;

            double ut = caUt;
            if (approachDistance < desired)
                ut -= Math.Sqrt(Math.Abs(desired * desired - approachDistance * approachDistance)) / approachSpeed;
            if (approachSpeed > HotApproachSpeed) ut -= HotLeadSeconds;
            return ut;
        }

        /// <summary>调相轨道的**半径倍率**：`(1 + 1.25/max(N,5))^{2/3}`（上游原式，N=5 时 ≈1.1604）。
        /// 抬轨那支用 `aTgt × ratio`、压轨那支用 `aTgt / ratio`。
        /// ⚠ 只在本文件内用（2026-09-29：曾被提为 public 供"近场夹具"构造初相，那个夹具已按用户意见
        ///   换成**异轨档 + 用阶梯自己当 oracle 扫相位** ⇒ 外部零调用者 ⇒ 收回 private）。</summary>
        private static double PhasingAxisRatio(double maxPhasingOrbits) =>
            Math.Pow(1.0 + 1.25 / Math.Max(maxPhasingOrbits, 5.0), 2.0 / 3.0);

        /// <summary>上游第 7 级：相位太慢就建一条**调相轨道**（改近/远点 + 在对面拱点圆化，或直接圆化）。
        /// ⚠ **2026-09-21：半径公式已回到上游原样** —— 我曾叠一层"按需要几圈"的线性式（A4 表引出，
        /// 动机是"5° 小相位差不该花 383 m/s"），但它**并没有更快**、只是多一个概念 ⇒ 删（见函数内注释）。
        /// </summary>
        private static void PlanPhasingOrbit(OrbitView self, OrbitView target, double now, double maxPhasing,
            Params p, List<PlanNode> nodes, out string status)
        {
            // ★★ **回到上游的形式**（2026-09-21 第二次"不要小聪明"审计）：上游只有
            //   `axisRatio = (1 + 1.25/N)^{2/3}`，**N = max(maxPhasingOrbits, 5)** —— **与相位差无关** ✓。
            //   （我曾在它上面叠一层"按需要几圈"的线性式并取更激进者 ✗ —— 实测它**并没有更快**，
            //    只是多了一个概念 ✗ ⇒ 删。）上游在 N=5 时 `axisRatio = 1.16` ⇒ **每圈收 ~86°**，几圈就完事 ✓。
            //   ⚠ A4 表当年测到"5° 小相位差也要 383 m/s" ⇒ 归入**"总修缮"清单**（先照 MJ 跑通）。
            double axisRatio = PhasingAxisRatio(maxPhasing);
            double aTgt = target.semiMajorAxis;
            double lowRadius = aTgt / axisRatio;
            double highRadius = aTgt * axisRatio;

            bool useLow = lowRadius > p.MinRadius && self.semiMajorAxis < aTgt;
            double phasingRadius = useLow ? lowRadius : highRadius;

            if (self.ApR < phasingRadius)
            {
                double ut1 = now + PhasingBurnLead;
                V3 dv1 = OrbitalManeuverCalculator.DeltaVToChangeApoapsis(self, ut1, phasingRadius);
                (V3 r1, V3 v1) = self.RightHandedStateVectorsAtUT(ut1);
                OrbitView transfer = OrbitView.FromStateVectors(r1, v1 + dv1, self.mu, ut1,
                    self.equatorialAxis, self.bodyRadius);
                double ut2 = transfer.NextApoapsisTime(ut1);
                nodes.Add(new PlanNode(dv1, ut1));
                nodes.Add(new PlanNode(OrbitalManeuverCalculator.DeltaVToCircularize(transfer, ut2), ut2));
                status = $"建调相轨道：抬远点到 {phasingRadius / 1000.0:F0} km 后圆化（相位圈数超过上限 {maxPhasing:F0}）";
            }
            else if (self.PeR > phasingRadius)
            {
                double ut1 = now + PhasingBurnLead;
                V3 dv1 = OrbitalManeuverCalculator.DeltaVToChangePeriapsis(self, ut1, phasingRadius);
                (V3 r1, V3 v1) = self.RightHandedStateVectorsAtUT(ut1);
                OrbitView transfer = OrbitView.FromStateVectors(r1, v1 + dv1, self.mu, ut1,
                    self.equatorialAxis, self.bodyRadius);
                double ut2 = transfer.NextPeriapsisTime(ut1);
                nodes.Add(new PlanNode(dv1, ut1));
                nodes.Add(new PlanNode(OrbitalManeuverCalculator.DeltaVToCircularize(transfer, ut2), ut2));
                status = $"建调相轨道：压近点到 {phasingRadius / 1000.0:F0} km 后圆化（相位圈数超过上限 {maxPhasing:F0}）";
            }
            else
            {
                double ut = self.NextTimeOfRadius(now, phasingRadius);
                nodes.Add(new PlanNode(OrbitalManeuverCalculator.DeltaVToCircularize(self, ut), ut));
                status = $"建调相轨道：在 {phasingRadius / 1000.0:F0} km 处直接圆化（相位圈数超过上限 {maxPhasing:F0}）";
            }
        }
    }
}
