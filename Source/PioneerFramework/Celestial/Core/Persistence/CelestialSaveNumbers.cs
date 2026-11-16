using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PioneerFramework.Celestial
{
    internal static class CelestialSaveNumbers
    {
        internal static void AddConverters(JsonSerializerOptions options)
        {
            options.Converters.Add(new RoundTripDoubleConverter());
            options.Converters.Add(new RoundTripSingleConverter());
        }

        internal static bool MatchesLegacyDouble(double saved, double exact) =>
            saved == exact || saved == double.Parse(exact.ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        internal static bool MatchesLegacyDouble(double? saved, double? exact) =>
            saved == exact || saved.HasValue && exact.HasValue && MatchesLegacyDouble(saved.Value, exact.Value);

        internal static bool MatchesLegacySingle(float saved, float exact) =>
            saved == exact || saved == float.Parse(exact.ToString("G7", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        // Waylith's JSON number writer uses only 15 double digits and 7 single digits.
        private sealed class RoundTripDoubleConverter : JsonConverter<double>
        {
            public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDouble();
            public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
                writer.WriteRawValue(value.ToString("G17", CultureInfo.InvariantCulture));
        }

        private sealed class RoundTripSingleConverter : JsonConverter<float>
        {
            public override float Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetSingle();
            public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options) =>
                writer.WriteRawValue(value.ToString("G9", CultureInfo.InvariantCulture));
        }
    }
}
