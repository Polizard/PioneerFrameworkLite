// 帧系搬运台架（2026-11-18 新增）：`FrameTransitionCore.Offset` 在不同父链深度下的单次成本。
//
// 为什么需要它：跨 SOI 换系、跨天体状态搬运都走这条纯算术路径，但从未被单独测量。
// 换系本身的成本 ≈ Offset（两条父链求和 + LCA 查找）；深度 = 2/4/8 对应真实层级。
using System.Diagnostics;
using System.Text.Json;
using PioneerFramework;
using PioneerFramework.Celestial;

internal static class FrameBenchScenarios
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, "metrics.json");
        if (File.Exists(path)) throw new IOException("Refusing to overwrite benchmark evidence.");

        var results = new List<object>();
        foreach (int depth in new[] { 2, 4, 8 })
        {
            var left = Chain(depth, 1000);
            var right = Chain(depth, 2000);
            results.Add(MeasureOffset("offset-depth" + depth, 2_000_000, left, right));
        }
        results.Add(MeasureLca("lca-depth8", 2_000_000, Chain(8, 1000), Chain(8, 2000)));

        File.WriteAllText(path, JsonSerializer.Serialize(new {
            createdUtc = DateTime.UtcNow, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), processors = Environment.ProcessorCount,
            gitCommit = EphemerisBenchScenarios.Commit(), scenario = "frames", results
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in results) Console.WriteLine(JsonSerializer.Serialize(result));
    }

    private static object MeasureOffset(string name, int samples, FrameNode[] left, FrameNode[] right)
    {
        // 两趟取较快者：首趟会吃分层编译/ PGO 提升，单趟读数会系统性偏高。
        double best = double.MaxValue, bestTotal = 0; double acc = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            acc = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < samples; i++)
            {
                var (position, velocity) = FrameTransitionCore.Offset(left, right);
                acc += position.x + velocity.y;
            }
            sw.Stop();
            double ns = sw.Elapsed.TotalMilliseconds * 1e6 / samples;
            if (ns < best) { best = ns; bestTotal = sw.Elapsed.TotalMilliseconds; }
        }
        return new { name, samples, nsPerOp = best, totalMs = bestTotal, acc };
    }

    private static object MeasureLca(string name, int samples, FrameNode[] left, FrameNode[] right)
    {
        for (int i = 0; i < samples / 20; i++) FrameTransitionCore.FindLca(left, right);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long acc = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < samples; i++) acc += FrameTransitionCore.FindLca(left, right);
        sw.Stop();
        return new { name, samples, nsPerOp = sw.Elapsed.TotalMilliseconds * 1e6 / samples, totalMs = sw.Elapsed.TotalMilliseconds, acc };
    }

    /// <summary>造一条 depth 层的父链：最后一个是共同根（BodyId=0, ParentId=-1）。</summary>
    private static FrameNode[] Chain(int depth, int seed)
    {
        var nodes = new FrameNode[depth];
        for (int i = 0; i < depth; i++)
        {
            bool root = i == depth - 1;
            int bodyId = root ? 0 : seed + i;
            int parentId = root ? -1 : (i + 1 == depth - 1 ? 0 : seed + i + 1);
            nodes[i] = new FrameNode(bodyId, parentId,
                new DVector3(1e9 + seed + i, 2e9 + seed, 3e8 + i), new DVector3(10.0 + i, 20.0, 30.0));
        }
        return nodes;
    }
}
