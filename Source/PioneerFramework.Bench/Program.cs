using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using UnityEngine;
using PioneerFramework;
using MechJebLib.Lambert;   // TransferGeometry（我们那份移植文件刻意放在上游命名空间里）
using MechJebLib.Maths;     // Izzo（注意：Izzo.cs 在 Lambert 文件夹里，命名空间却是 Maths）
using static PioneerFramework.PFViewSurfaceDetails;

namespace PioneerFramework.Bench
{
    /// <summary>
    /// M0 基线台架（2026-10-14）：数学核心各热区的**改前数字**。
    /// 纪律：① 必须 `-c Release`；② 每个变体预热 350 ms（升频）后交错测 9 轮，报最好/中位/散度；
    /// ③ 同时给"每次分配字节"（M1 零分配批的对照）；④ 结果打一张表，抄进 `数学核心NET10优化计划`；
    /// ⑤ 所有结果进 checksum 防死代码消除；⑥ **对比必须交错测**（分块先后测会得出假结论——2026-10-14 事故）。
    /// </summary>
    internal static class Program
    {
        private static double checksum;

        private static void Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            // 本机后台有 ZCode/firefox 等 ⇒ 提优先级，少被抢占（"平衡"电源方案的频率爬升另有 Warmup 治）
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { /* 无权限就随它 */ }
            Console.WriteLine("═══ PioneerFramework 数学核心基线台架（M0）═══");
            ProbeEnvironment();

            BenchMicroOps();
            BenchGravity();
            BenchGravityAB();
            BenchBake();
            BenchApplyReliefAB();
            BenchBakeKernelsAB();
            BenchSolvers();
            BenchParallelFloor();

            Console.WriteLine();
            Console.WriteLine($"checksum = {checksum:G17}（非零即可；防死代码消除）");
        }

        // ════════════════════════════════════════════════════
        //  台架骨架
        // ════════════════════════════════════════════════════

        private static string Human(double ns)
            => ns >= 1e9 ? $"{ns / 1e9,9:N3} s" : ns >= 1e6 ? $"{ns / 1e6,9:N3} ms" : ns >= 1e3 ? $"{ns / 1e3,9:N2} µs" : $"{ns,9:N1} ns";

        /// <summary>预热 350 ms：本机是笔记本"平衡"电源方案 ⇒ 短窗测量会被频率爬升吃掉
        ///（2026-10-14 事故：掩码 +126%、差分 ×1.8 皆噪声，散度 49–541%）。</summary>
        private static void Warmup(Action body)
        {
            var sw = Stopwatch.StartNew();
            do { body(); } while (sw.ElapsedMilliseconds < 350);
        }

        // ⚠ 本机是笔记本 ⇒ 单一窗口的 best-of-5 会被降频/后台抖动整段抬高
        //（2026-10-14 事故：掩码 +126% 是噪声不是缺陷）⇒ 一律给 best/中位/离散度，
        //  需要对比的变体必须**交错测**（BenchVariants），不许分块先后测。
        private static void Bench(string label, int iters, Action body, int reps = 9)
        {
            Warmup(body);
            var best = double.MaxValue;
            var samples = new double[reps];
            long alloc = 0;
            for (int r = 0; r < reps; r++)
            {
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < iters; i++) body();
                long t1 = Stopwatch.GetTimestamp();
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                double ns = (t1 - t0) * 1e9 / Stopwatch.Frequency / iters;
                samples[r] = ns;
                if (ns < best) best = ns;
                long perIter = (a1 - a0) / iters;
                if (perIter > alloc) alloc = perIter;
            }
            Array.Sort(samples);
            double med = samples[reps / 2];
            double spread = (samples[reps - 1] - samples[0]) / med * 100.0;
            Console.WriteLine($"{label,-58} {Human(best)}  (中位 {Human(med)}, 散 {spread,4:N0}%)   {alloc,7:N1} B/次");
        }

        /// <summary>交错 A/B 对比：同一轮里轮流测全部变体 ⇒ 机器降频/抖动同等地落在每个变体上。</summary>
        private static void BenchVariants(int iters, int reps, params (string Label, Action Body)[] cases)
        {
            foreach (var c in cases) Warmup(c.Body);
            var samples = new double[cases.Length][];
            var alloc = new long[cases.Length];
            for (int i = 0; i < cases.Length; i++) samples[i] = new double[reps];
            for (int r = 0; r < reps; r++)
                for (int i = 0; i < cases.Length; i++)
                {
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    long t0 = Stopwatch.GetTimestamp();
                    for (int k = 0; k < iters; k++) cases[i].Body();
                    long t1 = Stopwatch.GetTimestamp();
                    long a1 = GC.GetAllocatedBytesForCurrentThread();
                    samples[i][r] = (t1 - t0) * 1e9 / Stopwatch.Frequency / iters;
                    long perIter = (a1 - a0) / iters;
                    if (perIter > alloc[i]) alloc[i] = perIter;
                }
            for (int i = 0; i < cases.Length; i++)
            {
                Array.Sort(samples[i]);
                double med = samples[i][reps / 2];
                double spread = (samples[i][reps - 1] - samples[i][0]) / med * 100.0;
                Console.WriteLine($"{cases[i].Label,-58} {Human(samples[i][0])}  (中位 {Human(med)}, 散 {spread,4:N0}%)   {alloc[i],7:N1} B/次");
            }
        }

        private static void ProbeEnvironment()
        {
            Console.WriteLine();
            Console.WriteLine("── 环境 ──");
            Console.WriteLine($"Runtime      : {RuntimeInformation.FrameworkDescription}");
            Console.WriteLine($"Arch / CPUs  : {RuntimeInformation.ProcessArchitecture} / {Environment.ProcessorCount}");
            Console.WriteLine($"CPU          : {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}");
            Console.WriteLine($"ServerGC     : {System.Runtime.GCSettings.IsServerGC}｜GC mode={System.Runtime.GCSettings.LatencyMode}");
            Console.WriteLine($"Vector<T>    : IsHardwareAccelerated={Vector.IsHardwareAccelerated}｜double×{Vector<double>.Count}｜float×{Vector<float>.Count}");
            Console.WriteLine($"Intrinsics   : SSE2={Sse2.IsSupported}｜AVX2={Avx2.IsSupported}｜AVX512F={Avx512F.IsSupported}｜FMA={Fma.IsSupported}");
        }

        // ════════════════════════════════════════════════════
        //  1. 微算子（DVector3）
        // ════════════════════════════════════════════════════

        private static void BenchMicroOps()
        {
            Console.WriteLine();
            Console.WriteLine("── 1. 微算子（DVector3，逐次调用）──");
            DVector3 a = new DVector3(3.1e6, -2.7e6, 1.9e6);
            DVector3 b = new DVector3(-7.3e6, 4.1e6, 6.2e6);

            Bench("DVector3.normalized", 50_000_000, () => { checksum += a.normalized.x; });
            Bench("DVector3.Dot", 50_000_000, () => { checksum += DVector3.Dot(a, b); });
            Bench("DVector3.Cross", 50_000_000, () => { checksum += DVector3.Cross(a, b).z; });
            Bench("DVector3 + *（逐元素）", 50_000_000, () => { checksum += (a + b * 1.5).y; });
        }

        // ════════════════════════════════════════════════════
        //  2. 引力场（11 源：地月系 + 太阳 + 8 颗合成天体）
        // ════════════════════════════════════════════════════

        private static List<GravitySource> MakeSources()
        {
            var src = new List<GravitySource>(11)
            {
                new GravitySource(DVector3.zero, DVector3.zero, 3.986004418e14, 0),                       // 地球（根）
                new GravitySource(new DVector3(3.844e8, 0, 0), new DVector3(0, 1022, 0), 4.9048695e12, 1), // 月
                new GravitySource(new DVector3(-1.496e11, 0, 0), new DVector3(0, 29.78e3, 0), 1.32712440018e20, 2), // 日
            };
            for (int i = 3; i < 11; i++)
            {
                double t = i * 0.7;
                src.Add(new GravitySource(
                    new DVector3(Math.Cos(t) * 2.0e7, Math.Sin(t) * 2.0e7, Math.Sin(t * 2) * 1.0e6),
                    new DVector3(-Math.Sin(t) * 3.0e3, Math.Cos(t) * 3.0e3, 0),
                    1e11 * i, i));
            }
            return src;
        }

        private static void BenchGravity()
        {
            Console.WriteLine();
            Console.WriteLine("── 2. 引力场（11 源/次；整步 = 每群每步各一次）──");
            var sources = MakeSources();
            DVector3 pos = new DVector3(7.0e6, 0, 0);
            DVector3 vel = new DVector3(0, 7546, 0);
            var ignored = new List<int> { 2 };   // 掩码路径（大气档形态）
            var emptyMask = new List<int>();     // 空掩码探针
            var atmMask = new List<int>();       // 大气档真实形态：只留中心天体（0），忽略其余
            for (int i = 1; i < 11; i++) atmMask.Add(i);

            // 交错测：掩码变体与差分对比必须在同一轮里轮转（本机抖动大，分块先后测会得出假结论）
            BenchVariants(1_000_000, 9,
                ("FieldAccelJerk（11 源，无掩码）", () =>
                {
                    NBodyIntegrator.FieldAccelJerk(pos, vel, sources, null, out var acc, out var jrk);
                    checksum += acc.y + jrk.z;
                }),
                ("FieldAccelJerk（掩码 null 显式）", () =>
                {
                    List<int>? none = null;
                    NBodyIntegrator.FieldAccelJerk(pos, vel, sources, none, out var acc, out var jrk);
                    checksum += acc.y + jrk.z;
                }),
                ("FieldAccelJerk（掩码 0 项 = 只加判断）", () =>
                {
                    NBodyIntegrator.FieldAccelJerk(pos, vel, sources, emptyMask, out var acc, out var jrk);
                    checksum += acc.y + jrk.z;
                }),
                ("FieldAccelJerk（掩码 1 项）", () =>
                {
                    NBodyIntegrator.FieldAccelJerk(pos, vel, sources, ignored, out var acc, out var jrk);
                    checksum += acc.y + jrk.z;
                }),
                ("FieldAccelJerk（掩码 10 项 = 大气档形态）", () =>
                {
                    NBodyIntegrator.FieldAccelJerk(pos, vel, sources, atmMask, out var acc, out var jrk);
                    checksum += acc.y + jrk.z;
                }),
                ("DifferentialAccelJerk（11 源）", () =>
                {
                    // 与上面几次同轮交错（非等价物，只作同窗口参照：它是"每源两次求值"的定义成本）
                    NBodyIntegrator.DifferentialAccelJerk(pos, vel, pos + new DVector3(120, 0, 0), vel + new DVector3(0, 0.1, 0),
                        sources, out var acc, out var jrk);
                    checksum += acc.y + jrk.z;
                }));
        }

        // ════════════════════════════════════════════════════
        //  3. 烘焙 CPU 核（真实规模：3072² 像素 / 1536² 高度场 / 真实盖章数）
        // ════════════════════════════════════════════════════

        private static void BenchBake()
        {
            Console.WriteLine();
            Console.WriteLine("── 3. 烘焙 CPU 核（真实规模）──");
            const int size = 3072;          // 补丁图边长
            const int fs = size / 2;        // 高度场 1536²

            var rng = new System.Random(20261014);
            var px = new Color32[size * size];
            var field = new float[fs * fs];
            var low = new float[fs * fs];
            var dst = new float[fs * fs];
            var tmp = new float[fs * fs];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32((byte)(60 + rng.Next(40)), (byte)(70 + rng.Next(40)), (byte)(90 + rng.Next(30)), 255);
            for (int i = 0; i < field.Length; i++) { field[i] = (float)rng.NextDouble(); low[i] = field[i] * 0.5f; }

            // 真实盖章数（Earth 实测：山 14882 · 河 2572 段 · 路 5176 段）
            var hills = new List<HillStamp>(14882);
            for (int i = 0; i < 14882; i++)
                hills.Add(new HillStamp { x = (float)rng.NextDouble() * fs, y = (float)rng.NextDouble() * fs, radius = 3f + (float)rng.NextDouble() * 6f, weight = 0.5f + (float)rng.NextDouble() });
            var lines = new List<LineStamp>(2572);
            for (int i = 0; i < 2572; i++)
            {
                // ⚠ 真实形态：河/路是**相邻格心**串起来的短段（一格 ≈ 7.8 px）。用随机长线会把 bbox 放大到全图
                //   ⇒ 数字失真一个数量级（首测 12 s 的教训）。这里按生产尺度：段长 ~8 px。
                float x0 = (float)rng.NextDouble() * size, y0 = (float)rng.NextDouble() * size;
                lines.Add(new LineStamp { ax = x0, ay = y0, bx = x0 + 5.5f, by = y0 + 5.5f, halfWidth = 1.4f });
            }

            Bench("AccumulateHills 1536² × 14882 座", 20, () => { AccumulateHills(field, fs, hills); checksum += field[12345]; });
            Bench("BoxBlur 1536² r=8 passes=2", 20, () => { BoxBlur(field, dst, tmp, fs, 8, 2); checksum += dst[777]; });
            Bench("MeanAbsRelief 1536²", 20, () => { checksum += MeanAbsRelief(field, low, fs, 0.02f); });
            Bench("ApplyRelief 3072²（2×双线性/px）", 20, () => { ApplyRelief(px, size, field, low, fs, 0.5f, 0.35f); checksum += px[999].r; });
            Bench("DrawSegment × 2572（河/路一段）", 10, () =>
            {
                for (int i = 0; i < lines.Count; i++) DrawSegment(px, size, lines[i], new Color32(73, 82, 100, 255), 0.9f);
                checksum += px[555].r;
            });
        }

        // ════════════════════════════════════════════════════
        //  4. 求解器（Gooding / Izzo Lambert）
        // ════════════════════════════════════════════════════

        private static void BenchSolvers()
        {
            Console.WriteLine();
            Console.WriteLine("── 4. 求解器（单次调用）──");
            const double mu = 3.986004418e14;
            DVector3 r1 = new DVector3(7.0e6, 0, 0);
            DVector3 r2 = new DVector3(0, 7.4e6, 0);
            const double tof = 1800.0;

            Bench("Gooding.Solve（LEO 90° 移相，1800 s）", 20_000, () =>
            {
                var (vi, vf) = Gooding.Solve(mu, r1, r2, tof);
                checksum += vi.y + vf.x;
            });
            Bench("Izzo.Solve（同题；MJ 移植面）", 20_000, () =>
            {
                DVector3 hNormal = DVector3.Cross(r1, r2).normalized;   // Prograde 需要轨道面法向
                var (vi, vf) = Izzo.Solve(mu, r1, r2, tof, TransferGeometry.Prograde, 0, hNormal);
                checksum += vi.y + vf.x;
            });
        }

        // ════════════════════════════════════════════════════
        //  8. 烘焙另两核 A/B（M3）：AccumulateHills / BoxBlur（竖直半轴）
        //  ⚠ 比对按 **float 位型**（抓 −0/+0、NaN 差异；逐值 == 会漏掉符号零）。
        // ════════════════════════════════════════════════════

        private static void BenchBakeKernelsAB()
        {
            Console.WriteLine();
            Console.WriteLine("── 8. 烘焙另两核 A/B（逐位断言 + 同轮交错）──");

            AssertAccumulateHillsSameAsLegacy();
            AssertBoxBlurSameAsLegacy();
            SelfCheckBakeKernelsAssert();
            Console.WriteLine("  逐位相等断言：AccumulateHills（512²×2000 座，含 <8px 尾段）+ BoxBlur（64/130/512 × r=3,8 × p=1,2）—— 全过 ✓");

            const int fs = 1536;
            var rng = new System.Random(20261014);
            var src = new float[fs * fs];
            for (int i = 0; i < src.Length; i++) src[i] = (float)rng.NextDouble();
            var dstA = new float[fs * fs];
            var dstB = new float[fs * fs];
            var tmpA = new float[fs * fs];
            var tmpB = new float[fs * fs];
            var srcA = (float[])src.Clone();
            var srcB = (float[])src.Clone();
            var fieldA = new float[fs * fs];
            var fieldB = new float[fs * fs];
            var hills = new List<HillStamp>(14882);
            for (int i = 0; i < 14882; i++)
                hills.Add(new HillStamp { x = (float)rng.NextDouble() * fs, y = (float)rng.NextDouble() * fs, radius = 3f + (float)rng.NextDouble() * 6f, weight = 0.5f + (float)rng.NextDouble() });

            BenchVariants(20, 9,
                ("A 旧 BoxBlur（全标量）", () => { LegacyBoxBlur(srcA, dstA, tmpA, fs, 8, 2); checksum += dstA[777]; }),
                ("B 新 BoxBlur（竖直半轴向 SIMD）", () => { BoxBlur(srcB, dstB, tmpB, fs, 8, 2); checksum += dstB[777]; }));
            BenchVariants(20, 9,
                ("A 旧 AccumulateHills（全标量）", () => { LegacyAccumulateHills(fieldA, fs, hills); checksum += fieldA[12345]; }),
                ("B 新 AccumulateHills（x 侧 SIMD + Select）", () => { AccumulateHills(fieldB, fs, hills); checksum += fieldB[12345]; }));
        }

        private static void AssertAccumulateHillsSameAsLegacy()
        {
            const int size = 512;
            var rng = new System.Random(4242);
            var a = new float[size * size];
            var b = new float[size * size];
            var stamps = new List<HillStamp>();
            for (int i = 0; i < 2000; i++)
                stamps.Add(new HillStamp
                {
                    x = (float)rng.NextDouble() * size,
                    y = (float)rng.NextDouble() * size,
                    radius = 1f + (float)rng.NextDouble() * 7f,   // 含 <8 px 的小穹顶 ⇒ 尾段/退化都走到
                    weight = (float)rng.NextDouble() * 1.5f,
                });
            LegacyAccumulateHills(a, size, stamps);
            AccumulateHills(b, size, stamps);
            AssertFloatsIdentical(a, b, "AccumulateHills");
        }

        private static void AssertBoxBlurSameAsLegacy()
        {
            foreach (int size in new[] { 64, 130, 512 })
            {
                var rng = new System.Random(9000 + size);
                var src = new float[size * size];
                for (int i = 0; i < src.Length; i++) src[i] = (float)(rng.NextDouble() * 4 - 2);
                foreach (int radius in new[] { 3, 8 })
                {
                    foreach (int passes in new[] { 1, 2 })
                    {
                        // ⚠ 两腿必须**各自一份 src**：`BoxBlur` 在 p+1<passes 时把 dst 拷回 src
                        //   （写穿输入，既有语义）——共用一份会让第二腿跑到被改过的输入上（本轮实测踩到，
                        //   断言当场报"逐位不等"，但错的是测试装配不是实现）。
                        var srcA = (float[])src.Clone();
                        var srcB = (float[])src.Clone();
                        var da = new float[size * size];
                        var db = new float[size * size];
                        var ta = new float[size * size];
                        var tb = new float[size * size];
                        LegacyBoxBlur(srcA, da, ta, size, radius, passes);
                        BoxBlur(srcB, db, tb, size, radius, passes);
                        AssertFloatsIdentical(da, db, $"BoxBlur(size={size}, r={radius}, p={passes})");
                    }
                }
            }
        }

        private static void AssertFloatsIdentical(float[] a, float[] b, string what)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                    throw new InvalidOperationException($"{what} 逐位不等：i={i} 旧 {a[i]:G9}（位 {BitConverter.SingleToInt32Bits(a[i]):X8}）≠ 新 {b[i]:G9}（位 {BitConverter.SingleToInt32Bits(b[i]):X8}）");
            }
        }

        private static void SelfCheckBakeKernelsAssert()
        {
            // ① 同输入 ⇒ 必须判相等；② 单点扰动 ⇒ 必须判不等（断言是活的）
            // ⚠ `BoxBlur` 会把 dst 拷回 src（p+1<passes 时）⇒ 每次调用都喂**独立克隆**，
            //   否则第二次调用跑在已被改写的输入上（本轮连踩两次，都是测试装配而非实现）。
            const int size = 64;
            var rng = new System.Random(5);
            var src = new float[size * size];
            var t = new float[size * size];
            for (int i = 0; i < src.Length; i++) src[i] = (float)rng.NextDouble();
            var d0 = new float[size * size];
            var d1 = new float[size * size];
            BoxBlur((float[])src.Clone(), d0, t, size, 4, 2);
            BoxBlur((float[])src.Clone(), d1, t, size, 4, 2);
            if (FirstFloatDiff(d0, d1) >= 0) throw new InvalidOperationException("断言自证失败：同输入竟判不等（BoxBlur）");
            var src2 = (float[])src.Clone();
            src2[7 * size + 9] += 1f;
            BoxBlur(src2, d1, t, size, 4, 2);
            if (FirstFloatDiff(d0, d1) < 0)
                throw new InvalidOperationException("断言自证失败：BoxBlur 单点 +1 后输出未变 ⇒ 场→输出通路是死的");

            var stamps = new List<HillStamp> { new HillStamp { x = 20f, y = 20f, radius = 6f, weight = 1f } };
            var h0 = new float[size * size];
            var h1 = new float[size * size];
            AccumulateHills(h0, size, stamps);
            stamps[0] = new HillStamp { x = 20f, y = 20f, radius = 6.5f, weight = 1f };
            AccumulateHills(h1, size, stamps);
            if (FirstFloatDiff(h0, h1) < 0)
                throw new InvalidOperationException("断言自证失败：AccumulateHills 半径扰动后输出未变");
        }

        private static int FirstFloatDiff(float[] a, float[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return i;
            return -1;
        }

        // ── 改动前的逐字旧实现（标量；只做 A/B 参照，不进生产）──

        private static void LegacyAccumulateHills(float[] field, int size, IList<HillStamp> stamps)
        {
            for (int i = 0; i < stamps.Count; i++)
            {
                HillStamp s = stamps[i];
                if (s.radius <= 0.05f || s.weight <= 0f) continue;
                float r2 = s.radius * s.radius;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(s.x - s.radius));
                int x1 = Mathf.Min(size - 1, Mathf.CeilToInt(s.x + s.radius));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(s.y - s.radius));
                int y1 = Mathf.Min(size - 1, Mathf.CeilToInt(s.y + s.radius));
                for (int y = y0; y <= y1; y++)
                {
                    int row = y * size;
                    float dy = y + 0.5f - s.y;
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = x + 0.5f - s.x;
                        float d2 = dx * dx + dy * dy;
                        if (d2 >= r2) continue;
                        float t = 1f - d2 / r2;
                        field[row + x] += s.weight * t * t;
                    }
                }
            }
        }

        private static void LegacyBoxBlur(float[] src, float[] dst, float[] tmp, int size, int radius, int passes)
        {
            if (radius < 1 || size <= 1) return;
            for (int p = 0; p < passes; p++)
            {
                LegacyBlurAxis(src, tmp, size, radius, true);
                LegacyBlurAxis(tmp, dst, size, radius, false);
                if (p + 1 < passes) Array.Copy(dst, src, size * size);
            }
        }

        private static void LegacyBlurAxis(float[] src, float[] dst, int size, int radius, bool horizontal)
        {
            float norm = 1f / (2 * radius + 1);
            for (int a = 0; a < size; a++)
            {
                float sum = 0f;
                for (int k = -radius; k <= radius; k++)
                    sum += LegacySample(src, size, a, k, horizontal);
                for (int b = 0; b < size; b++)
                {
                    if (horizontal) dst[a * size + b] = sum * norm;
                    else dst[b * size + a] = sum * norm;
                    sum -= LegacySample(src, size, a, b - radius, horizontal);
                    sum += LegacySample(src, size, a, b + radius + 1, horizontal);
                }
            }
        }

        private static float LegacySample(float[] f, int size, int a, int b, bool horizontal)
        {
            int c = b < 0 ? 0 : (b >= size ? size - 1 : b);
            return horizontal ? f[a * size + c] : f[c * size + a];
        }

        // ════════════════════════════════════════════════════
        //  7. ApplyRelief A/B（M3 SIMD 首批）：标量原式 vs `map=0.5` 向量快路
        //  ⚠ 断言比的是**最终字节**（烘焙写出去的就是字节）⇒ 文档口径：字节逐位相同即契约。
        // ════════════════════════════════════════════════════

        private static void BenchApplyReliefAB()
        {
            Console.WriteLine();
            Console.WriteLine("── 7. ApplyRelief A/B（同轮交错；先逐位相等断言）──");

            foreach (int size in new[] { 32, 64, 130, 256, 3072 })
                AssertApplyReliefSameAsLegacy(size);
            SelfCheckApplyReliefAssert();
            Console.WriteLine("  逐位相等断言：32/64/130/256/3072（随机场 + 平场）—— 全过 ✓（含断言自证）");

            const int S = 3072, FS = S / 2;
            var rng = new System.Random(20261014);
            var field = new float[FS * FS];
            var low = new float[FS * FS];
            for (int i = 0; i < field.Length; i++) { field[i] = (float)rng.NextDouble(); low[i] = field[i] * 0.7f; }
            var pxA = new Color32[S * S];
            var pxB = new Color32[S * S];
            for (int i = 0; i < pxA.Length; i++)
            {
                var c = new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
                pxA[i] = c; pxB[i] = c;
            }
            const float scale = 0.05f, strength = 0.08f;

            BenchVariants(2, 9,
                ("A 旧 ApplyRelief（标量原式）", () =>
                {
                    LegacyApplyRelief(pxA, S, field, low, FS, scale, strength);
                    checksum += pxA[1000].r;
                }),
                ("B 新 ApplyRelief（map=0.5 向量快路）", () =>
                {
                    ApplyRelief(pxB, S, field, low, FS, scale, strength);
                    checksum += pxB[1000].r;
                }));
        }

        private static void AssertApplyReliefSameAsLegacy(int size)
        {
            int fs = size / 2;
            var rng = new System.Random(1000 + size);
            var px0 = new Color32[size * size];
            for (int i = 0; i < px0.Length; i++)
                px0[i] = new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            var field = new float[fs * fs];
            var low = new float[fs * fs];
            for (int i = 0; i < field.Length; i++) { field[i] = (float)(rng.NextDouble() * 2.0 - 0.5); low[i] = field[i] * 0.8f; }
            RunApplyReliefCompare(size, fs, px0, field, low);          // 随机场（含负值/越界 shade 的夹紧档）
            for (int i = 0; i < low.Length; i++) low[i] = field[i];
            RunApplyReliefCompare(size, fs, px0, field, low);          // 平场（relief=0 的恒等档）
        }

        private static void RunApplyReliefCompare(int size, int fs, Color32[] px0, float[] field, float[] low)
        {
            var a = (Color32[])px0.Clone();
            var b = (Color32[])px0.Clone();
            LegacyApplyRelief(a, size, field, low, fs, 0.05f, 0.08f);
            ApplyRelief(b, size, field, low, fs, 0.05f, 0.08f);
            int bad = FirstDiff(a, b);
            if (bad >= 0)
                throw new InvalidOperationException(
                    $"ApplyRelief 逐位不等（size={size}）：px[{bad}] 旧 {a[bad]} ≠ 新 {b[bad]}");
        }

        private static int FirstDiff(Color32[] a, Color32[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a) return i;
            return -1;
        }

        private static void SelfCheckApplyReliefAssert()
        {
            const int size = 64, fs = 32;
            var px0 = new Color32[size * size];
            for (int i = 0; i < px0.Length; i++) px0[i] = new Color32(120, 130, 140, 255);
            var field = new float[fs * fs];
            var low = new float[fs * fs];
            // ⚠ 参数要**非饱和**：field−low = 0.05 ⇒ shade = 1 + (0.08/0.05)·0.05 = 1.08（在 [0.60, 1.35] 内）。
            //   若取 0.5/0.25（shade 顶在 hi=1.35），扰动根本**不该**改变输出 —— 第一版就这么把对的实现
            //   当"通路是死的"给炸了（自证本身也得先选对参数，第二次踩坑）。
            for (int i = 0; i < field.Length; i++) { field[i] = 0.5f; low[i] = 0.45f; }

            // ① 同输入：比较必须判相等（否则它恒红）
            var a = (Color32[])px0.Clone();
            var b = (Color32[])px0.Clone();
            LegacyApplyRelief(a, size, field, low, fs, 0.05f, 0.08f);
            ApplyRelief(b, size, field, low, fs, 0.05f, 0.08f);
            if (FirstDiff(a, b) >= 0) throw new InvalidOperationException("断言自证失败：同输入竟判不等");

            // ② 大扰动一个场元素：输出必须**相对未扰动的那份**变化（证明"场→字节"通路是活的）。
            //   ⚠ 不是"旧 vs 新"（那两腿看到同一份扰动 ⇒ 恒相等，第一版就这么写错了，被本自证当场逮住）。
            field[5 * fs + 7] = 100f;
            var d = (Color32[])px0.Clone();
            ApplyRelief(d, size, field, low, fs, 0.05f, 0.08f);
            if (FirstDiff(a, d) < 0)
                throw new InvalidOperationException("断言自证失败：扰动 100 后输出未变 ⇒ 场→字节通路是死的");
        }

        // ── 改动前的逐字旧实现（标量；只做 A/B 参照，不进生产）──

        private static void LegacyApplyRelief(Color32[] px, int size, float[] field, float[] low, int fieldSize,
            float reliefScale, float strength, float lo = 0.60f, float hi = 1.35f)
        {
            if (reliefScale <= 1e-6f || strength <= 0f || fieldSize < 2) return;
            float k = strength / reliefScale;
            float map = (float)fieldSize / size;
            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                float fy = (y + 0.5f) * map - 0.5f;
                for (int x = 0; x < size; x++)
                {
                    float fx = (x + 0.5f) * map - 0.5f;
                    float relief = LegacySampleField(field, fieldSize, fx, fy) - LegacySampleField(low, fieldSize, fx, fy);
                    float shade = Mathf.Clamp(1f + k * relief, lo, hi);
                    Color32 c = px[row + x];
                    px[row + x] = new Color32(
                        (byte)Mathf.Clamp(c.r * shade, 0f, 255f),
                        (byte)Mathf.Clamp(c.g * shade, 0f, 255f),
                        (byte)Mathf.Clamp(c.b * shade, 0f, 255f),
                        c.a);
                }
            }
        }

        private static float LegacySampleField(float[] f, int size, float fx, float fy)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, size - 2);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, size - 2);
            float tx = Mathf.Clamp01(fx - x0);
            float ty = Mathf.Clamp01(fy - y0);
            int r0 = y0 * size, r1 = r0 + size;
            float a = Mathf.Lerp(f[x0 + r0], f[x0 + 1 + r0], tx);
            float b = Mathf.Lerp(f[x0 + r1], f[x0 + 1 + r1], tx);
            return Mathf.Lerp(a, b, ty);
        }

        // ════════════════════════════════════════════════════
        //  6. 并行地板标定（ParallelOps.ShouldParallelize 的 MinParallelWorkSeconds）
        //  ⚠ 必须同轮交错（串行/并行各测一遍、轮流 9 轮取最好）——分块先后测会被降频骗（见 §1 仪器事故）。
        // ════════════════════════════════════════════════════

        private static void BenchParallelFloor()
        {
            Console.WriteLine();
            Console.WriteLine("── 6. 并行地板标定（总工作量扫描；串行 vs Parallel.For 同轮交错 best-of-9）──");

            // 单件成本标定：1 µs ≈ 多少次 Sqrt 累加（本机口径）
            double itersPerUs;
            {
                long t0 = Stopwatch.GetTimestamp();
                double s = 0;
                const int probe = 4_000_000;
                for (int i = 0; i < probe; i++) s += Math.Sqrt(i * 0.5 + 1.0);
                long t1 = Stopwatch.GetTimestamp();
                checksum += s;
                double us = (t1 - t0) * 1e6 / Stopwatch.Frequency;
                itersPerUs = probe / us;
                Console.WriteLine($"  标定：1 µs ≈ {itersPerUs:N0} 次 Sqrt 累加（{probe:N0} 次 = {us:N0} µs）");
            }

            SweepFloor("count=16（=本机核数）", 16, itersPerUs);
            SweepFloor("count=8", 8, itersPerUs);
            SweepFloor("count=4", 4, itersPerUs);
            SweepFloor("count=2（最坏情形：只有两件可并行）", 2, itersPerUs);
        }

        private static void SweepFloor(string label, int items, double itersPerUs)
        {
            Console.WriteLine($"  ── {label} ──");
            var part = new double[items];
            double[] levelsUs = { 8, 16, 32, 64, 128, 256, 512 };
            foreach (double levelUs in levelsUs)
            {
                int itemIters = Math.Max(1, (int)(levelUs * itersPerUs / items));
                double itemUs = itemIters / itersPerUs;

                double SerialArm()
                {
                    for (int i = 0; i < items; i++) part[i] = CostWork(itemIters);
                    double s = 0;
                    for (int i = 0; i < items; i++) s += part[i];   // 收尾定序累加：两臂对称，不进被测面
                    return s;
                }
                double ParallelArm()
                {
                    Parallel.For(0, items, i => part[i] = CostWork(itemIters));
                    double s = 0;
                    for (int i = 0; i < items; i++) s += part[i];
                    return s;
                }

                Warmup(() => { checksum += SerialArm(); });
                Warmup(() => { checksum += ParallelArm(); });

                double bestS = double.MaxValue, bestP = double.MaxValue;
                for (int r = 0; r < 9; r++)
                {
                    bestS = Math.Min(bestS, TimeArm(SerialArm));
                    bestP = Math.Min(bestP, TimeArm(ParallelArm));
                }
                Console.WriteLine($"    总工作量 {levelUs,5:N0} µs（{items} 件 × {itemUs,5:N1} µs）：串行 {bestS,7:N1} µs ｜ 并行 {bestP,7:N1} µs ｜ 并行/串行 {bestP / bestS,5:N2}");
            }
        }

        private static double CostWork(int iters)
        {
            double s = 0;
            for (int i = 0; i < iters; i++) s += Math.Sqrt(i * 0.5 + 1.0);
            return s;
        }

        /// <summary>测一臂：重复到窗口 ≥2 ms（小工作量也要有够长的窗，否则计时器分辨率与调度抖动会吃掉信号）。</summary>
        private static double TimeArm(Func<double> arm)
        {
            long t0 = Stopwatch.GetTimestamp();
            int k = 0;
            do
            {
                checksum += arm();
                k++;
            }
            while ((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency < 2000.0 && k < 100_000);
            long t1 = Stopwatch.GetTimestamp();
            return (t1 - t0) * 1e6 / Stopwatch.Frequency / k;
        }

        // ════════════════════════════════════════════════════
        //  5. 引力场 A/B：新实现（Span/ref + 内核内联）vs 旧实现（逐源结构体拷贝）
        //  ⚠ 旧实现是**逐字拷贝**改动前的 `NBodyIntegrator`（含私有核）——"改前/改后"只在
        //    同一进程同轮交错测才有意义（跨轮绝对数字漂移 ±40%，见 2026-10-14 仪器事故）。
        // ════════════════════════════════════════════════════

        private static void BenchGravityAB()
        {
            Console.WriteLine();
            Console.WriteLine("── 5. 引力场 A/B（同轮交错；先逐位相等断言，再计时）──");
            var sources = MakeSources();
            DVector3 pos = new DVector3(7.0e6, 0, 0);
            DVector3 vel = new DVector3(0, 7546, 0);
            var ignored = new List<int> { 2 };
            var atmMask = new List<int>();
            for (int i = 1; i < 11; i++) atmMask.Add(i);

            // 逐位相等断言（判据本身先自证：先证明"相等比较是活的"，再拿它判改动）
            SelfCheckGravityAssert();
            AssertSameAsLegacy(pos, vel, sources, null);
            AssertSameAsLegacy(pos, vel, sources, ignored);
            AssertSameAsLegacy(pos, vel, sources, atmMask);
            AssertSameAsLegacy(pos, vel, null!, null);            // 空源表
            AssertDifferentialSameAsLegacy(pos, vel, sources);
            Console.WriteLine("  逐位相等断言：无掩码/掩码1/掩码10/空源表/差分 —— 全过 ✓");

            BenchVariants(1_000_000, 9,
                ("A 旧 FieldAccelJerk（逐源结构体拷贝）", () => { LegacyFieldAccelJerk(pos, vel, sources, null, out var a, out var j); checksum += a.y + j.z; }),
                ("B 新 FieldAccelJerk（Span/ref readonly）", () => { NBodyIntegrator.FieldAccelJerk(pos, vel, sources, null, out var a, out var j); checksum += a.y + j.z; }),
                ("A 旧（掩码 1 项）", () => { LegacyFieldAccelJerk(pos, vel, sources, ignored, out var a, out var j); checksum += a.y + j.z; }),
                ("B 新（掩码 1 项）", () => { NBodyIntegrator.FieldAccelJerk(pos, vel, sources, ignored, out var a, out var j); checksum += a.y + j.z; }),
                ("A 旧 DifferentialAccelJerk", () => { LegacyDifferentialAccelJerk(pos, vel, new DVector3(120, 0, 0), new DVector3(0, 0.1, 0), sources, out var a, out var j); checksum += a.y + j.z; }),
                ("B 新 DifferentialAccelJerk", () => { NBodyIntegrator.DifferentialAccelJerk(pos, vel, new DVector3(120, 0, 0), new DVector3(0, 0.1, 0), sources, out var a, out var j); checksum += a.y + j.z; }));
        }

        private static void SelfCheckGravityAssert()
        {
            // "相等比较是活的"证明：给旧实现与新实现在**同一位置**比 ⇒ 必须相等；错 1 km ⇒ 必须不等。
            var sources = MakeSources();
            DVector3 pos = new DVector3(7.0e6, 0, 0), vel = new DVector3(0, 7546, 0);
            LegacyFieldAccelJerk(pos, vel, sources, null, out var a0, out var j0);
            NBodyIntegrator.FieldAccelJerk(pos, vel, sources, null, out var a1, out var j1);
            bool same = a0.x == a1.x && a0.y == a1.y && a0.z == a1.z && j0.x == j1.x && j0.y == j1.y && j0.z == j1.z;
            if (!same) throw new InvalidOperationException("断言自证失败：同一输入竟判不相等");
            NBodyIntegrator.FieldAccelJerk(pos + new DVector3(1e3, 0, 0), vel, sources, null, out var a2, out var j2);
            bool differ = a0.x != a2.x || a0.y != a2.y || a0.z != a2.z || j0.x != j2.x || j0.y != j2.y || j0.z != j2.z;
            if (!differ) throw new InvalidOperationException("断言自证失败：输入错 1 km 仍判相等 ⇒ 相等比较是假的");
            Console.WriteLine("  断言自证：同输入判相等 ✓ ｜ 错 1 km 判不等 ✓");
        }

        private static void AssertSameAsLegacy(DVector3 pos, DVector3 vel, List<GravitySource> sources, List<int>? mask)
        {
            LegacyFieldAccelJerk(pos, vel, sources, mask, out var a0, out var j0);
            NBodyIntegrator.FieldAccelJerk(pos, vel, sources, mask, out var a1, out var j1);
            if (a0.x != a1.x || a0.y != a1.y || a0.z != a1.z || j0.x != j1.x || j0.y != j1.y || j0.z != j1.z)
                throw new InvalidOperationException(
                    $"逐位相等断言失败（mask={(mask == null ? "null" : mask.Count.ToString())}）：旧 {a0}/{j0} ≠ 新 {a1}/{j1}");
        }

        private static void AssertDifferentialSameAsLegacy(DVector3 pos, DVector3 vel, List<GravitySource> sources)
        {
            DVector3 rp = new DVector3(120, 0, 0), rv = new DVector3(0, 0.1, 0);
            LegacyDifferentialAccelJerk(pos, vel, rp, rv, sources, out var a0, out var j0);
            NBodyIntegrator.DifferentialAccelJerk(pos, vel, rp, rv, sources, out var a1, out var j1);
            if (a0.x != a1.x || a0.y != a1.y || a0.z != a1.z || j0.x != j1.x || j0.y != j1.y || j0.z != j1.z)
                throw new InvalidOperationException($"差分逐位相等断言失败：旧 {a0}/{j0} ≠ 新 {a1}/{j1}");
        }

        // ── 以下是改动前的逐字旧实现（只做 A/B 参照，不进生产）──

        private static void LegacyFieldAccelJerk(DVector3 pos, DVector3 vel, List<GravitySource> sources,
            List<int>? ignoredBodyIds, out DVector3 acceleration, out DVector3 jerk)
        {
            double ax = 0.0, ay = 0.0, az = 0.0;
            double jx = 0.0, jy = 0.0, jz = 0.0;

            if (sources != null)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    if (ignoredBodyIds != null && LegacyContainsId(ignoredBodyIds, s.bodyId)) continue;
                    LegacyBodyAccelJerkCore(
                        pos.x, pos.y, pos.z, vel.x, vel.y, vel.z,
                        s.position.x, s.position.y, s.position.z,
                        s.velocity.x, s.velocity.y, s.velocity.z,
                        s.mu,
                        out double bx, out double by, out double bz,
                        out double bjx, out double bjy, out double bjz);
                    ax += bx; ay += by; az += bz;
                    jx += bjx; jy += bjy; jz += bjz;
                }
            }

            acceleration = new DVector3(ax, ay, az);
            jerk = new DVector3(jx, jy, jz);
        }

        private static void LegacyDifferentialAccelJerk(DVector3 pos, DVector3 vel, DVector3 refPos, DVector3 refVel,
            List<GravitySource> sources, out DVector3 acceleration, out DVector3 jerk)
        {
            double ax = 0.0, ay = 0.0, az = 0.0;
            double jx = 0.0, jy = 0.0, jz = 0.0;

            if (sources != null)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];

                    LegacyBodyAccelJerkCore(
                        pos.x, pos.y, pos.z, vel.x, vel.y, vel.z,
                        s.position.x, s.position.y, s.position.z,
                        s.velocity.x, s.velocity.y, s.velocity.z,
                        s.mu,
                        out double bx, out double by, out double bz,
                        out double bjx, out double bjy, out double bjz);

                    LegacyBodyAccelJerkCore(
                        refPos.x, refPos.y, refPos.z, refVel.x, refVel.y, refVel.z,
                        s.position.x, s.position.y, s.position.z,
                        s.velocity.x, s.velocity.y, s.velocity.z,
                        s.mu,
                        out double rx, out double ry, out double rz,
                        out double rjx, out double rjy, out double rjz);

                    ax += bx - rx; ay += by - ry; az += bz - rz;
                    jx += bjx - rjx; jy += bjy - rjy; jz += bjz - rjz;
                }
            }

            acceleration = new DVector3(ax, ay, az);
            jerk = new DVector3(jx, jy, jz);
        }

        private static bool LegacyContainsId(List<int> ids, int bodyId)
        {
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] == bodyId) return true;
            return false;
        }

        private static void LegacyBodyAccelJerkCore(
            double scX, double scY, double scZ,
            double scVx, double scVy, double scVz,
            double bX, double bY, double bZ,
            double bVx, double bVy, double bVz,
            double mu,
            out double ax, out double ay, out double az,
            out double jx, out double jy, out double jz)
        {
            double rx = bX - scX;
            double ry = bY - scY;
            double rz = bZ - scZ;
            double vx = bVx - scVx;
            double vy = bVy - scVy;
            double vz = bVz - scVz;

            double rSq = rx * rx + ry * ry + rz * rz;

            if (rSq < 0.01)
            {
                ax = ay = az = jx = jy = jz = 0.0;
                return;
            }

            double invR = 1.0 / Math.Sqrt(rSq);
            double invRSq = invR * invR;
            double invR3 = invRSq * invR;
            double muR3 = mu * invR3;

            ax = muR3 * rx;
            ay = muR3 * ry;
            az = muR3 * rz;

            double rDotV = rx * vx + ry * vy + rz * vz;
            double alpha = 3.0 * rDotV * invRSq;

            jx = muR3 * vx - alpha * ax;
            jy = muR3 * vy - alpha * ay;
            jz = muR3 * vz - alpha * az;
        }
    }
}
