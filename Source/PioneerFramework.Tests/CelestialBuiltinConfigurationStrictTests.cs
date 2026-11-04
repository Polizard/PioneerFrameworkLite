using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

public class CelestialBuiltinConfigurationStrictTests
{
    [Theory]
    [InlineData("air")]
    [InlineData("light")]
    [InlineData("environment")]
    [InlineData("map")]
    [InlineData("biomes")]
    [InlineData("visual")]
    public void KnownBuiltinVersionRejectsMissingTypoAndDuplicateFieldsAndRetainsOriginalBytes(string kind)
    {
        ICelestialResolvedProperties properties = kind switch {
            "air" => new AtmosphereResolvedProperties("probe", 1, 2, 3),
            "light" => new LightSourceResolvedProperties("probe", new(1, 1, 1, 1)),
            "environment" => new ExistingEnvironmentResolvedProperties("probe", new("env", 60000, "sun", Array.Empty<string>())),
            "map" => new MapGenerationResolvedProperties("probe", new("map", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<CelestialMapBiomeProfile>())),
            "biomes" => new BiomeGenerationResolvedProperties("probe", new("biome", CelestialBiomeGenerationMode.VanillaSurface, 10, 0, 0, new[] { "Terrain" }, Array.Empty<string>())),
            _ => new VisualResolvedProperties("probe", CelestialVisualKind.Surface, new("visual", 1, 1, 1, 1, null, null, 0, 1, 60000, 60000)) };
        string raw = Encoding.UTF8.GetString(properties.Serialize().Span);
        var first = JsonNode.Parse(raw)!.AsObject().First();
        foreach (string fault in new[] { "missing", "typo", "duplicate" })
        {
            string corrupted;
            if (fault == "duplicate") corrupted = raw.Insert(1, "\"" + first.Key + "\":" + first.Value!.ToJsonString() + ",");
            else { var node = JsonNode.Parse(raw)!.AsObject(); node.Remove(first.Key); if (fault == "typo") node[first.Key + "Typo"] = first.Value!.DeepClone(); corrupted = node.ToJsonString(); }
            var bytes = Encoding.UTF8.GetBytes(corrupted);
            var record = new CelestialCompSaveRecord("probe", properties.HandlerId, 1, 1, bytes, ReadOnlySpan<byte>.Empty);
            var restored = CelestialCompStateCodec.Decode(record, CelestialBuiltinCompRegistry.Create()).Value;
            Assert.False(restored.IsReady, kind + ":" + fault);
            Assert.Equal(bytes, restored.OriginalRecord.ConfigurationPayload.ToArray());
        }
    }
}
