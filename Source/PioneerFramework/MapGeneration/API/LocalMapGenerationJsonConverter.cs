using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PioneerFramework.MapGeneration
{
    public sealed class LocalMapGenerationJsonConverter : JsonConverter<LocalMapGenerationSpec>
    {
        public override LocalMapGenerationSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            try { return LocalMapGenerationCodec.Decode(System.Text.Encoding.UTF8.GetBytes(document.RootElement.GetRawText())); }
            catch (Exception exception) when (exception is System.IO.InvalidDataException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
            { throw new JsonException("Invalid frozen local map specification.", exception); }
        }
        public override void Write(Utf8JsonWriter writer, LocalMapGenerationSpec value, JsonSerializerOptions options)
            => writer.WriteRawValue(LocalMapGenerationCodec.Encode(value).Span);
    }
}
