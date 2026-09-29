using System;
using System.Text;
using MechJebLib.Functions;
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **执行器台架**（2026-09-19）：把游戏侧的**执行器政策**搬进离线测试，与 `BurnArc` 前向模型并排比。
    ///
    /// ## 为什么要有它（教训）
    /// "执行器 vs 模型"的分歧原来只能靠**游戏内探针**逐轮查（一轮 = 开一次游戏），而且那轮的
    /// "模型预测"曲线本身是坏的（见 Q3）⇒ 白烧了几轮。政策是**确定的、可枚举的**：
    ///   ① 居中点火（`ignition = node − t_b/2`）；② 物理步 = 2 s；③ `a = F/m(t)`（燃耗扣质量，a 自然增大）；
    ///   ④ 方向 = 计划的 RTN 分量**在当前 RTN 里取**（游戏内 = `OrbitMath.GetDeltaVSystem(pos, vel, rad, tan, nor)`），
    ///      **每步**重取（2026-09-19 起；旧版按 2° 节拍刷新 = <see cref="Refresh.Policy"/> 对照组）；⑤ 单步 Δv 被 `remainingBurnDV` 截断
    ///      （截断时工质同比例少扣）。
    /// 另加一条**网络选择**政策：只烧**已对准的主网**（`ve=4000`、`ṁ=140`、`F=560 kN`），
    /// RCS 网（`ve=1500`、`ṁ=20`）只在未对准时接管 ⇒ **合并 ve=3687.5 不是执行器的 ve**。
    ///
    /// ## 判的三件事
    /// Q1 **刷新量化的代价**：冻结方向（B2.3 之前）/ 2° 节拍（现状）/ 每步连续 —— 终态 `e` 差多少？
    /// Q2 **模型对不对**：`BurnArc.TryPredict`（喂**点火前**轨道 —— 这是它的契约）应当与"连续方向执行器"一致。
    /// Q3 **喂错基准的后果**：把**当前（已烧过的）轨道**喂给 `TryPredictPartial`（游戏里的调用方式）
    ///    ⇒ 预测随**已烧累计 Δv** 漂移（这正是"模型与实测差 20 倍"的真因候选）。
    ///
    /// 场景取**游戏实测那一轮**的原值（e=0.3 起于 r=7000 km、节点在 300 s、`t_b`=98 s、
    /// 过推档 F=560 kN/ṁ=140、m0=74995.1 kg）⇒ 台架读数可与日志**逐点对照**。
    /// </summary>
    public class BurnArcExecutorBench
    {
        private readonly ITestOutputHelper _out;

        public BurnArcExecutorBench(ITestOutputHelper output) => _out = output;

        private static readonly double Mu = OrbitMath.GM;      // 2e14（与游戏一致）
        private const double R0 = 7_000_000.0;

        // ── 游戏侧执行器政策（DebugShipSpawner 过推档 + 计划读数）──
        private const double Thrust = 560_000.0;               // 主网 F = ve 4000 × ṁ 140
        private const double MassFlow = 140.0;                 // 主网 ṁ（kg/s）
        private const double IgnitionMass = 74_995.1;          // 点火时群质量（日志原值）
        private const double StepSeconds = 2.0;                // 物理步
        private const double BurnSeconds = 98.0;               // 计划燃烧时长（日志原值）
        private const double NodeSeconds = 300.0;              // 布置 → 节点

        private enum Refresh { Frozen, Policy, Continuous }

        /// <summary>**分量在哪个时刻的位姿上分解**（= 方向律的"锚点"）。
        /// `Node` = 计划自带的口径（`OrbitAdapter.TryQueuePlan` 用**节点时刻**位姿分解 ⇒
        /// `PlannedManeuver.radialDV/…` 就是节点帧分量）；`Ignition` = 点火帧（2026-09-19 之前与之后
        /// 游戏里实际发生的：`StartBurn` 拿到世界向量后**在点火帧重新分解**）。两者在节点处相差
        /// ω·(49 s) ≈ 2.44°，**这正是 2026-09-19 实测的「模型方向 vs 执行方向 2.23–2.36°」**。</summary>
        private enum Epoch { Node, Ignition }

        /// <summary>
        /// **已删除的旧政策**（`BurnArcPolicy.SweepPerRefreshRad`，2026-09-19 删）—— 只为**对照组**保留：
        /// 它复现了修复前的游戏实测（e=3.76e-3、不变量 0.9897/0.9888 与旧日志逐位吻合）⇒ 证明"量化 = 残差源"
        /// 这条结论有据。**生产代码里不要再出现这个节拍**（游戏现在每步重取方向 = <see cref="Refresh.Continuous"/>）。
        /// </summary>
        private const double OldPolicySweepRad = 0.035;

        private static bool OldPolicyShouldRefresh(double omega, double lastRefreshTick, double tick)
        {
            if (!(omega > 0.0)) return false;
            if (double.IsNegativeInfinity(lastRefreshTick) || tick < lastRefreshTick) return true;
            return (tick - lastRefreshTick) * omega >= OldPolicySweepRad;
        }

        /// <summary>点火前的轨道（同 `BurnArcTests.Eccentric`：近心点 r=R0、e=0.3、顺行赤道）。</summary>
        private static OrbitView PreBurn()
        {
            double vc = Math.Sqrt(Mu / R0);
            return OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, Math.Sqrt(1.3) * vc, 0),
                Mu, 0.0, new V3(0, 0, 1), R0 * 0.2);
        }

        /// <summary>计划的 Δv 及其在**节点** RTN 上的分量（= 游戏里显示/存储的三个数）。</summary>
        private static (V3 dv, double rad, double tan, double nor) Plan(OrbitView o, double nodeUt)
        {
            V3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, nodeUt);
            return (dv, Components(o, nodeUt, dv).rad, Components(o, nodeUt, dv).tan, Components(o, nodeUt, dv).nor);
        }

        /// <summary>把同一个世界 Δv 分解到 <paramref name="ut"/> 时刻的 RTN。</summary>
        private static (double rad, double tan, double nor) Components(OrbitView o, double ut, V3 dv)
        {
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);
            V3 rhat = r.normalized;
            V3 hhat = V3.Cross(r, v).normalized;
            V3 that = V3.Cross(hhat, rhat);
            return (V3.Dot(dv, rhat), V3.Dot(dv, that), V3.Dot(dv, hhat));
        }

        /// <summary>方向律：**直接调用游戏的唯一算式**（`BurnStep.DirectionInRTN`），不再手写第二份 ✗。
        /// 边界处只做 V3↔DVector3 的零换系转换（两者都在适配层系）。</summary>
        private static V3 DirAt(V3 r, V3 v, double rad, double tan, double nor)
        {
            DVector3 d = BurnStep.DirectionInRTN(
                new DVector3(r.x, r.y, r.z), new DVector3(v.x, v.y, v.z), rad, tan, nor);
            return new V3(d.x, d.y, d.z);
        }

        private readonly struct Exec
        {
            public readonly OrbitView final;
            public readonly double appliedDv, propellant, massEnd;
            public readonly V3 dvVecSum;          // 交付冲量的**矢量和**（终端真值的等价物）
            public readonly double[] sampleR, sampleV, dirLagDeg;
            public readonly V3 dir0, dirEnd;      // 诊断：点火时的方向 / 终了时"连续"应在的方向
            public Exec(OrbitView final, double appliedDv, double propellant, double massEnd,
                double[] sampleR, double[] sampleV, double[] dirLagDeg, V3 dir0, V3 dirEnd, V3 dvVecSum)
            {
                this.final = final; this.appliedDv = appliedDv; this.propellant = propellant;
                this.massEnd = massEnd; this.sampleR = sampleR; this.sampleV = sampleV;
                this.dirLagDeg = dirLagDeg; this.dir0 = dir0; this.dirEnd = dirEnd;
                this.dvVecSum = dvVecSum;
            }
        }

        /// <summary>
        /// 跑一遍执行器。<paramref name="untilSeconds"/> &lt; `BurnSeconds` 时只烧到那一刻（用于 Q3 取"此刻的轨道"）。
        /// </summary>
        private static Exec Execute(OrbitView o, double nodeUt, double untilSeconds, Refresh refresh, int samples,
            Epoch epoch = Epoch.Node, int idleStartSteps = 0)
        {
            (V3 dv, _, _, _) = Plan(o, nodeUt);
            double startUt = nodeUt - 0.5 * BurnSeconds;
            (double rad, double tan, double nor) = Components(o, epoch == Epoch.Node ? nodeUt : startUt, dv);
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(startUt);

            double m = IgnitionMass, remaining = dv.magnitude, applied = 0.0, propellant = 0.0;
            V3 dvSum = V3.zero;   // 交付冲量的矢量和（终端真值的等价物）
            double lastRefresh = double.NegativeInfinity;
            V3 dir = DirAt(r, v, rad, tan, nor);      // StartBurn 的初始分解（点火时刻）
            V3 Di0 = dir;
            var sR = new double[samples + 1];
            var sV = new double[samples + 1];
            var sLag = new double[samples + 1];

            double span = Math.Min(BurnSeconds, untilSeconds);
            int n = (int)Math.Round(span / StepSeconds);
            for (int i = 0; i < n; i++)
            {
                // ★ 起步空转（游戏实测：前 ~1.9 步 + 中段 ~0.9 步完全空转 = 不推力不耗料，
                //   但**时间照样流逝** ⇒ 燃烧总时长被拉到 106s 而工质只够 98.8s ⇒ Δv 质心后移）
                if (i < idleStartSteps)
                {
                    (r, v) = MechJebLib.TwoBody.Shepperd.Solve(Mu, StepSeconds, r, v);
                    continue;
                }
                if (refresh == Refresh.Continuous)
                {
                    dir = DirAt(r, v, rad, tan, nor);
                }
                else if (refresh == Refresh.Policy)
                {
                    double h = V3.Cross(r, v).magnitude;
                    double omega = h / (r.magnitude * r.magnitude);
                    double tick = startUt + i * StepSeconds;
                    if (OldPolicyShouldRefresh(omega, lastRefresh, tick))
                    {
                        dir = DirAt(r, v, rad, tan, nor);
                        lastRefresh = tick;
                    }
                }

                double a = Thrust / m;
                double dvFull = a * StepSeconds;
                double burnDv = Math.Min(dvFull, remaining);
                if (burnDv <= 0.0) break;

                v += dir * burnDv;                        // 执行器：沿**指令方向**、幅值已按指令缩放
                dvSum += dir * burnDv;                    // 终端真值等价物：交付冲量的矢量和
                double share = dvFull > 0.0 ? burnDv / dvFull : 0.0;
                propellant += MassFlow * StepSeconds * share;
                m -= MassFlow * StepSeconds * share;
                remaining -= burnDv;
                applied += burnDv;

                // ★ 每步的**重力积分**不能省（游戏里 = 物理步；漏了 ⇒ 98 s 位置差 ½gt²≈19 km ✗
                //   2026-09-19：第一版台架就漏了这条，导致 a 比游戏小 13 km ⇒ 台架自己的 bug）
                (r, v) = MechJebLib.TwoBody.Shepperd.Solve(Mu, StepSeconds, r, v);

                int slot = (int)Math.Round((i + 1) * samples / (double)n);
                if (slot >= 1 && slot <= samples && sR[slot] == 0.0)
                {
                    sR[slot] = r.magnitude;
                    sV[slot] = v.magnitude;
                    // 与游戏同读数：执行中的方向 vs 模型该步"连续"应在的方向（= 陈旧角）
                    sLag[slot] = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                        V3.Dot(dir, DirAt(r, v, rad, tan, nor))))) * 180.0 / Math.PI;
                }
            }

            return new Exec(OrbitView.FromStateVectors(r, v, Mu, startUt + n * StepSeconds, new V3(0, 0, 1), R0 * 0.2),
                applied, propellant, m, sR, sV, sLag, dir0: Di0, dirEnd: DirAt(r, v, rad, tan, nor), dvVecSum: dvSum);
        }

        /// <summary>
        /// **命令恒定加速度的执行器**（用户裁定后的游戏政策）：`a_cmd = F/m₀`（点火质量 ⇒ 可行域里最紧的恒定值），
        /// 居中窗口 = `t_b = Δv/a_cmd`，每步按它给 Δv，油门份额 = `a_cmd / (F/m(t))`（比例油门 ⇒ 工质总量不变）。
        /// 与 `BurnCore.EstimateOver.BurnSeconds` **同式**（台架是它的复刻，不是第二权威）。
        /// </summary>
        private static Exec ExecuteConstA(OrbitView o, double nodeUt, V3 dv, int samples,
            double ignitionShiftSeconds = 0.0, bool gameLikeFirstWindow = false,
            int anomalySteps = 4, double anomalyDeg = 7.2, double anomalyMag = 0.946,
            double rollDeg = 0.0, bool applyLagOneStep = false, double dirRateMul = 1.0)
        {
            double tBurn = dv.magnitude * IgnitionMass / Thrust;      // = Δv / a_cmd
            double aCmd = dv.magnitude / tBurn;                       // = F/m₀
            double startUt = nodeUt - 0.5 * tBurn + ignitionShiftSeconds;
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(startUt);
            (double rad, double tan, double nor) = Components(o, nodeUt, dv);

            double m = IgnitionMass, remaining = dv.magnitude, applied = 0.0, propellant = 0.0;
            V3 dvSum = V3.zero;   // 交付冲量的矢量和（终端真值的等价物）
            var sR = new double[samples + 1]; var sV = new double[samples + 1]; var sLag = new double[samples + 1];
            V3 dir = DirAt(r, v, rad, tan, nor);
            int n = (int)Math.Round(tBurn / StepSeconds) + 1;         // +1：末步截断
            // ⚠ **滞后开关以前是空的**（声明了、循环里没人用）⇒ 我据此写过"滞后/时机零效应"的结论 ✗
            //   （2026-09-20 更正：那是从一台**不做事的开关**读出来的）。现在按游戏的真实时序实现：
            //   冲量用的是**上一拍**算出来的方向（因为游戏里方向在写 `pendingImpulse` 的那一拍算、
            //   而冲量要等下一拍物理步首才并入速度）。
            V3 dirPrev = DirAt(r, v, rad, tan, nor);
            for (int i = 0; i < n; i++)
            {
                dir = DirAt(r, v, rad, tan, nor);
                if (rollDeg != 0.0)
                {
                    // ★ 假设：整段燃烧方向绕轨道法向滚转 δ°（面内误差的唯一形态 ⇒ i/LAN 不变 ✓）
                    V3 nh = V3.Cross(r, v).normalized;
                    V3 tp = V3.Cross(nh, dir);
                    double rr = rollDeg * Math.PI / 180.0;
                    dir = (dir * Math.Cos(rr) + tp * Math.Sin(rr)).normalized;
                }
                double aFull = Thrust / m;                            // 当前质量下的全油门
                double dvFull = aFull * StepSeconds;
                double dvCmd = aCmd * StepSeconds;
                double burnDv = Math.Min(dvCmd, remaining);
                if (burnDv <= 0.0) break;

                // ★ 游戏实测的首段异常（**由日志量得，不是猜的**）：首 10 s（4 步）只交付 0.946 倍、
                //   净方向偏 7.2°（其后每段都是 1.000 / 0.2°）。用来判"首段异常能否解释游戏那 ~1.1e-3"。
                V3 dirUse = applyLagOneStep ? dirPrev : dir;
                double dvUse = burnDv;
                if (gameLikeFirstWindow && i < anomalySteps)
                {
                    dvUse = burnDv * anomalyMag;
                    V3 nrm = V3.Cross(r, v).normalized;               // 绕轨道法向把方向偏 θ°
                    double a7 = anomalyDeg * Math.PI / 180.0;
                    V3 t1 = V3.Cross(nrm, dir);
                    dirUse = (dir * Math.Cos(a7) + t1 * Math.Sin(a7)).normalized;
                }
                v += dirUse * dvUse;
                dvSum += dirUse * dvUse;                  // 终端真值等价物
                double share = dvFull > 0.0 ? dvUse / dvFull : 0.0;   // = aCmd/aFull = m/m₀
                propellant += MassFlow * StepSeconds * share;
                m -= MassFlow * StepSeconds * share;
                remaining -= burnDv; applied += dvUse;
                dirPrev = dir;                                 // 本拍算的方向 ⇒ 下一拍用（滞后开关）
                (r, v) = MechJebLib.TwoBody.Shepperd.Solve(Mu, StepSeconds, r, v);

                int slot = (int)Math.Round((i + 1) * samples / (double)n);
                if (slot >= 1 && slot <= samples && sR[slot] == 0.0)
                {
                    sR[slot] = r.magnitude; sV[slot] = v.magnitude;
                    sLag[slot] = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                        V3.Dot(dir, DirAt(r, v, rad, tan, nor))))) * 180.0 / Math.PI;
                }
            }
            return new Exec(OrbitView.FromStateVectors(r, v, Mu, startUt + n * StepSeconds, new V3(0, 0, 1), R0 * 0.2),
                applied, propellant, m, sR, sV, sLag, dir, DirAt(r, v, rad, tan, nor), dvSum);
        }

        /// <summary>模型无关的不变量：把点火前状态开普勒推进到节点、加计划 Δv，与"实测终态"比 ε 与 |h|。</summary>
        private static (double epsRatio, double hRatio) Invariants(OrbitView o, double nodeUt, V3 dv, OrbitView final)
        {
            (V3 rN, V3 vN) = o.RightHandedStateVectorsAtUT(nodeUt);
            V3 vAfter = vN + dv;
            (V3 rB, V3 vB) = o.RightHandedStateVectorsAtUT(nodeUt - 0.5 * BurnSeconds);
            (V3 rE, V3 vE) = final.RightHandedStateVectorsAtUT(final.epoch);
            _ = rB;

            double epsPre = vN.sqrMagnitude / 2.0 - Mu / rN.magnitude;
            double epsExp = vAfter.sqrMagnitude / 2.0 - Mu / rN.magnitude;
            double epsNow = vE.sqrMagnitude / 2.0 - Mu / rE.magnitude;

            V3 hPre = V3.Cross(rN, vN);
            V3 hExp = V3.Cross(rN, vAfter);
            V3 hNow = V3.Cross(rE, vE);

            double dE = epsNow - epsPre, xE = epsExp - epsPre;
            double dH = (hNow - hPre).magnitude, xH = (hExp - hPre).magnitude;
            return (xE != 0.0 ? dE / xE : double.NaN, xH != 0.0 ? dH / xH : double.NaN);
        }

        [Fact]
        public void Bench_RefreshQuantization_IsResidualSource()
        {
            var o = PreBurn();
            double nodeUt = NodeSeconds;
            (V3 dv, double rad, double tan, double nor) = Plan(o, nodeUt);
            var sb = new StringBuilder();

            (V3 p0, V3 vel0) = o.RightHandedStateVectorsAtUT(0.0);
            double h0 = V3.Cross(p0, vel0).magnitude;
            double omega = h0 / (p0.magnitude * p0.magnitude);   // = `BurnArcPolicy.OmegaFromState`（已删）
            sb.Append("══ 执行器台架（μ=").Append(Mu.ToString("E2")).Append("，R0=7000 km，e=0.3，t_b=")
              .Append(BurnSeconds.ToString("F0")).Append("s，2 s 步）══");
            sb.Append("\n计划 |Δv| = ").Append(dv.magnitude.ToString("F2")).Append(" m/s　RTN = ")
              .Append(rad.ToString("F1")).Append(" / ").Append(tan.ToString("F1")).Append(" / ").Append(nor.ToString("F1"))
              .Append("　【自证：游戏日志 = 808.09　−361.3 / −722.8 / 0.0】");
            sb.Append("\nω = ").Append(omega.ToString("E3")).Append(" rad/s ⇒ 2° 节拍刷新周期 = ")
              .Append((OldPolicySweepRad / omega).ToString("F1")).Append(" s（t_b 内只刷 ")
              .Append((BurnSeconds * omega / OldPolicySweepRad).ToString("F1")).Append(" 次）【旧政策；现为每步重取】");

            var frozen = Execute(o, nodeUt, BurnSeconds, Refresh.Frozen, 9);
            var policy = Execute(o, nodeUt, BurnSeconds, Refresh.Policy, 9);
            var cont = Execute(o, nodeUt, BurnSeconds, Refresh.Continuous, 9);
            // ★ 第二次实测（2026-09-19 修完刷新节拍后）：游戏 = **每步刷新 + 点火帧锚点** ⇒ 单独建模
            var contIgn = Execute(o, nodeUt, BurnSeconds, Refresh.Continuous, 9, Epoch.Ignition);
            // ★ 游戏实测那一次的**忠实复刻**：节点帧锚点 + 每步 + 起步空转 2 步（质量序列反推 1.86 步）
            var gameLike = Execute(o, nodeUt, BurnSeconds, Refresh.Continuous, 9, Epoch.Node, idleStartSteps: 2);
            // ★ 现行政策（用户裁定）：命令恒定加速度 ⇒ 应该走到"均匀摊开"那一档
            var constA = ExecuteConstA(o, nodeUt, dv, 9);
            // 复刻游戏实测（含首段异常）⇒ 检验"游戏那 ~1.1e-3 是不是首段异常造成的"
            var gameLikeC = ExecuteConstA(o, nodeUt, dv, 9, 0.0, gameLikeFirstWindow: true);
            var invGameLikeC = Invariants(o, nodeUt, dv, gameLikeC.final);
            // ★ 用 **e 与不变量两个读数同时** 卡"首段异常的内部结构"（n 步 × θ°），而不是只对 e
            var grid = new System.Text.StringBuilder();
            grid.Append("\n  ── 首段异常结构网格（目标：e≈1.04~1.26e-3 且 不变量≈0.9956~0.9966）──");
            foreach (int n in new[] { 1, 2, 4, 6 })
                foreach (double th in new[] { 0.0, 5.0, 10.0, 30.0 })
                {
                    var ex = ExecuteConstA(o, nodeUt, dv, 0, 0.0, true, n, th, 0.946);
                    var iv = Invariants(o, nodeUt, dv, ex.final);
                    bool hitE = ex.final.eccentricity > 1.0e-3 && ex.final.eccentricity < 1.3e-3;
                    bool hitI = iv.epsRatio > 0.9950 && iv.epsRatio < 0.9970;
                    grid.Append("\n    n=").Append(n).Append(" θ=").Append(th.ToString("F0")).Append("°：e=")
                        .Append(ex.final.eccentricity.ToString("E3")).Append(" 不变量=")
                        .Append(iv.epsRatio.ToString("F4")).Append(hitE && hitI ? "  ★两中" : (hitE ? "  (仅e中)" : ""));
                }
            grid.Append("\n  ⇒ 命中的结构 = 首段异常的真身（再用它反推机制）");
            // ★ **点火对齐敏感度**：游戏里实际点火落在"首个 ≥ 目标 tick 的物理步"上，会有 ±1 步（±2 s）抖动。
            //   在拿"e 变好/变差"下结论之前，先量这个抖动本身能把 e 推到多大 —— 否则追的是噪声。
            var alignSens = new (double shift, double e)[]
            {
                (-4.0, ExecuteConstA(o, nodeUt, dv, 0, -4.0).final.eccentricity),
                (-2.0, ExecuteConstA(o, nodeUt, dv, 0, -2.0).final.eccentricity),
                ( 0.0, ExecuteConstA(o, nodeUt, dv, 0,  0.0).final.eccentricity),
                ( 2.0, ExecuteConstA(o, nodeUt, dv, 0,  2.0).final.eccentricity),
                ( 4.0, ExecuteConstA(o, nodeUt, dv, 0,  4.0).final.eccentricity),
            };
            // ★ 计划一致性：**排队时刻**算的计划 vs **活重解时刻**算的计划（同一节点）。
            //   游戏侧：判据的期望值用排队时的向量、执行器烧的是重解后的向量 ⇒ 两者若不同向，就是那 0.2°。
            var planSb = new System.Text.StringBuilder();
            {
                (V3 dvStage, _, _, _) = Plan(o, nodeUt);                     // 从 staging 轨道
                var ovResolve = OrbitView.FromStateVectors(
                    o.RightHandedStateVectorsAtUT(nodeUt - 300.0).pos,
                    o.RightHandedStateVectorsAtUT(nodeUt - 300.0).vel,
                    Mu, nodeUt - 300.0, new V3(0, 0, 1), R0 * 0.2);
                (V3 dvResolve, _, _, _) = Plan(ovResolve, nodeUt);           // 从重解时刻的轨道（同一节点）
                double ang = Math.Acos(Math.Max(-1.0, Math.Min(1.0, V3.Dot(dvStage.normalized, dvResolve.normalized))))
                             * 180.0 / Math.PI;
                planSb.Append("\n  ── 计划一致性（同一节点、两个时刻）──")
                       .Append("\n    排队时刻 |Δv|=").Append(dvStage.magnitude.ToString("F3"))
                       .Append("　重解时刻 |Δv|=").Append(dvResolve.magnitude.ToString("F3"))
                       .Append("（差 ").Append(((dvResolve.magnitude - dvStage.magnitude) / dvStage.magnitude * 100.0).ToString("F4")).Append("%）")
                       .Append("\n    两者夹角 = ").Append(ang.ToString("F4")).Append("°　【游戏那 0.2° 偏置若源于此，这里应 ~0.2°】");
            }
            // ★ **通道扫描**：R/T 分量各加一个小偏置（m/s），看哪条通道能对上游戏的三个读数
            //    （滚转 = R/T 按 −R·δ / +T·δ 的特定混合比例；纯 R、纯 T 各有签名）
            var chSb = new System.Text.StringBuilder();
            chSb.Append("\n  ── 通道偏置（游戏：0.9959/0.9956、e=1.257e-3、Δa=+8.46km）──");
            foreach (var (nm, dR, dT) in new[] { ("R+10", 10.0, 0.0), ("R-10", -10.0, 0.0),
                                                 ("T+5", 0.0, 5.0), ("T-5", 0.0, -5.0),
                                                 ("R-7/T+4", -7.0, 4.0) })
            {
                // ⚠ 必须在**节点的 RTN 基**上加偏置（不能直接改适配层分量 ✗：那是惯性系）
                (V3 rN3, V3 vN3) = o.RightHandedStateVectorsAtUT(nodeUt);
                V3 rh3 = rN3.normalized, hh3 = V3.Cross(rN3, vN3).normalized, th3 = V3.Cross(hh3, rh3);
                V3 dvBias = dv + rh3 * dR + th3 * dT;
                var ec2 = ExecuteConstA(o, nodeUt, dvBias, 0, 0.0);
                var ic2 = Invariants(o, nodeUt, dv, ec2.final);
                chSb.Append("\n    ").Append(nm).Append("：Δε=").Append(ic2.epsRatio.ToString("F4"))
                    .Append(" |Δh|=").Append(ic2.hRatio.ToString("F4"))
                    .Append(" e=").Append(ec2.final.eccentricity.ToString("E3"))
                    .Append(" Δa=").Append(((ec2.final.semiMajorAxis - 7055e3) / 1000.0).ToString("F1")).Append("km");
            }
            // ★★ **四轨道对照**（2026-09-19）：与游戏内「矩阵对照」逐格比 ——
            //    游戏（A 基准）：7000→e=1.257e-3/Δa=+8.46｜6600→2.951e-3/+19.42｜8500→3.797e-3/+32.21｜7800→1.984e-3/+15.49
            //    ⇒ 若台架逐格对上 ⇒ 残差就是"有限燃烧"本身（B2.2 弧补偿是正解 ✓）；
            //      对不上 ⇒ 差异的形状 = 游戏多出来的那一项。
            {
                var orbSb = new System.Text.StringBuilder();
                orbSb.Append("\n  ── 四轨道（台架 vs 游戏 A 基准）──");
                foreach (double r0v in new[] { 7.0e6, 6.6e6, 8.5e6, 7.8e6 })
                {
                    double vcv = Math.Sqrt(Mu / r0v);
                    var ov4 = OrbitView.FromStateVectors(new V3(r0v, 0, 0),
                        new V3(0, Math.Sqrt(1.3) * vcv, 0), Mu, 0.0, new V3(0, 0, 1), r0v * 0.2);
                    double node4 = 300.0;
                    (V3 dv4, _, _, _) = Plan(ov4, node4);
                    var e4 = ExecuteConstA(ov4, node4, dv4, 0, 0.0);
                    double exp4 = ov4.Radius(node4);
                    orbSb.Append("\n    r₀=").Append((r0v / 1000).ToString("F0")).Append("km：台架 e=")
                         .Append(e4.final.eccentricity.ToString("E3")).Append(" Δa=")
                         .Append(((e4.final.semiMajorAxis - exp4) / 1000.0).ToString("+0.00;-0.00;0.00"))
                         .Append("km｜|Δv|=").Append(dv4.magnitude.ToString("F1"));
                }
                sb.Append(orbSb);
            }
            // ★★ **方向倍率 k 扫描**：哪一档 k 能同时复现游戏四条轨道的 (e, Δa) 图案？
            //    游戏：7000→1.257e-3/+8.46｜6600→2.951e-3/+19.42｜8500→3.797e-3/+32.21｜7800→1.984e-3/+15.49
            {
                var kSb = new System.Text.StringBuilder();
                kSb.Append("\n  ── 方向倍率 k × 四轨道（e / Δa km）──");
                foreach (double k in new[] { 1.0, 1.5, 2.0, 2.5, 3.0 })
                {
                    kSb.Append("\n    k=").Append(k.ToString("F1")).Append("：");
                    foreach (double r0v in new[] { 7.0e6, 6.6e6, 8.5e6, 7.8e6 })
                    {
                        double vcv = Math.Sqrt(Mu / r0v);
                        var ov5 = OrbitView.FromStateVectors(new V3(r0v, 0, 0),
                            new V3(0, Math.Sqrt(1.3) * vcv, 0), Mu, 0.0, new V3(0, 0, 1), r0v * 0.2);
                        (V3 dv5, _, _, _) = Plan(ov5, 300.0);
                        var e5 = ExecuteConstA(ov5, 300.0, dv5, 0, 0.0, false, 4, 0.0, 1.0, 0.0, false, k);
                        kSb.Append(" ").Append((r0v / 1000).ToString("F0")).Append(": ")
                           .Append(e5.final.eccentricity.ToString("E3")).Append("/")
                           .Append(((e5.final.semiMajorAxis - ov5.Radius(300.0)) / 1000.0).ToString("+0.0;-0.0")).Append("km");
                    }
                }
                sb.Append(kSb);
            }
            // ★ **滚转假设**（面内方向转过 δ）：四个读数必须一起对上才算通过
            //    游戏：不变量 0.9959/0.9956、e=1.257e-3、argp=27.5°、Δa=+8.46km、i/LAN 恰为 0
            var rollSb = new System.Text.StringBuilder();
            rollSb.Append("\n  ── 滚转 δ × 四读数（游戏：0.9959/0.9956、e=1.257e-3、Δa=+8.46km、argp=27.5°）──");
            foreach (double rd in new[] { 0.2, 0.4, 0.5, 0.6, 0.8 })
            {
                var er = ExecuteConstA(o, nodeUt, dv, 0, 0.0, false, 4, 0.0, 1.0, rd);
                var ir = Invariants(o, nodeUt, dv, er.final);
                rollSb.Append("\n    δ=").Append(rd.ToString("F1")).Append("°：Δε=").Append(ir.epsRatio.ToString("F4"))
                      .Append(" |Δh|=").Append(ir.hRatio.ToString("F4"))
                      .Append(" e=").Append(er.final.eccentricity.ToString("E3"))
                      .Append(" argp=").Append((er.final.argumentOfPeriapsis * 180.0 / Math.PI).ToString("F1")).Append("°")
                      .Append(" Δa=").Append(((er.final.semiMajorAxis - 7055e3) / 1000.0).ToString("F1")).Append("km");
            }
            // ★ **施加滞后**（游戏：pendingImpulse 下一步才入速度）对不变量/e/Δa 的影响
            {
                var el = ExecuteConstA(o, nodeUt, dv, 0, 0.0, false, 4, 0.0, 1.0, 0.0, true);
                var il = Invariants(o, nodeUt, dv, el.final);
                sb.Append("\n  ── 施加滞后（1 步=2s）：Δε=").Append(il.epsRatio.ToString("F4"))
                  .Append(" |Δh|=").Append(il.hRatio.ToString("F4"))
                  .Append(" e=").Append(el.final.eccentricity.ToString("E3"))
                  .Append(" Δa=").Append(((el.final.semiMajorAxis - 7055e3) / 1000.0).ToString("F1")).Append("km")
                  .Append("   【游戏 0.9959/0.9956、e=1.257e-3、+8.46km】");
            }
            // ★ 相位 × **不变量**（游戏实测 0.9959/0.9956）：哪一档对得上 = 游戏的有效相位偏移
            var invSb = new System.Text.StringBuilder();
            invSb.Append("\n  ── 相位 × 不变量（游戏实测 Δε=0.9959、|Δh|=0.9956）──");
            foreach (double sh in new[] { -4.0, -2.0, 0.0, 2.0, 4.0, 6.0 })
            {
                var e2 = ExecuteConstA(o, nodeUt, dv, 0, sh);
                var iv2 = Invariants(o, nodeUt, dv, e2.final);
                double dEpsFromGame = Math.Abs(iv2.epsRatio - 0.9959);
                invSb.Append("\n    ").Append(sh.ToString("+0;-0;0")).Append("s：Δε=")
                    .Append(iv2.epsRatio.ToString("F4")).Append(" |Δh|=").Append(iv2.hRatio.ToString("F4"))
                    .Append(" e=").Append(e2.final.eccentricity.ToString("E3"))
                    .Append(" argp=").Append((e2.final.argumentOfPeriapsis * 180.0 / Math.PI).ToString("F2")).Append("°")
                    .Append(" Δa=").Append(((e2.final.semiMajorAxis - 7055e3) / 1000.0).ToString("F1")).Append("km")
                    .Append(dEpsFromGame < 0.0008 ? "  ★贴游戏" : "");
            }
            double sensMin = double.MaxValue, sensMax = 0.0;
            foreach (var (_, ev) in alignSens) { sensMin = Math.Min(sensMin, ev); sensMax = Math.Max(sensMax, ev); }
            var sensSb = new System.Text.StringBuilder();
            // ★ 终端真值的等价物（台架侧）：交付冲量的**矢量和**及其相对计划的滚转
            //   游戏实测：|Σ|/计划 = 806.1/808.09 = 0.9975、滚转 −0.23°
            //   ⚠ 对称窗口 + "方向随框架旋转"的律在数学上应当**只折幅度、不产生滚转** ⇒ 台架这里若 ≈0，
            //     则游戏那 −0.23° 是**真不对称**，且它的量（ω×4.6 s）就是定位它的钥匙。
            {
                double vsumRatio = constA.dvVecSum.magnitude / Math.Max(dv.magnitude, 1e-9);
                double vsumAng = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                    V3.Dot(constA.dvVecSum.normalized, dv.normalized)))) * 180.0 / Math.PI;
                sensSb.Append("终端真值等价物：|Σ|=").Append(constA.dvVecSum.magnitude.ToString("F3"))
                      .Append(" / 计划 ").Append(dv.magnitude.ToString("F3"))
                      .Append("（比值 ").Append(vsumRatio.ToString("F5")).Append("）")
                      .Append("　滚转=").Append(vsumAng.ToString("F4")).Append("°")
                      .Append("　【游戏 0.9975 / −0.23°｜对称窗口应为 ≈0】")
                      .Append('\n');
            }
            sensSb.Append("点火对齐敏感度（±4 s = ±2 步）：");
            foreach (var (sh, ev) in alignSens)
                sensSb.Append(' ').Append(sh.ToString("+0;-0;0")).Append("s→").Append(ev.ToString("E3"));
            sensSb.Append("　极差比 ").Append((sensMax / sensMin).ToString("F2"));
            var invConst = Invariants(o, nodeUt, dv, constA.final);
            var polIgn = Execute(o, nodeUt, BurnSeconds, Refresh.Policy, 9, Epoch.Ignition);
            Assert.True(BurnArc.TryPredict(o, nodeUt, dv, BurnSeconds, out BurnArc.Result mr, out string why), why);

            sb.Append("\n\n── Q1 方向律：三种刷新节拍 + 模型（同一计划、同一执行器，只差方向怎么取）──");
            sb.Append("\n  冻结（更早的 B2.3 之前）：e = ").Append(frozen.final.eccentricity.ToString("E4"))
              .Append("　a = ").Append((frozen.final.semiMajorAxis / 1000).ToString("F2")).Append(" km");
            sb.Append("\n  2° 节拍（**旧政策，现已删**）：e = ").Append(policy.final.eccentricity.ToString("E4"))
              .Append("　a = ").Append((policy.final.semiMajorAxis / 1000).ToString("F2")).Append(" km");
            sb.Append("\n  每步连续（**现行政策**）　：e = ").Append(cont.final.eccentricity.ToString("E4"))
              .Append("　a = ").Append((cont.final.semiMajorAxis / 1000).ToString("F2")).Append(" km");
            sb.Append("\n  BurnArc 模型（喂点火前轨道）：e = ").Append(mr.achieved.eccentricity.ToString("E4"))
              .Append("　a = ").Append((mr.achieved.semiMajorAxis / 1000).ToString("F2")).Append(" km");
            sb.Append("\n  **命令恒定加速度（现行政策，用户裁定）**：e = ").Append(constA.final.eccentricity.ToString("E4"))
              .Append("　不变量 ").Append(invConst.epsRatio.ToString("F4")).Append("/").Append(invConst.hRatio.ToString("F4"))
              .Append("　时长=").Append((dv.magnitude * IgnitionMass / Thrust).ToString("F1")).Append("s")
              .Append("　工质=").Append(constA.propellant.ToString("F1")).Append(" kg（全油门 ")
              .Append(cont.propellant.ToString("F1")).Append(" ⇒ 同料久烧）");
            sb.Append("\n  恒定加速度终态：argp=").Append((constA.final.argumentOfPeriapsis * 180.0 / Math.PI).ToString("F3")).Append("°【游戏实测 27.49°】");
            sb.Append("\n  【修复前游戏实测 e = 6.058e-3、a = 7076 km ⇒ 落在「2° 节拍」那一行】");
            sb.Append("\n  恒定加速度 + **游戏实测首段异常**（前 10s ×0.946、净方向偏 7.2°）：e = ")
              .Append(gameLikeC.final.eccentricity.ToString("E3"))
              .Append("　不变量 ").Append(invGameLikeC.epsRatio.ToString("F4")).Append("/").Append(invGameLikeC.hRatio.ToString("F4"))
              .Append("　【游戏实测：e≈1.0~1.26e-3、不变量 0.9956~0.9966】");
            sb.Append("\n  ").Append(sensSb);
            foreach (var (nm, ex) in new[] { ("冻结", frozen), ("2° 节拍", policy), ("连续", cont) })
                sb.Append("\n  方向诊断[").Append(nm).Append("]：点火方向=(")
                  .Append(ex.dir0.x.ToString("F3")).Append(",").Append(ex.dir0.y.ToString("F3")).Append(",")
                  .Append(ex.dir0.z.ToString("F3")).Append(") 末步连续方向=(")
                  .Append(ex.dirEnd.x.ToString("F3")).Append(",").Append(ex.dirEnd.y.ToString("F3")).Append(",")
                  .Append(ex.dirEnd.z.ToString("F3")).Append(") 夹角=")
                  .Append((Math.Acos(Math.Max(-1.0, Math.Min(1.0, V3.Dot(ex.dir0, ex.dirEnd)))) * 180.0 / Math.PI)
                      .ToString("F2")).Append("°　末 |r|=").Append((ex.sampleR[9] == 0.0 ? 0.0 : ex.sampleR[9]).ToString("F0"));

            var invF = Invariants(o, nodeUt, dv, frozen.final);
            var invP = Invariants(o, nodeUt, dv, policy.final);
            var invC = Invariants(o, nodeUt, dv, cont.final);
            var invGame = Invariants(o, nodeUt, dv, gameLike.final);
            var invPolIgn = Invariants(o, nodeUt, dv, polIgn.final);
            var invContIgn = Invariants(o, nodeUt, dv, contIgn.final);
            sb.Append("\n\n── Q2 不变量（Δε / |Δh|）＋终态 e —— **2×2：方向锚点 × 刷新节拍** ──");
            sb.Append("\n  锚点＝节点帧（计划自带口径）：2° 节拍 ").Append(invP.epsRatio.ToString("F4")).Append("/").Append(invP.hRatio.ToString("F4"))
              .Append(" e=").Append(policy.final.eccentricity.ToString("E3"))
              .Append("　｜每步 ").Append(invC.epsRatio.ToString("F4")).Append("/").Append(invC.hRatio.ToString("F4"))
              .Append(" e=").Append(cont.final.eccentricity.ToString("E3"));
            sb.Append("\n  锚点＝点火帧（旧口径：`StartBurn` 处重算）：2° 节拍 ").Append(invPolIgn.epsRatio.ToString("F4")).Append("/").Append(invPolIgn.hRatio.ToString("F4"))
              .Append(" e=").Append(polIgn.final.eccentricity.ToString("E3"))
              .Append("　｜每步 ").Append(invContIgn.epsRatio.ToString("F4")).Append("/").Append(invContIgn.hRatio.ToString("F4"))
              .Append(" e=").Append(contIgn.final.eccentricity.ToString("E3"));
            sb.Append("\n  节点帧 + 每步 + **起步空转 2 步**（实测 106s 含 ~2.7 步空转的复刻）：")
              .Append(invGame.epsRatio.ToString("F4")).Append("/").Append(invGame.hRatio.ToString("F4"))
              .Append(" e=").Append(gameLike.final.eccentricity.ToString("E3"))
              .Append("　a=").Append((gameLike.final.semiMajorAxis / 1000).ToString("F1")).Append(" km");
            sb.Append("\n  冻结（无锚点概念）=").Append(invF.epsRatio.ToString("F4")).Append(" / ").Append(invF.hRatio.ToString("F4"))
              .Append(" e=").Append(frozen.final.eccentricity.ToString("E3"));
            sb.Append("\n  【游戏实测①（2° 节拍时代）：0.9897/0.9890、e=6.058e-3 ⇒ 不变量落在节点帧格】");
            sb.Append("\n  【游戏实测②（每步 + 点火帧）：0.9974/0.9974、e=7.477e-3】");
            sb.Append("\n  【游戏实测③（每步 + 节点帧）：0.9808/0.9792、e=5.794e-3、a=7095 km ⇒ 空转把不变量压下来】");
            sb.Append("\n  ⇒ **目标行 = 「节点帧 + 每步 + 不空转」= 0.9975/0.9973、e=7.79e-4**（点火门放宽到锥角后的预期）");
            sb.Append("\n  ⇒ ⚠ **两次实测内部不一致**（不变量说节点帧、e 说点火帧）—— 方向诊断「模型 vs 执行方向」");
            sb.Append("\n     恒 2.23–2.36°（= ω·49s 的锚点差）说明**执行器的命令是点火帧锚点**。两条读数谁对谁错的");
            sb.Append("\n     判别法已不必要：**让执行器直接用计划自带的节点帧分量**（`SetBurnSolution`）⇒ 按构造同律。");
            sb.Append("\n  陈旧角（2° 节拍，deg；游戏日志锯齿 0.48→2.38、周期≈40 s）：");
            for (int i = 1; i <= 9; i++) sb.Append(" ").Append(policy.dirLagDeg[i].ToString("F2"));
            sb.Append("\n  连续执行器同读数：");
            for (int i = 1; i <= 9; i++) sb.Append(" ").Append(cont.dirLagDeg[i].ToString("F3"));
            sb.Append("\n  燃耗：连续 ").Append(cont.propellant.ToString("F1")).Append(" kg｜2° 节拍 ")
              .Append(policy.propellant.ToString("F1")).Append(" kg【游戏日志 13795.7】");
            sb.Append("\n  施加 Δv：连续 ").Append(cont.appliedDv.ToString("F1")).Append("｜2° 节拍 ")
              .Append(policy.appliedDv.ToString("F1")).Append(" m/s【游戏日志 808.5，计划 808.09】");

            // ── Q3：喂错基准（当前 = 已烧过的轨道）⇒ 预测随累计 Δv 漂移 ──
            sb.Append("\n\n── Q3 基准错喂：TryPredictPartial(当前轨道) vs (点火前轨道) ──");
            double worstWrong = 0.0, worstRight = 0.0;
            foreach (double el in new[] { 9.8, 29.4, 49.0, 68.6, 98.0 })
            {
                var now = Execute(o, nodeUt, el, Refresh.Continuous, 0);
                Assert.True(BurnArc.TryPredictPartial(now.final, nodeUt, dv, BurnSeconds, el,
                    out BurnArc.Result wrong, out _));
                Assert.True(BurnArc.TryPredictPartial(o, nodeUt, dv, BurnSeconds, el,
                    out BurnArc.Result right, out _));

                double vMeas = now.final.RightHandedOrbitalVelocityAtUT(now.final.epoch).magnitude;
                double vWrong = wrong.achieved.RightHandedOrbitalVelocityAtUT(wrong.achieved.epoch).magnitude;
                double vRight = right.achieved.RightHandedOrbitalVelocityAtUT(right.achieved.epoch).magnitude;
                double eWrong = Math.Abs(vWrong - vMeas), eRight = Math.Abs(vRight - vMeas);
                worstWrong = Math.Max(worstWrong, eWrong);
                worstRight = Math.Max(worstRight, eRight);
                sb.Append("\n  t=").Append(el.ToString("F0")).Append("s：实测|v|=").Append(vMeas.ToString("F1"))
                  .Append("　错基准=").Append(vWrong.ToString("F1")).Append("（差 ").Append(eWrong.ToString("F1"))
                  .Append("，已烧 Δv=").Append(now.appliedDv.ToString("F1"))
                  .Append("）　对基准=").Append(vRight.ToString("F1")).Append("（差 ").Append(eRight.ToString("F2")).Append("）");
            }
            sb.Append("\n  【游戏日志同一现象：t=100s 实测|v|=5347.3、模型预测 4652.9，差 694.3 ≈ 已烧 Δv】");

            sb.Append(grid);
            sb.Append(planSb);
            sb.Append(rollSb);
            sb.Append(chSb);
            sb.Append(invSb);
            _out.WriteLine(sb.ToString());

            // ── 断言（= 诊断落锤 + 回归护栏）──
            // ① 基准取自证：台架的 RTN 分量与游戏日志一致
            Assert.InRange(dv.magnitude, 807.0, 810.0);
            Assert.InRange(rad, -363.0, -360.0);
            Assert.InRange(tan, -724.0, -721.0);
            // ② **锚点换一格，e 差 ~10 倍**（不变量看起来都"接近 1"，只有 e 能分辨 —— 这就是当初
            //    为什么"不变量都对了 e 还是 7.5e-3"）：节点帧锚点 = 计划自带口径 = 现行执行器口径
            Assert.True(cont.final.eccentricity < contIgn.final.eccentricity * 0.3,
                $"节点帧锚点应显著优于点火帧：{cont.final.eccentricity:E3} vs {contIgn.final.eccentricity:E3}");
            Assert.True(invC.epsRatio > 0.996 && invC.hRatio > 0.996,
                $"节点帧 + 每步：ε {invC.epsRatio:F4}、h {invC.hRatio:F4}");
            // ③ 点火帧锚点 = 旧行为（`StartBurn` 只拿到世界向量 ⇒ 在点火帧重新分解）：数字留档，
            //    供下次有人改回时立刻看出代价。⚠ 注意不变量在这里**反而**偏到 1.01 以上（比节点帧"更像 1"）
            //    —— 所以**不变量不能用来认锚点**，只有 e 能分辨（这正是本次踩的坑）。
            // ③b **空转的代价**（游戏实测 106s 里有 ~2.7 步空转 ⇒ 不变量 0.9808/0.9792、e 5.79e-3）：
            //    台架起步空转 2 步就落到 0.956 ⇒ **空转是最后那 2–4% 的来源**（比线性外推大得多，故实测）
            Assert.InRange(invGame.epsRatio, 0.9450, 0.9650);
            Assert.InRange(invGame.hRatio, 0.9430, 0.9630);
            // ③c **恒定加速度政策**（用户裁定）：模型（喂同一条 t_b）必须给出同一条轨道 —— 这是 B2.2 的闸门；
            //    工质应与全油门**逐位相同**（火箭方程不依赖燃烧形状）。
            Assert.True(BurnArc.TryPredict(o, nodeUt, dv, dv.magnitude * IgnitionMass / Thrust,
                out BurnArc.Result mrConst, out string whyC), whyC);
            // 容差 2.5e-4 = **执行器 2 s 步离散化的地板**（模型用 400 步 RK4，比执行器更细 ⇒ 它更准；
            // 这一项只有把步长/步内处理也细化了才能消 —— 登记：若将来要 e < 3e-4，先动这里）
            Assert.True(Math.Abs(mrConst.achieved.eccentricity - constA.final.eccentricity) < 2.5e-4,
                $"模型 e={mrConst.achieved.eccentricity:E3} vs 恒定加速度执行器 e={constA.final.eccentricity:E3}");
            Assert.InRange(constA.final.eccentricity, 3.0e-4, 8.0e-4);
            Assert.True(invConst.epsRatio > 0.996 && invConst.hRatio > 0.996,
                $"恒定加速度：ε {invConst.epsRatio:F4}、h {invConst.hRatio:F4}");
            // 容差 0.3%：逐段比例油门是 O(dt²) 离散化（实测 0.16%）⇒ 总量应与全油门基本一致
            Assert.True(Math.Abs(constA.propellant - cont.propellant) < cont.propellant * 0.003,
                $"工质应与全油门相同：{constA.propellant:F1} vs {cont.propellant:F1} kg");
            // ④ **噪声地板**：±1 步的点火对齐抖动对 e 的影响必须被记录（实测游戏里点火落点有这一步抖动）
            // 记录项（**不是**"应有限"）：实测 ±4 s 的对齐抖动能把 e 推 5.5 倍 ⇒ 拿单轮 e 比政策前，
            // 先确认差异超出这条带。阈值只防"完全失控"。
            Assert.True(sensMax / sensMin < 20.0,
                $"点火对齐敏感度（记录）：{sensMin:E3} ~ {sensMax:E3}（比值 {sensMax / sensMin:F2}）");
            Assert.True(invGame.epsRatio < invC.epsRatio - 0.03,
                $"空转该显著压低不变量：空转 {invGame.epsRatio:F4} vs 不空转 {invC.epsRatio:F4}");
            Assert.InRange(invPolIgn.epsRatio, 1.0075, 1.0105);
            Assert.InRange(invPolIgn.hRatio, 1.0085, 1.0110);
            Assert.InRange(invContIgn.epsRatio, 1.0145, 1.0175);
            Assert.InRange(invContIgn.hRatio, 1.0160, 1.0190);
            Assert.InRange(polIgn.final.eccentricity, 3.6e-3, 5.2e-3);
            Assert.InRange(contIgn.final.eccentricity, 6.5e-3, 8.5e-3);
            // ④ 前向模型（**喂点火前轨道**）与"每步重取"的执行器一致到有限燃烧残差量级
            Assert.True(Math.Abs(mr.achieved.eccentricity - cont.final.eccentricity) < 1e-3,
                $"模型 e={mr.achieved.eccentricity:E3} vs 连续执行器 e={cont.final.eccentricity:E3}");
            // ⑤ 基准错喂（游戏里的旧调用方式）⇒ 偏差 ≈ 已烧累计 Δv；喂对基准 ⇒ 偏差 ≪ 它
            Assert.True(worstWrong > 500.0, $"错基准应漂移 ≈ 已烧 Δv，实测 {worstWrong:F1} m/s");
            Assert.True(worstRight < 30.0, $"对基准的最大 |v| 偏差 {worstRight:F1} m/s 应远小于错基准 {worstWrong:F1}");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // ★ 2026-09-19：**把游戏那台仪器搬进台架**
        //
        // 教训：台架原来把「冲量矢量和」(`dvVecSum`) 当"终端真值的等价物"——**那是另一台仪器**。
        // 它看不见"冲量施加在不同位置"的效果（冲量在弧上各点作用，反推到节点后与"在节点施加"不等价），
        // 所以台架的 0.99968 与游戏的 0.99754 **不可比**，我拿它们对照了好几轮 = 又一次「仪器没校准」。
        // 本节的仪器与游戏 `LiveBurnAcceptanceRunner` 的终端真值**逐字同口径**：
        //   ① 取终态时刻的状态；② 用**适配层**把它推回节点 UT；③ 减掉点火前节点状态 ⇒ 交付 Δv 向量；
        //   ④ 在节点 RTN 上分解；⑤ 与计划向量比幅值/滚转（滚转 = 绕 ĥ 的带符号转角）。
        // ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>终端真值（同游戏口径）：反推交付 Δv 向量 + 相对计划向量的带符号滚转。</summary>
        private static (double mag, double rad, double tan, double nor, double rollDeg) TerminalTruth(
            OrbitView pre, double nodeUt, V3 dvPlan, OrbitView post)
        {
            (V3 r0, V3 v0) = pre.RightHandedStateVectorsAtUT(nodeUt);
            (V3 _, V3 vb) = post.RightHandedStateVectorsAtUT(nodeUt);
            V3 dvDel = vb - v0;
            (double rad, double tan, double nor) = Components(pre, nodeUt, dvDel);
            V3 hhat = V3.Cross(r0, v0).normalized;
            V3 a = dvPlan.normalized, b = dvDel.normalized;
            double roll = Math.Atan2(V3.Dot(V3.Cross(a, b), hhat), V3.Dot(a, b)) * 180.0 / Math.PI;
            return (dvDel.magnitude, rad, tan, nor, roll);
        }

        /// <summary>绕给定轴把向量转 <paramref name="deg"/> 度（面内滚转的构造器，保持模长）。</summary>
        private static V3 RotAbout(V3 v, V3 axisHat, double deg)
        {
            double r = deg * Math.PI / 180.0;
            V3 p = V3.Cross(axisHat, v);
            return (v * Math.Cos(r) + p * Math.Sin(r)).normalized * v.magnitude;
        }

        /// <summary>由**节点帧**的 R/T/N 分量合成世界向量（= 执行器实际拿在手里的那份解）。</summary>
        private static V3 FromComponents(OrbitView o, double ut, double rad, double tan, double nor)
        {
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(ut);
            V3 rhat = r.normalized;
            V3 hhat = V3.Cross(r, v).normalized;
            V3 that = V3.Cross(hhat, rhat);
            return rad * rhat + tan * that + nor * hhat;
        }

        [Fact]
        public void Bench_TerminalTruth_ApplesToApples()
        {
            var o = PreBurn();
            double nodeUt = NodeSeconds;
            (V3 dv, _, _, _) = Plan(o, nodeUt);
            (double pRad, double pTan, double pNor) = Components(o, nodeUt, dv);
            double tBurn = dv.magnitude * IgnitionMass / Thrust;   // 命令恒定加速度下的时长（与执行器同式）

            var ideal = ExecuteConstA(o, nodeUt, dv, 0);
            var ttIdeal = TerminalTruth(o, nodeUt, dv, ideal.final);

            bool ok = BurnArc.TryPredict(o, nodeUt, dv, tBurn, out var model, out string why);
            var ttModel = ok ? TerminalTruth(o, nodeUt, dv, model.achieved) : default;

            var sb = new StringBuilder();
            sb.Append("\n══ 终端真值 · 同仪器对照（反推状态，无模型）══");
            sb.Append("\n计划：|Δv|=").Append(dv.magnitude.ToString("F3"))
              .Append("　R/T/N=").Append(pRad.ToString("F2")).Append(" / ")
              .Append(pTan.ToString("F2")).Append(" / ").Append(pNor.ToString("F2"))
              .Append("　t_b(a_cmd 口径)=").Append(tBurn.ToString("F2")).Append("s");
            sb.Append("\n【游戏 G4 实测：反推 806.102 / 计划 808.091 = 0.99754")
              .Append("｜R/T/N 反推 -363.80 / -719.34 / 0.00｜ΔR/ΔT = -2.46 / +3.46｜滚转 -0.266°】");
            sb.Append("\n理想执行器：账本指令 ").Append(ideal.appliedDv.ToString("F3"))
              .Append(" ⇒ 反推 ").Append(ttIdeal.mag.ToString("F3"))
              .Append("（比值 ").Append((ttIdeal.mag / dv.magnitude).ToString("F5")).Append("）")
              .Append("　滚转 ").Append(ttIdeal.rollDeg.ToString("F4")).Append("°")
              .Append("　ΔR/ΔT/ΔN = ").Append((ttIdeal.rad - pRad).ToString("F2")).Append(" / ")
              .Append((ttIdeal.tan - pTan).ToString("F2")).Append(" / ")
              .Append((ttIdeal.nor - pNor).ToString("F2"));
            sb.Append("\n冲量矢量和（旧仪器，仅供对照）：|Σ|=").Append(ideal.dvVecSum.magnitude.ToString("F3"))
              .Append("（比值 ").Append((ideal.dvVecSum.magnitude / dv.magnitude).ToString("F5")).Append("）");
            if (ok)
            {
                sb.Append("\nBurnArc 模型：账本 ").Append(tBurn.ToString("F1")).Append("s ⇒ 反推 ")
                  .Append(ttModel.mag.ToString("F3"))
                  .Append("（比值 ").Append((ttModel.mag / dv.magnitude).ToString("F5")).Append("）")
                  .Append("　滚转 ").Append(ttModel.rollDeg.ToString("F4")).Append("°")
                  .Append("　ΔR/ΔT = ").Append((ttModel.rad - pRad).ToString("F2")).Append(" / ")
                  .Append((ttModel.tan - pTan).ToString("F2"))
                  .Append("　e=").Append(model.achieved.eccentricity.ToString("E3"));
            }
            else sb.Append("\nBurnArc 模型：失败（").Append(why).Append("）");

            // ── 游戏那轮的**真实指挥官**：重解后的解（节点帧分量）＋ −2 s 提前点火 ──            //   目的：把"重解差"与"执行延迟"两项从"未知残差"里剔除（结果空间先枚举好）：
            //     · 若这行的 ΔR/ΔT ≈ 游戏 (-2.46/+3.46) ⇒ 残差**全部**是这三项之和，无未知机制；
            //     · 若仍停在理想行的 (-2.04/+1.41) ⇒ 另有游戏独有机制，量级 = 差值。
            V3 dvRes = FromComponents(o, nodeUt, -362.51, -722.63, 0.0);
            var exRes = ExecuteConstA(o, nodeUt, dvRes, 0);
            var ttRes = TerminalTruth(o, nodeUt, dv, exRes.final);
            var exRes2 = ExecuteConstA(o, nodeUt, dvRes, 0, -2.0);
            var ttRes2 = TerminalTruth(o, nodeUt, dv, exRes2.final);
            // ★ **一拍的框架滞后**（2026-09-20 新增假设，由修好后的 R/T/N 读数逼出来）：
            //   修好仪器后逐窗读数是"切向每窗少 0.10~0.115 m/s"⇒ 反推方向偏 **0.095°**，
            //   而一步的框架转角 `ω·dt = 0.0998°` —— 差 5%。⇒ 冲量用的方向比它落位的时刻晚**一拍**。
            var exLag = ExecuteConstA(o, nodeUt, dv, 0, 0.0, false, 4, 7.2, 0.946, 0.0, true);
            var ttLag = TerminalTruth(o, nodeUt, dv, exLag.final);
            var exResLag = ExecuteConstA(o, nodeUt, dvRes, 0, -2.0, false, 4, 7.2, 0.946, 0.0, true);
            var ttResLag = TerminalTruth(o, nodeUt, dv, exResLag.final);
            sb.Append("\n── 游戏指挥官（与排队计划的夹角 ").Append(
                (Math.Acos(Math.Max(-1.0, Math.Min(1.0, V3.Dot(dv.normalized, dvRes.normalized)))) * 180.0 / Math.PI)
                .ToString("F4")).Append("°）──");
            sb.Append("\n  重解版（重解 −362.51/−722.63，居中）：反推 ").Append(ttRes.mag.ToString("F3"))
              .Append("（比值 ").Append((ttRes.mag / dv.magnitude).ToString("F5")).Append("）　滚转 ")
              .Append(ttRes.rollDeg.ToString("F4")).Append("°　ΔR/ΔT=")
              .Append((ttRes.rad - pRad).ToString("F2")).Append(" / ").Append((ttRes.tan - pTan).ToString("F2"));
            sb.Append("\n  重解版 + 点火提前 2s（= 游戏的 ign tick 口径）：反推 ").Append(ttRes2.mag.ToString("F3"))
              .Append("（比值 ").Append((ttRes2.mag / dv.magnitude).ToString("F5")).Append("）　滚转 ")
              .Append(ttRes2.rollDeg.ToString("F4")).Append("°　ΔR/ΔT=")
              .Append((ttRes2.rad - pRad).ToString("F2")).Append(" / ").Append((ttRes2.tan - pTan).ToString("F2"));
            sb.Append("\n  理想执行器 + **一拍框架滞后**：反推 ").Append(ttLag.mag.ToString("F3"))
              .Append("（比值 ").Append((ttLag.mag / dv.magnitude).ToString("F5")).Append("）　滚转 ")
              .Append(ttLag.rollDeg.ToString("F4")).Append("°　ΔR/ΔT=")
              .Append((ttLag.rad - pRad).ToString("F2")).Append(" / ").Append((ttLag.tan - pTan).ToString("F2"))
              .Append("　e=").Append(exLag.final.eccentricity.ToString("E3"));
            sb.Append("\n  重解版 + 提前 2s + **一拍滞后**：反推 ").Append(ttResLag.mag.ToString("F3"))
              .Append("（比值 ").Append((ttResLag.mag / dv.magnitude).ToString("F5")).Append("）　滚转 ")
              .Append(ttResLag.rollDeg.ToString("F4")).Append("°　ΔR/ΔT=")
              .Append((ttResLag.rad - pRad).ToString("F2")).Append(" / ").Append((ttResLag.tan - pTan).ToString("F2"))
              .Append("　e=").Append(exResLag.final.eccentricity.ToString("E3"))
              .Append("　**【游戏实测 0.99754 / −0.266° / (−2.46, +3.46) / e=1.26~1.28e-3】**");

            // ── 仪器标定：滚转/幅值 → ΔR/ΔT 的雅可比（把游戏那三个数反解成"有效滚转/有效幅值"）──            sb.Append("\n── 标定（同一台仪器：人为改指令，读残差）──");
            V3 hhat = V3.Cross(o.RightHandedStateVectorsAtUT(nodeUt).Item1, o.RightHandedStateVectorsAtUT(nodeUt).Item2).normalized;
            foreach (double roll in new[] { -0.3, -0.15, 0.15, 0.3 })
            {
                V3 dvR = RotAbout(dv, hhat, roll);
                var ex = ExecuteConstA(o, nodeUt, dvR, 0);
                var tt = TerminalTruth(o, nodeUt, dv, ex.final);
                sb.Append("\n  指令滚转 ").Append(roll.ToString("F2")).Append("° ⇒ 反推 |Δv|=")
                  .Append(tt.mag.ToString("F3")).Append("　ΔR/ΔT=")
                  .Append((tt.rad - pRad).ToString("F2")).Append(" / ")
                  .Append((tt.tan - pTan).ToString("F2"));
            }
            foreach (double k in new[] { 0.997, 1.003 })
            {
                var ex = ExecuteConstA(o, nodeUt, dv * k, 0);
                var tt = TerminalTruth(o, nodeUt, dv, ex.final);
                sb.Append("\n  指令幅值 ×").Append(k.ToString("F3")).Append(" ⇒ 反推 |Δv|=")
                  .Append(tt.mag.ToString("F3")).Append("　ΔR/ΔT=")
                  .Append((tt.rad - pRad).ToString("F2")).Append(" / ")
                  .Append((tt.tan - pTan).ToString("F2"));
            }

            _out.WriteLine(sb.ToString());

            // 记录型断言（只防完全失控；本测的产出是**读数**）
            Assert.True(ok, "BurnArc 前向模型应可用");
            Assert.InRange(ttIdeal.mag / dv.magnitude, 0.99, 1.01);
            Assert.True(Math.Abs(ttIdeal.rollDeg) < 1.0, $"理想执行器滚转应在 1° 内，实测 {ttIdeal.rollDeg:F4}°");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // ★★ "MJ 优先"第 5 件：**活差额律**（`BurnStep.DeficitToTargetV`）验收 + 与冻结律对照（2026-09-20）
        //
        // MJ 的节点执行器（`MechJeb2/MechJebModuleNodeExecutor.cs`，逐行读过）不是"烧一个冻结的 Δv"：
        //   · `NextDirection()`：把**活轨道**（`node.GetBurnVector(Orbit)` 里的 `Orbit` 是活的）推到节点 `UT`，
        //     取"目标速度 − 活速度"作为方向 —— **每拍重算**；只有末段 Δv 小于一秒的量时才冻结方向；
        //   · `ShouldTerminateStock()`：终止判据是**几何的**（剩余差额与推力方向夹角 ≥ 90° ⇒ 超调 ⇒ 收手）；
        //   · `CalculateIgnitionUT()`：点火 = 节点 − 半程（居中，与我们同）。
        // 我们的"冻结的解 + 事后弧补偿 + 收口微调"是它的近似 ⇒ 按用户裁定（"MJ 优秀就按 MJ 来"）改。
        //
        // **本节要判两件事，结果空间先枚举好**：
        //   ① **它被实现对了吗**（= 收敛到它的定义）：`v(UT)` 失配 → 地板 ✓；
        //   ② **它能不能顶替那三件套**（= 后处理轨道的 `e` 真的归零吗）？
        //      预判**不能** —— "v(UT) 对" ≠ "轨道对"：燃烧期间**位置已经偏移**（δr ≈ ∫∫a dt²，
        //      本场景实测数公里径向），而同样的速度在**不同半径**上就是**另一条轨道**
        //      （vis-viva：Δ(1/a) = −2Δr/r² ⇒ e ~ δr/r 量级 = **实测那笔 5.96e-4 弧误差**）。
        //      ⇒ 若 ② 被证伪，正确的组合是"**MJ 的架构**（逐拍活差额）× **我们的靶**（意图圆锥曲线）"。
        // ═══════════════════════════════════════════════════════════════════════════════════════

        // 批5 U4（2026-09-28）：本地那两个恒等转换助手已删（类型统一后移植面类型就是 `DVector3`）。

        private readonly struct LiveExec
        {
            public readonly OrbitView final;
            public readonly double appliedDv, propellant, residAtUt, minDot;
            public readonly int steps;
            public readonly bool reversed;
            public LiveExec(OrbitView final, double appliedDv, double propellant, double residAtUt,
                int steps, bool reversed, double minDot)
            {
                this.final = final; this.appliedDv = appliedDv; this.propellant = propellant;
                this.residAtUt = residAtUt; this.steps = steps; this.reversed = reversed; this.minDot = minDot;
            }
        }

        /// <summary>**活差额律的执行器**（台架模型，与游戏逐条对应）：每步用 `BurnStep.DeficitToTargetV`
        /// 重算差额（= 游戏 `burnTargetV` 那条，**同一份算式**），差额既给方向又给剩余量；
        /// 终止 = 地板（达标）或反向（超调收手）。重力按力学步长积分（与游戏物理步同构）。</summary>
        private static LiveExec ExecuteLiveDeficit(OrbitView o, double nodeUt, V3 dv,
            double floor = 1e-3, double midKick = 0.0)
        {
            double tBurn = dv.magnitude * IgnitionMass / Thrust;
            double aCmd = dv.magnitude / tBurn;
            double startUt = nodeUt - 0.5 * tBurn;
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(startUt);
            V3 vTarget = o.RightHandedStateVectorsAtUT(nodeUt).Item2 + dv;

            double m = IgnitionMass, propellant = 0.0, applied = 0.0;
            V3 prevDir = V3.zero;   // 首步：与零向量点乘 ⇒ 不会误判"反向"
            int n = (int)Math.Round(tBurn / StepSeconds) + 8, steps = 0;
            bool reversed = false; double minDot = 1.0;
            for (int i = 0; i < n; i++)
            {
                double nowUt = startUt + i * StepSeconds;
                (V3 _, V3 vLiveUt) = MechJebLib.TwoBody.Shepperd.Solve(Mu, nodeUt - nowUt, r, v);
                BurnStep.DeficitToTargetV(vLiveUt, vTarget, prevDir, floor,
                    out DVector3 defDir, out double rem, out bool rev);
                if (rev) { reversed = true; break; }
                if (!(rem > 0.0)) break;
                V3 dir = defDir;
                if (prevDir.sqrMagnitude > 0.0)
                    minDot = Math.Min(minDot, V3.Dot(dir, prevDir.normalized));
                double aFull = Thrust / m;
                double dvFull = aFull * StepSeconds;
                double burnDv = Math.Min(aCmd * StepSeconds, rem);
                v += dir * burnDv;
                double share = dvFull > 0.0 ? burnDv / dvFull : 0.0;
                propellant += MassFlow * StepSeconds * share;
                m -= MassFlow * StepSeconds * share;
                applied += burnDv; steps++; prevDir = dir;
                (r, v) = MechJebLib.TwoBody.Shepperd.Solve(Mu, StepSeconds, r, v);
                if (midKick != 0.0 && i == n / 3) v += dir * midKick;   // 扰动注入（测反向终止）
            }
            double endUt = startUt + steps * StepSeconds;
            (V3 _, V3 vEndUt) = MechJebLib.TwoBody.Shepperd.Solve(Mu, nodeUt - endUt, r, v);
            return new LiveExec(OrbitView.FromStateVectors(r, v, Mu, endUt, new V3(0, 0, 1), R0 * 0.2),
                applied, propellant, (vEndUt - vTarget).magnitude, steps, reversed, minDot);
        }

        [Fact]
        public void Bench_LiveDeficitLaw_ConvergesButDoesNotZeroOrbitError()
        {
            var o = PreBurn();
            double nodeUt = NodeSeconds;
            (V3 dv, double pRad, double pTan, double pNor) = Plan(o, nodeUt);
            V3 vTarget = o.RightHandedStateVectorsAtUT(nodeUt).Item2 + dv;

            var frozen = ExecuteConstA(o, nodeUt, dv, 0);
            var ttFrozen = TerminalTruth(o, nodeUt, dv, frozen.final);
            (V3 rFr, V3 vFr) = frozen.final.RightHandedStateVectorsAtUT(frozen.final.epoch);
            (V3 _, V3 vFrUt) = MechJebLib.TwoBody.Shepperd.Solve(Mu, nodeUt - frozen.final.epoch, rFr, vFr);
            double frozenResidUt = (vFrUt - vTarget).magnitude;

            var live = ExecuteLiveDeficit(o, nodeUt, dv);
            var ttLive = TerminalTruth(o, nodeUt, dv, live.final);

            var sb = new StringBuilder();
            sb.Append("\n══ 活差额律（MJ 节点执行器）× 冻结律（我们此前那套）对照 ══");
            sb.Append("\n计划：|Δv|=").Append(dv.magnitude.ToString("F3"))
              .Append("　R/T/N=").Append(pRad.ToString("F2")).Append(" / ").Append(pTan.ToString("F2"))
              .Append(" / ").Append(pNor.ToString("F2"));
            sb.Append("\n① 定义判据（v(UT) 失配，**独立仪器**：把终态推回 UT 与目标速度比）：");
            sb.Append("\n   冻结律 = ").Append(frozenResidUt.ToString("E3")).Append(" m/s");
            sb.Append("\n   活差额 = ").Append(live.residAtUt.ToString("E3")).Append(" m/s（地板 ")
              .Append("1e-3）　步数 ").Append(live.steps).Append("　反向终止 ").Append(live.reversed);
            sb.Append("\n② 后处理轨道（同仪器 TerminalTruth）：");
            sb.Append("\n   冻结律：反推 |Δv|=").Append(ttFrozen.mag.ToString("F3"))
              .Append("（比值 ").Append((ttFrozen.mag / dv.magnitude).ToString("F5"))
              .Append("）　滚转 ").Append(ttFrozen.rollDeg.ToString("F4"))
              .Append("°　e=").Append(frozen.final.eccentricity.ToString("E3"));
            sb.Append("\n   活差额：反推 |Δv|=").Append(ttLive.mag.ToString("F3"))
              .Append("（比值 ").Append((ttLive.mag / dv.magnitude).ToString("F5"))
              .Append("）　滚转 ").Append(ttLive.rollDeg.ToString("F4"))
              .Append("°　e=").Append(live.final.eccentricity.ToString("E3"));
            sb.Append("\n③a 扰动吸收（燃烧中途 +200 m/s 顺行推一把，未超调）：");
            var absorb = ExecuteLiveDeficit(o, nodeUt, dv, 1e-3, 200.0);
            sb.Append("反向终止 ").Append(absorb.reversed).Append("　步数 ").Append(absorb.steps)
              .Append("（未扰动 ").Append(live.steps).Append("）　终态失配 ")
              .Append(absorb.residAtUt.ToString("E3")).Append(" ⇒ **律自己把它吸收了**（冻结律做不到）");
            sb.Append("\n③b 超调（+2000 m/s ⇒ 远超剩余差额）：");
            var kicked = ExecuteLiveDeficit(o, nodeUt, dv, 1e-3, 2000.0);
            sb.Append("反向终止 ").Append(kicked.reversed).Append("　步数 ").Append(kicked.steps)
              .Append("（未扰动 ").Append(live.steps).Append("）　方向最小点乘 ")
              .Append(kicked.minDot.ToString("F4")).Append("（≥0 = **从不反向烧**）");
            _out.WriteLine(sb.ToString());

            // ── 断言 ──
            // ① 实现正确：收敛到它自己的定义（地板即终态失配）
            Assert.True(live.residAtUt <= 2e-3,
                $"活差额应收敛到地板：失配 {live.residAtUt:E3} m/s");
            Assert.True(frozenResidUt > 10.0 * Math.Max(live.residAtUt, 1e-9),
                $"冻结律的 v(UT) 失配应远大于活差额：{frozenResidUt:E3} vs {live.residAtUt:E3} m/s");
            // ② **靶不够**：v(UT) 归零**不等于**轨道归零（位置已偏移 ⇒ 半径不同 ⇒ 另一条轨道）
            //    ⇒ 这条断言把"照搬 MJ 的靶"钉死为**不合格**（正确组合 = MJ 架构 × 意图圆锥曲线靶）
            Assert.True(live.final.eccentricity > 1e-5,
                $"逐拍 v(UT) 差额律**不应**把轨道误差归零（位置偏移仍在）：e={live.final.eccentricity:E3}"
                + $"（若此断言失败 ⇒ 我的量纲分析错了，那时才该把靶换成 v(UT)）");
            // ③ 扰动：小的被吸收（仍收敛、提前收工）；大的（超调）⇒ 收手且从不反向烧
            Assert.False(absorb.reversed, "200 m/s 顺行扰动不应判为超调");
            Assert.True(absorb.residAtUt <= 2e-3,
                $"扰动应被吸收（仍收敛到地板）：失配 {absorb.residAtUt:E3}");
            Assert.True(absorb.steps < live.steps, $"吸收扰动应提前收工：{absorb.steps} vs {live.steps}");
            Assert.True(kicked.reversed, "2000 m/s 超前扰动应触发反向终止");
            Assert.True(kicked.minDot >= 0.0, $"从不反向烧：最小点乘 {kicked.minDot:F4}");
            Assert.True(kicked.steps < live.steps, $"应提前收手：{kicked.steps} vs {live.steps}");
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════
        // ★★ 第二步：**靶换成"意图轨道"**（MJ 的架构 × 我们的靶）
        //
        // 上面那条证明：MJ 的靶（`v(UT)`）解决了"重力在窗口内的贡献"（滚转 −0.174°→0.000°、
        // e 5.96e-4→1.14e-4），但**解决不了位置偏移**（燃烧期间 δr ≈ 数公里 ⇒ 同样的速度在不同半径上
        // 就是另一条轨道）。而计划真正要的**不是**"UT 处的速度"，是"**落到那条轨道上**"。
        //
        // ⚠ **踩过的两个坑（都靠台架当场抓住，别再走一遍）**：
        //   ① 靶取"意图圆锥曲线在**当前半径**处的速度" ⇒ 对**圆轨靶是病态的**：圆只有一个半径，
        //      本点半径不等于 a_t 就无解（实测：定律一步没烧、e 停在原值 0.3 ✗）；
        //   ② 靶取标量 `(a, |h|)`（= `TrimBurn` 原来的共面口径）⇒ **纯面外变轨会被静默跳过**：
        //      换平面既不改 a 也不改 |h|（|h| = r|v|sinγ）⇒ 差额恒 0 ⇒ **整个平面改变不执行** ✗✗。
        // ⇒ 正确靶 = **`(a_t, 角动量向量 h_t·ĥ_t)`**：能量是**标量**（任意 `a > r/2` 都容纳本点 ⇒ 无病态），
        //   平面靠**向量**表达（纯换平面就活在这一项里）。原语 = `TrimBurn.TryPlan(..., hTargetDir, ...)`
        //   （与收口微调**同一份算式**，只是靶多了方向、且**逐拍**调它而不是收口时调一次）。
        // ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>**意图轨道靶的活差额律**执行器：与 <see cref="ExecuteLiveDeficit" /> 同架构，
        /// 靶 = 计划想做的那条轨道（在节点处一次施加计划 Δv 得到的 `(a, h向量)`）。
        /// `useVectorPlane=false` ⇒ 退成共面标量靶（对照：用来证明"平面必须用向量"）。</summary>
        private static LiveExec ExecuteLiveTargetDeficit(OrbitView o, double nodeUt, V3 dv,
            double floor = 1e-3, bool useVectorPlane = true)
        {
            double tBurn = dv.magnitude * IgnitionMass / Thrust;
            double aCmd = dv.magnitude / tBurn;
            double startUt = nodeUt - 0.5 * tBurn;
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(startUt);
            // 靶：点火前轨道在节点的状态 + 计划 Δv ⇒ 计划想做的那条轨道
            (V3 rPreUt, V3 vPreUt) = o.RightHandedStateVectorsAtUT(nodeUt);
            V3 vAfter = vPreUt + dv;
            double aT = 1.0 / (2.0 / rPreUt.magnitude - vAfter.sqrMagnitude / Mu);   // vis-viva
            V3 hT = V3.Cross(rPreUt, vAfter);

            double m = IgnitionMass, propellant = 0.0, applied = 0.0;
            V3 prevDir = V3.zero;
            int n = (int)Math.Round(tBurn / StepSeconds) + 8, steps = 0;
            bool reversed = false; double minDot = 1.0, remEnd = 0.0;
            for (int i = 0; i < n; i++)
            {
                DVector3 rD = r, vD = v;
                DVector3 hDir = useVectorPlane ? hT.normalized : DVector3.Cross(rD, vD).normalized;
                if (!TrimBurn.TryPlan(rD, vD, Mu, aT, hT.magnitude, hDir,
                        out DVector3 def, out _, out _)) break;
                double rem = def.magnitude;
                remEnd = rem;
                // 判据（地板 / 反向收手）与游戏**同一份**：`BurnStep.Deficit`
                BurnStep.Deficit(def, prevDir, floor, out DVector3 d2, out double rem2, out bool rev);
                if (rev) { reversed = true; break; }
                if (!(rem2 > 0.0)) break;
                V3 dir = d2;
                if (prevDir.sqrMagnitude > 0.0)
                    minDot = Math.Min(minDot, V3.Dot(dir, prevDir.normalized));
                double aFull = Thrust / m;
                double dvFull = aFull * StepSeconds;
                double burnDv = Math.Min(aCmd * StepSeconds, rem);
                v += dir * burnDv;
                double share = dvFull > 0.0 ? burnDv / dvFull : 0.0;
                propellant += MassFlow * StepSeconds * share;
                m -= MassFlow * StepSeconds * share;
                applied += burnDv; steps++; prevDir = dir;
                (r, v) = MechJebLib.TwoBody.Shepperd.Solve(Mu, StepSeconds, r, v);
            }
            double endUt = startUt + steps * StepSeconds;
            return new LiveExec(OrbitView.FromStateVectors(r, v, Mu, endUt, new V3(0, 0, 1), R0 * 0.2),
                applied, propellant, remEnd, steps, reversed, minDot);
        }

        /// <summary>把"计划"构造成一条**纯平面改变**：目标 = 同半径、同形状、倾角差 Δi 的圆轨道。
        /// 标量 `(a, |h|)` 靶在这种几何上差额恒 0 ⇒ 一步不烧；向量靶必须把整段 Δv 交付掉。
        /// ⚠ **`RelativeInclination` 的单位是弧度**（= `SafeAcos(法向点积)`），不是度 ——
        ///   我第一版按度写了判据，于是"5° 的完整交付（0.0873 rad）"被读成"几乎没动" ✗。
        ///   返回值里连**意图轨道**一起给，判据一律打"与**意图**的夹角"（不是"与初始的夹角"）。</summary>
        private static (OrbitView o, double nodeUt, V3 dv, OrbitView intended) PlaneChangeCase(double incDeg)
        {
            double vc = Math.Sqrt(Mu / R0);
            var o = OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, vc, 0), Mu, 0.0,
                new V3(0, 0, 1), R0 * 0.2);
            double nodeUt = 300.0;
            (V3 r0, V3 v0) = o.RightHandedStateVectorsAtUT(nodeUt);
            double di = incDeg * Math.PI / 180.0;
            // 绕节点半径方向把速度转 Δi（纯平面改变：|v| 不变、径向分量不变 ⇒ a、|h| 都不变）
            V3 rhat = r0.normalized;
            double nr = V3.Dot(v0, rhat);
            V3 vin = (v0 - rhat * nr).normalized;
            V3 perp = V3.Cross(rhat, vin);
            V3 vin2 = (vin * Math.Cos(di) + perp * Math.Sin(di)).normalized;
            V3 vAfter = vin2 * (v0 - rhat * nr).magnitude + rhat * nr;
            var intended = OrbitView.FromStateVectors(r0, vAfter, Mu, nodeUt, new V3(0, 0, 1), R0 * 0.2);
            return (o, nodeUt, vAfter - v0, intended);
        }

        /// <summary>
        /// ★★ **游戏日志证伪的那条几何**（2026-09-20，G2 交会那轮）：靶近圆（e_t≈0.023）、
        /// **本点半径在靶近拱点之下**。旧"退化支"（保形状、重标 `a = r/(1±e)`）在这条几何上**极不连续**：
        /// 本点低 0.3 km ⇒ 靶的 a 被拉走 258.8 km（`(1−e)` vs `(1+e)` 差 `2e`）⇒ 靶跟着本点跑 ⇒
        /// 燃烧**在别的轨道上提前收工**（实测 164.9 / 430.01 m/s）。
        /// 本节把这台仪器也留下：`rescaleOld=true` = 复现被删的旧政策（对照），`false` = 现行"保能量、不重标 a"。
        /// </summary>
        private static LiveExec ExecuteTargetDeficitWithPolicy(OrbitView o, double nodeUt, V3 dv,
            bool rescaleOld, double floor = 1e-3)
        {
            double tBurn = dv.magnitude * IgnitionMass / Thrust;
            double aCmd = dv.magnitude / tBurn;
            double startUt = nodeUt - 0.5 * tBurn;
            (V3 r, V3 v) = o.RightHandedStateVectorsAtUT(startUt);
            (V3 rPreUt, V3 vPreUt) = o.RightHandedStateVectorsAtUT(nodeUt);
            V3 vAfter = vPreUt + dv;
            double aT = 1.0 / (2.0 / rPreUt.magnitude - vAfter.sqrMagnitude / Mu);
            V3 hT = V3.Cross(rPreUt, vAfter);

            double m = IgnitionMass, propellant = 0.0, applied = 0.0;
            V3 prevDir = V3.zero;
            int n = (int)Math.Round(tBurn / StepSeconds) + 8, steps = 0;
            bool reversed = false; double minDot = 1.0, remEnd = 0.0;
            for (int i = 0; i < n; i++)
            {
                DVector3 rD = r, vD = v;
                DVector3 def;
                if (!rescaleOld)
                {
                    if (!TrimBurn.TryPlan(rD, vD, Mu, aT, hT.magnitude, hT.normalized,
                            out def, out _, out _)) break;
                }
                else
                {
                    // ── 复现**已删**的旧政策（只为对照）：保形状、按"本点在靶曲线外"重标 a ──
                    V3 hvecOld = V3.Cross(r, v);
                    double hOverMuA = Math.Min(1.0, hT.sqrMagnitude / (Mu * aT));
                    double ecc = Math.Sqrt(Math.Max(0.0, 1.0 - hOverMuA));
                    double rm = r.magnitude;
                    double aUse = rm < aT * (1.0 - ecc) - 1e-6 ? rm / (1.0 + ecc)
                        : (rm > aT * (1.0 + ecc) + 1e-6 ? rm / (1.0 - ecc) : aT);
                    double hUse = Math.Sqrt(Mu * aUse * (1.0 - ecc * ecc));
                    double vtO = Math.Sqrt(Mu * (2.0 / rm - 1.0 / aUse));
                    double cgO = Math.Min(1.0, hUse / (rm * vtO));
                    V3 rhat = r.normalized;
                    V3 hhat = hvecOld.sqrMagnitude > 0 ? hvecOld.normalized : new V3(0, 0, 1);
                    V3 that = V3.Cross(hhat, rhat);
                    double vrO = V3.Dot(v, rhat);
                    double gO = Math.Acos(cgO) * (vrO < 0.0 ? -1.0 : 1.0);
                    def = (that * Math.Cos(gO) + rhat * Math.Sin(gO)) * vtO - v;
                }
                double rem = def.magnitude;
                remEnd = rem;
                BurnStep.Deficit(def, prevDir, floor, out DVector3 d2, out double rem2, out bool rev);
                if (rev) { reversed = true; break; }
                if (!(rem2 > 0.0)) break;
                V3 dir = d2;
                if (prevDir.sqrMagnitude > 0.0)
                    minDot = Math.Min(minDot, V3.Dot(dir, prevDir.normalized));
                double aFull = Thrust / m;
                double dvFull = aFull * StepSeconds;
                double burnDv = Math.Min(aCmd * StepSeconds, rem);
                v += dir * burnDv;
                double share = dvFull > 0.0 ? burnDv / dvFull : 0.0;
                propellant += MassFlow * StepSeconds * share;
                m -= MassFlow * StepSeconds * share;
                applied += burnDv; steps++; prevDir = dir;
                (r, v) = MechJebLib.TwoBody.Shepperd.Solve(Mu, StepSeconds, r, v);
            }
            double endUt = startUt + steps * StepSeconds;
            return new LiveExec(OrbitView.FromStateVectors(r, v, Mu, endUt, new V3(0, 0, 1), R0 * 0.2),
                applied, propellant, remEnd, steps, reversed, minDot);
        }

        /// <summary>
        /// ★★ **游戏日志证伪的那条政策**（2026-09-20，G2 那轮）：出范围时旧写法"保形状、把 a 重标到
        /// 本点当拱点"（`a = r/(1±e)`）。日志读数：本点半径只比靶近拱点低 **0.3 km**，靶的 a 被拉走 **258.8 km**
        /// ⇒ **"最小幅度重标"这个假设在近圆靶上不成立**（近圆时 `(1−e)` 与 `(1+e)` 相差 `2e`），
        /// 而且靶跟着本点跑（自证式）⇒ 燃烧会在**别的轨道**上收工（实测 430.01 计划只交付 164.9）。
        ///
        /// 本节只判**政策本身**（可判定、不依赖场景巧合）：同一个本点，两种政策给出的**目标 a** 差多少。
        /// ⚠ 为什么不做"燃烧对照"：要复现"提前收工"还需要一个巧合（船自己的 a 恰好≈被重标的靶 a，
        ///   日志那轮就是这样：5490.6 vs 5449）；我试了三种合成几何，只要船在燃烧中**回到范围内**，
        ///   精确支就会接管并把结果拉回意图 ⇒ 对照分不出好坏 ✗。**判政策比判场景可靠**。
        /// </summary>
        [Fact]
        public void Bench_OutOfRangePolicy_RescaleIsNotMinimal()
        {
            // 日志原值：意图 a_t=5751.4 km、e_t=0.022982（近拱点 5619.2 km）；本点 r=5618.9 km（低 0.3 km）
            const double aT = 5_751_400.0, eT = 0.022982;
            double rp = aT * (1.0 - eT);
            double rPoint = rp - 300.0;
            double rescaleA = rPoint / (1.0 + eT);          // 旧政策的目标 a（本点当近拱点）
            double radialGap = rp - rPoint;                 // 0.3 km
            double aShift = aT - rescaleA;                  // 258.8 km

            var sb = new StringBuilder();
            sb.Append("\n══ 出范围政策：旧「重标 a」（已删）vs 现行「保能量」══");
            sb.Append("\n靶 a_t=").Append((aT / 1000.0).ToString("F1")).Append(" km　e_t=")
              .Append(eT.ToString("F5")).Append("　近拱点=").Append((rp / 1000.0).ToString("F1")).Append(" km");
            sb.Append("\n本点 r=").Append((rPoint / 1000.0).ToString("F1")).Append(" km（比近拱点低 ")
              .Append((radialGap / 1000.0).ToString("F2")).Append(" km）");
            sb.Append("\n旧政策的目标 a = r/(1+e) = ").Append((rescaleA / 1000.0).ToString("F1"))
              .Append(" km ⇒ **偏移 ").Append((aShift / 1000.0).ToString("F1")).Append(" km**（= 径向缺口的 ")
              .Append((aShift / radialGap).ToString("F0")).Append(" 倍）");
            sb.Append("\n现行政策的目标 a = a_t = ").Append((aT / 1000.0).ToString("F1")).Append(" km（不动）");
            _out.WriteLine(sb.ToString());

            // ① 旧政策的偏移相对于"缺口"是**荒谬**的（近圆靶：偏移 ≈ 2e·a，与缺口无关）
            Assert.True(aShift > 100_000.0,
                $"旧政策本该把 a 拉走 >100 km：实测 {aShift / 1000.0:F1} km");
            Assert.True(aShift / radialGap > 100.0,
                $"偏移/缺口 应是个大数（说明最小幅度不成立）：{aShift / radialGap:F0}");
            // ② 现政策在**同一本点**上：目标 a 就是意图的 a（不存在重标）
            DVector3 rD = new DVector3(rPoint, 0, 0);
            DVector3 vD = new DVector3(0, Math.Sqrt(Mu / rPoint), 0);               // 本点：近圆速度
            double hT = Math.Sqrt(Mu * aT * (1.0 - eT * eT));
            Assert.True(TrimBurn.TryPlan(rD, vD, Mu, aT, hT, new DVector3(0, 0, 1),
                    out DVector3 dv, out string why, out double aUsed), why);
            Assert.Equal(aT, aUsed, 6);                                            // 目标 a 不被重标
            // ③ 出范围支应只改幅值（方向沿当前速度）—— 判别式：Δv 与 v 平行
            double cross = DVector3.Cross(dv, vD).magnitude / (dv.magnitude * vD.magnitude);
            Assert.True(cross < 1e-12, $"出范围支应沿当前速度方向改幅值：sin∠={cross:E3}");
            _ = eT;
        }

        [Fact]
        public void Bench_OutOfRangeTarget_EnergyOnlyDoesNotStopEarly()
        {
            // ── 几何取自**游戏日志自洽的那套数**，但意图轨道**由计划推出**（游戏 `trimTargetA/H` 就是这么算的：
            //    `SemiMajorAxis(r_pre(UT), v_pre(UT)+Δv)` ⇒ 意图 = "计划那一发真会把我放到哪条轨道上"）──
            // ⚠ 我第一版把意图**硬编**成日志的 (5751.4 km, 0.023)，而喂的计划只有 99.2 m/s ⇒ **两者不自洽**
            //   ⇒ 三条律（含忠实的冻结律）都落在 5635 km，判据全红 ✗ —— 是**我的仪器**错（不是律）。
            // 构造：船在节点前**向近拱点下降**（半径递减）⇒ 燃烧前半段 `r < 意图的近拱点` ⇒ 出范围支在跑 ✓
            const double planDv = 430.0;            // 日志原值（G2 那轮的计划 Δv）
            double rShip = 5_574_000.0;
            double vc = Math.Sqrt(Mu / rShip);
            // 带径向分量 ⇒ 半径变化：**上升**（向外 5%）⇒ 节点前 `r < 节点半径 = 意图近拱点` ⇒ **出范围** ✓
            var chaser = OrbitView.FromStateVectors(new V3(rShip, 0, 0),
                new V3(+0.05 * vc, vc, 0), Mu, 0.0, new V3(0, 0, 1), R0 * 0.2);
            double nodeUt = 432.0;                  // = t_b/2（燃烧居中）
            (V3 rNode, V3 vNode) = chaser.RightHandedStateVectorsAtUT(nodeUt);
            V3 dv = vNode.normalized * planDv;      // 顺行 430 m/s ⇒ 意图以节点为本点近拱点
            V3 vAfter = vNode + dv;
            double aT = 1.0 / (2.0 / rNode.magnitude - vAfter.sqrMagnitude / Mu);
            double eT = Math.Sqrt(Math.Max(0.0, 1.0 - V3.Cross(rNode, vAfter).sqrMagnitude / (Mu * aT)));

            var oldWay = ExecuteTargetDeficitWithPolicy(chaser, nodeUt, dv, true);
            var newWay = ExecuteTargetDeficitWithPolicy(chaser, nodeUt, dv, false);
            var frozen = ExecuteConstA(chaser, nodeUt, dv, 0);

            // 判据一律打"**与意图轨道**的夹角"（不是与初始）：smaErr 用意图的 a_t
            double smaOld = Math.Abs(oldWay.final.semiMajorAxis - aT) / aT;
            double smaNew = Math.Abs(newWay.final.semiMajorAxis - aT) / aT;
            double smaFrozen = Math.Abs(frozen.final.semiMajorAxis - aT) / aT;
            var sb = new StringBuilder();
            sb.Append("\n══ 出范围靶（游戏日志那条几何）：重标 a（已删）vs 保能量（现行）══");
            sb.Append("\n构造：船在节点前向近拱点下降（半径递减）⇒ 燃烧前半段在意图范围**之下**");
            sb.Append("\n意图轨道（**由计划推出**）：a_t=").Append((aT / 1000.0).ToString("F1")).Append(" km e_t=")
              .Append(eT.ToString("F5")).Append(" ⇒ 径向范围 [")
              .Append((aT * (1 - eT) / 1000.0).ToString("F1")).Append(", ")
              .Append((aT * (1 + eT) / 1000.0).ToString("F1")).Append("] km");
            sb.Append("\n计划 |Δv|=").Append(dv.magnitude.ToString("F1")).Append(" m/s");
            sb.Append("\n重标 a（旧，已删）：交付 ").Append(oldWay.appliedDv.ToString("F1"))
              .Append(" m/s（").Append((oldWay.appliedDv / dv.magnitude).ToString("F3"))
              .Append(" 倍计划）　步数 ").Append(oldWay.steps)
              .Append("　a 落 ").Append((oldWay.final.semiMajorAxis / 1000.0).ToString("F1"))
              .Append(" km（**smaErr=").Append(smaOld.ToString("E2")).Append("**）");
            sb.Append("\n保能量（现行）：交付 ").Append(newWay.appliedDv.ToString("F1"))
              .Append(" m/s（").Append((newWay.appliedDv / dv.magnitude).ToString("F3"))
              .Append(" 倍计划）　步数 ").Append(newWay.steps)
              .Append("　a 落 ").Append((newWay.final.semiMajorAxis / 1000.0).ToString("F1"))
              .Append(" km（smaErr=").Append(smaNew.ToString("E2")).Append("）");
            sb.Append("\n冻结律（改前那套，对照）：交付 ").Append(frozen.appliedDv.ToString("F1"))
              .Append(" m/s　a 落 ").Append((frozen.final.semiMajorAxis / 1000.0).ToString("F1"))
              .Append(" km（smaErr=").Append(smaFrozen.ToString("E2")).Append("）");
            _out.WriteLine(sb.ToString());

            // ① 现行政策达标：a 落到**由计划推出**的意图（验收线 1%），且不差于忠实交付计划的冻结律
            Assert.True(smaNew < 0.01, $"保能量支应把 a 落到意图 1% 内：smaErr={smaNew:E2}");
            Assert.True(smaNew < smaFrozen * 3.0,
                $"保能量支不该比冻结律差：{smaNew:E2} vs {smaFrozen:E2}");
            // ② 交付量与计划同量级（不许像日志那轮只交付 38% 就收工）
            Assert.True(newWay.appliedDv > dv.magnitude * 0.8 && newWay.appliedDv < dv.magnitude * 1.3,
                $"交付量应与计划同量级：{newWay.appliedDv:F1} vs {dv.magnitude:F1} m/s");
            // ⚠ 本节**不判**旧政策（重标）—— 见 `Bench_OutOfRangePolicy_RescaleIsNotMinimal` 的说明：
            //   只要船在燃烧中回到范围内，精确支就接管并把结果拉回意图 ⇒ 燃烧对照分不出好坏 ✗。
        }

        [Fact]
        public void Bench_LiveTargetDeficit_ZeroesOrbitAndDeliversPlaneChange()
        {
            var o = PreBurn();
            double nodeUt = NodeSeconds;
            (V3 dv, _, _, _) = Plan(o, nodeUt);

            var vec = ExecuteLiveTargetDeficit(o, nodeUt, dv, 1e-3, true);
            var ttVec = TerminalTruth(o, nodeUt, dv, vec.final);
            var scal = ExecuteLiveTargetDeficit(o, nodeUt, dv, 1e-3, false);
            var frozen = ExecuteConstA(o, nodeUt, dv, 0);
            var ttFrozen = TerminalTruth(o, nodeUt, dv, frozen.final);

            var sb = new StringBuilder();
            sb.Append("\n══ 靶之争（同一份原语 `TrimBurn.TryPlan`，只换靶的形态）══");
            // ⚠ **必须连 a 的误差一起读**（验收判据是 `e < 1e-3 && smaErr < 1%` 两条）：
            //   退化支的取舍是"修 e、让 a 落在**本点半径**"（e=0 蕴含 a=r，两者不可兼得）
            //   ⇒ 只看 e 会把"e 极好但 a 跑掉"读成完胜 ✗（这正是本类注释里那条已知取舍）。
            (V3 _, V3 vPreUt) = o.RightHandedStateVectorsAtUT(nodeUt);
            double aT = 1.0 / (2.0 / o.RightHandedStateVectorsAtUT(nodeUt).Item1.magnitude
                - (vPreUt + dv).sqrMagnitude / Mu);
            double smaFrozen = Math.Abs(frozen.final.semiMajorAxis - aT) / aT;
            double smaVec = Math.Abs(vec.final.semiMajorAxis - aT) / aT;
            sb.Append("\n冻结律（现行）：e=").Append(frozen.final.eccentricity.ToString("E3"))
              .Append("　滚转 ").Append(ttFrozen.rollDeg.ToString("F4"))
              .Append("°　smaErr=").Append(smaFrozen.ToString("E3"));
            sb.Append("\n共面标量靶 (a,|h|)：e=").Append(scal.final.eccentricity.ToString("E3"))
              .Append("　smaErr=").Append((Math.Abs(scal.final.semiMajorAxis - aT) / aT).ToString("E3"))
              .Append("　步数 ").Append(scal.steps);
            sb.Append("\n意图轨道靶 (a,h向量)：e=").Append(vec.final.eccentricity.ToString("E3"))
              .Append("　滚转 ").Append(ttVec.rollDeg.ToString("F4"))
              .Append("°　smaErr=").Append(smaVec.ToString("E3"))
              .Append("　步数 ").Append(vec.steps)
              .Append("　终态差额 ").Append(vec.residAtUt.ToString("E3")).Append(" m/s");

            // ── 纯平面改变（Δi = 5°）：标量靶一步不烧，向量靶必须交付 ──
            var (po, pUt, pdv, pIntended) = PlaneChangeCase(5.0);
            var pScal = ExecuteLiveTargetDeficit(po, pUt, pdv, 1e-3, false);
            var pVec = ExecuteLiveTargetDeficit(po, pUt, pdv, 1e-3, true);
            double remScal = pIntended.RelativeInclination(pScal.final);   // 与**意图**的夹角（rad）
            double remVec = pIntended.RelativeInclination(pVec.final);
            sb.Append("\n\n纯平面改变（Δi = ").Append((5.0 * Math.PI / 180.0).ToString("F4"))
              .Append(" rad = 5°，计划 |Δv|=").Append(pdv.magnitude.ToString("F2")).Append(" m/s）：");
            sb.Append("\n  共面标量靶：**与意图的**剩余倾角 ").Append(remScal.ToString("F4"))
              .Append(" rad = ").Append((remScal * 180.0 / Math.PI).ToString("F3"))
              .Append("°　交付 ").Append(pScal.appliedDv.ToString("F3")).Append(" m/s　步数 ").Append(pScal.steps);
            sb.Append("\n  意图轨道靶：**与意图的**剩余倾角 ").Append(remVec.ToString("E3"))
              .Append(" rad = ").Append((remVec * 180.0 / Math.PI).ToString("F5"))
              .Append("°　交付 ").Append(pVec.appliedDv.ToString("F3")).Append(" m/s　步数 ").Append(pVec.steps);
            _out.WriteLine(sb.ToString());

            // ① 主判据：意图轨道靶把轨道误差按构造归零（比冻结律低 ≥2 个数量级）
            Assert.True(vec.final.eccentricity < frozen.final.eccentricity * 0.01,
                $"意图轨道靶应显著优于冻结律：{vec.final.eccentricity:E3} vs {frozen.final.eccentricity:E3}");
            Assert.True(vec.final.eccentricity < 1e-5,
                $"意图轨道靶应把 e 压到 1e-5 以下：e={vec.final.eccentricity:E3}");
            // ①b **a 的代价**（退化支的已知取舍：e=0 蕴含 a=r_now）：验收判据另一条是 smaErr < 1%
            Assert.True(smaVec < 0.01,
                $"意图轨道靶的 |Δa|/a 必须过验收线 1%：{smaVec:E3}（本点半径与 a_t 的差）");
            // ② **平面必须用向量靶**（定义性判据：这不是"更好"，是"能不能"）
            Assert.True(pScal.steps == 0, $"标量靶在纯平面改变上应一步不烧（差额恒 0），实测步数 {pScal.steps}");
            Assert.True(pVec.steps > 0, "向量靶必须执行平面改变");
            Assert.True(remScal > 0.08, $"标量靶应与意图差整整一段 Δi（rad）：{remScal:F4}");
            Assert.True(remVec < 1e-3, $"向量靶应与意图对齐到 1e-3 rad 内：{remVec:E3}");
            Assert.True(remVec < remScal * 0.01,
                $"向量靶应把剩余倾角压到标量靶的 1% 以下：{remVec:E3} vs {remScal:F4} rad");
        }

        [Fact]
        public void DeficitToTargetV_DefinitionAspects()
        {
            var floor = 1e-3;
            // ⚠ `prevDir` 在**首步**是点火方向（= 计划 Δv 的方向）—— 判据用"上一步施加的方向"，
            //   传一个与之反向的向量会把正常差额判成"超调"（我第一版就传了 (1,0,0) ⇒ 全额差额被判反向 ✗）。
            var dv = new DVector3(-363.0, -719.0, 0.0);
            var prev = dv / dv.magnitude;

            // ① 点火那一刻：差额 == 计划 Δv（逐位）—— 律在 t=0 退化为计划
            BurnStep.DeficitToTargetV(new DVector3(0, 0, 0), dv, prev, floor, out var d0, out var r0, out var rev0);
            Assert.Equal(dv.magnitude, r0, 12);
            Assert.False(rev0);
            Assert.True(V3.Distance(new V3(d0.x, d0.y, d0.z), new V3(dv.x, dv.y, dv.z).normalized) < 1e-15,
                "方向应为差额方向");

            // ② 达标（含地板内）⇒ 无方向、不反向、剩余 0
            BurnStep.DeficitToTargetV(new DVector3(10, 0, 0), new DVector3(10.0000005, 0, 0), prev, floor,
                out var d1, out var r1, out var rev1);
            Assert.Equal(0.0, r1);
            Assert.Equal(DVector3.zero, d1);
            Assert.False(rev1);

            // ③ 反向（超调）⇒ 剩余 0、reversed、不产出方向
            //    （这里 `prev` **必须**与差额反向：上一步往 +x 推、而现在该往 −x ⇒ 超调 ⇒ 收手）
            BurnStep.DeficitToTargetV(new DVector3(10, 0, 0), new DVector3(9, 0, 0), new DVector3(1, 0, 0), floor,
                out var d2, out var r2, out var rev2);
            Assert.True(rev2);
            Assert.Equal(0.0, r2);
            Assert.Equal(DVector3.zero, d2);

            // ④ **帧无关**：三个输入同时绕 z 转 30° ⇒ 方向跟着转（游戏系/适配层系同一份算式的前提）
            double c = Math.Cos(30.0 * Math.PI / 180.0), s = Math.Sin(30.0 * Math.PI / 180.0);
            var liveRot = new DVector3(10.0 * c, 10.0 * s, 0);
            var tgtRot = new DVector3(9.0 * c, 9.0 * s, 0);
            var prevRot = new DVector3(-c, -s, 0);   // 与差额同向（差额 = −1·(c,s,0)）
            BurnStep.DeficitToTargetV(liveRot, tgtRot, prevRot, floor, out var d3, out var r3, out var rev3);
            Assert.False(rev3);
            Assert.True(Math.Abs(r3 - 1.0) < 1e-12, $"旋转不改变差额模长：{r3}");
            double ang = Math.Atan2(d3.y, d3.x);
            Assert.True(Math.Abs(ang * 180.0 / Math.PI - 210.0) < 1e-9
                || Math.Abs(ang * 180.0 / Math.PI + 150.0) < 1e-9,
                $"方向应与输入同系旋转：ang={ang * 180.0 / Math.PI:F6}°");
        }
    }
}
