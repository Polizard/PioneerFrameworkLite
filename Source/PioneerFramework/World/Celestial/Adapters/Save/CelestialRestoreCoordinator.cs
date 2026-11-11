using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.MapGeneration;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialRestoreCoordinator
    {
        private readonly Guid session;
        private readonly ICelestialPhysicsBridge? physics;
        private readonly CelestialCompRegistry registry;
        private readonly Func<string, CelestialResult<CelestialBodyTemplate>> resolveTemplate;
        private readonly ICelestialMapBackend? mapBackend;
        private readonly ICelestialRemovalResources resources;
        private CelestialSaveDocument? document;
        private bool bound;
        private bool completed;
        public CelestialRuntime Runtime { get; private set; } = null!;
        public CelestialRestoreCoordinator(Guid worldSessionId, ICelestialPhysicsBridge? physics, CelestialCompRegistry registry,
            Func<string, CelestialResult<CelestialBodyTemplate>> resolveTemplate, ICelestialRemovalResources? resources = null, ICelestialMapBackend? mapBackend = null)
        {
            if (worldSessionId == Guid.Empty) throw new ArgumentException("Missing restore session.");
            session = worldSessionId; this.physics = physics; this.registry = registry; this.resolveTemplate = resolveTemplate;
            this.resources = resources ?? new EmptyResources(); this.mapBackend = mapBackend;
        }
        public CelestialResult<bool> RestoreIdentities(CelestialSaveDocument saved)
        {
            if (document != null) return Reject("RestoreAlreadyStarted");
            var valid = CelestialSaveCodec.Validate(saved);
            if (!valid.Success) return valid;
            CelestialCapabilityRegistry.Global.Seal();
            if (saved.Stamp.WorldSessionId == session) return Reject("RestoreSessionMustChange");
            try
            {
                Runtime = CelestialRuntime.Restore(saved, registry, physics ?? new UnavailableBridge(saved.PhysicsCheckpoint), session, resources, resolveTemplate);
                document = saved;
                if (physics == null)
                    foreach (var instance in Runtime.Catalog.CaptureInstances().Where(instance => instance.Lifecycle != CelestialLifecycle.Removed))
                        instance.SuspendForRestore(new[] { new CelestialError("ProviderUnavailable", instance.Id) });
                return CelestialResult.Ok(true);
            }
            catch (Exception exception) { return Reject("IdentityRestoreFailed", exception.Message); }
        }
        public CelestialResult<bool> BindNativeObjects()
        {
            if (document == null) return Reject("RestoreNotStarted");
            if (bound) return CelestialResult.Ok(true);
            var definition = DefDatabase<PlanetLayerDef>.GetNamedSilentFail("PF_CelestialSurface");
            if (definition == null || Find.World?.grid == null) return Reject("NativeWorldUnavailable");
            try
            {
                var surfaces = Runtime.Surfaces ?? new CelestialSurfaceService(Runtime, definition);
                var maps = Runtime.Maps ?? new CelestialMapService(Runtime, surfaces, mapBackend);
                foreach (var site in document.Maps)
                {
                    var restored = maps.RestoreSite(site);
                    if (!restored.Success) return restored;
                }
                foreach (var saved in document.Surfaces)
                {
                    if (!Find.WorldGrid.PlanetLayers.TryGetValue(saved.LayerId, out var layer) || layer is not CelestialSurfaceLayer surface ||
                        surface.Header.OwnerId != saved.OwnerId || surface.Header.PhysicalRadiusMeters != saved.PhysicalRadiusMeters || surface.Header.NativeRadius != saved.NativeRadius ||
                        surface.Header.Subdivisions != saved.Subdivisions || surface.Header.GenerationSeed != saved.GenerationSeed || surface.Header.State != saved.State ||
                        !surface.Header.GenerationConfiguration.Span.SequenceEqual(saved.GenerationConfiguration.Span)) return Reject("SurfaceIdentityMismatch", saved.OwnerId.Value);
                    var restored = surfaces.RestoreHeader(surface.Header);
                    if (!restored.Success) return restored;
                }
                foreach (var site in document.Maps.Where(site => !site.ParentRetired))
                {
                    if (!Find.WorldGrid.PlanetLayers.TryGetValue(site.Address.LayerId, out var layer)) return Reject("MapIdentityMismatch");
                    // Failed native provenance retains the requested address even when the native parent moved.
                    var parent = site.Generation is { State: MapGenerationState.Failed }
                        ? Find.WorldObjects.MapParents.SingleOrDefault(parent => parent.ID == site.MapParentId)
                        : Find.WorldObjects.MapParentAt(new(site.Address.TileId, layer));
                    if (parent == null || parent.ID != site.MapParentId || site.MapId.HasValue && parent.Map?.uniqueID != site.MapId) return Reject("MapIdentityMismatch");
                    // 半生成地图只核对保存身份，保留其失败诊断。
                    if (site.MapId.HasValue && site.Failure == null)
                    {
                        var restored = maps.BindExisting(site.Address, parent, parent.Map);
                        if (!restored.Success) return restored;
                    }
                }
                bound = true;
                return CelestialResult.Ok(true);
            }
            catch (Exception exception) { return Reject("NativeBindingFailed", exception.Message); }
        }
        public CelestialResult<bool> CompleteRestore()
        {
            if (completed) return CelestialResult.Ok(true);
            if (document == null) return Reject("RestoreNotStarted");
            if (physics == null) return Reject("ProviderUnavailable");
            if (!bound) return Reject("NativeBindingPending");
            if (document.PhysicsCheckpoint == null || document.PhysicsCheckpoint.ProviderId != physics.ProviderId) return Reject("ProviderUnavailable");
            try
            {
                var committed = document.CommittedBodies.Select(id => document.Bodies.Single(body => body.Description.Id == id).Assembly!.Registration!).ToArray();
                var restored = physics.RestoreCheckpoint(document.PhysicsCheckpoint, committed);
                if (!restored.Success) return CelestialResult.Reject<bool>(restored.Errors.ToArray());
                var stamp = restored.Stamp!.Value;
                if (stamp.WorldSessionId != session || stamp.TopologyRevision != document.Stamp.TopologyRevision || stamp.PhysicalSeconds != document.Stamp.PhysicalSeconds ||
                    stamp.StateRevision != document.Stamp.StateRevision || stamp.ReferenceFrameId != document.Stamp.ReferenceFrameId) return Reject("ProviderContractViolation");
                Runtime.Coordinator.RestoreStamp(stamp);
                var attached = Runtime.Creation.AttachRestoredComponents();
                if (!attached.Success) return attached;
                Runtime.Creation.CompleteRestoredSuspensions();
                // 更新投递修订，不推进待恢复事务；原生绑定完成后才以原 OperationId 重交。
                Runtime.Creation.SetRestoredClock(stamp);
                var creation = Runtime.Creation.ResumeRestoredCreations();
                if (!creation.Success) return creation;
                var removal = Runtime.Removal.ResumeRestoredRemovals();
                if (!removal.Success) return removal;
                Runtime.EndRestore(); completed = true;
                Runtime.Creation.NotifyRestoredComponents();
                return CelestialResult.Ok(true);
            }
            catch (Exception exception) { return Reject("PhysicsRestoreFailed", exception.Message); }
        }
        private static CelestialResult<bool> Reject(string code, string detail = "") => CelestialResult.Reject<bool>(new CelestialError(code, Detail: detail));
        private sealed class EmptyResources : ICelestialRemovalResources
        {
            public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id) => Array.Empty<CelestialError>();
            public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
        }
        private sealed class UnavailableBridge(PhysicsCheckpoint? checkpoint) : ICelestialPhysicsBridge
        {
            public string ProviderId => checkpoint?.ProviderId ?? "Unavailable";
            public PhysicsContractVersion ContractVersion => checkpoint?.ContractVersion ?? PhysicsContractVersion.Current;
            public PhysicsBridgeStatus Status => PhysicsBridgeStatus.Unavailable;
            public PhysicsReferenceFrame ReferenceFrame => new(checkpoint?.Stamp.ReferenceFrameId ?? "Unavailable", "Unavailable", "Unavailable", "Unavailable");
            public PhysicsOperation Submit(PhysicsChangeSet request) => GetOperation(request.OperationId);
            public PhysicsOperation GetOperation(string id) => new(id, PhysicsOperationStatus.Rejected, new[] { new CelestialError("ProviderUnavailable") });
            public PhysicsIntervalPreparation PrepareInterval(PhysicsIntervalRequest request) => PhysicsIntervalPreparation.Reject(new CelestialError("ProviderUnavailable"));
            public void CommitPreparedInterval(PhysicsIntervalToken token) => throw new InvalidOperationException("ProviderUnavailable");
            public void AbortPreparedInterval(PhysicsIntervalToken token) { }
            public PhysicsSnapshotResult Sample(PhysicsSampleRequest request) => PhysicsSnapshotResult.Reject(new CelestialError("ProviderUnavailable"));
            public PhysicsCheckpoint CaptureCheckpoint() => checkpoint ?? throw new InvalidOperationException("ProviderUnavailable");
            public PhysicsRestoreResult RestoreCheckpoint(PhysicsCheckpoint saved, IReadOnlyList<CelestialBodyRegistration> bodies) => PhysicsRestoreResult.Reject(new CelestialError("ProviderUnavailable"));
        }
    }
}
