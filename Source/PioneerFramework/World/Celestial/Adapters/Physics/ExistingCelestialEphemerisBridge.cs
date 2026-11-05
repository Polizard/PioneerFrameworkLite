using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>
    /// 用户批准的生产协议适配器：封装原圆轨父链及 SurfaceFrame 自转约定。
    /// 不改变船舶积分器；候选成员和区间私有，提交只交换已验证的数据。
    /// </summary>
    public sealed partial class ExistingCelestialEphemerisBridge : ICelestialPhysicsBridge, ICelestialPhysicsForecastProvider
    {
        public const string Id = "PFL.ExistingEphemeris";
        private readonly object gate = new();
        private Dictionary<CelestialInstanceId, Body> bodies = new();
        private HashSet<CelestialInstanceId> usedIds = new();
        private Dictionary<string, Entry> operations = new(StringComparer.Ordinal);
        private List<History> history = new();
        private readonly Dictionary<Guid, Prepared> tokens = new();
        private Prepared? active;
        public Guid SessionId { get; }
        public string ProviderId => Id;
        public PhysicsContractVersion ContractVersion => PhysicsContractVersion.Current;
        public PhysicsBridgeStatus Status => PhysicsBridgeStatus.Ready;
        public PhysicsReferenceFrame ReferenceFrame { get; } = new("PFL.ExistingEphemeris.inertial.v1", "existing parent hierarchy absolute origin", "+Y north; longitude zero -Z", "physical seconds; 1 legacy physics tick = 1 second");
        public PhysicsSampleStamp CommittedStamp { get; private set; }
        public ExistingCelestialEphemerisBridge(Guid sessionId, double initialSeconds = 0)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A world session is required.", nameof(sessionId));
            if (!double.IsFinite(initialSeconds) || initialSeconds < 0) throw new ArgumentOutOfRangeException(nameof(initialSeconds));
            SessionId = sessionId;
            CommittedStamp = new(sessionId, 0, 0, 0, initialSeconds, ReferenceFrame.Id, PhysicsSampleKind.Committed);
            history.Add(new(initialSeconds, initialSeconds, CommittedStamp, bodies));
        }
        public static CelestialResult<CelestialPhysicsConnection> Connect(CelestialPhysicsWorldRequest request)
        {
            var bridge = new ExistingCelestialEphemerisBridge(request.WorldSessionId, request.InitialSeconds);
            return CelestialResult.Ok(new CelestialPhysicsConnection(bridge, bridge.CommittedStamp));
        }
        public bool TryGetParameters(CelestialInstanceId id, out ExistingEphemerisParameters parameters)
        {
            lock (gate) { parameters = bodies.TryGetValue(id, out var body) ? body.Parameters : null!; return parameters != null; }
        }
        // 保留原父系公式，避免通过大绝对坐标相减引入新的舍入误差；仅限可采样的权威区间或已提交历史。
        internal CelestialResult<(DVector3 Position, DVector3 Velocity)> SampleParent(CelestialInstanceId id, double seconds, long revision,
            ICelestialPhysicsIntervalSampler? interval = null)
        {
            lock (gate)
            {
                CelestialResult<(DVector3, DVector3)> Fail(string code) => CelestialResult.Reject<(DVector3, DVector3)>(new CelestialError(code, id));
                if (!double.IsFinite(seconds)) return Fail("TimeUnavailable");
                Dictionary<CelestialInstanceId, Body>? source;
                if (interval != null)
                {
                    if (!ReferenceEquals(interval, active) || active!.Status != 0 || seconds < active.StartSeconds || seconds > active.EndSeconds)
                        return Fail("PreparedIntervalExpired");
                    source = active.Bodies;
                }
                else source = seconds > CommittedStamp.PhysicalSeconds ? null : history.LastOrDefault(frame => frame.Stamp.TopologyRevision == CommittedStamp.TopologyRevision && seconds >= frame.Start && seconds <= frame.End)?.Bodies;
                if (source == null) return Fail("TimeUnavailable");
                if (!source.TryGetValue(id, out var body)) return Fail("BodyMissing");
                if (body.Registration.Descriptor.DescriptorRevision != revision) return Fail("DescriptorRevisionConflict");
                var local = body.Parameters.SampleParent(seconds);
                return local.Position.IsFinite() && local.Velocity.IsFinite() ? CelestialResult.Ok(local) : Fail("InvalidState");
            }
        }
        public PhysicsOperation Submit(PhysicsChangeSet request)
        {
            lock (gate)
            {
                if (request.WorldSessionId != SessionId) return Rejected(request.OperationId, "StaleSession");
                if (IsOperationExpired(request.OperationId)) return Rejected(request.OperationId, "OperationExpired");
                if (request.Changes.Count == 0 || request.Changes.Any(change => change == null || !change.InstanceId.IsValid || !Enum.IsDefined(change.Kind)))
                    return Rejected(request.OperationId, "InvalidChanges");
                string hash = request.ContentHash();
                if (operations.TryGetValue(request.OperationId, out var known))
                {
                    if (known.Hash != hash) return Rejected(request.OperationId, "OperationConflict");
                    if (known.Receipt.Status == PhysicsOperationStatus.Pending)
                    {
                        var refreshed = known with { Request = request, Authorized = true };
                        operations[request.OperationId] = refreshed;
                        if (active != null && active.Operations[request.OperationId].Receipt.Status == PhysicsOperationStatus.Pending)
                            active.Operations[request.OperationId] = refreshed;
                    }
                    return known.Receipt;
                }
                if (request.ExpectedTopologyRevision != CommittedStamp.TopologyRevision) return Rejected(request.OperationId, "RevisionConflict");
                var added = new Entry(request, hash, new(request.OperationId, PhysicsOperationStatus.Pending), true);
                operations.Add(request.OperationId, added);
                active?.Operations.Add(request.OperationId, added);
                return added.Receipt;
            }
        }
        public PhysicsOperation GetOperation(string operationId)
        { lock (gate) return IsOperationExpired(operationId) ? Rejected(operationId, "OperationExpired") :
            operations.TryGetValue(operationId, out var entry) ? entry.Receipt : Rejected(operationId, "OperationMissing"); }
        public PhysicsIntervalPreparation PrepareInterval(PhysicsIntervalRequest request)
        {
            lock (gate)
            {
                if (request.WorldSessionId != SessionId) return Reject("StaleSession");
                if (request.ExpectedTopologyRevision != CommittedStamp.TopologyRevision) return Reject("RevisionConflict");
                if (request.StartSeconds != CommittedStamp.PhysicalSeconds || !double.IsFinite(request.EndSeconds) || request.EndSeconds < request.StartSeconds)
                    return Reject("TimeUnavailable");
                if (active != null) return Reject("IntervalAlreadyPrepared");
                if (request.OperationIds.Distinct(StringComparer.Ordinal).Count() != request.OperationIds.Count) return Reject("DuplicateOperation");
                var selected = new List<Entry>();
                foreach (string id in request.OperationIds)
                {
                    if (!operations.TryGetValue(id, out var entry)) return Reject("OperationMissing");
                    if (entry.Receipt.Status == PhysicsOperationStatus.Rejected) return PhysicsIntervalPreparation.Reject(entry.Receipt.Errors.ToArray());
                    if (entry.Receipt.Status == PhysicsOperationStatus.Applied) continue;
                    if (!entry.Authorized) return Reject("RestorePendingResubmission");
                    if (entry.Request.ExpectedTopologyRevision != CommittedStamp.TopologyRevision) return RejectOperations(selected.Append(entry), new CelestialError("RevisionConflict"));
                    selected.Add(entry);
                }
                var nextBodies = new Dictionary<CelestialInstanceId, Body>(bodies);
                var nextUsed = new HashSet<CelestialInstanceId>(usedIds);
                var affected = new HashSet<CelestialInstanceId>();
                foreach (var change in selected.SelectMany(entry => entry.Request.Changes))
                {
                    if (!affected.Add(change.InstanceId)) return RejectOperations(selected, new CelestialError("DuplicateBodyChange", change.InstanceId));
                    bool exists = nextBodies.TryGetValue(change.InstanceId, out var previous);
                    if (change.Kind == PhysicsBodyChangeKind.Unregister)
                    {
                        if (!exists) return RejectOperations(selected, new CelestialError("BodyMissing", change.InstanceId));
                        nextBodies.Remove(change.InstanceId);
                        continue;
                    }
                    var registration = change.Registration!;
                    if (change.Kind == PhysicsBodyChangeKind.Register)
                    {
                        if (exists || !nextUsed.Add(change.InstanceId)) return RejectOperations(selected, new CelestialError("IdentityAlreadyUsed", change.InstanceId));
                    }
                    else if (!exists || registration.DefinitionId != previous!.Registration.DefinitionId ||
                        registration.CompatibilityHandle != previous.Registration.CompatibilityHandle || registration.Descriptor.DescriptorRevision <= previous.Registration.Descriptor.DescriptorRevision)
                        return RejectOperations(selected, new CelestialError("DescriptorRevisionConflict", change.InstanceId));
                    var parsed = ExistingEphemerisParameters.Parse(registration.Initialization);
                    if (!parsed.Success) return RejectOperations(selected, parsed.Errors.Select(e => e with { InstanceId = change.InstanceId }).ToArray());
                    nextBodies[change.InstanceId] = new(registration, parsed.Value);
                }
                var graphError = ValidateGraph(nextBodies);
                if (graphError != null) return RejectOperations(selected, graphError);
                var stamp = CommittedStamp with { StateRevision = CommittedStamp.StateRevision + 1,
                    TopologyRevision = CommittedStamp.TopologyRevision + (affected.Count > 0 ? 1 : 0), PhysicalSeconds = request.EndSeconds };
                // 全体端点验证完成后才提供候选区间；任意子步溢出仍通过结构化错误返回。
                foreach (double time in new[] { request.StartSeconds, request.EndSeconds })
                {
                    var validation = Snapshot(nextBodies, stamp with { PhysicalSeconds = time }, nextBodies.Keys.ToArray(), PhysicsSampleKind.AuthoritativeInterval);
                    if (!validation.Success) return RejectOperations(selected, validation.Errors.ToArray());
                    validation.Snapshot!.Dispose();
                }
                var nextOperations = new Dictionary<string, Entry>(operations, StringComparer.Ordinal);
                foreach (var entry in selected) nextOperations[entry.Request.OperationId] = entry with {
                    Receipt = new(entry.Request.OperationId, PhysicsOperationStatus.Applied, appliedSeconds: stamp.PhysicalSeconds, topologyRevision: stamp.TopologyRevision) };
                var nextHistory = new List<History>(history);
                nextHistory.Add(new(affected.Count == 0 ? request.StartSeconds : request.EndSeconds, request.EndSeconds, stamp, nextBodies));
                // 只保留最近区间；显示查询超出保存范围必须显式失败。
                if (nextHistory.Count > 128) nextHistory.RemoveRange(0, nextHistory.Count - 128);
                var token = new PhysicsIntervalToken(Id, SessionId, Guid.NewGuid(), CommittedStamp.StateRevision);
                active = new(token, request.StartSeconds, request.EndSeconds, stamp, nextBodies, nextUsed, nextOperations, nextHistory);
                tokens.Add(token.TokenId, active);
                return PhysicsIntervalPreparation.Prepared(token, active);
            }
        }
        public void CommitPreparedInterval(PhysicsIntervalToken token)
        {
            lock (gate)
            {
                var prepared = GetToken(token);
                if (prepared.Status == 1) return;
                if (prepared.Status != 0 || active != prepared || token.ExpectedStateRevision != CommittedStamp.StateRevision)
                    throw new InvalidOperationException("Invalid prepared interval.");
                bodies = prepared.Bodies; usedIds = prepared.UsedIds; operations = prepared.Operations; history = prepared.History;
                CommittedStamp = prepared.Stamp; prepared.Status = 1; active = null;
                // 已提交 token 保留轻量幂等记录；候选图只由当前历史持有。
                prepared.ReleaseSampler();
            }
        }
        public void AbortPreparedInterval(PhysicsIntervalToken token)
        {
            lock (gate)
            {
                var prepared = GetToken(token);
                if (prepared.Status != 0) return;
                prepared.Status = 2; active = null; prepared.ReleaseSampler();
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
            lock (gate)
            {
                if (request.WorldSessionId != SessionId) return Failure("StaleSession");
                if (request.ExpectedTopologyRevision != CommittedStamp.TopologyRevision) return Failure("RevisionConflict");
                if (!Enum.IsDefined(request.Purpose)) return Failure("InvalidSamplePurpose");
                double time = request.PhysicalSeconds ?? CommittedStamp.PhysicalSeconds;
                if (!double.IsFinite(time) || time > CommittedStamp.PhysicalSeconds ||
                    request.Purpose == PhysicsSamplePurpose.SimulationCommitted && time != CommittedStamp.PhysicalSeconds) return Failure("TimeUnavailable");
                var frame = history.LastOrDefault(frame => frame.Stamp.TopologyRevision == request.ExpectedTopologyRevision && time >= frame.Start && time <= frame.End);
                if (frame == null) return Failure("TimeUnavailable");
                return Snapshot(frame.Bodies, frame.Stamp with { PhysicalSeconds = time }, request.InstanceIds,
                    time == CommittedStamp.PhysicalSeconds ? PhysicsSampleKind.Committed : PhysicsSampleKind.PresentationInterpolation);
            }
        }
        private static CelestialError? ValidateGraph(Dictionary<CelestialInstanceId, Body> candidates)
        {
            if (candidates.Values.Where(b => b.Registration.CompatibilityHandle.HasValue).GroupBy(b => b.Registration.CompatibilityHandle).Any(g => g.Count() > 1))
                return new("DuplicateCompatibilityHandle");
            foreach (var pair in candidates)
            {
                if (pair.Value.Registration.InitializationReferences.Values.Any(id => !candidates.ContainsKey(id))) return new("ReferenceTargetMissing", pair.Key);
                var seen = new HashSet<CelestialInstanceId> { pair.Key };
                var current = pair.Value;
                while (current.Registration.ParentInstanceId is { } parent)
                {
                    if (!seen.Add(parent)) return new("ParentCycle", pair.Key);
                    if (!candidates.TryGetValue(parent, out current!)) return new("ParentMissing", pair.Key);
                }
            }
            return null;
        }
        private static PhysicsSnapshotResult Snapshot(Dictionary<CelestialInstanceId, Body> source, PhysicsSampleStamp stamp,
            IReadOnlyList<CelestialInstanceId> ids, PhysicsSampleKind kind)
        {
            var states = new Dictionary<CelestialInstanceId, CelestialMotionState>();
            CelestialMotionState Evaluate(CelestialInstanceId id)
            {
                if (states.TryGetValue(id, out var known)) return known;
                var body = source[id]; var model = body.Parameters;
                var local = model.SampleParent(stamp.PhysicalSeconds);
                DVector3 position = local.Position, velocity = local.Velocity;
                if (body.Registration.ParentInstanceId is { } parent)
                {
                    var parentState = Evaluate(parent);
                    position += parentState.Position; velocity += parentState.LinearVelocity;
                }
                double omega = 2.0 * Math.PI / model.DayLengthTicks;
                double half = -omega * stamp.PhysicalSeconds * 0.5;
                var state = new CelestialMotionState(body.Registration.Descriptor.DescriptorRevision, position, velocity,
                    new DQuaternion(0, Math.Sin(half), 0, Math.Cos(half)), new DVector3(0, -omega, 0));
                states.Add(id, state); return state;
            }
            foreach (var id in ids)
            {
                if (!source.ContainsKey(id)) return PhysicsSnapshotResult.Reject(new CelestialError("BodyMissing", id));
                Evaluate(id);
            }
            var selected = ids.Distinct().ToDictionary(id => id, id => states[id]);
            return CelestialPhysicsSnapshot.Create(stamp with { SampleKind = kind }, selected.Keys.Select(id => source[id].Registration.Descriptor), selected);
        }
        private PhysicsIntervalPreparation RejectOperations(IEnumerable<Entry> selected, params CelestialError[] errors)
        {
            foreach (var entry in selected) operations[entry.Request.OperationId] = entry with { Receipt = new(entry.Request.OperationId, PhysicsOperationStatus.Rejected, errors) };
            return PhysicsIntervalPreparation.Reject(errors);
        }
        private static PhysicsIntervalPreparation Reject(string code) => PhysicsIntervalPreparation.Reject(new CelestialError(code));
        private static PhysicsSnapshotResult Failure(string code) => PhysicsSnapshotResult.Reject(new CelestialError(code));
        private static PhysicsOperation Rejected(string id, string code) => new(id, PhysicsOperationStatus.Rejected, new[] { new CelestialError(code) });
        private sealed record Body(CelestialBodyRegistration Registration, ExistingEphemerisParameters Parameters);
        private sealed record Entry(PhysicsChangeSet Request, string Hash, PhysicsOperation Receipt, bool Authorized);
        private sealed record History(double Start, double End, PhysicsSampleStamp Stamp, Dictionary<CelestialInstanceId, Body> Bodies);
        private sealed class Prepared(PhysicsIntervalToken token, double start, double end, PhysicsSampleStamp stamp,
            Dictionary<CelestialInstanceId, Body> bodies, HashSet<CelestialInstanceId> usedIds,
            Dictionary<string, Entry> operations, List<History> history) : ICelestialPhysicsIntervalSampler
        {
            public PhysicsIntervalToken Token { get; } = token;
            public double StartSeconds { get; } = start;
            public double EndSeconds { get; } = end;
            public PhysicsSampleStamp Stamp { get; } = stamp;
            public Dictionary<CelestialInstanceId, Body> Bodies { get; private set; } = bodies;
            public HashSet<CelestialInstanceId> UsedIds { get; private set; } = usedIds;
            public Dictionary<string, Entry> Operations { get; private set; } = operations;
            public List<History> History { get; private set; } = history;
            public volatile int Status;
            public PhysicsSnapshotResult Sample(double physicalSeconds, IReadOnlyList<CelestialInstanceId> ids)
            {
                var source = Bodies;
                return Status != 0 || source == null ? Failure("PreparedIntervalExpired") : !double.IsFinite(physicalSeconds) || physicalSeconds < StartSeconds || physicalSeconds > EndSeconds
                    ? Failure("TimeUnavailable") : Snapshot(source, Stamp with { PhysicalSeconds = physicalSeconds }, ids, PhysicsSampleKind.AuthoritativeInterval);
            }
            public void ReleaseSampler() { Bodies = null!; UsedIds = null!; Operations = null!; History = null!; }
        }
    }
}
