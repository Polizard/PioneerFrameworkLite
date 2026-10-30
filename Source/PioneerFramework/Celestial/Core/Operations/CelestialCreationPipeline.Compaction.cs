using System;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed partial class CelestialCreationPipeline
    {
        internal CelestialResult<bool> DiscardFailedCreation(CelestialInstanceId id)
        {
            if (!catalog.TryGetInstance(id, out var instance) || !instance.FailedCreation)
                return CelestialResult.Reject<bool>(new CelestialError("NotFailedCreation", id));
            if (instance.CommittedMembership || RootSurfaceId == id || instance.RootSurfaceCandidate)
                return CelestialResult.Reject<bool>(new CelestialError("FailedCreationStillOwned", id));
            RetireRemovedInstance(id); return CelestialResult.Ok(true);
        }
        internal void RetireRemovedInstance(CelestialInstanceId id)
        {
            if (!catalog.TryGetInstance(id, out var instance) || instance.CommittedMembership) throw new InvalidOperationException("BodyStillCommitted");
            if (assemblies.TryGetValue(id, out var assembly) && assembly.Components.Any(component => !component.IsReleased)) throw new InvalidOperationException("ComponentStillAttached");
            assemblies.Remove(id); unavailableBodies.Remove(id);
            instance.RetireFailedCreation();
        }
        internal void CompactTerminalBatches()
        {
            foreach (var id in batches.Keys)
                if (operations.Get(id).Status != CelestialOperationStatus.Pending) batches.Remove(id);
        }
    }
}
