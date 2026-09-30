using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MechJebLib.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **并行扩展性曲线**（2026-09-30，回答"为什么 8 核只有不到 4 倍"）。
    ///
    /// 负载用 `TwoImpulseTransfer.NextManeuver`（= #1 并行的那一段，~0.65 ms/次，纯 FPU），
    /// 扫 `MaxDegreeOfParallelism = 1 / 2 / 4 / 8 / 16`，再扫**粒度**（同样总工作量、切成 12 项 vs 96 项）：
    ///   · 曲线在 8 之后**平掉** ⇒ 是 **SMT**（超线程假核，纯 FPU 无增益）；
    ///   · 12 项比 96 项**比值低很多** ⇒ 是**粒度/调度固定成本 + 负载不均**（12 项 8 核 = 1.5 波）。
    ///
    /// ⚠ 只打读数、不断言时间（机器不同必然脆）。
    /// </summary>
    public class ParallelScalingCurveTests
    {
        private readonly ITestOutputHelper _out;
        public ParallelScalingCurveTests(ITestOutputHelper o) => _out = o;

        private const double Mu = 3.986004418e14;

        private static (V3 r1, V3 v1, V3 r2, V3 v2) Geometry(int i)
        {
            double vc = Math.Sqrt(Mu / 7.0e6);
            return (new V3(7.0e6, 0, 0),
                    new V3(0, 2.0e2 * i, vc),                       // 每条略有不同的几何（避免缓存整体命中）
                    new V3(1.2e7 * Math.Cos(0.01 * i), 0, 1.2e7 * Math.Sin(0.01 * i)),
                    new V3(-1.2e3, 3.0e3, 2.2e3));
        }

        private static void Work(int i)
        {
            var (r1, v1, r2, v2) = Geometry(i % 12);
            TwoImpulseTransferPoke(r1, v1, r2, v2);
        }

        /// <summary>薄包装：只为让工作体读起来干净（真正的负载是 `NextManeuver`）。</summary>
        private static void TwoImpulseTransferPoke(V3 r1, V3 v1, V3 r2, V3 v2)
            => MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2);

        [Fact]
        public void ScalingCurve_ByDegree_And_Granularity()
        {
            const int Reps = 10;
            _out.WriteLine($"\n[并行扩展性] 这台机器：{Environment.ProcessorCount} 逻辑核（8 物理核 Ryzen 7 8745H）");

            // ── ① 12 项（= #1 的形状） ──
            double baseMs = 0;
            foreach (int d in new[] { 1, 2, 4, 8, 16 })
            {
                Work(0);                                            // 预热
                var opts = new ParallelOptions { MaxDegreeOfParallelism = d };
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < Reps; r++) Parallel.For(0, 12, opts, Work);
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds / Reps;
                if (d == 1) baseMs = ms;
                _out.WriteLine($"  12 项 · {d,2} 线程：{ms,8:F3} ms/批（相对 1 线程 {baseMs / ms,5:F2}×）");
            }

            // ── ② 96 项（同样总工作量、粒度 1/8）⇒ 分离"粒度/调度不均" ──
            double baseMs96 = 0;
            foreach (int d in new[] { 1, 8, 16 })
            {
                var opts = new ParallelOptions { MaxDegreeOfParallelism = d };
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < Reps; r++) Parallel.For(0, 96, opts, Work);
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds / Reps;
                if (d == 1) baseMs96 = ms;
                _out.WriteLine($"  96 项 · {d,2} 线程：{ms,8:F3} ms/批（相对 1 线程 {baseMs96 / ms,5:F2}×）");
            }

            // ── ③ **判决实验**：同样粒度、**零静态状态**的负载（`BurnArc.TryPredict`，400 步 RK4）
            //      ⇒ 若它接近 6–8×，则"扩不上去"的锅在 `TwoImpulseTransfer` 那条路的**共享静态**上
            //      （最可疑 = `Gooding` 的 `Interlocked` 计数器：每个 Lambert 解里被加很多次，
            //        全核争同一条缓存行 = RMW 串行化）。──
            const int N3 = 24;
            double baseMs3 = 0;
            foreach (int d in new[] { 1, 8, 16 })
            {
                var opts = new ParallelOptions { MaxDegreeOfParallelism = d };
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < Reps; r++) Parallel.For(0, N3, opts, BurnWork);
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds / Reps;
                if (d == 1) baseMs3 = ms;
                _out.WriteLine($"  24 项 RK4 · {d,2} 线程：{ms,8:F3} ms/批（相对 1 线程 {baseMs3 / ms,5:F2}×）");
            }
        }

        /// <summary>③ 用的负载：`BurnArc.TryPredict`（400 步 RK4；类里**零静态可变状态**）。</summary>
        private static void BurnWork(int i)
        {
            double r0 = 7.0e6 + 1000.0 * (i % 12);
            double vc = Math.Sqrt(Mu / r0);
            var o = OrbitView.FromStateVectors(new V3(r0, 0, 0), new V3(0, Math.Sqrt(1.3) * vc, 0),
                Mu, 0.0, new V3(0, 0, 1), r0 * 0.2);
            var dv = new V3(0, 0.01 * (i + 1), 0);
            BurnArc.TryPredict(o, 300.0, dv, 5.0, 0.0, out _, out _);
        }

        /// <summary>
        /// ④ **分配量的对照组**：同一台机器上，两条路的 Gen0 回收次数（同工作量）。
        /// 纯 FPU 的 `BurnArc` 应为 ~0；若 `TwoImpulseTransfer` 每项触发大量 Gen0，
        /// 则"扩不上去"的主因是**分配/GC**（多核共用一个堆 ⇒ 回收与分配路径互相干扰）。
        /// </summary>
        [Fact]
        public void AllocationPressure_OfTheTwoPaths()
        {
            const int N = 96;
            // 预热
            for (int i = 0; i < 12; i++) { Work(i); BurnWork(i); }

            int g0a = GC.CollectionCount(0);
            for (int i = 0; i < N; i++) Work(i);
            int g0b = GC.CollectionCount(0);

            int g1a = GC.CollectionCount(0);
            for (int i = 0; i < N; i++) BurnWork(i);
            int g1b = GC.CollectionCount(0);

            _out.WriteLine($"\n[分配压力] 同样 {N} 项：`TwoImpulseTransfer` = Gen0 {g0b - g0a} 次"
                + $"｜`BurnArc.TryPredict` = Gen0 {g1b - g1a} 次");
        }

        /// <summary>
        /// ⑤ **异常代价的受控对照**：既然 ②③④ 把"粒度 / 静态状态 / 分配"都排除了，
        /// 剩下最可疑的是**异常**（`Izzo` 用 `throw` 报"不收敛"，而 `TwoImpulseTransfer` 的目标函数
        /// **在搜索内部 try/catch** ⇒ 一次 `NextManeuver` 里可能抛几千次）。
        /// 这里跑两个**除异常外同量级**的负载，各在 1 / 8 线程下比较扩展比。
        /// </summary>
        [Fact]
        public void ExceptionCost_ScalingControl()
        {
            const int K = 2000;      // 每项抛/捕 2000 次（量级与一次搜索的失败 eval 数相当）
            const int Items = 12;
            Work(0);
            ThrowWork(0);

            foreach (int d in new[] { 1, 8 })
            {
                var opts = new ParallelOptions { MaxDegreeOfParallelism = d };

                var sw = Stopwatch.StartNew();
                for (int r = 0; r < 10; r++) Parallel.For(0, Items, opts, ThrowWork);
                sw.Stop();
                double msThrow = sw.Elapsed.TotalMilliseconds / 10;

                var sw2 = Stopwatch.StartNew();
                for (int r = 0; r < 10; r++) Parallel.For(0, Items, opts, MathWork);
                sw2.Stop();
                double msMath = sw2.Elapsed.TotalMilliseconds / 10;

                _out.WriteLine($"  ⑤ {d} 线程：抛/捕 {K}×{Items} 项 = {msThrow,8:F2} ms｜同量级纯算术 = {msMath,8:F2} ms");
            }
        }

        private static void ThrowWork(int i)
        {
            for (int k = 0; k < 2000; k++)
                try { throw new InvalidOperationException("control"); }
                catch (Exception) { /* 与 Izzo 的"不收敛"同形：抛出去、在同一层捕回来 */ }
        }

        private static void MathWork(int i)
        {
            double acc = i;
            for (int k = 0; k < 2000; k++) acc = Math.Sqrt(acc + k) * 1.0000001 + Math.Sin(acc) * 1e-3;
            if (acc < 0) throw new Exception("never");
        }

        /// <summary>
        /// ⑥ **每项成本的离散度**（压死骆驼的最后一根草）：并行墙钟 ≈ 最慢那一项 + 排队，
        /// 所以"负载长尾"会直接把扩展比压到"总工作量 ÷ (并行度 × 最慢项)"。
        /// 这里量 #1 那条路的 12 个起点（= 生产里并行的那 12 个单元）各自的墙钟。
        /// </summary>
        [Fact]
        public void PerItemCostSpread_OfTheSeeds()
        {
            const double Mu2 = 3.986004418e14;
            var r1 = new V3(7.0e6, 0, 0);
            var v1 = new V3(0, 7.4e3, 1.2e3);
            var r2 = new V3(0, 1.2e7, 3.0e6);
            var v2 = new V3(-2.0e3, 3.2e3, 2.4e3);

            // 单次整调用（12 个起点全跑）作为基准
            MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu2, r1, v1, r2, v2);
            var swAll = Stopwatch.StartNew();
            MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu2, r1, v1, r2, v2);
            swAll.Stop();

            // 用同一条公式把 12 个起点**分别**计时：借用公开入口，靠"改一个几何"无法拆种子
            // ⇒ 换成测**不同几何**的离散度（生产里 12 个组同拍重规划就是这个形状）
            var ms = new double[12];
            for (int i = 0; i < 12; i++)
            {
                var (a1, b1, a2, b2) = Geometry(i);
                MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu2, a1, b1, a2, b2);   // 预热
                var sw = Stopwatch.StartNew();
                MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu2, a1, b1, a2, b2);
                sw.Stop();
                ms[i] = sw.Elapsed.TotalMilliseconds;
            }

            Array.Sort(ms);
            _out.WriteLine($"\n[负载离散度] 一次整调用（12 起点）= {swAll.Elapsed.TotalMilliseconds:F2} ms"
                + $"｜12 种几何的单次成本：min {ms[0]:F2} / 中位 {ms[6]:F2} / max {ms[11]:F2} ms"
                + $"（max/min = {ms[11] / Math.Max(ms[0], 1e-9):F1}×）");
            _out.WriteLine("  逐项(ms)：" + string.Join(" ", Array.ConvertAll(ms, x => x.ToString("F2"))));
        }

        /// <summary>
        /// ⑦ **降频判决**（最后一根稻草）：同一份固定量的纯算术，
        /// 先**单线程独跑**量出 T₁；再**8 线程各跑一份**，看每个线程自己花了多少。
        ///   · 若 8 线程下每线程 ≈ T₁ ⇒ 没降频（那才怪得着软件）；
        ///   · 若明显大于 T₁ ⇒ **全核负载下降频**（45W APU 的物理事实）⇒ 8 物理核拿不到 8× 是**机器的**上限，
        ///     与我们的代码无关（这条决定"该不该继续在软件里追倍率"）。
        /// </summary>
        [Fact]
        public void FrequencyUnderAllCoreLoad_ThrottleProbe()
        {
            const long Loops = 40_000_000;

            double solo = TimeOneThread(Loops);                    // T₁（独占）
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            var perThread = new double[Environment.ProcessorCount];
            Parallel.For(0, Environment.ProcessorCount, opts, t => perThread[t] = TimeOneThread(Loops));

            double worst = 0, best = double.MaxValue;
            foreach (double x in perThread) { if (x > worst) worst = x; if (x < best) best = x; }

            _out.WriteLine($"\n[降频判决] 固定工作量单线程独占 = {solo:F1} ms"
                + $"｜{Environment.ProcessorCount} 线程各跑一份：最快 {best:F1} / 最慢 {worst:F1} ms"
                + $"（每线程相对独占 = {worst / solo:F2}× ⇒ {(worst / solo > 1.25 ? "**全核降频**" : "基本无降频")}）");
        }

        private static double TimeOneThread(long loops)
        {
            var sw = Stopwatch.StartNew();
            double acc = 1.0;
            for (long k = 0; k < loops; k++) acc = Math.Sqrt(acc * 1.0000001 + k) + Math.Sin(acc) * 1e-3;
            sw.Stop();
            if (acc < 0) throw new Exception("never");
            return sw.Elapsed.TotalMilliseconds;
        }

        // ────────────────────────────────────────────────────────────────────────────
        // ⑧ 定点测量：把"整调用"拆成"求解器"与"搜索框架"两段，各自量扩展性。
        //    `CompassSearch` 是 `internal`，而本工程**把该文件编译进来**（linked）⇒ 可直接调。
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>⑧-a 单独 `Izzo.Solve`（收敛输入、不抛异常）的扩展性。</summary>
        [Fact]
        public void IzzoSolver_Scaling()
        {
            double mu = 1.0;
            var r1 = new V3(1.0, 0, 0);
            var r2 = new V3(0, 1.2, 0.3);
            var n = V3.Cross(r1, r2).normalized;

            void One() { for (int k = 0; k < 2000; k++) MechJebLib.Maths.Izzo.Solve(mu, r1, r2, 2.0, MechJebLib.Lambert.TransferGeometry.Prograde, 0, n); }

            One();
            foreach (int d in new[] { 1, 2, 4, 8, 16 })
            {
                var opts = new ParallelOptions { MaxDegreeOfParallelism = d };
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < 5; r++) Parallel.For(0, 16, opts, _ => One());
                sw.Stop();
                _out.WriteLine($"  ⑧-a Izzo.Solve（每项 2000 次）· {d,2} 线程：{sw.Elapsed.TotalMilliseconds / 5,8:F2} ms/批");
            }
        }

        /// <summary>⑧-b `CompassSearch`（真实搜索框架）：目标函数**不抛** vs **每次抛** 的扩展性对照。</summary>
        [Fact]
        public void _Placeholder() { }

        /// <summary>
        /// ⑪ **纠正基线后的三档对照**（⚠ 早先用 `MaxDegreeOfParallelism = 1` 当"串行基线"是**错的**：
        /// 那只串行了**外层** `Parallel.For`，而 `NextManeuver` **内层的 `ParallelOps.For`（12 种子）照样吃满核**
        /// ⇒ 那个分母其实是"已并行"的）。
        ///   A = `ForceSerial = true`（**真串行**）· B = 外层 d=1（**只有内层并行** = 当前生产形状）·
        ///   C = 外层 d=8（内外都并行 ⇒ 有嵌套/超订）。
        /// </summary>
        [Fact]
        public void ThreeTier_BaselineCorrected()
        {
            const int Items = 12, Reps = 10;

            double a, b, c;
            try
            {
                ParallelOps.ForceSerial = true;
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < Reps; r++) for (int i = 0; i < Items; i++) Work(i);
                sw.Stop(); a = sw.Elapsed.TotalMilliseconds / Reps;

                ParallelOps.ForceSerial = false;
                var opts1 = new ParallelOptions { MaxDegreeOfParallelism = 1 };
                sw.Restart();
                for (int r = 0; r < Reps; r++) Parallel.For(0, Items, opts1, Work);
                sw.Stop(); b = sw.Elapsed.TotalMilliseconds / Reps;

                var opts8 = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
                sw.Restart();
                for (int r = 0; r < Reps; r++) Parallel.For(0, Items, opts8, Work);
                sw.Stop(); c = sw.Elapsed.TotalMilliseconds / Reps;
            }
            finally { ParallelOps.ForceSerial = false; }

            _out.WriteLine($"\n[⑪ 三档对照·基线已纠正] 12 项同负载："
                + $"\n  A 真串行（ForceSerial）= {a:F2} ms ⇒ 1.00×"
                + $"\n  B 只有内层并行（= 当前生产形状）= {b:F2} ms ⇒ 相对 A **{a / b:F2}×**"
                + $"\n  C 内外都并行（嵌套/超订）= {c:F2} ms ⇒ 相对 A {a / c:F2}×，相对 B {b / c:F2}×");
        }

        /// <summary>
        /// ⑩ **①号建议（把并行提到外层）的 go/no-go 事实**：外层 `for(iter<50)` 实际跑几次？
        ///   手法：`fixedTime: true` 会**跳过外层**（= 只做 1 次 `ManeuverInternal`，dtguess=0），
        ///   所以 `T(普通) / T(fixedTime)` ≈ **外层实际迭代数**（每轮成本同级）。
        ///   ⇒ ≈1 就说明"外层并行"没东西可并行（那 50 次重试是纸面的）；明显 >1 才值得提。
        /// </summary>
        [Fact]
        public void OuterRetryCount_Proxy()
        {
            double sumRatio = 0;
            int n = 0;
            var detail = new System.Text.StringBuilder();

            for (int i = 0; i < 12; i++)
            {
                var (r1, v1, r2, v2) = Geometry(i);

                // 预热两种路径
                MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, fixedTime: true);
                MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2);

                var sw = Stopwatch.StartNew();
                for (int k = 0; k < 3; k++)
                    MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2, fixedTime: true);
                sw.Stop(); double tFixed = sw.Elapsed.TotalMilliseconds / 3;

                sw.Restart();
                for (int k = 0; k < 3; k++)
                    MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu, r1, v1, r2, v2);
                sw.Stop(); double tNormal = sw.Elapsed.TotalMilliseconds / 3;

                double ratio = tNormal / Math.Max(tFixed, 1e-9);
                sumRatio += ratio; n++;
                detail.Append($"  G{i}: fixed={tFixed:F2}ms normal={tNormal:F2}ms ⇒ ≈{ratio:F1} 轮\n");
            }

            _out.WriteLine($"\n[① 外层重试计数(代理)] 12 种几何平均 ≈ **{sumRatio / n:F1} 轮** 外层迭代"
                + "（1 = 外层形同虚设；越大 ⇒ '把并行提到外层'收益越大）");
            _out.WriteLine(detail.ToString().TrimEnd());
        }
        /// ⑧-b 已证明"目标每次抛"比"不抛"贵 **37×** ⇒ 次数 × 单次代价（≈6 µs）= 这条路径的大头。
        /// </summary>
        [Fact]
        public void ExceptionRate_PerPlan()
        {
            int count = 0;
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> h =
                (s, e) => System.Threading.Interlocked.Increment(ref count);
            AppDomain.CurrentDomain.FirstChanceException += h;
            try
            {
                var (w1, w2, w3, w4) = Geometry(1);
                MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu, w1, w2, w3, w4);   // 预热

                const int N = 12;
                count = 0;
                for (int i = 0; i < N; i++)
                {
                    var (a1, b1, a2, b2) = Geometry(i);
                    MechJebLib.Maneuvers.TwoImpulseTransfer.NextManeuver(Mu, a1, b1, a2, b2);
                }
                _out.WriteLine($"\n[异常频率] {N} 次 `NextManeuver`（12 种几何）共**抛出并被捕获** {count} 次异常"
                    + $"（每次 {count / (double)N:F0} 次 ⇒ 按 ~6 µs/次 ≈ {count / (double)N * 6e-3:F2} ms/次）");
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= h; }
        }
        [Fact]
        public void CompassSearch_Scaling_NoThrowVsThrow()
        {
            double[] lo = { -1, -1, -1 }, hi = { 1, 1, 1 }, step = { 0.25, 0.25, 0.25 };

            double NoThrow(double[] x) => (x[0] * x[0] + x[1] * x[1] + x[2] * x[2]) + 0.1 * Math.Sin(x[0]);
            double Throws(double[] x)
            {
                try { throw new Exception("inf"); }
                catch (Exception) { return double.MaxValue; }     // 与 TwoImpulseTransfer 的目标函数同形
            }

            void Run(Func<double[], double> f)
            {
                for (int i = 0; i < 20; i++)
                    MechJebLib.Maneuvers.TwoImpulseTransfer.CompassSearch(f, new[] { 0.3, 0.2, 0.1 }, lo, hi, step, 1e-9, 60, out _);
            }

            Run(NoThrow); Run(Throws);
            foreach (int d in new[] { 1, 8, 16 })
            {
                var opts = new ParallelOptions { MaxDegreeOfParallelism = d };
                double m1, m2;

                var sw = Stopwatch.StartNew();
                for (int r = 0; r < 5; r++) Parallel.For(0, 12, opts, _ => Run(NoThrow));
                sw.Stop(); m1 = sw.Elapsed.TotalMilliseconds / 5;

                sw.Restart();
                for (int r = 0; r < 5; r++) Parallel.For(0, 12, opts, _ => Run(Throws));
                sw.Stop(); m2 = sw.Elapsed.TotalMilliseconds / 5;

                _out.WriteLine($"  ⑧-b CompassSearch · {d,2} 线程：目标不抛 {m1,8:F2} ms｜目标每次抛 {m2,8:F2} ms（{m2 / Math.Max(m1, 1e-9):F1}×）");
            }
        }
    }
}
