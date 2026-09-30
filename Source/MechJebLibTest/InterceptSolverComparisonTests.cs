using System;
using System.Text;
using MechJebLib.Lambert;
using MechJebLib.Maths;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **拦截解算器两臂对照**（2026-10-01 建；**同日改过一次结论**，两版都留档）——
    /// 现在它只说一件事：**"同轨·沿迹短弧"带里，`Gooding` 与 `Izzo` 都是精确的**（1e-8 m 级）。
    ///
    /// ## ⚠ 它曾给过一个**错的**结论（留着当教训）
    /// 第一版写着"`Gooding` 偏 5.6× 距离（1 km 处偏 56 km）"，并据此把生产入口换成了 `Izzo`。**那是错的**：
    /// 代码把两个解算器返回的"**r1 处的速度矢量**"当成 Δv 直接加了（应为 `Δv = v1 − v1_当前`）
    /// ⇒ **两个库给出同一个偏差**——一看两个库同时偏出同一个数，就该先怀疑仪器，我却先怀疑了库。
    /// 游戏内实跑（MVID `49f2d34e…`）也否掉了那次换法：阶梯自己的 `独立积分到达偏差` 分布**没变**
    /// （仍 67 条 ≥1 km）、规划量还更差（计划 Δv 12827 → 20027 m/s、`BLOCKED` 124 次）⇒ **改动当场撤回**。
    /// （那条 km 读数的真因 = 那台仪器**用 5 s 定步长**、总时长与 `closingTime` 最多差 2.5 s，而目标轨速 5.6 km/s
    /// ⇒ 1 s 差 = 5.6 km **假红**；已在 `RendezvousLadder` 里修成"整段 5 s + 余数一步"。）
    ///
    /// ## 判据
    /// 同带、两体真值（`Shepperd` 独立推进当裁判，与两个解算器都不同族）：**两臂都 < dist/1000**。
    /// 用途 = 给这条带留一把**已校准**的尺：将来真怀疑解算器，先跑它，别先改生产。
    /// </summary>
    public class InterceptSolverComparisonTests
    {
        private readonly ITestOutputHelper _out;
        public InterceptSolverComparisonTests(ITestOutputHelper o) => _out = o;

        private static readonly double Mu = OrbitMath.GM;
        private const double RTgt = 6_300_000.0;            // = 地球那条的目标半径
        private const double MaxClosingSpeed = 100.0;       // = 阶梯 `Params.MaxClosingSpeed`

        /// <summary>目标圆轨 + 追击者同轨沿迹偏移 <paramref name="alongM"/> 米。</summary>
        private static (OrbitView chaser, OrbitView target) Setup(double alongM)
        {
            double vc0 = Math.Sqrt(Mu / RTgt);
            var rT = new V3(RTgt, 0, 0);
            var vT = new V3(0, 0, -vc0);
            double dTheta = alongM / RTgt;
            var rC = new V3(RTgt * Math.Cos(dTheta), 0, -RTgt * Math.Sin(dTheta));
            var vC = new V3(vc0 * Math.Sin(dTheta), 0, -vc0 * Math.Cos(dTheta));
            return (OrbitView.FromStateVectors(rC, vC, Mu, 0.0, new V3(0, 0, 1), 0.0),
                    OrbitView.FromStateVectors(rT, vT, Mu, 0.0, new V3(0, 0, 1), 0.0));
        }

        /// <summary>两体推进到 t 秒（5 s 步）——与两个解算器都不同族的裁判。</summary>
        private static V3 Propagate(V3 r, V3 v, double t)
        {
            const double step = 5.0;
            int n = (int)Math.Round(t / step);
            for (int i = 0; i < n; i++) (r, v) = Shepperd.Solve(Mu, step, r, v);
            return r;
        }

        [Fact]
        public void BothLambertSolvers_AreExactInTheCloseRangeBand()
        {
            var sb = new StringBuilder();
            sb.AppendLine("拦截解算器两臂（同轨沿迹；ToF = dist/100；裁判 = Shepperd 独立推进）—— 两臂都应 1e-8 m 级：");
            sb.AppendLine("   dist(km)  dist/r     ToF(s)   Izzo            Gooding        判据 dist/1000");

            foreach (double dkm in new[] { 1.0, 2.0, 5.0, 10.0, 20.0, 40.0, 80.0, 160.0 })
            {
                double dist = dkm * 1000.0;
                double tof = dist / MaxClosingSpeed;
                (OrbitView chaser, OrbitView target) = Setup(dist);

                (V3 ri, V3 vi) = chaser.RightHandedStateVectorsAtUT(0.0);
                (V3 rf, V3 _vf) = target.RightHandedStateVectorsAtUT(tof);

                DVector3 hRef = V3.Cross(ri, vi);

                double missIzzo = double.NaN, missGooding = double.NaN;
                try
                {
                    (DVector3 ii, DVector3 _if) = Izzo.Solve(Mu, ri, rf, tof, TransferGeometry.Prograde, 0, hRef);
                    missIzzo = (Propagate(ri, vi + (ii - vi), tof) - rf).magnitude;   // ⚠ 返回的是 **r1 处的速度矢量** ⇒ Δv = ii − vi
                }
                catch (Exception) { /* 退化几何 ⇒ 记 NaN */ }

                try
                {
                    (DVector3 gi, DVector3 _gf) = Gooding.Solve(Mu, ri, rf, tof, TransferGeometry.Prograde, 0, hRef);
                    missGooding = (Propagate(ri, vi + (gi - vi), tof) - rf).magnitude;   // 同上：Δv = gi − vi
                }
                catch (Exception) { /* 退化几何 ⇒ 记 NaN */ }
                _ = _vf;

                double tol = dist / 1000.0;
                sb.AppendLine($"  {dkm,7:F0}  {dist / RTgt,8:E2}  {tof,7:F0}   {missIzzo,14:E3}   {missGooding,16:E3}   {tol,10:F1}");

                Assert.True(missIzzo < tol && missGooding < tol,
                    $"两臂在本带都应当精确，实测 Izzo {missIzzo:E3} m / Gooding {missGooding:E3} m（判据 {tol:F1} m）" +
                    $"⇒ 结论要重核（dist={dkm}km）");
            }

            _out.WriteLine(sb.ToString().TrimEnd());
        }
    }
}
