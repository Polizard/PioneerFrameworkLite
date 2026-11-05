using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>移除预检、提供者注销及空层清理；不确定结果保留原 ID 重试，终态控制记录随显式代次关闭回收。</summary>
    public sealed class CelestialRemovalPipeline
    {
        private readonly CelestialCatalog catalog;
        private readonly CelestialCreationPipeline creation;
        private readonly ICelestialPhysicsBridge physics;
        private readonly CelestialOperationStore operations;
        private readonly CelestialReferenceIndex references;
        private readonly ICelestialRemovalResources resources;
        private readonly Dictionary<string, CelestialRemovalRecord> removals = new(StringComparer.Ordinal);
        public CelestialRemovalPipeline(CelestialCatalog catalog, CelestialCreationPipeline creation, ICelestialPhysicsBridge physics,
            CelestialOperationStore operations, CelestialReferenceIndex references, ICelestialRemovalResources resources)
        {
            this.catalog = catalog;
            this.creation = creation;
            this.physics = physics;
            this.operations = operations;
            this.references = references;
            this.resources = resources;
        }
        public IReadOnlyList<string> PendingPhysicsOperationIds => Array.AsReadOnly(removals.Values
            .Where(record => record.Phase == CelestialRemovalPhase.AwaitingPhysics && record.PhysicsOperationId != null &&
                operations.Get(record.OperationId).Status == CelestialOperationStatus.Pending && Check(record.InstanceId, record.OperationId, true).Allowed &&
                CelestialPhysicsRetries.Get(physics, record.PhysicsOperationId).Status == PhysicsOperationStatus.Pending)
            .Select(record => record.PhysicsOperationId!).OrderBy(id => id, StringComparer.Ordinal).ToArray());

        public CelestialRemovalCheck CanRemove(CelestialInstanceId id) => Check(id, null, false);
        private CelestialRemovalCheck Check(CelestialInstanceId id, string? ownOperationId, bool allowRemoving, bool checkComponents = true)
        {
            var blockers = new List<CelestialError>();
            if (!catalog.TryGetInstance(id, out var instance)) return new CelestialRemovalCheck(new[] { new CelestialError("BodyMissing", id) });
            if (instance.Lifecycle != CelestialLifecycle.Ready && !(allowRemoving && instance.Lifecycle == CelestialLifecycle.Removing))
                blockers.Add(new CelestialError(instance.Lifecycle == CelestialLifecycle.Removing ? "BodyRemoving" : "BodyNotReady", id));
            if (creation.RootSurfaceId == id) blockers.Add(new CelestialError("RootSurfaceProtected", id));
            foreach (var child in catalog.CaptureInstances().Where(body => body.ParentId == id && body.Lifecycle != CelestialLifecycle.Removed))
                blockers.Add(new CelestialError("ChildBodyExists", id, Detail: child.Id.Value));
            foreach (var dependent in catalog.CaptureInstances().Where(body => body.Id != id && body.Lifecycle != CelestialLifecycle.Removed))
                foreach (var reference in creation.GetInitializationReferences(dependent.Id).Where(reference => reference.Value == id))
                    blockers.Add(new CelestialError("InitializationReference", id, Detail: dependent.Id.Value + ":" + reference.Key));
            foreach (var reference in references.ForBody(id))
                blockers.Add(new CelestialError(reference.Kind switch {
                    CelestialReferenceKind.Map => "MapExists",
                    CelestialReferenceKind.WorldObject => "WorldObjectExists",
                    CelestialReferenceKind.Ship => "ShipAttached",
                    CelestialReferenceKind.Megastructure => "MegastructureAttached",
                    CelestialReferenceKind.Transport => "TransportActive",
                    CelestialReferenceKind.ExternalPhysics => "ExternalPhysicsAttachment",
                    _ => "ExtensionReference" }, id, Detail: reference.ReferenceId + ": " + reference.OwnerId));
            foreach (var entry in operations.Capture().Where(entry => entry.Operation.Status == CelestialOperationStatus.Pending &&
                entry.Operation.OperationId != ownOperationId && entry.Operation.InstanceIds.Contains(id)))
                blockers.Add(new CelestialError("ActiveOperation", id, Detail: entry.Operation.OperationId));
            try { blockers.AddRange(resources.Inspect(id)); }
            catch (Exception exception) { blockers.Add(new CelestialError("ResourceCheckFailed", id, Detail: exception.Message)); }
            if (checkComponents)
                foreach (var component in creation.GetComponents(id).Where(component => component.IsAttached))
                {
                    try { blockers.AddRange(component.CanRemove().Blockers); }
                    catch (Exception exception) { blockers.Add(new CelestialError("ComponentRemovalCheckFailed", id, component.Descriptor.Key, exception.Message)); }
                }
            return new CelestialRemovalCheck(blockers);
        }

        public CelestialOperation Remove(RemoveBodyRequest request)
        {
            var ticket = operations.Begin(request.Context, "PFL.Celestial.Remove.v1:" + request.InstanceId.Value,
                catalog.Revision, new[] { request.InstanceId });
            if (!ticket.Success) return new CelestialOperation(request.Context.OperationId, CelestialOperationStatus.Rejected, errors: ticket.Errors);
            if (!ticket.Value.IsNew) return ticket.Value.Operation;
            var check = Check(request.InstanceId, request.Context.OperationId, false);
            if (!check.Allowed) return Reject(request.Context.OperationId, request.InstanceId, check.Blockers);
            var instance = catalog.CaptureInstances().Single(body => body.Id == request.InstanceId);
            instance.Lifecycle = CelestialLifecycle.Removing;
            string? physicsId = instance.CommittedMembership ? "physics/remove/" + Uri.EscapeDataString(request.Context.OperationId) : null;
            var record = new CelestialRemovalRecord(request.Context.OperationId, request.InstanceId, physicsId,
                physicsId == null ? CelestialRemovalPhase.Cleanup : CelestialRemovalPhase.AwaitingPhysics);
            removals.Add(record.OperationId, record);
            if (physicsId != null)
            {
                if (physics.Status != PhysicsBridgeStatus.Ready)
                { Pending(record, new[] { new CelestialError("ProviderUnavailable", request.InstanceId) }); return operations.Get(record.OperationId); }
                try
                {
                    var receipt = physics.Submit(new PhysicsChangeSet(physicsId, operations.WorldSessionId, creation.PhysicsTopologyRevision,
                        new[] { PhysicsBodyChange.Unregister(request.InstanceId) }));
                    if (CelestialPhysicsRetries.IsTransient(receipt)) { Pending(record, receipt.Errors); return operations.Get(record.OperationId); }
                    if (receipt.Status == PhysicsOperationStatus.Rejected)
                    {
                        instance.Lifecycle = CelestialLifecycle.Ready;
                        return Reject(record.OperationId, record.InstanceId, receipt.Errors);
                    }
                }
                catch (Exception exception)
                {
                    Pending(record, new[] { new CelestialError("ProviderSubmissionFailed", request.InstanceId, Detail: exception.Message) });
                }
            }
            return operations.Get(record.OperationId);
        }
        public CelestialOperation GetOperation(string operationId) => operations.Get(operationId);
        internal void RetrySubmissions()
        {
            if (physics.Status != PhysicsBridgeStatus.Ready) return;
            foreach (var record in removals.Values.Where(record => record.Phase == CelestialRemovalPhase.AwaitingPhysics &&
                operations.Get(record.OperationId).Status == CelestialOperationStatus.Pending).ToArray())
            {
                var check = Check(record.InstanceId, record.OperationId, true);
                if (!check.Allowed) { Pending(record, check.Blockers); continue; }
                if (!CelestialPhysicsRetries.IsMissingOrTransient(CelestialPhysicsRetries.Get(physics, record.PhysicsOperationId!))) continue;
                try
                {
                    var receipt = physics.Submit(new(record.PhysicsOperationId!, operations.WorldSessionId, creation.PhysicsTopologyRevision,
                        new[] { PhysicsBodyChange.Unregister(record.InstanceId) }));
                    if (CelestialPhysicsRetries.IsTransient(receipt)) Pending(record, receipt.Errors);
                    else if (receipt.Status == PhysicsOperationStatus.Rejected)
                    {
                        catalog.TryGetInstance(record.InstanceId, out var instance); instance.Lifecycle = CelestialLifecycle.Ready;
                        Reject(record.OperationId, record.InstanceId, receipt.Errors);
                    }
                }
                catch (Exception exception) { Pending(record, new[] { new CelestialError("ProviderSubmissionFailed", record.InstanceId, Detail: exception.Message) }); }
            }
        }
        internal void CompactEpoch(long epoch)
        {
            foreach (var id in removals.Keys) if (CelestialOperationEpochs.TryGetEpoch(id, out long owner) && owner == epoch) removals.Remove(id);
        }
        public IReadOnlyList<CelestialRemovalRecord> Capture() => Array.AsReadOnly(removals.Values.OrderBy(record => record.OperationId, StringComparer.Ordinal).ToArray());
        internal void RestoreRecords(IEnumerable<CelestialRemovalRecord> records)
        { foreach (var record in records) removals.Add(record.OperationId, record); }
        internal CelestialResult<bool> ResumeRestoredRemovals()
        {
            var errors = new List<CelestialError>();
            foreach (var record in Capture().Where(record => record.Phase == CelestialRemovalPhase.AwaitingPhysics && operations.Get(record.OperationId).Status == CelestialOperationStatus.Pending))
            {
                if (!Check(record.InstanceId, record.OperationId, true).Allowed) continue;
                var receipt = physics.Submit(new PhysicsChangeSet(record.PhysicsOperationId!, operations.WorldSessionId, creation.PhysicsTopologyRevision,
                    new[] { PhysicsBodyChange.Unregister(record.InstanceId) }));
                if (receipt.Status == PhysicsOperationStatus.Rejected) errors.AddRange(receipt.Errors);
            }
            return errors.Count == 0 ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(errors.ToArray());
        }

        public void Synchronize()
        {
            foreach (var original in Capture())
            {
                if (operations.Get(original.OperationId).Status != CelestialOperationStatus.Pending) continue;
                var record = original;
                if (!catalog.TryGetInstance(record.InstanceId, out var instance))
                {
                    Reject(record.OperationId, record.InstanceId, new[] { new CelestialError("BodyMissing", record.InstanceId) });
                    continue;
                }
                if (record.Phase == CelestialRemovalPhase.AwaitingPhysics)
                {
                    var receipt = CelestialPhysicsRetries.Get(physics, record.PhysicsOperationId!);
                    if (CelestialPhysicsRetries.IsMissingOrTransient(receipt)) { Pending(record, receipt.Errors); continue; }
                    if (receipt.Status == PhysicsOperationStatus.Pending)
                    {
                        var check = Check(record.InstanceId, record.OperationId, true);
                        if (!check.Allowed) Pending(record, check.Blockers);
                        continue;
                    }
                    if (receipt.Status == PhysicsOperationStatus.Rejected)
                    {
                        instance.Lifecycle = CelestialLifecycle.Ready;
                        Reject(record.OperationId, record.InstanceId, receipt.Errors);
                        continue;
                    }
                    instance.CommittedMembership = false;
                    record = record with { Phase = CelestialRemovalPhase.Cleanup };
                    removals[record.OperationId] = record;
                }
                var cleanupCheck = Check(record.InstanceId, record.OperationId, true, false);
                if (!cleanupCheck.Allowed) { PendingCleanup(record, cleanupCheck.Blockers); continue; }
                var errors = new List<CelestialError>();
                try
                {
                    var cleanup = resources.ReleaseEmptySurface(record.InstanceId);
                    if (!cleanup.Success) errors.AddRange(cleanup.Errors);
                    else if (!cleanup.Value) errors.Add(new CelestialError("EmptyLayerCleanupFailed", record.InstanceId));
                }
                catch (Exception exception) { errors.Add(new CelestialError("EmptyLayerCleanupFailed", record.InstanceId, Detail: exception.Message)); }
                if (errors.Count > 0) { PendingCleanup(record, errors); continue; }
                foreach (var component in creation.GetComponents(record.InstanceId).Reverse())
                {
                    try { component.Release(); }
                    catch (Exception exception) { errors.Add(new CelestialError("ComponentReleaseFailed", record.InstanceId, component.Descriptor.Key, exception.Message)); }
                }
                if (errors.Count > 0) { PendingCleanup(record, errors); continue; }
                var published = catalog.Publish(new CelestialCatalogChangeSet(catalog.Revision, Array.Empty<CelestialBodyDescription>(), new[] { record.InstanceId }));
                if (!published.Success) { PendingCleanup(record, published.Errors); continue; }
                removals[record.OperationId] = record with { Phase = CelestialRemovalPhase.Completed };
                creation.RetireRemovedInstance(record.InstanceId);
                instance.Errors = Array.Empty<CelestialError>();
                operations.Update(new CelestialOperation(record.OperationId, CelestialOperationStatus.Applied, new[] { record.InstanceId }, appliedRevision: published.Value.Revision));
            }
        }
        private CelestialOperation Reject(string operationId, CelestialInstanceId id, IEnumerable<CelestialError> errors)
        {
            var rejected = new CelestialOperation(operationId, CelestialOperationStatus.Rejected, new[] { id }, errors);
            operations.Update(rejected);
            return rejected;
        }
        private void Pending(CelestialRemovalRecord record, IEnumerable<CelestialError> errors)
        {
            var pending = new CelestialOperation(record.OperationId, CelestialOperationStatus.Pending, new[] { record.InstanceId }, errors);
            operations.Update(pending);
            if (catalog.TryGetInstance(record.InstanceId, out var instance)) instance.Errors = pending.Errors;
        }
        private void PendingCleanup(CelestialRemovalRecord record, IEnumerable<CelestialError> errors)
            => Pending(record, errors.Prepend(new CelestialError("CleanupPending", record.InstanceId)));
    }
}
