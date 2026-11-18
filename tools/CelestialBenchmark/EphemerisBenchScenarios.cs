// 星历采样台架（2026-11-18 新增）：同一场景下 circular vs elliptical 的真实成本。
//
// 为什么需要它：2026-11-13/17 两次合并把「程序化星系」接进来，生成天体全部走
// `existing.elliptical.v1`（开普勒迭代），而原有 `CelestialBenchFactory` 只造
// `existing.circular.v1` —— 台架从那一刻起就测不到新路径。本场景直接用**正式类**
// （`ExistingEphemerisParameters.Parse` + `SampleParent`）跑两种模式，给出真实倍数。
//
// 口径：单次 `SampleParent(seconds)` 的算法成本（含解析结果复用，不含 provider/快照/字典缓存）。
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PioneerFramework.Celestial;

internal static class EphemerisBenchScenarios
{
    private const double OrbitRadius = 1.5e11;
    private const double OrbitPeriod = 3.15e7;

    internal static void Run(string output, string mode)
    {
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, "metrics.json");
        if (File.Exists(path)) throw new IOException("Refusing to overwrite benchmark evidence.");

        var results = new List<object>();
        const int samples = 3_000_000;
        results.Add(Measure("circular", Circular(), samples));
        foreach (double eccentricity in new[] { 0.01, 0.05, 0.2 })
            results.Add(Measure("elliptical-e" + eccentricity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Elliptical(eccentricity), samples));

        File.WriteAllText(path, JsonSerializer.Serialize(new {
            createdUtc = DateTime.UtcNow, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), processors = Environment.ProcessorCount,
            gitCommit = Commit(), scenario = "ephemeris", samples, results
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in results) Console.WriteLine(JsonSerializer.Serialize(result));
    }

    private static object Measure(string name, ExistingEphemerisParameters parameters, int samples)
    {
        // 两趟取较快者：首趟会吃分层编译 / PGO 提升（实测能差一倍）。
        double best = double.MaxValue, bestTotal = 0, acc = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < samples / 20; i++) parameters.SampleParent(i * 0.01);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            acc = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < samples; i++)
            {
                var (position, velocity) = parameters.SampleParent(i * 0.01);
                acc += position.x + velocity.y;
            }
            sw.Stop();
            double ns = sw.Elapsed.TotalMilliseconds * 1e6 / samples;
            if (ns < best) { best = ns; bestTotal = sw.Elapsed.TotalMilliseconds; }
        }
        return new { name, samples, nsPerOp = best, totalMs = bestTotal, acc };
    }

    private static ExistingEphemerisParameters Circular()
    {
        const string json = """
            {"orbitRadius":150000000000,"orbitPeriod":31500000,"initialPhase":0.7,
             "orbitNormal":[0,1,0],"dayLengthTicks":60000,"bodyType":"Planet","frameOriginReference":"frameOrigin"}
            """;
        return Parse("existing.circular.v1", json);
    }

    private static ExistingEphemerisParameters Elliptical(double eccentricity)
    {
        string json =
            "{\"Motion\":{\"OrbitRadiusMeters\":150000000000,\"OrbitPeriodSeconds\":31500000,\"OrbitPhaseRadians\":0.7," +
            "\"OrbitNormal\":[0,1,0],\"BodyType\":\"Planet\",\"FrameOriginReference\":\"frameOrigin\"," +
            "\"RotationAxis\":[0,1,0],\"RotationPeriodSeconds\":60000,\"RotationPhaseRadians\":0," +
            "\"Retrograde\":false,\"SynchronousTargetReference\":\"\"}," +
            "\"Eccentricity\":" + eccentricity.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," +
            "\"EpochSeconds\":0,\"Periapsis\":[150000000000,0,0],\"OrbitRetrograde\":false}";
        return Parse("existing.elliptical.v1", json);
    }

    private static ExistingEphemerisParameters Parse(string mode, string json)
    {
        var initialization = new PhysicsInitialization(ExistingCelestialEphemerisBridge.Id, mode, 1, Encoding.UTF8.GetBytes(json));
        var parsed = ExistingEphemerisParameters.Parse(initialization);
        if (!parsed.Success) throw new InvalidOperationException(mode + ": " + string.Join(",", parsed.Errors.Select(error => error.Code + " " + error.Detail)));
        return parsed.Value;
    }

    internal static string Commit()
    {
        try
        {
            var start = new ProcessStartInfo("git", "rev-parse HEAD") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(start)!;
            string text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return text;
        }
        catch { return ""; }
    }
}
