using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    public interface ICelestialSurfaceGenerator
    {
        CelestialResult<bool> Generate(CelestialSurfaceLayer layer, CelestialSurfaceHeader header);
    }
    public sealed class CelestialSurfaceService : ICelestialRemovalResources, ICelestialCommandHandler
    {
        private readonly CelestialRuntime runtime;
        private readonly WorldGrid grid;
        private readonly Game game;
        private readonly PlanetLayerDef definition;
        private readonly ICelestialSurfaceGenerator generator;
        private readonly Dictionary<CelestialInstanceId, int> associations = new();
        public long ResourceRevision { get; private set; }
        public CelestialSurfaceService(CelestialRuntime runtime, PlanetLayerDef definition, ICelestialSurfaceGenerator? generator = null)
        {
            this.runtime = runtime;
            this.definition = definition;
            grid = Verse.Find.WorldGrid ?? throw new InvalidOperationException("WorldGrid unavailable.");
            game = Current.Game ?? throw new InvalidOperationException("Game unavailable.");
            this.generator = generator ?? new NativeGenerator();
            if (!typeof(CelestialSurfaceLayer).IsAssignableFrom(definition.layerType)) throw new ArgumentException("Celestial surface layer type required.", nameof(definition));
            var attached = runtime.AttachSurfaces(this);
            if (!attached.Success) throw new InvalidOperationException(attached.Errors[0].Code);
            var registered = runtime.RegisterCommandHandler(CompSurface.RequestCommand, this);
            if (!registered.Success) throw new InvalidOperationException(registered.Errors[0].Code);
            grid.OnPlanetLayerRemoved += NativeLayerRemoved;
        }
        private static string RequestHash(CelestialInstanceId id) => "PFL.Celestial.Surface.v1:" + id.Value;
        internal void StopObservingNativeChanges() => grid.OnPlanetLayerRemoved -= NativeLayerRemoved;
        private void NativeLayerRemoved(PlanetLayer layer)
        {
            if (!ReferenceEquals(Current.Game, game) || !ReferenceEquals(Verse.Find.WorldGrid, grid) || runtime.IsRestoring ||
                layer is not CelestialSurfaceLayer surface || surface.Header == null || runtime.Creation.RootSurfaceId == surface.Header.OwnerId) return;
            runtime.WithPublication(() => {
                var id = surface.Header.OwnerId;
                if (!associations.TryGetValue(id, out int known) || known != layer.LayerID || grid.PlanetLayers.ContainsKey(known)) return false;
                associations.Remove(id);
                ResourceRevision++;
                // Keep historical Applied receipts, but persist that their produced native identity has retired.
                // An old ID must never bind a subsequently regenerated layer, including a reused numeric layer ID.
                foreach (var record in runtime.Operations.Capture().Where(record => record.ContentHash == RequestHash(id) &&
                    record.Operation.Status == CelestialOperationStatus.Applied && !record.Operation.Errors.Any(error => error.Code == "SurfaceRetired")))
                {
                    var operation = record.Operation;
                    runtime.Operations.Update(new(operation.OperationId, operation.Status, operation.InstanceIds,
                        operation.Errors.Append(new CelestialError("SurfaceRetired", id, Detail: known.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                        operation.AppliedRevision, operation.RelatedOperationId));
                }
                return true;
            });
        }
        public CelestialResult<CelestialSurfaceHandle> Find(CelestialInstanceId id)
        {
            if (!ReferenceEquals(grid, Verse.Find.WorldGrid)) return Reject<CelestialSurfaceHandle>("WorldChanged", id);
            if (!associations.TryGetValue(id, out int layerId)) return Reject<CelestialSurfaceHandle>("SurfaceNotCreated", id);
            if (!grid.PlanetLayers.TryGetValue(layerId, out var layer) || layer is not CelestialSurfaceLayer surface || surface.Header.OwnerId != id)
                return Reject<CelestialSurfaceHandle>("SurfaceIdentityMismatch", id);
            return surface.Header.State == CelestialSurfaceState.Ready ? CelestialResult.Ok(new CelestialSurfaceHandle(id, layerId)) :
                Reject<CelestialSurfaceHandle>("SurfaceNotReady", id);
        }
        public CelestialSurfaceOperation Request(CelestialSurfaceRequest request)
        {
            var result = runtime.WithPublication(() => RequestCore(request));
            if (result.Operation.Status != CelestialOperationStatus.Pending || runtime.IsPublicationHeld || runtime.IsExecutingCallbacks || !runtime.HostWork.IsHostThread) return result;
            runtime.ProcessHostWork();
            return runtime.WithPublication(() => RequestCore(request));
        }
        private CelestialSurfaceOperation RequestCore(CelestialSurfaceRequest request)
        {
            string operationId = request.Context.OperationId;
            if (request.Context.WorldSessionId != runtime.WorldSessionId) return Failure(operationId, request.InstanceId, "StaleSession");
            if (!ReferenceEquals(grid, Verse.Find.WorldGrid)) return Failure(operationId, request.InstanceId, "WorldChanged");
            var ticket = runtime.Operations.Begin(request.Context, RequestHash(request.InstanceId), runtime.Catalog.Revision, new[] { request.InstanceId });
            if (!ticket.Success) return new CelestialSurfaceOperation(new CelestialOperation(operationId, CelestialOperationStatus.Rejected, errors: ticket.Errors));
            if (!ticket.Value.IsNew)
            {
                if (ticket.Value.Operation.Errors.Any(error => error.Code == "SurfaceRetired")) return Failure(operationId, request.InstanceId, "SurfaceRetired");
                var known = Find(request.InstanceId);
                return new CelestialSurfaceOperation(ticket.Value.Operation, known.Success ? known.Value : null);
            }
            CelestialSurfaceOperation Fail(IEnumerable<CelestialError> errors)
            {
                var result = new CelestialOperation(operationId, CelestialOperationStatus.Rejected, new[] { request.InstanceId }, errors);
                runtime.WithPublication(() => { runtime.Operations.Update(result); return true; });
                return new CelestialSurfaceOperation(result);
            }
            if (!runtime.Catalog.TryGetInstance(request.InstanceId, out var instance) || instance.Lifecycle != CelestialLifecycle.Ready)
                return Fail(new[] { new CelestialError("BodyNotReady", request.InstanceId) });
            if (associations.ContainsKey(request.InstanceId))
            {
                var existing = Find(request.InstanceId);
                return existing.Success ? Applied(operationId, existing.Value) : Fail(existing.Errors);
            }
            if (grid.PlanetLayers.Values.OfType<CelestialSurfaceLayer>().Any(layer => layer.Header.OwnerId == request.InstanceId))
                return Fail(new[] { new CelestialError("SurfaceIdentityMismatch", request.InstanceId) });
            var properties = runtime.Creation.GetProperties(request.InstanceId).OfType<SurfaceResolvedProperties>().SingleOrDefault();
            if (properties == null) return Fail(new[] { new CelestialError("SurfaceCapabilityMissing", request.InstanceId) });
            double nativeRadius = instance.Description.RadiusMeters / properties.MetersPerLayerUnit;
            if (!float.IsFinite((float)nativeRadius) || (float)nativeRadius <= 0) return Fail(new[] { new CelestialError("InvalidSurfaceScale", request.InstanceId) });
            var header = CelestialSurfaceHeader.Create(instance, properties);
            long expectedDescriptorRevision = instance.Description.DescriptorRevision;
            return new(runtime.HostWork.Enqueue(operationId, instance.Id, expectedDescriptorRevision, () => Generate().Operation));
            CelestialSurfaceOperation Generate()
            {
            var existing = runtime.WithPublication(() => associations.ContainsKey(instance.Id) ? Find(instance.Id) : null);
            if (existing != null)
                return existing.Success ? runtime.WithPublication(() => Applied(operationId, existing.Value)) : Fail(existing.Errors);
            CelestialSurfaceLayer? created = null;
            var previousIds = grid.PlanetLayers.Keys.ToHashSet();
            try
            {
                created = RuntimePlanetLayerService.RegisterCelestialInstance(grid, definition, header);
                runtime.WithPublication(() => { associations.Add(request.InstanceId, created.LayerID); ResourceRevision++; return true; });
                var generated = RuntimePlanetLayerService.GenerateCelestialPreservingRand(header.GenerationSeed, () => generator.Generate(created, created.Header));
                if (!generated.Success) throw new SurfaceGenerationException(generated.Errors);
                if (!generated.Value || created.Header.State == CelestialSurfaceState.Failed || created.TilesCount == 0 || created.TilesCount != created.Tiles.Count)
                    throw new InvalidOperationException("Surface generation did not complete all native tiles.");
                if (!ReferenceEquals(grid, Verse.Find.WorldGrid) || !runtime.IsHostJobCurrent(request.Context.WorldSessionId, instance.Id, expectedDescriptorRevision))
                    throw new InvalidOperationException("HostWorkIdentityChanged");
                return runtime.WithPublication(() => {
                    if (!associations.TryGetValue(instance.Id, out int layerId) || layerId != created.LayerID ||
                        !grid.PlanetLayers.TryGetValue(layerId, out var registeredLayer) || !ReferenceEquals(registeredLayer, created))
                        throw new SurfaceGenerationException(new[] { new CelestialError("SurfaceIdentityMismatch", instance.Id) });
                    created.Header.State = CelestialSurfaceState.Ready;
                    return Applied(operationId, new CelestialSurfaceHandle(instance.Id, created.LayerID));
                });
            }
            catch (Exception exception)
            {
                created ??= grid.PlanetLayers.Values.OfType<CelestialSurfaceLayer>().FirstOrDefault(layer => !previousIds.Contains(layer.LayerID) && layer.Header.OwnerId == instance.Id);
                var errors = exception is SurfaceGenerationException rejected ? rejected.Errors.ToList() : new List<CelestialError> {
                    new("SurfaceGenerationFailed", request.InstanceId, Detail: exception.Message) };
                if (created != null && grid.PlanetLayers.TryGetValue(created.LayerID, out var registeredLayer) && ReferenceEquals(registeredLayer, created))
                {
                    created.Header.State = CelestialSurfaceState.Failed;
                    // A failed job may only clean up its own association, never overwrite another job's ready layer.
                    runtime.WithPublication(() => { associations.TryAdd(instance.Id, created.LayerID); return true; });
                    try
                    {
                        var cleanup = RuntimePlanetLayerService.RemoveCelestialInstance(created);
                        if (cleanup.Success && cleanup.Value)
                            runtime.WithPublication(() => { if (associations.GetValueOrDefault(instance.Id, -1) == created.LayerID) associations.Remove(instance.Id); return true; });
                        else
                        {
                            errors.Add(new CelestialError("SurfaceCleanupBlocked", instance.Id));
                            errors.AddRange(cleanup.Errors);
                        }
                    }
                    catch (Exception cleanupException) { errors.Add(new CelestialError("SurfaceCleanupBlocked", instance.Id, Detail: cleanupException.Message)); }
                }
                return Fail(errors);
            }
        }
        }
        private CelestialSurfaceOperation Applied(string operationId, CelestialSurfaceHandle handle)
        {
            var operation = new CelestialOperation(operationId, CelestialOperationStatus.Applied, new[] { handle.OwnerId }, appliedRevision: runtime.Catalog.Revision);
            runtime.Operations.Update(operation);
            return new CelestialSurfaceOperation(operation, handle);
        }

        public CelestialResult<bool> RestoreHeader(CelestialSurfaceHeader header)
        {
            if (!grid.PlanetLayers.TryGetValue(header.LayerId, out var layer) || layer is not CelestialSurfaceLayer surface ||
                surface.Header.OwnerId != header.OwnerId || surface.Header.PhysicalRadiusMeters != header.PhysicalRadiusMeters ||
                surface.Header.NativeRadius != header.NativeRadius || surface.Header.Subdivisions != header.Subdivisions ||
                surface.Header.GenerationSeed != header.GenerationSeed || surface.Header.State != header.State ||
                !surface.Header.GenerationConfiguration.Span.SequenceEqual(header.GenerationConfiguration.Span))
                return Reject<bool>("SurfaceIdentityMismatch", header.OwnerId);
            if (associations.TryGetValue(header.OwnerId, out int existing) && existing != header.LayerId) return Reject<bool>("SurfaceIdentityMismatch", header.OwnerId);
            if (grid.PlanetLayers.Values.OfType<CelestialSurfaceLayer>().Count(candidate => candidate.Header.OwnerId == header.OwnerId) != 1)
                return Reject<bool>("AmbiguousSurfaceOwner", header.OwnerId);
            if (!associations.ContainsKey(header.OwnerId)) ResourceRevision++;
            associations[header.OwnerId] = header.LayerId;
            return CelestialResult.Ok(true);
        }
        public IReadOnlyList<CelestialSurfaceHeader> CaptureHeaders()
        {
            if (!ReferenceEquals(grid, Verse.Find.WorldGrid)) throw new InvalidOperationException("WorldChanged");
            var result = new List<CelestialSurfaceHeader>();
            foreach (var pair in associations.OrderBy(pair => pair.Value))
            {
                if (!grid.PlanetLayers.TryGetValue(pair.Value, out var layer) || layer is not CelestialSurfaceLayer surface || surface.Header.OwnerId != pair.Key)
                    throw new InvalidOperationException("SurfaceIdentityMismatch: " + pair.Key.Value);
                result.Add(surface.Header.WithLayerId(pair.Value));
            }
            return result.AsReadOnly();
        }
        public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id)
        {
            if (!associations.TryGetValue(id, out int layerId)) return Array.Empty<CelestialError>();
            if (!grid.PlanetLayers.TryGetValue(layerId, out var layer) || layer is not CelestialSurfaceLayer surface || surface.Header.OwnerId != id)
                return new[] { new CelestialError("SurfaceIdentityMismatch", id) };
            return RuntimePlanetLayerService.InspectCelestialContents(surface);
        }
        public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id)
        {
            if (!associations.TryGetValue(id, out int layerId)) return CelestialResult.Ok(true);
            if (!grid.PlanetLayers.TryGetValue(layerId, out var layer) || layer is not CelestialSurfaceLayer surface || surface.Header.OwnerId != id)
                return Reject<bool>("SurfaceIdentityMismatch", id);
            var removed = RuntimePlanetLayerService.RemoveCelestialInstance(surface);
            if (removed.Success && removed.Value && associations.Remove(id)) ResourceRevision++;
            return removed;
        }
        public CelestialResult<bool> Execute(CelestialCommand command)
        {
            var result = Request(new CelestialSurfaceRequest(new(command.OperationId + "/surface", runtime.WorldSessionId, runtime.Catalog.Revision), command.TargetId));
            return result.Operation.Status is CelestialOperationStatus.Applied or CelestialOperationStatus.Pending ? CelestialResult.Ok(true) : CelestialResult.Reject<bool>(result.Operation.Errors.ToArray());
        }
        private static CelestialSurfaceOperation Failure(string operationId, CelestialInstanceId id, string code)
            => new(new CelestialOperation(operationId, CelestialOperationStatus.Rejected, errors: new[] { new CelestialError(code, id) }));
        private static CelestialResult<T> Reject<T>(string code, CelestialInstanceId id) => CelestialResult.Reject<T>(new CelestialError(code, id));
        private sealed class NativeGenerator : ICelestialSurfaceGenerator
        {
            public CelestialResult<bool> Generate(CelestialSurfaceLayer layer, CelestialSurfaceHeader header)
            {
                layer.RunWorldGeneration(header.GenerationSeed);
                return CelestialResult.Ok(true);
            }
        }
        private sealed class SurfaceGenerationException(IReadOnlyList<CelestialError> errors) : Exception("Surface generation rejected.")
        {
            public IReadOnlyList<CelestialError> Errors { get; } = errors;
        }
    }
}
