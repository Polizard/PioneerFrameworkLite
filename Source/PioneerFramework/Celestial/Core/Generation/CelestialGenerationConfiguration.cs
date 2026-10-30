using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PioneerFramework.Celestial
{
    /// <summary>层初始化前可独立读取的已解析配置；不包含组件运行状态。</summary>
    public static class CelestialGenerationConfiguration
    {
        public static ReadOnlyMemory<byte> Encode(IEnumerable<CelestialCompSaveRecord> records)
        {
            var ordered = records.OrderBy(record => record.Key, StringComparer.Ordinal).ToArray();
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(1);
            writer.Write(ordered.Length);
            foreach (var record in ordered)
            {
                writer.Write(record.Key);
                writer.Write(record.HandlerId);
                writer.Write(record.ConfigurationVersion);
                writer.Write(record.SchemaVersion);
                writer.Write(record.ConfigurationPayload.Length);
                writer.Write(record.ConfigurationPayload.Span);
            }
            writer.Flush();
            return stream.ToArray();
        }
        public static CelestialResult<IReadOnlyList<CelestialCompSaveRecord>> Decode(ReadOnlyMemory<byte> payload)
        {
            try
            {
                using var stream = new MemoryStream(payload.ToArray(), false);
                using var reader = new BinaryReader(stream, Encoding.UTF8);
                if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported generation configuration version.");
                int count = reader.ReadInt32();
                if (count < 0 || count > payload.Length) throw new InvalidDataException("Invalid component count.");
                var records = new List<CelestialCompSaveRecord>();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < count; index++)
                {
                    string key = reader.ReadString();
                    string handler = reader.ReadString();
                    int version = reader.ReadInt32();
                    int schema = reader.ReadInt32();
                    int length = reader.ReadInt32();
                    if (!keys.Add(key) || length < 0 || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid component payload.");
                    records.Add(new CelestialCompSaveRecord(key, handler, version, schema, reader.ReadBytes(length), ReadOnlySpan<byte>.Empty));
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing generation configuration bytes.");
                return CelestialResult.Ok<IReadOnlyList<CelestialCompSaveRecord>>(records.AsReadOnly());
            }
            catch (Exception exception) when (exception is ArgumentException or IOException)
            { return CelestialResult.Reject<IReadOnlyList<CelestialCompSaveRecord>>(new CelestialError("InvalidGenerationConfiguration", Detail: exception.Message)); }
        }
    }
}
