using System;
using System.Diagnostics;
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 批7 #3 的**成本测量**（2026-09-30）：`BurnArc` 的 **7 档点火相位扫描**值不值得并行。
    ///
    /// ## 为什么先量
    /// §17.4 把 #3 列为候选，但同时写着"**被 #2 覆盖**"，而 #2 已经落地（跨组并行 + 预取）。
    /// 更关键的两条现场事实（都读过代码）：
    ///   ① 这条扫描**只在退路律下跑**（`!liveDeficitLaw && !burnPhaseSwept`）⇒ 默认的活差额律**根本不扫**，
    ///      且**每机动只扫一次**（`burnPhaseSwept`）⇒ 它是**稀有**路径；
    ///   ② 它位于 `ComputeGroupPhysics` —— **已经在 `Parallel.For` 里** ⇒ 再并行是**嵌套并行`
    ///      （外层已吃满核 ⇒ 内层拿不到更多线程，收益递减、还多一层调度风险）。
    /// ⇒ 先量单次扫描的绝对成本，再决定做/不做（本库纪律：B 组先量再改；也可"判定不做带理由"）。
    ///
    /// ⚠ 只打读数、不断言时间。
    /// </summary>
    public class BurnArcPhaseScanCostTests
    {
        private readonly ITestOutputHelper _out;
        public BurnArcPhaseScanCostTests(ITestOutputHelper o) => _out = o;

        private static readonly double Mu = OrbitMath.GM;

        /// <summary>
        /// ⚠⚠ **口径横幅（2026-09-30 加，必读）**：本用例的墙钟读数是**测试进程的构建配置**下的，
        /// 而**测试默认跑 Debug、游戏跑 Release**。同一台仪器实测：
        /// ```
        /// 单次 7 档扫描：Debug 6.108 ms ｜ Release 1.065 ms（**5.7×**）
        /// 一次 TryPredict ：Debug 0.916 ms ｜ Release 0.147 ms（**6.2×**）
        /// 每步 RK4        ：Debug 2.29 µs  ｜ Release 0.36 µs （**6.4×**）
        /// ```
        /// ⇒ **读绝对值前先看这一行**；本项当初那句"6.389 ms 逼近 8 ms 验收线"就是 **Debug 口径**，
        ///   游戏里（Release）是 **1.07 ms（串行）/ 0.45 ms（并行）**。位级判据那两条不受口径影响 ✓。
        /// </summary>
        private static string BuildFlavor()
        {
#if DEBUG
            return "Debug（测试默认：**比 Release 慢约 6×**，游戏跑 Release ⇒ 绝对耗时必须换算）";
#else
            return "Release（= 游戏口径）";
#endif
        }

        private static OrbitView Orbit(double r0)
        {
            double vc = Math.Sqrt(Mu / r0);
            return OrbitView.FromStateVectors(new V3(r0, 0, 0), new V3(0, Math.Sqrt(1.3) * vc, 0),
                Mu, 0.0, new V3(0, 0, 1), r0 * 0.2);
        }

        /// <summary>整段 7 档扫描（含每档一次 `BurnArc.TryPredict`）的墙钟。</summary>
        [Fact]
        public void SevenShiftScan_CostReading()
        {
            var o = Orbit(7.0e6);
            const double node = 300.0;
            V3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, node);
            double burnSeconds = dv.magnitude * 74_995.1 / 560_000.0;

            // 预热
            for (double sh = -6.0; sh <= 6.0 + 1e-9; sh += 2.0)
                BurnArc.TryPredict(o, node, dv, burnSeconds, sh + BurnArc.ExecutionDelaySeconds, out _, out _);

            const int Reps = 50;
            var sw = Stopwatch.StartNew();
            double bestE = double.MaxValue, chosen = 0.0;
            for (int r = 0; r < Reps; r++)
            {
                bestE = double.MaxValue;
                for (double sh = -6.0; sh <= 6.0 + 1e-9; sh += 2.0)
                {
                    if (!BurnArc.TryPredict(o, node, dv, burnSeconds, sh + BurnArc.ExecutionDelaySeconds,
                            out BurnArc.Result swr, out _)) continue;
                    double e = swr.eccentricity;
                    if (e < bestE) { bestE = e; chosen = sh; }
                }
            }
            sw.Stop();
            double perScanMs = sw.Elapsed.TotalMilliseconds / Reps;

            _out.WriteLine(string.Format(
                "\n[批7 #3 成本 · 口径 {4}] 单次 7 档扫描 = {0:F3} ms（{1} 次平均；选中 {2:+0;-0;0}s，预测 e={3:E2}）"
                + "\n⇒ 触发面：**执行器拥有的计划**（= 常规每一次机动）**每机动一次**（`burnPhaseSwept`）",
                perScanMs, Reps, chosen, bestE, BuildFlavor()));
        }

        /// <summary>
        /// **拆解那 6.389 ms 花在哪（2026-09-30 补）**：一次 `TryPredict` = **固定成本**（解析滑行 + 终态算轨道要素）
        /// + **每步成本**（400 个 RK4 步）。
        /// 手段：`TryPredictPartial(..., elapsedSeconds)` 能只积到某个比例 ⇒ 改步数、看耗时**斜率（每步）与截距（固定）**；
        /// 再单独量 `OrbitView.FromStateVectors`（= 终态那次完整要素转换）与 `Astro.KeplerianFromStateVectors`。
        /// ⚠ 只打读数、不断言时间。
        /// </summary>
        [Fact]
        public void Predict_CostDecomposition_FixedVsPerStep()
        {
            var o = Orbit(7.0e6);
            const double node = 300.0;
            V3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, node);
            double burnSeconds = dv.magnitude * 74_995.1 / 560_000.0;

            double MedianMs(Action a, int reps)
            {
                for (int i = 0; i < 5; i++) a();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < reps; i++) a();
                sw.Stop();
                return sw.Elapsed.TotalMilliseconds / reps;
            }

            // (a) 终态那次完整要素转换（`FromStateVectors` = `KeplerianFromStateVectors` + 近圆分支）
            (V3 rr, V3 vv) = o.RightHandedStateVectorsAtUT(400.0);
            double msFromSv = MedianMs(() => OrbitView.FromStateVectors(rr, vv, Mu, 400.0, o.equatorialAxis, o.bodyRadius), 20000);
            double msKepler = MedianMs(() => MechJebLib.Functions.Astro.KeplerianFromStateVectors(Mu, rr, vv), 20000);

            // (b) 不同积分比例 ⇒ 线性拟合出"每步成本"与"固定成本"
            double msFull = MedianMs(() => BurnArc.TryPredict(o, node, dv, burnSeconds, out _, out _), 200);
            double msHalf = MedianMs(() => BurnArc.TryPredictPartial(o, node, dv, burnSeconds, 0.5 * burnSeconds, out _, out _), 200);
            double msQuarter = MedianMs(() => BurnArc.TryPredictPartial(o, node, dv, burnSeconds, 0.25 * burnSeconds, out _, out _), 200);
            double perStepUs = (msFull - msQuarter) / (BurnArc.Steps * 0.75) * 1000.0;
            double fixedMs = msQuarter - perStepUs * 100 / 1000.0;

            _out.WriteLine(string.Format(
                "\n[批7 #3 拆解 · 口径 {8}] 一次 `TryPredict` = {0:F3} ms（{9} 步 RK4）｜半步 {1:F3}｜四分之一 {2:F3}\n"
                + "     ⇒ 每步 {3:F2} µs（每步 = 1×`CurrentDir` + 4×`Deriv`，共 5 次 r̂/ĥ/… 重算）\n"
                + "     ⇒ 固定成本 ≈ {4:F3} ms（其中**终态算要素** `FromView` = {5:F3} ms = `KeplerianFromStateVectors` {6:F3} ms）\n"
                + "     ⇒ {9} 步合计 ≈ {7:F3} ms｜**一次扫描（7 档）= 7 × 上面这一整套**",
                msFull, msHalf, msQuarter, perStepUs, fixedMs, msFromSv, msKepler,
                perStepUs * BurnArc.Steps / 1000.0, BuildFlavor(), BurnArc.Steps));
        }

        /// <summary>
        /// **整段 7 档扫描**：串行臂 vs 并行臂（`ParallelOps.For`）的墙钟 —— 生产在"外层串行"时走并行臂。
        /// </summary>
        [Fact]
        public void SevenShiftScan_SerialVsParallel_WallClock()
        {
            var o = Orbit(7.0e6);
            const double node = 300.0;
            V3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, node);
            double burnSeconds = dv.magnitude * 74_995.1 / 560_000.0;

            double One(double sh) => BurnArc.TryPredict(o, node, dv, burnSeconds,
                sh + BurnArc.ExecutionDelaySeconds, out BurnArc.Result r, out _) ? r.eccentricity : double.NaN;

            double ScanSerial()
            {
                double best = double.MaxValue;
                for (double sh = -6.0; sh <= 6.0 + 1e-9; sh += 2.0) { double e = One(sh); if (e < best) best = e; }
                return best;
            }
            double ScanParallel()
            {
                var e = new double[7];
                ParallelOps.For(7, 1e-3, k => e[k] = One(-6.0 + 2.0 * k));
                double best = double.MaxValue;
                for (int k = 0; k < 7; k++) if (e[k] < best) best = e[k];
                return best;
            }

            for (int i = 0; i < 3; i++) { ScanSerial(); ScanParallel(); }
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) ScanSerial();
            sw.Stop();
            double msSer = sw.Elapsed.TotalMilliseconds / 20.0;
            sw.Restart();
            for (int i = 0; i < 20; i++) ScanParallel();
            sw.Stop();
            double msPar = sw.Elapsed.TotalMilliseconds / 20.0;

            _out.WriteLine(string.Format(
                "\n[批7 #3 现状 · 口径 {3}] 单次 7 档扫描：串行 {0:F3} ms｜并行（生产在'外层串行'档走的臂）{1:F3} ms ⇒ {2:F2}×（7 档挤 8 物理核）",
                msSer, msPar, msSer / msPar, BuildFlavor()));
        }

        /// <summary>
        /// **7 档 e 值：串行 vs 并行逐位相同**（判据），同时是对"`BurnArc` 类里没有静态可变状态"的
        /// **负向对照** —— 若有（像 `Gooding` 当年那样），并发跑就会漂，这条会红。
        /// </summary>
        [Fact]
        public void SevenShiftScan_ParallelAndSerial_AreBitIdentical()
        {
            var o = Orbit(7.0e6);
            const double node = 300.0;
            V3 dv = OrbitalManeuverCalculator.DeltaVToCircularize(o, node);
            double burnSeconds = dv.magnitude * 74_995.1 / 560_000.0;
            const int N = 7;

            double One(double sh) => BurnArc.TryPredict(o, node, dv, burnSeconds,
                sh + BurnArc.ExecutionDelaySeconds, out BurnArc.Result r, out _) ? r.eccentricity : double.NaN;

            var serial = new double[N];
            for (int k = 0; k < N; k++) serial[k] = One(-6.0 + 2.0 * k);

            var parallel = new double[N];
            ParallelOps.For(N, 1e-3, k => parallel[k] = One(-6.0 + 2.0 * k));

            for (int k = 0; k < N; k++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(serial[k]), BitConverter.DoubleToInt64Bits(parallel[k]));
        }
    }
}
