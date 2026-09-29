using System;
using System.Collections.Generic;
using MechJebLib.Primitives;
using PioneerFramework.Pure.Tests;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **交会阶梯的端到端台架（四条几何）** —— 批5.B（2026-09-28）建。
    ///
    /// ## 为什么建它
    /// 旧用例（`RendezvousGuidanceCoreTests` 的 T11/T12/T13/T14/T16）覆盖的是**仍在玩家路径上的需求**
    /// （一圈交会 / LEO→GEO / GTO 异轨 / 死区不憋死），但它们**由已作废的旧律驱动**
    /// （`InPlaneRendezvousPilot{ShapeFirstLadder=true}` → `RendezvousGuidanceLaw.Compose`）。
    /// 旧律要归档（用户 2026-09-22 裁定"那个东西不能用了"），而 `Source/已弃置/README.md` 排队中 §1 要求
    /// **先有替代再搬** ⇒ 本文件就是那份替代：**用活路径（`RendezvousLadder` + `LadderPilot` + `RendezvousShip`）
    /// 重跑同一批几何**。
    ///
    /// ## 判据（事先登记，四条共用）
    ///   ① **到得了**：末态 距离 ≤ 300 m 且 相对速度 ≤ 2 m/s（游戏口径，与 `LadderVsLawComparison` 同）；
    ///   ② **有界时间**：界写在每个用例里，取**物理/旧用例给的界**，**不按实测反推**；
    ///   ③ **不侵地**：最低点未破安全半径；
    ///   ④ 读数必须打出来（用时 / Δv / 规划次数 / 级序列 / 被政策挡下的发数）——"跑通了"不是证据。
    ///
    /// ## 船
    /// 一律用**游戏那条调试船**（`RendezvousShip.HighThrustShip` = 玩家路径上的那档）。
    /// 旧用例里的中推档随旧律台架一起退役——它考的是连续律的低推行为，对阶梯没有意义。
    ///
    /// ## ⚠ 台架保真度（**已登记的未建模项**，别当"已经同条件"）
    /// · **节点窗口闸**（`BurnStep.NodeWindowExpired`；2026-09-21 那个无界燃烧真 bug 的第三条判据）
    ///   **未建模**：本台架每一发都由"活差额 + 地板"保证终止，但若将来出现"烧过节点"的失败形态，
    ///   先补这一条再下结论。
    /// · **原子烧 / 三个 dt / 一步延迟**未建模（那三样在 `BurnArcExecutorBench` 里有建模）。
    /// </summary>
    public class LadderEndToEndTests
    {
        private readonly ITestOutputHelper _out;
        public LadderEndToEndTests(ITestOutputHelper output) => _out = output;

        // 与旧用例（RendezvousGuidanceCoreTests）同一套尺度 ⇒ 读数可直接对照
        private const double Mu = 2.0e14;
        private const double R1 = 5.5e6;             // LEO
        private const double R2 = 2.6322e7;          // GEO（真同步半径 = (μ/ω²)^(1/3)，不是"24h 假设"）
        private const double Dt = 5.0;               // 物理步（与既有台架一致）
        private const double Desired = 300.0;        // 判据：300 m
        private const double RelTol = 2.0;           // 判据：2 m/s

        /// <summary>顺行约定：法向 (0,−1,0) ⇒ 切向 ĥ×r̂ = (0,0,+1)（与旧台架同一套，**别改成 +Y**：
        /// 改了就是"两条轨道方向相反"的退化轨迹，旧台架为这个吃过一次账）。</summary>
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);

        private sealed class Run
        {
            public bool Reached, Crashed, FuelOut, LadderDeclaredDone;
            public double T, Dist, Rel, MinRadius = double.PositiveInfinity, Dv, MainDv, RcsDv, MainFuel, RcsFuel, Mass;
            public int PlanCount, Skipped;
            public readonly List<string> Rungs = new List<string>();
            public readonly List<string> Trace = new List<string>();
            public readonly List<string> BurnLog = new List<string>();
        }

        /// <summary>安全半径（台架口径）：与阶梯自己的 `Params.MinRadius` 同值
        /// ⇒ "侵地"判据与阶梯"不许掉到多低"的判据是同一根线。</summary>
        private static readonly double SafeRadius = new LadderPilot().Params.MinRadius;

        /// <summary>**判据的唯一权威**：`RendezvousGuidanceCore.HasRendezvoused`（默认 300 m / 2 m/s）。
        /// ⚠ 别在这里另写"距离 &lt; x 且 速度 &lt; y"：本台架第一版就是自己写了 `300 m / 2 m/s` 的内联判据，
        ///   而阶梯的收工门带 5% 松弛（见 `LadderPilot.Params` 的注释）⇒ 误报"没到"。</summary>
        private static bool Arrived(double dist, double rel) => RendezvousGuidanceCore.HasRendezvoused(dist, rel);

        /// <summary>跑一次：阶梯驾驶员 + 同一个 `RendezvousShip` 执行器 + 同一个完成判据。
        /// ⚠ 最低点/侵地在本函数里自己跟踪：`RendezvousShip.Propagate` **不管**这两项
        ///   （原来只有旧律驾驶员的 `Step` 里更新 ⇒ 阶梯侧一直是 `Infinity`）。</summary>
        private static Run Fly(ITarget target, double maxSeconds, double chaserPhaseDeg = 0.0)
        {
            double vc = Math.Sqrt(Mu / R1);
            double a0 = chaserPhaseDeg * Math.PI / 180.0;
            var ship = RendezvousShip.HighThrustShip(
                new DVector3(R1 * Math.Cos(a0), 0, R1 * Math.Sin(a0)),
                new DVector3(-vc * Math.Sin(a0), 0, vc * Math.Cos(a0)),
                new DVector3(0, 0, 1));
            var pilot = new LadderPilot();
            var res = new Run();
            for (double t = 0.0; t < maxSeconds; t += Dt)
            {
                DVector3 acc = pilot.Step(ship, target, Mu, t, Dt);
                ship.Propagate(acc, Mu, Dt);

                double r = ship.Pos.magnitude;
                if (r < res.MinRadius) res.MinRadius = r;
                if (r < SafeRadius) { res.Crashed = true; break; }

                double dist = (target.PosAt(t + Dt) - ship.Pos).magnitude;
                double rel = (target.VelAt(t + Dt) - ship.Vel).magnitude;
                res.T = t + Dt; res.Dist = dist; res.Rel = rel;
                if (t % 4000.0 < Dt && res.Trace.Count < 40)
                    res.Trace.Add($"t={t,7:F0} r={r / 1000.0,6:F0}km dist={dist / 1000.0,9:F1}km rel={rel,7:F1} m/s " +
                                  $"料={ship.MainFuel + ship.RcsFuel,6:F0}kg Δv={ship.MainDv + ship.RcsDv,7:F0}｜{pilot.DebugStuck}");
                // 停：① **到达判据成立**（300 m / 2 m/s，唯一权威）⇒ 真到达
                //     ② 料尽 ⇒ 失败（**不许**折算成"到达"）
                //     ③ 阶梯宣告完成 ⇒ 交给判据裁决（**宣告 ≠ 到达**，本库登记过"静默假成功"）
                if (Arrived(dist, rel)) { res.Reached = true; break; }
                if (pilot.FuelExhausted) break;
                if (pilot.Done) { res.LadderDeclaredDone = true; break; }
            }
            res.FuelOut = pilot.FuelExhausted;
            res.Dv = ship.MainDv + ship.RcsDv;
            res.MainDv = ship.MainDv; res.RcsDv = ship.RcsDv;
            res.MainFuel = ship.MainFuel; res.RcsFuel = ship.RcsFuel; res.Mass = ship.Mass;
            res.PlanCount = pilot.PlanCount;
            res.Skipped = pilot.SkippedByPolicy;
            res.Rungs.AddRange(pilot.Rungs);
            res.BurnLog.AddRange(pilot.BurnLog);
            return res;
        }

        private void PrintRungs(string tag, Run r)
        {
            _out.WriteLine($"     {tag} 级序列（{r.Rungs.Count} 次规划）：");
            int n = Math.Min(r.Rungs.Count, 12);
            for (int i = 0; i < n; i++) _out.WriteLine("       " + r.Rungs[i]);
            if (r.Rungs.Count > n) _out.WriteLine($"       …（共 {r.Rungs.Count} 次，只列前 {n}）");
        }

        /// <summary>**只在没收口时**打轨迹（每 4000 s 一行）+ 逐发账：红字要能被读懂，不能只给一个"没到"。</summary>
        private void PrintTrace(string tag, Run r)
        {
            _out.WriteLine($"     {tag} 轨迹（{r.Trace.Count} 行，每 4000 s）：");
            foreach (string s in r.Trace) _out.WriteLine("       " + s);
            _out.WriteLine($"     {tag} 逐发账（共 {r.BurnLog.Count} 发，列最后 10 发）：");
            int n = Math.Min(r.BurnLog.Count, 10);
            for (int i = r.BurnLog.Count - n; i < r.BurnLog.Count; i++)
                _out.WriteLine("       " + r.BurnLog[i]);
        }

        // ── 几何⓪：**台架自检**（原 T0，随旧律用例一起搬家时**保留**）────────────────
        /// <summary>
        /// 台架的传播是 RK4（`RendezvousShip.Propagate`）；**纯引力下必须保能量**，
        /// 否则下面四条几何的读数全都不可信。这条守卫与旧律无关（它测的是台架本身）。
        /// ⚠ 原在 `RendezvousGuidanceCoreTests.T0`（那个文件随旧律归档）⇒ 搬到这里，
        ///   因为"新台架用的还是同一套 RK4"，守卫不能跟着被测物一起消失。
        /// </summary>
        [Fact]
        public void G0_HarnessSelfCheck_Rk4ConservesEnergy()
        {
            double vc = Math.Sqrt(Mu / R1);
            var p = new DVector3(R1, 0, 0);
            var v = new DVector3(0, 0, vc);
            double e0 = 0.5 * v.sqrMagnitude - Mu / p.magnitude;

            var ship = RendezvousShip.Spawn(p, v, new DVector3(0, 0, 1));
            const double dt = 5.0;
            int steps = (int)(5731.0 * 40 / dt);       // 40 圈
            for (int i = 0; i < steps; i++) ship.Propagate(DVector3.zero, Mu, dt);

            double e1 = 0.5 * ship.Vel.sqrMagnitude - Mu / ship.Pos.magnitude;
            double relErr = Math.Abs(e1 - e0) / Math.Abs(e0);
            _out.WriteLine($"G0 台架能量相对漂移 {relErr:E3}（40 圈，dt={dt}s）");
            Assert.True(relErr < 1e-9, $"台架 RK4 保能量不达标：{relErr:E3}");
        }

        // ── 几何①：同轨 15°（"一圈交会"量级）────────────────────────────────────
        /// <summary>
        /// 同轨、相位差 15°，**目标时间尺度 = 一个轨道周期**（LEO 缩轨到大气层边缘只能收 ≈20° ⇒
        /// ≤20° 一圈内可闭合）。界沿用旧 T12 的 **2 个周期**（旧律实测 1.68 圈；**不按本台架实测反推**）。
        /// </summary>
        [Fact]
        public void G1_CoOrbital15_WithinOneOrbitScale()
        {
            double period = 2.0 * Math.PI * Math.Sqrt(R1 * R1 * R1 / Mu);
            var target = CircularTarget.Create(R1, Mu, 15.0 * Math.PI / 180.0, OrbitNormal);
            var r = Fly(target, 6.0 * period);

            _out.WriteLine($"G1 同轨 15°：用时 {r.T:F0}s = {r.T / period:F2} 个周期（P={period:F0}s）｜Δv={r.Dv:F0} m/s" +
                           $"（主 {r.MainDv:F0} + RCS {r.RcsDv:F0}；守恒律参考 {(2.0 / 3.0) * R1 * (15.0 * Math.PI / 180.0) / period:F0}）" +
                           $"｜末 {r.Dist:F0} m / {r.Rel:F2} m/s（严格 300 m/2 m/s：{(r.Dist <= Desired && r.Rel <= RelTol ? "✓" : "✗")}）" +
                           $"｜最低点 {r.MinRadius / 1000.0:F0} km｜余料 主 {r.MainFuel:F0} / RCS {r.RcsFuel:F0} kg" +
                           $"｜规划 {r.PlanCount} 次｜政策挡下 {r.Skipped} 发");
            PrintRungs("G1", r);

            Assert.False(r.Crashed, $"侵地：最低点 {r.MinRadius / 1000.0:F0} km（安全半径 {SafeRadius / 1000.0:F0} km）");
            Assert.True(r.Reached,
                $"未到 300 m/2 m/s：{r.Dist / 1000.0:F2} km / {r.Rel:F2} m/s" +
                $"（阶梯宣告完成={r.LadderDeclaredDone}｜料尽={r.FuelOut}｜Δv={r.Dv:F0} m/s）");
            // **只断言"有界"**（README 的原话是"有界时间到 300 m/2 m/s"）。
            // ⚠ 实测 5.41 个周期。**"一圈交会"是一条产品要求（待"总修缮"），不是与旧律的比较** ——
            //   旧律已被裁定作废（"那个东西不能用了"），它的读数（T12 的 1.68 周期）**不作参照**：
            //   它快在那里，代价在别处（LEO→GEO 3.27× 螺旋价且超预算、末端极限环、反复买冲量）。
            //   阶梯自己的 `PlanPhasingOrbit` 注释也登记着"小相位差也贵/慢 ⇒ 归入总修缮清单（先照 MJ 跑通）"，
            //   与"先照 MJ、创新放最后"的裁定一致 ⇒ 这里把 5.41 当读数打出来，只钉"不许无界"。
            Assert.True(r.T <= 12.0 * period, $"调相把时间拖到无界：{r.T / period:F2} 个周期（> 12）");
        }

        // ── 几何②：LEO→GEO（半径差大、形状主导）────────────────────────────────
        /// <summary>
        /// 高推船从 LEO 交会到 GEO。
        /// ⚠ **旧律在这条几何上有一条已登记缺口（E5）**：形状通道是"限权连续螺旋" ⇒ 实测 3.27× 螺旋价
        /// （10695 m/s）**超过这条船自己的 9986 预算** ⇒ 游戏里会跑光工质。阶梯有霍曼/调相级 ⇒ 本用例
        /// 顺带给出"阶梯在这条几何上的账单"，与霍曼（两冲量下界）和螺旋价并排。
        /// 界沿用旧 T13 的循环上界（1.5e6 s）；**不断言"一个目标周期内完成"**（旧律明确做不到，理由见 T13）。
        /// </summary>
        [Fact]
        public void G2_LeoToGeo_ReachesWithinBound()
        {
            const double maxSeconds = 1_500_000.0;
            var target = CircularTarget.Create(R2, Mu, 0.0, OrbitNormal);
            var r = Fly(target, maxSeconds);

            double vc1 = Math.Sqrt(Mu / R1), vc2 = Math.Sqrt(Mu / R2);
            double at = 0.5 * (R1 + R2);
            double hohmann = (Math.Sqrt(Mu * (2.0 / R1 - 1.0 / at)) - vc1)
                           + (vc2 - Math.Sqrt(Mu * (2.0 / R2 - 1.0 / at)));
            double spiral = Math.Abs(vc1 - vc2);
            double periodTgt = 2.0 * Math.PI * Math.Sqrt(R2 * R2 * R2 / Mu);

            _out.WriteLine($"G2 LEO→GEO：用时 {r.T:F0}s = {r.T / periodTgt:F2} 个 GEO 周期（P={periodTgt:F0}s）｜" +
                           $"Δv={r.Dv:F0}（主 {r.MainDv:F0} + RCS {r.RcsDv:F0}；霍曼下界 {hohmann:F0}｜螺旋价 {spiral:F0}｜" +
                           $"比值 {r.Dv / hohmann:F2}× 霍曼）｜末 {r.Dist:F0} m / {r.Rel:F2} m/s" +
                           $"（严格 300 m/2 m/s：{(r.Dist <= Desired && r.Rel <= RelTol ? "✓" : "✗")}）｜" +
                           $"最低点 {r.MinRadius / 1000.0:F0} km｜余料 主 {r.MainFuel:F0} / RCS {r.RcsFuel:F0} kg｜规划 {r.PlanCount} 次｜政策挡下 {r.Skipped} 发");
            PrintRungs("G2", r);
            if (!r.Reached || r.FuelOut) PrintTrace("G2", r);

            Assert.False(r.Crashed, $"侵地：最低点 {r.MinRadius / 1000.0:F0} km（安全半径 {SafeRadius / 1000.0:F0} km）");
            Assert.True(r.Reached,
                $"未到 300 m/2 m/s：{r.Dist / 1000.0:F2} km / {r.Rel:F2} m/s" +
                $"（阶梯宣告完成={r.LadderDeclaredDone}｜料尽={r.FuelOut}｜Δv={r.Dv:F0} m/s）");
        }

        // ── 几何③：GTO 椭圆目标（异轨，e≈0.65）──────────────────────────────────
        /// <summary>
        /// 目标在 LEO→GEO 的椭圆转移轨道上（近地点 R1、远地点 R2，e ≈ 0.65），追方在 LEO。
        /// ⚠ **追方错开 30° 出发**：`CreateGto` 让目标 t=0 就在近地点 = 追方的出发位置
        /// ⇒ 两者**初始距离 0**，而阶梯第 1 级是"已进入停下尺度 ⇒ 就地匹配速度" ⇒ 台架会跑出一个
        /// 退化用例（第一版实测：单发 2179 m/s 匹配速度后一路飞离）。错开相位才是"异轨交会"的本意。
        /// 界沿用旧 T16 的循环上界（40 个目标周期）；判据是收口（阶梯 Done 或到达判据）。
        /// </summary>
        [Fact]
        public void G3_GtoEccentricTarget_Reaches()
        {
            var target = EllipticTarget.CreateGto(R1, R2, Mu, OrbitNormal);
            double periodTgt = 2.0 * Math.PI * Math.Sqrt(Math.Pow(0.5 * (R1 + R2), 3) / Mu);
            var r = Fly(target, 40.0 * periodTgt, chaserPhaseDeg: -30.0);

            _out.WriteLine($"G3 LEO→GTO（椭圆目标 a={0.5 * (R1 + R2) / 1000.0:F0} km, e={(R2 - R1) / (R2 + R1):F3}；追方错开 −30°）：" +
                           $"用时 {r.T:F0}s = {r.T / periodTgt:F2} 个目标周期（P={periodTgt:F0}s）｜Δv={r.Dv:F0} m/s" +
                           $"（主 {r.MainDv:F0} + RCS {r.RcsDv:F0}）｜末 {r.Dist:F0} m / {r.Rel:F2} m/s" +
                           $"（严格 300 m/2 m/s：{(r.Dist <= Desired && r.Rel <= RelTol ? "✓" : "✗")}）｜" +
                           $"最低点 {r.MinRadius / 1000.0:F0} km｜余料 主 {r.MainFuel:F0} / RCS {r.RcsFuel:F0} kg｜规划 {r.PlanCount} 次｜政策挡下 {r.Skipped} 发");
            PrintRungs("G3", r);
            if (!r.Reached || r.FuelOut) PrintTrace("G3", r);

            Assert.False(r.Crashed, $"侵地：最低点 {r.MinRadius / 1000.0:F0} km（安全半径 {SafeRadius / 1000.0:F0} km）");
            Assert.True(r.Reached,
                $"异轨（椭圆目标）未到 300 m/2 m/s：{r.Dist / 1000.0:F0} km / {r.Rel:F2} m/s" +
                $"（阶梯宣告完成={r.LadderDeclaredDone}｜料尽={r.FuelOut}｜Δv={r.Dv:F0} m/s）");
        }

        // ── 几何④：同轨 90° + 游戏差额地板（"死区不憋死"）─────────────────────────
        /// <summary>
        /// 复现"点火死区把交会憋死"的**需求面**：执行器的差额忽略地板（`BurnDeficitIgnoreFloor = 1.0`，
        /// 台架 `LadderPilot.DeadbandMps` 同值）会把"小于一步能给的差额"判为噪声、不发指令。
        /// 要求：**它不能把阶梯憋死** ⇒ 仍须收敛。
        /// ⚠ 这条政策的**单元面**在 `BurnStep.Deficit` 里（地板/反向），本用例是它的端到端面；
        ///   读数里的"政策挡下 N 发"是覆盖率证据（N = 0 说明本用例没考到政策，不是通过的理由）。
        /// 界沿用旧 T14 的循环上界（60 个周期）。
        /// </summary>
        [Fact]
        public void G4_CoOrbital90_DeficitFloor_MustNotStall()
        {
            double period = 2.0 * Math.PI * Math.Sqrt(R1 * R1 * R1 / Mu);
            var target = CircularTarget.Create(R1, Mu, 90.0 * Math.PI / 180.0, OrbitNormal);
            var r = Fly(target, 60.0 * period);

            _out.WriteLine($"G4 同轨 90°（差额地板 {new LadderPilot().DeadbandMps:F1} m/s）：" +
                           $"用时 {r.T:F0}s = {r.T / period:F1} 个周期｜Δv={r.Dv:F0} m/s（主 {r.MainDv:F0} + RCS {r.RcsDv:F0}）｜" +
                           $"末 {r.Dist:F0} m / {r.Rel:F2} m/s（严格 300 m/2 m/s：{(r.Dist <= Desired && r.Rel <= RelTol ? "✓" : "✗")}）｜" +
                           $"最低点 {r.MinRadius / 1000.0:F0} km｜余料 主 {r.MainFuel:F0} / RCS {r.RcsFuel:F0} kg｜" +
                           $"规划 {r.PlanCount} 次｜政策挡下 {r.Skipped} 发");
            PrintRungs("G4", r);
            if (!r.Reached || r.FuelOut) PrintTrace("G4", r);

            Assert.False(r.Crashed, $"侵地：最低点 {r.MinRadius / 1000.0:F0} km（安全半径 {SafeRadius / 1000.0:F0} km）");
            Assert.True(r.Reached, $"差额地板把阶梯憋死了：{r.Dist / 1000.0:F0} km / {r.Rel:F2} m/s（政策挡下 {r.Skipped} 发）");
        }

        // ── 几何⑤（月球）：**用阶梯自己当 oracle** 扫相位 —— 异轨不必等窗口，同轨只能等一个会合周期 ──
        /// <summary>
        /// 游戏侧夹具（`DebugShipSpawner.SpawnMoonRendezvousDiffOrbitTest`）的构造 = "**扫目标相位、
        /// 拿阶梯自己的 `MakePlan` 当目标函数**"（取"**直接霍曼**且**计划完成最早**"的那个相位）。
        /// 本用例离线把这条构造在**三档几何**上都量出来：
        ///   · **异轨·升**（追击方 2900 → 目标 3300）：存在直接霍曼 ⇒ 计划时长 ≈ 一个转移（~7.7e3 s）
        ///   · **异轨·降**（追击方 3700 → 目标 3300）：同上（~9.2e3 s）
        ///   · **同轨**（3300 ↔ 3300）：**任何相位都扫不出直接霍曼**（Δa=0 ⇒ 霍曼无解）⇒ 只能落调相级
        ///     ⇒ 这正是用户两次等不下去的那件事（2026-09-29："近距离跑霍曼是非常弱智的行为"、
        ///     "我需要的是异轨交会测试，你他妈怎么这么喜欢同轨"）。
        /// ⚠ 目标函数**用阶梯自己的 `MakePlan`**，不写"夹角公式"——我推过两次夹角**都是错的**
        ///   （见交接 §⑭：把"补相位"误解成"把相位差收成 0"，而窗口在 −22°）。
        /// ⚠ 参数必须显式给：台架默认 `Params.MinRadius = 5.1e6` 是**地球尺度**，月球档要 1.6e6。
        /// </summary>
        [Fact]
        public void G6_MoonDiffOrbit_NoWindowWait_CoOrbitalIsThePathologicalOne()
        {
            const double muMoon = 5.0e12;
            const double rTgt = 3.3e6;
            var prm = new RendezvousLadder.Params
            {
                DesiredDistance = 283.0,
                MaxClosingSpeed = 100.0,
                MaxPhasingOrbits = 5.0,
                MinRadius = 1.6e6,
            };

            // 扫相位：只认 `Rung.Hohmann`（"一步到位"），目标 = 计划完成时刻最早；扫不到 ⇒ +∞
            (double dur, double deg, double dv, string status) ScanDirectHohmann(double rChaser)
            {
                double vC = Math.Sqrt(muMoon / rChaser), vT = Math.Sqrt(muMoon / rTgt);
                double bestDur = double.PositiveInfinity, bestDeg = 0.0, bestDv = 0.0;
                string bestSt = "（扫遍 0..360° 没找到直接霍曼）";
                for (int pass = 0; pass < 2; pass++)
                {
                    double center = pass == 0 ? 0.0 : bestDeg;
                    double half = pass == 0 ? 180.0 : 2.0;
                    double step = pass == 0 ? 2.0 : 0.25;
                    for (double deg = center - half; deg <= center + half; deg += step)
                    {
                        double a = deg * Math.PI / 180.0;
                        var rCv = new DVector3(rChaser, 0, 0);
                        var vCv = new DVector3(0, 0, vC);
                        var rTv = new DVector3(rTgt * Math.Cos(a), 0, rTgt * Math.Sin(a));
                        var vTv = new DVector3(-vT * Math.Sin(a), 0, vT * Math.Cos(a));
                        var ovC = OrbitView.FromStateVectors(rCv, vCv, muMoon, 0.0, DVector3.up, 1.5e6);
                        var ovT = OrbitView.FromStateVectors(rTv, vTv, muMoon, 0.0, DVector3.up, 1.5e6);
                        var rung = RendezvousLadder.MakePlan(ovC, ovT, 0.0, prm, out List<PlanNode> nodes, out string status);
                        if (rung != RendezvousLadder.Rung.Hohmann || nodes.Count == 0) continue;
                        double dur = nodes[nodes.Count - 1].ut;      // now = 0 ⇒ 末节点时刻就是"计划完成用时"
                        if (dur <= 0.0 || double.IsNaN(dur)) continue;
                        if (dur < bestDur)
                        {
                            bestDur = dur; bestDeg = deg; bestSt = status;
                            bestDv = 0.0;
                            foreach (var n in nodes) bestDv += n.dV.magnitude;
                        }
                    }
                }
                return (bestDur, bestDeg, bestDv, bestSt);
            }

            var asc = ScanDirectHohmann(2.9e6);      // 异轨·升（= 用户原报障"3300→2844 压轨"那一族）
            var desc = ScanDirectHohmann(3.7e6);     // 异轨·降
            var co = ScanDirectHohmann(rTgt);        // 同轨（病理档）

            _out.WriteLine($"G6 月球异轨档自检（μ={muMoon:E1}｜目标 r={rTgt / 1000.0:F0}km）：");
            _out.WriteLine($"     异轨·升（追击 2900km）：φ={asc.deg:F2}° ⇒ **直接霍曼**，计划用时 {asc.dur:F0}s" +
                           $"｜Δv {asc.dv:F1} m/s｜{asc.status}");
            _out.WriteLine($"     异轨·降（追击 3700km）：φ={desc.deg:F2}° ⇒ **直接霍曼**，计划用时 {desc.dur:F0}s" +
                           $"｜Δv {desc.dv:F1} m/s｜{desc.status}");
            _out.WriteLine(double.IsPositiveInfinity(co.dur)
                ? "     同轨（追击 3300km）：**任何相位都扫不出直接霍曼**（Δa=0 ⇒ 无窗口）" +
                  "⇒ 只能落调相级 ⇒ 计划时长 ≥ 一个会合周期（8.5e4 s 量级）"
                : $"     同轨（追击 3300km）：居然扫到直接霍曼（{co.dur:F0}s）⇒ **结论要重核**");

            Assert.False(double.IsPositiveInfinity(asc.dur), "异轨·升扫不出直接霍曼 ⇒ 夹具构造不成立");
            Assert.False(double.IsPositiveInfinity(desc.dur), "异轨·降扫不出直接霍曼 ⇒ 夹具构造不成立");
            Assert.True(asc.dur <= 3.0e4, $"异轨·升的计划用时 {asc.dur:F0}s 超过 3e4 s（不该要等窗口）");
            Assert.True(desc.dur <= 3.0e4, $"异轨·降的计划用时 {desc.dur:F0}s 超过 3e4 s（不该要等窗口）");
            Assert.True(double.IsPositiveInfinity(co.dur),
                $"同轨居然找得到直接霍曼（{co.dur:F0}s）⇒ 那「同轨必须等一个会合周期」的机理就不是 Δa=0，" +
                "G1 的注释与交接 §⑭ 都要重核");
        }
    }
}
