using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

string Value(string key, string fallback)
{
    int index = Array.IndexOf(args, key);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
string scenario = Value("--scenario", "all");
if (scenario == "identity")
{
    string path = Path.GetFullPath(Value("--dll", "1.7/Assemblies/PioneerFrameworkLite.dll"));
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var result = new { path, bytes = stream.Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
        mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid) };
    using var destination = new FileStream(Value("--output", ".zcode/celestial-remediation/dll-identity.json"), FileMode.CreateNew);
    System.Text.Json.JsonSerializer.Serialize(destination, result);
    return;
}
if (scenario == "schema")
{
    var types = new[] { typeof(PioneerFramework.SpaceGameComp), typeof(PioneerFramework.SpaceEntityGroup), typeof(PioneerFramework.SpaceEntity),
        typeof(PioneerFramework.SpacePart), typeof(PioneerFramework.PropulsionData), typeof(PioneerFramework.SpaceMapComp) };
    var fields = types.Select(type => new { type = type.Name, fields = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly).Select(field => new { field.Name, field.IsInitOnly }) });
    File.WriteAllText(Value("--output", ".zcode/celestial-remediation/ship-input-schema.json"), System.Text.Json.JsonSerializer.Serialize(fields, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (scenario == "fixtures")
{
    CelestialFixtureAuthoring.Export(Path.GetFullPath(Value("--output", ".zcode/celestial-remediation/fixture-export")));
    return;
}
if (scenario == "ephemeris")
{
    // 2026-11-18：circular vs elliptical 的星历采样成本（合并后生成天体全走椭圆）。
    EphemerisBenchScenarios.Run(Path.GetFullPath(Value("--output", ".zcode/celestial-remediation/benchmark-ephemeris")), scenario);
    return;
}
if (scenario == "frames")
{
    // 2026-11-18：换系/跨帧搬运的纯算术成本（FrameTransitionCore.Offset）。
    FrameBenchScenarios.Run(Path.GetFullPath(Value("--output", ".zcode/celestial-remediation/benchmark-frames")));
    return;
}
if (scenario == "step")
{
    // 2026-11-18：SpaceGameComp.AdvancePhysics 单步成本（headless，runtime==null 分支）。
    PhysicsStepBenchScenarios.Run(Path.GetFullPath(Value("--output", ".zcode/celestial-remediation/benchmark-step")));
    return;
}
if (scenario == "compare")
{
    // 2026-11-18：基线对比闸门。--a/--b 可以是 metrics.json 或目录；超过容差退出码 1。
    double tolerance = double.TryParse(Value("--tolerance", "0"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
    Environment.ExitCode = PerfCompare.Run(Value("--a", ""), Value("--b", ""), tolerance);
    return;
}
CelestialBenchScenarios.Run(scenario, Path.GetFullPath(Value("--output", ".zcode/celestial-remediation/benchmark")));
