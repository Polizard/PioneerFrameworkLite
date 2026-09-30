using System;
using System.Diagnostics;
using MechJebLib.Primitives;
using PioneerFramework.Pure.Tests;      // 台架（CircularTarget）
using Xunit;
using Xunit.Abstractions;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// 批7 #2 的**天花板测量**（2026-09-30）：k 个编队**同一拍**重规划时，"串行 Σ(k)" vs "并行 max(k)"。
    ///
    /// ## 为什么先量
    /// §17.4 把 #2 排在最值的位置，理由写的是"直接削掉那个实测 19.2 ms 的引导尖峰"——那是 2026-09-26 的读数，
    /// **而 #1（多起点并行）已经把重活（霍曼搜索 9–21 ms）削了 4×**。所以"还值不值得动那段最深的引导代码"
    /// 必须**先量再说**（本库纪律：B 组先量再改；且"凡'这会弄红很多测试'必须先数出具体条数"同族）。
    ///
    /// ## 量的是什么
    /// 造 k 组**几何各不相同**的 (自船, 目标) 对（避免"同一份输入 ⇒ JIT/缓存让串行也不慢"的假象），
    /// 每组各规划一次：① 串行（Σ）② 用 `ParallelOps` 并行（≈max）。**只打读数、不断言时间**。
    /// ⚠ 它量的是**天花板**：真接线还要加"快照 → 校验输入 → 串行提交"的成本，所以真实收益 ≤ 这里的读数。
    /// </summary>
    public class ParallelReplanCeilingTests
    {
        private readonly ITestOutputHelper _out;
        public ParallelReplanCeilingTests(ITestOutputHelper o) => _out = o;

        private const double Mu = 2.0e14;
        private const double R0 = 7_000_000.0;
        private const double BodyRadius = 5.0e6;
        private static readonly DVector3 OrbitNormal = new DVector3(0, -1, 0);

        [Fact]
        public void SerialSum_Vs_ParallelMax_ForKReplans()
        {
            const int K = 8;                       // 8 个编队同拍重规划
            var selfs = new OrbitView[K];
            var goals = new OrbitView[K];
            var pars = new RendezvousLadder.Params[K];

            for (int i = 0; i < K; i++)
            {
                double vc = Math.Sqrt(Mu / R0);
                selfs[i] = OrbitView.FromStateVectors(new DVector3(R0, 0, 0), new DVector3(0, 0, vc), Mu, 0.0,
                    OrbitNormal, BodyRadius);
                var tgt = CircularTarget.Create(R0, Mu, (30.0 + 7.0 * i) * Math.PI / 180.0, OrbitNormal);
                goals[i] = OrbitView.FromStateVectors(tgt.PosAt(0.0), tgt.VelAt(0.0), Mu, 0.0,
                    tgt.PlaneNormalAt(0.0), BodyRadius);
                pars[i] = new RendezvousLadder.Params { DesiredDistance = 283.0, MinRadius = 5.1e6 };
            }

            // 预热（JIT + 首调用）
            for (int i = 0; i < K; i++)
                RendezvousLadder.MakePlan(selfs[i], goals[i], 0.0, pars[i], out _, out _);

            // ① 串行：Σ(k)
            const int Reps = 20;
            var sw = Stopwatch.StartNew();
            int sink = 0;
            for (int r = 0; r < Reps; r++)
                for (int i = 0; i < K; i++)
                {
                    RendezvousLadder.MakePlan(selfs[i], goals[i], r * 10.0, pars[i], out var nodes, out _);
                    sink += nodes.Count;
                }
            sw.Stop();
            double msSerial = sw.Elapsed.TotalMilliseconds / Reps;

            // ② 并行：≈max(k)（每组写自己的槽位；归约不用 —— 这里只要耗时）
            var nodeCounts = new int[K * Reps];
            sw.Restart();
            ParallelOps.For(K * Reps, 2e-3, k =>
            {
                int i = k % K, r = k / K;
                RendezvousLadder.MakePlan(selfs[i], goals[i], r * 10.0, pars[i], out var nodes, out _);
                nodeCounts[k] = nodes.Count;
            });
            sw.Stop();
            double msParallel = sw.Elapsed.TotalMilliseconds / Reps;

            _out.WriteLine(string.Format(
                "\n[批7 #2 天花板] {0} 核 · {1} 组同拍重规划：串行 Σ = {2:F2} ms / 并行 ≈max = {3:F2} ms（{4:F2}×）"
                + "\n⇒ 真接线还要加「快照 + 校验输入 + 串行提交」，**真实收益 ≤ 这个比值**（sink={5} 防优化掉）",
                Environment.ProcessorCount, K, msSerial, msParallel, msSerial / Math.Max(msParallel, 1e-9), sink));
        }
    }
}
