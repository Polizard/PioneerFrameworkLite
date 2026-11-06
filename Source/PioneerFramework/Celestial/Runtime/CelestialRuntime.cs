using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>由 CelestialBodyManager 唯一持有的普通宿主对象。所有写入由共同边界协调，查询不推进状态。</summary>
    public sealed class CelestialRuntime : ICelestialQueryService
    {
        private readonly object publicationGate = new();
        private int advanceActive;
        private int executingCallbacks;
        internal bool IsExecutingCallbacks => System.Threading.Volatile.Read(ref executingCallbacks) != 0;
        private CelestialCommittedReadState? FrozenReadState => Coordinator.HasPreparedStep || IsExecutingCallbacks ? System.Threading.Volatile.Read(ref readState) : null;
        private long inputVersion;
        private CelestialCommittedReadState? readState;
        /// <summary>True only when the calling thread owns the publication monitor; not a global lock-state query.</summary>
        internal bool IsPublicationHeld => System.Threading.Monitor.IsEntered(publicationGate);
        internal CelestialHostWorkQueue HostWork { get; }
        internal void ProcessHostWork() => HostWork.Process();
        internal bool IsHostJobCurrent(Guid session, CelestialInstanceId id, long revision) => WithPublication(() =>
            session == WorldSessionId && !IsRestoring && Catalog.TryGetInstance(id, out var instance) && instance.Lifecycle == CelestialLifecycle.Ready && instance.Description.DescriptorRevision == revision);
        private readonly ICelestialPhysicsBridge physics;
        private readonly Dictionary<Type, object> services = new();
        private bool servicesSealed;
        private CelestialSurfaceService? surfaces;
        private CelestialMapService? maps;
        internal CelestialMapService? Maps => maps;
        internal T WithPublication<T>(Func<T> action) { lock (publicationGate) return action(); }
        private readonly CelestialRemovalResourceRegistry nativeResources;
        internal CelestialCatalog Catalog { get; } = new();
        internal CelestialOperationStore Operations { get; }
        internal CelestialReferenceIndex References { get; }
        internal CelestialCreationPipeline Creation { get; }
        internal CelestialRemovalPipeline Removal { get; }
        internal CelestialCommandQueue Commands { get; }
        internal CelestialCompScheduler Scheduler { get; }
        internal CelestialPhysicsCoordinator Coordinator { get; }
        public Guid WorldSessionId => Operations.WorldSessionId;
        public CelestialProviderDiagnostics Provider => new(physics.ProviderId, physics.ContractVersion, physics.Status, CurrentStamp) {
            Errors = Coordinator.Faults.Concat(Coordinator.RetentionDiagnostics).ToArray() };
        internal ulong WorldSeed { get; }
        internal ICelestialPhysicsBridge Physics => physics;
        internal CelestialSurfaceService? Surfaces => surfaces;
        internal IReadOnlyDictionary<string, byte[]> Extensions { get; private set; } = new Dictionary<string, byte[]>();
        internal CelestialSaveDocument? RestoreDocument { get; private set; }
        internal bool IsRestoring { get; private set; }
        internal CelestialRuntime(ICelestialPhysicsBridge physics, PhysicsSampleStamp initialStamp, ICelestialRemovalResources resources,
            ulong worldSeed, Func<string, CelestialResult<CelestialBodyTemplate>> resolveTemplate, IEnumerable<ICelestialStepParticipant>? participants = null)
            : this(physics, initialStamp, resources, worldSeed, resolveTemplate, participants, null, null) { }
        private CelestialRuntime(ICelestialPhysicsBridge physics, PhysicsSampleStamp initialStamp, ICelestialRemovalResources resources,
            ulong worldSeed, Func<string, CelestialResult<CelestialBodyTemplate>> resolveTemplate, IEnumerable<ICelestialStepParticipant>? participants,
            CelestialSaveDocument? document, CelestialCompRegistry? registry)
        {
#if !PURE_MATH
            PioneerFramework.MapGeneration.LocalMapGenerationModules.Freeze();
#endif
            this.physics = physics;
            HostWork = new(this);
            WorldSeed = worldSeed;
            RestoreDocument = document; IsRestoring = document != null;
            if (document != null) Catalog.Restore(document);
            var epochs = new CelestialOperationEpochs();
            if (document != null) { epochs.Restore(document.Epochs); Extensions = document.Extensions.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal); }
            Operations = new CelestialOperationStore(initialStamp.WorldSessionId, document?.Operations, epochs);
            References = new CelestialReferenceIndex(Catalog, initialStamp.WorldSessionId, document?.References);
            Commands = new CelestialCommandQueue(initialStamp.WorldSessionId, Catalog, document?.Commands, Operations.Epochs);
            Creation = new CelestialCreationPipeline(Catalog, physics, Operations, worldSeed, resolveTemplate, Submit, type =>
                type.IsInstanceOfType(this) ? this : services.GetValueOrDefault(type));
            Creation.Synchronize(initialStamp);
            nativeResources = new CelestialRemovalResourceRegistry(resources);
            Removal = new CelestialRemovalPipeline(Catalog, Creation, physics, Operations, References, nativeResources);
            Scheduler = new CelestialCompScheduler(Catalog, Creation);
            Coordinator = new CelestialPhysicsCoordinator(physics, initialStamp, publicationGate, participants);
            if (document != null)
            {
                Creation.RestoreAssemblies(document, registry!);
                Removal.RestoreRecords(document.Removals);
                Scheduler.Restore(document.SchedulerCursors, document.Events);
            }
        }
        internal static CelestialRuntime Restore(CelestialSaveDocument document, CelestialCompRegistry registry, ICelestialPhysicsBridge physics,
            Guid session, ICelestialRemovalResources resources, Func<string, CelestialResult<CelestialBodyTemplate>> resolveTemplate) =>
            new(physics, document.Stamp with { WorldSessionId = session }, resources, document.WorldSeed, resolveTemplate, null, document, registry);
        internal void EndRestore() => IsRestoring = false;
        internal CelestialResult<bool> RegisterService<T>(T service) where T : class
        {
            lock (publicationGate)
            {
                if (servicesSealed) return CelestialResult.Reject<bool>(new CelestialError("RegistrySealed"));
                return services.TryAdd(typeof(T), service) ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(new CelestialError("DuplicateService"));
            }
        }
        internal CelestialOperation Create(CreateBodyRequest request)
        {
            lock (publicationGate) { inputVersion++; return IsRestoring ? FrozenOperation(request.Context) : IsExecutingCallbacks ? BusyOperation(request.Context) : Commands.Contains(request.Context.OperationId) ? Conflict(request.Context.OperationId) : Creation.Create(request); }
        }
        internal CelestialOperation CreateData(CelestialCreateBatchRequest request)
        { lock (publicationGate) { inputVersion++; return IsRestoring ? FrozenOperation(new(request.Scope.OperationId, request.Scope.WorldSessionId, request.Scope.ExpectedRevision)) :
            IsExecutingCallbacks ? BusyOperation(request.Scope.ToContext()) : Commands.Contains(request.Scope.QualifiedOperationId) ? Conflict(request.Scope.QualifiedOperationId) : Creation.CreateData(request); } }
        internal CelestialOperation CreateBatch(CreateBatchRequest request)
        {
            lock (publicationGate) { inputVersion++; return IsRestoring ? FrozenOperation(request.Context) : IsExecutingCallbacks ? BusyOperation(request.Context) : Commands.Contains(request.Context.OperationId) ? Conflict(request.Context.OperationId) : Creation.CreateBatch(request); }
        }
        internal CelestialOperation Remove(RemoveBodyRequest request)
        {
            lock (publicationGate) { inputVersion++; return IsRestoring ? FrozenOperation(request.Context) : IsExecutingCallbacks ? BusyOperation(request.Context) : HostWork.HasJob(request.InstanceId) ?
                new(request.Context.OperationId, CelestialOperationStatus.Rejected, errors: new[] { new CelestialError("HostWorkPending", request.InstanceId) }) :
                Commands.Contains(request.Context.OperationId) ? Conflict(request.Context.OperationId) : Removal.Remove(request); }
        }
        internal CelestialOperation GetOperation(string operationId)
        {
            lock (publicationGate) return Commands.Contains(operationId) ? Commands.Get(operationId) : Operations.Get(operationId);
        }
        public CelestialResult<CelestialBodySnapshot> Query(CelestialInstanceId id)
        { if (FrozenReadState is { } frozen) return frozen.Query(id); lock (publicationGate) return FrozenReadState is { } current ? current.Query(id) : Creation.Query(id); }
        public IReadOnlyList<T> GetCapabilities<T>(CelestialInstanceId id) where T : class
        { if (FrozenReadState is { } frozen) return frozen.GetCapabilities<T>(id); lock (publicationGate) return FrozenReadState is { } current ? current.GetCapabilities<T>(id) : GetLiveCapabilities<T>(id); }
        internal IReadOnlyList<T> GetLiveCapabilities<T>(CelestialInstanceId id) where T : class => Catalog.TryGetInstance(id, out var instance) && instance.Lifecycle is not (CelestialLifecycle.Blocked or CelestialLifecycle.Removed) ?
            Creation.GetComponents(id).Where(component => component.IsAttached && !component.IsReleased).OfType<T>().ToArray() : Array.Empty<T>();
        public bool TryGetInitializationReference(CelestialInstanceId owner, string key, out CelestialInstanceId target)
        {
            if (FrozenReadState is { } frozen)
            { target = default; return frozen.InitializationReferences.TryGetValue(owner, out var references) && references.TryGetValue(key, out target); }
            lock (publicationGate) return Creation.GetInitializationReferences(owner).TryGetValue(key, out target);
        }
        internal void AttachConsumerResources(ICelestialRemovalResources provider)
        { lock (publicationGate) nativeResources.Add(provider); }
        internal CelestialRemovalCheck CanRemove(CelestialInstanceId id)
        {
            if (FrozenReadState != null) return new(new[] { new CelestialError("HostWorkPending", id) });
            lock (publicationGate) return FrozenReadState != null ? new(new[] { new CelestialError("HostWorkPending", id) }) : Removal.CanRemove(id);
        }
        internal CelestialResult<string> RegisterReference(CelestialReference reference)
        { lock (publicationGate) return IsRestoring ? CelestialResult.Reject<string>(new CelestialError(reference.WorldSessionId != WorldSessionId ? "StaleSession" : "RestorePending")) : References.Register(reference); }
        internal CelestialResult<bool> ReleaseReference(string referenceId)
        { lock (publicationGate) return IsRestoring ? CelestialResult.Reject<bool>(new CelestialError(referenceId.StartsWith(WorldSessionId.ToString("N") + "/", StringComparison.Ordinal) ? "RestorePending" : "StaleSession")) : References.Release(referenceId); }
        internal CelestialResult<bool> AttachSurfaces(CelestialSurfaceService service)
        {
            lock (publicationGate)
            {
                if (servicesSealed) return CelestialResult.Reject<bool>(new CelestialError("RegistrySealed"));
                if (surfaces != null) return CelestialResult.Reject<bool>(new CelestialError("SurfaceServiceAlreadyAttached"));
                surfaces = service;
                nativeResources.Add(service);
                return CelestialResult.Ok(true);
            }
        }
        internal CelestialSurfaceOperation RequestSurface(CelestialSurfaceRequest request)
        {
            lock (publicationGate)
            {
                if (IsRestoring) return new(FrozenOperation(request.Context));
                if (surfaces == null) return new CelestialSurfaceOperation(new CelestialOperation(request.Context.OperationId,
                    CelestialOperationStatus.Rejected, errors: new[] { new CelestialError("SurfaceServiceUnavailable", request.InstanceId) }));
                if (Commands.Contains(request.Context.OperationId)) return new CelestialSurfaceOperation(Conflict(request.Context.OperationId));
            }
            return surfaces!.Request(request);
        }
        internal CelestialResult<string> Submit(CelestialCommand command)
        {
            lock (publicationGate)
            {
                if (IsRestoring) return CelestialResult.Reject<string>(new CelestialError(command.WorldSessionId != WorldSessionId ? "StaleSession" : "RestorePending", command.TargetId));
                if (Operations.Contains(command.OperationId))
                    return CelestialResult.Reject<string>(new CelestialError("OperationConflict", command.TargetId));
                return Commands.Enqueue(command);
            }
        }
        internal CelestialResult<bool> AttachMaps(CelestialMapService service)
        {
            lock (publicationGate)
            {
                if (servicesSealed) return CelestialResult.Reject<bool>(new CelestialError("RegistrySealed"));
                if (maps != null) return CelestialResult.Reject<bool>(new CelestialError("MapServiceAlreadyAttached"));
                maps = service;
                nativeResources.Add(service);
                return CelestialResult.Ok(true);
            }
        }
        internal CelestialMapOperation RequestMap(CelestialMapRequest request)
        {
            lock (publicationGate)
            {
                if (IsRestoring) return new(FrozenOperation(request.Context));
                if (maps == null) return new(new CelestialOperation(request.Context.OperationId, CelestialOperationStatus.Rejected,
                    errors: new[] { new CelestialError("MapServiceUnavailable", request.Address.InstanceId) }));
                if (Commands.Contains(request.Context.OperationId)) return new(Conflict(request.Context.OperationId));
            }
            return maps!.RequestMap(request);
        }
        internal CelestialResult<bool> EnqueueEvent(Guid session, CelestialInstanceId id, CelestialEvent message, string? compKey = null)
        {
            lock (publicationGate) return session != WorldSessionId ? CelestialResult.Reject<bool>(new CelestialError("StaleSession", id)) :
                IsRestoring ? CelestialResult.Reject<bool>(new CelestialError("RestorePending", id)) : Scheduler.Enqueue(id, message, compKey);
        }
        public CelestialCatalogSnapshot CaptureCatalog()
        { if (FrozenReadState is { } frozen) return frozen.Catalog; lock (publicationGate) return FrozenReadState is { } current ? current.Catalog : Catalog.Capture(); }
        public IReadOnlyList<CelestialEpochUsage> CaptureEpochUsage() => FrozenReadState is { } frozen ? frozen.EpochUsage : WithPublication(Operations.Epochs.CaptureUsage);
        internal CelestialResult<long> OpenEpoch(Guid session) => WithPublication(() => session == WorldSessionId ? Operations.OpenEpoch() :
            CelestialResult.Reject<long>(new CelestialError("StaleSession")));
        internal CelestialResult<bool> CloseEpoch(Guid session, long epoch) => WithPublication(() => {
            if (session != WorldSessionId) return CelestialResult.Reject<bool>(new CelestialError("StaleSession"));
            if (Coordinator.HasPreparedStep || IsExecutingCallbacks || HostWork.IsBusy) return CelestialResult.Reject<bool>(new CelestialError("HostWorkPending"));
            var retired = Operations.Capture().Where(record => CelestialOperationEpochs.TryGetEpoch(record.Operation.OperationId, out long owner) && owner == epoch)
                .Select(record => record.Operation.OperationId).ToArray();
            var result = Operations.CloseEpoch(epoch);
            if (result.Success)
            {
                Commands.CompactEpoch(epoch); Removal.CompactEpoch(epoch); maps?.CompactEpoch(epoch); Creation.CompactTerminalBatches();
                foreach (var instance in Catalog.CaptureInstances().Where(instance => instance.FailedCreation && instance.OperationEpoch == epoch))
                    DiscardFailedCreation(session, instance.Id);
                if (physics is ICelestialPhysicsRetention retention)
                    try { retention.AcknowledgeClosedEpoch(Operations.Epochs.Capture(), retired); }
                    catch (Exception exception) { Coordinator.RecordRetentionFailure(exception); }
            }
            return result;
        });
        internal CelestialResult<bool> DiscardFailedCreation(Guid session, CelestialInstanceId id) => WithPublication(() => {
            if (session != WorldSessionId) return CelestialResult.Reject<bool>(new CelestialError("StaleSession", id));
            if (Coordinator.HasPreparedStep || IsExecutingCallbacks || HostWork.HasJob(id)) return CelestialResult.Reject<bool>(new CelestialError("HostWorkPending", id));
            var blockers = nativeResources.Inspect(id).Concat(References.ForBody(id).Select(reference => new CelestialError("ActiveReference", id, Detail: reference.ReferenceId)))
                .Concat(Operations.Capture().Where(record => record.Operation.Status == CelestialOperationStatus.Pending && record.Operation.InstanceIds.Contains(id))
                    .Select(record => new CelestialError("ActiveOperation", id, Detail: record.Operation.OperationId))).ToArray();
            if (blockers.Length != 0) return CelestialResult.Reject<bool>(blockers);
            var result = Creation.DiscardFailedCreation(id);
            if (result.Success) readState = null;
            return result;
        });
        internal void AbandonBootstrap() { lock (publicationGate) Creation.AbandonUncommittedWorld(); }
        public PhysicsSampleStamp CurrentStamp { get { if (FrozenReadState is { } frozen) return frozen.Stamp; lock (publicationGate) return Coordinator.CommittedStamp; } }
        internal CelestialResult<bool> RegisterCommandHandler(string kind, ICelestialCommandHandler handler)
        { lock (publicationGate) return Commands.Register(kind, handler); }
        public PhysicsSnapshotResult Sample(PhysicsSampleRequest request)
        {
            if (FrozenReadState != null) return SampleCommitted(request);
            lock (publicationGate) return SampleCommitted(request);
        }
        private PhysicsSnapshotResult SampleCommitted(PhysicsSampleRequest request) => IsRestoring ? PhysicsSnapshotResult.Reject(new CelestialError("RestorePending")) :
                Coordinator.IsFaulted ? PhysicsSnapshotResult.Reject(new CelestialError("ConsistencyFault")) :
                physics.Status != PhysicsBridgeStatus.Ready ? PhysicsSnapshotResult.Reject(new CelestialError(physics.Status switch {
                    PhysicsBridgeStatus.Restoring => "ProviderRestoring", PhysicsBridgeStatus.Faulted => "ProviderFaulted", _ => "ProviderUnavailable" })) : physics.Sample(request);
        internal CelestialResult<bool> Advance(PhysicsIntervalRequest request)
        {
            CelestialCostCounters.Advanced();
            if (System.Threading.Interlocked.CompareExchange(ref advanceActive, 1, 0) != 0)
                return CelestialResult.Reject<bool>(new CelestialError("AdvanceInProgress"));
            try
            {
                if (!HostWork.IsHostThread) return CelestialResult.Reject<bool>(new CelestialError("WrongHostThread"));
                PhysicsIntervalRequest combined;
                PhysicsSampleStamp basedOn;
                long expectedInputVersion;
                lock (publicationGate)
                {
                if (IsRestoring) return CelestialResult.Reject<bool>(new CelestialError("RestorePending"));
                Commands.Seal();
                servicesSealed = true;
                Creation.RetrySubmissions();
                Removal.RetrySubmissions();
                basedOn = Coordinator.CommittedStamp;
                expectedInputVersion = inputVersion;
                if (readState == null || readState.Catalog.Revision != Catalog.Revision || readState.FaultRevision != Creation.FaultRevision)
                    readState = CelestialCommittedReadState.Capture(this);
                System.Threading.Volatile.Write(ref readState, readState with { Stamp = basedOn, EpochUsage = Operations.Epochs.CaptureUsage() });
                combined = new PhysicsIntervalRequest(request.WorldSessionId, request.ExpectedTopologyRevision, request.StartSeconds, request.EndSeconds,
                    request.OperationIds.Concat(Creation.PendingPhysicsOperationIds).Concat(Removal.PendingPhysicsOperationIds).Distinct(StringComparer.Ordinal));
                }
                var prepared = Coordinator.Prepare(combined);
                if (!prepared.Success) return CelestialResult.Reject<bool>(prepared.Errors.ToArray());
                lock (publicationGate)
                {
                if (Coordinator.CommittedStamp != basedOn || inputVersion != expectedInputVersion)
                { Coordinator.Abort(prepared.Value); return CelestialResult.Reject<bool>(new CelestialError("PublicationConflict")); }
                Coordinator.Publish(prepared.Value);
                Creation.Synchronize(Coordinator.CommittedStamp);
                Removal.Synchronize();
                Creation.CompactTerminalBatches();
                if (readState == null || readState.Catalog.Revision != Catalog.Revision || readState.FaultRevision != Creation.FaultRevision)
                    readState = CelestialCommittedReadState.Capture(this);
                System.Threading.Volatile.Write(ref readState, readState with { Stamp = Coordinator.CommittedStamp });
                System.Threading.Volatile.Write(ref executingCallbacks, 1);
                }
                try
                {
                    Commands.Drain(this);
                    Scheduler.OnCommitted(Coordinator.CommittedStamp, this);
                }
                finally
                {
                    lock (publicationGate)
                    {
                        System.Threading.Volatile.Write(ref executingCallbacks, 0);
                        if (readState == null || readState.Catalog.Revision != Catalog.Revision || readState.FaultRevision != Creation.FaultRevision)
                            readState = CelestialCommittedReadState.Capture(this);
                        System.Threading.Volatile.Write(ref readState, readState with { Stamp = Coordinator.CommittedStamp, EpochUsage = Operations.Epochs.CaptureUsage() });
                    }
                }
                ProcessHostWork();
                return CelestialResult.Ok(true);
            }
            finally { System.Threading.Volatile.Write(ref advanceActive, 0); }
        }
        internal bool HasPendingPhysicsChanges
        {
            get { lock (publicationGate) return Creation.PendingPhysicsOperationIds.Any() || Removal.PendingPhysicsOperationIds.Any(); }
        }
        private static CelestialOperation Conflict(string operationId) => new(operationId, CelestialOperationStatus.Rejected, errors: new[] { new CelestialError("OperationConflict") });
        private CelestialOperation BusyOperation(CelestialOperationContext context) => new(context.OperationId, CelestialOperationStatus.Rejected,
            errors: new[] { new CelestialError(context.WorldSessionId == WorldSessionId ? "HostWorkPending" : "StaleSession") });
        private CelestialOperation FrozenOperation(CelestialOperationContext context) => new(context.OperationId, CelestialOperationStatus.Rejected,
            errors: new[] { new CelestialError(context.WorldSessionId != WorldSessionId ? "StaleSession" : "RestorePending") });
    }
    internal sealed class CelestialRemovalResourceRegistry(ICelestialRemovalResources primary) : ICelestialRemovalResources
    {
        private readonly List<ICelestialRemovalResources> providers = new() { primary };
        internal void Add(ICelestialRemovalResources provider) => providers.Add(provider);
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id)
        {
            var errors = new List<CelestialError>();
            foreach (var provider in providers)
                try { errors.AddRange(provider.Inspect(id)); }
                catch (Exception exception) { errors.Add(new CelestialError("ResourceCheckFailed", id, Detail: exception.Message)); }
            return errors;
        }
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id)
        {
            foreach (var provider in providers)
            {
                var result = provider.ReleaseEmptySurface(id);
                if (!result.Success) return result;
                if (!result.Value) return CelestialResult.Reject<bool>(new CelestialError("EmptyLayerCleanupFailed", id));
            }
            return CelestialResult.Ok(true);
        }
    }
}
