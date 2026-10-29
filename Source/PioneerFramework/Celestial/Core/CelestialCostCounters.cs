using System;
using System.Threading;

namespace PioneerFramework.Celestial
{
    /// <summary>Opt-in structural measurements for isolated tests/benchmarks; disabled in normal play.</summary>
    internal static class CelestialCostCounters
    {
        private static int enabled;
        private static readonly long[] counts = new long[7];
        internal static IDisposable Measure()
        {
            if (Interlocked.CompareExchange(ref enabled, 1, 0) != 0) throw new InvalidOperationException("A celestial measurement is already active.");
            Array.Clear(counts);
            return new Scope();
        }
        internal static CelestialCostSnapshot Snapshot() => new(Interlocked.Read(ref counts[0]), Interlocked.Read(ref counts[1]),
            Interlocked.Read(ref counts[2]), Interlocked.Read(ref counts[3]), Interlocked.Read(ref counts[4]),
            Interlocked.Read(ref counts[5]), Interlocked.Read(ref counts[6]));
        private static void Record(int index) { if (Volatile.Read(ref enabled) != 0) Interlocked.Increment(ref counts[index]); }
        internal static ReadOnlyMemory<byte> EncodeProperties(ICelestialResolvedProperties properties)
            => CelestialFrozenConfiguration.Get(properties).Bytes;
        internal static ReadOnlyMemory<byte> SerializeProperties(ICelestialResolvedProperties properties)
        { Record(0); return properties.Serialize(); }
        internal static ReadOnlyMemory<byte> CaptureState(CelestialBodyComp component)
        { Record(1); return component.CaptureState(); }
        internal static void VisualProfileEncoded() => Record(2);
        internal static void VisualHashComputed() => Record(3);
        internal static void Advanced() => Record(4);
        internal static void ShipGraphPrepared() => Record(5);
        internal static void CatalogSorted() => Record(6);
        private sealed class Scope : IDisposable
        {
            private bool disposed;
            public void Dispose() { if (disposed) return; Volatile.Write(ref enabled, 0); disposed = true; }
        }
    }
    internal readonly record struct CelestialCostSnapshot(long ConfigurationSerializations, long StateCaptures,
        long VisualProfileSerializations, long VisualHashes, long Advances, long ShipGraphs, long CatalogSorts);
}
