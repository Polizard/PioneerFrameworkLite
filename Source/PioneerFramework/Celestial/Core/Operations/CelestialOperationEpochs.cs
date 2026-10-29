using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>Only open epochs retain operation records. The monotonic high-water mark rejects every closed generation.</summary>
    public sealed class CelestialOperationEpochs
    {
        public const int MaximumOpenEpochs = 8;
        public const int MaximumRetainedPerEpoch = 4096;
        public const int MaximumPendingPerEpoch = 256;
        private long nextEpoch = 1;
        private readonly Dictionary<long, Usage> open = new() { [0] = new() };
        public CelestialEpochState Capture() => new(nextEpoch, open.Keys.Order());
        public IReadOnlyList<CelestialEpochUsage> CaptureUsage() => open.OrderBy(pair => pair.Key)
            .Select(pair => new CelestialEpochUsage(pair.Key, pair.Value.Pending, pair.Value.Terminal)).ToArray();
        internal void Restore(CelestialEpochState state)
        {
            if (state.NextEpoch < 1 || state.OpenEpochs.Count > MaximumOpenEpochs || state.OpenEpochs.Distinct().Count() != state.OpenEpochs.Count ||
                state.OpenEpochs.Any(epoch => epoch < 0 || epoch >= state.NextEpoch)) throw new ArgumentException("InvalidOperationEpochs");
            open.Clear(); foreach (long epoch in state.OpenEpochs) open.Add(epoch, new()); nextEpoch = state.NextEpoch;
        }
        public CelestialResult<long> Open()
        {
            if (open.Count >= MaximumOpenEpochs) return CelestialResult.Reject<long>(new CelestialError("EpochLimitReached"));
            if (nextEpoch == long.MaxValue) return CelestialResult.Reject<long>(new CelestialError("EpochSpaceExhausted"));
            long epoch = nextEpoch++; open.Add(epoch, new()); return CelestialResult.Ok(epoch);
        }
        public CelestialResult<bool> Close(long epoch)
        {
            if (!open.TryGetValue(epoch, out var usage)) return CelestialResult.Reject<bool>(new CelestialError(epoch >= 0 && epoch < nextEpoch ? "OperationExpired" : "InvalidOperationEpoch"));
            if (usage.Pending != 0) return CelestialResult.Reject<bool>(new CelestialError("EpochHasPendingOperations"));
            open.Remove(epoch); return CelestialResult.Ok(true);
        }
        internal string? Check(string operationId)
        {
            if (!TryGetEpoch(operationId, out long epoch)) return "InvalidOperationEpoch";
            return Check(epoch);
        }
        internal string? Check(long epoch) => open.ContainsKey(epoch) ? null : epoch >= 0 && epoch < nextEpoch ? "OperationExpired" : "InvalidOperationEpoch";
        internal string? Reserve(string operationId)
        {
            var error = Check(operationId); if (error != null) return error;
            TryGetEpoch(operationId, out long epoch); var usage = open[epoch];
            if (usage.Pending >= MaximumPendingPerEpoch) return "EpochPendingLimitReached";
            if (usage.Pending + usage.Terminal >= MaximumRetainedPerEpoch) return "EpochReceiptLimitReached";
            usage.Pending++; return null;
        }
        internal void Completed(string operationId)
        {
            TryGetEpoch(operationId, out long epoch); var usage = open[epoch];
            if (usage.Pending <= 0) throw new InvalidOperationException("EpochPendingUnderflow");
            usage.Pending--; usage.Terminal++;
        }
        internal void Recount(string operationId, CelestialOperationStatus status)
        {
            var error = Reserve(operationId); if (error != null) throw new ArgumentException(error);
            if (status != CelestialOperationStatus.Pending) Completed(operationId);
        }
        public static string Qualify(long epoch, string operationId)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(epoch); ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
            return "epoch/" + epoch.ToString(CultureInfo.InvariantCulture) + "/" + Uri.EscapeDataString(operationId);
        }
        public static bool TryGetEpoch(string operationId, out long epoch)
        {
            epoch = 0;
            if (string.IsNullOrWhiteSpace(operationId)) return false;
            if (!operationId.StartsWith("epoch/", StringComparison.Ordinal)) return true;
            int slash = operationId.IndexOf('/', 6);
            return slash > 6 && slash < operationId.Length - 1 && long.TryParse(operationId.AsSpan(6, slash - 6), NumberStyles.None, CultureInfo.InvariantCulture, out epoch) && epoch >= 0;
        }
        private sealed class Usage { internal int Pending, Terminal; }
    }
}
