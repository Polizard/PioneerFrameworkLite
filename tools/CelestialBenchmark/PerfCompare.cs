// 台架对比 / 回归闸门（2026-11-18 新增）。
//
// 用 `--scenario compare --a <基线> --b <本次> [--tolerance 0.15]`：
//   · a / b 可以是 metrics.json 文件，也可以是包含多个 metrics.json 的目录；
//   · 按 `name + bodies/groups/ships/maps` 配对，逐项比 p50ms / p95ms / allocatedBytes / nsPerOp；
//   · 任一指标涨幅超过容差 ⇒ 退出码 1（可直接进闸门）。
//
// 机器不同（CPU 不同）时只告警不判红：性能基线天然与机器绑定，跨机比较没有意义。
using System.Text.Json;

internal static class PerfCompare
{
    internal static int Run(string a, string b, double tolerance)
    {
        var baseline = Load(a);
        var current = Load(b);
        if (baseline.Count == 0) { Console.WriteLine("baseline has no metrics"); return 1; }
        if (current.Count == 0) { Console.WriteLine("current has no metrics"); return 1; }

        // 2026-11-18：容差按场景分级。大 N 的 bodies/ships 受 GC 与分配影响，
        // 同提交同机器的两次跑也会差 20–50%（实测），用 15% 当闸门只会天天假红；
        // 真正要守的是 step / ephemeris / frames 这类低噪声路径。
        // 2026-11-18 实测：bodies/ships 在**同提交同机器**上两次跑能差到 126%（presentation-batch 256），
        // 因为大 N 场景被 GC 支配。给它们设任何阈值都只会制造假红 ⇒ 判为**仅供参考**（infinity 容差）。
        // 真正的闸门 = step / ephemeris / frames 这三条低噪声路径。
        // 2026-11-18 二次实测：连 ns 级的 ephemeris/frames 也会受机器状态影响（同提交 depth4 两次差 181%），
        // 所以闸门只留**唯一稳定且真正代表后端**的指标：step 的 p50 与分配（32/128 群组）。
        // 其余一律「参考」——照常打印，不判红。
        static double DefaultTolerance(string key) =>
            key.StartsWith("step/", StringComparison.Ordinal) && !key.Contains("[groups=0]", StringComparison.Ordinal)
                ? 0.15 : double.PositiveInfinity;

        bool differentMachine = baseline.Values.Select(m => m.Cpu).Distinct().Count() > 0 &&
                                current.Values.Select(m => m.Cpu).Distinct().Count() > 0 &&
                                !baseline.Values.Select(m => m.Cpu).First().Equals(current.Values.Select(m => m.Cpu).First(), StringComparison.Ordinal);
        if (differentMachine) Console.WriteLine("WARN: baseline 与本次 CPU 不同，数值不可直接比较（仅作参考）");

        var failures = new List<string>();
        var missing = baseline.Keys.Where(key => !current.ContainsKey(key)).ToArray();
        foreach (string key in missing) Console.WriteLine($"MISSING {key}（本次没有产出）");

        Console.WriteLine();
        Console.WriteLine($"{"metric",-58} {"base",12} {"now",12} {"delta",9}  verdict");
        foreach (var (key, expected) in baseline.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!current.TryGetValue(key, out var actual)) continue;
            foreach (string field in new[] { "p50ms", "p95ms", "allocatedBytes", "nsPerOp" })
            {
                if (!expected.Values.TryGetValue(field, out double before) || !actual.Values.TryGetValue(field, out double now)) continue;
                if (before <= 0) continue;
                double limit = tolerance > 0 ? tolerance : DefaultTolerance(key);
                // p95 是 GC 尾巴：step[128] 两次跑能差 ±20%，p50 只差 ±10%。尾部不设闸门，只看 p50 与分配。
                bool informational = double.IsInfinity(limit) ||
                    (field == "p95ms" && key.StartsWith("step/", StringComparison.Ordinal));
                double delta = (now - before) / before;
                bool bad = delta > limit && !differentMachine;
                string verdict = informational ? "info" : bad ? "RED" : delta > limit ? "warn" : "ok";
                Console.WriteLine($"{key + "." + field,-58} {before,12:F4} {now,12:F4} {delta,8:P1}  {verdict,-5} " +
                    (informational ? "(参考)" : $"(limit {limit:P0})"));
                if (bad && !informational) failures.Add($"{key}.{field} +{delta:P0}");
            }
        }

        Console.WriteLine();
        if (failures.Count > 0)
        {
            Console.WriteLine("REGRESSION: " + string.Join(" | ", failures));
            return 1;
        }
        Console.WriteLine("OK: 无超过容差的回退");
        return 0;
    }

    private sealed record Metric(string Cpu, Dictionary<string, double> Values);

    private static Dictionary<string, Metric> Load(string path)
    {
        var metrics = new Dictionary<string, Metric>(StringComparer.Ordinal);
        IEnumerable<string> files = File.Exists(path)
            ? new[] { path }
            : Directory.Exists(path) ? Directory.GetFiles(path, "metrics.json", SearchOption.AllDirectories) : Array.Empty<string>();
        foreach (string file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            string scenario = root.TryGetProperty("scenario", out var scenarioValue) ? scenarioValue.GetString() ?? "" :
                Path.GetFileName(Path.GetDirectoryName(file) ?? "");
            string cpu = root.TryGetProperty("cpu", out var cpuValue) ? cpuValue.GetString() ?? "" : "";
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;
            foreach (var result in results.EnumerateArray())
            {
                string name = result.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "" : "";
                string key = scenario + "/" + name;
                foreach (string suffix in new[] { "bodies", "groups", "ships", "maps" })
                    if (result.TryGetProperty(suffix, out var dimension)) key += "[" + suffix + "=" + dimension.GetRawText() + "]";
                var values = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (string field in new[] { "p50ms", "p95ms", "allocatedBytes", "nsPerOp" })
                    if (result.TryGetProperty(field, out var value) && value.TryGetDouble(out double number)) values[field] = number;
                metrics[key] = new Metric(cpu, values);
            }
        }
        return metrics;
    }
}
