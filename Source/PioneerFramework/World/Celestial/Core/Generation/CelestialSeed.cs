using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PioneerFramework.Celestial
{
    /// <summary>版本 1：固定字节协议，字段独立派生，抽样顺序不改变已经存在的字段。</summary>
    public static class CelestialSeed
    {
        private static readonly byte[] protocol = Encoding.ASCII.GetBytes("PFL.Celestial.Generation.v1");
        public const int AlgorithmVersion = 1;

        public static ulong Derive(ulong worldSeed, string generationPath, string compKey, string parameterKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(generationPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(compKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(parameterKey);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(protocol);
            writer.Write(worldSeed);
            WriteField(writer, generationPath);
            WriteField(writer, compKey);
            WriteField(writer, parameterKey);
            writer.Flush();
            return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(stream.ToArray()));
        }

        internal static ulong NextWord(ulong sampleWord, ulong counter)
        {
            Span<byte> input = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(input, sampleWord);
            BinaryPrimitives.WriteUInt64LittleEndian(input[8..], counter);
            return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(input));
        }

        private static void WriteField(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(checked((uint)bytes.Length));
            writer.Write(bytes);
        }
    }
}
