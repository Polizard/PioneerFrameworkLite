using System;
using System.Collections.Generic;

namespace PioneerFramework.Celestial
{
    public sealed record CelestialSavedComponentFault(string Key, bool Disabled, CelestialError Error);
    public sealed record CelestialRestoreSuspension(CelestialLifecycle Lifecycle, IReadOnlyList<CelestialError> PreviousErrors);
    public sealed record CelestialSavedAssembly(string EntryKey, int Index, string GenerationPath, string Label,
        IReadOnlyDictionary<string, CelestialInstanceId> InitializationReferences, CelestialBodyRegistration? Registration);
    public sealed record CelestialSavedBody(CelestialBodyDescription Description, CelestialGenerationOrigin GenerationOrigin,
        CelestialLifecycle Lifecycle, bool CommittedMembership, CelestialInstanceId? ParentId, IReadOnlyList<CelestialError> Errors,
        IReadOnlyList<CelestialCompDescriptor> Descriptors, IReadOnlyList<CelestialCompSaveRecord> Components, CelestialSavedAssembly? Assembly)
    {
        public CelestialRestoreSuspension? RestoreSuspension { get; init; }
        public long OperationEpoch { get; init; }
        public bool FailedCreation { get; init; }
        public bool RootSurfaceCandidate { get; init; }
        public IReadOnlyList<CelestialSavedComponentFault> ComponentFaults { get; init; } = Array.Empty<CelestialSavedComponentFault>();
    }
    public sealed record CelestialSavedCreation(string OperationId, string SystemDefinitionId, ulong WorldSeed, CelestialInstanceId? RootSurfaceId,
        IReadOnlyList<CelestialInstanceId> InstanceIds, bool HasPhysics, string? CompensationId, IReadOnlyList<CelestialError> Failure);
    public sealed record CelestialSavedSurface(CelestialInstanceId OwnerId, int LayerId, double PhysicalRadiusMeters, float NativeRadius,
        int Subdivisions, int GenerationSeed, ReadOnlyMemory<byte> GenerationConfiguration, CelestialSurfaceState State);

    /// <summary>共同发布边界上的独立快照。配置、状态、清单与物理成员关系均显式保存。</summary>
    public sealed record CelestialSaveDocument
    {
        public const int CurrentVersion = 2;
        public int FormatVersion { get; init; }
        public int GenerationAlgorithmVersion { get; init; }
        public ulong WorldSeed { get; init; }
        public long CatalogRevision { get; init; }
        public long NextHandle { get; init; }
        public CelestialInstanceId? RootSurfaceId { get; init; }
        public PhysicsSampleStamp Stamp { get; init; }
        public IReadOnlyList<CelestialSavedBody> Bodies { get; init; } = Array.Empty<CelestialSavedBody>();
        public IReadOnlyList<CelestialInstanceId> PublishedBodies { get; init; } = Array.Empty<CelestialInstanceId>();
        public IReadOnlyList<CelestialInstanceId> CommittedBodies { get; init; } = Array.Empty<CelestialInstanceId>();
        public IReadOnlyList<CelestialOperationRecord> Operations { get; init; } = Array.Empty<CelestialOperationRecord>();
        public IReadOnlyList<CelestialSavedCreation> Creations { get; init; } = Array.Empty<CelestialSavedCreation>();
        public IReadOnlyList<CelestialRemovalRecord> Removals { get; init; } = Array.Empty<CelestialRemovalRecord>();
        public IReadOnlyList<CelestialReference> References { get; init; } = Array.Empty<CelestialReference>();
        public IReadOnlyList<CelestialCommandRecord> Commands { get; init; } = Array.Empty<CelestialCommandRecord>();
        public IReadOnlyList<CelestialSchedulerCursor> SchedulerCursors { get; init; } = Array.Empty<CelestialSchedulerCursor>();
        public IReadOnlyList<CelestialPendingEvent> Events { get; init; } = Array.Empty<CelestialPendingEvent>();
        public IReadOnlyList<CelestialSavedSurface> Surfaces { get; init; } = Array.Empty<CelestialSavedSurface>();
        public IReadOnlyList<CelestialMapSiteRecord> Maps { get; init; } = Array.Empty<CelestialMapSiteRecord>();
        public PhysicsCheckpoint? PhysicsCheckpoint { get; init; }
        public CelestialEpochState Epochs { get; init; } = new(1, new long[] { 0 });
        public IReadOnlyDictionary<string, byte[]> Extensions { get; init; } = new Dictionary<string, byte[]>();
        public IReadOnlyDictionary<string, int> HashSchemas { get; init; } = new Dictionary<string, int> {
            ["creation"] = 2, ["command"] = 1, ["physics"] = 1, ["removal"] = 1 };
        public static CelestialSaveDocument CreateEmpty(Guid session, ulong worldSeed) => new() {
            FormatVersion = CurrentVersion, GenerationAlgorithmVersion = 1, WorldSeed = worldSeed,
            Stamp = new(session, 0, 0, 0, 0, "PFL.System.v1", PhysicsSampleKind.Committed) };
    }
}
