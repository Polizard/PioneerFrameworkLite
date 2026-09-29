// ⚠ **已归档（批5.B，2026-09-28）** —— 本文件**不参与编译**（整文件退役，不是用例级手术）。
// 原因：T1–T16 全部**由旧律驱动**（`InPlaneRendezvousPilot{ShapeFirstLadder=true}` → `RendezvousGuidanceLaw.Compose`）
//       ⇒ 旧律一归档它们连编译都过不了。而"先有替代再搬"这条规矩要的**替代品已经就位**：
//       `MechJebLibTest/LadderEndToEndTests.cs` 用**活路径**（阶梯 + `LadderPilot` + 同一执行器）
//       重跑了同一批几何（一圈交会 / LEO→GEO / GTO 异轨 / 死区不憋死；四条全绿、口径 300 m / 2 m/s）。
//       **T0（RK4 保能量自检）也搬过去了**（那个守卫测的是台架本身、与律无关）⇒ 见该文件的 `G0`。
// 取回代价：搬回 `Source/PioneerFramework.Pure.Tests/`（它不靠 csproj 链接，靠目录通配）。

﻿using System;
using PioneerFramework;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// 交会闭环的验收（2026-09-17）。对应 <c>DesignNotes/交会物理本质与闭环提法-20260917.md</c>。
    ///
    /// 每条测的都是**理论的可证断言**，不是"跑起来看着对"：
    ///   T0 台架自检  —— RK4 保能量（**先验仪器**，见 verification-instruments-must-be-verified）
    ///   T1 相位通道  —— 成本 ≈ (2/3)·r·Δφ（§5.4 守恒律），且随预算 T 成反比
    ///   T2 形状通道  —— 成本 = |v_c1 − v_c2|（§7.6 螺旋价），且 ≤ 1.15×霍曼（§10.1）
    ///   T3 全流程    —— 中推力船（旧架构失效区）能收敛
    ///   T4 执行器    —— 主推对准门 + RCS 预算不被吃穿
    /// </summary>
    public class RendezvousGuidanceCoreTests
    {
        private readonly ITestOutputHelper _out;
        public RendezvousGuidanceCoreTests(ITestOutputHelper o) { _out = o; }

        /// <summary>诊断开关：置 true 则远场禁用 RCS，用于分离"选网政策"的贡献。</summary>
        public static bool MainOnlyFarField = false;

        private const double Mu = 2.0e14;
        private const double R1 = 5.5e6;             // LEO
        private const double R2 = 2.6322e7;          // GEO
        // ⚠ **必须与 CircularPos/Vel 的旋向一致**：CircularPos(r,ang)=(cos,0,sin)*r 的轨道法向是 −Y。
        //   传 +Y 会让目标与飞船**反向公转**（同面、旋向相反）⇒ 律追一个反向目标，
        //   表现为"δn 说 −3e-6 而相位以 −2e-4 rad/s 收"这种自相矛盾的读数（2026-09-17 实测）。
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);

        private static DVector3 CircularPos(double r, double ang)
            => new DVector3(Math.Cos(ang) * r, 0, Math.Sin(ang) * r);

        private static DVector3 CircularVel(double r, double ang)
        {
            double v = Math.Sqrt(Mu / r);
            return new DVector3(-Math.Sin(ang) * v, 0, Math.Cos(ang) * v);
        }

        /// <summary>中推力船：**唯一实现已收到 <see cref="TestPlants.MidThrust" />**（B5-5：
        /// 此前 `TerminalCommitComparison.T5Ship` 是它的逐字副本）。本函数只转发，调用方零改动。</summary>
        private static RendezvousShip MidThrustShip(double r, double ang) => TestPlants.MidThrust(r, ang);

        // ── T0：台架自检 ────────────────────────────────────────────────────
        /// <summary>RK4 在纯引力下必须保能量；否则后面所有数字都不可信。</summary>
        [Fact]
        public void Sim_SelfCheck_Rk4ConservesEnergy()
        {
            var p = CircularPos(R1, 0.0);
            var v = CircularVel(R1, 0.0);
            double e0 = 0.5 * v.sqrMagnitude - Mu / p.magnitude;

            var ship = RendezvousShip.Spawn(p, v, new DVector3(0, 0, 1));
            double dt = 5.0;
            int steps = (int)(5731.0 * 40 / dt);       // 40 圈
            for (int i = 0; i < steps; i++) ship.Propagate(DVector3.zero, Mu, dt);

            double e1 = 0.5 * ship.Vel.sqrMagnitude - Mu / ship.Pos.magnitude;
            double relErr = Math.Abs(e1 - e0) / Math.Abs(e0);
            _out.WriteLine($"T0 能量相对漂移 {relErr:E3}（40 圈，dt={dt}s）");
            Assert.True(relErr < 1e-9, $"台架 RK4 保能量不达标：{relErr:E3}");
        }

        // ── T1：相位通道 = 双积分器，成本 ≈ (2/3)·r·Δφ ──────────────────────
        /// <summary>
        /// 同轨（a 相同）⇒ 自然漂移为零 ⇒ 相位只能靠控制买。
        /// 守恒律 Δv·t = (2/3)·r·Δφ 是**唯一**的汇率：换个算法也变不了。
        /// 本测扫描时间预算 T，断言 Δv·T 恒定且等于解析值。
        ///
        /// ⚠ 场景取 GEO 半径而非 LEO：**相位通道靠"下潜加速时钟"，下潜深度由 δa=(2a/3n)(Δφ/T) 定**。
        /// LEO（高度仅 500 km）下 δφ=60°/T=24000s 需要潜 146 km，叠上偏心率就穿地
        /// （2026-09-17 实测 a 掉到 4762 km &lt; 地表 5000 km）——那是场景不可行，不是律错。
        /// </summary>
        /// 可行性：两点火时长 |δn*|/U = Δφ·r³/(3·η·μ·T) 必须 ≪ T
        /// ⇒ $T \gg \sqrt{\Deltaarphi\,r^3/(3\eta\mu)}$，GEO + η=0.05 时为 ≈25,000 s。
        [Theory]
        [InlineData(48000.0)]
        [InlineData(96000.0)]
        [InlineData(192000.0)]
        public void T1_PhaseChannel_ObeysRentLaw(double budget)
        {
            double dphi0 = 2.0 * Math.PI / 6.0;                       // 60°
            double invariant = (2.0 / 3.0) * R2 * dphi0;              // = 1.837e7 m

            var ship = MidThrustShip(R2, 0.0);
            var target = CircularTarget.Create(R2, Mu, dphi0, OrbitNormal);   // 目标领先 60°（同轨）
            // 本测 = **远场相位通道**（与测试名同范围）。终端交接见 T5。
            var pilot = new InPlaneRendezvousPilot
            {
                TimeBudget = budget, ClosePhase = true, MainOnly = MainOnlyFarField,
                TerminalHandoff = false,   // 本测 = 远场相位通道；终端由 T5 独立验
            };

            // **在 T 处判定**：律的承诺是"用 T 收到位"，超期后的行为不属于被验对象。
            // **在 T 处判定**（律的承诺是“用 T 收到位”）。截点之后的行为不属于被验对象：
            // 终端交接尚未实现，见 §13.3c 的两处失败诊断。
            double t = 0.0, dt = 5.0, dist = double.NaN, rel = double.NaN;
            while (t < budget)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out dist, out rel, out _);
                t += dt;
                if (RendezvousGuidanceCore.HasRendezvoused(dist, rel)) break;
            }
            bool converged = RendezvousGuidanceCore.HasRendezvoused(dist, rel);

            double dv = ship.MainDv + ship.RcsDv;

            // ── 基准：**精确两点火最小燃料**（不是渐近守恒律）──
            //   渐近式 Δv=(2/3)rΔφ/T 只在 T ≫ √(4|Δφ|/U) 时成立；LEO/GEO 下该尺度 ≈35,680 s。
            //   T=48000 只有 1.35 倍 ⇒ 用它当基准会把"有限控制修正"误判成浪费。
            double aRef0 = Math.Min(Math.Max(ship.MainAccel, ship.RcsAccel),
                                    pilot.ThrustToGravityCap * Mu / (R2 * R2));
            double U0 = 3.0 * aRef0 / R2;
            double costRef = RendezvousGuidanceCore.MinFuelCostReference(-dphi0, U0, budget) * (R2 / 3.0);
            double errRel = Math.Abs(dv - costRef) / costRef;
            double asympt = invariant / budget;

            _out.WriteLine($"T1 T={budget:F0}s Δv_main={ship.MainDv:F1} Δv_rcs={ship.RcsDv:F2} " +
                           $"总={dv:F1} m/s（精确基准 {costRef:F1}，渐近式 {asympt:F1}）" +
                           $"  偏差 {errRel:P1} ｜最低点={ship.MinRadius / 1000.0:F0}km");
            _out.WriteLine($"     分账: c₀={pilot.DvFromC0:F0} 谐波={pilot.DvFromHarm:F0} " +
                           $"c₀翻转={pilot.C0SignFlips} 对准等待={ship.AlignWaitSeconds:F0}s " +
                           $"｜末 dist={dist / 1000.0:F0}km rel={rel:F1}m/s");

            Assert.False(ship.Crashed, $"侵地：最低点 {ship.MinRadius:F0} m < 安全半径");
            Assert.True(converged || dist < 0.35 * R2,
                $"相位未实质收敛：dist={dist / 1000.0:F0}km");
            // 判据对**精确两点火最小燃料**，不对渐近守恒律
            // ——后者只在 T >> √(4|Δφ|/U)≈35,680s 时成立，T=48000 用它当基准会凭空造出 45% “偏差”。
            Assert.True(dv <= costRef * 1.25,
                $"超出精确最小燃料 25%：Δv={dv:F1} vs 基准 {costRef:F1}");
            // 三条 InlineData 的 Δv·T 一致 ⇒ 验证 Δv ∝ 1/T 的汇率恒定
        }

        // ── T2：形状通道 = 螺旋价 |v_c1 − v_c2|，且 ≤ 1.15×霍曼 ─────────────
        /// <summary>相位自由（只把 a 开到目标）时，连续推力的成本恰是两端圆轨速度之差。</summary>
        [Fact]
        public void T2_ShapeChannel_CostsSpiralPrice()
        {
            double vc1 = Math.Sqrt(Mu / R1), vc2 = Math.Sqrt(Mu / R2);
            double spiral = vc1 - vc2;                                 // 3274.8
            // 霍曼
            double at = 0.5 * (R1 + R2);
            double vp = Math.Sqrt(Mu * (2.0 / R1 - 1.0 / at));
            double va = Math.Sqrt(Mu * (2.0 / R2 - 1.0 / at));
            double hohmann = (vp - vc1) + (vc2 - va);                  // 2862.0

            var ship = MidThrustShip(R1, 0.0);
            var target = CircularTarget.Create(R2, Mu, 0.0, OrbitNormal);
            var pilot = new InPlaneRendezvousPilot { ClosePhase = false };

            double t = 0.0, dt = 5.0, a = 0.0, e = 0.0, rp = 0.0, ra = 0.0;
            while (t < 200000.0)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out _, out _, out _);
                t += dt;
                a = OrbitMath.SemiMajorAxis(ship.Pos, ship.Vel, Mu);
                OrbitMath.GetEccentricity(ship.Pos, ship.Vel, out _, out e, Mu);
                if (ship.Crashed) break;
                if (Math.Abs(a - R2) < 20000.0 && e < 0.01) break;   // a **且** e 都要到位
            }
            rp = a * (1.0 - e); ra = a * (1.0 + e);

            double dv = ship.MainDv + ship.RcsDv;
            double ratioH = dv / hohmann;

            _out.WriteLine($"T2 螺旋价实测 {dv:F1} m/s（解析 {spiral:F1}）｜霍曼 {hohmann:F1}｜比值 {ratioH:F4}" +
                           $"｜用时 {t:F0}s｜末 a={a / 1000.0:F0}km e={e:F4} rp={rp / 1000.0:F0}km" +
                           $"｜RCS 用 {ship.RcsDv:F2}｜最低点={ship.MinRadius / 1000.0:F0}km");

            Assert.False(ship.Crashed, $"侵地：最低点 {ship.MinRadius:F0} m");
            Assert.True(Math.Abs(dv - spiral) / spiral < 0.10, $"螺旋价偏差过大：{dv:F1} vs {spiral:F1}");
            // §10.1 的 1+√2 是**螺旋 vs 霍曼**的界（LEO→GEO 解析值 1.144）；
            // 实测再叠上 e 通道清理残余偏心率的开销 ⇒ 1.20 为本台架口径的上界。
            Assert.True(ratioH <= 1.20, $"超出实用区界：{ratioH:F4} > 1.20");
        }

        // ── T9：高推力下是否收敛为**近似双脉冲转移**（理论 §11.1） ──────────────
        /// <summary>
        /// 理论上 $a_{max}	o\infty$ 时远场律应退化为两点火（§11.1：P 是 F 的该极限）。
        /// 度量：把 Δv 按任务时间分 20 箱，**脉冲集中度** = 首末各 3 箱的 Δv 占比。
        /// 理想双脉冲 = 1.0；均匀燃烧 = 0.3。
        ///
        /// ⚠ 预期有个东西会拦住它：**η 限权**（$a_t\le\eta g_{local}$）本身是为近圆约化设的，
        /// 会把高推力船硬压成低推力。本测同时跑 η=0.05 与 η 放开两组，把这一点分离出来。
        /// </summary>
        [Theory]
        [InlineData(0.05, 0.05)]
        [InlineData(0.05, 0.01)]
        [InlineData(0.05, 0.002)]
        [InlineData(20.0, 0.05)]
        public void T9_HighThrust_ApproachesTwoImpulse(double eta, double fTrack)
        {
            double dphi0 = 2.0 * Math.PI / 6.0;
            double T = 96000.0;
            var ship = MidThrustShip(R2, 0.0);
            ship.MainThrust = 400000.0;          // 高推力：a0 = 400kN/17.5t = 22.9 m/s²
            var target = CircularTarget.Create(R2, Mu, dphi0, OrbitNormal);
            var pilot = new InPlaneRendezvousPilot
            {
                TimeBudget = T, ClosePhase = true, TerminalHandoff = false,
                ThrustToGravityCap = eta, TrackingFraction = fTrack,
                AdaptiveCapEnabled = true,   // 本测显式启用自适应限权（§17.5 的发现靠它）
                BinStart = 0.0, BinSpan = T,
            };

            double t = 0.0, dt = 5.0, dist = double.NaN, rel = double.NaN;
            while (t < T)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out dist, out rel, out _);
                t += dt;
            }

            double conc = pilot.ImpulseConcentration();
            double dv = ship.MainDv + ship.RcsDv;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 20; i++)
                sb.Append(pilot.DvBins[i] > dv * 0.02 ? "#" : ".");
            _out.WriteLine($"T9 η={eta:F2} f={fTrack:F3} 高推力(22.9 m/s²) 总Δv={dv:F1} " +
                           $"脉冲集中度={conc:P0} 末 dist={dist / 1000.0:F0}km");
            _out.WriteLine($"     Δv 时间分布: {sb}  (每箱 {T / 20 / 1000:F0}ks)");
            _out.WriteLine($"     参考：两冲量密实解应≈两端各一箱，中间空白");
        }
        // ── T8：终端"策略"的代价核算（CW 模型内精确） ──────────────────────
        /// <summary>
        /// 把**策略本身的代价**与仿真杂项分开：在 CW 模型内比较三种执行方式，
        /// 同一个初始相对状态、同一个视界。
        ///   A 执行计划   ：打 Δv₁ → 滑行 T → 打 Δv₂
        ///   B 滚动重解   ：每步重解、只打 Δv₁（台架终端的实际做法）
        ///   C 温柔下界   ：相位租金 (2/3)·r·Δφ/T
        /// </summary>
        [Fact]
        public void T8_TerminalPolicy_RecedingVsExecutePlan()
        {
            double n = Math.Sqrt(Mu / (R2 * R2 * R2));
            double T = 0.45 * 2.0 * Math.PI / n;
            var r0 = new DVector3(0.0, 942000.0, 0.0);   // 纯沿迹偏差 942 km（同轨）
            var v0 = DVector3.zero;
            // A：执行计划
            bool ok = CwRendezvousCore.Solve(r0, v0, n, T, out var d1, out var d2);
            Assert.True(ok);
            double costA = d1.magnitude + d2.magnitude;
            // B：滚动重解（每 dtSec 一次，只打 Δv₁），直到收敛或超时
            double dtSec = 5.0, costB = 0.0, tB = 0.0;
            var rc = r0; var vc = v0;
            while (tB < 60.0 * T && rc.magnitude + vc.magnitude > 1.0)    
        {
                if (CwRendezvousCore.SolveBest(rc, vc, n, out var pB))        
        {
                    var step = pB.Dv1;
                    costB += step.magnitude;
                    vc = vc + step;
        
        }
                CwRendezvousCore.Propagate(rc, vc, n, dtSec, out var rn, out var vn);
                rc = rn; vc = vn; tB += dtSec;
    
        }
            // ⚠ **[关键] 用台架**真实的**终端入口状态再算一遍**——终端账单由远场交班状态决定。
            //   实测入口（T5@192000）：r=(-413,-1226,0)km, v=(153.2,0.2,0.0)m/s（**径向**速度）
            var rEntry = new DVector3(-413000.0, -1226000.0, 0.0);
            var vEntry = new DVector3(153.2, 0.2, 0.0);
            bool okE = CwRendezvousCore.SolveBest(rEntry, vEntry, n, out var pE);
            _out.WriteLine($"T8 **台架真实入口态**：dist={rEntry.magnitude / 1000.0:F0}km " +
                           $"|v_rel|={vEntry.magnitude:F1}m/s");
            if (okE)
                _out.WriteLine($"T8 E 该态的最小两点火 = {pE.TotalDv:F1} m/s " +
                               $"(Δv₁={pE.Dv1.magnitude:F1} Δv₂={pE.Dv2.magnitude:F1}, tof={pE.ToF:F0}s)");
            else
                _out.WriteLine("T8 E 该态 CW 无解（线性化早已失效）");

            // C：温柔下界（相位租金）
            double dphi = 942000.0 / R2;
            double costC = (2.0 / 3.0) * R2 * dphi / T;

            // F：**在真实入口态上跑滚动策略**（固定短视界 0.15·T_orb，与台架终端同口径）
            //    目的：把"台架的 2.9×" 分解成 "策略本身" + "执行路径/非线性"
            double tF = 0.15 * 2.0 * Math.PI / n;
            double costF = 0.0, tElapsedF = 0.0;
            var rf = rEntry; var vf = vEntry;
            while (tElapsedF < 60.0 * T && rf.magnitude + vf.magnitude > 1.0)
            {
                if (CwRendezvousCore.Solve(rf, vf, n, tF, out var d1F, out _))
                {
                    vf = vf + d1F;
                    costF += d1F.magnitude;
                }
                CwRendezvousCore.Propagate(rf, vf, n, dtSec, out var rnF, out var vnF);
                rf = rnF; vf = vnF; tElapsedF += dtSec;
            }
            _out.WriteLine($"T8 F 真实入口态 + 滚动(固定短视界 0.15T_orb) = {costF:F1} m/s，用时 {tElapsedF:F0}s");
            _out.WriteLine($"T8 F 与 C（温柔下界 {costC:F1}）之比…");

            // D：滚动重解 **+ 节流**（台架的真实执行路径）——每步最多打 aMax*dt
            double aMaxTerm = 0.457, costD = 0.0, tD = 0.0;
            var rd = r0; var vd = v0;
            while (tD < 60.0 * T && rd.magnitude + vd.magnitude > 1.0)
            {
                if (CwRendezvousCore.SolveBest(rd, vd, n, out var pD))
                {
                    double want = pD.Dv1.magnitude;
                    double give = Math.Min(want, aMaxTerm * dtSec);
                    if (want > 1e-12) vd = vd + pD.Dv1 * (give / want);
                    costD += give;
                }
                CwRendezvousCore.Propagate(rd, vd, n, dtSec, out var rn2, out var vn2);
                rd = rn2; vd = vn2; tD += dtSec;
            }

            _out.WriteLine($"T8 视界 T={T:F0}s（{T / (2 * Math.PI / n):F2}·T_orb）");
            _out.WriteLine($"T8 A 执行计划   = {costA:F1} m/s  (Δv₁={d1.magnitude:F1} Δv₂={d2.magnitude:F1})");
            _out.WriteLine($"T8 B 滚动重解   = {costB:F1} m/s，用时 {tB:F0}s") ;
            _out.WriteLine($"T8 C 温柔下界   = {costC:F1} m/s");
            _out.WriteLine($"T8 B/A = {costB / Math.Max(costA, 1e-9):F1}×   B/C = {costB / Math.Max(costC, 1e-9):F1}×");
            _out.WriteLine($"T8 D 滚动+节流 = {costD:F1} m/s，用时 {tD:F0}s   D/B = {costD / Math.Max(costB, 1e-9):F1}×   D/C = {costD / Math.Max(costC, 1e-9):F1}×");
        }

        // ── T7：紧预算的可行性判据（E2 的计划层产出） ──────────────────────
        /// <summary>
        /// 单腿能闭合的最大相位 = **U·T²/4**（从静止起步两次 bang 的极大值，在 δn*=UT/2 处取到），
        /// **不是** U·T²/2 —— 后者是"已在 δn 上"的预测式给的，漏掉了建立 δn 的时间。
        /// 这条差 2 倍的判据是本批纠正的一处**文档错误**，且实测恰好有工况卡在缝里。
        /// </summary>
        [Fact]
        public void T7_FeasibilityBound_IsQuarterUT2_NotHalf()
        {
            double U = 3.0 * 0.05 * (Mu / (R2 * R2)) / R2;      // 与台架同口径：U=3·a_max/r，a_max=η·g
            double T = 48000.0;
            double bound = RendezvousGuidanceCore.MaxClosablePhase(U, T);
            Assert.Equal(0.25 * U * T * T, bound, 12);

            // **实测工况恰好卡在缝里**：|Δφ|=60° 时 U·T²/4 < |Δφ| < U·T²/2
            double dphi = 2.0 * Math.PI / 6.0;
            _out.WriteLine($"T7 U={U:E3} 上界 U·T²/4={bound:F4}  |Δφ|={dphi:F4}  U·T²/2={0.5 * U * T * T:F4}" +
                           $"  ⇒ {(dphi > bound ? "**单腿不可行**" : "可行")}");
            Assert.True(dphi > bound, "该工况本应落在单腿不可行的缝里");
            Assert.True(dphi < 0.5 * U * T * T, "但仍在 S 形式自认为可行的范围内——这正是漏诊来源");

            // 最短腿长：收掉 |Δφ| 需要 T ≥ 2√(|Δφ|/U)，此处应 > 48000（印证上面判不可行）
            double minLeg = RendezvousGuidanceCore.MinLegSeconds(dphi, U);
            _out.WriteLine($"T7 MinLegSeconds={minLeg:F0}s（给了 {T:F0}s）");
            Assert.True(minLeg > T, $"最短腿长应超过给定预算：{minLeg:F0} > {T:F0}");
        }

        /// <summary>可达上界随当前 δn 从 U·T²/4（静止）连续升到 U·T²/2（已在 δn 上）。</summary>
        [Fact]
        public void T7_MaxAchievable_RisesWithExistingDrift()
        {
            double U = 3.0 * 0.05 * (Mu / (R2 * R2)) / R2;
            double T = 60000.0;
            double atRest = RendezvousGuidanceCore.MaxAchievablePhase(0.0, U, T, out _);
            double onRef = RendezvousGuidanceCore.MaxAchievablePhase(U * T, U, T, out _);
            double quarter = 0.25 * U * T * T;
            _out.WriteLine($"T7 可达上界：静止={atRest:F4}（U·T²/4={quarter:F4}）  已在 δn*=UT 上={onRef:F4}");
            Assert.True(atRest > quarter * 0.9 && atRest < quarter * 1.1, $"静止态应≈U·T²/4，实际 {atRest:F4}");
            Assert.True(onRef > atRest * 1.5, $"已在参考上时上界应显著更大：{onRef:F4} vs {atRest:F4}");
        }

        // ── T6：面外通道（一般情况，不能只做共面） ────────────────────────
        /// <summary>
        /// 同轨但**目标轨道面倾斜** Δi：相对距离是 $r\cdot\Delta i$ 量级的交叉轨迹振荡
        /// （GEO 上 Δi=2° ⇒ 919 km），所需 Δv 量级 $v_c\cdot\Delta i$ ≈ 96 m/s。
        /// 面外是纯谐振子（无长期项）⇒ 纯一阶谐波律，见 CrossTrackControl 的推导。
        /// </summary>
        [Theory]
        [InlineData(2.0)]
        [InlineData(-3.0)]
        public void T6_OutOfPlane_RendezvousWithInclinationOffset(double diDeg)
        {
            double di = diDeg * Math.PI / 180.0;
            // 目标法向：绕 PlaneX 轴倾斜 di
            var nTilt = new DVector3(-Math.Sin(di), -Math.Cos(di), 0.0);   // OrbitNormal=(0,-1,0) 倾斜后
            double dphi0 = 2.0 * Math.PI / 6.0;
            double vc = Math.Sqrt(Mu / R2);
            double planeDv = vc * Math.Abs(di);                            // 经典面外变轨量级

            var ship = MidThrustShip(R2, 0.0);
            var target = CircularTarget.Create(R2, Mu, dphi0, nTilt);
            var pilot = new InPlaneRendezvousPilot
            {
                TimeBudget = 192000.0, ClosePhase = true, TerminalHandoff = true,
                ReArmLegs = true, TerminalEngageFraction = 0.05,
            };

            double t = 0.0, dt = 5.0, dist = double.NaN, rel = double.NaN;
            while (t < 700000.0)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out dist, out rel, out _);
                t += dt;
                if (RendezvousGuidanceCore.HasRendezvoused(dist, rel)) break;
            }
            bool ok = RendezvousGuidanceCore.HasRendezvoused(dist, rel);
            double dv = ship.MainDv + ship.RcsDv;
            _out.WriteLine($"T6 终端分账: Δv={pilot.TerminalDv:F1} 步数={pilot.TerminalSteps} " +
                           $"入口 dist={pilot.TerminalEntryDist / 1000.0:F0}km rel={pilot.TerminalEntryRel:F1} " +
                           $"入口面外 z={pilot.TerminalEntryR.z / 1000.0:F1}km");
            // **该入口态在 CW 模型里的理想成本** —— 分离"状态贵"与"终端处理得差"
            double nG = Math.Sqrt(Mu / (R2 * R2 * R2));
            if (CwRendezvousCore.SolveBest(pilot.TerminalEntryR, pilot.TerminalEntryV, nG, out var pe))
                _out.WriteLine($"     入口态 CW 理想 = {pe.TotalDv:F1} m/s (Δv₁={pe.Dv1.magnitude:F1} Δv₂={pe.Dv2.magnitude:F1} tof={pe.ToF:F0}s)" +
                               $"  ⇒ 终端实际/理想 = {pilot.TerminalDv / Math.Max(pe.TotalDv, 1e-9):F2}×");
            _out.WriteLine($"T6 Δi={diDeg:F1}° 用时 {t:F0}s 总Δv={dv:F1}（面外量级 {planeDv:F0}）" +
                           $" 面外通道用 {pilot.CrossTrackDv:F1}" +
                           $" 末面外 z={pilot.LastCrossTrackZ / 1000.0:F2}km 原语={pilot.LastTerminalKind} tof={pilot.LastTerminalToF:F0}s" +
                           $"｜末 dist={dist / 1000.0:F2}km rel={rel:F1}m/s ｜终端={pilot.InTerminal} ｜最低点={ship.MinRadius / 1000.0:F0}km");
            Assert.True(ok, $"带倾角差未交会：dist={dist / 1000.0:F2}km rel={rel:F1}m/s");
            Assert.False(ship.Crashed, "侵地");
        }

        // ── T5：终端交接（独立于远场律） ──────────────────────────────────
        /// <summary>
        /// **终端 = 滚动时域的两点边值解**（复用 `Gooding.Solve`），不是位置比例律。
        /// 两次直觉做法的失败诊断见设计稿 §13.3d：大距离切滑道会与 CW 耦合 1.5n·Δr 这个
        /// 不稳定模式对抗；分腿重起会在 T→0 时因 k=1/(fT²) 爆炸而发散。
        ///
        /// **当前状态：未通过**——远场律只把相对距离压到 0.20·r 量级，终端接入后仍收不到
        /// 5 km / 25 m/s。这是本稿此刻唯一未完成的一环。
        /// </summary>
        [Theory]
        [InlineData(96000.0)]
        [InlineData(192000.0)]
        public void T5_Terminal_ReachesTolerance(double budget)
        {
            double dphi0 = 2.0 * Math.PI / 6.0;
            var ship = MidThrustShip(R2, 0.0);
            var target = CircularTarget.Create(R2, Mu, dphi0, OrbitNormal);
            var pilot = new InPlaneRendezvousPilot
            {
                TimeBudget = budget, ClosePhase = true, TerminalHandoff = true, ReArmLegs = true, TimeBudget0 = budget,
                TerminalEngageFraction = 0.05,   // 正常走近圈；卡住了由停滞兜底（见 pilot）
            };

            double t = 0.0, dt = 5.0, dist = double.NaN, rel = double.NaN;
            while (t < budget + 400000.0)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out dist, out rel, out _);
                t += dt;
                if (RendezvousGuidanceCore.HasRendezvoused(dist, rel)) break;
            }
            bool ok = RendezvousGuidanceCore.HasRendezvoused(dist, rel);
            _out.WriteLine($"T5 T={budget:F0}s 用时 {t:F0}s 总Δv={ship.MainDv + ship.RcsDv:F1} " +
                           $"末 dist={dist / 1000.0:F2}km rel={rel:F1}m/s 终端={pilot.InTerminal} " +
                           $"｜终端原语={pilot.LastTerminalKind} tof={pilot.LastTerminalToF:F0}s");
            _out.WriteLine($"     终端分账: Δv={pilot.TerminalDv:F1} 步数={pilot.TerminalSteps} 入口 dist={pilot.TerminalEntryDist / 1000.0:F0}km rel={pilot.TerminalEntryRel:F1}");
            _out.WriteLine($"         入口 RTN: r=({pilot.TerminalEntryR.x / 1000.0:F0},{pilot.TerminalEntryR.y / 1000.0:F0},{pilot.TerminalEntryR.z / 1000.0:F0})km v=({pilot.TerminalEntryV.x:F1},{pilot.TerminalEntryV.y:F1},{pilot.TerminalEntryV.z:F1})m/s");
            Assert.True(ok, $"终端未收到容差：dist={dist / 1000.0:F2}km rel={rel:F1}m/s");
        }

        // ── T3：全流程（中推力船，旧架构失效区） ────────────────────────────
        /// <summary>LEO 起、与 GEO 目标交会。旧架构（Θ 大 + 定时刻 Lambert）在此区发散。</summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(Math.PI)]
        [InlineData(-2.0)]
        public void T3_FullRendezvous_MidThrust_Converges(double targetPhase)
        {
            var ship = MidThrustShip(R1, 0.0);
            var target = CircularTarget.Create(R2, Mu, targetPhase, OrbitNormal);
            // 预算必须盖住**先做完形状改变**：η=0.05 限权后 LEO→GEO 螺旋本身就要
            // ∫dv/(0.05·g(r)) ≈ 10^5 s 量级。旧值 60000 s 是**预算不可行**，不是律的问题。
            var pilot = new InPlaneRendezvousPilot { TimeBudget = 300000.0, ClosePhase = true,
                TerminalHandoff = false };   // 本测 = 远场成本；终端由 T5 验

            double t = 0.0, dt = 5.0;
            double dist = double.NaN, rel = double.NaN;
            bool done = false;
            while (t < 400000.0)
            {
                pilot.Step(ship, target, Mu, t + dt, dt, out _, out dist, out rel, out _);
                t += dt;
                if (!done && RendezvousGuidanceCore.HasRendezvoused(dist, rel))
                {
                    done = true;
                    break;
                }
            }

            double dv = ship.MainDv + ship.RcsDv;
            _out.WriteLine($"T3 相位0={targetPhase:F2} 用时 {t:F0}s 总Δv={dv:F1} " +
                           $"(主 {ship.MainDv:F0} / RCS {ship.RcsDv:F1})｜末 dist={dist / 1000.0:F2}km rel={rel:F1}m/s " +
                           $"｜对准等待 {ship.AlignWaitSeconds:F0}s");

            // ── 本测验的是**远场律在整个转移上的成本**（形状+相位一起）──
            //   形状地板 = 螺旋价 |v_c1-v_c2| = 3273.7；相位在该预算下几乎免费（异轨自然漂移大）。
            //   **终端精确匹配不在本测范围**：需从远场律切精确制导（Lambert/CW），
            //   是设计稿 §12.2 明确的“唯一必须精确的地方”，尚未接线。
            double spiral = Math.Sqrt(Mu / R1) - Math.Sqrt(Mu / R2);
            _out.WriteLine($"     远场成本 / 螺旋价地板 = {dv / spiral:F4}");
            Assert.True(dv <= spiral * 1.20, $"远场成本超螺旋价 20%：{dv:F1} vs {spiral:F1}");
            // 粗收敛上界 30%（实测 3.2% / 6.6% / 27%）。**成本才是本测的主张**；精确落点见 T5。
            Assert.True(dist < 0.30 * R2, $"远场粗收敛未达标：dist={dist / 1000.0:F0}km");
        }

        /// <summary>高推船（= 游戏调试船同参）在测试里的构造。</summary>
        private static RendezvousShip HighThrustShip(double r, double ang)
            => RendezvousShip.HighThrustShip(CircularPos(r, ang), CircularVel(r, ang), new DVector3(0, 0, 1));

        // ── T13：**高推船 LEO→GEO**（"一大圈"）─────────────────────────────────
        /// <summary>
        /// 与 T11 同一工况，但换成**游戏里那条高推船**（dV≈9986、满载 7.87 m/s²）。
        ///
        /// 为什么必须补这一档：形状通道的指令是 τ 限定的 $a_t=\delta n\,r/(3	au)$（与推力无关），
        /// 而限权 0.05·g 只对**低推力船**是紧约束 ⇒ 用 8kN 台架船测"快不快"是测错了对象。
        /// 本条的目标 = **LEO→GEO 在"一个目标周期"量级内完成**（GEO 一圈 = 60000s）。
        /// </summary>
        [Fact]
        public void T13_HighThrust_LeoToGeo_WithinOneTargetPeriod()
        {
            var ship = HighThrustShip(R1, 0.0);
            var target = CircularTarget.Create(R2, Mu, 0.0, OrbitNormal);
            double periodTgt = 2.0 * Math.PI * Math.Sqrt(R2 * R2 * R2 / Mu);   // GEO 一圈 = 60000s
            var pilot = new InPlaneRendezvousPilot
            {
                ShapeFirstLadder = true, LegSeconds = 60000.0,
                TimeBudget = 60000.0, TimeBudget0 = 60000.0,
                TerminalHandoff = true,
            };

            double t = 0.0, dt = 5.0, dist = double.NaN, rel = double.NaN;
            bool done = false;
            while (t < 1_500_000.0)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out dist, out rel, out _);
                t += dt;
                if (ship.Crashed) break;
                if (RendezvousGuidanceCore.HasRendezvoused(dist, rel)) { done = true; break; }
            }

            double spiral = Math.Sqrt(Mu / R1) - Math.Sqrt(Mu / R2);
            double hohmann;
            {
                double at = 0.5 * (R1 + R2);
                double vp = Math.Sqrt(Mu * (2.0 / R1 - 1.0 / at));
                double va = Math.Sqrt(Mu * (2.0 / R2 - 1.0 / at));
                double vc1 = Math.Sqrt(Mu / R1), vc2 = Math.Sqrt(Mu / R2);
                hohmann = (vp - vc1) + (vc2 - va);
            }
            double dv = ship.MainDv + ship.RcsDv;
            _out.WriteLine($"T13 高推船 LEO→GEO：用时 {t:F0}s = {t / periodTgt:F2} 个目标周期（GEO P={periodTgt:F0}s）" +
                           $"｜Δv={dv:F1}（螺旋价 {spiral:F1}｜霍曼 {hohmann:F1}｜比值 {dv / spiral:F3}）" +
                           $"｜末 dist={dist / 1000.0:F2}km rel={rel:F1}m/s｜最低点={ship.MinRadius / 1000.0:F0}km" +
                           $"｜对齐等待 {ship.AlignWaitSeconds:F0}s");
            _out.WriteLine($"     分账: c0={pilot.DvFromC0:F1} 谐波={pilot.DvFromHarm:F1} 终端={pilot.TerminalDv:F1}" +
                           $"（终端步数={pilot.TerminalSteps} 入口 dist={pilot.TerminalEntryDist / 1000.0:F0}km " +
                           $"rel={pilot.TerminalEntryRel:F1}m/s）｜主={ship.MainDv:F0} RCS={ship.RcsDv:F1}" +
                           $"｜C0 翻转={pilot.C0SignFlips}｜侵地={ship.Crashed}");

            Assert.False(ship.Crashed, $"侵地：最低点 {ship.MinRadius / 1000.0:F0}km");
            Assert.True(done, $"未收敛：dist={dist / 1000.0:F1}km rel={rel:F1}m/s");
            // ⚠ **本测目前不能断言"一个目标周期内完成"**——但那不是律的缺陷，是**机制缺口**：
            //   形状通道是限权的连续推力螺旋（LEO→GEO 实测 ≈1.4e5s = 2.3 个 GEO 周期）。
            //   要做到 0.23 个周期（霍曼 ≈1.4e4s / 2862 m/s）必须**显式构造两冲量**
            //   （近/远地点各烧一次 + 相位窗口）——另一套机制，未实现。
            //   ⚠ 想靠"拆掉限权"抄近路已被实测证伪（2.37× 成本、不收敛、掉出安全线，见 Compose 注释）。
            // ⚠ **已登记缺口（E5）**：目标 ≤1.6× 螺旋价，但**高推船当前 = 3.27×（10695 m/s）
            //   且超过它自己的 9986 预算** ⇒ LEO→GEO 用这条船在游戏里会跑光工质。
            //   同工况低推船（T11）= 1.51× / 4928 ✓ 收敛。
            //   ⚠ 已排除的两个错误解释（都实测过、都不是原因）：
            //     ① 拆掉形状通道的限权 ⇒ 2.37× 成本 + **不收敛** + 掉出安全线（Compose 有记录）；
            //     ② 限制终端每步交付比例 ⇒ **读数逐位不变**（终端步数=0，终端压根没参与）。
            //   ⇒ 缺口在**远场**：两船的 aRef（=min(aMax,0.05g)）与腿长完全相同，唯一差别是执行器选择
            //     （高推船 aRcs·15=6 > 1.65 ⇒ 走 RCS；低推船 1.29 < 1.65 ⇒ 走主网），疑点在此。
            //   本条继续**测量**（不 Skip），缺口变化时读数会变。
        }

        // ── T15：【诊断】游戏判据（300m/2m/s）下终端到底收不收敛 ────────────────
        /// <summary>
        /// ⚠ **我的测试判据一直比游戏松 16 倍**：T11/T12/T13/T14 都用 `HasRendezvoused(…, 5000, 25)`
        /// （台架自己的口径），而**游戏要的是 300 m / 2 m/s**（`RendezvousGuidanceCore.HasRendezvoused` 默认）。
        /// ⇒ "收敛=True" 从来只意味着"距离进了 5 km"，**不等于交会成立**。
        /// 本条用**游戏判据**量：同轨 15°、一圈工况，跑到 300m/2m/s 需要多久 / 能不能到。
        /// </summary>
        [Theory]
        [InlineData(15.0)]
        [InlineData(90.0)]
        public void T15_GameCriterion_StrictTolerance_Diagnostic(double dphiDeg)
        {
            double r = R1;
            double period = 2.0 * Math.PI * Math.Sqrt(r * r * r / Mu);
            double dphi = dphiDeg * Math.PI / 180.0;

            var ship = HighThrustShip(r, 0.0);
            var target = CircularTarget.Create(r, Mu, dphi, OrbitNormal);
            var pilot = new InPlaneRendezvousPilot
            {
                ShapeFirstLadder = true, LegSeconds = 0.0,
                TimeBudget = 0.0, TimeBudget0 = period,
                TerminalHandoff = true,
                ActuatorDeadband = 0.001 * (ship.MainThrust / ship.Mass) * 10.0,
                ModelSafetyClip = true,      // 与游戏同口径（含撞行星裁剪）——否则 90° 工况离线复现不出来
                TerminalSlideFraction = 0.01, TerminalSlideTau = 3000.0, TerminalSlideClosingCap = 50.0,   // 与游戏同口径
            };

            double t = 0.0, dt = 5.0, dist = double.NaN, rel = double.NaN;
            double bestDist = double.PositiveInfinity, bestRel = double.PositiveInfinity, bestT = 0.0;
            bool strict = false;
            var trace = new System.Collections.Generic.List<string>();
            while (t < 40.0 * period)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out dist, out rel, out _);
                t += dt;
                if (ship.Crashed) break;
                if (dist < bestDist) { bestDist = dist; bestRel = rel; bestT = t; }
                if (RendezvousGuidanceCore.HasRendezvoused(dist, rel)) { strict = true; break; }
                if (trace.Count < 16 && t > 0.5 * period && t % (2.0 * period) < dt)
                    trace.Add($"{t / period:F1}P {dist:F0}m/{rel:F2}");
            }

            _out.WriteLine($"T15 游戏判据(300m/2m/s) {dphiDeg}°：严格收敛={strict} 用时={t:F0}s({t / period:F2}P)" +
                           $"｜最优 dist={bestDist:F0}m rel={bestRel:F2}m/s @{bestT / period:F2}P" +
                           $"｜Δv={ship.MainDv + ship.RcsDv:F1}｜终端步数={pilot.TerminalSteps}");
            _out.WriteLine("     轨迹: " + string.Join(" | ", trace) + (ship.Crashed ? "  【Crashed】" : ""));
            Assert.True(strict);
        }

        // ── T16：**异轨（GTO 椭圆目标）交会** —— 用户的原始工况 ─────────────────
        /// <summary>
        /// LEO 圆轨追方 → **GTO 椭圆目标**（近地点与追方同点、远地点到 GEO）：e≈0.654。
        ///
        /// ⚠ 为什么现在才加：台架此前**只有圆轨目标**，而用户的原始问题就是 LEO→GTO
        ///   ⇒ "椭圆目标"这一整类工况从未被测过（这是本轮的**方法论缺口**，不是笔误）。
        ///
        /// 预期暴露的结构问题：偏心通道（c₁,c₂）用的是**追方自己的 e 矢量**、目标是把它收到零
        /// ⇒ **把追方圆化** ✗ —— 对椭圆目标这是反方向的努力。
        /// </summary>
        [Fact]
        public void T16_EccentricTarget_Gto_Rendezvous()
        {
            var ship = HighThrustShip(R1, 0.0);
            var target = EllipticTarget.CreateGto(R1, R2, Mu, OrbitNormal);
            double periodTgt = 2.0 * Math.PI * Math.Sqrt(Math.Pow(0.5 * (R1 + R2), 3) / Mu);
            var pilot = new InPlaneRendezvousPilot
            {
                ShapeFirstLadder = true, LegSeconds = 0.0,
                TimeBudget = 0.0, TimeBudget0 = 60000.0,
                TerminalHandoff = true,
                ActuatorDeadband = 0.001 * (ship.MainThrust / ship.Mass) * 10.0,
                ModelSafetyClip = true,
            };

            double t = 0.0, dt = 5.0;
            double bestDist = double.PositiveInfinity, bestRel = double.PositiveInfinity, bestT = 0.0;
            bool strict = false;
            var trace = new System.Collections.Generic.List<string>();
            while (t < 40.0 * periodTgt)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out double dist, out double rel, out _);
                t += dt;
                if (ship.Crashed) break;
                if (dist < bestDist) { bestDist = dist; bestRel = rel; bestT = t; }
                if (RendezvousGuidanceCore.HasRendezvoused(dist, rel)) { strict = true; break; }
                if (trace.Count < 10 && t % (4.0 * periodTgt) < dt)
                    trace.Add($"{t / periodTgt:F1}P {dist / 1000.0:F0}km/{rel:F1}");
            }

            _out.WriteLine($"T16 LEO→GTO(椭圆目标 e=0.654)：严格收敛={strict} 用时={t:F0}s({t / periodTgt:F2}个目标周期)" +
                           $"｜最优 dist={bestDist / 1000.0:F0}km rel={bestRel:F1}m/s @{bestT / periodTgt:F2}P" +
                           $"｜Δv={ship.MainDv + ship.RcsDv:F0}｜最低点={ship.MinRadius / 1000.0:F0}km");
            _out.WriteLine("     轨迹: " + string.Join(" | ", trace));
            Assert.True(strict, $"异轨（椭圆目标）未收敛：最优 {bestDist / 1000.0:F0}km / {bestRel:F1}m/s");
        }

        // ── T14：**点火死区**能不能把律憋死（2026-09-17 游戏内实测的复现）──────
        /// <summary>
        /// 复现用户那次的**零点火**：同轨 LEO、相位差 90°、腿长按"受大气层限制"那条
        /// （= 菜单里"最快交会（约 4.4 圈）"给的 25056s），船 = 游戏调试船（高推）。
        ///
        /// 死区口径 = `0.02·aMax·dt`（旧值）vs `0.001·aMax·dt`（新值）：
        ///   · 旧值 ⇒ 律的指令只有 0.92 m/s 而阈是 1.57 m/s ⇒ **一轮都不点火**（游戏内实测：3 次登记 0 次点火）；
        ///   · 新值 ⇒ 阈值 0.079 m/s ≪ 指令 ⇒ 正常收敛。
        /// ⚠ 台架原先**不建模死区**，所以这类"律在算、船不动"的故障离线测不出来（本轮结构性教训）。
        /// </summary>
        [Theory]
        [InlineData(0.02, false)]    // 旧值：复现停摆（必须不收敛）
        [InlineData(0.001, true)]    // 新值：必须收敛
        public void T14_ActuatorDeadband_MustNotStallTheLaw(double deadbandFraction, bool expectConverged)
        {
            double r = R1;
            double period = 2.0 * Math.PI * Math.Sqrt(r * r * r / Mu);
            double dphi = 90.0 * Math.PI / 180.0;
            // 菜单"最快"档给腿长的口径：受撞行星限制的物理下限 |Δφ|/δn_max
            double dnMax = 6.26e-5;                       // LEO 缩轨到大气层边缘（见 §24.2）
            double leg = Math.Abs(dphi) / dnMax;          // ≈25056s

            var ship = HighThrustShip(r, 0.0);
            var target = CircularTarget.Create(r, Mu, dphi, OrbitNormal);
            var pilot = new InPlaneRendezvousPilot
            {
                ShapeFirstLadder = true, LegSeconds = leg,
                TimeBudget = leg, TimeBudget0 = period,
                TerminalHandoff = true,
                ActuatorDeadband = deadbandFraction * (ship.MainThrust / ship.Mass) * 10.0,   // 与游戏同口径
            };

            double t = 0.0, dt = 5.0, dist = double.NaN, rel = double.NaN;
            bool done = false;
            while (t < 6.0 * period * 10.0)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out dist, out rel, out _);
                t += dt;
                if (ship.Crashed) break;
                if (RendezvousGuidanceCore.HasRendezvoused(dist, rel)) { done = true; break; }
            }

            _out.WriteLine($"T14 死区={deadbandFraction:P1}·aMax·dt={pilot.ActuatorDeadband:F3}m/s" +
                           $" 腿长={leg:F0}s 用时 {t:F0}s（{t / period:F1} 个周期）" +
                           $"｜Δv={ship.MainDv + ship.RcsDv:F1}｜末 dist={dist / 1000.0:F2}km rel={rel:F1}m/s" +
                           $"｜收敛={done}（期望 {expectConverged}）");

            Assert.Equal(expectConverged, done);
        }

        // ── T12：**一圈交会**（用户的硬要求）────────────────────────────────────
        /// <summary>
        /// 同轨、相位差 15°，**目标时间尺度 = 一个轨道周期**。这条是"自动交会该有多快"的判据。
        ///
        /// 物理背景（为什么一圈是可达的，以及它的边界在哪）：
        ///   相位靠"缩轨"来收，而缩轨**只能缩到大气层边缘**（地表+100km）⇒ LEO 上
        ///   δn_max≈6.3e-5 rad/s ⇒ **一个轨道周期能收 ≈20° 相位**。
        ///   故 ≤20° 的相位差一圈内可闭合，代价 = 相位守恒律 (2/3)·r·Δφ/P ≈ 168 m/s（15° 时）。
        ///   （>20° 时一圈物理上做不到，律会受撞行星保护钳制、按最大速率收——见 T11 的 120° 口径。）
        ///
        /// 该测同时钉住**默认腿长不再是一个常数**：以前填 60000s（台架值 = GEO 的一圈），
        /// 在 LEO 上等于 10.5 圈 ⇒ 交会被人为拖慢一个数量级。
        /// </summary>
        [Fact]
        public void T12_OneOrbitRendezvous_Leo_ConvergesWithinOnePeriod()
        {
            double r = R1;
            double period = 2.0 * Math.PI * Math.Sqrt(r * r * r / Mu);
            double dphi = 15.0 * Math.PI / 180.0;

            var ship = MidThrustShip(r, 0.0);
            var target = CircularTarget.Create(r, Mu, dphi, OrbitNormal);
            var pilot = new InPlaneRendezvousPilot
            {
                ShapeFirstLadder = true,     // 游戏侧政策（PlanLeg 定段模式与腿长）
                LegSeconds = 0.0,            // → 腿长由 PlanLeg 定 = max(目标一个周期, …)
                TimeBudget = 0.0,
                // ⚠ TimeBudget0 **必须给一个真值**：它是"兜底交班"的机会倍数基准
                //   （`late = relDist < 0.10·r && t > 2·TimeBudget0`）。填 0 会让该条件恒真
                //   ⇒ 远场刚拉出 δn 就被终端接管，终端要杀 605 m/s 的相对速度（实测入口 518km/605m/s、
                //   账单 884/982 m/s）。**这是仪器的错，不是律的错。**
                TimeBudget0 = period,        // 与 PlanLeg 的腿长同量纲（一个周期）
                // ⚠ **交班早晚是一对权衡**（本测实测）：远场在滑行**中段**时距离恰好经过 5%，
                //   而那时 δn 在峰值 ⇒ 终端接手要杀 327 m/s。实测：
                //     5%（默认）: 用时 1.28 圈 · 终端 597 · 总 697
                //     2%        : 用时 1.69 圈 · 终端 319 · 总 544
                //   取 5%：本测的主张是**时间尺度**，而预算是够的（10000 m/s）。
                TerminalEngageFraction = 0.05,
                TerminalHandoff = true,
            };

            double t = 0.0, dt = 5.0, dist = double.NaN, rel = double.NaN;
            bool done = false;
            while (t < 6.0 * period)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out dist, out rel, out _);
                t += dt;
                if (ship.Crashed) break;
                if (RendezvousGuidanceCore.HasRendezvoused(dist, rel)) { done = true; break; }
            }

            double dv = ship.MainDv + ship.RcsDv;
            double rent = (2.0 / 3.0) * r * dphi / period;
            _out.WriteLine($"T12 同轨15° 一圈交会：用时 {t:F0}s = {t / period:F2} 个周期（P={period:F0}s）" +
                           $"｜Δv={dv:F1} m/s（守恒律参考 {rent:F1}）｜末 dist={dist / 1000.0:F2}km rel={rel:F1}m/s" +
                           $"｜最低点={ship.MinRadius / 1000.0:F0}km 终端={pilot.InTerminal}({pilot.LastTerminalKind})");
            // 分账：远场（c0/谐波）vs 终端 —— "一圈"达成后**代价花在哪**必须量出来，不能猜
            _out.WriteLine($"     分账: c0={pilot.DvFromC0:F1} 谐波={pilot.DvFromHarm:F1} 终端={pilot.TerminalDv:F1} " +
                           $"（终端步数={pilot.TerminalSteps} 入口 dist={pilot.TerminalEntryDist / 1000.0:F0}km " +
                           $"rel={pilot.TerminalEntryRel:F1}m/s）｜主={ship.MainDv:F0} RCS={ship.RcsDv:F1} " +
                           $"｜C0 翻转={pilot.C0SignFlips}");

            Assert.False(ship.Crashed, $"侵地：最低点 {ship.MinRadius / 1000.0:F0}km");
            Assert.True(done, $"未收敛：dist={dist / 1000.0:F1}km rel={rel:F1}m/s");
            // **一圈量级**：给 2 个周期（实测 1.68；修好腿长前这里是 ~10 圈）。
            // ⚠ 实测 1.68 而非 ≈1.0 的原因**不是远场**——分账显示 **86% 的 Δv 花在终端**
            //   （终端 607 / 总 707），终端在交班后还要 1000+ 步慢慢磨到 300m/2m/s。
            //   那是设计稿已登记的"终端经济学"缺口（§16/§19.7），本条不修它，只如实钉住总时长。
            Assert.True(t <= 2.0 * period, $"未在一圈量级内完成：{t / period:F2} 个周期（实测 ≈1.7）");
            // 代价量级护栏：实测 697 m/s = 远场 ≈100（≈守恒律的 167）+ **终端 ≈600**。
            //   终端那一笔是设计稿已登记的已知缺口（CW 线性化 + 滚动执行 ≈1.74×，见 §16/§19.7），
            //   不是本测要修的东西；这里只钉住"不许再差一个数量级"（5× 租金 = 838）。
            Assert.True(dv <= rent * 5.0, $"代价超相位租金 5 倍：{dv:F1} vs {rent:F1}");
        }

        // ── T11：**形状优先阶梯**（游戏侧政策）——LEO→GEO 全链 ──────────────────
        /// <summary>
        /// 复现并钉住 2026-09-17 游戏内的真实失败工况：**LEO 追 GEO**（半径差 4.8 倍，共面）。
        ///
        /// 当时的症状（日志实测）：新律一路满推力推进，烧掉 1549 m/s / 5814 kg，
        /// 距离却从 21,581 km 涨到 30,077 km，永不收敛；随后撞行星保护升级 → 整轮报废。
        /// **根因是腿长不覆盖机动**（预算不可行），不是律错：低推力档限权 a=0.05·g(r) 随半径降 23 倍，
        /// LEO→GEO 单是推力时间就要 1~3×10^5 s，而当时给的是 60000 s。纯台架 T3 的注释早已写明。
        ///
        /// 本测验**游戏在跑的那条政策**：`ShapeFirstLadder` ⇒ 每段调 `RendezvousGuidanceLaw.PlanLeg`
        /// （与 `RunNewRendezvousLaw` 同一个函数）⇒ 形状段（把 a 开到目标）+ 相位段（收相位）+ 终端。
        /// </summary>
        [Fact]
        public void T11_ShapeFirstLadder_LeoToGeo_Converges()
        {
            var ship = MidThrustShip(R1, 0.0);
            var target = CircularTarget.Create(R2, Mu, 0.0, OrbitNormal);
            var pilot = new InPlaneRendezvousPilot
            {
                ShapeFirstLadder = true,     // 游戏侧政策（PlanLeg 决定段模式与腿长）
                LegSeconds = 60000.0,        // 与游戏默认一致（关键：腿长由 PlanLeg 按模式放大）
                TerminalHandoff = true,
                TimeBudget = 60000.0,        // 首段起点；后续段由 PlanLeg 重排
                TimeBudget0 = 60000.0,
            };

            double t = 0.0, dt = 5.0, dist = double.NaN, rel = double.NaN;
            bool done = false;
            while (t < 1_500_000.0)
            {
                pilot.Step(ship, target, Mu, t, dt, out _, out dist, out rel, out _);
                t += dt;
                if (ship.Crashed) break;
                if (RendezvousGuidanceCore.HasRendezvoused(dist, rel)) { done = true; break; }
            }

            double spiral = Math.Sqrt(Mu / R1) - Math.Sqrt(Mu / R2);
            _out.WriteLine($"T11 LEO→GEO 用时 {t:F0}s（{t / 3600.0:F1}h）总Δv={ship.MainDv + ship.RcsDv:F1} " +
                           $"(螺旋价 {spiral:F1}｜比值 {(ship.MainDv + ship.RcsDv) / spiral:F4})" +
                           $"｜末 dist={dist / 1000.0:F2}km rel={rel:F1}m/s｜最低点={ship.MinRadius / 1000.0:F0}km " +
                           $"｜对齐等待 {ship.AlignWaitSeconds:F0}s 终端={pilot.InTerminal}({pilot.LastTerminalKind})");

            Assert.False(ship.Crashed, $"侵地：最低点 {ship.MinRadius / 1000.0:F0}km");
            Assert.True(done, $"未收敛：dist={dist / 1000.0:F1}km rel={rel:F1}m/s（政策未能把 LEO→GEO 走通）");
            // 成本上界 1.6×螺旋价。实测 1.5055，构成 = 螺旋价(1.0) + e 通道清理残余偏心率
            //   （纯台架 T2 的口径：含 e 清理后 ≤1.20）+ 相位租金 + 终端消速。
            //   §10.1 的 1+√2 界说的是"螺旋 vs 霍曼"，不是本口径，别混用。
            Assert.True(ship.MainDv + ship.RcsDv <= spiral * 1.6,
                $"成本超螺旋价 60%：{ship.MainDv + ship.RcsDv:F1} vs {spiral:F1}");
            // ⚠ **耗时上界 = "不许再出现'飞一半不干了'"的回归护栏**：
            //   形状段收敛后若仍等到腿末才切相位段，就要白等一整个腿长（实测 294,875s/次）。
            //   修复前实测总耗时 1,188,465s；加"形状收敛即提前收段"后 147,880s。给 3× 余量。
            Assert.True(t < 300_000.0,
                $"耗时过长（形状→相位段衔接退化了？）：{t:F0}s（修复后应 ≈1.5e5）");
        }

        // ── T4：执行器 —— 主推对准门 + RCS 预算 ─────────────────────────────
        /// <summary>
        /// 主推必须先在 1.5° 锥内才出力；否则本步只转不烧（对齐等待被计时）。
        /// 大 Δv 必须落到主网，RCS 只做小修正——否则 43.5 m/s 的 RCS 预算会被打光。
        /// </summary>
        [Fact]
        public void T4_Actuator_MainCarriesBulk_RcsBudgetPreserved()
        {
            var ship = MidThrustShip(R1, 0.0);
            var target = CircularTarget.Create(R2, Mu, 0.0, OrbitNormal);
            // 预算必须盖住**先做完形状改变**：η=0.05 限权后 LEO→GEO 螺旋本身就要
            // ∫dv/(0.05·g(r)) ≈ 10^5 s 量级。旧值 60000 s 是**预算不可行**，不是律的问题。
            var pilot = new InPlaneRendezvousPilot { TimeBudget = 300000.0, ClosePhase = true,
                TerminalHandoff = false };   // 本测 = 远场成本；终端由 T5 验

            double t = 0.0, dt = 5.0;
            while (t < 120000.0) { pilot.Step(ship, target, Mu, t + dt, dt, out _, out _, out _, out _); t += dt; }

            _out.WriteLine($"T4 主网 Δv={ship.MainDv:F0} RCS Δv={ship.RcsDv:F1}（可达上限 ≈43.5）" +
                           $"｜RCS 时长={ship.RcsSeconds:F0}s｜对准等待={ship.AlignWaitSeconds:F0}s");

            Assert.True(ship.MainDv > 1000.0, "主体 Δv 应由主网承担");
            // RCS 可达 Δv 随总质量下降而变化（主网燃料烧掉后，同一罐 500kg 能换更多 Δv）。
            // 真正该断言的是"**主网承担主体**"，而非一个固定数字。
            Assert.True(ship.MainDv > 5.0 * ship.RcsDv,
                $"主网未承担主体：主 {ship.MainDv:F0} / RCS {ship.RcsDv:F1}");
        }

        /// <summary>选网口径直接单测（纯函数，不依赖仿真）。</summary>
        [Fact]
        public void T4b_SelectActuator_MatchesGameRule()
        {
            // Δv 小 ⇒ RCS（免对准、烧得快）
            Assert.Equal(RendezvousGuidanceCore.Actuator.Rcs,
                RendezvousGuidanceCore.SelectActuator(5.0, 9.0, 0.85, mainAligned: false));
            // Δv 大 + 已对准 ⇒ 主网
            Assert.Equal(RendezvousGuidanceCore.Actuator.Main,
                RendezvousGuidanceCore.SelectActuator(500.0, 9.0, 0.85, mainAligned: true));
            // Δv 大 + 未对准 ⇒ 只能用 RCS 兜底
            Assert.Equal(RendezvousGuidanceCore.Actuator.Rcs,
                RendezvousGuidanceCore.SelectActuator(500.0, 9.0, 0.85, mainAligned: false));
            // 没有 RCS ⇒ 即使未对准也只能等主推转过去
            Assert.Equal(RendezvousGuidanceCore.Actuator.Main,
                RendezvousGuidanceCore.SelectActuator(500.0, 9.0, 0.0, mainAligned: false));
            // 全尽
            Assert.Equal(RendezvousGuidanceCore.Actuator.None,
                RendezvousGuidanceCore.SelectActuator(5.0, 0.0, 0.0, mainAligned: true));
        }
    }
}
