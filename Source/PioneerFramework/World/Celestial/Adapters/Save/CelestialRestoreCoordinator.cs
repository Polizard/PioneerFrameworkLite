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
        private readonly Dictionary<CelestialInstanceId, double> legacySurfaceRadii = new();
        private readonly List<(CelestialSurfaceHeader Header, double Radius)> correctedHeaders = new();
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
                saved = RestoreLegacyDescriptorNumbers(saved);
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
                correctedHeaders.Clear();
                var surfaces = Runtime.Surfaces ?? new CelestialSurfaceService(Runtime, definition);
                var maps = Runtime.Maps ?? new CelestialMapService(Runtime, surfaces, mapBackend);
                foreach (var site in document.Maps)
                {
                    var restored = maps.RestoreSite(site);
                    if (!restored.Success) return restored;
                }
                foreach (var saved in document.Surfaces)
                {
                    if (!Find.WorldGrid.PlanetLayers.TryGetValue(saved.LayerId, out var layer) || layer is not CelestialSurfaceLayer surface)
                        return Reject("SurfaceIdentityMismatch", FormattableString.Invariant($"{saved.OwnerId.Value}: layer {saved.LayerId} is missing"));
                    var header = surface.Header;
                    if (header.OwnerId != saved.OwnerId || header.Subdivisions != saved.Subdivisions ||
                        header.GenerationSeed != saved.GenerationSeed || header.State != saved.State ||
                        !header.GenerationConfiguration.Span.SequenceEqual(saved.GenerationConfiguration.Span))
                        return Reject("SurfaceIdentityMismatch", saved.OwnerId.Value + ": native header differs from the checkpoint");
                    bool physicalRadiusMatches = header.PhysicalRadiusMeters == saved.PhysicalRadiusMeters ||
                        legacySurfaceRadii.TryGetValue(saved.OwnerId, out double legacyRadius) && header.PhysicalRadiusMeters == legacyRadius;
                    if (!physicalRadiusMatches || !MatchesNativeRadius(header, saved))
                        return Reject("SurfaceIdentityMismatch", FormattableString.Invariant(
                            $"{saved.OwnerId.Value}: physical radius {header.PhysicalRadiusMeters:G17}/{saved.PhysicalRadiusMeters:G17}, native radius {header.NativeRadius:G9}/{saved.NativeRadius:G9}"));
                    var restored = surfaces.RestoreHeader(header);
                    if (!restored.Success) return restored;
                    if (header.PhysicalRadiusMeters != saved.PhysicalRadiusMeters)
                        correctedHeaders.Add((header, saved.PhysicalRadiusMeters));
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

        private bool MatchesNativeRadius(CelestialSurfaceHeader header, CelestialSavedSurface saved)
        {
            if (header.NativeRadius == saved.NativeRadius) return true;

            // Older Waylith saves wrote the JSON single with seven significant digits.
            // Verify the exact native radius against the frozen surface scale before accepting that representation.
            var scale = Runtime.Creation.GetProperties(saved.OwnerId).OfType<SurfaceResolvedProperties>().SingleOrDefault();
            return scale != null && header.NativeRadius == (float)(saved.PhysicalRadiusMeters / scale.MetersPerLayerUnit) &&
                CelestialSaveNumbers.MatchesLegacySingle(saved.NativeRadius, header.NativeRadius);
        }

        private CelestialSaveDocument RestoreLegacyDescriptorNumbers(CelestialSaveDocument saved)
        {
            if (saved.PhysicsCheckpoint is not { ProviderId: ExistingCelestialEphemerisBridge.Id, PayloadVersion: 2 }) return saved;

            // The builtin bridge checks the reconstructed descriptors against the original
            // registration hash before it publishes any physics state.
            var bodies = saved.Bodies.Select(RestoreLegacyBody).ToArray();
            var physicalRadii = bodies.ToDictionary(body => body.Description.Id, body => body.Description.RadiusMeters);
            var surfaces = saved.Surfaces.Select(surface =>
            {
                double exact = physicalRadii[surface.OwnerId];
                if (surface.PhysicalRadiusMeters == exact) return surface;
                legacySurfaceRadii[surface.OwnerId] = surface.PhysicalRadiusMeters;
                return surface with { PhysicalRadiusMeters = exact };
            }).ToArray();
            return saved with { Bodies = bodies, Surfaces = surfaces };
        }

        private static CelestialSavedBody RestoreLegacyBody(CelestialSavedBody body)
        {
            if (!body.CommittedMembership || body.Assembly?.Registration is not { } registration) return body;
            var shape = body.Components.SingleOrDefault(record => record.HandlerId == SphericalBodyResolvedProperties.Handler);
            var motion = body.Components.SingleOrDefault(record => record.HandlerId == ExternalPhysicsResolvedProperties.Handler);
            if (shape == null || motion == null) return body;
            var geometry = SphericalBodyResolvedProperties.Deserialize(shape.Key, shape.ConfigurationVersion, shape.ConfigurationPayload);
            var physics = ExternalPhysicsResolvedProperties.Deserialize(motion.Key, motion.ConfigurationVersion, motion.ConfigurationPayload);
            if (!geometry.Success || !physics.Success) return body;

            var saved = body.Description;
            double radius = geometry.Value.RadiusMeters;
            double? gm = physics.Value.GravitationalParameter;
            if (saved.RadiusMeters == radius && saved.GravitationalParameter == gm ||
                !CelestialSaveNumbers.MatchesLegacyDouble(saved.RadiusMeters, radius) ||
                !CelestialSaveNumbers.MatchesLegacyDouble(saved.GravitationalParameter, gm)) return body;

            var description = new CelestialBodyDescription(saved.Id, saved.DefinitionId, saved.Handle, saved.DescriptorRevision,
                saved.Category, radius, gm, saved.Tags);
            var corrected = new CelestialBodyRegistration(registration.DefinitionId, new(saved.Id, saved.DescriptorRevision, radius, gm),
                registration.Initialization, registration.ParentInstanceId, registration.InitializationReferences, registration.CompatibilityHandle);
            return body with { Description = description, Assembly = body.Assembly with { Registration = corrected } };
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
                foreach (var (header, radius) in correctedHeaders) header.RestorePhysicalRadius(radius);
                correctedHeaders.Clear();
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
