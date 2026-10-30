using System;
using System.Linq;

namespace PioneerFramework.Celestial
{
    internal static class CelestialPhysicsRetries
    {
        internal static bool IsTransient(PhysicsOperation receipt) => receipt.Status == PhysicsOperationStatus.Rejected && receipt.Errors.Count > 0 &&
            receipt.Errors.All(error => error.Code is "ProviderUnavailable" or "ProviderRestoring" or "ProviderSubmissionFailed");
        internal static bool IsMissingOrTransient(PhysicsOperation receipt) => IsTransient(receipt) ||
            receipt.Status == PhysicsOperationStatus.Rejected && receipt.Errors.Any(error => error.Code == "OperationMissing");
        internal static PhysicsOperation Get(ICelestialPhysicsBridge physics, string id)
        {
            try { return physics.GetOperation(id); }
            catch (Exception exception) { return new(id, PhysicsOperationStatus.Rejected,
                new[] { new CelestialError("ProviderUnavailable", Detail: exception.Message) }); }
        }
    }
}
