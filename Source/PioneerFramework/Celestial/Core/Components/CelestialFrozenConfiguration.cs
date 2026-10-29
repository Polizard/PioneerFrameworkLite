using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace PioneerFramework.Celestial
{
    /// <summary>Resolved properties are immutable. Weak ownership avoids retaining ended worlds or failed resolutions.</summary>
    internal static class CelestialFrozenConfiguration
    {
        private static readonly ConditionalWeakTable<ICelestialResolvedProperties, Lazy<Payload>> payloads = new();
        internal static Payload Get(ICelestialResolvedProperties properties)
            => payloads.GetValue(properties, value => new Lazy<Payload>(() => new(CelestialCostCounters.SerializeProperties(value).ToArray()),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        internal static void Retain(ICelestialResolvedProperties properties, ReadOnlyMemory<byte> bytes)
            => payloads.GetValue(properties, _ => new Lazy<Payload>(() => new(bytes.ToArray()), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));
        internal sealed class Payload(byte[] bytes)
        {
            internal ReadOnlyMemory<byte> Bytes { get; } = bytes;
            internal string Digest { get; } = Convert.ToHexString(SHA256.HashData(bytes));
        }
    }
}
