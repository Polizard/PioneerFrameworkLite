using System;
using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    public enum CelestialReferenceKind { Map, WorldObject, Ship, Megastructure, Transport, ExternalPhysics, Extension }
    public sealed record CelestialReference(string ReferenceId, CelestialInstanceId InstanceId, CelestialReferenceKind Kind,
        string OwnerId, Guid WorldSessionId, long ExpectedRevision);
    public interface ICelestialRemovalResources
    {
        IReadOnlyList<CelestialError> Inspect(CelestialInstanceId instanceId);
        CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId instanceId);
    }
    public enum CelestialRemovalPhase { AwaitingPhysics, Cleanup, Completed }
    public sealed record CelestialRemovalRecord(string OperationId, CelestialInstanceId InstanceId, string? PhysicsOperationId, CelestialRemovalPhase Phase);
}
