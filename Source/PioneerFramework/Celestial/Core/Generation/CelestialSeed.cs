using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PioneerFramework.Celestial
{
    /// <summary>版本 1：固定字节协议，字段独立派生，抽样顺序不改变已经存在的字段。</summary>
    public static class CelestialSeed
    {
        private static readonly byte[] protocol = Encoding.ASCII.GetBytes("PFL.Celestial.Generation.v1");

        /// <summary>Single derivation for a surface's native layer seed; every layer-creation path must use this.</summary>
        public static ulong SurfaceSeed(ulong worldSeed, string generationPath, string surfaceCompKey)
            => Derive(worldSeed, generationPath, surfaceCompKey, "surface.seed");

        /// <summary>Per-tile derivation; the tile index is part of the generation path (no inline formatting at call sites).</summary>
        public static ulong DeriveTile(ulong worldSeed, int tileIndex, string compKey, string parameterKey)
            => Derive(worldSeed, tileIndex.ToString(CultureInfo.InvariantCulture), compKey, parameterKey);

        /// <summary>Per-cell derivation inside a tile (integer cell coordinates are part of the generation path).</summary>
        public static ulong DeriveCell(ulong worldSeed, int tileIndex, int x, int z, string compKey, string parameterKey)
            => Derive(worldSeed, tileIndex.ToString(CultureInfo.InvariantCulture) + "/" + x.ToString(CultureInfo.InvariantCulture) + "/" + z.ToString(CultureInfo.InvariantCulture), compKey, parameterKey);

        /// <summary>Default create-request seed; keeps both creation entry points on one formula.</summary>
        public static ulong CreateSeed(ulong worldSeed, string generationPath)
            => Derive(worldSeed, generationPath, "$create", "seed");

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
