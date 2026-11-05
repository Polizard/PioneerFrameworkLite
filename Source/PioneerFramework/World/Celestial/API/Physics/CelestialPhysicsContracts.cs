using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public readonly record struct PhysicsContractVersion(int Major, int Minor)
    {
        public static PhysicsContractVersion Current { get; } = new(1, 0);
    }
    public enum PhysicsBridgeStatus { Unavailable, Ready, Restoring, Faulted }
    public enum PhysicsPreparationStatus { Pending, Prepared, Rejected }
    public sealed record PhysicsReferenceFrame(string Id, string Origin, string Axes, string TimeBasis);

    public interface ICelestialPhysicsBridge
    {
        string ProviderId { get; }
        PhysicsContractVersion ContractVersion { get; }
        PhysicsBridgeStatus Status { get; }
        PhysicsReferenceFrame ReferenceFrame { get; }
        PhysicsOperation Submit(PhysicsChangeSet request);
        PhysicsOperation GetOperation(string operationId);
        PhysicsIntervalPreparation PrepareInterval(PhysicsIntervalRequest request);
        void CommitPreparedInterval(PhysicsIntervalToken token);
        void AbortPreparedInterval(PhysicsIntervalToken token);
        PhysicsSnapshotResult Sample(PhysicsSampleRequest request);
        PhysicsCheckpoint CaptureCheckpoint();
        PhysicsRestoreResult RestoreCheckpoint(PhysicsCheckpoint checkpoint, IReadOnlyList<CelestialBodyRegistration> committedBodies);
    }

    public sealed class PhysicsIntervalRequest
    {
        public Guid WorldSessionId { get; }
        public long ExpectedTopologyRevision { get; }
        public double StartSeconds { get; }
        public double EndSeconds { get; }
        public IReadOnlyList<string> OperationIds { get; }
        public PhysicsIntervalRequest(Guid worldSessionId, long expectedTopologyRevision, double startSeconds, double endSeconds,
            IEnumerable<string>? operationIds = null)
        {
            WorldSessionId = worldSessionId;
            ExpectedTopologyRevision = expectedTopologyRevision;
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
            OperationIds = Array.AsReadOnly((operationIds ?? Array.Empty<string>()).ToArray());
        }
    }
    public readonly record struct PhysicsIntervalToken(string ProviderId, Guid WorldSessionId, Guid TokenId, long ExpectedStateRevision);

    public interface ICelestialPhysicsIntervalSampler
    {
        double StartSeconds { get; }
        double EndSeconds { get; }
        PhysicsSnapshotResult Sample(double physicalSeconds, IReadOnlyList<CelestialInstanceId> ids);
    }

    public sealed class PhysicsIntervalPreparation
    {
        public PhysicsPreparationStatus Status { get; }
        public PhysicsIntervalToken? Token { get; }
        public ICelestialPhysicsIntervalSampler? Sampler { get; }
        public IReadOnlyList<CelestialError> Errors { get; }
        private PhysicsIntervalPreparation(PhysicsPreparationStatus status, PhysicsIntervalToken? token,
            ICelestialPhysicsIntervalSampler? sampler, IEnumerable<CelestialError> errors)
        {
            Status = status;
            Token = token;
            Sampler = sampler;
            Errors = Array.AsReadOnly(errors.ToArray());
        }
        public static PhysicsIntervalPreparation Prepared(PhysicsIntervalToken token, ICelestialPhysicsIntervalSampler sampler)
            => new(PhysicsPreparationStatus.Prepared, token, sampler, Array.Empty<CelestialError>());
        public static PhysicsIntervalPreparation Pending(params CelestialError[] errors)
            => new(PhysicsPreparationStatus.Pending, null, null, errors);
        public static PhysicsIntervalPreparation Reject(params CelestialError[] errors)
            => new(PhysicsPreparationStatus.Rejected, null, null, errors);
    }
}
