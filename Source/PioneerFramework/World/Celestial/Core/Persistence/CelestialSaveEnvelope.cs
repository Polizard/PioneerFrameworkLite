using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PioneerFramework.Celestial
{
    /// <summary>Wire v2: bounded Brotli stream, ordered 256 KiB chunks, independent SHA-256 integrity.</summary>
    internal static class CelestialSaveEnvelope
    {
        internal const int ChunkBytes = 256 * 1024;
        internal const int MaximumDecodedBytes = 128 * 1024 * 1024;
        internal const int MaximumEncodedBytes = 192 * 1024 * 1024;
        internal const int MaximumChunkCount = MaximumDecodedBytes / ChunkBytes + 2;
        private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };

        internal static byte[] Encode(CelestialSaveDocument document, JsonSerializerOptions payloadOptions)
        {
            using var compressed = new MemoryStream();
            long length;
            using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, true))
            using (var bounded = new BoundedWriter(brotli))
            {
                JsonSerializer.Serialize(bounded, document, payloadOptions);
                length = bounded.Length;
            }
            var bytes = compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length));
            var chunks = new List<Chunk>();
            for (int offset = 0; offset < bytes.Length; offset += ChunkBytes)
            {
                var data = bytes.Slice(offset, Math.Min(ChunkBytes, bytes.Length - offset)).ToArray();
                chunks.Add(new(chunks.Count, Hash(data), data));
            }
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope(2, "brotli", length, "sha256", Hash(bytes), chunks.Count, chunks.ToArray()), Options);
        }

        internal static CelestialSaveDocument Decode(ReadOnlyMemory<byte> payload, JsonSerializerOptions payloadOptions)
        {
            if (payload.Length > MaximumEncodedBytes) throw new InvalidDataException("Encoded save exceeds the limit.");
            RejectDuplicates(payload.Span);
            var value = JsonSerializer.Deserialize<Envelope>(payload.Span, Options) ?? throw new JsonException("Missing envelope.");
            if (value.FormatVersion != 2 || value.Compression != "brotli" || value.HashScheme != "sha256" ||
                value.DecodedLength <= 0 || value.DecodedLength > MaximumDecodedBytes || value.ChunkCount <= 0 ||
                value.ChunkCount > MaximumChunkCount || value.Chunks == null || value.Chunks.Length != value.ChunkCount)
                throw new InvalidDataException("Invalid save envelope.");
            using var compressed = new MemoryStream();
            for (int index = 0; index < value.Chunks.Length; index++)
            {
                var chunk = value.Chunks[index];
                if (chunk == null || chunk.Index != index || chunk.Data == null || chunk.Data.Length <= 0 || chunk.Data.Length > ChunkBytes ||
                    index < value.Chunks.Length - 1 && chunk.Data.Length != ChunkBytes || Hash(chunk.Data) != chunk.Sha256)
                    throw new InvalidDataException("Invalid or reordered chunk.");
                compressed.Write(chunk.Data);
            }
            if (Hash(compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length))) != value.CompressedSha256)
                throw new InvalidDataException("Compressed stream integrity mismatch.");
            // The incremental decoder reports consumed input, allowing rejection of trailing/concatenated streams.
            using var decoder = new BrotliDecoder();
            using var decoded = new MemoryStream();
            var input = compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length));
            var buffer = new byte[64 * 1024];
            int consumedTotal = 0;
            while (true)
            {
                var status = decoder.Decompress(input.Slice(consumedTotal), buffer, out int consumed, out int written);
                consumedTotal += consumed;
                if (decoded.Length + written > value.DecodedLength) throw new InvalidDataException("Decoded save exceeds its declared limit.");
                decoded.Write(buffer, 0, written);
                if (status == System.Buffers.OperationStatus.Done)
                {
                    if (consumedTotal != input.Length || decoded.Length != value.DecodedLength) throw new InvalidDataException("Truncated or trailing save stream.");
                    break;
                }
                if (status != System.Buffers.OperationStatus.DestinationTooSmall || consumed == 0 && written == 0)
                    throw new InvalidDataException("Invalid Brotli stream.");
            }
            var json = decoded.GetBuffer().AsSpan(0, checked((int)decoded.Length));
            RejectDuplicates(json);
            return JsonSerializer.Deserialize<CelestialSaveDocument>(json, payloadOptions) ?? throw new JsonException("Missing save data.");
        }

        private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private static void RejectDuplicates(ReadOnlySpan<byte> bytes)
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
        private sealed record Envelope(
            [property: JsonPropertyName("formatVersion")] int FormatVersion,
            [property: JsonPropertyName("compression")] string Compression,
            [property: JsonPropertyName("decodedLength")] long DecodedLength,
            [property: JsonPropertyName("hashScheme")] string HashScheme,
            [property: JsonPropertyName("compressedSha256")] string CompressedSha256,
            [property: JsonPropertyName("chunkCount")] int ChunkCount,
            [property: JsonPropertyName("chunks")] Chunk[] Chunks);
        private sealed record Chunk([property: JsonPropertyName("index")] int Index,
            [property: JsonPropertyName("sha256")] string Sha256, [property: JsonPropertyName("data")] byte[] Data);
        private sealed class BoundedWriter(Stream inner) : Stream
        {
            private long length;
            public override long Length => length;
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> bytes)
            { if (length + bytes.Length > MaximumDecodedBytes) throw new InvalidDataException("Save exceeds decoded limit."); inner.Write(bytes); length += bytes.Length; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Position { get => length; set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
