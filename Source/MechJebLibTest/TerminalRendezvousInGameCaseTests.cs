using System;
using System.Text;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using PioneerFramework.Pure.Tests;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **冻结几何：2026-10-01 游戏内交会成功那局（地球·低推力）的回归判据**（B「终端律收尾」）。
    ///
    /// ## 为什么建它
    /// 地球·低推力那条曾 **58 次 `BLOCKED` 烧干**（§10.30）；终端律 + "速度待增量"输出落地后
    /// 游戏内交会成功（§10.37）：切入 dist=55,248 m / 相对速度 48.06 m/s（a=0.670 m/s²），
    /// 收到 **2.0 km / 4.9 m/s**、`BLOCKED` 0 次，判据仍是用户裁定的 2 km（没有放宽）。
    /// 离线台架此前**只能测"门内初值 → 300 m/2 m/s"**（`TerminalRendezvousLawTests`，执行器是每步节流、
    /// 无死区/无节拍）⇒ **游戏侧通道政策带来的失败形态看不见**（§10.33 那版"指令被 2 m/s 死区吃掉、
    /// 一发没烧出去"就是这么漏掉的）。本文件把**那一局的切入几何**冻结成判据，并把**通道政策**建进模型。
    ///
    /// ## 冻结量（全部取自游戏日志/源码，**不按台架实测反推**）
    /// | 量 | 值 | 出处 |
    /// |---|---|---|
    /// | 切入距离 / 相对速度 | 55,248 m / 48.06 m/s | §10.37 日志 |
    /// | 规划时加速 | a = 0.670 m/s² | 同上（`EstimateMaxAcceleration`，含 RCS 的总推力/质量） |
    /// | 通道节拍 | 10 s | `GuidanceTerminalCheckInterval`（既有终端闭环节拍） |
    /// | 修正烧死区 | 2.0 m/s | `GuidanceBurnDeadband` |
    /// | 单发上限 | 10 m/s | `TerminalRendezvousLaw.Params.MaxDvPerTick`（默认） |
    /// | 完成判据 | dist ≤ 2,000 m 且 rel ≤ 10 m/s | `rendezvousArriveDist`/`rendezvousArriveRelVel`（2026-10-01 用户裁定） |
    ///
    /// ## ⚠ 方向不可考
    /// 日志只给**模长**（55,248 m / 48.06 m/s），没给方向 ⇒ 取**沿迹追赶**代表几何
    /// （追方在目标后方 55,248 m、以 +48.06 m/s 沿迹速度追赶）。**换方向必须重登记本判据**。
    ///
    /// ## 判据（事先登记）
    ///   ① **速度待增量臂**：必须在 `TimeCap` 内达到用户判据（2 km / 10 m/s）；
    ///   ② **不烧干**：终端总 Δv ≤ `DvGuard`（对照：修复前那局烧干 ≈4900 m/s、58 次 `BLOCKED`；
    ///      本几何的理想账 ≈50–70 m/s ⇒ 护栏取到理想账的 ~5 倍仍是"量级护栏"，精细账进读数）；
    ///   ③ **反事实臂**（把输出换成修复前的"加速度形态"）**必须收不了口**
    ///      —— 实测：20,000 s 不收敛（最近 7.7 km / 60 m/s）且 Δv **8.6×**（1075 vs 124.6 m/s，
    ///      即"烧干"路的形态）⇒ 保证这条判据真能抓住那个病（不然台架是摆设）；
    ///   ④ 读数必须打出来（用时 / Δv / 发数 / 被死区吃掉的拍数）。
    ///
    /// ## ⚠ 台架保真度（**已登记的未建模项**，别当"已经同条件"）
    /// · **姿态与锥门**未建模（假设主推已对准、即时出力）——依据：现场那局 71 发修正全部
    ///   `correction burn started`（对准不是瓶颈）；
    /// · **换网**未建模（主推/RCS 不分，一律按 a=0.670 出力的有限燃烧）；
    /// · **耗料导致的 a 上升**未建模（常量 0.670）· 轨道保持/摄动未建模（两体）；
    /// · **阶梯（切入前的远场）不进本台架** —— 冻结的只是"切入 → 收口"这一段。
    /// </summary>
    public class TerminalRendezvousInGameCaseTests
    {
        private readonly ITestOutputHelper _out;
        public TerminalRendezvousInGameCaseTests(ITestOutputHelper o) => _out = o;

        private static readonly double Mu = OrbitMath.GM;
        private const double RTgt = 6_300_000.0;    // 地球 + 500 km（与 `SpawnRendezvousTest` 同）
        private const double Dt = 1.0;              // 台架步长（燃烧时长只有几秒量级 ⇒ 比物理步细）
        private const double Accel = 0.670;         // 规划时 aMax（§10.37 日志）
        private const double Cadence = 10.0;        // GuidanceTerminalCheckInterval
        private const double Deadband = 2.0;        // GuidanceBurnDeadband
        private const double Dist0 = 55_248.0;      // 切入距离（§10.37）
        private const double Rel0 = 48.06;          // 切入相对速度（§10.37）
        private const double GoalDist = 2_000.0;    // 用户裁定（rendezvousArriveDist）
        private const double GoalRel = 10.0;        // 用户裁定（rendezvousArriveRelVel）
        private const double TimeCap = 20_000.0;    // 与 `TerminalRendezvousLawTests` 同界（s）
        private const double DvGuard = 300.0;       // 不烧干护栏（m/s）

        private static readonly TerminalRendezvousLaw.Params P = new TerminalRendezvousLaw.Params();

        private readonly struct Outcome
        {
            public readonly bool Met;
            public readonly double T, Dv, MinDist, RelAtMin;
            public readonly int Burns, Dropped;
            public Outcome(bool met, double t, double dv, double minDist, double relAtMin, int burns, int dropped)
            { Met = met; T = t; Dv = dv; MinDist = minDist; RelAtMin = relAtMin; Burns = burns; Dropped = dropped; }
        }

        /// <summary>
        /// 跑一次"切入 → 收口"：**游戏侧通道政策**建模 = 10 s 节拍发令、|Δv| &lt; 死区不发、
        /// 烧的期间不发新令（原子烧）、燃烧按 a=0.670 有限加速摊开。
        /// </summary>
        /// <param name="speedToGo">true = 生产形态（"速度待增量"，`TerminalRendezvousLaw.ComputeDv`）；
        /// false = 反事实臂（修复前的"加速度形态" `aCmd·dt`，见 <see cref="AccelFormDv" />）。</param>
        private static Outcome Run(bool speedToGo)
        {
            double vc = Math.Sqrt(Mu / RTgt);
            DVector3 rT = new DVector3(RTgt, 0, 0);
            DVector3 vT = new DVector3(0, 0, -vc);
            DVector3 rHat0 = rT.normalized;
            DVector3 hHat0 = DVector3.Cross(rT, vT).normalized;
            DVector3 tHat0 = DVector3.Cross(hHat0, rHat0);

            // 沿迹追赶：追方在目标**后方** Dist0、沿迹速度**多** Rel0（RTN: pos=(0,−d,0)、vel=(0,+rel,0)）
            var ship = RendezvousShip.Spawn(rT - tHat0 * Dist0, vT + tHat0 * Rel0, tHat0);

            bool burning = false;
            DVector3 burnDir = DVector3.zero;
            double dvLeft = 0.0, lastCmd = double.NegativeInfinity;
            double dvTotal = 0.0, minDist = double.PositiveInfinity, relAtMin = 0.0;
            int burns = 0, dropped = 0;

            for (double t = 0.0; t <= TimeCap; t += Dt)
            {
                DVector3 rC = ship.Pos, vC = ship.Vel;
                DVector3 dPos = rC - rT, dVel = vC - vT;
                DVector3 rHat = rT.normalized;
                DVector3 hHat = DVector3.Cross(rT, vT).normalized;
                DVector3 tHat = DVector3.Cross(hHat, rHat);
                var relPosRtn = new DVector3(DVector3.Dot(dPos, rHat), DVector3.Dot(dPos, tHat), DVector3.Dot(dPos, hHat));
                var relVelRtn = new DVector3(DVector3.Dot(dVel, rHat), DVector3.Dot(dVel, tHat), DVector3.Dot(dVel, hHat));
                double dist = relPosRtn.magnitude, rel = relVelRtn.magnitude;
                if (dist < minDist) { minDist = dist; relAtMin = rel; }

                // 完成判据 = 游戏那一对（距离 + 相对速度**同时**满足；只判距离会在擦身而过时报假到达）
                if (dist <= GoalDist && rel <= GoalRel)
                    return new Outcome(true, t, dvTotal, minDist, relAtMin, burns, dropped);

                double n = Math.Sqrt(Mu / Math.Pow(rT.magnitude, 3.0));

                // ① 发令：不烧、且在节拍上（指令被死区吃掉也**照常推进节拍** —— 游戏侧 terminalNextTick 同此）
                if (!burning && t >= lastCmd + Cadence - 1e-9)
                {
                    lastCmd = t;
                    DVector3 dvRtn = speedToGo
                        ? TerminalRendezvousLaw.ComputeDv(relPosRtn, relVelRtn, n, Cadence, P)
                        : AccelFormDv(relPosRtn, relVelRtn, n, Cadence, P);
                    double mag = dvRtn.magnitude;
                    if (mag >= Deadband)
                    {
                        double give = Math.Min(mag, P.MaxDvPerTick);
                        DVector3 u = dvRtn.normalized;
                        DVector3 dvWorld = u.x * rHat + u.y * tHat + u.z * hHat;
                        burnDir = dvWorld.normalized;
                        dvLeft = give;
                        burning = true;
                        burns++;
                    }
                    else dropped++;
                }

                // ② 本步推力（有限燃烧；方向 = 发令时刻冻结的方向 = 游戏侧"固定惯性方向"）
                DVector3 thrustAcc = DVector3.zero;
                if (burning)
                {
                    double step = Math.Min(Accel * Dt, dvLeft);
                    if (step > 0.0)
                    {
                        thrustAcc = burnDir * (step / Dt);
                        dvLeft -= step;
                        dvTotal += step;
                    }
                    if (dvLeft <= 1e-12) burning = false;
                }

                // ③ 推进：追方 RK4（含推力，与 `RendezvousShip` 同一套）；目标解析（Shepperd）
                ship.Propagate(thrustAcc, Mu, Dt);
                (rT, vT) = Shepperd.Solve(Mu, Dt, rT, vT);
            }

            return new Outcome(false, TimeCap, dvTotal, minDist, relAtMin, burns, dropped);
        }

        /// <summary>
        /// **修复前的输出形态**（§10.37 第 2 点）：`aCmd·dt`（`aCmd = aDes − aNat`）。
        /// 实测（本文件反事实臂）：这一形态**每拍只给 1–3 m/s 量级**，追不上几何的演化
        /// —— 20,000 s 内**收不了口**（最近 7.7 km / 60 m/s）且 Δv 到 **8.6×**（1075 vs 124.6 m/s）
        /// ⇒ 就是"烧干"那条路的形态（§10.37 当场读到的是它的极端：指令 ~1 m/s、低于 2 m/s 死区 ⇒ 那一拍直接不发）。
        /// ⚠ 步①–③与 `TerminalRendezvousLaw.ComputeDv` **同式抄写** —— 生产侧已不存在这个形态，
        /// 只能抄；将来若改律的步①–③，这里要跟着核（本臂只是"旧形态能不能过死区"的模型）。
        /// </summary>
        private static DVector3 AccelFormDv(DVector3 relPosRtn, DVector3 relVelRtn, double n, double dt,
            TerminalRendezvousLaw.Params p)
        {
            double d = relPosRtn.magnitude;
            double tau = Math.Max(1e-3, p.TimeConstant);
            double tauV = Math.Max(1e-3, p.VelocityTimeConstant);

            DVector3 vDes = d > p.Deadband ? relPosRtn * (-1.0 / tau) : DVector3.zero;
            if (d > 0.0)
            {
                DVector3 uAway = relPosRtn / d;
                double vRadial = DVector3.Dot(vDes, uAway);
                if (-vRadial > p.ClosingSpeedCap)
                    vDes += uAway * (-p.ClosingSpeedCap - vRadial);
            }
            DVector3 aDes = (vDes - relVelRtn) / tauV;
            double nn = n * n;
            var aNat = new DVector3(
                3.0 * nn * relPosRtn.x + 2.0 * n * relVelRtn.y,
                -2.0 * n * relVelRtn.x,
                -nn * relPosRtn.z);
            return (aDes - aNat) * dt;
        }

        private void Print(string arm, Outcome r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"── {arm} ──");
            sb.AppendLine($"  切入：dist={Dist0:F0} m｜rel={Rel0:F2} m/s｜a={Accel:F3} m/s²");
            sb.AppendLine($"  通道：节拍={Cadence:F0}s｜死区={Deadband:F1} m/s｜单发上限={P.MaxDvPerTick:F0} m/s");
            sb.AppendLine($"  结果：{(r.Met ? $"✓ 达到判据 @ t={r.T:F0}s" : $"✗ 未达到（{TimeCap:F0}s 用尽）")}" +
                          $"｜Δv={r.Dv:F1} m/s｜发数={r.Burns}｜死区吃掉={r.Dropped}");
            sb.AppendLine($"  轨迹：最近距离={r.MinDist:F0} m（当时相对速度 {r.RelAtMin:F2} m/s）");
            sb.AppendLine($"  判据：dist ≤ {GoalDist:F0} m 且 rel ≤ {GoalRel:F1} m/s（用户 2026-10-01 裁定）" +
                          $"｜Δv 护栏 {DvGuard:F0} m/s（烧干那档 ≈4900）");
            _out.WriteLine(sb.ToString().TrimEnd());
        }

        [Fact]
        public void FrozenInGameGeometry_ReachesUserCriterion_UnderGameChannelPolicy()
        {
            var r = Run(speedToGo: true);
            Print("速度待增量臂（生产形态）", r);
            Assert.True(r.Met,
                $"冻结几何（{Dist0:F0} m / {Rel0:F2} m/s）在 {TimeCap:F0}s 内没达到用户判据" +
                $"（dist ≤ {GoalDist:F0} m 且 rel ≤ {GoalRel:F1} m/s）⇒ 交会终端回归");
            Assert.True(r.Dv <= DvGuard,
                $"终端 Δv {r.Dv:F1} m/s 超过护栏 {DvGuard:F0} m/s（烧干那档 ≈4900 m/s）⇒ 机制在退化");
        }

        [Fact]
        public void FrozenInGameGeometry_Counterfactual_AccelFormOutput_CannotReach()
        {
            var r = Run(speedToGo: false);
            Print("反事实臂（修复前的加速度形态输出）", r);
            Assert.False(r.Met,
                "反事实臂竟然收口了 —— 要么死区/节拍建模与游戏侧不符，要么律的输出形态已不再是" +
                "被测的那个（这条判据就抓不住旧病了）");
        }
    }
}
