using System;
using System.Text;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **近场终端律的离线判据**（2026-10-01）：`TerminalRendezvousLaw`（位置+速度联合）在**两体真值**下
    /// 能不能把"进门时的粗状态"收口到 (距离 ≤ 300 m, 相对速度 ≤ 2 m/s)。
    ///
    /// ## 为什么要这条（现场）
    /// 地球·低推力那条：距离 2↔21 km 反复弹、58 次 `BLOCKED` 烧干。成对读数指着"刹车只对速度、不对位置"
    /// （§10.30）⇒ 新律要**同时**收位置与速度。这条台架就在两体真值上验它 —— **且必须带"有限燃烧"臂**：
    /// 现实执行器是有限燃烧（低推力档 ~0.3 m/s²），脉冲近似的结论不算。
    ///
    /// ## 判据
    /// 对每一组初值（门内：距离 × 相对速度 × 三个方向）：
    /// · **脉冲臂**与**有限燃烧臂**都必须在 `TimeCap` 内达到 `Settled`（300 m / 2 m/s）；
    /// · 单调性不做硬判据（允许小波动），但**不许发散**（时间上限就是它的界）。
    /// 读数（用时 / Δv 累计）打出来 —— 它是"温柔不温柔"的账。
    /// </summary>
    public class TerminalRendezvousLawTests
    {
        private readonly ITestOutputHelper _out;
        public TerminalRendezvousLawTests(ITestOutputHelper o) => _out = o;

        private static readonly double Mu = OrbitMath.GM;
        private const double RTgt = 6_300_000.0;
        private const double Dt = 2.0;                 // 物理步（与游戏一致）
        private const double TimeCap = 20000.0;        // 判据时间上限（s）——"柔参数"档的接近很慢，故放宽
        private const double LowThrustAccel = 0.30;    // 低推力档（现场 G2 实测 0.28–0.74 m/s²）

        /// <summary>目标圆轨（顺行；h ∥ +Y，与其它台架同约定）。</summary>
        private static (DVector3 r, DVector3 v) Target()
        {
            double vc = Math.Sqrt(Mu / RTgt);
            return (new DVector3(RTgt, 0, 0), new DVector3(0, 0, -vc));
        }

        private static (DVector3 rHat, DVector3 tHat, DVector3 hHat) RtnBasis(DVector3 r, DVector3 v)
        {
            DVector3 rHat = r.normalized;
            DVector3 hHat = DVector3.Cross(r, v).normalized;
            return (rHat, DVector3.Cross(hHat, rHat), hHat);
        }

        private readonly struct Result
        {
            public readonly bool Ok; public readonly double T, Dv, Dist, Rel;
            public Result(bool ok, double t, double dv, double dist, double rel) { Ok = ok; T = t; Dv = dv; Dist = dist; Rel = rel; }
        }

        /// <summary>跑一次终端：<paramref name="finiteBurn"/> = true 时把每拍 Δv 按低推力**摊到多拍**（执行器口径）。</summary>
        private static Result Run(DVector3 relPos0, DVector3 relVel0, bool finiteBurn, TerminalRendezvousLaw.Params p)
        {
            (DVector3 rt, DVector3 vt) = Target();
            (DVector3 rHat, DVector3 tHat, DVector3 hHat) = RtnBasis(rt, vt);
            DVector3 rc = rt + relPos0.x * rHat + relPos0.y * tHat + relPos0.z * hHat;
            DVector3 vc = vt + relVel0.x * rHat + relVel0.y * tHat + relVel0.z * hHat;

            double dvTotal = 0.0;

            for (double t = 0.0; t <= TimeCap; t += Dt)
            {
                (rc, vc) = Shepperd.Solve(Mu, Dt, rc, vc);
                (rt, vt) = Shepperd.Solve(Mu, Dt, rt, vt);
                (rHat, tHat, hHat) = RtnBasis(rt, vt);

                DVector3 dPos = rc - rt, dVel = vc - vt;
                DVector3 relPosRtn = new DVector3(DVector3.Dot(dPos, rHat), DVector3.Dot(dPos, tHat), DVector3.Dot(dPos, hHat));
                DVector3 relVelRtn = new DVector3(DVector3.Dot(dVel, rHat), DVector3.Dot(dVel, tHat), DVector3.Dot(dVel, hHat));

                double dist = relPosRtn.magnitude, rel = relVelRtn.magnitude;
                if (TerminalRendezvousLaw.Settled(dist, rel, p)) return new Result(true, t, dvTotal, dist, rel);

                double n = Math.Sqrt(Mu / Math.Pow(rt.magnitude, 3.0));
                DVector3 dvRtn = TerminalRendezvousLaw.ComputeDv(relPosRtn, relVelRtn, n, Dt, p);

                if (!finiteBurn)
                {
                    DVector3 dv = dvRtn.x * rHat + dvRtn.y * tHat + dvRtn.z * hHat;
                    vc += dv; dvTotal += dv.magnitude;
                }
                else
                {
                    // 有限燃烧：**速率限制**——每拍最多给 aMax·dt，方向按**本拍重算的指令**（真实执行器就是这个模型：
                //   它每拍朝"当前指令"推进一点，**不累计欠账**；第一版把欠账累加 ⇒ 指令反向时虚增 Δv ✗）
                    double give = Math.Min(LowThrustAccel * Dt, dvRtn.magnitude);
                    if (dvRtn.sqrMagnitude > 0.0)
                    {
                        DVector3 u = dvRtn.normalized;
                        DVector3 dv = (u.x * rHat + u.y * tHat + u.z * hHat) * give;
                        vc += dv; dvTotal += give;
                    }
                }
            }
            return new Result(false, TimeCap, dvTotal, 0, 0);
        }

        [Fact]
        public void TerminalLaw_Converges_FromInsideTheGate_PulseAndLowThrust()
        {
            var cases = new (string name, DVector3 pos, DVector3 vel)[]
            {
                ("径向 20 km / 20 m/s 外推", new DVector3(20_000, 0, 0), new DVector3(20, 0, 0)),
                ("沿迹 20 km / 100 m/s 追赶", new DVector3(0, 20_000, 0), new DVector3(0, -100, 0)),
                ("沿迹 50 km / 300 m/s 追赶", new DVector3(0, 50_000, 0), new DVector3(0, -300, 0)),
                ("面外 10 km / 50 m/s", new DVector3(0, 0, 10_000), new DVector3(0, 0, -50)),
                ("混合 5 km / 100 m/s（现场循环那档）", new DVector3(3_000, 4_000, 0), new DVector3(20, -98, 0)),
                ("门角 2 km / 10 m/s（应当很快）", new DVector3(0, 2_000, 0), new DVector3(0, 10, 0)),
            };

            // 参数档：**时间 ↔ 燃料**的取舍表（默认值由它定，别拍脑袋）
            var sets = new (string name, TerminalRendezvousLaw.Params p)[]
            {
                ("凶（τ=60s 上限 100）", new TerminalRendezvousLaw.Params()),
                ("中（τ=200s 上限 50）", new TerminalRendezvousLaw.Params { TimeConstant = 200.0, VelocityTimeConstant = 100.0, ClosingSpeedCap = 50.0 }),
                ("柔（τ=600s 上限 20）", new TerminalRendezvousLaw.Params { TimeConstant = 600.0, VelocityTimeConstant = 300.0, ClosingSpeedCap = 20.0 }),
            };

            int fail = 0;
            foreach (var set in sets)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"── 参数档 {set.name} ──");
                sb.AppendLine("  初值                                脉冲臂(用时/Δv)          低推力臂(0.30 m/s²)");
                foreach (var c in cases)
                {
                    var pulse = Run(c.pos, c.vel, finiteBurn: false, set.p);
                    var low = Run(c.pos, c.vel, finiteBurn: true, set.p);
                    sb.AppendLine($"  {c.name,-34} " +
                                  $"{(pulse.Ok ? $"✓ {pulse.T,6:F0}s Δv={pulse.Dv,7:F1}" : "✗ 未收口"),-24} " +
                                  $"{(low.Ok ? $"✓ {low.T,6:F0}s Δv={low.Dv,7:F1}" : "✗ 未收口")}");
                    if (!pulse.Ok || !low.Ok) fail++;
                }
                _out.WriteLine(sb.ToString().TrimEnd());
            }

            Assert.True(fail == 0, $"有 {fail} 组没在 {TimeCap:F0}s 内收口（脉冲臂或低推力臂）⇒ 律不成立");
        }

        [Fact]
        public void TerminalLaw_IsQuietWhenSettled_AndGateIsCoarse()
        {
            var p = new TerminalRendezvousLaw.Params();
            // 已收口 ⇒ 不再发指令（避免在噪声里抖）
            DVector3 dv = TerminalRendezvousLaw.ComputeDv(new DVector3(50, 0, 0), new DVector3(0.1, 0, 0),
                Math.Sqrt(Mu / Math.Pow(RTgt, 3.0)), Dt, p);
            Assert.True(dv.magnitude < 0.5, $"已收口时仍在发 {dv.magnitude:F2} m/s 的指令 ⇒ 会在噪声里抖");

            // 粗条件：门内 → true；出界 → false
            Assert.True(TerminalRendezvousLaw.GateMet(49_000, 299, p));
            Assert.False(TerminalRendezvousLaw.GateMet(51_000, 100, p));
            Assert.False(TerminalRendezvousLaw.GateMet(10_000, 301, p));
            _out.WriteLine($"门：dist ≤ {p.GateDistance:F0} m 且 rel ≤ {p.GateRelVel:F0} m/s｜τ={p.TimeConstant:F0}s" +
                           $"｜接近率上限={p.ClosingSpeedCap:F0}｜死区={p.Deadband:F0} m");
        }
    }
}
