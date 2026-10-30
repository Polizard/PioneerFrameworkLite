using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PioneerFramework.Celestial
{
    internal static class CelestialJsonFields
    {
        internal static void RejectDuplicates(ReadOnlySpan<byte> bytes)
        {
            var reader = new Utf8JsonReader(bytes);
            var objects = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!)) throw new JsonException("Duplicate field.");
            }
        }
    }
}
