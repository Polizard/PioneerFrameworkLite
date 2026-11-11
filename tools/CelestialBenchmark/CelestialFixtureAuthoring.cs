using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PioneerFramework.Celestial;
using PioneerFramework.TestSupport.Celestial;

internal static class CelestialFixtureAuthoring
{
    internal static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFiles(directory).Any()) throw new InvalidOperationException("Refusing to overwrite frozen fixtures.");
        var ready = new CelestialSaveAdapter(CelestialBenchFactory.Create(3)).CaptureCheckpoint().Value;
        var pending = new CelestialSaveAdapter(CelestialBenchFactory.Create(2, pending: true)).CaptureCheckpoint().Value;
        var body = ready.Bodies[0];
        var unknown = body with {
            Descriptors = body.Descriptors.Append(new CelestialCompDescriptor("unknown", "fixture.missing", 1)).ToArray(),
            Components = body.Components.Append(new CelestialCompSaveRecord("unknown", "fixture.missing", 1, 1,
                new byte[] { 0, 255, 7 }, new byte[] { 31, 0, 0, 72 })).ToArray() };
        var surface = new CelestialSavedSurface(body.Description.Id, 3, body.Description.RadiusMeters, 100, 0, 123,
            CelestialGenerationConfiguration.Encode(body.Components), CelestialSurfaceState.Ready);
        var retired = ready with { Surfaces = new[] { surface },
            Operations = ready.Operations.Append(new CelestialOperationRecord("fixture/map", new CelestialOperation("fixture/native-map",
                CelestialOperationStatus.Applied, new[] { body.Description.Id }, appliedRevision: ready.CatalogRevision))).ToArray(),
            Maps = new[] { new CelestialMapSiteRecord(new(body.Description.Id, 3, 8, 81), 81, null) {
                ParentRetired = true, RetiredOperationIds = new[] { "fixture/native-map" } } } };
        var documents = new Dictionary<string, CelestialSaveDocument> { ["ready.json"] = ready, ["pending.json"] = pending,
            ["unknown.json"] = ready with { Bodies = new[] { unknown }.Concat(ready.Bodies.Skip(1)).ToArray() }, ["retired-map.json"] = retired };
        foreach (var pair in documents)
        {
            var result = CelestialSaveCodec.Encode(pair.Value);
            if (!result.Success) throw new InvalidOperationException(pair.Key + ": " + string.Join(",", result.Errors.Select(error => error.Code)));
            File.WriteAllBytes(Path.Combine(directory, pair.Key), result.Value.ToArray());
        }
        File.WriteAllText(Path.Combine(directory, "malformed.json"), "{\"formatVersion\":" + CelestialSaveDocument.CurrentVersion + ",", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "unsupported.json"), "{\"formatVersion\":99}", new UTF8Encoding(false));
        var manifest = Directory.GetFiles(directory, "*.json").Order().ToDictionary(file => Path.GetFileName(file)!,
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Exported six frozen fixtures and SHA manifest: " + directory);
    }
}
