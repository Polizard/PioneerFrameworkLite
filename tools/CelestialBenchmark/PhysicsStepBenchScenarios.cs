// 物理步台架（2026-11-18 新增）：`SpaceGameComp.AdvancePhysics` 的单步成本。
//
// 为什么需要它：现有台架只测天体 runtime 的 advance / ship prepare-publish，
// 真正每 tick 跑的物理步宿主（操舵 + 冲量 + 群组物理 + 引导）从未被单独计时。
// 本场景用与测试相同的 headless 世界壳（未初始化 Game + 一个天体 + N 个群组），
// 只跑 runtime==null 的分支（即纯船步），测 p50/p95 与分配。
//
// ⚠ 不含：天体区间提交（需要真实 CelestialRuntime）、渲染、存档。
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PioneerFramework;
using PioneerFramework.Celestial;
using Verse;

internal static class PhysicsStepBenchScenarios
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, "metrics.json");
        if (File.Exists(path)) throw new IOException("Refusing to overwrite benchmark evidence.");

        var results = new List<object>();
        foreach (int groups in new[] { 0, 32, 128 })
            results.Add(Measure(groups, 1000));

        File.WriteAllText(path, JsonSerializer.Serialize(new {
            createdUtc = DateTime.UtcNow, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), processors = Environment.ProcessorCount,
            gitCommit = EphemerisBenchScenarios.Commit(), scenario = "step",
            note = "SpaceGameComp.AdvancePhysics, runtime==null branch (ship step only)", results
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in results) Console.WriteLine(JsonSerializer.Serialize(result));
    }

    private static object Measure(int groupCount, int samples)
    {
        var previous = Current.Game;
        try
        {
            var game = (Game)RuntimeHelpers.GetUninitializedObject(typeof(Game));
            SetField(typeof(Game), "tickManager", game, RuntimeHelpers.GetUninitializedObject(typeof(TickManager)));
            var space = new SpaceGameComp(game);
            var bodies = new CelestialBodyManager(game);
            SetField(typeof(CelestialBodyManager), "initialized", bodies, true);
            SetField(typeof(CelestialBodyManager), "bodiesById", bodies,
                new Dictionary<int, CelestialBodyInfo> { [0] = new CelestialBodyInfo { bodyId = 0, bodyType = CelestialBodyType.Planet } });
            game.components = new List<GameComponent> { space, bodies };
            Current.Game = game;
            SetField(typeof(TickManager), "ticksGameInt", Find.TickManager, 100);
            SetField(typeof(TickManager), "curTimeSpeed", Find.TickManager, TimeSpeed.Normal);
            var clock = new TimeScaleManager(game);
            game.components.Add(clock);
            RealTime.frameCount = 10000;
            RealTime.deltaTime = 1f / 240f;
            clock.GetPhysicsTick();

            for (int index = 0; index < groupCount; index++)
            {
                var ship = new SpaceEntityGroup(100 + index, 2)
                {
                    CelestialBodyId = 0, IsActive = true, IsMovable = true, exemptPlanetCollision = true,
                    CurrentOrbit = new()
                    {
                        positionSystem = (SystemVec3)new DVector3(1e7 + index * 1000, 0, 0),
                        velocitySystem = (SystemVec3)new DVector3(0, 0, 100), rotation = DQuaternion.identity
                    }
                };
                space.RegisterGroup(ship);
            }

            int tick = 200;
            for (int i = 0; i < 32; i++) space.AdvancePhysics(tick++);   // 预热
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetTotalAllocatedBytes(true);
            var samplesArray = new double[samples];
            for (int i = 0; i < samples; i++)
            {
                long start = Stopwatch.GetTimestamp();
                space.AdvancePhysics(tick++);
                samplesArray[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            }
            long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            Array.Sort(samplesArray);
            double At(double q) => samplesArray[Math.Min(samples - 1, (int)(samples * q))];
            return new { name = "advance-physics", groups = groupCount, samples, p50ms = At(0.5), p95ms = At(0.95), p99ms = At(0.99),
                maxMs = samplesArray[^1], allocatedBytes = bytes };
        }
        catch (Exception exception)
        {
            return new { name = "advance-physics", groups = groupCount, failed = exception.GetType().Name + ": " + exception.Message };
        }
        finally
        {
            Current.Game = previous;
        }
    }

    private static void SetField(Type type, string name, object target, object value)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(type.Name + "." + name + " not found");
        field.SetValue(target, value);
    }
}
