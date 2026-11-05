using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PioneerFramework.Celestial
{
    /// <summary>宿主安全边界内执行。查询只读；物理 Applied 后核对快照，再整体发布实例目录。</summary>
    public sealed partial class CelestialCreationPipeline
    {
        private readonly CelestialCatalog catalog;
        private readonly ICelestialPhysicsBridge physics;
        private readonly CelestialOperationStore operations;
        private readonly ulong worldSeed;
        private readonly Func<string, CelestialResult<CelestialBodyTemplate>> resolveTemplate;
        private readonly Func<CelestialCommand, CelestialResult<string>> submit;
        private readonly Func<Type, object?> resolveService;
        private readonly Dictionary<string, Batch> batches = new(StringComparer.Ordinal);
        private readonly Dictionary<CelestialInstanceId, Assembly> assemblies = new();
        public Guid WorldSessionId => operations.WorldSessionId;
        public long PhysicsTopologyRevision { get; private set; }
        public double PhysicalSeconds { get; private set; }
        public CelestialInstanceId? RootSurfaceId { get; internal set; }
        public IReadOnlyList<string> PendingPhysicsOperationIds => Array.AsReadOnly(batches.Values
            .Where(batch => operations.Get(batch.OperationId).Status == CelestialOperationStatus.Pending && batch.HasPhysics)
            .Select(batch => batch.CompensationId ?? batch.PhysicsId)
            .Concat(FrozenCompensations.Select(batch => batch.CompensationId!))
            .Where(id => CelestialPhysicsRetries.Get(physics, id).Status == PhysicsOperationStatus.Pending).OrderBy(id => id, StringComparer.Ordinal).ToArray());

        public CelestialCreationPipeline(CelestialCatalog catalog, ICelestialPhysicsBridge physics, CelestialOperationStore operations,
            ulong worldSeed, Func<string, CelestialResult<CelestialBodyTemplate>> resolveTemplate,
            Func<CelestialCommand, CelestialResult<string>>? submit = null, Func<Type, object?>? resolveService = null)
        {
            this.catalog = catalog;
            this.physics = physics;
            this.operations = operations;
            this.worldSeed = worldSeed;
            this.resolveTemplate = resolveTemplate;
            this.submit = submit ?? (_ => CelestialResult.Reject<string>(new CelestialError("CommandQueueUnavailable")));
            this.resolveService = resolveService ?? (_ => null);
        }

        public CelestialOperation Create(CreateBodyRequest request)
        {
            if (request.Context.WorldSessionId != WorldSessionId) return Reject(request.Context.OperationId, "StaleSession");
            if (string.IsNullOrWhiteSpace(request.GenerationKey) || string.IsNullOrWhiteSpace(request.DefinitionId)) return Reject(request.Context.OperationId, "InvalidCreationRequest");
            try
            {
                var template = resolveTemplate(request.DefinitionId);
                if (!template.Success) return Rejected(request.Context.OperationId, template.Errors);
                template = template.Value.WithOverrides(request.Overrides);
                if (!template.Success) return Rejected(request.Context.OperationId, template.Errors);
                string path = "operation/" + Uri.EscapeDataString(request.Context.OperationId) + "/" + Uri.EscapeDataString(request.GenerationKey) + "[0]";
                ulong seed = request.Seed ?? CelestialSeed.Derive(worldSeed, path, "$create", "seed");
                var resolved = template.Value.Resolve(new CelestialGenerationContext(seed, path));
                if (!resolved.Success) return Rejected(request.Context.OperationId, resolved.Errors);
                var item = new CelestialSpawnItem(CelestialInstanceId.Parse("dynamic:" + path), request.GenerationKey, 0, path, template.Value, resolved.Value, request.ParentId);
                return CreateBatch(new CreateBatchRequest(request.Context, new CelestialSpawnManifest("dynamic", seed, null, new[] { item })));
            }
            catch (Exception exception) { return Rejected(request.Context.OperationId, new[] { new CelestialError("CreationResolutionFailed", Detail: exception.Message) }); }
        }

        public CelestialOperation CreateBatch(CreateBatchRequest request)
        {
            string operationId = request.Context.OperationId;
            if (request.Context.WorldSessionId != WorldSessionId) return Reject(operationId, "StaleSession");
            string hash;
            try { hash = ContentHash(request.Manifest); }
            catch (Exception exception) { return Rejected(operationId, new[] { new CelestialError("InvalidManifest", Detail: exception.Message) }); }
            var ticket = operations.Begin(request.Context, hash, catalog.Revision, request.Manifest.Items.Select(item => item.Id));
            if (!ticket.Success) return Rejected(operationId, ticket.Errors);
            if (!ticket.Value.IsNew) return ticket.Value.Operation;
            CelestialResult<bool> validation;
            try { validation = Validate(request.Manifest); }
            catch (Exception exception) { validation = CelestialResult.Reject<bool>(new CelestialError("ComponentValidationFailed", Detail: exception.Message)); }
            if (!validation.Success)
            {
                var rejected = new CelestialOperation(operationId, CelestialOperationStatus.Rejected, ticket.Value.Operation.InstanceIds, validation.Errors);
                operations.Update(rejected);
                return rejected;
            }
            var batch = new Batch(operationId, request.Manifest);
            batches.Add(operationId, batch);
            try
            {
                foreach (var item in request.Manifest.Items)
                {
                    var geometry = item.Components.Select(component => component.Properties).OfType<ICelestialGeometryProperties>().Single();
                    var physical = item.Components.Select(component => component.Properties).OfType<ICelestialPhysicsBindingProperties>().SingleOrDefault();
                    var description = new CelestialBodyDescription(item.Id, item.Template.DefinitionId, catalog.AllocateHandle(), 1,
                        item.Template.Category, geometry.RadiusMeters, physical?.GravitationalParameter, item.Template.Tags);
                    var instance = new CelestialBodyInstance(description, new CelestialGenerationOrigin(request.Manifest.WorldSeed, item.GenerationPath)) { ParentId = item.ParentId };
                    CelestialOperationEpochs.TryGetEpoch(operationId, out long operationEpoch); instance.OperationEpoch = operationEpoch;
                    instance.RootSurfaceCandidate = request.Manifest.RootSurfaceId == item.Id;
                    batch.Assemblies.Add(new Assembly(instance, item));
                }
                var staged = catalog.StageBatch(batch.Assemblies.Select(assembly => assembly.Instance).ToArray());
                if (!staged.Success) return Fail(batch, staged.Errors);
                foreach (var assembly in batch.Assemblies)
                {
                    assemblies.Add(assembly.Instance.Id, assembly);
                    foreach (var resolved in assembly.Resolved)
                    {
                        assembly.Instance.SetCompDescriptor(resolved.Descriptor);
                        assembly.Instance.SetCompRecord(new CelestialCompSaveRecord(resolved.Descriptor.Key, resolved.Descriptor.HandlerId,
                            resolved.Properties.Version, resolved.Descriptor.SchemaVersion, CelestialCostCounters.EncodeProperties(resolved.Properties).Span, ReadOnlySpan<byte>.Empty));
                        var component = resolved.Factory.Create(resolved.Properties) ?? throw new InvalidOperationException("Component factory returned null.");
                        assembly.Components.Add(component);
                        component.BindDescriptor(resolved.Descriptor);
                        var contract = CelestialCapabilityRegistry.Global.Validate(component);
                        if (!contract.Success) return Fail(batch, contract.Errors);
                    }
                }
                foreach (var assembly in batch.Assemblies)
                {
                    foreach (var component in assembly.Components)
                    {
                        if (component.IsDisabled) continue;
                        var properties = assembly.Resolved.Single(resolved => resolved.Descriptor.Key == component.Descriptor.Key).Properties;
                        try { component.Attach(new Context(this, assembly, properties)); }
                        catch (Exception exception) { HandleComponentFault(assembly.Instance, component, "ComponentAttachFailed", exception); }
                        if (assembly.Instance.Lifecycle == CelestialLifecycle.Blocked) return Fail(batch, assembly.Instance.Errors);
                    }
                    Capture(assembly);
                    if (assembly.Instance.Lifecycle == CelestialLifecycle.Blocked) return Fail(batch, assembly.Instance.Errors);
                    var bindings = assembly.Components.OfType<ICelestialPhysicsBinding>().ToArray();
                    bool declared = assembly.Spawn.Components.Any(resolved => resolved.Descriptor.Provides.Contains("physics.binding", StringComparer.Ordinal));
                    if (bindings.Length != (declared ? 1 : 0)) return Fail(batch, new[] { new CelestialError("HandlerContractViolation", assembly.Instance.Id) });
                    if (bindings.Length == 1)
                    {
                        var registration = bindings[0].CreateRegistration(assembly.Instance.Description, assembly.Spawn.ParentId, assembly.Spawn.InitializationReferences);
                        if (!registration.Success) return Fail(batch, registration.Errors);
                        if (!Matches(assembly.Instance.Description, registration.Value.Descriptor) || registration.Value.InstanceId != assembly.Instance.Id ||
                            registration.Value.DefinitionId != assembly.Instance.Description.DefinitionId || registration.Value.ParentInstanceId != assembly.Spawn.ParentId ||
                            registration.Value.InitializationReferences.Count != assembly.Spawn.InitializationReferences.Count ||
                            assembly.Spawn.InitializationReferences.Any(pair => !registration.Value.InitializationReferences.TryGetValue(pair.Key, out var target) || target != pair.Value))
                            return Fail(batch, new[] { new CelestialError("InvalidRegistration", assembly.Instance.Id) });
                        assembly.Registration = registration.Value;
                    }
                }
                batch.HasPhysics = batch.Assemblies.Any(assembly => assembly.Registration != null);
                foreach (var assembly in batch.Assemblies) assembly.Instance.Lifecycle = CelestialLifecycle.Registering;
                if (batch.HasPhysics)
                {
                    if (physics.Status != PhysicsBridgeStatus.Ready)
                    {
                        foreach (var assembly in batch.Assemblies) assembly.Instance.Errors = new[] { new CelestialError("ProviderUnavailable", assembly.Instance.Id) };
                        return operations.Get(operationId);
                    }
                    try
                    {
                        var receipt = physics.Submit(new PhysicsChangeSet(batch.PhysicsId, WorldSessionId, PhysicsTopologyRevision,
                            batch.Assemblies.Where(assembly => assembly.Registration != null).Select(assembly => PhysicsBodyChange.Register(assembly.Registration!))));
                        if (CelestialPhysicsRetries.IsTransient(receipt))
                            foreach (var assembly in batch.Assemblies) assembly.Instance.Errors = receipt.Errors;
                        else if (receipt.Status == PhysicsOperationStatus.Rejected) return Fail(batch, receipt.Errors);
                    }
                    catch (Exception exception)
                    {
                        foreach (var assembly in batch.Assemblies) assembly.Instance.Errors = new[] { new CelestialError("ProviderSubmissionFailed", assembly.Instance.Id, Detail: exception.Message) };
                    }
                }
                return operations.Get(operationId);
            }
            catch (Exception exception) { return Fail(batch, new[] { new CelestialError("ComponentAssemblyFailed", Detail: exception.Message) }); }
        }

        public CelestialOperation GetOperation(string operationId) => operations.Get(operationId);
        public CelestialResult<CelestialBodySnapshot> Query(CelestialInstanceId id)
        {
            if (!catalog.TryGetInstance(id, out var instance)) return CelestialResult.Reject<CelestialBodySnapshot>(new CelestialError("BodyMissing", id));
            return CelestialResult.Ok(new CelestialBodySnapshot(instance.Description, instance.Lifecycle, instance.CommittedMembership,
                instance.ParentId, instance.CompStateRecords.Keys, instance.Errors));
        }

        public void Synchronize(PhysicsSampleStamp? committedStamp = null)
        {
            if (committedStamp.HasValue)
            {
                if (committedStamp.Value.WorldSessionId != WorldSessionId) throw new InvalidOperationException("Stale committed physics session.");
                PhysicsTopologyRevision = committedStamp.Value.TopologyRevision;
                PhysicalSeconds = committedStamp.Value.PhysicalSeconds;
            }
            SynchronizeFrozenCompensations();
            foreach (var batch in batches.Values.OrderBy(batch => batch.OperationId, StringComparer.Ordinal))
            {
                if (operations.Get(batch.OperationId).Status != CelestialOperationStatus.Pending) continue;
                if (batch.CompensationId != null) { FinishCompensation(batch); continue; }
                if (batch.HasPhysics)
                {
                    var receipt = CelestialPhysicsRetries.Get(physics, batch.PhysicsId);
                    if (CelestialPhysicsRetries.IsMissingOrTransient(receipt)) continue;
                    if (receipt.Status == PhysicsOperationStatus.Rejected) { Fail(batch, receipt.Errors); continue; }
                    if (receipt.Status != PhysicsOperationStatus.Applied) continue;
                    PhysicsTopologyRevision = Math.Max(PhysicsTopologyRevision, receipt.TopologyRevision ?? PhysicsTopologyRevision);
                    foreach (var assembly in batch.Assemblies.Where(assembly => assembly.Registration != null)) assembly.Instance.CommittedMembership = true;
                    var sample = physics.Sample(new PhysicsSampleRequest(WorldSessionId, PhysicsTopologyRevision,
                        batch.Assemblies.Where(assembly => assembly.Registration != null).Select(assembly => assembly.Instance.Id)));
                    if (!sample.Success)
                    {
                        foreach (var assembly in batch.Assemblies) assembly.Instance.Errors = sample.Errors;
                        continue;
                    }
                    using var snapshot = sample.Snapshot!;
                    bool valid = batch.Assemblies.Where(assembly => assembly.Registration != null).All(assembly =>
                        snapshot.TryGetBodyDescriptor(assembly.Instance.Id, out var descriptor) && Matches(assembly.Instance.Description, descriptor) &&
                        snapshot.TryGetBodyState(assembly.Instance.Id, out var state) && state.DescriptorRevision == descriptor.DescriptorRevision);
                    if (!valid) { Compensate(batch, new[] { new CelestialError("DescriptorStateMismatch") }); continue; }
                    PhysicalSeconds = snapshot.Stamp.PhysicalSeconds;
                }
                try
                {
                    foreach (var assembly in batch.Assemblies)
                    {
                        foreach (var component in assembly.Components.Where(component => component.IsAttached))
                        {
                            try { component.OnReady(); }
                            catch (Exception exception) { HandleComponentFault(assembly.Instance, component, "ComponentReadyFailed", exception); }
                            if (assembly.Instance.Lifecycle == CelestialLifecycle.Blocked) throw new InvalidOperationException("ComponentReadyFailed");
                        }
                        Capture(assembly);
                    }
                }
                catch (Exception exception)
                {
                    Compensate(batch, batch.Assemblies.SelectMany(assembly => assembly.Instance.Errors)
                        .Append(new CelestialError("ComponentReadyFailed", Detail: exception.Message)).Distinct().ToArray());
                    continue;
                }
                var published = catalog.Publish(new CelestialCatalogChangeSet(catalog.Revision, batch.Assemblies.Select(assembly => assembly.Instance.Description)));
                if (!published.Success) { Compensate(batch, published.Errors); continue; }
                if (batch.Manifest.RootSurfaceId.HasValue) RootSurfaceId = batch.Manifest.RootSurfaceId;
                foreach (var assembly in batch.Assemblies) assembly.Instance.Errors = assembly.Instance.Errors
                    .Where(error => error.Code is not ("ProviderUnavailable" or "ProviderRestoring" or "ProviderSubmissionFailed")).ToArray();
                operations.Update(new CelestialOperation(batch.OperationId, CelestialOperationStatus.Applied, batch.Assemblies.Select(assembly => assembly.Instance.Id), appliedRevision: published.Value.Revision));
            }
        }

        private CelestialResult<bool> Validate(CelestialSpawnManifest manifest)
        {
            CelestialResult<bool> RejectValidation(string code, CelestialInstanceId? id = null) => CelestialResult.Reject<bool>(new CelestialError(code, id));
            if (manifest.Items.Count == 0) return RejectValidation("EmptyManifest");
            var items = new Dictionary<CelestialInstanceId, CelestialSpawnItem>();
            foreach (var item in manifest.Items)
            {
                if (!items.TryAdd(item.Id, item)) return RejectValidation("DuplicateInstanceId", item.Id);
                if (catalog.TryGetInstance(item.Id, out _)) return RejectValidation("IdentityReserved", item.Id);
                var graph = CelestialCompGraph.Resolve(item.Components.Select(component => component.Descriptor).ToArray());
                if (!graph.Success) return CelestialResult.Reject<bool>(graph.Errors.ToArray());
                if (item.Components.Count(component => component.Properties is ICelestialGeometryProperties) != 1) return RejectValidation("InvalidGeometry", item.Id);
                if (item.Components.Count(component => component.Descriptor.Provides.Contains("geometry", StringComparer.Ordinal)) != 1 ||
                    item.Components.Count(component => component.Descriptor.Provides.Contains("surface", StringComparer.Ordinal)) > 1 ||
                    item.Components.Count(component => component.Descriptor.Provides.Contains("physics.binding", StringComparer.Ordinal)) > 1)
                    return RejectValidation("InvalidCapabilityProviders", item.Id);
                foreach (var component in item.Components)
                    if (component.Properties.Key != component.Descriptor.Key || component.Properties.HandlerId != component.Descriptor.HandlerId || component.Properties.Version < 1)
                        return RejectValidation("HandlerContractViolation", item.Id);
                var geometry = item.Components.Single(component => component.Descriptor.Provides.Contains("geometry", StringComparer.Ordinal)).Properties as ICelestialGeometryProperties;
                if (geometry == null || !double.IsFinite(geometry.RadiusMeters) || geometry.RadiusMeters <= 0) return RejectValidation("InvalidGeometry", item.Id);
                if (item.Components.Count(component => component.Properties is ICelestialPhysicsBindingProperties) !=
                    item.Components.Count(component => component.Descriptor.Provides.Contains("physics.binding", StringComparer.Ordinal)))
                    return RejectValidation("HandlerContractViolation", item.Id);
                foreach (var component in item.Components)
                {
                    if (component.Properties is not ICelestialResolvedConfigurationValidator validator) continue;
                    var consistent = validator.Validate(Array.AsReadOnly(item.Components.Select(value => value.Properties).ToArray()));
                    if (!consistent.Success) return CelestialResult.Reject<bool>(consistent.Errors.ToArray());
                    if (!consistent.Value) return RejectValidation("InconsistentParameters", item.Id);
                }
            }
            foreach (var item in manifest.Items)
            {
                var chain = new HashSet<CelestialInstanceId> { item.Id };
                CelestialInstanceId? parentId = item.ParentId;
                while (parentId.HasValue)
                {
                    if (!chain.Add(parentId.Value)) return RejectValidation("ParentInstanceCycle", item.Id);
                    if (items.TryGetValue(parentId.Value, out var parent)) parentId = parent.ParentId;
                    else
                    {
                        if (!catalog.TryGetInstance(parentId.Value, out var existing) || existing.Lifecycle != CelestialLifecycle.Ready)
                            return RejectValidation("ParentUnavailable", item.Id);
                        break;
                    }
                }
                foreach (var target in item.InitializationReferences.Values)
                    if (!items.ContainsKey(target) && (!catalog.TryGetInstance(target, out var existing) || existing.Lifecycle != CelestialLifecycle.Ready))
                        return RejectValidation("ReferenceUnavailable", item.Id);
            }
            if (manifest.RootSurfaceId.HasValue)
            {
                if (RootSurfaceId.HasValue || batches.Values.Any(batch => batch.Manifest.RootSurfaceId.HasValue &&
                    operations.Get(batch.OperationId).Status != CelestialOperationStatus.Rejected)) return RejectValidation("RootAlreadyBound");
                if (!items.TryGetValue(manifest.RootSurfaceId.Value, out var root) || !root.Components.Any(component => component.Descriptor.Provides.Contains("surface", StringComparer.Ordinal)))
                    return RejectValidation("InvalidRootSurface");
            }
            return CelestialResult.Ok(true);
        }

        private static bool Matches(CelestialBodyDescription description, CelestialPhysicsDescriptor descriptor)
            => description.Id == descriptor.Id && description.DescriptorRevision == descriptor.DescriptorRevision &&
                description.RadiusMeters == descriptor.RadiusMeters && description.GravitationalParameter == descriptor.GravitationalParameter;
        private void Capture(Assembly assembly)
        {
            foreach (var component in assembly.Components)
            {
                if (component.IsReleased || component.Failure != null) continue;
                try { assembly.Instance.SetCompRecord(CelestialCompStateCodec.Encode(component,
                    assembly.Resolved.Single(resolved => resolved.Descriptor.Key == component.Descriptor.Key).Properties)); }
                catch (Exception exception) { HandleComponentFault(assembly.Instance, component, "ComponentCaptureFailed", exception); }
            }
        }
        private void Compensate(Batch batch, IReadOnlyList<CelestialError> errors)
        {
            if (!batch.Assemblies.Any(assembly => assembly.Instance.CommittedMembership)) { Fail(batch, errors); return; }
            batch.Failure = Array.AsReadOnly(errors.ToArray());
            batch.CompensationId = "physics/compensate/" + Uri.EscapeDataString(batch.OperationId);
            foreach (var assembly in batch.Assemblies)
            {
                assembly.Instance.Lifecycle = CelestialLifecycle.Blocked;
                assembly.Instance.FailedCreation = true;
                assembly.Instance.Errors = batch.Failure;
            }
            operations.Update(new CelestialOperation(batch.OperationId, CelestialOperationStatus.Pending,
                batch.Assemblies.Select(assembly => assembly.Instance.Id), errors, relatedOperationId: batch.CompensationId));
            try { physics.Submit(new PhysicsChangeSet(batch.CompensationId, WorldSessionId, PhysicsTopologyRevision,
                batch.Assemblies.Where(assembly => assembly.Instance.CommittedMembership).Select(assembly => PhysicsBodyChange.Unregister(assembly.Instance.Id)))); }
            catch (Exception exception) { foreach (var assembly in batch.Assemblies) assembly.Instance.Errors = batch.Failure.Append(
                new CelestialError("ProviderSubmissionFailed", assembly.Instance.Id, Detail: exception.Message)).ToArray(); }
        }
        private void FinishCompensation(Batch batch)
        {
            var receipt = CelestialPhysicsRetries.Get(physics, batch.CompensationId!);
            if (receipt.Status == PhysicsOperationStatus.Pending || CelestialPhysicsRetries.IsMissingOrTransient(receipt)) return;
            if (receipt.Status == PhysicsOperationStatus.Applied)
            {
                foreach (var assembly in batch.Assemblies) assembly.Instance.CommittedMembership = false;
                Fail(batch, batch.Failure);
            }
            else
            {
                // 已注册成员仍需保留，不能在补偿失败时释放它们持有的资源。
                var errors = batch.Failure.Concat(receipt.Errors).Append(new CelestialError("CompensationFailed")).ToArray();
                foreach (var assembly in batch.Assemblies) assembly.Instance.Errors = errors;
                operations.Update(new CelestialOperation(batch.OperationId, CelestialOperationStatus.Pending,
                    batch.Assemblies.Select(assembly => assembly.Instance.Id), errors, relatedOperationId: batch.CompensationId));
            }
        }
        private CelestialOperation Fail(Batch batch, IEnumerable<CelestialError> errors)
        {
            FaultRevision++;
            var failures = new List<CelestialError>(errors);
            foreach (var assembly in batch.Assemblies.AsEnumerable().Reverse())
            {
                foreach (var component in assembly.Components.AsEnumerable().Reverse())
                {
                    try { component.Release(); }
                    catch (Exception exception) { failures.Add(new CelestialError("ComponentReleaseFailed", assembly.Instance.Id, component.Descriptor.Key, exception.Message)); }
                }
                assembly.Instance.Lifecycle = CelestialLifecycle.Blocked;
                assembly.Instance.FailedCreation = true;
                assembly.Components.Clear();
            }
            var copied = Array.AsReadOnly(failures.ToArray());
            foreach (var assembly in batch.Assemblies) assembly.Instance.Errors = copied;
            var rejected = new CelestialOperation(batch.OperationId, CelestialOperationStatus.Rejected,
                batch.Assemblies.Select(assembly => assembly.Instance.Id), copied, relatedOperationId: batch.CompensationId);
            operations.Update(rejected);
            return rejected;
        }

        internal IReadOnlyList<CelestialBodyComp> GetComponents(CelestialInstanceId id)
            => assemblies.TryGetValue(id, out var assembly) ? assembly.Components.AsReadOnly() : Array.Empty<CelestialBodyComp>();
        internal CelestialBodyRegistration? GetRegistration(CelestialInstanceId id)
            => assemblies.TryGetValue(id, out var assembly) ? assembly.Registration : null;
        internal string GetLabel(CelestialInstanceId id)
            => assemblies.TryGetValue(id, out var assembly) ? assembly.Spawn.Template.Label :
                unavailableBodies.TryGetValue(id, out var unavailable) ? unavailable.Assembly?.Label ?? unavailable.Description.DefinitionId : id.Value;
        internal IReadOnlyList<ICelestialResolvedProperties> GetProperties(CelestialInstanceId id)
            => assemblies.TryGetValue(id, out var assembly) ? Array.AsReadOnly(assembly.Resolved.Select(component => component.Properties).ToArray()) : Array.Empty<ICelestialResolvedProperties>();
        internal void AbandonUncommittedWorld()
        {
            foreach (var assembly in assemblies.Values.Where(assembly => !assembly.Instance.CommittedMembership && assembly.Instance.Lifecycle != CelestialLifecycle.Removed))
            {
                assembly.Instance.Lifecycle = CelestialLifecycle.Blocked;
                var errors = new List<CelestialError> { new("BootstrapAborted", assembly.Instance.Id) };
                foreach (var component in assembly.Components.AsEnumerable().Reverse())
                {
                    try { component.Release(); }
                    catch (Exception exception) { errors.Add(new CelestialError("ComponentReleaseFailed", assembly.Instance.Id, component.Descriptor.Key, exception.Message)); }
                }
                assembly.Instance.Errors = errors.AsReadOnly();
            }
            foreach (var batch in batches.Values.Where(batch => operations.Get(batch.OperationId).Status == CelestialOperationStatus.Pending &&
                batch.Assemblies.All(assembly => !assembly.Instance.CommittedMembership)))
                operations.Update(new CelestialOperation(batch.OperationId, CelestialOperationStatus.Rejected,
                    batch.Assemblies.Select(assembly => assembly.Instance.Id), new[] { new CelestialError("BootstrapAborted") }));
        }
        private static CelestialOperation Reject(string operationId, string code) => Rejected(operationId, new[] { new CelestialError(code) });
        private static CelestialOperation Rejected(string operationId, IEnumerable<CelestialError> errors)
            => new(operationId, CelestialOperationStatus.Rejected, errors: errors);

        private static string ContentHash(CelestialSpawnManifest manifest)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write("PFL.Celestial.Create.v2");
            writer.Write(manifest.WorldSeed);
            writer.Write(manifest.SystemDefinitionId);
            writer.Write(manifest.RootSurfaceId?.Value ?? "");
            writer.Write(manifest.Items.Count);
            foreach (var item in manifest.Items.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
            {
                writer.Write(item.Id.Value);
                writer.Write(item.EntryKey);
                writer.Write(item.Index);
                writer.Write(item.GenerationPath);
                writer.Write(item.ParentId?.Value ?? "");
                writer.Write(item.Template.DefinitionId);
                // Display labels are excluded from logical identity (localisation may change).
                writer.Write(item.Template.Category);
                writer.Write(item.Template.Tags.Count);
                foreach (string tag in item.Template.Tags.OrderBy(tag => tag, StringComparer.Ordinal)) writer.Write(tag);
                writer.Write(item.InitializationReferences.Count);
                foreach (var pair in item.InitializationReferences.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { writer.Write(pair.Key); writer.Write(pair.Value.Value); }
                writer.Write(item.Components.Count);
                foreach (var component in item.Components.OrderBy(component => component.Descriptor.Key, StringComparer.Ordinal))
                {
                    var descriptor = component.Descriptor;
                    writer.Write(descriptor.Key);
                    writer.Write(descriptor.HandlerId);
                    writer.Write(descriptor.SchemaVersion);
                    writer.Write((int)descriptor.FailurePolicy);
                    writer.Write(descriptor.ExclusiveSlot ?? "");
                    foreach (var list in new[] { descriptor.Provides, descriptor.Requires, descriptor.Before, descriptor.After })
                    {
                        writer.Write(list.Count);
                        foreach (string value in list.OrderBy(value => value, StringComparer.Ordinal)) writer.Write(value);
                    }
                    writer.Write((int)descriptor.UpdatePolicy.Mode);
                    writer.Write(descriptor.UpdatePolicy.IntervalSeconds);
                    writer.Write(component.Properties.Version);
                    var payload = CelestialCostCounters.EncodeProperties(component.Properties);
                    writer.Write(payload.Length);
                    writer.Write(payload.Span);
                }
            }
            writer.Flush();
            return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        }

        private sealed class Assembly(CelestialBodyInstance instance, CelestialSpawnItem spawn)
        {
            public CelestialBodyInstance Instance { get; } = instance;
            public CelestialSpawnItem Spawn { get; } = spawn;
            public IReadOnlyList<CelestialResolvedComp> Resolved { get; } = OrderComponents(spawn.Components);
            public List<CelestialBodyComp> Components { get; } = new();
            public CelestialBodyRegistration? Registration { get; set; }
        }
        private static ReadOnlyCollection<CelestialResolvedComp> OrderComponents(IReadOnlyList<CelestialResolvedComp> components)
        {
            var byKey = components.ToDictionary(component => component.Descriptor.Key, StringComparer.Ordinal);
            return Array.AsReadOnly(CelestialCompGraph.Resolve(components.Select(component => component.Descriptor).ToArray()).Value
                .Select(descriptor => byKey[descriptor.Key]).ToArray());
        }
        private sealed class Batch(string operationId, CelestialSpawnManifest manifest)
        {
            public string OperationId { get; } = operationId;
            public string PhysicsId { get; } = "physics/create/" + Uri.EscapeDataString(operationId);
            public CelestialSpawnManifest Manifest { get; } = manifest;
            public List<Assembly> Assemblies { get; } = new();
            public bool HasPhysics { get; set; }
            public string? CompensationId { get; set; }
            public IReadOnlyList<CelestialError> Failure { get; set; } = Array.Empty<CelestialError>();
        }
        private sealed class Context(CelestialCreationPipeline owner, Assembly assembly, ICelestialResolvedProperties properties) : ICelestialCompContext
        {
            public CelestialInstanceId InstanceId => assembly.Instance.Id;
            public Guid WorldSessionId => owner.WorldSessionId;
            public long CatalogRevision => owner.catalog.Revision;
            public long DescriptorRevision => assembly.Instance.Description.DescriptorRevision;
            public ICelestialResolvedProperties Properties => properties;
            public double PhysicalSeconds => owner.PhysicalSeconds;
            public long OperationEpoch => assembly.Instance.OperationEpoch;
            public IReadOnlyList<T> GetCapabilities<T>() where T : class => assembly.Instance.Lifecycle is CelestialLifecycle.Blocked or CelestialLifecycle.Removed ? Array.Empty<T>() :
                assembly.Components.Where(component => component.IsAttached && !component.IsDisabled && !component.IsReleased).OfType<T>().ToArray();
            public bool TryGetService<T>(out T service) where T : class
            {
                service = owner.resolveService(typeof(T)) as T ?? null!;
                return service != null;
            }
            public bool TryGetCapability<T>(out T capability) where T : class
            {
                var matches = GetCapabilities<T>();
                capability = matches.Count == 1 ? matches[0] : null!;
                return matches.Count == 1;
            }
            public CelestialResult<string> Submit(CelestialCommand command)
            {
                if (command.TargetId != InstanceId || command.WorldSessionId != owner.WorldSessionId)
                    return CelestialResult.Reject<string>(new CelestialError("InvalidCommandContext", InstanceId, properties.Key));
                if (!assembly.Components.Any(component => component.Descriptor.Key == properties.Key && component.IsAttached))
                    return CelestialResult.Reject<string>(new CelestialError("ComponentUnavailable", InstanceId, properties.Key));
                if (!command.OperationId.StartsWith("epoch/", StringComparison.Ordinal))
                {
                    if (assembly.Instance.Lifecycle != CelestialLifecycle.Registering)
                        return CelestialResult.Reject<string>(new CelestialError("OperationScopeRequired", InstanceId, properties.Key));
                    if (OperationEpoch != 0) command = new(command.Kind, CelestialOperationEpochs.Qualify(OperationEpoch, command.OperationId), command.TargetId,
                        command.WorldSessionId, command.ExpectedRevision, command.Payload.Span);
                }
                return owner.submit(command);
            }
        }
    }
}
