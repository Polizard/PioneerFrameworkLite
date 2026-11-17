using System;
using System.Collections.Generic;
using System.Linq;
using PioneerFramework.Celestial;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class CelestialBodyManager
    {
        private readonly Game? hostGame;
        private long instanceCatalogRevision = -1;
        private Guid instanceSession;
        private bool instanceRestorePending;
        private long primaryLightCatalog = -1;
        private long primaryLightFaults = -1;
        private Guid primaryLightSession;
        private CelestialInstanceId? primaryLightId;
        private ICelestialPhysicsIntervalSampler? celestialIntervalSampler;
        private PhysicsSampleStamp? celestialIntervalCommittedStamp;
        private PhysicsSampleStamp CommittedMotionStamp => celestialIntervalCommittedStamp ?? celestialRuntime!.CurrentStamp;
        // Preparation owns the sampler; workers must not acquire the host publication lock for its clock.
        internal double MotionSampleSeconds => celestialIntervalSampler?.StartSeconds ?? celestialRuntime!.CurrentStamp.PhysicalSeconds;
        internal IDisposable UseCelestialInterval(ICelestialPhysicsIntervalSampler sampler)
        {
            if (celestialRuntime == null || celestialIntervalSampler != null) throw new InvalidOperationException("CelestialIntervalScopeUnavailable");
            EnsureInitialized();
            var sampled = sampler.Sample(sampler.StartSeconds, Array.Empty<CelestialInstanceId>());
            if (!sampled.Success) throw new InvalidOperationException("CelestialIntervalScopeUnavailable");
            using (var snapshot = sampled.Snapshot!)
                if (snapshot.Stamp.WorldSessionId != celestialRuntime.WorldSessionId || snapshot.Stamp.ReferenceFrameId != celestialRuntime.Physics.ReferenceFrame.Id ||
                    snapshot.Stamp.SampleKind != PhysicsSampleKind.AuthoritativeInterval || snapshot.Stamp.PhysicalSeconds != sampler.StartSeconds)
                    throw new InvalidOperationException("CelestialIntervalScopeMismatch");
            celestialIntervalCommittedStamp = celestialRuntime.CurrentStamp;
            celestialIntervalSampler = sampler;
            ResetMotionCaches();
            return new IntervalScope(this, sampler);
        }
        private void ResetMotionCaches()
        { parentSamples.Reset(); absSamples.Reset(); systemSourcesPrefetch.Clear(); }
        private sealed class IntervalScope(CelestialBodyManager owner, ICelestialPhysicsIntervalSampler sampler) : IDisposable
        {
            private bool disposed;
            public void Dispose()
            {
                if (disposed) return;
                if (!ReferenceEquals(owner.celestialIntervalSampler, sampler)) throw new InvalidOperationException("CelestialIntervalScopeMismatch");
                owner.celestialIntervalSampler = null;
                owner.celestialIntervalCommittedStamp = null;
                owner.ResetMotionCaches();
                disposed = true;
            }
        }
        public CelestialMotionState GetBodyMotion(int handle, double seconds)
        {
            var body = GetBodyById(handle) ?? throw new InvalidOperationException("BodyMissing: " + handle);
            if (celestialRuntime != null) return SampleInstanceMotion(body, seconds);
            double omega = OrbitAdapter.AngularVelocity(body);
            double half = -omega * seconds * 0.5;
            return GetOfflineBodyMotion(body, seconds,
                new(0, Math.Sin(half), 0, Math.Cos(half)), new(0, -omega, 0));
        }
        private CelestialBodyInfo? GetPrimaryLightInstance()
        {
            var runtime = celestialRuntime!;
            long revision = runtime.Catalog.Revision;
            long faults = runtime.Creation.FaultRevision;
            if (primaryLightSession == runtime.WorldSessionId && primaryLightCatalog == revision && primaryLightFaults == faults)
                return primaryLightId.HasValue ? bodiesById.Values.SingleOrDefault(body => body.InstanceId == primaryLightId.Value) : null;
            primaryLightSession = runtime.WorldSessionId; primaryLightCatalog = revision; primaryLightFaults = faults; primaryLightId = null;
            if (runtime.Creation.RootSurfaceId is { } root)
            {
                var profile = runtime.Creation.GetProperties(root).OfType<ExistingEnvironmentResolvedProperties>().SingleOrDefault();
                if (runtime.TryGetInitializationReference(root, profile?.Profile.PrimaryLightReference ?? "primaryLight", out var light) &&
                    runtime.GetCapabilities<ICelestialLightSourceCapability>(light).Count == 1)
                {
                    primaryLightId = light;
                    return bodiesById.Values.SingleOrDefault(body => body.InstanceId == light);
                }
            }
            var lights = bodiesById.Values.Where(body => runtime.GetCapabilities<ICelestialLightSourceCapability>(body.InstanceId).Count == 1).ToArray();
            if (lights.Length != 1) return null;
            primaryLightId = lights[0].InstanceId;
            return lights[0];
        }
        public CelestialResult<CelestialTargetToken> CaptureTarget(int handle)
        {
            var body = GetBodyById(handle);
            if (celestialRuntime == null || body == null) return CelestialResult.Reject<CelestialTargetToken>(new CelestialError("BodyMissing"));
            var token = new CelestialTargetToken(celestialRuntime.WorldSessionId, body.InstanceId, body.DescriptorRevision, handle);
            var valid = ValidateTarget(token);
            return valid.Success ? CelestialResult.Ok(token) : CelestialResult.Reject<CelestialTargetToken>(valid.Errors.ToArray());
        }
        public CelestialResult<bool> ValidateTarget(CelestialTargetToken token)
        {
            if (celestialRuntime == null || token.WorldSessionId != celestialRuntime.WorldSessionId)
                return CelestialResult.Reject<bool>(new CelestialError("StaleSession", token.InstanceId));
            var current = celestialRuntime.Query(token.InstanceId);
            if (!current.Success) return CelestialResult.Reject<bool>(current.Errors.ToArray());
            if (current.Value.Description.Handle != token.Handle || current.Value.Description.DescriptorRevision != token.DescriptorRevision)
                return CelestialResult.Reject<bool>(new CelestialError("DescriptorRevisionConflict", token.InstanceId));
            return current.Value.Lifecycle == CelestialLifecycle.Ready && !celestialRuntime.IsRestoring ? CelestialResult.Ok(true) :
                CelestialResult.Reject<bool>(new CelestialError("BodyNotReady", token.InstanceId));
        }
        private bool CanAttachConsumer(int handle)
        {
            if (celestialRuntime == null) return true;
            var body = celestialRuntime.Query(bodiesById[handle].InstanceId);
            return body.Success && body.Value.Lifecycle == CelestialLifecycle.Ready;
        }
        private sealed class ConsumerResources(CelestialBodyManager manager) : ICelestialRemovalResources
        {
            public IReadOnlyList<CelestialError> Inspect(CelestialInstanceId id)
            {
                var query = manager.celestialRuntime!.Query(id);
                if (!query.Success) return query.Errors;
                int handle = query.Value.Description.Handle;
                var errors = manager.GetGroupIdsForBody(handle).Select(group => new CelestialError("ShipAttached", id, Detail: "group:" + group))
                    .Concat(manager.GetEntityIdsForBody(handle).Select(entity => new CelestialError("ShipAttached", id, Detail: "entity:" + entity))).ToList();
                var game = manager.hostGame;
                var landing = game?.GetComponent<Landing.LandingTransitManager>();
                if (landing != null)
                    foreach (var task in landing.Tasks.Where(task => task.TargetBodyId == handle && task.Status != Landing.LandingTransitStatus.Completed))
                        errors.Add(new("TransportActive", id, Detail: "landing:" + task.TaskId));
                var world = game?.World ?? (ReferenceEquals(Current.Game, game) ? Current.CreatingWorld : null);
                if (world?.worldObjects != null)
                    foreach (var transit in world.worldObjects.AllWorldObjects.OfType<Launch.LaunchTransitWorldObject>().Where(transit => !transit.Destroyed &&
                        transit.BodyId == handle && transit.Status != Launch.LaunchTransitStatus.Completed))
                        errors.Add(new("TransportActive", id, Detail: "launch:" + transit.ID));
                var structures = game?.GetComponent<Megastructures.MegastructureManager>();
                if (structures != null)
                    foreach (var structure in structures.Snapshots.Where(structure => structure.Lifecycle != Megastructures.StructureLifecycle.Retired))
                        if (structure.Nodes.Any(node => node.Spatial.Mode == Megastructures.SpatialMode.BodyAnchored && node.Spatial.BodyId == handle ||
                            node.Spatial.Mode == Megastructures.SpatialMode.FrameFixed && node.Spatial.FrameId == handle))
                            errors.Add(new("MegastructureAttached", id, Detail: structure.Id));
                var ai = game?.GetComponent<SpaceAI.SpaceAiManager>();
                if (ai != null)
                    foreach (var ship in ai.Ships.Where(ship => ship.planning != null && ship.planningCelestialTarget?.InstanceId == id))
                        errors.Add(new("ActiveOperation", id, Detail: "AI:" + ship.groupId));
                return errors;
            }
            public CelestialResult<bool> ReleaseEmptySurface(CelestialInstanceId id) => CelestialResult.Ok(true);
        }
        private void BuildInstanceTable(CelestialRuntime runtime, CelestialCatalogSnapshot catalog)
        {
            var handles = catalog.Bodies.ToDictionary(body => body.Id, body => body.Handle);
            var next = new Dictionary<int, CelestialBodyInfo>();
            var hierarchyRoles = new List<int>();
            foreach (var description in catalog.Bodies)
            {
                var query = runtime.Query(description.Id).Value;
                var properties = runtime.Creation.GetProperties(description.Id);
                var atmosphere = runtime.GetCapabilities<ICelestialAtmosphereCapability>(description.Id).SingleOrDefault()?.GetProfile() ?? default;
                var visuals = runtime.GetCapabilities<ICelestialVisualCapability>(description.Id).Select(capability => capability.GetVisualDescription()).ToArray();
                var visualAtmosphere = visuals.SingleOrDefault(visual => visual.Kind == CelestialVisualKind.Atmosphere);
                var projected = CelestialRenderProjection.Create(description, visuals,
                    runtime.GetCapabilities<ICelestialSurfaceCapability>(description.Id).Count == 1, atmosphere, 0);
                var registration = runtime.Creation.GetRegistration(description.Id);
                ExistingEphemerisParameters? model = null;
                if (registration?.Initialization.ProviderId == ExistingCelestialEphemerisBridge.Id)
                {
                    var parsed = ExistingEphemerisParameters.Parse(registration.Initialization);
                    if (parsed.Success) model = parsed.Value;
                }
                string originKey = model?.FrameOriginReference ?? "frameOrigin";
                int origin = runtime.TryGetInitializationReference(description.Id, originKey, out var originId) && handles.TryGetValue(originId, out int originHandle) ? originHandle : description.Handle;
                // category remains open metadata. Existing provider roles only adapt the legacy query/UI shape.
                var type = projected.bodyType;
                if (model != null && Enum.TryParse<CelestialBodyType>(model.BodyType, out var modelType) && Enum.IsDefined(modelType)) type = modelType;
                else if (type != CelestialBodyType.Star) hierarchyRoles.Add(description.Handle);
                next.Add(description.Handle, projected with {
                    label = runtime.Creation.GetLabel(description.Id), bodyType = type,
                    ParentBodyId = query.ParentId.HasValue && handles.TryGetValue(query.ParentId.Value, out int parent) ? parent : -1,
                    ReferenceFrameBodyId = origin, atmosphereThickness = atmosphere.topAltitude,
                    atmosphereVisualThickness = visualAtmosphere == null ? 0 : visualAtmosphere.Profile.ShellHeightMeters * 2,
                    dayLengthTicks = CelestialDayLength.ResolveFrom(properties),
                    orbitRadius = model?.OrbitRadius ?? 0, orbitEccentricity = model?.Eccentricity ?? 0,
                    orbitPeriod = model?.OrbitPeriod ?? 0,
                    initialPhase = model?.InitialPhase ?? 0, orbitNormal = model == null ? Vector3.up : (Vector3)model.OrbitNormal });
            }
            foreach (int handle in hierarchyRoles)
            {
                var body = next[handle];
                if (next.TryGetValue(body.ParentBodyId, out var parent) && parent.hasSurface && parent.bodyType != CelestialBodyType.Star)
                    next[handle] = body with { bodyType = CelestialBodyType.Moon };
            }
            foreach (int handle in next.Keys.ToArray())
                next[handle] = next[handle] with { moonBodies = Array.AsReadOnly(next.Values.Where(body => body.ParentBodyId == handle).ToArray()) };
            bodiesById = next;
            instanceCatalogRevision = catalog.Revision;
            instanceSession = runtime.WorldSessionId;
            instanceRestorePending = runtime.IsRestoring;
            primaryLightCatalog = -1;
        }
        private CelestialMotionState SampleInstanceMotion(CelestialBodyInfo body, double seconds)
        {
            PhysicsSnapshotResult result;
            if (celestialIntervalSampler != null && seconds >= celestialIntervalSampler.StartSeconds && seconds <= celestialIntervalSampler.EndSeconds)
                result = celestialIntervalSampler.Sample(seconds, new[] { body.InstanceId });
            else
            {
                var runtime = celestialRuntime!;
                var stamp = CommittedMotionStamp;
                var request = new PhysicsSampleRequest(runtime.WorldSessionId, stamp.TopologyRevision, new[] { body.InstanceId },
                    seconds == stamp.PhysicalSeconds ? PhysicsSamplePurpose.SimulationCommitted : PhysicsSamplePurpose.Presentation, seconds);
                // Preparation already owns the publication boundary and freezes this committed revision.
                // A worker must query the provider directly rather than wait for its host thread's lock.
                result = celestialIntervalCommittedStamp.HasValue ? runtime.Physics.Sample(request) : runtime.Sample(request);
            }
            if (!result.Success) throw new InvalidOperationException("CelestialMotionUnavailable: " + string.Join(",", result.Errors.Select(error => error.Code)));
            using var snapshot = result.Snapshot!;
            if (snapshot.Stamp.WorldSessionId != celestialRuntime!.WorldSessionId || snapshot.Stamp.ReferenceFrameId != celestialRuntime.Physics.ReferenceFrame.Id ||
                snapshot.Stamp.PhysicalSeconds != seconds || !snapshot.TryGetBodyState(body.InstanceId, out var state) || state.DescriptorRevision != body.DescriptorRevision)
                throw new InvalidOperationException("CelestialDescriptorMismatch: " + body.InstanceId);
            return state;
        }
        private (DVector3 Position, DVector3 Velocity) SampleExistingParent(CelestialBodyInfo body, double seconds)
        {
            var interval = celestialIntervalSampler != null && seconds >= celestialIntervalSampler.StartSeconds && seconds <= celestialIntervalSampler.EndSeconds ? celestialIntervalSampler : null;
            var result = ((ExistingCelestialEphemerisBridge)celestialRuntime!.Physics).SampleParent(body.InstanceId, seconds, body.DescriptorRevision, interval);
            if (!result.Success) throw new InvalidOperationException("CelestialMotionUnavailable: " + string.Join(",", result.Errors.Select(error => error.Code)));
            return result.Value;
        }
    }
}
