using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class PreparedCelestialStep
    {
        internal CelestialPhysicsCoordinator Owner { get; }
        internal PhysicsIntervalToken PhysicsToken { get; }
        internal IReadOnlyList<ICelestialPreparedStepPart> Parts { get; }
        internal IReadOnlyList<ICelestialPreparedStepPart> AbortOrder { get; }
        internal int Status { get; set; }
        public PhysicsSampleStamp Stamp { get; }
        public ICelestialPhysicsIntervalSampler Sampler { get; }
        internal PreparedCelestialStep(CelestialPhysicsCoordinator owner, PhysicsIntervalToken token, PhysicsSampleStamp stamp,
            ICelestialPhysicsIntervalSampler sampler, IReadOnlyList<ICelestialPreparedStepPart> parts, IReadOnlyList<ICelestialPreparedStepPart> abortOrder)
        {
            Owner = owner;
            PhysicsToken = token;
            Stamp = stamp;
            Sampler = sampler;
            Parts = parts;
            AbortOrder = abortOrder;
        }
    }
    public sealed class CelestialPhysicsCoordinator
    {
        private readonly ICelestialPhysicsBridge physics;
        private readonly object publicationGate;
        private readonly object preparationGate = new();
        private int preparing;
        private IReadOnlyList<ICelestialStepParticipant> participants;
        internal CelestialResult<bool> AttachHostParticipants(IEnumerable<ICelestialStepParticipant> additions)
        {
            lock (publicationGate)
            {
                if (active != null || System.Threading.Volatile.Read(ref preparing) != 0 || IsFaulted) return CelestialResult.Reject<bool>(new CelestialError("IntervalAlreadyPrepared"));
                var combined = participants.Concat(additions).OrderBy(participant => participant.Phase)
                    .ThenBy(participant => participant.Id, StringComparer.Ordinal).ToArray();
                if (combined.Select(participant => participant.Id).Distinct(StringComparer.Ordinal).Count() != combined.Length ||
                    combined.Any(participant => string.IsNullOrWhiteSpace(participant.Id) || !Enum.IsDefined(participant.Phase)) ||
                    combined.Count(participant => participant.Phase == CelestialStepParticipantPhase.Clock) > 1)
                    return CelestialResult.Reject<bool>(new CelestialError("DuplicateStepParticipant"));
                participants = Array.AsReadOnly(combined);
                return CelestialResult.Ok(true);
            }
        }
        private PreparedCelestialStep? active;
        public PhysicsSampleStamp CommittedStamp { get; private set; }
        public bool IsFaulted { get; private set; }
        internal void RestoreStamp(PhysicsSampleStamp stamp)
        {
            lock (publicationGate)
            {
                if (active != null || IsFaulted || stamp.WorldSessionId != CommittedStamp.WorldSessionId) throw new InvalidOperationException("Invalid restore stamp.");
                CommittedStamp = stamp;
            }
        }
        public IReadOnlyList<CelestialError> Faults { get; private set; } = Array.Empty<CelestialError>();
        public IReadOnlyList<CelestialError> RetentionDiagnostics { get; private set; } = Array.Empty<CelestialError>();
        internal void RecordRetentionFailure(Exception exception) => RetentionDiagnostics = Array.AsReadOnly(RetentionDiagnostics.TakeLast(31)
            .Append(new CelestialError("ProviderRetentionFailed", Detail: exception.Message)).ToArray());
        private void AcknowledgeTerminalToken(PhysicsIntervalToken token)
        {
            if (physics is not ICelestialPhysicsRetention retention) return;
            try { retention.AcknowledgeTerminalToken(token); }
            catch (Exception exception) { RecordRetentionFailure(exception); }
        }
        public bool HasPreparedStep => active != null || System.Threading.Volatile.Read(ref preparing) != 0;
        public CelestialPhysicsCoordinator(ICelestialPhysicsBridge physics, PhysicsSampleStamp initialStamp, object publicationGate,
            IEnumerable<ICelestialStepParticipant>? participants = null)
        {
            this.physics = physics;
            this.publicationGate = publicationGate;
            CommittedStamp = initialStamp;
            var ordered = (participants ?? Array.Empty<ICelestialStepParticipant>()).OrderBy(participant => participant.Phase)
                .ThenBy(participant => participant.Id, StringComparer.Ordinal).ToArray();
            if (ordered.Select(participant => participant.Id).Distinct(StringComparer.Ordinal).Count() != ordered.Length ||
                ordered.Any(participant => string.IsNullOrWhiteSpace(participant.Id) || !Enum.IsDefined(participant.Phase)) ||
                ordered.Count(participant => participant.Phase == CelestialStepParticipantPhase.Clock) > 1)
                throw new ArgumentException("Invalid shared-step participants.", nameof(participants));
            this.participants = Array.AsReadOnly(ordered);
        }

        public CelestialResult<PreparedCelestialStep> Prepare(PhysicsIntervalRequest request)
        {
            if (System.Threading.Interlocked.CompareExchange(ref preparing, 1, 0) != 0) return Reject("IntervalAlreadyPrepared");
            try
            {
            lock (preparationGate)
            {
                if (IsFaulted) return Reject("ConsistencyFault");
                if (active != null) return Reject("IntervalAlreadyPrepared");
                if (request.WorldSessionId != CommittedStamp.WorldSessionId) return Reject("StaleSession");
                if (request.ExpectedTopologyRevision != CommittedStamp.TopologyRevision) return Reject("RevisionConflict");
                if (request.StartSeconds != CommittedStamp.PhysicalSeconds || !double.IsFinite(request.EndSeconds) || request.EndSeconds < request.StartSeconds) return Reject("TimeUnavailable");
                if (physics.Status != PhysicsBridgeStatus.Ready) return Reject(physics.Status switch {
                    PhysicsBridgeStatus.Restoring => "ProviderRestoring", PhysicsBridgeStatus.Faulted => "ProviderFaulted", _ => "ProviderUnavailable" });
                if (physics.ContractVersion.Major != PhysicsContractVersion.Current.Major) return Reject("UnsupportedContractVersion");
                var preparedParts = new List<ICelestialPreparedStepPart>();
                var publicationParts = new List<(CelestialStepParticipantPhase Phase, string Id, ICelestialPreparedStepPart Part)>();
                PhysicsIntervalToken? token = null;
                try
                {
                    var prepared = physics.PrepareInterval(request);
                    if (prepared.Status != PhysicsPreparationStatus.Prepared)
                    {
                        AbortParts(preparedParts, null);
                        return CelestialResult.Reject<PreparedCelestialStep>(prepared.Errors.Count > 0 ? prepared.Errors.ToArray() : new[] { new CelestialError("ProviderUnavailable") });
                    }
                    token = prepared.Token;
                    if (!token.HasValue || prepared.Sampler == null) throw new InvalidOperationException("Invalid prepared provider contract.");
                    var result = prepared.Sampler.Sample(request.EndSeconds, Array.Empty<CelestialInstanceId>());
                    if (!result.Success)
                    {
                        AbortParts(preparedParts, token);
                        return CelestialResult.Reject<PreparedCelestialStep>(result.Errors.ToArray());
                    }
                    using var snapshot = result.Snapshot!;
                    var stamp = snapshot.Stamp;
                    if (stamp.WorldSessionId != request.WorldSessionId || stamp.PhysicalSeconds != request.EndSeconds ||
                        stamp.TopologyRevision < request.ExpectedTopologyRevision || stamp.StateRevision <= CommittedStamp.StateRevision ||
                        stamp.ReferenceFrameId != physics.ReferenceFrame.Id || stamp.SampleKind != PhysicsSampleKind.AuthoritativeInterval)
                        throw new InvalidOperationException("Invalid prepared sample stamp.");
                    stamp = stamp with { SampleKind = PhysicsSampleKind.Committed };
                    var context = new CelestialPreparedStepContext(request, prepared.Sampler, stamp);
                    // 时钟先准备，确保其他参与方失败时也能取消本次选中的宿主区间；时钟仍在最后发布。
                    foreach (var participant in participants.OrderByDescending(participant => participant.Phase).ThenBy(participant => participant.Id, StringComparer.Ordinal))
                    {
                        var part = participant.Prepare(context);
                        if (!part.Success)
                        {
                            AbortParts(preparedParts, token);
                            return CelestialResult.Reject<PreparedCelestialStep>(part.Errors.ToArray());
                        }
                        if (part.Value == null) throw new InvalidOperationException("Missing prepared participant.");
                        preparedParts.Add(part.Value);
                        publicationParts.Add((participant.Phase, participant.Id, part.Value));
                    }
                    active = new PreparedCelestialStep(this, token.Value, stamp, prepared.Sampler,
                        Array.AsReadOnly(publicationParts.OrderBy(part => part.Phase).ThenBy(part => part.Id, StringComparer.Ordinal).Select(part => part.Part).ToArray()), preparedParts.AsReadOnly());
                    return CelestialResult.Ok(active);
                }
                catch (Exception exception)
                {
                    AbortParts(preparedParts, token);
                    return CelestialResult.Reject<PreparedCelestialStep>(new CelestialError("StepPreparationFailed", Detail: exception.Message));
                }
            }
            }
            finally { System.Threading.Volatile.Write(ref preparing, 0); }
        }
        public void Publish(PreparedCelestialStep step)
        {
            lock (publicationGate)
            {
                if (step.Owner != this) throw new ArgumentException("Prepared step belongs to another coordinator.", nameof(step));
                if (step.Status == 1) return;
                if (IsFaulted || active != step || step.Status != 0) throw new InvalidOperationException("Invalid prepared step.");
                try
                {
                    physics.CommitPreparedInterval(step.PhysicsToken);
                    foreach (var part in step.Parts) part.Commit();
                    CommittedStamp = step.Stamp;
                    step.Status = 1;
                    active = null;
                    AcknowledgeTerminalToken(step.PhysicsToken);
                }
                catch (Exception exception)
                {
                    step.Status = 3;
                    IsFaulted = true;
                    Faults = new[] { new CelestialError("ConsistencyFault", Detail: exception.Message) };
                    throw new InvalidOperationException("Shared physical publication violated its commit contract.", exception);
                }
            }
        }
        public void Abort(PreparedCelestialStep step)
        {
            lock (publicationGate)
            {
                if (step.Owner != this) throw new ArgumentException("Prepared step belongs to another coordinator.", nameof(step));
                if (step.Status != 0) return;
                AbortParts(step.AbortOrder, step.PhysicsToken);
                step.Status = 2;
                active = null;
            }
        }
        private void AbortParts(IEnumerable<ICelestialPreparedStepPart> parts, PhysicsIntervalToken? token)
        {
            var errors = new List<CelestialError>();
            foreach (var part in parts.Reverse())
                try { part.Abort(); }
                catch (Exception exception) { errors.Add(new CelestialError("StepAbortFailed", Detail: exception.Message)); }
            if (token.HasValue)
                try { physics.AbortPreparedInterval(token.Value); AcknowledgeTerminalToken(token.Value); }
                catch (Exception exception) { errors.Add(new CelestialError("StepAbortFailed", Detail: exception.Message)); }
            if (errors.Count > 0) { IsFaulted = true; Faults = errors.AsReadOnly(); }
        }
        private static CelestialResult<PreparedCelestialStep> Reject(string code)
            => CelestialResult.Reject<PreparedCelestialStep>(new CelestialError(code));
    }
}
