using System;
using System.Text;
using MechJebLib.Lambert;
using MechJebLib.Maths;   // `Izzo`（4e2451b291cf89e3Ff09
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **近距拦截对照台架（**判定"不换 CW"的证据**）：，Lambert vs CW（Hill 闭式解）** —— 2026-10-01。
    ///
    /// ## 要回答的问题
    /// 用户："离得足够近了以后是不是应该换一套引导律，而不是迂腐地兰伯特？"
    /// 现场读数（G4 那一局）：那 41 条"落点偏 1–12 km"的坏计划，全落在 **相对半径 0.15%–0.87%** 的带里
    /// （而归档的 `CwRendezvousCore` 自述适用界 = **5% 以内**）⇒ 本条就是量这条边界。
    ///
    /// ## 怎么量（口径）
    /// · **两体真值** = `Shepperd.Solve`（独立推进）；两臂都拿它当裁判；
    /// · **Lambert 臂**：从追击者到"目标在 now+ToF 的位置"的两冲量（`Izzo`，与阶梯 `Intercept` 同族工具），
    ///   取 Δv1 ⇒ 用两体推进到 now+ToF ⇒ 与目标真实位置比 ⇒ **落点误差**（= 阶梯那条 `独立积分到达偏差` 的同口径）；
    /// · **CW 臂**：相对状态（RTN 系）⇒ `CwRendezvousCore.Solve(..., n, ToF)` 取 Δv1 ⇒ 同样两体推进比落点；
    /// · **ToF 政策与阶梯一致**：`ToF = dist / 100`（`MaxClosingSpeed=100` 那条上限）。
    ///
    /// ⚠ 本台架**只量解法质量**，不量阶梯的完整规划（少了 rung 选择/相位窗口等）⇒ 结论只用于"该不该在近距换解算器"。
    /// ⚠ 只打读数、不断言时间；误差本身是判据（打印出来给人看）。
    /// </summary>
    public class CwVsLambertCloseRangeTests
    {
        private readonly ITestOutputHelper _out;
        public CwVsLambertCloseRangeTests(ITestOutputHelper o) => _out = o;

        private static readonly double Mu = OrbitMath.GM;   // `OrbitMath.GM` 不是编译期常量
        private const double RTgt = 6_300_000.0;          // = G4 的目标半径（6300 km）
        private static readonly double N = Math.Sqrt(Mu / (RTgt * RTgt * RTgt));   // 平均角速度

        /// <summary>目标作圆轨；追击者在同一条轨道上**沿迹**偏移 <paramref name="alongM"/> 米（同轨追击 = G4 的形态）。</summary>
        private static (V3 rc, V3 vc, V3 rt, V3 vt) Setup(double alongM)
        {
            double vc0 = Math.Sqrt(Mu / RTgt);
            V3 rT = new V3(RTgt, 0, 0);
            V3 vT = new V3(0, 0, -vc0);                    // 顺行（h ∥ +Y，与其它台架同约定）
            // 沿迹偏移：把追击者放在目标"后面"一个角
            double dTheta = alongM / RTgt;
            V3 rC = new V3(RTgt * Math.Cos(dTheta), 0, -RTgt * Math.Sin(dTheta));
            V3 vC = new V3(vc0 * Math.Sin(dTheta), 0, -vc0 * Math.Cos(dTheta));
            return (rC, vC, rT, vT);
        }

        /// <summary>两体推进到 t 秒后（5 s 步）——即"独立积分"那把尺。</summary>
        private static V3 Propagate(V3 r, V3 v, double t)
        {
            const double step = 5.0;
            int n = (int)Math.Round(t / step);
            for (int i = 0; i < n; i++) { (r, v) = Shepperd.Solve(Mu, step, r, v); }
            return r;
        }

        /// <summary>把惯性矢量写进 RTN 系（r̂ 径向 / t̂ 沿迹 / ĥ 法向）。</summary>
        private static V3 ToRtn(V3 d, V3 rHat, V3 tHat, V3 hHat)
            => new V3(V3.Dot(d, rHat), V3.Dot(d, tHat), V3.Dot(d, hHat));

        private static V3 FromRtn(V3 d, V3 rHat, V3 tHat, V3 hHat)
            => d.x * rHat + d.y * tHat + d.z * hHat;

        [Fact]
        public void CloseRange_LambertVsCw_TwoBodyMissSweep()
        {
            var sb = new StringBuilder();
            sb.AppendLine("近距拦截：Lambert vs CW（两体真值；ToF = dist/100，与阶梯同政策）");
            sb.AppendLine("  dist(km)  dist/r     ToF(s)   ToF/T    Lambert 落点误差(m)   CW 落点误差(m)   Lambert|Δv1|  CW|Δv1|");

            foreach (double dkm in new[] { 1.0, 2.0, 5.0, 10.0, 20.0, 40.0, 80.0, 160.0 })
            {
                double dist = dkm * 1000.0;
                double T = dist / 100.0;                       // 阶梯的 ToF 政策（MaxClosingSpeed = 100）
                (V3 rc, V3 vc, V3 rt, V3 vt) = Setup(dist);

                // 目标的 RTN 基（沿迹偏移 ⇒ 相对位置主要在 t̂ 上）
                V3 rHat = rt.normalized;
                V3 hHat = V3.Cross(rt, vt).normalized;
                V3 tHat = V3.Cross(hHat, rHat);

                V3 tAfter = tHat;                              // 目标圆轨：RTN 基随时间只是绕 ĥ 转
                V3 rtFuture = Propagate2(rt, vt, T, out V3 vtFuture);

                // ── Lambert 臂（= 阶梯 Intercept 的同族工具：Izzo）──
                double lambertMiss = double.NaN, lambertDv = double.NaN;
                try
                {
                    V3 hC = V3.Cross(rc, vc).normalized;
                    (V3 vi, V3 vf) = Izzo.Solve(Mu, rc, rtFuture, T, TransferGeometry.Prograde, 0, hC);
                    V3 dv1 = vi - vc;
                    V3 rArr = Propagate(rc, vc + dv1, T);
                    lambertMiss = (rArr - rtFuture).magnitude;
                    lambertDv = dv1.magnitude;
                }
                catch (Exception ex) { lambertMiss = double.NaN; sb.AppendLine($"      （Lambert 抛 {ex.GetType().Name}）"); }

                // ── CW 臂（归档的闭式解）──
                V3 rel0 = rc - rt, vel0 = vc - vt;
                V3 relRtn = ToRtn(rel0, rHat, tHat, hHat);
                V3 velRtn = ToRtn(vel0, rHat, tHat, hHat);
                double cwMiss = double.NaN, cwDv = double.NaN;
                if (CwRendezvousCore.Solve(relRtn, velRtn, N, T, out V3 dv1r, out V3 dv2r))
                {
                    V3 dv1 = FromRtn(dv1r, rHat, tHat, hHat);
                    V3 rArr = Propagate(rc, vc + dv1, T);
                    cwMiss = (rArr - rtFuture).magnitude;
                    cwDv = dv1.magnitude;
                }

                sb.AppendLine($"  {dkm,7:F0}  {dist / RTgt,8:E2}  {T,7:F0}  {T * N / (2 * Math.PI),7:E2}  "
                            + $"{lambertMiss,18:E3}  {cwMiss,12:E3}  {lambertDv,12:F2}  {cwDv,7:F2}");
            }

            _out.WriteLine(sb.ToString().TrimEnd());
        }

        /// <summary>推进并顺带给出末端速度（供构造末端 RTN 基）。</summary>
        private static V3 Propagate2(V3 r, V3 v, double t, out V3 vEnd)
        {
            const double step = 5.0;
            int n = (int)Math.Round(t / step);
            for (int i = 0; i < n; i++) { (r, v) = Shepperd.Solve(Mu, step, r, v); }
            vEnd = v;
            return r;
        }
    }
}
