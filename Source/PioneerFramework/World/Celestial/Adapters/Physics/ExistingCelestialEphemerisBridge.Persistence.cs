using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PioneerFramework.Celestial
{
    public sealed partial class ExistingCelestialEphemerisBridge
    {
        public PhysicsCheckpoint CaptureCheckpoint()
        {
            lock (gate)
            {
                if (active != null) throw new InvalidOperationException("CheckpointUnavailable");
                var data = new SaveData(StampData.From(CommittedStamp), RegistrationHash(bodies.Values.Select(body => body.Registration)),
                    receiptEpochs?.NextEpoch, receiptEpochs?.OpenEpochs.ToArray(),
                    usedIds.Select(id => id.Value).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                    operations.Values.OrderBy(entry => entry.Request.OperationId, StringComparer.Ordinal).Select(entry => new OperationData(
                        entry.Request.OperationId, entry.Hash, entry.Receipt.Status, entry.Receipt.AppliedSeconds, entry.Receipt.TopologyRevision,
                        entry.Receipt.Errors.Select(e => new ErrorData(e.Code, e.InstanceId?.Value, e.CompKey, e.Detail)).ToArray(),
                        entry.Request.Changes.Select(c => new ChangeData(c.Kind, c.InstanceId.Value, c.Registration == null ? null : ToData(c.Registration))).ToArray())).ToArray());
                return new(Id, ContractVersion, 2, Guid.NewGuid().ToString("N"), CommittedStamp, JsonSerializer.SerializeToUtf8Bytes(data, SaveOptions));
            }
        }
        public PhysicsRestoreResult RestoreCheckpoint(PhysicsCheckpoint checkpoint, IReadOnlyList<CelestialBodyRegistration> committedBodies)
        {
            lock (gate)
            {
                if (checkpoint.ProviderId != Id) return PhysicsRestoreResult.Reject(new CelestialError("ProviderUnavailable"));
                if (checkpoint.ContractVersion != ContractVersion || checkpoint.PayloadVersion != 2) return PhysicsRestoreResult.Reject(new CelestialError("UnsupportedContractVersion"));
                if (active != null) return PhysicsRestoreResult.Reject(new CelestialError("IntervalAlreadyPrepared"));
                // 恢复只允许空会话；错误载荷不能替换已运行的世界。
                if (bodies.Count != 0 || operations.Count != 0 || CommittedStamp.StateRevision != 0) return PhysicsRestoreResult.Reject(new CelestialError("RestoreRequiresEmptyProvider"));
                try
                {
                    var data = JsonSerializer.Deserialize<SaveData>(checkpoint.Payload.Span, SaveOptions)!;
                    if (data == null || data.Stamp.ToStamp() != checkpoint.Stamp || data.RegistrationHash != RegistrationHash(committedBodies) ||
                        checkpoint.Stamp.ReferenceFrameId != ReferenceFrame.Id || checkpoint.Stamp.SampleKind != PhysicsSampleKind.Committed)
                        throw new ArgumentException("Checkpoint identity mismatch.");
                    var restored = new Dictionary<CelestialInstanceId, Body>();
                    foreach (var registration in committedBodies)
                    {
                        var parsed = ExistingEphemerisParameters.Parse(registration.Initialization);
                        if (!parsed.Success) return PhysicsRestoreResult.Reject(parsed.Errors.ToArray());
                        restored.Add(registration.InstanceId, new(registration, parsed.Value));
                    }
                    var graphError = ValidateGraph(restored);
                    if (graphError != null) return PhysicsRestoreResult.Reject(graphError);
                    var used = data.UsedIds.Select(CelestialInstanceId.Parse).ToHashSet();
                    if (used.Count != data.UsedIds.Length || restored.Keys.Any(id => !used.Contains(id))) throw new ArgumentException("Invalid identity history.");
                    var stamp = checkpoint.Stamp with { WorldSessionId = SessionId, ClockRevision = checked(checkpoint.Stamp.ClockRevision + 1) };
                    var validation = Snapshot(restored, stamp, restored.Keys.ToArray(), PhysicsSampleKind.Committed);
                    if (!validation.Success) return PhysicsRestoreResult.Reject(validation.Errors.ToArray());
                    validation.Snapshot!.Dispose();
                    var restoredOperations = new Dictionary<string, Entry>(StringComparer.Ordinal);
                    foreach (var operation in data.Operations)
                    {
                        var changes = operation.Changes.Select(change => change.Kind switch {
                            PhysicsBodyChangeKind.Register when change.Registration != null => PhysicsBodyChange.Register(FromData(change.Registration)),
                            PhysicsBodyChangeKind.Update when change.Registration != null => PhysicsBodyChange.Update(FromData(change.Registration)),
                            PhysicsBodyChangeKind.Unregister when change.Registration == null => PhysicsBodyChange.Unregister(CelestialInstanceId.Parse(change.Id)),
                            _ => throw new ArgumentException("Invalid change kind.") }).ToArray();
                        if (changes.Length == 0 || changes.Where((c, i) => c.InstanceId.Value != operation.Changes[i].Id).Any() || !Enum.IsDefined(operation.Status))
                            throw new ArgumentException("Invalid operation.");
                        var request = new PhysicsChangeSet(operation.Id, SessionId, stamp.TopologyRevision, changes);
                        if (request.ContentHash() != operation.Hash)
                        {
                            // An old writer could round a saved descriptor. Try the committed numbers
                            // while retaining every other operation field and requiring its original hash.
                            var corrected = changes.Select(change => restored.TryGetValue(change.InstanceId, out var body)
                                ? RestoreOperationNumbers(change, body.Registration.Descriptor) : change);
                            request = new PhysicsChangeSet(operation.Id, SessionId, stamp.TopologyRevision, corrected);
                            if (request.ContentHash() != operation.Hash) throw new ArgumentException("Invalid operation content hash.");
                        }
                        if (operation.Status == PhysicsOperationStatus.Applied && (!operation.AppliedSeconds.HasValue ||
                            !double.IsFinite(operation.AppliedSeconds.Value) || operation.AppliedSeconds.Value > stamp.PhysicalSeconds ||
                            operation.TopologyRevision is not { } revision || revision < 0 || revision > stamp.TopologyRevision))
                            throw new ArgumentException("Invalid applied receipt.");
                        if (operation.Status != PhysicsOperationStatus.Applied && (operation.AppliedSeconds.HasValue || operation.TopologyRevision.HasValue))
                            throw new ArgumentException("Invalid pending or rejected receipt.");
                        var errors = operation.Errors.Select(e => new CelestialError(e.Code, e.InstanceId == null ? null : CelestialInstanceId.Parse(e.InstanceId), e.CompKey, e.Detail)).ToArray();
                        if (errors.Any(e => string.IsNullOrWhiteSpace(e.Code))) throw new ArgumentException("Invalid diagnostic.");
                        restoredOperations.Add(operation.Id, new(request, operation.Hash,
                            new(operation.Id, operation.Status, errors, operation.AppliedSeconds, operation.TopologyRevision), false));
                    }
                    CelestialEpochState? restoredEpochs = null;
                    if (data.NextEpoch.HasValue)
                    { var tracker = new CelestialOperationEpochs(); tracker.Restore(new(data.NextEpoch.Value, data.OpenEpochs ?? throw new ArgumentException("Missing epoch state."))); restoredEpochs = tracker.Capture(); }
                    else if (data.OpenEpochs != null) throw new ArgumentException("Invalid epoch state.");
                    receiptEpochs = restoredEpochs;
                    bodies = restored; usedIds = used; operations = restoredOperations; CommittedStamp = stamp;
                    history = new() { new(stamp.PhysicalSeconds, stamp.PhysicalSeconds, stamp, restored) };
                    tokens.Clear();
                    return PhysicsRestoreResult.Ok(stamp);
                }
                catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or NullReferenceException or OverflowException)
                { return PhysicsRestoreResult.Reject(new CelestialError("RestoreMismatch", Detail: exception.Message)); }
            }
        }
        private string RegistrationHash(IEnumerable<CelestialBodyRegistration> registrations)
            => new PhysicsChangeSet("checkpoint", SessionId, 0, registrations.Select(PhysicsBodyChange.Register)).ContentHash();
        private static RegistrationData ToData(CelestialBodyRegistration r) => new(r.InstanceId.Value, r.DefinitionId, r.Descriptor.DescriptorRevision,
            r.Descriptor.RadiusMeters, r.Descriptor.GravitationalParameter, r.Initialization.ProviderId, r.Initialization.ModeId, r.Initialization.Version,
            r.Initialization.Payload.ToArray(), r.ParentInstanceId?.Value, r.CompatibilityHandle,
            r.InitializationReferences.ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal));
        private static CelestialBodyRegistration FromData(RegistrationData r) => new(r.DefinitionId,
            new(CelestialInstanceId.Parse(r.Id), r.Revision, r.Radius, r.Gm), new(r.Provider, r.Mode, r.Version, r.Payload),
            r.Parent == null ? null : CelestialInstanceId.Parse(r.Parent), r.References.ToDictionary(p => p.Key, p => CelestialInstanceId.Parse(p.Value), StringComparer.Ordinal), r.Handle);
        private static PhysicsBodyChange RestoreOperationNumbers(PhysicsBodyChange change, CelestialPhysicsDescriptor exact)
        {
            if (change.Registration is not { } registration || registration.Descriptor.DescriptorRevision != exact.DescriptorRevision ||
                !CelestialSaveNumbers.MatchesLegacyDouble(registration.Descriptor.RadiusMeters, exact.RadiusMeters) ||
                !CelestialSaveNumbers.MatchesLegacyDouble(registration.Descriptor.GravitationalParameter, exact.GravitationalParameter))
                return change;
            var corrected = new CelestialBodyRegistration(registration.DefinitionId, exact, registration.Initialization,
                registration.ParentInstanceId, registration.InitializationReferences, registration.CompatibilityHandle);
            return change.Kind == PhysicsBodyChangeKind.Register ? PhysicsBodyChange.Register(corrected) : PhysicsBodyChange.Update(corrected);
        }

        private static readonly JsonSerializerOptions SaveOptions = CreateSaveOptions();
        private static JsonSerializerOptions CreateSaveOptions()
        {
            var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
            CelestialSaveNumbers.AddConverters(options);
            return options;
        }
        private sealed record StampData([property: JsonPropertyName("session")] Guid Session, [property: JsonPropertyName("clock")] long Clock, [property: JsonPropertyName("state")] long State, [property: JsonPropertyName("topology")] long Topology, [property: JsonPropertyName("seconds")] double Seconds, [property: JsonPropertyName("frame")] string Frame, [property: JsonPropertyName("kind")] PhysicsSampleKind Kind)
        {
            internal static StampData From(PhysicsSampleStamp v) => new(v.WorldSessionId, v.ClockRevision, v.StateRevision, v.TopologyRevision, v.PhysicalSeconds, v.ReferenceFrameId, v.SampleKind);
            internal PhysicsSampleStamp ToStamp() => new(Session, Clock, State, Topology, Seconds, Frame, Kind);
        }
        private sealed record SaveData([property: JsonPropertyName("stamp")] StampData Stamp, [property: JsonPropertyName("registrationHash")] string RegistrationHash, [property: JsonPropertyName("nextEpoch")] long? NextEpoch, [property: JsonPropertyName("openEpochs")] long[]? OpenEpochs, [property: JsonPropertyName("usedIds")] string[] UsedIds, [property: JsonPropertyName("operations")] OperationData[] Operations);
        private sealed record RegistrationData([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("definitionId")] string DefinitionId, [property: JsonPropertyName("revision")] long Revision, [property: JsonPropertyName("radius")] double Radius, [property: JsonPropertyName("gm")] double? Gm, [property: JsonPropertyName("provider")] string Provider, [property: JsonPropertyName("mode")] string Mode, [property: JsonPropertyName("version")] int Version, [property: JsonPropertyName("payload")] byte[] Payload, [property: JsonPropertyName("parent")] string? Parent, [property: JsonPropertyName("handle")] int? Handle, [property: JsonPropertyName("references")] Dictionary<string, string> References);
        private sealed record ChangeData([property: JsonPropertyName("kind")] PhysicsBodyChangeKind Kind, [property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("registration")] RegistrationData? Registration);
        private sealed record OperationData([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("hash")] string Hash, [property: JsonPropertyName("status")] PhysicsOperationStatus Status, [property: JsonPropertyName("appliedSeconds")] double? AppliedSeconds, [property: JsonPropertyName("topologyRevision")] long? TopologyRevision, [property: JsonPropertyName("errors")] ErrorData[] Errors, [property: JsonPropertyName("changes")] ChangeData[] Changes);
        private sealed record ErrorData([property: JsonPropertyName("code")] string Code, [property: JsonPropertyName("instanceId")] string? InstanceId, [property: JsonPropertyName("compKey")] string? CompKey, [property: JsonPropertyName("detail")] string Detail);
    }
}
