using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace PioneerFramework.MapGeneration
{
    public static class LocalMapGenerationCodec
    {
        public const int CurrentVersion = 1;
        public static ReadOnlyMemory<byte> Encode(LocalMapGenerationSpec spec)
        {
            ArgumentNullException.ThrowIfNull(spec);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", CurrentVersion);
                writer.WritePropertyName("recipe"); WriteRecipe(writer, spec.Recipe);
                writer.WriteStartArray("purposeRecipes");
                foreach (var pair in spec.PurposeRecipes.OrderBy(pair => pair.Key))
                {
                    writer.WriteStartObject(); writer.WriteNumber("purpose", (int)pair.Key);
                    writer.WritePropertyName("recipe"); WriteRecipe(writer, pair.Value); writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteString("nativeGeneratorId", spec.NativeGeneratorId);
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }
        public static LocalMapGenerationSpec Decode(ReadOnlyMemory<byte> bytes)
        {
            using var document = JsonDocument.Parse(bytes);
            var root = Fields(document.RootElement, "formatVersion", "recipe", "purposeRecipes", "nativeGeneratorId");
            if (root["formatVersion"].GetInt32() != CurrentVersion) throw new InvalidDataException("Unsupported local map recipe version.");
            var purposes = new Dictionary<MapGenerationPurpose, LocalMapRecipe>();
            foreach (var item in root["purposeRecipes"].EnumerateArray())
            {
                var fields = Fields(item, "purpose", "recipe");
                purposes.Add((MapGenerationPurpose)fields["purpose"].GetInt32(), ReadRecipe(fields["recipe"]));
            }
            return new(ReadRecipe(root["recipe"]), purposes, root["nativeGeneratorId"].GetString());
        }
        public static string SnapshotHash(LocalMapGenerationSpec spec) => Convert.ToHexString(SHA256.HashData(Encode(spec).Span));
        private static void WriteRecipe(Utf8JsonWriter writer, LocalMapRecipe recipe)
        {
            writer.WriteStartObject(); writer.WriteString("id", recipe.Id); writer.WriteStartArray("steps");
            foreach (var step in recipe.Steps)
            {
                writer.WriteStartObject(); writer.WriteString("key", step.Key); writer.WriteString("handlerId", step.HandlerId);
                writer.WriteNumber("configurationVersion", step.ConfigurationVersion); writer.WriteNumber("algorithmVersion", step.AlgorithmVersion);
                writer.WriteNumber("stage", (int)step.Stage); writer.WriteNumber("domain", (int)step.Domain);
                writer.WriteBase64String("configuration", step.Configuration.Span);
                WriteKeys(writer, "before", step.Before); WriteKeys(writer, "after", step.After);
                WritePorts(writer, "inputs", step.Inputs); WritePorts(writer, "outputs", step.Outputs);
                if (step.NativeOrder.HasValue) writer.WriteNumber("nativeOrder", step.NativeOrder.Value); else writer.WriteNull("nativeOrder");
                writer.WriteBoolean("isOpaque", step.IsOpaque); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        private static void WriteKeys(Utf8JsonWriter writer, string name, IEnumerable<string> keys)
        {
            writer.WriteStartArray(name);
            foreach (var key in keys.OrderBy(key => key, StringComparer.Ordinal)) writer.WriteStringValue(key);
            writer.WriteEndArray();
        }
        private static void WritePorts(Utf8JsonWriter writer, string name, IEnumerable<MapGenerationPort> ports)
        {
            writer.WriteStartArray(name);
            foreach (var port in ports.OrderBy(port => port.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("name", port.Name); writer.WriteString("key", port.Key);
                writer.WriteString("typeId", port.TypeId); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        private static LocalMapRecipe ReadRecipe(JsonElement element)
        {
            var recipe = Fields(element, "id", "steps");
            var steps = new List<MapGenerationStepSpec>();
            foreach (var item in recipe["steps"].EnumerateArray())
            {
                var fields = Fields(item, "key", "handlerId", "configurationVersion", "algorithmVersion", "stage", "domain", "configuration", "before", "after", "inputs", "outputs", "nativeOrder", "isOpaque");
                steps.Add(new(fields["key"].GetString()!, fields["handlerId"].GetString()!, fields["configurationVersion"].GetInt32(), fields["algorithmVersion"].GetInt32(),
                    (MapGenerationStage)fields["stage"].GetInt32(), (MapGenerationDomain)fields["domain"].GetInt32(), fields["configuration"].GetBytesFromBase64(),
                    fields["before"].EnumerateArray().Select(key => key.GetString()!), fields["after"].EnumerateArray().Select(key => key.GetString()!),
                    ReadPorts(fields["inputs"]), ReadPorts(fields["outputs"]),
                    fields["nativeOrder"].ValueKind == JsonValueKind.Null ? null : fields["nativeOrder"].GetInt32(), fields["isOpaque"].GetBoolean()));
            }
            return new(recipe["id"].GetString()!, steps);
        }
        private static IEnumerable<MapGenerationPort> ReadPorts(JsonElement element)
        {
            foreach (var item in element.EnumerateArray())
            {
                var fields = Fields(item, "name", "key", "typeId");
                yield return new(fields["name"].GetString()!, fields["key"].GetString()!, fields["typeId"].GetString()!);
            }
        }
        private static Dictionary<string, JsonElement> Fields(JsonElement element, params string[] expected)
        {
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!fields.TryAdd(property.Name, property.Value) || !expected.Contains(property.Name, StringComparer.Ordinal))
                    throw new InvalidDataException("Duplicate or unknown recipe field: " + property.Name);
            if (fields.Count != expected.Length) throw new InvalidDataException("Missing recipe field.");
            return fields;
        }
    }
}
