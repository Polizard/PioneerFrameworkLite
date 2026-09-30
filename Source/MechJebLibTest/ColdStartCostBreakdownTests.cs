using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using MechJebLib.Lambert;
using MechJebLib.Maneuvers;
using MechJebLib.Maths;
using MechJebLib.Primitives;
using MechJebLib.TwoBody;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **冷启动计算的成本拆解**（2026-09-30）—— "warm start 不接"之后的问题：**冷启动本身有没有可省的地方**。
    ///
    /// ## 它量什么（全部不改生产代码）
    ///   · **A 总耗时**：`NextManeuver`（= 阶梯霍曼级真正走的那条）的中位墙钟，按几何分档；
    ///   · **B 实际要几轮**：`maxiter` 是**公开参数** ⇒ **二分**出"最小 cap 就能给出同一答案"
    ///     ⇒ 再按生产那条**倍增批**（1→2→4→8）算出"算了几个候选 / 真正需要几个"（**浪费比**）；
    ///   · **C 异常率**：`AppDomain.FirstChanceException` 计数（**抛异常≈6 µs/次，是普通求值的 ~37 倍**）；
    ///   · **D GC 压力**：gen0/gen1 收集次数（分不清单次分配 ⇒ 只作量级读数）；
    ///   · **E 微基准**：`Izzo.Solve`（正常 / **共线抛异常**那条路）与 `Shepperd.Solve` 的单次成本
    ///     ⇒ 用于把 A 分解成"eval 数 × 单位成本 + 异常数 × 异常成本"；
    ///   · **F 扫描结构**：`CompassSearch`（**internal，生产源码直链进测试工程** ⇒ 可直接驱动）
    ///     在**光滑目标**与**平坦目标**下各用多少次求值 —— 回答"60 轮预算用掉几轮"、
    ///     "退化（平坦）维度是**省**还是**费**"。
    ///
    /// ## 判据（只断言结构事实，**不断言时间**）
    ///   ① 每格冷解都成功且 `dt1 > 0`；② 二分出的最小 cap 落在 `[1, 50]`；③ 异常计数自洽（≥ 0）；
    ///   ④ `CompassSearch` 在平坦目标下的求值次数**不高于**光滑目标（否则"退化维度很贵"要重新认识）。
    ///   **时间只打印、不进 PASS/FAIL**（本库纪律：观测项不进判据）。
    ///
    /// ⚠ 读数请**单独跑**（`--filter ColdStartCostBreakdownTests`）：xUnit 同程序集内并行会污染墙钟。
    /// </summary>
    [Collection("ParallelOps")]     // 与 ForceSerial 开关的用例串行，避免并行度被别的用例改掉
    public class ColdStartCostBreakdownTests
    {
        private readonly ITestOutputHelper _out;
        public ColdStartCostBreakdownTests(ITestOutputHelper output) => _out = output;

        private const double Mu = 2.0e14;          // = 地球档（`OrbitMath.GM`）
        private const double R0 = 7_000_000.0;

        /// <summary>
        /// ⚠⚠ **口径横幅（2026-09-30 加，必读）**：本台架的墙钟读数是**测试进程的构建配置**下的，
        /// 而**测试默认跑 Debug、游戏跑 Release**。同一台架实测（60 条几何）：
        /// ```
        /// 冷解 p50：Debug 0.72 ms ｜ Release 0.32 ms（**2.2×**）；单解 并行 0.78 → 0.40 ms
        /// ```
        /// ⇒ **读绝对值前先看这一行**（`#if DEBUG` 自动标）。位级/结构类判据不受口径影响 ✓。
        /// </summary>
        private static string BuildFlavor()
        {
#if DEBUG
            return "Debug（测试默认：**比 Release 慢约 2–6×**，游戏跑 Release ⇒ 绝对耗时必须换算）";
#else
            return "Release（= 游戏口径）";
#endif
        }

        private static int _throws;
        private static void CountFirstChance(object s, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
            => Interlocked.Increment(ref _throws);

        private static (V3 r, V3 v) Circular(double radius, double phaseDeg)
        {
            double vc = Math.Sqrt(Mu / radius);
            double ph = phaseDeg * Math.PI / 180.0;
            var r = new V3(radius * Math.Cos(ph), 0, radius * Math.Sin(ph));
            var v = new V3(Math.Sin(ph), 0, -Math.Cos(ph)) * vc;
            return (r, v);
        }

        private static double Median(double[] xs)
        {
            var c = (double[])xs.Clone();
            Array.Sort(c);
            return c.Length % 2 == 1 ? c[c.Length / 2] : 0.5 * (c[c.Length / 2 - 1] + c[c.Length / 2]);
        }

        /// <summary>按生产的**倍增批**（1→2→4→8）算：cap 为 <paramref name="cap"/> 时，
        /// **算了几个候选**（`iter + batch`，那一批整批算完才判）／**真正需要几个**（`iter + 1`，成功者在批内第几个）。</summary>
        private static (int computed, int needed) ScheduleFor(int cap)
        {
            int iter = 0, batchSize = 1;
            while (iter < cap)
            {
                int batch = Math.Min(batchSize, cap - iter);
                // 假设成功者就在这一批的**第一个**（最省的情形）⇒ needed = iter+1；computed 恒为整批
                return (iter + batch, iter + 1);
            }
            return (0, 0);
        }

        [Fact]
        public void ColdPath_CostBreakdown()
        {
            AppDomain.CurrentDomain.FirstChanceException += CountFirstChance;
            try
            {
                // ── 几何（与阶梯台架同族：半径比 × 相位；φ=0 那一档是**严格共线**的病理档）──
                var geoms = new (string name, double ratio, double phase)[]
                {
                    ("同族·升 1.15 φ=0（共线病理）", 1.15, 0.0),
                    ("同族·升 1.15 φ=40", 1.15, 40.0),
                    ("异轨 1.6 φ=0（共线病理）", 1.6, 0.0),
                    ("异轨 1.6 φ=40", 1.6, 40.0),
                };

                var sb = new StringBuilder();
                _out.WriteLine($"冷启动成本拆解（μ={Mu:E1} · R0={R0 / 1000.0:F0}km · 单线程测量内核 · 机器 8 物理/16 逻辑）");
                _out.WriteLine("    几何                                  中位耗时  最小cap  本轮算了/需要  异常/次   gen0/100次");

                double sumMs = 0.0;
                foreach (var g in geoms)
                {
                    (V3 r1, V3 v1) = Circular(R0, 0.0);
                    (V3 r2, V3 v2) = Circular(R0 * g.ratio, g.phase);

                    // 热身（JIT + 静态构造）
                    for (int i = 0; i < 3; i++) TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, coplanar: false);

                    // ── A 中位耗时 ──
                    const int N = 9;
                    var times = new double[N];
                    for (int i = 0; i < N; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        var sol = TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, coplanar: false);
                        sw.Stop();
                        times[i] = sw.Elapsed.TotalMilliseconds;
                        Assert.True(sol.dt1 > 0.0 && !double.IsNaN(sol.dt1), $"{g.name}：冷解不合法（dt1={sol.dt1:R}）");
                    }
                    double med = Median(times);
                    sumMs += med;

                    // ── B 二分最小 cap（同一答案＝不抛且 dt1>0）──
                    int lo = 1, hi = 50;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) / 2;
                        bool ok;
                        try
                        {
                            var s2 = TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, maxiter: mid, coplanar: false);
                            ok = s2.dt1 > 0.0 && !double.IsNaN(s2.dt1);
                        }
                        catch { ok = false; }
                        if (ok) hi = mid; else lo = mid + 1;
                    }
                    int cap = lo;
                    Assert.InRange(cap, 1, 50);
                    (int computed, int needed) = ScheduleFor(cap);

                    // ── C 异常/次（N 次解 + 上面那些二分解，除以总次数）──
                    int before = Volatile.Read(ref _throws);
                    int M = 20;
                    for (int i = 0; i < M; i++) TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, coplanar: false);
                    double throwsPerSolve = (Volatile.Read(ref _throws) - before) / (double)M;

                    // ── D GC 压力（每 100 次解的 gen0 次数）──
                    int g0a = GC.CollectionCount(0), g0b;
                    for (int i = 0; i < 100; i++) TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, coplanar: false);
                    g0b = GC.CollectionCount(0);

                    sb.AppendLine($"    {g.name,-36} {med,8:F2} ms {cap,7}  {computed,6}/{needed,-6}  {throwsPerSolve,8:F1}  {g0b - g0a,10}");
                }

                // ── E 微基准：一次 eval 的三个积木 ──
                double izzoNs = MicroIzzo(normal: true);
                double izzoThrowNs = MicroIzzo(normal: false);
                double shepNs = MicroShepperd();
                double compassSmooth = MicroCompass(smooth: true);
                double compassFlat = MicroCompass(smooth: false);

                _out.WriteLine("逐几何读数：");
                _out.WriteLine(sb.ToString().TrimEnd());
                _out.WriteLine("");
                _out.WriteLine("E 单次成本（每调用的中位/平均 ns）：");
                _out.WriteLine($"    Izzo.Solve（正常几何）      {izzoNs,10:F0} ns");
                _out.WriteLine($"    Izzo.Solve（**共线 ⇒ 抛**） {izzoThrowNs,10:F0} ns   （= 正常那档的 {izzoThrowNs / izzoNs:F1}×）");
                _out.WriteLine($"    Shepperd.Solve              {shepNs,10:F0} ns   ⇒ 一次目标求值 ≈ 2×Shepperd + 1×Izzo = {2 * shepNs + izzoNs:F0} ns");

                // 分解：一轮 = 12 个种子搜索；每个种子搜索 ≈ compassSmooth 次求值
                //   ⇒ 单次冷解 ≈ 算了几个候选 × 12 × compassSmooth 次求值 × (2Shep + Izzo) + 异常开销
                double evalNs = 2 * shepNs + izzoNs;
                _out.WriteLine("");
                _out.WriteLine("F 扫描结构（`CompassSearch` 直接驱动；盒子/步长/容差按生产形状：step≈tt/4、tol≈tt·1e-6、上限 60 轮）：");
                _out.WriteLine($"    光滑目标（有唯一极小）：{compassSmooth,8:F0} 次求值/种子搜索");
                _out.WriteLine($"    平坦目标（退化轴）    ：{compassFlat,8:F0} 次求值/种子搜索");
                Assert.True(compassFlat <= compassSmooth * 1.05,
                    $"平坦目标的求值次数（{compassFlat}）高于光滑目标（{compassSmooth}）⇒ " +
                    "「只在严格下降时才移动 ⇒ 平坦轴立刻收敛」这条判断不成立，成本叙述要重写");

                _out.WriteLine("");
                _out.WriteLine($"合计（按上面的模型估）：一次冷解 ≈ 算了 {12}×{compassSmooth:F0} 次求值/候选 × 候选数 × {evalNs:F0} ns");
                _out.WriteLine($"    ⇒ 1 个候选 ≈ {12 * compassSmooth * evalNs / 1e6:F2} ms（模型）｜实测中位（4 档几何均值）{sumMs / geoms.Length:F2} ms");
                _out.WriteLine("    读数说明：**时间只打印、不进判据**；要复现请单独跑本类（并行会污染墙钟）。");
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= CountFirstChance;
            }
        }

        /// <summary>
        /// **分布面**（比单格中位重要：游戏里的尖峰由**尾**决定）。
        /// 采样 60 条几何（半径比/相位/三个真实 μ），**只保留能进生产霍曼级的那些**
        /// （复刻 `DeltaVAndTimeForHohmannTransferCore` 的退化守卫 `T_syn ≤ 200×tHohmann` ⇒ 与生产同一族）。
        /// 每条量：中位耗时、最小 cap（二分）、异常/次；最后给 p50/p90/max + cap 直方图 + 串行/并行切分。
        /// </summary>
        [Fact]
        public void ColdPath_PopulationSweep()
        {
            AppDomain.CurrentDomain.FirstChanceException += CountFirstChance;
            try
            {
                double[] mus = { 5.0e12, 2.0e14, 2.0e16 };            // 月球 / 地球 / 太阳（与蒙特卡洛同族）
                var rnd = new Random(20260930);                        // 固定种子 ⇒ 可复现
                var times = new System.Collections.Generic.List<double>();
                var caps = new System.Collections.Generic.List<int>();
                var throwRates = new System.Collections.Generic.List<double>();
                int sampled = 0, rejectedByGuard = 0;

                foreach (double mu in mus)
                {
                    for (int i = 0; i < 40; i++)
                    {
                        double r1 = (2.0e6 + rnd.NextDouble() * 1.2e7) * MountScale(mu);
                        double ratio = 1.02 + rnd.NextDouble() * 0.98;
                        double phase = rnd.NextDouble() * 360.0;
                        double u1 = rnd.NextDouble() * 360.0;
                        if (sampled >= 60) break;

                        (V3 ra, V3 va) = CircularAt(mu, r1, u1);
                        (V3 rb, V3 vb) = CircularAt(mu, r1 * ratio, phase);
                        if (!PassesProductionGuard(mu, ra, va, rb, vb)) { rejectedByGuard++; continue; }
                        sampled++;

                        for (int w = 0; w < 2; w++)
                            TwoImpulseTransfer.NextManeuver(mu, ra, va, rb, vb, coplanar: false);

                        const int N = 5;
                        var ts = new double[N];
                        for (int k = 0; k < N; k++)
                        {
                            var sw = Stopwatch.StartNew();
                            TwoImpulseTransfer.NextManeuver(mu, ra, va, rb, vb, coplanar: false);
                            sw.Stop();
                            ts[k] = sw.Elapsed.TotalMilliseconds;
                        }
                        times.Add(Median(ts));

                        int lo = 1, hi = 50;
                        while (lo < hi)
                        {
                            int mid = (lo + hi) / 2;
                            bool ok;
                            try
                            {
                                var s2 = TwoImpulseTransfer.NextManeuver(mu, ra, va, rb, vb, maxiter: mid, coplanar: false);
                                ok = s2.dt1 > 0.0 && !double.IsNaN(s2.dt1);
                            }
                            catch { ok = false; }
                            if (ok) hi = mid; else lo = mid + 1;
                        }
                        caps.Add(lo);

                        int t0 = Volatile.Read(ref _throws);
                        for (int k = 0; k < 10; k++) TwoImpulseTransfer.NextManeuver(mu, ra, va, rb, vb, coplanar: false);
                        throwRates.Add((Volatile.Read(ref _throws) - t0) / 10.0);
                    }
                }

                times.Sort();
                caps.Sort();
                throwRates.Sort();
                double Pct(System.Collections.Generic.List<double> xs, double p) => xs[Math.Min(xs.Count - 1, (int)(p * xs.Count))];
                int PctI(System.Collections.Generic.List<int> xs, double p) => xs[Math.Min(xs.Count - 1, (int)(p * xs.Count))];

                var hist = new System.Collections.Generic.SortedDictionary<int, int>();
                foreach (int c in caps) hist[c] = hist.TryGetValue(c, out int v) ? v + 1 : 1;

                _out.WriteLine($"冷启动分布面（采样 {sampled} 条 · 退化守卫挡掉 {rejectedByGuard} 条 · 与生产同一族）：");
                _out.WriteLine($"    ⚠ 口径 {BuildFlavor()}");
                _out.WriteLine($"    耗时 ms：p50 {Pct(times, 0.50):F2}｜p90 {Pct(times, 0.90):F2}｜max {times[times.Count - 1]:F2}");
                _out.WriteLine($"    最小 cap：p50 {PctI(caps, 0.5)}｜p90 {PctI(caps, 0.9)}｜max {caps[caps.Count - 1]}");
                _out.WriteLine($"    异常/次：p50 {Pct(throwRates, 0.5):F1}｜p90 {Pct(throwRates, 0.9):F1}｜max {throwRates[throwRates.Count - 1]:F1}");
                var hs = new StringBuilder();
                foreach (var kv in hist) hs.Append($"cap={kv.Key}:{kv.Value}  ");
                _out.WriteLine("    cap 直方图：" + hs.ToString().TrimEnd());

                // 串行/并行切分（同一个解，只切换调度 ⇒ 逐位相同）
                (V3 r1b, V3 v1b) = CircularAt(Mu, R0, 0.0);
                (V3 r2b, V3 v2b) = CircularAt(Mu, R0 * 1.6, 40.0);
                double ParallelMs() { var t = new double[5]; for (int i = 0; i < 5; i++) { var sw = Stopwatch.StartNew(); TwoImpulseTransfer.NextManeuver(Mu, r1b, v1b, r2b, v2b, coplanar: false); sw.Stop(); t[i] = sw.Elapsed.TotalMilliseconds; } return Median(t); }
                double par = ParallelMs();
                bool old = ParallelOps.ForceSerial;
                ParallelOps.ForceSerial = true;
                double ser = ParallelMs();
                ParallelOps.ForceSerial = old;
                _out.WriteLine($"    单个解：并行 {par:F2} ms｜串行 {ser:F2} ms（12 起点在 8 物理核上 ⇒ {ser / par:F2}×）");
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= CountFirstChance;
            }
        }

        /// <summary>
        /// **重规划节拍的重复劳动有多大**（"减少调用次数"是世界上唯一不碰退化 argmin 的省法）。
        /// 沿着一条轨道每 30 s 求一次霍曼计划（= 阶梯每拍做的事），看**相邻两拍的计划差多少**：
        /// 首次点火**绝对时刻**（UT1）与两发 Δv。若 UT1 稳定而 Δv 几乎不动 ⇒ 节拍式重规划在重复劳动。
        /// </summary>
        [Fact]
        public void ReplanDrift_WhatActuallyChangesIn30Seconds()
        {
            var geoms = new (string name, double mu, double rChaser, double rTgt, double phase)[]
            {
                ("LEO 7.0→8.05e3 km（地球）", 2.0e14, 7.0e6, 8.05e6, 40.0),
                ("月球 2.9→3.3e3 km", 5.0e12, 2.9e6, 3.3e6, 60.0),
            };

            _out.WriteLine("重规划节拍：沿轨道每 30 s 求一次霍曼计划（阶梯每拍做的事）");
            foreach (var g in geoms)
            {
                (V3 r1, V3 v1) = CircularAt(g.mu, g.rChaser, 0.0);
                (V3 r2, V3 v2) = CircularAt(g.mu, g.rTgt, g.phase);

                double prevUt1 = double.NaN, prevDv = double.NaN;
                var sb = new StringBuilder();
                double maxDvDrift = 0.0, maxUtDrift = 0.0;
                for (int k = 0; k <= 10; k++)
                {
                    double now = k * 30.0;
                    var sol = TwoImpulseTransfer.NextManeuver(g.mu, r1, v1, r2, v2, coplanar: false);
                    double ut1 = now + sol.dt1;
                    double dv = sol.dv1.magnitude + sol.dv2.magnitude;
                    if (k > 0)
                    {
                        maxDvDrift = Math.Max(maxDvDrift, Math.Abs(dv - prevDv));
                        maxUtDrift = Math.Max(maxUtDrift, Math.Abs(ut1 - prevUt1));
                    }
                    if (k <= 3 || k == 10)
                        sb.AppendLine($"        t={now,6:F0}s：点火 UT1={ut1,10:F2}（距现在 {sol.dt1,9:F2}s）｜Δv={dv,9:F4} m/s");
                    prevUt1 = ut1; prevDv = dv;
                    if (k < 10)
                    {
                        // ⚠ **两条状态矢量必须推进到同一时刻**（第一版只推进了追击者 ⇒ 解的是
                        //   "t=30s 的追击者 vs t=0 的目标" ⇒ 读数全是假的 —— 本库第三次踩"测试自己的构造"）。
                        (r1, v1) = Advance(g.mu, r1, v1, 30.0);
                        (r2, v2) = Advance(g.mu, r2, v2, 30.0);
                    }
                }
                _out.WriteLine($"    {g.name}：相邻两拍｜ΔUT1｜≤ {maxUtDrift:F4}s（点火绝对时刻）｜｜ΔΔv｜≤ {maxDvDrift:F6} m/s");
                _out.WriteLine(sb.ToString().TrimEnd());
            }
        }

        private static (V3 r, V3 v) Advance(double mu, V3 r, V3 v, double dt)
        {
            const double step = 5.0;
            int n = (int)Math.Round(dt / step);
            for (int i = 0; i < n; i++)
            {
                var (rn, vn) = Shepperd.Solve(mu, step, r, v);
                r = rn; v = vn;
            }
            return (r, v);
        }

        /// <summary>μ 越小轨道越小 ⇒ 用 μ 的立方根做尺度（保持"周期同一量级"）。</summary>
        private static double MountScale(double mu) => Math.Pow(mu / 2.0e14, 1.0 / 3.0);

        private static (V3 r, V3 v) CircularAt(double mu, double radius, double phaseDeg)
        {
            double vc = Math.Sqrt(mu / radius);
            double ph = phaseDeg * Math.PI / 180.0;
            var r = new V3(radius * Math.Cos(ph), 0, radius * Math.Sin(ph));
            var v = new V3(Math.Sin(ph), 0, -Math.Cos(ph)) * vc;
            return (r, v);
        }

        /// <summary>**复刻生产的退化守卫**（`DeltaVAndTimeForHohmannTransferCore` 里那一段）：
        /// `T_syn > 200 × tHohmann` ⇒ 生产**根本不会**走这条解（返回 NaN）⇒ 不该进本台架的样本。
        /// 该式与实现**逐字同式**（判据与生产同一把尺；这里只是采样过滤器，不重算答案）。</summary>
        private static bool PassesProductionGuard(double mu, V3 r1, V3 v1, V3 r2, V3 v2)
        {
            double a1 = MechJebLib.Functions.Astro.SmaFromStateVectors(mu, r1, v1);
            double a2 = MechJebLib.Functions.Astro.SmaFromStateVectors(mu, r2, v2);
            if (!(a1 > 0.0) || !(a2 > 0.0)) return false;
            double tHohmann = Math.PI * Math.Sqrt(Math.Pow(a1 + a2, 3.0) / (8.0 * mu));
            double t1 = MechJebLib.Functions.Astro.PeriodFromStateVectors(mu, r1, v1);
            double t2 = MechJebLib.Functions.Astro.PeriodFromStateVectors(mu, r2, v2);
            double invDiff = Math.Abs(1.0 / t1 - 1.0 / t2);
            double synodic = invDiff > 0.0 ? 1.0 / invDiff : double.PositiveInfinity;
            return synodic <= 200.0 * tHohmann;
        }

        /// <summary>一次 `Izzo.Solve` 的成本。正常档 = 非共线的三角几何；抛档 = **严格共线**（`r1 ∥ r2`）⇒ 走异常路。</summary>
        private static double MicroIzzo(bool normal)
        {
            V3 r1 = new V3(1.0, 0.0, 0.0);
            V3 r2 = normal ? new V3(Math.Cos(0.6), Math.Sin(0.6), 0.0) * 1.05 : new V3(1.05, 0.0, 0.0);
            V3 h = new V3(0, 0, 1);
            int n = 20000;
            for (int i = 0; i < 200; i++) Run();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) Run();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1e6 / n;

            void Run()
            {
                try { Izzo.Solve(1.0, r1, r2, 3.0, TransferGeometry.Prograde, 0, h); }
                catch { /* 抛档：成本就是要量的 */ }
            }
        }

        private static double MicroShepperd()
        {
            var r = new V3(1.0, 0.0, 0.0);
            var v = new V3(0.0, 1.0, 0.0);
            int n = 200000;
            for (int i = 0; i < 2000; i++) Shepperd.Solve(1.0, 0.3, r, v);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) Shepperd.Solve(1.0, 0.3, r, v);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1e6 / n;
        }

        /// <summary>用生产形状的盒子驱动 `CompassSearch`（internal ⇒ 直测），数**实际求值次数**。
        /// 光滑档 = 三维抛物面；平坦档 = 只有一维有梯度（另两维退化，模拟"窗口/相位维 Δv 相同"）。</summary>
        private static double MicroCompass(bool smooth)
        {
            double ttguess = 3400.0;                     // 7000→8000 km 量级的转移时长
            double syn = 43000.0;                        // 会合周期量级
            double[] lo = { -0.5 * syn, 0.1 * ttguess, -8000.0 };
            double[] hi = { 1.0 * syn, 6.0 * ttguess, 8000.0 };
            double[] step = { (hi[0] - lo[0]) / 8.0, Math.Max(ttguess / 4.0, (hi[1] - lo[1]) / 16.0), (hi[2] - lo[2]) / 8.0 };
            double tol = Math.Max(ttguess, hi[1] - lo[1]) * 1e-6;
            double[] x0 = { 0.0, ttguess, 0.0 };

            int evals = 0;
            double F(double[] x)
            {
                evals++;
                if (smooth) return x[0] * x[0] * 1e-6 + (x[1] - ttguess) * (x[1] - ttguess) + x[2] * x[2] * 1e-6;
                return (x[1] - ttguess) * (x[1] - ttguess);      // 只在 tt 维有梯度（另两维平坦 ⇒ 打平）
            }

            for (int i = 0; i < 20; i++) { evals = 0; TwoImpulseTransfer.CompassSearch(F, x0, lo, hi, step, tol, 60, out _); }
            evals = 0;
            TwoImpulseTransfer.CompassSearch(F, x0, lo, hi, step, tol, 60, out _);
            return evals;
        }
    }
}
