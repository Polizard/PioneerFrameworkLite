using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public enum CelestialLifecycle { Staged, Registering, Ready, Removing, Removed, Blocked }

    public sealed record CelestialGenerationOrigin(ulong WorldSeed, string GenerationKey, int AlgorithmVersion = 1);

    public sealed class CelestialBodyInstance
    {
        private readonly Dictionary<string, CelestialCompSaveRecord> records = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CelestialCompDescriptor> descriptors = new(StringComparer.Ordinal);
        public CelestialInstanceId Id => Description.Id;
        public CelestialBodyDescription Description { get; internal set; }
        public CelestialGenerationOrigin GenerationOrigin { get; }
        public CelestialLifecycle Lifecycle { get; internal set; }
        public bool CommittedMembership { get; internal set; }
        public CelestialInstanceId? ParentId { get; internal set; }
        public IReadOnlyList<CelestialError> Errors { get; internal set; } = Array.Empty<CelestialError>();
        internal CelestialRestoreSuspension? RestoreSuspension { get; set; }
        internal Dictionary<string, CelestialSavedComponentFault> ComponentFaults { get; } = new(StringComparer.Ordinal);
        internal long OperationEpoch { get; set; }
        internal bool FailedCreation { get; set; }
        internal bool RootSurfaceCandidate { get; set; }
        internal void RetireFailedCreation()
        { Lifecycle = CelestialLifecycle.Removed; records.Clear(); descriptors.Clear(); ComponentFaults.Clear(); Errors = Array.Empty<CelestialError>(); FailedCreation = false; }
        internal void SuspendForRestore(IEnumerable<CelestialError> errors)
        {
            if (Lifecycle != CelestialLifecycle.Blocked) RestoreSuspension ??= new(Lifecycle, Errors.ToArray());
            Lifecycle = CelestialLifecycle.Blocked;
            Errors = Errors.Concat(errors).Distinct().ToArray();
        }
        internal void CompleteRestoreSuspension()
        {
            if (RestoreSuspension == null) return;
            Lifecycle = RestoreSuspension.Lifecycle;
            Errors = RestoreSuspension.PreviousErrors.ToArray();
            RestoreSuspension = null;
        }
        public IReadOnlyDictionary<string, CelestialCompSaveRecord> CompStateRecords { get; }
        public IReadOnlyDictionary<string, CelestialCompDescriptor> CompDescriptors { get; }

        public CelestialBodyInstance(CelestialBodyDescription description, CelestialGenerationOrigin? generationOrigin = null)
        {
            Description = description ?? throw new ArgumentNullException(nameof(description));
            GenerationOrigin = generationOrigin ?? new CelestialGenerationOrigin(0, description.Id.Value);
            Lifecycle = CelestialLifecycle.Staged;
            CompStateRecords = new ReadOnlyDictionary<string, CelestialCompSaveRecord>(records);
            CompDescriptors = new ReadOnlyDictionary<string, CelestialCompDescriptor>(descriptors);
        }

        internal void SetCompRecord(CelestialCompSaveRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            records[record.Key] = record;
        }
        internal void SetCompDescriptor(CelestialCompDescriptor descriptor) => descriptors[descriptor.Key] = descriptor;
    }
}
