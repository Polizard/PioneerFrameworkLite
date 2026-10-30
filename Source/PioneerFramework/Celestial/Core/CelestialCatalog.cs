using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialCatalogChangeSet
    {
        public long ExpectedRevision { get; }
        public IReadOnlyList<CelestialBodyDescription> Upserts { get; }
        public IReadOnlyList<CelestialInstanceId> Removed { get; }

        public CelestialCatalogChangeSet(long expectedRevision, IEnumerable<CelestialBodyDescription> upserts,
            IEnumerable<CelestialInstanceId>? removed = null)
        {
            ArgumentNullException.ThrowIfNull(upserts);
            ExpectedRevision = expectedRevision;
            Upserts = Array.AsReadOnly(upserts.ToArray());
            Removed = Array.AsReadOnly((removed ?? Array.Empty<CelestialInstanceId>()).ToArray());
        }
    }

    public sealed class CelestialCatalogSnapshot
    {
        private readonly ReadOnlyDictionary<CelestialInstanceId, CelestialBodyDescription> descriptions;
        public long Revision { get; }
        public IReadOnlyList<CelestialBodyDescription> Bodies { get; }

        internal CelestialCatalogSnapshot(long revision, IEnumerable<CelestialBodyDescription> bodies)
        {
            Revision = revision;
            var ordered = bodies.OrderBy(body => body.Handle).ToArray();
            Bodies = Array.AsReadOnly(ordered);
            descriptions = new ReadOnlyDictionary<CelestialInstanceId, CelestialBodyDescription>(ordered.ToDictionary(body => body.Id));
        }

        public bool TryGet(CelestialInstanceId id, out CelestialBodyDescription description)
            => descriptions.TryGetValue(id, out description!);

        public CelestialBodyDescription Get(CelestialInstanceId id) => descriptions[id];
    }

    /// <summary>集中验证后整体发布目录。旧快照持有旧事实，整数句柄与移除身份永久保留。</summary>
    public sealed class CelestialCatalog
    {
        private readonly object gate = new();
        private readonly Dictionary<CelestialInstanceId, CelestialBodyInstance> instances = new();
        private readonly Dictionary<int, CelestialInstanceId> handles = new();
        private CelestialCatalogSnapshot snapshot = new(0, Array.Empty<CelestialBodyDescription>());
        private long nextHandle;
        private CelestialBodyInstance[]? orderedInstances;

        public long Revision { get { lock (gate) return snapshot.Revision; } }
        public long NextHandle { get { lock (gate) return nextHandle; } }

        public int AllocateHandle()
        {
            lock (gate)
            {
                if (nextHandle > int.MaxValue) throw new InvalidOperationException("Celestial handle space exhausted.");
                return (int)nextHandle++;
            }
        }

        public CelestialResult<bool> Stage(CelestialBodyInstance instance)
        {
            ArgumentNullException.ThrowIfNull(instance);
            return StageBatch(new[] { instance });
        }

        public CelestialResult<bool> StageBatch(IReadOnlyList<CelestialBodyInstance> batch)
        {
            ArgumentNullException.ThrowIfNull(batch);
            lock (gate)
            {
                var newIds = new HashSet<CelestialInstanceId>();
                var newHandles = new HashSet<int>();
                foreach (var instance in batch)
                {
                    if (instance == null) return Reject<bool>("InvalidInstance");
                    if (instances.ContainsKey(instance.Id) || !newIds.Add(instance.Id)) return Reject<bool>("IdentityReserved", instance.Id);
                    if (handles.ContainsKey(instance.Description.Handle) || !newHandles.Add(instance.Description.Handle)) return Reject<bool>("HandleInUse", instance.Id);
                }
                foreach (var instance in batch)
                {
                    instances.Add(instance.Id, instance);
                    handles.Add(instance.Description.Handle, instance.Id);
                    nextHandle = Math.Max(nextHandle, (long)instance.Description.Handle + 1);
                }
                orderedInstances = null;
                return CelestialResult.Ok(true);
            }
        }

        public bool TryResolveHandle(int handle, out CelestialInstanceId id)
        {
            lock (gate) return handles.TryGetValue(handle, out id);
        }

        public bool TryGet(CelestialInstanceId id, out CelestialBodyDescription description)
        {
            lock (gate) return snapshot.TryGet(id, out description!);
        }

        public CelestialCatalogSnapshot Capture()
        {
            lock (gate) return snapshot;
        }

        internal bool TryGetInstance(CelestialInstanceId id, out CelestialBodyInstance instance)
        {
            lock (gate) return instances.TryGetValue(id, out instance!);
        }

        internal IReadOnlyList<CelestialBodyInstance> CaptureInstances()
        {
            lock (gate)
            {
                if (orderedInstances == null)
                {
                    CelestialCostCounters.CatalogSorted();
                    orderedInstances = instances.Values.OrderBy(instance => instance.Description.Handle).ToArray();
                }
                return orderedInstances;
            }
        }
        internal void Restore(CelestialSaveDocument document)
        {
            lock (gate)
            {
                if (instances.Count != 0 || snapshot.Revision != 0) throw new InvalidOperationException("Catalog already initialized.");
                foreach (var saved in document.Bodies)
                {
                    var instance = new CelestialBodyInstance(saved.Description, saved.GenerationOrigin) {
                        Lifecycle = saved.Lifecycle, CommittedMembership = saved.CommittedMembership, ParentId = saved.ParentId, Errors = saved.Errors.ToArray(),
                        RestoreSuspension = saved.RestoreSuspension, OperationEpoch = saved.OperationEpoch, FailedCreation = saved.FailedCreation, RootSurfaceCandidate = saved.RootSurfaceCandidate };
                    foreach (var fault in saved.ComponentFaults) instance.ComponentFaults.Add(fault.Key, fault);
                    foreach (var record in saved.Components) instance.SetCompRecord(record);
                    foreach (var descriptor in saved.Descriptors) instance.SetCompDescriptor(descriptor);
                    instances.Add(instance.Id, instance); handles.Add(instance.Description.Handle, instance.Id);
                }
                nextHandle = document.NextHandle;
                snapshot = new CelestialCatalogSnapshot(document.CatalogRevision, document.PublishedBodies.Select(id => instances[id].Description));
            }
        }

        public CelestialResult<CelestialCatalogSnapshot> Publish(CelestialCatalogChangeSet changes)
        {
            ArgumentNullException.ThrowIfNull(changes);
            lock (gate)
            {
                if (changes.ExpectedRevision != snapshot.Revision)
                    return Reject<CelestialCatalogSnapshot>("RevisionConflict");
                var changed = new HashSet<CelestialInstanceId>();
                foreach (CelestialBodyDescription description in changes.Upserts)
                {
                    if (description == null) return Reject<CelestialCatalogSnapshot>("InvalidDescriptor");
                    if (!changed.Add(description.Id)) return Reject<CelestialCatalogSnapshot>("DuplicateChange", description.Id);
                    if (!instances.TryGetValue(description.Id, out var instance) || instance.Lifecycle == CelestialLifecycle.Removed)
                        return Reject<CelestialCatalogSnapshot>("BodyMissing", description.Id);
                    if (instance.Lifecycle is CelestialLifecycle.Blocked or CelestialLifecycle.Removing)
                        return Reject<CelestialCatalogSnapshot>("BodyNotReady", description.Id);
                    if (instance.Description.Handle != description.Handle || instance.Description.DefinitionId != description.DefinitionId)
                        return Reject<CelestialCatalogSnapshot>("DescriptorIdentityMismatch", description.Id);
                    if (snapshot.TryGet(description.Id, out _) && description.DescriptorRevision <= instance.Description.DescriptorRevision)
                        return Reject<CelestialCatalogSnapshot>("DescriptorRevisionConflict", description.Id);
                    if (description.DescriptorRevision < instance.Description.DescriptorRevision)
                        return Reject<CelestialCatalogSnapshot>("DescriptorRevisionConflict", description.Id);
                }
                foreach (CelestialInstanceId id in changes.Removed)
                {
                    if (!changed.Add(id)) return Reject<CelestialCatalogSnapshot>("DuplicateChange", id);
                    if (!instances.TryGetValue(id, out var instance) || instance.Lifecycle == CelestialLifecycle.Removed)
                        return Reject<CelestialCatalogSnapshot>("BodyMissing", id);
                }
                if (changed.Count == 0) return CelestialResult.Ok(snapshot);

                var nextDescriptions = snapshot.Bodies.ToDictionary(description => description.Id);
                foreach (CelestialBodyDescription description in changes.Upserts) nextDescriptions[description.Id] = description;
                foreach (CelestialInstanceId id in changes.Removed) nextDescriptions.Remove(id);
                var nextSnapshot = new CelestialCatalogSnapshot(checked(snapshot.Revision + 1), nextDescriptions.Values);

                foreach (CelestialBodyDescription description in changes.Upserts)
                {
                    CelestialBodyInstance instance = instances[description.Id];
                    instance.Description = description;
                    instance.Lifecycle = CelestialLifecycle.Ready;
                }
                foreach (CelestialInstanceId id in changes.Removed)
                {
                    CelestialBodyInstance instance = instances[id];
                    instance.CommittedMembership = false;
                    instance.Lifecycle = CelestialLifecycle.Removed;
                }
                snapshot = nextSnapshot;
                return CelestialResult.Ok(snapshot);
            }
        }

        private static CelestialResult<T> Reject<T>(string code, CelestialInstanceId? id = null)
            => CelestialResult.Reject<T>(new CelestialError(code, id));
    }
}
