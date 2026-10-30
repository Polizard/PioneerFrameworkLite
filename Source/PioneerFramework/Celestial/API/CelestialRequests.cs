using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed record CelestialOperationContext(string OperationId, Guid WorldSessionId, long ExpectedRevision);
    public enum CelestialOperationStatus { Pending, Applied, Rejected }
    public sealed class CelestialOperation
    {
        public string OperationId { get; }
        public CelestialOperationStatus Status { get; }
        public IReadOnlyList<CelestialInstanceId> InstanceIds { get; }
        public IReadOnlyList<CelestialError> Errors { get; }
        public long? AppliedRevision { get; }
        public string? RelatedOperationId { get; }
        public CelestialOperation(string operationId, CelestialOperationStatus status, IEnumerable<CelestialInstanceId>? instanceIds = null,
            IEnumerable<CelestialError>? errors = null, long? appliedRevision = null, string? relatedOperationId = null)
        {
            OperationId = operationId;
            Status = status;
            InstanceIds = Array.AsReadOnly((instanceIds ?? Array.Empty<CelestialInstanceId>()).ToArray());
            Errors = Array.AsReadOnly((errors ?? Array.Empty<CelestialError>()).ToArray());
            AppliedRevision = appliedRevision;
            RelatedOperationId = relatedOperationId;
        }
    }
    public sealed class CreateBodyRequest
    {
        public CelestialOperationContext Context { get; }
        public string DefinitionId { get; }
        public string GenerationKey { get; }
        public ulong? Seed { get; }
        public CelestialInstanceId? ParentId { get; }
        public IReadOnlyList<CelestialCompDefinition> Overrides { get; }
        public CreateBodyRequest(CelestialOperationContext context, string definitionId, string generationKey, ulong? seed = null,
            CelestialInstanceId? parentId = null, IEnumerable<CelestialCompDefinition>? overrides = null)
        {
            Context = context;
            DefinitionId = definitionId;
            GenerationKey = generationKey;
            Seed = seed;
            ParentId = parentId;
            Overrides = Array.AsReadOnly((overrides ?? Array.Empty<CelestialCompDefinition>()).ToArray());
        }
    }
    public sealed record CreateBatchRequest(CelestialOperationContext Context, CelestialSpawnManifest Manifest);
    public sealed record RemoveBodyRequest(CelestialOperationContext Context, CelestialInstanceId InstanceId);

    public sealed class CelestialBodySnapshot
    {
        public CelestialInstanceId Id => Description.Id;
        public CelestialBodyDescription Description { get; }
        public CelestialLifecycle Lifecycle { get; }
        public bool CommittedMembership { get; }
        public CelestialInstanceId? ParentId { get; }
        public IReadOnlyList<string> ComponentKeys { get; }
        public IReadOnlyList<CelestialError> Errors { get; }
        public CelestialBodySnapshot(CelestialBodyDescription description, CelestialLifecycle lifecycle, bool committedMembership,
            CelestialInstanceId? parentId, IEnumerable<string> componentKeys, IEnumerable<CelestialError> errors)
        {
            Description = description;
            Lifecycle = lifecycle;
            CommittedMembership = committedMembership;
            ParentId = parentId;
            ComponentKeys = Array.AsReadOnly(componentKeys.ToArray());
            Errors = Array.AsReadOnly(errors.ToArray());
        }
    }
}
