using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PioneerFramework.Celestial
{
    // Wire-only DTOs. Renaming a domain member requires an explicit mapping edit, never an implicit format change.
    public static partial class CelestialSaveCodec
    {
        private static void AddV2Converters(JsonSerializerOptions options)
        {
            void Add<T, TData>(Func<T, TData> encode, Func<TData, T> decode) => options.Converters.Add(new Surrogate<T, TData>(encode, decode));
            Add<PhysicsContractVersion, PhysicsContractVersionDataV2>(v => new(v.Major, v.Minor), v => new(v.Major, v.Minor));
            Add<PhysicsSampleStamp, PhysicsSampleStampDataV2>(v => new(v.WorldSessionId, v.ClockRevision, v.StateRevision, v.TopologyRevision, v.PhysicalSeconds, v.ReferenceFrameId, v.SampleKind), v => new(v.WorldSessionId, v.ClockRevision, v.StateRevision, v.TopologyRevision, v.PhysicalSeconds, v.ReferenceFrameId, v.SampleKind));
            Add<CelestialPhysicsDescriptor, CelestialPhysicsDescriptorDataV2>(v => new(v.Id, v.DescriptorRevision, v.RadiusMeters, v.GravitationalParameter), v => new(v.Id, v.DescriptorRevision, v.RadiusMeters, v.GravitationalParameter));
            Add<CelestialGenerationOrigin, CelestialGenerationOriginDataV2>(v => new(v.WorldSeed, v.GenerationKey, v.AlgorithmVersion), v => new(v.WorldSeed, v.GenerationKey, v.AlgorithmVersion));
            Add<CelestialRestoreSuspension, CelestialRestoreSuspensionDataV2>(v => new(v.Lifecycle, v.PreviousErrors), v => new(v.Lifecycle, v.PreviousErrors));
            Add<CelestialSavedComponentFault, CelestialSavedComponentFaultDataV2>(v => new(v.Key, v.Disabled, v.Error), v => new(v.Key, v.Disabled, v.Error));
            Add<CelestialSavedAssembly, CelestialSavedAssemblyDataV2>(v => new(v.EntryKey, v.Index, v.GenerationPath, v.Label, v.InitializationReferences, v.Registration), v => new(v.EntryKey, v.Index, v.GenerationPath, v.Label, v.InitializationReferences, v.Registration));
            Add<CelestialSavedCreation, CelestialSavedCreationDataV2>(v => new(v.OperationId, v.SystemDefinitionId, v.WorldSeed, v.RootSurfaceId, v.InstanceIds, v.HasPhysics, v.CompensationId, v.Failure), v => new(v.OperationId, v.SystemDefinitionId, v.WorldSeed, v.RootSurfaceId, v.InstanceIds, v.HasPhysics, v.CompensationId, v.Failure));
            Add<CelestialOperationRecord, CelestialOperationRecordDataV2>(v => new(v.ContentHash, v.Operation), v => new(v.ContentHash, v.Operation));
            Add<CelestialRemovalRecord, CelestialRemovalRecordDataV2>(v => new(v.OperationId, v.InstanceId, v.PhysicsOperationId, v.Phase), v => new(v.OperationId, v.InstanceId, v.PhysicsOperationId, v.Phase));
            Add<CelestialReference, CelestialReferenceDataV2>(v => new(v.ReferenceId, v.InstanceId, v.Kind, v.OwnerId, v.WorldSessionId, v.ExpectedRevision), v => new(v.ReferenceId, v.InstanceId, v.Kind, v.OwnerId, v.WorldSessionId, v.ExpectedRevision));
            Add<CelestialCommandRecord, CelestialCommandRecordDataV2>(v => new(v.Command, v.Receipt), v => new(v.Command, v.Receipt));
            Add<CelestialSchedulerCursor, CelestialSchedulerCursorDataV2>(v => new(v.InstanceId, v.CompKey, v.LastSeconds), v => new(v.InstanceId, v.CompKey, v.LastSeconds));
            Add<CelestialPendingEvent, CelestialPendingEventDataV2>(v => new(v.InstanceId, v.CompKey, v.Message), v => new(v.InstanceId, v.CompKey, v.Message));
            Add<CelestialMapAddress, CelestialMapAddressDataV2>(v => new(v.InstanceId, v.LayerId, v.TileId, v.MapParentId), v => new(v.InstanceId, v.LayerId, v.TileId, v.MapParentId));
            Add<CelestialError, ErrorV2>(v => new(v.Code, v.InstanceId, v.CompKey, v.Detail, v.Category, v.RetryDisposition), v => new(v.Code, v.InstanceId, v.CompKey, v.Detail) { CategoryOverride = new CelestialError(v.Code).Category == v.Category ? null : v.Category, RetryOverride = new CelestialError(v.Code).RetryDisposition == v.RetryDisposition ? null : v.RetryDisposition });
            Add<CelestialEpochState, EpochV2>(v => new(v.NextEpoch, v.OpenEpochs), v => new(v.NextEpoch, v.OpenEpochs));
            Add<CelestialBodyRegistration, RegistrationV2>(v => new(v.DefinitionId, v.Descriptor, v.Initialization, v.ParentInstanceId, v.InitializationReferences, v.CompatibilityHandle), v => new(v.DefinitionId, v.Descriptor, v.Initialization, v.ParentInstanceId, v.InitializationReferences, v.CompatibilityHandle));
            Add<CelestialSavedSurface, SurfaceV2>(v => new(v.OwnerId, v.LayerId, v.PhysicalRadiusMeters, v.NativeRadius, v.Subdivisions, v.GenerationSeed, v.GenerationConfiguration.ToArray(), v.State, v.TerrainAlgorithmVersion), v => new(v.OwnerId, v.LayerId, v.PhysicalRadiusMeters, v.NativeRadius, v.Subdivisions, v.GenerationSeed, v.GenerationConfiguration, v.State, v.TerrainAlgorithmVersion));
            Add<CelestialMapSiteRecord, MapV2>(v => new(v.Address, v.MapParentId, v.MapId, v.Failure, v.ParentRetired, v.OperationIds, v.RetiredOperationIds, v.Generation), v => new(v.Address, v.MapParentId, v.MapId, v.Failure) { ParentRetired = v.ParentRetired, OperationIds = v.OperationIds, RetiredOperationIds = v.RetiredOperationIds, Generation = v.Generation });
            Add<CelestialSavedBody, BodyV2>(v => new(v.Description, v.GenerationOrigin, v.Lifecycle, v.CommittedMembership, v.ParentId, v.Errors, v.Descriptors, v.Components, v.Assembly, v.RestoreSuspension, v.OperationEpoch, v.FailedCreation, v.RootSurfaceCandidate, v.ComponentFaults), v => new(v.Description, v.GenerationOrigin, v.Lifecycle, v.CommittedMembership, v.ParentId, v.Errors, v.Descriptors, v.Components, v.Assembly) { RestoreSuspension = v.RestoreSuspension, OperationEpoch = v.OperationEpoch, FailedCreation = v.FailedCreation, RootSurfaceCandidate = v.RootSurfaceCandidate, ComponentFaults = v.ComponentFaults });
            Add<CelestialSaveDocument, DocumentV2>(v => new(v.FormatVersion, v.GenerationAlgorithmVersion, v.WorldSeed, v.CatalogRevision, v.NextHandle, v.RootSurfaceId, v.Stamp, v.Bodies, v.PublishedBodies, v.CommittedBodies, v.Operations, v.Creations, v.Removals, v.References, v.Commands, v.SchedulerCursors, v.Events, v.Surfaces, v.Maps, v.PhysicsCheckpoint, v.Epochs, v.HashSchemas, v.Extensions), v => new() { FormatVersion = v.FormatVersion, GenerationAlgorithmVersion = v.GenerationAlgorithmVersion, WorldSeed = v.WorldSeed, CatalogRevision = v.CatalogRevision, NextHandle = v.NextHandle, RootSurfaceId = v.RootSurfaceId, Stamp = v.Stamp, Bodies = v.Bodies, PublishedBodies = v.PublishedBodies, CommittedBodies = v.CommittedBodies, Operations = v.Operations, Creations = v.Creations, Removals = v.Removals, References = v.References, Commands = v.Commands, SchedulerCursors = v.SchedulerCursors, Events = v.Events, Surfaces = v.Surfaces, Maps = v.Maps, PhysicsCheckpoint = v.PhysicsCheckpoint, Epochs = v.Epochs, HashSchemas = v.HashSchemas, Extensions = v.Extensions });
        }
        private sealed record DescriptionData(
            [property: JsonPropertyName("id")] CelestialInstanceId Id,
            [property: JsonPropertyName("definitionId")] string DefinitionId,
            [property: JsonPropertyName("handle")] int Handle,
            [property: JsonPropertyName("descriptorRevision")] long DescriptorRevision,
            [property: JsonPropertyName("category")] string Category,
            [property: JsonPropertyName("radiusMeters")] double RadiusMeters,
            [property: JsonPropertyName("gravitationalParameter")] double? GravitationalParameter,
            [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags);
        private sealed record DescriptorData(
            [property: JsonPropertyName("key")] string Key,
            [property: JsonPropertyName("handlerId")] string HandlerId,
            [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
            [property: JsonPropertyName("provides")] IReadOnlyList<string> Provides,
            [property: JsonPropertyName("requires")] IReadOnlyList<string> Requires,
            [property: JsonPropertyName("exclusiveSlot")] string? ExclusiveSlot,
            [property: JsonPropertyName("before")] IReadOnlyList<string> Before,
            [property: JsonPropertyName("after")] IReadOnlyList<string> After,
            [property: JsonPropertyName("mode")] CelestialUpdateMode Mode,
            [property: JsonPropertyName("intervalSeconds")] double IntervalSeconds,
            [property: JsonPropertyName("failurePolicy")] CelestialCompFailurePolicy FailurePolicy);
        private sealed record ComponentData(
            [property: JsonPropertyName("key")] string Key,
            [property: JsonPropertyName("handlerId")] string HandlerId,
            [property: JsonPropertyName("configurationVersion")] int ConfigurationVersion,
            [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
            [property: JsonPropertyName("configuration")] byte[] Configuration,
            [property: JsonPropertyName("state")] byte[] State);
        private sealed record InitializationData(
            [property: JsonPropertyName("providerId")] string ProviderId,
            [property: JsonPropertyName("modeId")] string ModeId,
            [property: JsonPropertyName("version")] int Version,
            [property: JsonPropertyName("payload")] byte[] Payload);
        private sealed record CheckpointData(
            [property: JsonPropertyName("providerId")] string ProviderId,
            [property: JsonPropertyName("contractVersion")] PhysicsContractVersion ContractVersion,
            [property: JsonPropertyName("payloadVersion")] int PayloadVersion,
            [property: JsonPropertyName("checkpointId")] string CheckpointId,
            [property: JsonPropertyName("stamp")] PhysicsSampleStamp Stamp,
            [property: JsonPropertyName("payload")] byte[] Payload);
        private sealed record OperationData(
            [property: JsonPropertyName("operationId")] string OperationId,
            [property: JsonPropertyName("status")] CelestialOperationStatus Status,
            [property: JsonPropertyName("instanceIds")] IReadOnlyList<CelestialInstanceId> InstanceIds,
            [property: JsonPropertyName("errors")] IReadOnlyList<CelestialError> Errors,
            [property: JsonPropertyName("appliedRevision")] long? AppliedRevision,
            [property: JsonPropertyName("relatedOperationId")] string? RelatedOperationId);
        private sealed record CommandData(
            [property: JsonPropertyName("kind")] string Kind,
            [property: JsonPropertyName("operationId")] string OperationId,
            [property: JsonPropertyName("targetId")] CelestialInstanceId TargetId,
            [property: JsonPropertyName("worldSessionId")] Guid WorldSessionId,
            [property: JsonPropertyName("expectedRevision")] long ExpectedRevision,
            [property: JsonPropertyName("payload")] byte[] Payload);
        private sealed record EventData(
            [property: JsonPropertyName("kind")] string Kind,
            [property: JsonPropertyName("payload")] byte[] Payload);
        private sealed record PhysicsContractVersionDataV2(
            [property: JsonPropertyName("major")] int Major,
            [property: JsonPropertyName("minor")] int Minor);
        private sealed record PhysicsSampleStampDataV2(
            [property: JsonPropertyName("worldSessionId")] Guid WorldSessionId,
            [property: JsonPropertyName("clockRevision")] long ClockRevision,
            [property: JsonPropertyName("stateRevision")] long StateRevision,
            [property: JsonPropertyName("topologyRevision")] long TopologyRevision,
            [property: JsonPropertyName("physicalSeconds")] double PhysicalSeconds,
            [property: JsonPropertyName("referenceFrameId")] string ReferenceFrameId,
            [property: JsonPropertyName("sampleKind")] PhysicsSampleKind SampleKind);
        private sealed record CelestialPhysicsDescriptorDataV2(
            [property: JsonPropertyName("id")] CelestialInstanceId Id,
            [property: JsonPropertyName("descriptorRevision")] long DescriptorRevision,
            [property: JsonPropertyName("radiusMeters")] double RadiusMeters,
            [property: JsonPropertyName("gravitationalParameter")] double? GravitationalParameter);
        private sealed record CelestialGenerationOriginDataV2(
            [property: JsonPropertyName("worldSeed")] ulong WorldSeed,
            [property: JsonPropertyName("generationKey")] string GenerationKey,
            [property: JsonPropertyName("algorithmVersion")] int AlgorithmVersion);
        private sealed record CelestialRestoreSuspensionDataV2(
            [property: JsonPropertyName("lifecycle")] CelestialLifecycle Lifecycle,
            [property: JsonPropertyName("previousErrors")] IReadOnlyList<CelestialError> PreviousErrors);
        private sealed record CelestialSavedComponentFaultDataV2(
            [property: JsonPropertyName("key")] string Key,
            [property: JsonPropertyName("disabled")] bool Disabled,
            [property: JsonPropertyName("error")] CelestialError Error);
        private sealed record CelestialSavedAssemblyDataV2(
            [property: JsonPropertyName("entryKey")] string EntryKey,
            [property: JsonPropertyName("index")] int Index,
            [property: JsonPropertyName("generationPath")] string GenerationPath,
            [property: JsonPropertyName("label")] string Label,
            [property: JsonPropertyName("initializationReferences")] IReadOnlyDictionary<string, CelestialInstanceId> InitializationReferences,
            [property: JsonPropertyName("registration")] CelestialBodyRegistration? Registration);
        private sealed record CelestialSavedCreationDataV2(
            [property: JsonPropertyName("operationId")] string OperationId,
            [property: JsonPropertyName("systemDefinitionId")] string SystemDefinitionId,
            [property: JsonPropertyName("worldSeed")] ulong WorldSeed,
            [property: JsonPropertyName("rootSurfaceId")] CelestialInstanceId? RootSurfaceId,
            [property: JsonPropertyName("instanceIds")] IReadOnlyList<CelestialInstanceId> InstanceIds,
            [property: JsonPropertyName("hasPhysics")] bool HasPhysics,
            [property: JsonPropertyName("compensationId")] string? CompensationId,
            [property: JsonPropertyName("failure")] IReadOnlyList<CelestialError> Failure);
        private sealed record CelestialOperationRecordDataV2(
            [property: JsonPropertyName("contentHash")] string ContentHash,
            [property: JsonPropertyName("operation")] CelestialOperation Operation);
        private sealed record CelestialRemovalRecordDataV2(
            [property: JsonPropertyName("operationId")] string OperationId,
            [property: JsonPropertyName("instanceId")] CelestialInstanceId InstanceId,
            [property: JsonPropertyName("physicsOperationId")] string? PhysicsOperationId,
            [property: JsonPropertyName("phase")] CelestialRemovalPhase Phase);
        private sealed record CelestialReferenceDataV2(
            [property: JsonPropertyName("referenceId")] string ReferenceId,
            [property: JsonPropertyName("instanceId")] CelestialInstanceId InstanceId,
            [property: JsonPropertyName("kind")] CelestialReferenceKind Kind,
            [property: JsonPropertyName("ownerId")] string OwnerId,
            [property: JsonPropertyName("worldSessionId")] Guid WorldSessionId,
            [property: JsonPropertyName("expectedRevision")] long ExpectedRevision);
        private sealed record CelestialCommandRecordDataV2(
            [property: JsonPropertyName("command")] CelestialCommand Command,
            [property: JsonPropertyName("receipt")] CelestialOperation Receipt);
        private sealed record CelestialSchedulerCursorDataV2(
            [property: JsonPropertyName("instanceId")] CelestialInstanceId InstanceId,
            [property: JsonPropertyName("compKey")] string CompKey,
            [property: JsonPropertyName("lastSeconds")] double LastSeconds);
        private sealed record CelestialPendingEventDataV2(
            [property: JsonPropertyName("instanceId")] CelestialInstanceId InstanceId,
            [property: JsonPropertyName("compKey")] string? CompKey,
            [property: JsonPropertyName("message")] CelestialEvent Message);
        private sealed record CelestialMapAddressDataV2(
            [property: JsonPropertyName("instanceId")] CelestialInstanceId InstanceId,
            [property: JsonPropertyName("layerId")] int LayerId,
            [property: JsonPropertyName("tileId")] int TileId,
            [property: JsonPropertyName("mapParentId")] int? MapParentId);
        private sealed record ErrorV2(
            [property: JsonPropertyName("code")] string Code,
            [property: JsonPropertyName("instanceId")] CelestialInstanceId? InstanceId,
            [property: JsonPropertyName("compKey")] string? CompKey,
            [property: JsonPropertyName("detail")] string Detail,
            [property: JsonPropertyName("category")] CelestialErrorCategory Category,
            [property: JsonPropertyName("retryDisposition")] CelestialRetryDisposition RetryDisposition);
        private sealed record EpochV2(
            [property: JsonPropertyName("nextEpoch")] long NextEpoch,
            [property: JsonPropertyName("openEpochs")] IReadOnlyList<long> OpenEpochs);
        private sealed record RegistrationV2(
            [property: JsonPropertyName("definitionId")] string DefinitionId,
            [property: JsonPropertyName("descriptor")] CelestialPhysicsDescriptor Descriptor,
            [property: JsonPropertyName("initialization")] PhysicsInitialization Initialization,
            [property: JsonPropertyName("parentInstanceId")] CelestialInstanceId? ParentInstanceId,
            [property: JsonPropertyName("initializationReferences")] IReadOnlyDictionary<string, CelestialInstanceId> InitializationReferences,
            [property: JsonPropertyName("compatibilityHandle")] int? CompatibilityHandle);
        private sealed record SurfaceV2(
            [property: JsonPropertyName("ownerId")] CelestialInstanceId OwnerId,
            [property: JsonPropertyName("layerId")] int LayerId,
            [property: JsonPropertyName("physicalRadiusMeters")] double PhysicalRadiusMeters,
            [property: JsonPropertyName("nativeRadius")] float NativeRadius,
            [property: JsonPropertyName("subdivisions")] int Subdivisions,
            [property: JsonPropertyName("generationSeed")] int GenerationSeed,
            [property: JsonPropertyName("generationConfiguration")] byte[] GenerationConfiguration,
            [property: JsonPropertyName("state")] CelestialSurfaceState State,
            // 旧 v4 档没有该字段：缺省按"字段引入前"的语义版本读取；未知/更新版本仍由 codec 校验显式拒绝。
            [property: JsonPropertyName("terrainAlgorithmVersion")] int TerrainAlgorithmVersion = CelestialPlanetTerrain.LegacyVersionWithoutHeaderField);
        private sealed record MapV2(
            [property: JsonPropertyName("address")] CelestialMapAddress Address,
            [property: JsonPropertyName("mapParentId")] int MapParentId,
            [property: JsonPropertyName("mapId")] int? MapId,
            [property: JsonPropertyName("failure")] string? Failure,
            [property: JsonPropertyName("parentRetired")] bool ParentRetired,
            [property: JsonPropertyName("operationIds")] IReadOnlyList<string> OperationIds,
            [property: JsonPropertyName("retiredOperationIds")] IReadOnlyList<string> RetiredOperationIds,
            [property: JsonPropertyName("generation")] PioneerFramework.MapGeneration.LocalMapGenerationRecord? Generation);
        private sealed record BodyV2(
            [property: JsonPropertyName("description")] CelestialBodyDescription Description,
            [property: JsonPropertyName("generationOrigin")] CelestialGenerationOrigin GenerationOrigin,
            [property: JsonPropertyName("lifecycle")] CelestialLifecycle Lifecycle,
            [property: JsonPropertyName("committedMembership")] bool CommittedMembership,
            [property: JsonPropertyName("parentId")] CelestialInstanceId? ParentId,
            [property: JsonPropertyName("errors")] IReadOnlyList<CelestialError> Errors,
            [property: JsonPropertyName("descriptors")] IReadOnlyList<CelestialCompDescriptor> Descriptors,
            [property: JsonPropertyName("components")] IReadOnlyList<CelestialCompSaveRecord> Components,
            [property: JsonPropertyName("assembly")] CelestialSavedAssembly? Assembly,
            [property: JsonPropertyName("restoreSuspension")] CelestialRestoreSuspension? RestoreSuspension,
            [property: JsonPropertyName("operationEpoch")] long OperationEpoch,
            [property: JsonPropertyName("failedCreation")] bool FailedCreation,
            [property: JsonPropertyName("rootSurfaceCandidate")] bool RootSurfaceCandidate,
            [property: JsonPropertyName("componentFaults")] IReadOnlyList<CelestialSavedComponentFault> ComponentFaults);
        private sealed record DocumentV2(
            [property: JsonPropertyName("formatVersion")] int FormatVersion,
            [property: JsonPropertyName("generationAlgorithmVersion")] int GenerationAlgorithmVersion,
            [property: JsonPropertyName("worldSeed")] ulong WorldSeed,
            [property: JsonPropertyName("catalogRevision")] long CatalogRevision,
            [property: JsonPropertyName("nextHandle")] long NextHandle,
            [property: JsonPropertyName("rootSurfaceId")] CelestialInstanceId? RootSurfaceId,
            [property: JsonPropertyName("stamp")] PhysicsSampleStamp Stamp,
            [property: JsonPropertyName("bodies")] IReadOnlyList<CelestialSavedBody> Bodies,
            [property: JsonPropertyName("publishedBodies")] IReadOnlyList<CelestialInstanceId> PublishedBodies,
            [property: JsonPropertyName("committedBodies")] IReadOnlyList<CelestialInstanceId> CommittedBodies,
            [property: JsonPropertyName("operations")] IReadOnlyList<CelestialOperationRecord> Operations,
            [property: JsonPropertyName("creations")] IReadOnlyList<CelestialSavedCreation> Creations,
            [property: JsonPropertyName("removals")] IReadOnlyList<CelestialRemovalRecord> Removals,
            [property: JsonPropertyName("references")] IReadOnlyList<CelestialReference> References,
            [property: JsonPropertyName("commands")] IReadOnlyList<CelestialCommandRecord> Commands,
            [property: JsonPropertyName("schedulerCursors")] IReadOnlyList<CelestialSchedulerCursor> SchedulerCursors,
            [property: JsonPropertyName("events")] IReadOnlyList<CelestialPendingEvent> Events,
            [property: JsonPropertyName("surfaces")] IReadOnlyList<CelestialSavedSurface> Surfaces,
            [property: JsonPropertyName("maps")] IReadOnlyList<CelestialMapSiteRecord> Maps,
            [property: JsonPropertyName("physicsCheckpoint")] PhysicsCheckpoint? PhysicsCheckpoint,
            [property: JsonPropertyName("epochs")] CelestialEpochState Epochs,
            [property: JsonPropertyName("hashSchemas")] IReadOnlyDictionary<string, int> HashSchemas,
            [property: JsonPropertyName("extensions")] IReadOnlyDictionary<string, byte[]> Extensions);
    }
}
