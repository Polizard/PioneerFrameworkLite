using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed partial class CelestialCreationPipeline
    {
        private readonly Dictionary<CelestialInstanceId, CelestialSavedBody> unavailableBodies = new();
        private readonly List<CelestialSavedCreation> frozenCreations = new();
        internal IReadOnlyDictionary<string, CelestialInstanceId> GetInitializationReferences(CelestialInstanceId id) => assemblies.TryGetValue(id, out var assembly) ?
            assembly.Spawn.InitializationReferences : unavailableBodies.TryGetValue(id, out var unavailable) && unavailable.Assembly != null ?
                unavailable.Assembly.InitializationReferences : new Dictionary<string, CelestialInstanceId>();
        internal void SetRestoredClock(PhysicsSampleStamp stamp)
        { PhysicsTopologyRevision = stamp.TopologyRevision; PhysicalSeconds = stamp.PhysicalSeconds; }
        internal IReadOnlyList<CelestialSavedBody> CaptureBodies()
        {
            var result = new List<CelestialSavedBody>();
            foreach (var instance in catalog.CaptureInstances())
            {
                CelestialSavedAssembly? metadata = null;
                if (assemblies.TryGetValue(instance.Id, out var assembly))
                {
                    if (instance.Lifecycle is CelestialLifecycle.Ready or CelestialLifecycle.Registering or CelestialLifecycle.Removing && assembly.Components.All(component => component.IsAttached || component.IsDisabled)) Capture(assembly);
                    var spawn = assembly.Spawn;
                    metadata = new(spawn.EntryKey, spawn.Index, spawn.GenerationPath, spawn.Template.Label, spawn.InitializationReferences, assembly.Registration);
                }
                else if (unavailableBodies.TryGetValue(instance.Id, out var unavailable)) metadata = unavailable.Assembly;
                result.Add(new(instance.Description, instance.GenerationOrigin, instance.Lifecycle, instance.CommittedMembership, instance.ParentId,
                    instance.Errors.ToArray(), instance.CompDescriptors.Values.OrderBy(d => d.Key, StringComparer.Ordinal).ToArray(),
                    instance.CompStateRecords.Values.OrderBy(record => record.Key, StringComparer.Ordinal).ToArray(), metadata) { RestoreSuspension = instance.RestoreSuspension, OperationEpoch = instance.OperationEpoch, FailedCreation = instance.FailedCreation,
                    RootSurfaceCandidate = instance.RootSurfaceCandidate, ComponentFaults = instance.ComponentFaults.Values.OrderBy(f => f.Key, StringComparer.Ordinal).ToArray() });
            }
            return result.AsReadOnly();
        }
        internal IReadOnlyList<CelestialSavedCreation> CaptureCreations() => batches.Values
            .Where(batch => operations.Get(batch.OperationId).Status == CelestialOperationStatus.Pending)
            .Select(batch => new CelestialSavedCreation(batch.OperationId, batch.Manifest.SystemDefinitionId, batch.Manifest.WorldSeed, batch.Manifest.RootSurfaceId,
                batch.Assemblies.Select(assembly => assembly.Instance.Id).ToArray(), batch.HasPhysics, batch.CompensationId, batch.Failure.ToArray()))
            .Concat(frozenCreations).OrderBy(batch => batch.OperationId, StringComparer.Ordinal).ToArray();
        internal void RestoreAssemblies(CelestialSaveDocument document, CelestialCompRegistry registry)
        {
            registry.Seal(); RootSurfaceId = document.RootSurfaceId;
            foreach (var saved in document.Bodies)
            {
                catalog.TryGetInstance(saved.Description.Id, out var instance);
                unavailableBodies.Add(instance.Id, saved);
                if (saved.Lifecycle == CelestialLifecycle.Removed || saved.FailedCreation) continue;
                var decoded = Array.Empty<CelestialRestoredComp>();
                try
                {
                var disabled = saved.ComponentFaults.Where(f => f.Disabled).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
                decoded = saved.Components.Where(record => !disabled.Contains(record.Key)).Select(record => CelestialCompStateCodec.Decode(record, registry).Value).ToArray();
                var errors = decoded.SelectMany(component => component.Errors).ToList();
                var graph = CelestialCompGraph.ResolveActive(saved.Descriptors, disabled);
                if (!graph.Success) errors.AddRange(graph.Errors);
                if (saved.Assembly == null) errors.Add(new CelestialError("AssemblyMetadataMissing", instance.Id));
                if (errors.Count > 0)
                {
                    BlockRestored(instance, decoded, errors); continue;
                }
                var ordered = graph.Value.Select(descriptor => {
                    var value = decoded.Single(comp => comp.OriginalRecord.Key == descriptor.Key);
                    value.Comp!.BindDescriptor(descriptor);
                    if (instance.ComponentFaults.TryGetValue(descriptor.Key, out var fault)) value.Comp.RecordFailure(fault.Error);
                    return new CelestialResolvedComp(descriptor, value.Properties!, registry.TryGet(descriptor.HandlerId, out var handler) ? handler.Factory : throw new InvalidOperationException());
                }).ToArray();
                var properties = ordered.Select(component => component.Properties).ToArray();
                if (properties.OfType<ICelestialGeometryProperties>().SingleOrDefault()?.RadiusMeters != saved.Description.RadiusMeters ||
                    properties.OfType<ICelestialPhysicsBindingProperties>().SingleOrDefault()?.GravitationalParameter != saved.Description.GravitationalParameter)
                    errors.Add(new CelestialError("SavedDescriptorMismatch", instance.Id));
                foreach (var validator in properties.OfType<ICelestialResolvedConfigurationValidator>()) errors.AddRange(validator.Validate(properties).Errors);
                if (errors.Count > 0) { BlockRestored(instance, decoded, errors); continue; }
                var template = CelestialBodyTemplate.Create(saved.Description.DefinitionId, saved.Assembly!.Label, saved.Description.Category, saved.Description.Tags,
                    ordered.Select(resolved => new CelestialCompDefinition(resolved.Descriptor, resolved.Factory, _ => CelestialResult.Ok(resolved.Properties)))).Value;
                var spawn = new CelestialSpawnItem(instance.Id, saved.Assembly.EntryKey, saved.Assembly.Index, saved.Assembly.GenerationPath, template, ordered,
                    saved.ParentId, saved.Assembly.InitializationReferences);
                var assembly = new Assembly(instance, spawn) { Registration = saved.Assembly.Registration };
                assembly.Components.AddRange(graph.Value.Select(descriptor => decoded.Single(comp => comp.OriginalRecord.Key == descriptor.Key).Comp!));
                assemblies.Add(instance.Id, assembly);
                unavailableBodies.Remove(instance.Id);
                }
                catch (Exception exception)
                { BlockRestored(instance, decoded, new[] { new CelestialError("ComponentRestoreFailed", instance.Id, Detail: exception.Message) }); }
            }
            foreach (var saved in document.Creations)
            {
                if (saved.InstanceIds.Any(id => !assemblies.ContainsKey(id))) { frozenCreations.Add(saved); continue; }
                var manifest = new CelestialSpawnManifest(saved.SystemDefinitionId, saved.WorldSeed, saved.RootSurfaceId, saved.InstanceIds.Select(id => assemblies[id].Spawn));
                var batch = new Batch(saved.OperationId, manifest) { HasPhysics = saved.HasPhysics, CompensationId = saved.CompensationId, Failure = saved.Failure };
                batch.Assemblies.AddRange(saved.InstanceIds.Select(id => assemblies[id])); batches.Add(batch.OperationId, batch);
            }
        }
        private static void BlockRestored(CelestialBodyInstance instance, IEnumerable<CelestialRestoredComp> decoded, IEnumerable<CelestialError> errors)
        {
            var retained = errors.ToList();
            foreach (var component in decoded)
            {
                try { component.Comp?.Release(); }
                catch (Exception exception) { retained.Add(new CelestialError("ComponentReleaseFailed", instance.Id, component.OriginalRecord.Key, exception.Message)); }
            }
            instance.SuspendForRestore(retained);
        }
        internal CelestialResult<bool> AttachRestoredComponents()
        {
            foreach (var assembly in assemblies.Values)
            {
                foreach (var component in assembly.Components)
                {
                    if (component.IsAttached || component.IsDisabled) continue;
                    try { component.Attach(new Context(this, assembly,
                        assembly.Resolved.Single(resolved => resolved.Descriptor.Key == component.Descriptor.Key).Properties), CelestialAttachReason.Restored); }
                    catch (Exception exception) { HandleComponentFault(assembly.Instance, component, "ComponentAttachFailed", exception); }
                    if (assembly.Instance.Lifecycle == CelestialLifecycle.Blocked && assembly.Instance.RestoreSuspension == null) break;
                }
            }
            return CelestialResult.Ok(true);
        }
        internal void NotifyRestoredComponents()
        {
            foreach (var assembly in assemblies.Values)
                foreach (var component in assembly.Components.Where(component => component.IsAttached && component.Failure == null))
                {
                    try { component.NotifyRestored(); }
                    catch (Exception exception) { HandleComponentFault(assembly.Instance, component, "ComponentRestoredFailed", exception); }
                }
        }
        internal CelestialResult<bool> ResumeRestoredCreations()
        {
            var errors = new List<CelestialError>();
            foreach (var batch in batches.Values.Where(batch => operations.Get(batch.OperationId).Status == CelestialOperationStatus.Pending && batch.HasPhysics))
            {
                if (batch.Assemblies.Any(assembly => assembly.Instance.Lifecycle == CelestialLifecycle.Blocked) && batch.CompensationId == null) continue;
                var changes = batch.CompensationId == null ? batch.Assemblies.Where(a => a.Registration != null).Select(a => PhysicsBodyChange.Register(a.Registration!)) :
                    batch.Assemblies.Where(a => a.Instance.CommittedMembership).Select(a => PhysicsBodyChange.Unregister(a.Instance.Id));
                var receipt = physics.Submit(new PhysicsChangeSet(batch.CompensationId ?? batch.PhysicsId, WorldSessionId, PhysicsTopologyRevision, changes));
                if (receipt.Status == PhysicsOperationStatus.Rejected) errors.AddRange(receipt.Errors);
            }
            RetryFrozenCompensations(rebindPending: true);
            return errors.Count == 0 ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(errors.ToArray());
        }
        internal void CompleteRestoredSuspensions()
        {
            foreach (var assembly in assemblies.Values)
                if (assembly.Components.All(component => component.IsAttached || component.IsDisabled)) assembly.Instance.CompleteRestoreSuspension();
        }
    }
}
