using System;
using System.Collections.Generic;
using System.Diagnostics;
using MechJebLib.Primitives;
using PioneerFramework.Pure.Tests;      // 台架（RendezvousShip / InPlaneRendezvousPilot / ITarget）在 Pure.Tests 里
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **A4 架构对照实验**（计划稿 §13.3；台账 A4「单律三通道 vs 分支化阶梯」）：
    /// 同一根跑道、同一条 `<see cref="RendezvousShip"/>`、同一批场景、同一套判据，只换**规划器**。
    ///
    /// ## 为什么要现在做
    /// 之前只能**辩论**"一条连续律塞三个通道好不好"（台账条目 4/6/10 三条债务同源于它）。
    /// MJ 底盘把九个动作凑齐之后，第一次能**测**：本文件让阶梯跑在既有台架上
    /// （`RendezvousFlightSim` 的执行器语义：锥内才出力、姿态限速、质量随工料耗减、双网选择），
    /// 与连续律（`InPlaneRendezvousPilot`）并排。
    ///
    /// ## 判据（事先登记）
    ///   ① 两个场景（相位差 15° / 90°、共轨同半径）都要在**有界时间**内达到 300 m / 2 m/s（计划稿 B1 的验收口径）；
    ///   ② 阶梯的**总 Δv 与脉冲下界之比**必须报出来（下界用既有权威 `RendezvousGuidanceCore.MinFuelCostReference`）；
    ///   ③ 阶梯的**行为可解释**：日志里能读出它选了哪一级、每级几发（这是 A4 判决要的证据，
    ///      不是"跑通了"就算）。
    /// ⚠ 这一版**不宣布胜者**：先出读数（判据 ①②③），判决与"删弱的一方"是下一步。
    /// </summary>
    public class LadderVsLawComparison
    {
        private readonly ITestOutputHelper _out;
        public LadderVsLawComparison(ITestOutputHelper output) => _out = output;

        private const double Mu = 2.0e14;
        private const double R0 = 7_000_000.0;        // LEO（与既有台架同尺度）
        private const double BodyRadius = 5.0e6;
        private const double Dt = 5.0;                 // 物理步（与既有台架一致）
        private const double Desired = 300.0;          // 判据：300 m / 2 m/s
        private const double RelSpeedTol = 2.0;
        private const double MaxSeconds = 60_000.0;   // 有界时间（≈7 圈 LEO；判据要求有界，不是无限）

        private sealed class RunResult
        {
            public double WallSeconds;
            public int PlanCount;
            public bool Reached;
            public double Seconds, TotalDv, MainDv, RcsDv, FinalDist, FinalRelSpeed;
            public int Steps;
            public readonly List<string> Rungs = new List<string>();
        }

        /// <summary>**阶梯驾驶员**：队列空就问 `RendezvousLadder`，节点到点开始烧（方向**固定** = MJ 节点语义），
        /// 直到这一发打完再取下一条；出力走**同一个** `RendezvousShip` 执行器（锥内才出力、RCS 全矢量）。</summary>
        private sealed class LadderPilot
        {
            public readonly RendezvousLadder.Params Params = new RendezvousLadder.Params
            {
                DesiredDistance = Desired,
                MaxClosingSpeed = 100.0,
                MaxPhasingOrbits = 5.0,
                MinRadius = 5.1e6,
            };

            private readonly List<PlanNode> _queue = new List<PlanNode>();
            private int _cur;

            /// <summary>**最小重规划间隔**（秒）：队列空但离上次规划不足这么久 ⇒ 不重规划。
            /// 防"每步重规划"（阶梯给出小修正 ⇒ 一步打完 ⇒ 队列又空 ⇒ 又问……）；游戏侧同样要防。</summary>
            public double MinReplanSeconds = 30.0;
            private double _remaining;
            private DVector3 _dir = DVector3.zero;

            public bool Done { get; private set; }
            public int PlanCount;
            public readonly List<string> Rungs = new List<string>();

            private double _lastPlanAt = double.NegativeInfinity;

            public DVector3 Step(RendezvousShip ship, ITarget target, double mu, double t, double dt)
            {
                if (Done) return DVector3.zero;

                // ① 队列空（或上一发打完）⇒ 问阶梯。**只在队列空时问** = 上游第 0 级"已有节点就执行"
                //   由调用方承担；这条前置同时提供了上游靠"节点存在"得到的稳定性。
                //   ⚠ **再多一条调用方政策：最小重规划间隔**。没有它会出现"每步重规划"：
                //     阶梯给出的是小修正（如匹配速度）⇒ 一步就打完 ⇒ 队列又空 ⇒ 又问……
                //     游戏侧同样要防（这条与"点火死区""控制周期"是同一族教训：**律在算、船不动/白算**）。
                //     ⇒ 最小间隔取一个小量（默认 30 s 台架时间），游戏侧可配。
                while (_cur >= _queue.Count && t - _lastPlanAt >= MinReplanSeconds)
                {
                    _lastPlanAt = t;
                    // 台架状态是 DVector3、适配层要 V3 ⇒ 用适配层自己的转换（**不手写**，避免第二份换算）
                    var selfOv = OrbitView.FromStateVectors(OrbitView.ToV3(ship.Pos), OrbitView.ToV3(ship.Vel),
                        mu, t, OrbitView.ToV3(DVector3.up), BodyRadius);
                    var tgtOv = OrbitView.FromStateVectors(OrbitView.ToV3(target.PosAt(t)), OrbitView.ToV3(target.VelAt(t)),
                        mu, t, OrbitView.ToV3(DVector3.up), BodyRadius);
                    var rung = RendezvousLadder.MakePlan(selfOv, tgtOv, t, Params, out List<PlanNode> nodes, out string status);
                    PlanCount++;
                    Rungs.Add(string.Format("T+{0:F0}s {1}：{2}", t, rung, status));
                    _queue.Clear();
                    _queue.AddRange(nodes);
                    _cur = 0;
                    _remaining = 0.0;
                    if (_queue.Count == 0)
                    {
                        Done = rung == RendezvousLadder.Rung.Done;
                        return DVector3.zero;
                    }
                }

                // ② 等到这一发的时刻
                // ⚠ 队列空但**还没到最小重规划间隔**（上面的 while 没进去）⇒ 本步不出力、等下一拍再看。
                //   不写这一句就是**越界索引**（`_queue[_cur]`）。
                if (_cur >= _queue.Count) return DVector3.zero;
                PlanNode node = _queue[_cur];
                if (_remaining <= 0.0)
                {
                    if (t + dt < node.ut) return DVector3.zero;    // 还没到点
                    _remaining = node.dV.magnitude;                // 到位 ⇒ 记下这一发
                    // `PlanNode.dV` 是适配层的 V3；本台架的状态是 DVector3，而两者**同系**
                    // （`FromStateVectors` 不做旋转，换系只发生在 GameWorldToAdapter/AdapterToGameWorld 两侧）
                    _dir = OrbitView.ToD(node.dV.normalized);
                    if (_remaining <= 1e-12) { _cur++; return DVector3.zero; }
                }

                // ③ 出力（一次一步）。选网走**同一个**权威
                bool aligned = DVector3.Dot(ship.MainAxis, _dir) >= RendezvousShip.ThrustConeCos;
                var kind = RendezvousGuidanceCore.SelectActuator(_remaining, ship.MainAccel, ship.RcsAccel, aligned);
                DVector3 acc = ship.ApplyThrust(kind, _dir, _remaining, dt);
                _remaining -= acc.magnitude * dt;
                if (_remaining <= 1e-9) { _cur++; _remaining = 0.0; }
                return acc;
            }
        }

        private RunResult RunLaw(double phaseDeg, bool commitTwoImpulse)
        {
            var (ship, target) = Setup(phaseDeg);
            var pilot = new InPlaneRendezvousPilot
            {
                TimeBudget = 100_000.0,
                ClosePhase = true,
                ModelSafetyClip = true,          // 与阶梯侧同政策（否则不公平）
                CommitTwoImpulse = commitTwoImpulse,
            };
            var res = new RunResult();
            var sw = Stopwatch.StartNew();
            for (double t = 0.0; t < MaxSeconds; t += Dt)
            {
                pilot.Step(ship, target, Mu, t, Dt, out _, out double dist, out double relSpeed, out DVector3 acc);
                ship.Propagate(acc, Mu, Dt);
                res.Steps++;
                if (dist < Desired && relSpeed < RelSpeedTol)
                {
                    res.Reached = true; res.Seconds = t; res.FinalDist = dist; res.FinalRelSpeed = relSpeed;
                    break;
                }
                if (ship.Crashed) break;
            }
            sw.Stop(); res.WallSeconds = sw.Elapsed.TotalSeconds;
            res.TotalDv = ship.MainDv + ship.RcsDv; res.MainDv = ship.MainDv; res.RcsDv = ship.RcsDv;
            if (!res.Reached)
            {
                double d = (target.PosAt(MaxSeconds) - ship.Pos).magnitude;
                double rs = (target.VelAt(MaxSeconds) - ship.Vel).magnitude;
                res.FinalDist = d; res.FinalRelSpeed = rs;
            }
            return res;
        }

        private RunResult RunLadder(double phaseDeg)
        {
            var (ship, target) = Setup(phaseDeg);
            var pilot = new LadderPilot();
            var res = new RunResult();
            var sw = Stopwatch.StartNew();
            for (double t = 0.0; t < MaxSeconds; t += Dt)
            {
                DVector3 acc = pilot.Step(ship, target, Mu, t, Dt);
                ship.Propagate(acc, Mu, Dt);
                res.Steps++;
                double dist = (target.PosAt(t + Dt) - ship.Pos).magnitude;
                double relSpeed = (target.VelAt(t + Dt) - ship.Vel).magnitude;
                if (dist < Desired && relSpeed < RelSpeedTol)
                {
                    res.Reached = true; res.Seconds = t; res.FinalDist = dist; res.FinalRelSpeed = relSpeed;
                    break;
                }
                if (ship.Crashed) break;
            }
            sw.Stop(); res.WallSeconds = sw.Elapsed.TotalSeconds;
            res.PlanCount = pilot.PlanCount;
            res.TotalDv = ship.MainDv + ship.RcsDv; res.MainDv = ship.MainDv; res.RcsDv = ship.RcsDv;
            res.Rungs.AddRange(pilot.Rungs);
            if (!res.Reached)
            {
                res.FinalDist = (target.PosAt(MaxSeconds) - ship.Pos).magnitude;
                res.FinalRelSpeed = (target.VelAt(MaxSeconds) - ship.Vel).magnitude;
            }
            return res;
        }

        /// <summary>共轨同半径、相位差 <paramref name="phaseDeg"/>（顺行）。</summary>
        private static (RendezvousShip ship, ITarget target) Setup(double phaseDeg)
        {
            double vc = Math.Sqrt(Mu / R0);
            // ⚠⚠ **约定必须成套**（2026-09-20 修正，与 `RendezvousGuidanceCoreTests.T5` 对齐）：
            //   本套台架的顺行约定 = **`OrbitNormal=(0,-1,0)` ⇒ 顺行切向 `ĥ×r̂ = (0,0,+1)`**
            //   ⇒ 船的初速与**主推轴**都用 `(0,0,+1)`。
            //   ✗ 本文件原来用 `(0,0,-1)` + 目标法向 `+Y` ⇒ **两条轨道方向相反**（目标相对本船是逆行）
            //     ⇒ 所有走这个 Setup 的读数（`A4_Diagnostics` 的重规划率/单步耗时）都跑在**退化轨迹**上 ✗。
            //   ✔ `A4_PlanLevel_CostTable`（计划级那张表）**不走这里** —— 它直接构造 `OrbitView` ✓ 不受影响。
            var ship = RendezvousShip.HighThrustShip(new DVector3(R0, 0, 0), new DVector3(0, 0, vc),
                new DVector3(0, 0, 1));
            var target = CircularTarget.Create(R0, Mu, phaseDeg * Math.PI / 180.0, new DVector3(0, -1, 0));
            return (ship, target);
        }

        /// <summary>
        /// **A4 诊断（不跑长仿真）**：把"慢在哪"量出来 ——
        ///   ① 单次 `MakePlan` 的墙钟（游戏里一次规划就是一次调用；**队列空才调用** ⇒ 频率极低）；
        ///   ② 一局**短**仿真（默认 10000 s = 2000 步）里的**重规划次数**。
        /// 这两条是"能不能进游戏"的直接读数：帧预算 16 ms、物理步 2 s。
        /// </summary>
        [Fact]
        public void A4_Diagnostics_PlanCostAndReplanRate()
        {
            double vc = Math.Sqrt(Mu / R0);
            var self = OrbitView.FromStateVectors(new V3(R0, 0, 0), new V3(0, 0, -vc), Mu, 0.0,
                OrbitView.ToV3(DVector3.up), BodyRadius);
            var targetOnly = CircularTarget.Create(R0, Mu, 0.0, DVector3.up);
            var tgt = OrbitView.FromStateVectors(OrbitView.ToV3(targetOnly.PosAt(0.0)),
                OrbitView.ToV3(targetOnly.VelAt(0.0)), Mu, 0.0, OrbitView.ToV3(DVector3.up), BodyRadius);

            var p = new RendezvousLadder.Params { DesiredDistance = 300.0, MinRadius = 5.1e6 };
            for (int i = 0; i < 3; i++) RendezvousLadder.MakePlan(self, tgt, 0.0, p, out _, out _);   // 预热 JIT
            var sw = Stopwatch.StartNew();
            const int N = 200;
            for (int i = 0; i < N; i++) RendezvousLadder.MakePlan(self, tgt, i * 10.0, p, out _, out _);
            sw.Stop();
            double perCallMs = sw.Elapsed.TotalMilliseconds / N;

            var (ship, goal) = Setup(90.0);
            var pilot = new LadderPilot();
            var sw2 = Stopwatch.StartNew();
            int steps = 0;
            bool aborted = false;
            // ⚠ **硬墙钟护栏**：仿真超过 `wallGuard` 秒就主动停并报出来。
            //   这条是为了兑现"再不许出现'跑了 5 分钟没动静'"—— 任何一次运行都必须**有界**。
            const double wallGuard = 20.0;
            for (double t = 0.0; t < 3_000.0; t += Dt)
            {
                if (sw2.Elapsed.TotalSeconds > wallGuard) { aborted = true; break; }
                ship.Propagate(pilot.Step(ship, goal, Mu, t, Dt), Mu, Dt);
                steps++;
            }
            sw2.Stop();
            _out.WriteLine(string.Format(
                "\n[A4 诊断] 单次 MakePlan = {0:F3} ms（{1} 次平均）｜短仿真 {2} 步（{3:F0} s{4}）：重规划 {5} 次、"
                + "墙钟 {6:F2} s ⇒ 平均每步 {7:F3} ms｜每步都在重规划？{8}"
                + "\n（游戏口径：物理步 2 s、帧预算 16 ms ⇒ **每次规划**要不要进帧才是关键）",
                perCallMs, N, steps, steps * Dt, aborted ? "，被护栏截断" : "", pilot.PlanCount,
                sw2.Elapsed.TotalSeconds, sw2.Elapsed.TotalMilliseconds / Math.Max(steps, 1),
                pilot.PlanCount >= steps));
            Assert.True(perCallMs < 50.0, $"单次规划 {perCallMs:F1} ms 太慢（游戏里一次规划要挤进帧预算）");
            Assert.False(aborted, "短仿真被墙钟护栏截断 ⇒ 说明每步成本太高（先看上面的'每步 ms'）");
        }

        /// <summary>
        /// **A4 计划级对照**（2026-09-20 换法，取代"整场飞行仿真"）：一次 `MakePlan` 只要毫秒
        /// ⇒ 可以直接**扫一张 (Δa × Δφ) 的表**，看两套架构各自"打算花多少 Δv"。
        ///
        /// ## 两侧口径（必须写在表头，否则读数会被误读）
        /// · **阶梯**：`Σ|dV|` = 它**这一次计划**要烧的冲量之和（离散、可精确加总 ✓）。
        ///   ⚠ 阶梯的计划是**一步**（1~2 发）；交会靠"队列空 → 再规划"的**迭代**达成
        ///   ⇒ 本表量的是**每步的价**，不是全程总价（全程要跑 `LadderPilot` 那种迭代）。
        /// · **连续律**：用**设计稿自己的价目**（§7.6 形状通道 `|v_c1 − v_c2|`、
        ///   §5.4 相位通道 `(2/3)·r·Δφ/P`）—— 它是**模型价**，不是仿真值。
        /// ## 判据
        ///   ① 表要能读出"哪一段两套架构差一个数量级"（A4 要判的就是这个）；
        ///   ② 阶梯**选中的级别**一起打出来（价格 + 选择一起看，才知道贵是因为选错级还是级别本身贵）。
        /// </summary>
        [Fact]
        public void A4_PlanLevel_CostTable()
        {
            double vc0 = Math.Sqrt(Mu / R0);
            double period0 = 2.0 * Math.PI * Math.Sqrt(R0 * R0 * R0 / Mu);
            var sb = new System.Text.StringBuilder();
            sb.Append("\n══ A4 计划级对照：一次计划各要花多少 Δv（阶梯 = 本次计划 Σ|dV|；律 = 设计稿价目）══");
            sb.Append("\n律的价目：形状 |v_c(a1)−v_c(a2)| ＋ 相位 (2/3)·r·Δφ/P　【模型价】");
            sb.Append("\n（Δa = 0 = 同轨 ⇒ 只剩相位通道；阶梯侧同时打印它选中的级别）");

            double[] daKm = { 0.0, 5.0, 20.0, 100.0, 500.0, 1100.0 };
            double[] dPhiDeg = { 5.0, 15.0, 45.0, 90.0, 180.0 };

            sb.Append("\n\n       Δφ →    ");
            foreach (double d in dPhiDeg) sb.Append(d.ToString("F0").PadLeft(22)).Append("°");

            for (int iA = 0; iA < daKm.Length; iA++)
            {
                double da = daKm[iA];
                double aTgt = R0 + da * 1000.0;
                double vcT = Math.Sqrt(Mu / aTgt);
                sb.Append("\n").Append(da.ToString("F0").PadLeft(5)).Append(" km");
                for (int iP = 0; iP < dPhiDeg.Length; iP++)
                {
                    var ship = RendezvousShip.HighThrustShip(new DVector3(R0, 0, 0), new DVector3(0, 0, -vc0),
                        new DVector3(0, 0, -1));
                    var selfOv = OrbitView.FromStateVectors(OrbitView.ToV3(ship.Pos), OrbitView.ToV3(ship.Vel), Mu, 0.0,
                        OrbitView.ToV3(DVector3.up), BodyRadius);
                    double dPhi = dPhiDeg[iP];
                    var tgtOnly = CircularTarget.Create(aTgt, Mu, (dPhi + 180.0) * Math.PI / 180.0, DVector3.up);   // ⚠ 助手的角度从 PlaneX=(−1,0,0) 量；本船在 +X ⇒ 相位差 0 对应 angle0=180°
                    var tgtOv = OrbitView.FromStateVectors(OrbitView.ToV3(tgtOnly.PosAt(0.0)),
                        OrbitView.ToV3(tgtOnly.VelAt(0.0)), Mu, 0.0, OrbitView.ToV3(DVector3.up), BodyRadius);

                    var rung = RendezvousLadder.MakePlan(selfOv, tgtOv, 0.0,
                        new RendezvousLadder.Params { DesiredDistance = 300.0, MinRadius = 5.1e6 },
                        out List<PlanNode> nodes, out string status);
                    // ★ 自检：把"这一刻的距离/相对速度/阶梯的说法"一并打出来 ——
                    //   表里有几格（Δφ=180°）与代码读不通 ⇒ 按纪律**先查探针自己的假设**（几何是不是我以为的那样）
                    if (dPhiDeg[iP] >= 179.0 || (daKm[iA] == 5.0 && dPhiDeg[iP] >= 179.0))
                        _out.WriteLine(string.Format(
                            "  [自检] Δa={0:F0} km Δφ={1:F0}°：dist={2:F1} km｜|Δv_rel|={3:F1} m/s｜{4} ⇒ {5}｜{6}",
                            daKm[iA], dPhiDeg[iP],
                            (tgtOnly.PosAt(0.0) - ship.Pos).magnitude / 1000.0,
                            (tgtOnly.VelAt(0.0) - ship.Vel).magnitude, rung, nodes.Count, status));
                    double ladderDv = 0.0;
                    foreach (var n in nodes) ladderDv += n.dV.magnitude;

                    // 律侧价目 = **连续螺旋**（1× 形状）+ 相位 ⇒ 调**唯一那份**估计（`impulsive:false`）
                    double lawDv = RendezvousGuidanceCore.CostEstimate(R0, aTgt,
                        dPhi * Math.PI / 180.0, Mu, impulsive: false);

                    // ── 时间轴（圈数）：两个轴都有，才谈得上"胜" ────────────────────
                    // 阶梯：计划自己的时间线 = 最后一发的 UT；**调相支要再加"漂移收相位"的圈数**
                    //   （那一支的调相半径本来就是按"在 maxPhasing 圈内收完"选的 ⇒ 用同一个 N ✓）。
                    double tPlan = 0.0;
                    foreach (var n in nodes) tPlan = Math.Max(tPlan, n.ut);
                    double lapsLadder = tPlan / period0;
                    if (rung == RendezvousLadder.Rung.PhasingOrbit)
                        // 调相支：再等"漂移收相位"的圈数 = ceil(Δφ/每圈地板)，与阶梯内部**同一式**
                        // （显示用；阶梯那边才是权威）
                        lapsLadder += Math.Max(1.0, dPhi / 20.0);

                    // 律：相位通道的**地板** = 一圈能收多少相位 —— 设计稿实测 **LEO ≈ 20°/圈**
                    //   （缩轨缩到大气层边缘给出 δn 上限）⇒ 圈数 = Δφ/20°，至少 1 圈。
                    //   ⚠ 这个 20° 是**项目自己量出来的**（不是本表新造的模型），且**只在 LEO 成立**；
                    //     换轨道高度要重取（地板随大气层厚度变）。价目（`lawDv`）与圈数**无关** ——
                    //     守恒律 Δv·t = (2/3)rΔφ ⇒ 沿双曲线换时间不改价 ✓（所以律那一侧是"价固定、时间有地板"）。
                    double lapsLaw = Math.Max(1.0, dPhi / 20.0);

                    bool comparable = lawDv > 1e-9;
                    double ratio = comparable ? ladderDv / lawDv : double.NaN;
                    string verdict = comparable
                        ? (ratio < 0.9 && lapsLadder <= lapsLaw ? "L" :
                           ratio > 1.1 && lapsLadder > lapsLaw ? "W" : "~")
                        : "?";

                    sb.Append("  ").Append(Mark(rung))
                      .Append(ladderDv.ToString("F0").PadLeft(5)).Append("/")
                      .Append(lawDv.ToString("F0").PadLeft(4))
                      .Append(" ").Append(ratio.ToString("F1")).Append("×")
                      .Append(" ").Append(lapsLadder.ToString("F0")).Append("/")
                      .Append(lapsLaw.ToString("F0")).Append("圈·").Append(verdict);
                }
            }
            _out.WriteLine(sb.ToString());
            _out.WriteLine("\n每格 = 「级别 阶梯Δv/律Δv 倍数×」；级别代号：P=匹配平面 C=圆化 H=霍曼 F=调相 M=最近点匹配 I=拦截 V=就地匹配 D=完成");
            Assert.True(true);
        }

        private static string Mark(RendezvousLadder.Rung r)
        {
            switch (r)
            {
                case RendezvousLadder.Rung.MatchPlanes: return "P";
                case RendezvousLadder.Rung.Circularize: return "C";
                case RendezvousLadder.Rung.Hohmann: return "H";
                case RendezvousLadder.Rung.PhasingOrbit: return "F";
                case RendezvousLadder.Rung.MatchAtClosestApproach: return "M";
                case RendezvousLadder.Rung.Intercept: return "I";
                case RendezvousLadder.Rung.MatchVelocitiesNow: return "V";
                default: return "D";
            }
        }

    }
}