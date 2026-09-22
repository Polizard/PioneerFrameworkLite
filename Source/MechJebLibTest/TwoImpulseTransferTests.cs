using System;
using MechJebLib.Functions;
using MechJebLib.Maneuvers;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using static MechJebLib.Utils.Statics;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// `TwoImpulseTransfer`（两冲量转移）的验收。它替代了上游的 alglib `minbleic`
    /// ⇒ 判据分两层，**都不依赖"和上游同值"**：
    /// ① **仪器层**：手写的模式搜索在已知函数上确实收敛到已知极小（`CompassSearch` 是 `internal`，
    ///   本文件与它同程序集——因为测试工程**直接编译**那份源码）；
    /// ② **物理层**：算出来的两冲量施加后必须**真的交会**（位置**与**速度都对上，且是**由构造保证**的，
    ///   与优化器无关），共面圆轨的代价必须**等于解析霍曼**（这一条才真的在考优化器）。
    /// </summary>
    public class TwoImpulseTransferTests
    {
        private const double Mu = 2.0e14; // 与 OrbitMath.GM 同量级（本文件是纯数值测试，不依赖 OrbitMath）
        private static readonly V3 Pole = new V3(0, 0, 1);

        // ── ① 仪器：模式搜索本身 ────────────────────────────────────────────────

        [Fact]
        public void CompassSearch_FindsKnownMinimumOfConvexQuadratic()
        {
            var target = new[] { 1.5, -2.0, 0.75 };
            Func<double[], double> f = x =>
            {
                double s = 0;
                for (int i = 0; i < 3; i++) s += (x[i] - target[i]) * (x[i] - target[i]);
                return s;
            };

            double[] lo = { -5.0, -5.0, -5.0 }, hi = { 5.0, 5.0, 5.0 }, step = { 1.0, 1.0, 1.0 };
            double[] x = TwoImpulseTransfer.CompassSearch(f, new[] { 0.0, 0.0, 0.0 }, lo, hi, step, 1e-12, 200, out double fBest);

            Assert.True(fBest < 1e-18, $"没收敛到极小：fBest={fBest:E3}");
            for (int i = 0; i < 3; i++)
                Assert.True(Math.Abs(x[i] - target[i]) < 1e-7, $"第 {i} 维偏了：{x[i]:R} vs {target[i]:R}");
        }

        [Fact]
        public void CompassSearch_RespectsBounds_AndHandlesNonsmoothObjective()
        {
            // 极小在盒子外 ⇒ 只能停在边界上
            Func<double[], double> bowl = x => (x[0] - 10.0) * (x[0] - 10.0);
            double[] x = TwoImpulseTransfer.CompassSearch(bowl, new[] { 0.0 }, new[] { -1.0 }, new[] { 1.0 },
                new[] { 0.5 }, 1e-12, 200, out double fBest);
            Assert.Equal(1.0, x[0], 9);
            Assert.Equal(81.0, fBest, 6);

            // 非光滑（绝对值菱形）：模式搜索照样能下降（这正是"不用梯度"的意义）
            Func<double[], double> nonsmooth = y => Math.Abs(y[0] - 0.25) + Math.Abs(y[1] + 0.5);
            double[] y2 = TwoImpulseTransfer.CompassSearch(nonsmooth, new[] { 3.0, 3.0 }, new[] { -5.0, -5.0 }, new[] { 5.0, 5.0 },
                new[] { 1.0, 1.0 }, 1e-12, 200, out double f2);
            Assert.True(f2 < 1e-6, $"非光滑目标没收敛：{f2:E3}");
        }

        // ── ② 物理：交会必须真的成立 ────────────────────────────────────────────

        /// <summary>在给定半径/倾角/相位上的圆轨状态矢量（纯数值小工具，不依赖 OrbitView）。</summary>
        private static (V3 r, V3 v) CircularState(double radius, double incRad, double phaseRad)
        {
            var r = new V3(radius * Math.Cos(phaseRad), radius * Math.Sin(phaseRad), 0.0);
            var vdir = new V3(-Math.Sin(phaseRad), Math.Cos(phaseRad), 0.0);
            double vc = Math.Sqrt(Mu / radius);
            var v = new V3(vdir.x * vc, vdir.y * vc * Math.Cos(incRad), vdir.z * vc + vdir.y * vc * Math.Sin(incRad));
            return (r, v);
        }

        /// <summary>
        /// **判据：两次点火施加后，航天器在第二时刻必须与目标"同位置同速度"。**
        /// 这一步**由构造保证**（Lambert 保证转移弧两端的端点/速度，`dv2 = vf2 − vf`）
        /// ⇒ 它考的是**缩放与时间记账**（`Scale` 归一化、`dt1/dt2` 的绝对时刻、返回值单位）。
        /// 目标状态用 `Shepperd` **独立**从初始状态推进出来（不复用被测代码的内部量）。
        /// </summary>
        [Theory]
        [InlineData(7.0e6, 1.2e7, 0.0, 0.35)]      // 共面圆轨
        [InlineData(7.0e6, 1.2e7, 0.4, 0.35)]      // 异面圆轨（coplanar: false，连平面一起变）
        [InlineData(7.0e6, 9.0e6, 0.4, 0.35)]      // 异面 + 近距
        public void NextManeuver_RendezvousIsExact(double ra, double rb, double incB, double phaseB)
        {
            (V3 r1, V3 v1) = CircularState(ra, 0.0, 0.0);
            (V3 r2, V3 v2) = CircularState(rb, incB, phaseB);

            (V3 dv1, double dt1, V3 dv2, double dt2) =
                TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, coplanar: false, rendezvous: true, capture: true);

            Assert.True(dt1 > 0, $"第一次点火必须在未来（dt1={dt1:E3}）");
            Assert.True(dt2 > dt1, $"第二次点火必须在第一次之后（dt1={dt1:E3} dt2={dt2:E3}）");

            (V3 rb1, V3 vb1) = Shepperd.Solve(Mu, dt1, r1, v1);       // 到第一次点火时刻
            // ⚠ `Shepperd.Solve(mu, dt, …)` 是"再推进 dt"⇒ 这里必须是**区间** `dt2 − dt1`，
            // 不是绝对时刻 `dt2`（第一版就错在这儿：多飞了 dt1 秒，位置差 ~1e6 m）。
            (V3 rt2, V3 vt2) = Shepperd.Solve(Mu, dt2 - dt1, rb1, vb1 + dv1); // 转移弧的终端状态
            (V3 rTarget2, V3 vTarget2) = Shepperd.Solve(Mu, dt2, r2, v2); // 目标在同一时刻的状态（独立推进）

            Assert.True(V3.Distance(rt2, rTarget2) < 1e-6 * rTarget2.magnitude,
                $"位置没交会：差 {V3.Distance(rt2, rTarget2):E3} m");
            Assert.True(V3.Distance(vt2 + dv2, vTarget2) < 1e-6 * vTarget2.magnitude,
                $"速度没对上：差 {V3.Distance(vt2 + dv2, vTarget2):E3} m/s");
        }

        /// <summary>
        /// **判据：共面圆轨的最省两冲量就是解析霍曼。**
        /// 这条才真的在考优化器（交会那一条只考记账）。参照物是 `Astro.HohmannTransferParameters`
        /// ——它是**上游的闭式解**，与被测代码是两条独立路径。
        /// </summary>
        [Theory]
        [InlineData(7.0e6, 1.2e7)]
        [InlineData(7.0e6, 4.2e7)]
        [InlineData(1.0e7, 8.0e6)] // 下降转移（"反向"的一侧也要能找对）
        public void NextManeuver_CoplanarCircular_CostMatchesAnalyticHohmann(double ra, double rb)
        {
            (V3 r1, V3 v1) = CircularState(ra, 0.0, 0.0);
            (V3 r2, V3 v2) = CircularState(rb, 0.0, 0.7); // 相位随意：霍曼只是"等相位"，代价与相位无关

            (V3 dv1, double dt1, V3 dv2, double dt2) =
                TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, coplanar: true, rendezvous: true, capture: true);

            // ⚠ `HohmannTransferParameters` 的 dv 是**带符号**的（下降转移给负数）⇒ 只比幅值
            (double e1, double e2, double eTt, double _) = Astro.HohmannTransferParameters(Mu, r1, r2);
            double expect1 = Math.Abs(e1), expect2 = Math.Abs(e2), expectTt = Math.Abs(eTt);

            Assert.True(System.Math.Abs(dv1.magnitude - expect1) < 1e-6 * expect1,
                $"第一次点火：{dv1.magnitude:F6} vs 霍曼 {expect1:F6} m/s");
            Assert.True(System.Math.Abs(dv2.magnitude - expect2) < 1e-6 * expect2,
                $"第二次点火：{dv2.magnitude:F6} vs 霍曼 {expect2:F6} m/s");
            // ⚠ 转移时长的容差**只能是量出来的**：代价在 `tt` 上是**驻值**（一阶导数为 0）
            // ⇒ 只由代价驱动的优化器对 `tt` 只有**二阶**精度（实测相对差 1.7e-5），
            // 而代价本身是一阶量、能钉到 1e-6。差别不是实现粗糙，是这条判据的固有条件数。
            Assert.True(System.Math.Abs((dt2 - dt1) - expectTt) < 1e-4 * expectTt,
                $"转移时长：{dt2 - dt1:F3} vs 霍曼 {expectTt:F3} s");
        }

        /// <summary>`capture: false`（飞越而非交会）⇒ 只要**位置**在终点对上；速度不必匹配。</summary>
        [Fact]
        public void NextManeuver_WithoutCapture_InterceptsPositionOnly()
        {
            (V3 r1, V3 v1) = CircularState(7.0e6, 0.0, 0.0);
            (V3 r2, V3 v2) = CircularState(1.2e7, 0.25, 0.5);

            (V3 dv1, double dt1, V3 _, double dt2) =
                TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, coplanar: false, rendezvous: false, capture: false);

            (V3 rb1, V3 vb1) = Shepperd.Solve(Mu, dt1, r1, v1);
            (V3 rt2, V3 _) = Shepperd.Solve(Mu, dt2 - dt1, rb1, vb1 + dv1);
            (V3 rTarget2, V3 _) = Shepperd.Solve(Mu, dt2, r2, v2);

            Assert.True(V3.Distance(rt2, rTarget2) < 1e-6 * rTarget2.magnitude,
                $"位置没截住：差 {V3.Distance(rt2, rTarget2):E3} m");
        }

        /// <summary>
        /// ⭐ **近同轨必须有界返回**（2026-09-20 修，"MJ 优秀就按 MJ 来"整批扫描里找出的唯一数值核偏离）。
        ///
        /// 病：默认 `dt` 盒半宽取 `Astro.SynodicPeriod`，而它在近同轨上**发散**（同轨 ⇒ ∞；Δa=1 km ⇒ 3.8e7 s）
        /// ⇒ 盒宽天文数字 ⇒ 步长 = 盒宽/8 ⇒ 60 轮 × 8 起点 × 每轮 ~6 次 Lambert ⇒ **几分钟不返回** ✗
        /// （实测同轨 180 s 未返回，只能在外面加 NaN 兜底）。修：半宽封顶 `min(synodic, 64 × 目标周期)`。
        /// 判据：**直接调 `NextManeuver`**（不走那道 NaN 守卫，否则测不到被修的东西 ✓）——
        /// 允许"返回"或"抛契约内的无解异常"，**不允许磨着不返回** ✓。
        /// </summary>
        [Theory]
        [InlineData(0.1e3)]   // Δa = 100 m（几乎同轨）
        [InlineData(1.0e3)]   // Δa = 1 km（发散档：旧代码 synodic ≈ 3.8e7 s）
        [InlineData(10.0e3)]  // Δa = 10 km
        public void NextManeuver_NearCoOrbital_ReturnsBounded(double da)
        {
            (V3 r1, V3 v1) = CircularState(7.0e6, 0.0, 0.0);
            (V3 r2, V3 v2) = CircularState(7.0e6 + da, 0.0, 0.6);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool returned = false;
            try
            {
                TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, coplanar: true, rendezvous: true, capture: true);
                returned = true;
            }
            catch (Exception) { /* 契约内的"无解"：退化几何下所有起点都不可行 ⇒ 抛，允许 ✓ */ }
            sw.Stop();
            Assert.True(sw.Elapsed.TotalSeconds < 5.0,
                $"Δa={da:F0} m 的求解用了 {sw.Elapsed.TotalSeconds:F1} s（旧代码这里会磨几分钟）｜返回={returned}");
        }

        /// <summary>
        /// **回归哨兵（物理量，不是黄金值）**：`Δa=100 km` 这类"近但有解"的几何，求解器必须仍能找出
        /// **便宜**的远窗口方案（≈19~20 m/s 量级），而不是退到"最省的一发就要几百 m/s"。
        /// ⚠ 上一版我把哨兵写成"dt1 ≈ 291137 s"——那是**探针那条几何**（目标相位 270°）的数 ✗，
        ///   换一条测试几何就对不上（本条实测 dt1=35259 s）⇒ **哨兵只能钉与几何无关的物理量**（代价）✓。
        /// 由它抓住的回归：把封顶倍数取成 8 时，此处的方案从 ~19 m/s 掉到 ~397 m/s ✗。
        /// </summary>
        [Fact]
        public void NextManeuver_NearCoOrbital_StillFindsCheapWindow()
        {
            (V3 r1, V3 v1) = CircularState(7.0e6, 0.0, 0.0);
            (V3 r2, V3 v2) = CircularState(7.1e6, 0.0, 0.6);

            (V3 dv1, double dt1, V3 _, double _) =
                TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, coplanar: true, rendezvous: true, capture: true);

            Assert.True(dv1.magnitude < 30.0,
                $"应能找到便宜的等窗口方案（≈19 m/s 量级；封顶过紧时会退到 ≈397）：实测 {dv1.magnitude:F1} m/s（dt1={dt1:F0}s）");
        }
    }
}
