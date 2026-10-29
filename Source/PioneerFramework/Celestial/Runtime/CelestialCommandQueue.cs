using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PioneerFramework.Celestial
{
    public interface ICelestialCommandHandler
    {
        CelestialResult<bool> Execute(CelestialCommand command);
    }
    /// <summary>组件副作用在宿主边界执行。命令 ExpectedRevision 指向目标描述修订；投递会话单独校验。</summary>
    public sealed class CelestialCommandQueue
    {
        private readonly Guid session;
        private readonly CelestialCatalog catalog;
        private readonly CelestialOperationEpochs epochs;
        private readonly Dictionary<string, ICelestialCommandHandler> handlers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CelestialCommandRecord> records = new(StringComparer.Ordinal);
        private readonly Queue<string> pending = new();
        private bool sealedHandlers;
        public int PendingCount => pending.Count;
        public CelestialCommandQueue(Guid session, CelestialCatalog catalog, IEnumerable<CelestialCommandRecord>? restored = null, CelestialOperationEpochs? epochs = null)
        {
            this.session = session; this.catalog = catalog;
            this.epochs = epochs ?? new();
            foreach (var record in restored ?? Array.Empty<CelestialCommandRecord>())
            {
                var command = record.Command;
                this.epochs.Recount(command.OperationId, record.Receipt.Status);
                records.Add(command.OperationId, record with { Command = new(command.Kind, command.OperationId, command.TargetId, session, command.ExpectedRevision, command.Payload.Span) });
                if (record.Receipt.Status == CelestialOperationStatus.Pending) pending.Enqueue(command.OperationId);
            }
        }
        public CelestialResult<bool> Register(string kind, ICelestialCommandHandler handler)
        {
            if (sealedHandlers) return CelestialResult.Reject<bool>(new CelestialError("RegistrySealed"));
            if (string.IsNullOrWhiteSpace(kind) || handler == null) return CelestialResult.Reject<bool>(new CelestialError("InvalidCommandHandler"));
            return handlers.TryAdd(kind, handler) ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(new CelestialError("DuplicateCommandHandler"));
        }
        public void Seal() => sealedHandlers = true;
        public bool Contains(string operationId) => records.ContainsKey(operationId);
        public CelestialOperation Get(string operationId) => records.TryGetValue(operationId, out var record) ? record.Receipt :
            new CelestialOperation(operationId, CelestialOperationStatus.Rejected, errors: new[] { new CelestialError("OperationMissing") });
        public CelestialResult<string> Enqueue(CelestialCommand command)
        {
            if (command.WorldSessionId != session) return Reject("StaleSession", command);
            var epochError = epochs.Check(command.OperationId); if (epochError != null) return Reject(epochError, command);
            if (records.TryGetValue(command.OperationId, out var record))
                return Hash(record.Command) == Hash(command) ? CelestialResult.Ok(command.OperationId) : Reject("OperationConflict", command);
            var errors = Validate(command, true);
            if (errors.Count > 0) return CelestialResult.Reject<string>(errors.ToArray());
            epochError = epochs.Reserve(command.OperationId); if (epochError != null) return Reject(epochError, command);
            records.Add(command.OperationId, new CelestialCommandRecord(command,
                new CelestialOperation(command.OperationId, CelestialOperationStatus.Pending, new[] { command.TargetId })));
            pending.Enqueue(command.OperationId);
            return CelestialResult.Ok(command.OperationId);
        }
        internal void Drain(CelestialRuntime runtime)
        {
            int count = runtime.WithPublication(() => pending.Count);
            for (int index = 0; index < count; index++)
            {
                var work = runtime.WithPublication(() => {
                    string next = pending.Dequeue();
                    var entry = records[next];
                    return (Id: next, Record: entry, Errors: Validate(entry.Command, false), Handler: handlers.GetValueOrDefault(entry.Command.Kind));
                });
                string id = work.Id;
                var record = work.Record;
                var errors = work.Errors;
                if (errors.Count == 0)
                {
                    try
                    {
                        var result = work.Handler!.Execute(record.Command);
                        if (!result.Success) errors.AddRange(result.Errors);
                        else if (!result.Value) errors.Add(new CelestialError("CommandRejected", record.Command.TargetId));
                    }
                    catch (Exception exception) { errors.Add(new CelestialError("CommandExecutionFailed", record.Command.TargetId, Detail: exception.Message)); }
                }
                runtime.WithPublication(() => {
                    epochs.Completed(id);
                    records[id] = record with { Receipt = new CelestialOperation(id, errors.Count == 0 ? CelestialOperationStatus.Applied : CelestialOperationStatus.Rejected,
                        new[] { record.Command.TargetId }, errors, errors.Count == 0 ? catalog.Revision : null) };
                    return true;
                });
            }
        }
        public IReadOnlyList<CelestialCommandRecord> Capture() => Array.AsReadOnly(pending.Select(id => records[id]).Concat(records.Values
            .Where(record => record.Receipt.Status != CelestialOperationStatus.Pending).OrderBy(record => record.Command.OperationId, StringComparer.Ordinal)).ToArray());
        internal void CompactEpoch(long epoch)
        {
            foreach (var id in records.Keys) if (CelestialOperationEpochs.TryGetEpoch(id, out long owner) && owner == epoch) records.Remove(id);
        }
        private List<CelestialError> Validate(CelestialCommand command, bool allowRegistering)
        {
            var errors = new List<CelestialError>();
            if (!handlers.ContainsKey(command.Kind)) errors.Add(new CelestialError("CommandHandlerMissing", command.TargetId, Detail: command.Kind));
            if (!catalog.TryGetInstance(command.TargetId, out var instance)) errors.Add(new CelestialError("BodyMissing", command.TargetId));
            else
            {
                if (instance.Description.DescriptorRevision != command.ExpectedRevision) errors.Add(new CelestialError("RevisionConflict", command.TargetId));
                if (instance.Lifecycle != CelestialLifecycle.Ready && !(allowRegistering && instance.Lifecycle == CelestialLifecycle.Registering))
                    errors.Add(new CelestialError("BodyNotReady", command.TargetId));
            }
            return errors;
        }
        private static CelestialResult<string> Reject(string code, CelestialCommand command)
            => CelestialResult.Reject<string>(new CelestialError(code, command.TargetId));
        private static string Hash(CelestialCommand command)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write("PFL.Celestial.Command.v1");
            writer.Write(command.Kind);
            writer.Write(command.TargetId.Value);
            writer.Write(command.Payload.Length);
            writer.Write(command.Payload.Span);
            writer.Flush();
            return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        }
    }
}
