using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;

internal static class CelestialBenchScenarios
{
    internal static void Run(string scenario, string output)
    {
        Directory.CreateDirectory(output);
        if (File.Exists(Path.Combine(output, "metrics.json"))) throw new IOException("Refusing to overwrite benchmark evidence.");
        var results = new List<object>();
        var assembly = typeof(CelestialRuntime).Assembly;
        string assemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        void Save() => File.WriteAllText(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(new {
            createdUtc = DateTime.UtcNow, runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), processors = Environment.ProcessorCount,
            samples = 1000, configuration = "Release; production CPU paths; no rendering or GPU", results,
            assemblyMvid = assembly.ManifestModule.ModuleVersionId, assemblySha256
        }, new JsonSerializerOptions { WriteIndented = true }));
        void Add(string name, int bodies, int ships, int maps, int count, Action action, Func<long>? save = null, Func<object>? retention = null)
        {
            for (int i = 0; i < 16; i++) action();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetTotalAllocatedBytes(true);
            int[] gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            long[] samples = new long[count];
            using (CelestialCostCounters.Measure())
            {
                for (int i = 0; i < count; i++) { long start = Stopwatch.GetTimestamp(); action(); samples[i] = Stopwatch.GetTimestamp() - start; }
                long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                var counters = CelestialCostCounters.Snapshot();
                Array.Sort(samples);
                double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
                results.Add(new { name, bodies, ships, maps, count, p50ms = Ms(samples[count / 2]), p95ms = Ms(samples[(int)(count * .95)]),
                    p99ms = Ms(samples[(int)(count * .99)]), maxMs = Ms(samples[^1]), totalMs = Ms(samples.Sum()), allocatedBytes = bytes,
                    collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray(), counters,
                    heapBytes = GC.GetTotalMemory(false), peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
                    retention = retention?.Invoke(), saveBytes = save?.Invoke() });
            }
            Save(); Console.WriteLine($"{name}: bodies={bodies}, ships={ships}, maps={maps}, samples={count}");
        }
        long SaveSize(CelestialRuntime runtime)
        {
            var checkpoint = new CelestialSaveAdapter(runtime).CaptureCheckpoint();
            CelestialBenchFactory.Require(checkpoint.Success, "Checkpoint failed.");
            var encoded = CelestialSaveCodec.Encode(checkpoint.Value);
            CelestialBenchFactory.Require(encoded.Success, "Save encode failed.");
            return encoded.Value.Length;
        }
        object Retained(CelestialRuntime runtime) => new { epochs = runtime.CaptureEpochUsage(), operations = runtime.Operations.Capture().Count,
            commands = runtime.Commands.Capture().Count };
        if (scenario is "all" or "bodies")
            foreach (int count in new[] { 3, 32, 256, 1024, 10000 })
            {
                var runtime = CelestialBenchFactory.Create(count, stateful: true, visuals: true);
                Add("advance", count, 0, 0, 1000, () => CelestialBenchFactory.Advance(runtime, runtime.CurrentStamp.PhysicalSeconds + 1), () => SaveSize(runtime));
                var adapter = new CelestialViewAdapter(runtime);
                var ids = Enumerable.Range(0, count).Select(CelestialBenchFactory.Id).ToArray();
                Add("query", count, 0, 0, 1000, () => { foreach (var id in ids) CelestialBenchFactory.Require(runtime.Query(id).Success, "Query failed."); });
                Add("presentation", count, 0, 0, 1000, () => {
                    foreach (var id in ids) CelestialBenchFactory.Require(adapter.CapturePresentation(id, runtime.CurrentStamp.PhysicalSeconds).Success, "Presentation failed.");
                });
                Add("presentation-batch", count, 0, 0, 1000, () => {
                    foreach (var result in adapter.CapturePresentationBatch(ids, runtime.CurrentStamp, runtime.CurrentStamp.PhysicalSeconds))
                        CelestialBenchFactory.Require(result.Success, "Batch presentation failed.");
                });
                Add("checkpoint", count, 0, 0, 1, () => SaveSize(runtime));
            }
        if (scenario is "all" or "ships")
            foreach (int ships in new[] { 0, 1, 32, 128 })
                foreach (int maps in new[] { 0, 4 })
                {
                    using var host = new CelestialShipBenchScope(ships, maps);
                    Add("ship-prepare-publish", 3, ships, maps, 1000, host.Advance, () => SaveSize(host.Runtime));
                }
        if (scenario is "all" or "commands")
        {
            var runtime = CelestialBenchFactory.Create(3, stateful: true, commands: true); int index = 0; long epoch = 0;
            Add("command-lifetime", 3, 0, 0, 100000, () => {
                if (index > 0 && index % 4000 == 0)
                {
                    CelestialBenchFactory.Require(runtime.CloseEpoch(runtime.WorldSessionId, epoch).Success, "Epoch close rejected.");
                    var opened = runtime.OpenEpoch(runtime.WorldSessionId); CelestialBenchFactory.Require(opened.Success, "Epoch open rejected."); epoch = opened.Value;
                }
                var result = runtime.Submit(new("bench.increment", CelestialOperationEpochs.Qualify(epoch, "bench/command/" + index++), CelestialBenchFactory.Id(0),
                    runtime.WorldSessionId, 1));
                CelestialBenchFactory.Require(result.Success, "Command rejected.");
                CelestialBenchFactory.Advance(runtime, runtime.CurrentStamp.PhysicalSeconds + 1);
            }, () => SaveSize(runtime), () => Retained(runtime));
        }
        Save();
    }
}
