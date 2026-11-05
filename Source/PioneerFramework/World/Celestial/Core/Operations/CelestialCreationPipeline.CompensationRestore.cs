using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed partial class CelestialCreationPipeline
    {
        // Compensation needs identity and committed membership only. Failed comp factories must stay inactive.
        private IEnumerable<CelestialSavedCreation> FrozenCompensations => frozenCreations.Where(batch => batch.HasPhysics && batch.CompensationId != null &&
            operations.Get(batch.OperationId).Status == CelestialOperationStatus.Pending);
        private void RetryFrozenCompensations(bool rebindPending = false)
        {
            if (physics.Status != PhysicsBridgeStatus.Ready) return;
            foreach (var batch in FrozenCompensations.ToArray())
            {
                var known = CelestialPhysicsRetries.Get(physics, batch.CompensationId!);
                if (!CelestialPhysicsRetries.IsMissingOrTransient(known) && !(rebindPending && known.Status == PhysicsOperationStatus.Pending)) continue;
                try
                {
                    var changes = batch.InstanceIds.Where(id => catalog.TryGetInstance(id, out var instance) && instance.CommittedMembership)
                        .Select(PhysicsBodyChange.Unregister).ToArray();
                    if (changes.Length == 0) continue;
                    var receipt = physics.Submit(new(batch.CompensationId!, WorldSessionId, PhysicsTopologyRevision, changes));
                    if (receipt.Status == PhysicsOperationStatus.Rejected) RetainCompensationFailure(batch, receipt.Errors);
                }
                catch (Exception exception) { RetainCompensationFailure(batch, new[] { new CelestialError("ProviderSubmissionFailed", Detail: exception.Message) }); }
            }
        }
        private void SynchronizeFrozenCompensations()
        {
            foreach (var batch in FrozenCompensations.ToArray())
            {
                var receipt = CelestialPhysicsRetries.Get(physics, batch.CompensationId!);
                if (receipt.Status == PhysicsOperationStatus.Pending || CelestialPhysicsRetries.IsMissingOrTransient(receipt)) continue;
                if (receipt.Status != PhysicsOperationStatus.Applied) { RetainCompensationFailure(batch, receipt.Errors); continue; }
                foreach (var id in batch.InstanceIds)
                    if (catalog.TryGetInstance(id, out var instance))
                    { instance.CommittedMembership = false; instance.Lifecycle = CelestialLifecycle.Blocked; instance.FailedCreation = true; instance.Errors = batch.Failure.ToArray(); }
                operations.Update(new(batch.OperationId, CelestialOperationStatus.Rejected, batch.InstanceIds, batch.Failure, relatedOperationId: batch.CompensationId));
                frozenCreations.Remove(batch);
                FaultRevision++;
            }
        }
        private void RetainCompensationFailure(CelestialSavedCreation batch, IEnumerable<CelestialError> errors)
        {
            var diagnostics = batch.Failure.Concat(errors).Append(new CelestialError("CompensationFailed")).ToArray();
            foreach (var id in batch.InstanceIds) if (catalog.TryGetInstance(id, out var instance)) instance.Errors = diagnostics;
            operations.Update(new(batch.OperationId, CelestialOperationStatus.Pending, batch.InstanceIds, diagnostics, relatedOperationId: batch.CompensationId));
        }
    }
}
