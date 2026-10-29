using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PioneerFramework.Celestial
{
    public static class CelestialBakeCacheIdentity
    {
        public static string ConfigurationHash(CelestialGenerationOrigin origin, IEnumerable<CelestialCompSaveRecord> components)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write("PFL.Bake.Configuration.v1"); writer.Write(origin.WorldSeed); writer.Write(origin.GenerationKey); writer.Write(origin.AlgorithmVersion);
                var configuration = CelestialGenerationConfiguration.Encode(components); writer.Write(configuration.Length); writer.Write(configuration.Span);
            }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }
        public static string DiskKey(int worldSeed, CelestialInstanceId id, long descriptorRevision, string configurationHash)
        {
            if (!id.IsValid) throw new ArgumentException("Invalid cache identity.");
            ArgumentOutOfRangeException.ThrowIfLessThan(descriptorRevision, 1);
            ArgumentException.ThrowIfNullOrWhiteSpace(configurationHash);
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            { writer.Write("PFL.Bake.File.v1"); writer.Write(worldSeed); writer.Write(id.Value); writer.Write(descriptorRevision); writer.Write(configurationHash); }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }
    }
}
