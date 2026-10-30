using System;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed partial class CelestialCreationPipeline
    {
        internal void RetrySubmissions()
        {
            if (physics.Status != PhysicsBridgeStatus.Ready) return;
            RetryFrozenCompensations();
            foreach (var batch in batches.Values.Where(batch => batch.HasPhysics && operations.Get(batch.OperationId).Status == CelestialOperationStatus.Pending).ToArray())
            {
                string id = batch.CompensationId ?? batch.PhysicsId;
                if (!CelestialPhysicsRetries.IsMissingOrTransient(CelestialPhysicsRetries.Get(physics, id))) continue;
                try
                {
                    var changes = batch.CompensationId == null ? batch.Assemblies.Where(assembly => assembly.Registration != null)
                        .Select(assembly => PhysicsBodyChange.Register(assembly.Registration!)) : batch.Assemblies.Where(assembly => assembly.Instance.CommittedMembership)
                        .Select(assembly => PhysicsBodyChange.Unregister(assembly.Instance.Id));
                    var receipt = physics.Submit(new(id, WorldSessionId, PhysicsTopologyRevision, changes));
                    if (receipt.Status == PhysicsOperationStatus.Rejected)
                    {
                        foreach (var assembly in batch.Assemblies) assembly.Instance.Errors = receipt.Errors;
                        if (!CelestialPhysicsRetries.IsTransient(receipt) && batch.CompensationId == null) Fail(batch, receipt.Errors);
                    }
                }
                catch (Exception exception)
                {
                    foreach (var assembly in batch.Assemblies) assembly.Instance.Errors = new[] { new CelestialError("ProviderSubmissionFailed", assembly.Instance.Id, Detail: exception.Message) };
                }
            }
        }
    }
}
