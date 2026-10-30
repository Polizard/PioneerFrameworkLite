#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PioneerFramework.Celestial;

namespace PioneerFramework.TestSupport.Celestial;

/// <summary>只回放测试显式提供的状态；无积分、轨道或预测算法，不编入 Mod。</summary>
public sealed class DeterministicTestPhysicsBridge : ICelestialPhysicsBridge
{
    public const string Id = "PFL.Tests.Replay";
    private readonly object sync = new();
    private readonly SortedDictionary<double, Dictionary<CelestialInstanceId, CelestialMotionState>> fixtures = new();
    private Dictionary<CelestialInstanceId, CelestialBodyRegistration> bodies = new();
    private Dictionary<string, Entry> operations = new(StringComparer.Ordinal);
    private Dictionary<CelestialInstanceId, int> registrationCounts = new();
    private HashSet<CelestialInstanceId> usedIds = new();
    private List<Frame> history = new();
    private readonly Dictionary<Guid, Prepared> tokens = new();
    private Prepared? active;
    public Guid SessionId { get; }
    public string ProviderId => Id;
    public PhysicsContractVersion ContractVersion => PhysicsContractVersion.Current;
    public bool IsAvailable { get; set; } = true;
    public PhysicsBridgeStatus Status => IsAvailable ? PhysicsBridgeStatus.Ready : PhysicsBridgeStatus.Unavailable;
    public PhysicsReferenceFrame ReferenceFrame { get; } = new("test.inertial", "fixture origin", "+Y north; longitude zero -Z", "fixture physical seconds");
    public PhysicsSampleStamp CommittedStamp { get; private set; }

    public DeterministicTestPhysicsBridge(Guid sessionId, double initialSeconds = 0)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A world session is required.", nameof(sessionId));
        if (!double.IsFinite(initialSeconds)) throw new ArgumentOutOfRangeException(nameof(initialSeconds));
        SessionId = sessionId;
        CommittedStamp = new(sessionId, 0, 0, 0, initialSeconds, ReferenceFrame.Id, PhysicsSampleKind.Committed);
        history.Add(new Frame(CommittedStamp, new(), new()));
    }

    public void SetFrame(double seconds, IReadOnlyDictionary<CelestialInstanceId, CelestialMotionState> states)
    {
        lock (sync) fixtures[seconds] = new Dictionary<CelestialInstanceId, CelestialMotionState>(states);
    }
    public int RegistrationCount(CelestialInstanceId id)
    {
        lock (sync) return registrationCounts.GetValueOrDefault(id);
    }

    public PhysicsOperation Submit(PhysicsChangeSet request)
    {
        lock (sync)
        {
            if (request.WorldSessionId != SessionId) return Rejected(request.OperationId, "StaleSession");
            if (request.Changes.Any(change => change == null || !change.InstanceId.IsValid)) return Rejected(request.OperationId, "InvalidChanges");
            string hash = request.ContentHash();
            if (operations.TryGetValue(request.OperationId, out var known))
            {
                if (known.Hash != hash) return Rejected(request.OperationId, "OperationConflict");
                if (known.Receipt.Status == PhysicsOperationStatus.Pending)
                {
                    var refreshed = known with { Request = request, Authorized = true };
                    operations[request.OperationId] = refreshed;
                    if (active != null && active.Operations.TryGetValue(request.OperationId, out var staged) && staged.Receipt.Status == PhysicsOperationStatus.Pending)
                        active.Operations[request.OperationId] = refreshed;
                }
                return known.Receipt;
            }
            if (request.ExpectedTopologyRevision != CommittedStamp.TopologyRevision) return Rejected(request.OperationId, "RevisionConflict");
            var receipt = new PhysicsOperation(request.OperationId, PhysicsOperationStatus.Pending);
            var added = new Entry(request, hash, receipt, true);
            operations.Add(request.OperationId, added);
            // 提交时只交换已准备的账本；准备期间的新请求也必须留在该账本中等待下一边界。
            active?.Operations.Add(request.OperationId, added);
            return receipt;
        }
    }
    public PhysicsOperation GetOperation(string operationId)
    {
        lock (sync) return operations.TryGetValue(operationId, out var entry) ? entry.Receipt : Rejected(operationId, "OperationMissing");
    }

    public PhysicsIntervalPreparation PrepareInterval(PhysicsIntervalRequest request)
    {
        lock (sync)
        {
            PhysicsIntervalPreparation Reject(string code) => PhysicsIntervalPreparation.Reject(new CelestialError(code));
            if (request.WorldSessionId != SessionId) return Reject("StaleSession");
            if (!IsAvailable) return PhysicsIntervalPreparation.Pending(new CelestialError("ProviderUnavailable"));
            if (request.ExpectedTopologyRevision != CommittedStamp.TopologyRevision) return Reject("RevisionConflict");
            if (!double.IsFinite(request.StartSeconds) || !double.IsFinite(request.EndSeconds) || request.StartSeconds != CommittedStamp.PhysicalSeconds || request.EndSeconds < request.StartSeconds)
                return Reject("TimeUnavailable");
            if (active != null) return Reject("IntervalAlreadyPrepared");
            if (request.OperationIds.Distinct(StringComparer.Ordinal).Count() != request.OperationIds.Count) return Reject("DuplicateOperation");
            var selected = new List<Entry>();
            foreach (string operationId in request.OperationIds)
            {
                if (!operations.TryGetValue(operationId, out var entry)) return Reject("OperationMissing");
                if (entry.Receipt.Status == PhysicsOperationStatus.Rejected) return PhysicsIntervalPreparation.Reject(entry.Receipt.Errors.ToArray());
                if (entry.Receipt.Status == PhysicsOperationStatus.Applied) continue;
                if (!entry.Authorized) return Reject("RestorePendingResubmission");
                if (entry.Request.ExpectedTopologyRevision != CommittedStamp.TopologyRevision) return RejectOperations(selected.Append(entry), "RevisionConflict");
                selected.Add(entry);
            }
            var nextBodies = new Dictionary<CelestialInstanceId, CelestialBodyRegistration>(bodies);
            var nextUsed = new HashSet<CelestialInstanceId>(usedIds);
            var nextCounts = new Dictionary<CelestialInstanceId, int>(registrationCounts);
            var affected = new HashSet<CelestialInstanceId>();
            foreach (var change in selected.SelectMany(entry => entry.Request.Changes))
            {
                if (!affected.Add(change.InstanceId)) return RejectOperations(selected, "DuplicateBodyChange");
                var registration = change.Registration;
                bool exists = nextBodies.TryGetValue(change.InstanceId, out var previous);
                if (change.Kind == PhysicsBodyChangeKind.Unregister)
                {
                    if (!exists) return RejectOperations(selected, "BodyMissing");
                    nextBodies.Remove(change.InstanceId);
                    continue;
                }
                if (registration == null || registration.Initialization.ProviderId != Id || registration.Initialization.ModeId != "replay" || registration.Initialization.Version != 1)
                    return RejectOperations(selected, "UnsupportedInitialization");
                if (change.Kind == PhysicsBodyChangeKind.Register)
                {
                    if (exists || !nextUsed.Add(change.InstanceId)) return RejectOperations(selected, "IdentityAlreadyUsed");
                    nextCounts[change.InstanceId] = nextCounts.GetValueOrDefault(change.InstanceId) + 1;
                }
                else if (!exists || registration.DefinitionId != previous!.DefinitionId || registration.Descriptor.DescriptorRevision <= previous.Descriptor.DescriptorRevision)
                    return RejectOperations(selected, "RevisionConflict");
                nextBodies[change.InstanceId] = registration;
            }
            foreach (var registration in nextBodies.Values)
                if ((registration.ParentInstanceId.HasValue && !nextBodies.ContainsKey(registration.ParentInstanceId.Value)) ||
                    registration.InitializationReferences.Values.Any(id => !nextBodies.ContainsKey(id))) return RejectOperations(selected, "MissingReference");
            long nextTopology = CommittedStamp.TopologyRevision + (affected.Count > 0 ? 1 : 0);
            var stamp = CommittedStamp with { StateRevision = CommittedStamp.StateRevision + 1, TopologyRevision = nextTopology, PhysicalSeconds = request.EndSeconds };
            var preparedFrames = new SortedDictionary<double, Frame>();
            if (!fixtures.ContainsKey(request.StartSeconds) || !fixtures.ContainsKey(request.EndSeconds))
                return PhysicsIntervalPreparation.Pending(new CelestialError("TimeUnavailable"));
            foreach (var fixture in fixtures.Where(pair => pair.Key >= request.StartSeconds && pair.Key <= request.EndSeconds))
            {
                var states = new Dictionary<CelestialInstanceId, CelestialMotionState>();
                foreach (var id in nextBodies.Keys)
                {
                    if (!fixture.Value.TryGetValue(id, out var state)) return PhysicsIntervalPreparation.Pending(new CelestialError("TimeUnavailable", id));
                    states.Add(id, state);
                }
                var frame = new Frame(stamp with { PhysicalSeconds = fixture.Key }, nextBodies, states);
                var validation = Snapshot(frame, nextBodies.Keys.ToArray(), PhysicsSampleKind.AuthoritativeInterval);
                if (!validation.Success) return RejectOperations(selected, validation.Errors.ToArray());
                validation.Snapshot!.Dispose();
                preparedFrames.Add(fixture.Key, frame);
            }
            var nextOperations = new Dictionary<string, Entry>(operations, StringComparer.Ordinal);
            foreach (var entry in selected)
                nextOperations[entry.Request.OperationId] = entry with {
                    Receipt = new PhysicsOperation(entry.Request.OperationId, PhysicsOperationStatus.Applied, appliedSeconds: stamp.PhysicalSeconds, topologyRevision: stamp.TopologyRevision) };
            var nextHistory = new List<Frame>(history);
            // 拓扑变化只在应用端点生效；稳定成员区间可保留测试显式提供的中间帧。
            if (affected.Count == 0) nextHistory.AddRange(preparedFrames.Values);
            else nextHistory.Add(preparedFrames[request.EndSeconds]);
            var token = new PhysicsIntervalToken(Id, SessionId, Guid.NewGuid(), CommittedStamp.StateRevision);
            active = new Prepared(token, request.StartSeconds, request.EndSeconds, stamp, nextBodies, nextUsed, nextCounts, nextOperations, nextHistory, preparedFrames);
            tokens.Add(token.TokenId, active);
            return PhysicsIntervalPreparation.Prepared(token, active);
        }
    }

    public void CommitPreparedInterval(PhysicsIntervalToken token)
    {
        lock (sync)
        {
            var prepared = GetToken(token);
            if (prepared.Status == 1) return;
            if (prepared.Status != 0 || active != prepared || CommittedStamp.StateRevision != token.ExpectedStateRevision)
                throw new InvalidOperationException("Invalid prepared commit.");
            bodies = prepared.Bodies;
            usedIds = prepared.UsedIds;
            registrationCounts = prepared.Counts;
            operations = prepared.Operations;
            history = prepared.History;
            CommittedStamp = prepared.Stamp;
            prepared.Status = 1;
            active = null;
        }
    }
    public void AbortPreparedInterval(PhysicsIntervalToken token)
    {
        lock (sync)
        {
            var prepared = GetToken(token);
            if (prepared.Status != 0) return;
            prepared.Status = 2;
            active = null;
        }
    }
    private Prepared GetToken(PhysicsIntervalToken token)
    {
        if (token.ProviderId != Id || token.WorldSessionId != SessionId || !tokens.TryGetValue(token.TokenId, out var prepared) || prepared.Token != token)
            throw new InvalidOperationException("Invalid interval token.");
        return prepared;
    }

    public PhysicsSnapshotResult Sample(PhysicsSampleRequest request)
    {
        lock (sync)
        {
            if (request.WorldSessionId != SessionId) return Failure("StaleSession");
            if (!IsAvailable) return Failure("ProviderUnavailable");
            if (request.ExpectedTopologyRevision != CommittedStamp.TopologyRevision) return Failure("RevisionConflict");
            if (!Enum.IsDefined(request.Purpose)) return Failure("InvalidSamplePurpose");
            double time = request.PhysicalSeconds ?? CommittedStamp.PhysicalSeconds;
            if (!double.IsFinite(time) || time > CommittedStamp.PhysicalSeconds ||
                (request.Purpose == PhysicsSamplePurpose.SimulationCommitted && time != CommittedStamp.PhysicalSeconds)) return Failure("TimeUnavailable");
            var frame = history.LastOrDefault(frame => frame.Stamp.PhysicalSeconds == time && frame.Stamp.TopologyRevision == request.ExpectedTopologyRevision);
            return frame == null ? Failure("TimeUnavailable") : Snapshot(frame, request.InstanceIds, PhysicsSampleKind.Committed);
        }
    }
    private static PhysicsSnapshotResult Snapshot(Frame frame, IReadOnlyList<CelestialInstanceId> ids, PhysicsSampleKind kind)
    {
        var states = new Dictionary<CelestialInstanceId, CelestialMotionState>();
        foreach (var id in ids)
        {
            if (!frame.States.TryGetValue(id, out var state)) return PhysicsSnapshotResult.Reject(new CelestialError("BodyMissing", id));
            states[id] = state;
        }
        return CelestialPhysicsSnapshot.Create(frame.Stamp with { SampleKind = kind }, states.Keys.Select(id => frame.Bodies[id].Descriptor), states);
    }
    private static PhysicsSnapshotResult Failure(string code) => PhysicsSnapshotResult.Reject(new CelestialError(code));
    private static PhysicsOperation Rejected(string id, string code) => new(id, PhysicsOperationStatus.Rejected, new[] { new CelestialError(code) });
    private PhysicsIntervalPreparation RejectOperations(IEnumerable<Entry> entries, string code)
        => RejectOperations(entries, new[] { new CelestialError(code) });
    private PhysicsIntervalPreparation RejectOperations(IEnumerable<Entry> entries, CelestialError[] errors)
    {
        foreach (var entry in entries)
            operations[entry.Request.OperationId] = entry with { Receipt = new PhysicsOperation(entry.Request.OperationId, PhysicsOperationStatus.Rejected, errors) };
        return PhysicsIntervalPreparation.Reject(errors);
    }

    public PhysicsCheckpoint CaptureCheckpoint()
    {
        lock (sync)
        {
            if (active != null || !IsAvailable) throw new InvalidOperationException("CheckpointUnavailable");
            var data = new SaveData {
                Bodies = bodies.Values.Select(ToData).ToArray(),
                States = history[^1].States.ToDictionary(pair => pair.Key.Value, pair => ToData(pair.Value), StringComparer.Ordinal),
                UsedIds = usedIds.Select(id => id.Value).ToArray(),
                Counts = registrationCounts.ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal),
                Operations = operations.Values.Select(entry => new OperationData { Id = entry.Request.OperationId, Hash = entry.Hash,
                    Status = entry.Receipt.Status, AppliedSeconds = entry.Receipt.AppliedSeconds, TopologyRevision = entry.Receipt.TopologyRevision,
                    Errors = entry.Receipt.Errors.Select(error => new ErrorData { Code = error.Code, InstanceId = error.InstanceId?.Value, CompKey = error.CompKey, Detail = error.Detail }).ToArray(),
                    Changes = entry.Request.Changes.Select(change => new ChangeData { Kind = change.Kind, Id = change.InstanceId.Value,
                        Registration = change.Registration == null ? null : ToData(change.Registration) }).ToArray() }).ToArray()
            };
            return new PhysicsCheckpoint(Id, ContractVersion, 1, Guid.NewGuid().ToString("N"), CommittedStamp, JsonSerializer.SerializeToUtf8Bytes(data));
        }
    }
    public PhysicsRestoreResult RestoreCheckpoint(PhysicsCheckpoint checkpoint, IReadOnlyList<CelestialBodyRegistration> committedBodies)
    {
        lock (sync)
        {
            if (checkpoint.ProviderId != Id) return PhysicsRestoreResult.Reject(new CelestialError("ProviderUnavailable"));
            if (checkpoint.ContractVersion != ContractVersion || checkpoint.PayloadVersion != 1) return PhysicsRestoreResult.Reject(new CelestialError("UnsupportedContractVersion"));
            if (active != null) return PhysicsRestoreResult.Reject(new CelestialError("IntervalAlreadyPrepared"));
            try
            {
                var data = JsonSerializer.Deserialize<SaveData>(checkpoint.Payload.Span) ?? throw new InvalidOperationException();
                var restoredBodies = data.Bodies.Select(FromData).ToDictionary(body => body.InstanceId);
                string savedHash = new PhysicsChangeSet("verify", SessionId, 0, restoredBodies.Values.Select(PhysicsBodyChange.Register)).ContentHash();
                string suppliedHash = new PhysicsChangeSet("verify", SessionId, 0, committedBodies.Select(PhysicsBodyChange.Register)).ContentHash();
                if (savedHash != suppliedHash) return PhysicsRestoreResult.Reject(new CelestialError("RestoreMismatch"));
                var states = data.States.ToDictionary(pair => CelestialInstanceId.Parse(pair.Key), pair => FromData(pair.Value));
                var stamp = checkpoint.Stamp with { WorldSessionId = SessionId, ClockRevision = checkpoint.Stamp.ClockRevision + 1, SampleKind = PhysicsSampleKind.Committed };
                var validation = CelestialPhysicsSnapshot.Create(stamp, restoredBodies.Values.Select(body => body.Descriptor), states);
                if (!validation.Success) return PhysicsRestoreResult.Reject(validation.Errors.ToArray());
                validation.Snapshot!.Dispose();
                var restoredOperations = new Dictionary<string, Entry>(StringComparer.Ordinal);
                foreach (var operation in data.Operations)
                {
                    var changes = operation.Changes.Select(change => change.Kind switch {
                        PhysicsBodyChangeKind.Register => PhysicsBodyChange.Register(FromData(change.Registration!)),
                        PhysicsBodyChangeKind.Update => PhysicsBodyChange.Update(FromData(change.Registration!)),
                        PhysicsBodyChangeKind.Unregister => PhysicsBodyChange.Unregister(CelestialInstanceId.Parse(change.Id)),
                        _ => throw new InvalidOperationException() }).ToArray();
                    var request = new PhysicsChangeSet(operation.Id, SessionId, stamp.TopologyRevision, changes);
                    if (request.ContentHash() != operation.Hash) throw new InvalidOperationException();
                    restoredOperations.Add(operation.Id, new Entry(request, operation.Hash, new PhysicsOperation(operation.Id, operation.Status,
                        operation.Errors.Select(error => new CelestialError(error.Code, error.InstanceId == null ? null : CelestialInstanceId.Parse(error.InstanceId), error.CompKey, error.Detail)), operation.AppliedSeconds, operation.TopologyRevision), false));
                }
                var restoredUsed = data.UsedIds.Select(CelestialInstanceId.Parse).ToHashSet();
                if (restoredBodies.Keys.Any(id => !restoredUsed.Contains(id))) return PhysicsRestoreResult.Reject(new CelestialError("RestoreMismatch"));
                var restoredCounts = data.Counts.ToDictionary(pair => CelestialInstanceId.Parse(pair.Key), pair => pair.Value);
                bodies = restoredBodies;
                usedIds = restoredUsed;
                registrationCounts = restoredCounts;
                operations = restoredOperations;
                CommittedStamp = stamp;
                history = new List<Frame> { new(stamp, restoredBodies, states) };
                tokens.Clear();
                return PhysicsRestoreResult.Ok(stamp);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException or NullReferenceException)
            { return PhysicsRestoreResult.Reject(new CelestialError("RestoreMismatch", Detail: exception.Message)); }
        }
    }

    private sealed record Entry(PhysicsChangeSet Request, string Hash, PhysicsOperation Receipt, bool Authorized);
    private sealed record Frame(PhysicsSampleStamp Stamp, Dictionary<CelestialInstanceId, CelestialBodyRegistration> Bodies,
        Dictionary<CelestialInstanceId, CelestialMotionState> States);
    private sealed class Prepared(PhysicsIntervalToken token, double start, double end, PhysicsSampleStamp stamp,
        Dictionary<CelestialInstanceId, CelestialBodyRegistration> bodies, HashSet<CelestialInstanceId> usedIds,
        Dictionary<CelestialInstanceId, int> counts, Dictionary<string, Entry> operations, List<Frame> history,
        SortedDictionary<double, Frame> frames) : ICelestialPhysicsIntervalSampler
    {
        public PhysicsIntervalToken Token { get; } = token;
        public double StartSeconds { get; } = start;
        public double EndSeconds { get; } = end;
        public PhysicsSampleStamp Stamp { get; } = stamp;
        public Dictionary<CelestialInstanceId, CelestialBodyRegistration> Bodies { get; } = bodies;
        public HashSet<CelestialInstanceId> UsedIds { get; } = usedIds;
        public Dictionary<CelestialInstanceId, int> Counts { get; } = counts;
        public Dictionary<string, Entry> Operations { get; } = operations;
        public List<Frame> History { get; } = history;
        public volatile int Status;
        public PhysicsSnapshotResult Sample(double physicalSeconds, IReadOnlyList<CelestialInstanceId> ids)
            => Status != 0 ? Failure("PreparedIntervalExpired") : !frames.TryGetValue(physicalSeconds, out var frame)
                ? Failure("TimeUnavailable") : Snapshot(frame, ids, PhysicsSampleKind.AuthoritativeInterval);
    }

    private static RegistrationData ToData(CelestialBodyRegistration body) => new() {
        Id = body.InstanceId.Value, Definition = body.DefinitionId, Revision = body.Descriptor.DescriptorRevision,
        Radius = body.Descriptor.RadiusMeters, GM = body.Descriptor.GravitationalParameter,
        Provider = body.Initialization.ProviderId, Mode = body.Initialization.ModeId, Version = body.Initialization.Version,
        Payload = body.Initialization.Payload.ToArray(), Parent = body.ParentInstanceId?.Value, Handle = body.CompatibilityHandle,
        References = body.InitializationReferences.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal) };
    private static CelestialBodyRegistration FromData(RegistrationData body) => new(body.Definition,
        new CelestialPhysicsDescriptor(CelestialInstanceId.Parse(body.Id), body.Revision, body.Radius, body.GM),
        new PhysicsInitialization(body.Provider, body.Mode, body.Version, body.Payload), body.Parent == null ? null : CelestialInstanceId.Parse(body.Parent),
        body.References.ToDictionary(pair => pair.Key, pair => CelestialInstanceId.Parse(pair.Value), StringComparer.Ordinal), body.Handle);
    private static StateData ToData(CelestialMotionState state) => new() { Revision = state.DescriptorRevision,
        Position = new[] { state.Position.x, state.Position.y, state.Position.z }, Velocity = new[] { state.LinearVelocity.x, state.LinearVelocity.y, state.LinearVelocity.z },
        Orientation = new[] { state.Orientation.x, state.Orientation.y, state.Orientation.z, state.Orientation.w },
        AngularVelocity = new[] { state.AngularVelocity.x, state.AngularVelocity.y, state.AngularVelocity.z }, Valid = state.IsValid };
    private static CelestialMotionState FromData(StateData state)
    {
        if (state.Position.Length != 3 || state.Velocity.Length != 3 || state.Orientation.Length != 4 || state.AngularVelocity.Length != 3)
            throw new InvalidOperationException("Invalid checkpoint vector dimensions.");
        return new(state.Revision,
        new DVector3(state.Position[0], state.Position[1], state.Position[2]), new DVector3(state.Velocity[0], state.Velocity[1], state.Velocity[2]),
        new DQuaternion(state.Orientation[0], state.Orientation[1], state.Orientation[2], state.Orientation[3]),
        new DVector3(state.AngularVelocity[0], state.AngularVelocity[1], state.AngularVelocity[2]), state.Valid);
    }
    private sealed class SaveData
    {
        public RegistrationData[] Bodies { get; set; } = Array.Empty<RegistrationData>();
        public Dictionary<string, StateData> States { get; set; } = new();
        public string[] UsedIds { get; set; } = Array.Empty<string>();
        public Dictionary<string, int> Counts { get; set; } = new();
        public OperationData[] Operations { get; set; } = Array.Empty<OperationData>();
    }
    private sealed class RegistrationData
    {
        public string Id { get; set; } = "";
        public string Definition { get; set; } = "";
        public long Revision { get; set; }
        public double Radius { get; set; }
        public double? GM { get; set; }
        public string Provider { get; set; } = "";
        public string Mode { get; set; } = "";
        public int Version { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public string? Parent { get; set; }
        public int? Handle { get; set; }
        public Dictionary<string, string> References { get; set; } = new();
    }
    private sealed class StateData
    {
        public long Revision { get; set; }
        public double[] Position { get; set; } = Array.Empty<double>();
        public double[] Velocity { get; set; } = Array.Empty<double>();
        public double[] Orientation { get; set; } = Array.Empty<double>();
        public double[] AngularVelocity { get; set; } = Array.Empty<double>();
        public bool Valid { get; set; }
    }
    private sealed class ChangeData
    {
        public PhysicsBodyChangeKind Kind { get; set; }
        public string Id { get; set; } = "";
        public RegistrationData? Registration { get; set; }
    }
    private sealed class OperationData
    {
        public string Id { get; set; } = "";
        public string Hash { get; set; } = "";
        public PhysicsOperationStatus Status { get; set; }
        public double? AppliedSeconds { get; set; }
        public long? TopologyRevision { get; set; }
        public ErrorData[] Errors { get; set; } = Array.Empty<ErrorData>();
        public ChangeData[] Changes { get; set; } = Array.Empty<ChangeData>();
    }
    private sealed class ErrorData
    {
        public string Code { get; set; } = "";
        public string? InstanceId { get; set; }
        public string? CompKey { get; set; }
        public string Detail { get; set; } = "";
    }
}
