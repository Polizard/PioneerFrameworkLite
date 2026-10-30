using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed record CelestialOperationRecord(string ContentHash, CelestialOperation Operation);
    public sealed record CelestialOperationTicket(CelestialOperation Operation, bool IsNew);
    public sealed class CelestialOperationStore
    {
        private readonly Dictionary<string, CelestialOperationRecord> entries = new(StringComparer.Ordinal);
        public Guid WorldSessionId { get; }
        public CelestialOperationEpochs Epochs { get; }
        public CelestialOperationStore(Guid worldSessionId, IEnumerable<CelestialOperationRecord>? restored = null, CelestialOperationEpochs? epochs = null)
        {
            if (worldSessionId == Guid.Empty) throw new ArgumentException("Missing world session.", nameof(worldSessionId));
            WorldSessionId = worldSessionId;
            Epochs = epochs ?? new();
            foreach (var record in restored ?? Array.Empty<CelestialOperationRecord>()) { entries.Add(record.Operation.OperationId, record); Epochs.Recount(record.Operation.OperationId, record.Operation.Status); }
        }
        public CelestialResult<CelestialOperationTicket> Begin(CelestialOperationContext context, string contentHash, long actualRevision,
            IEnumerable<CelestialInstanceId> ids)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.OperationId) || string.IsNullOrWhiteSpace(contentHash)) return Reject("InvalidOperation");
            if (context.WorldSessionId != WorldSessionId) return Reject("StaleSession");
            var epochError = Epochs.Check(context.OperationId); if (epochError != null) return Reject(epochError);
            if (entries.TryGetValue(context.OperationId, out var known))
                return known.ContentHash == contentHash ? CelestialResult.Ok(new CelestialOperationTicket(known.Operation, false)) : Reject("OperationConflict");
            if (context.ExpectedRevision != actualRevision) return Reject("RevisionConflict");
            epochError = Epochs.Reserve(context.OperationId); if (epochError != null) return Reject(epochError);
            var operation = new CelestialOperation(context.OperationId, CelestialOperationStatus.Pending, ids);
            entries.Add(context.OperationId, new CelestialOperationRecord(contentHash, operation));
            return CelestialResult.Ok(new CelestialOperationTicket(operation, true));
        }
        public CelestialOperation Get(string operationId)
            => entries.TryGetValue(operationId, out var entry) ? entry.Operation :
                new CelestialOperation(operationId, CelestialOperationStatus.Rejected, errors: new[] { new CelestialError(Epochs.Check(operationId) ?? "OperationMissing") });
        public bool Contains(string operationId) => entries.ContainsKey(operationId);
        public CelestialResult<long> OpenEpoch() => Epochs.Open();
        public CelestialResult<bool> CloseEpoch(long epoch)
        {
            var result = Epochs.Close(epoch);
            if (!result.Success) return result;
            foreach (var key in entries.Keys) if (CelestialOperationEpochs.TryGetEpoch(key, out long owner) && owner == epoch) entries.Remove(key);
            return result;
        }
        internal void Update(CelestialOperation operation)
        {
            var previous = entries[operation.OperationId];
            if (previous.Operation.Status == CelestialOperationStatus.Pending && operation.Status != CelestialOperationStatus.Pending) Epochs.Completed(operation.OperationId);
            entries[operation.OperationId] = previous with { Operation = operation };
        }
        public IReadOnlyList<CelestialOperationRecord> Capture()
            => Array.AsReadOnly(entries.Values.OrderBy(entry => entry.Operation.OperationId, StringComparer.Ordinal).ToArray());
        private static CelestialResult<CelestialOperationTicket> Reject(string code)
            => CelestialResult.Reject<CelestialOperationTicket>(new CelestialError(code));
    }
}
