using System;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class CelestialViewAdapter(CelestialRuntime runtime)
    {
        private readonly CelestialVisualDescriptorCache cache = new(runtime);
        public CelestialResult<CelestialViewSnapshot> Capture(CelestialInstanceId id, PhysicsSampleStamp expected) => CaptureCore(id, expected, null);
        public CelestialResult<CelestialViewSnapshot> CapturePresentation(CelestialInstanceId id, double physicalSeconds) => CaptureCore(id, runtime.CurrentStamp, physicalSeconds);
        public System.Collections.Generic.IReadOnlyList<CelestialResult<CelestialViewSnapshot>> CapturePresentationBatch(
            System.Collections.Generic.IEnumerable<CelestialInstanceId> ids, PhysicsSampleStamp expected, double seconds) => runtime.WithPublication(() => {
                var requested = ids.ToArray();
                var bodies = requested.Select(runtime.Query).ToArray();
                var ready = bodies.Where(body => body.Success && body.Value.Lifecycle is CelestialLifecycle.Ready or CelestialLifecycle.Removing)
                    .Select(body => body.Value.Id).ToArray();
                var sampled = runtime.Sample(new(runtime.WorldSessionId, expected.TopologyRevision, ready, PhysicsSamplePurpose.Presentation, seconds));
                using var snapshot = sampled.Snapshot;
                cache.Prune();
                return requested.Select((id, index) => CaptureCore(id, expected, seconds, sampled, bodies[index])).ToArray();
            });
        internal bool IsConfigurationCurrent() => runtime.WithPublication(cache.IsCurrent);
        internal bool TryGetConfiguration(CelestialViewSnapshot snapshot, out CelestialVisualDescriptorCache.Entry configuration)
            => cache.TryGet(snapshot, out configuration);
        private CelestialResult<CelestialViewSnapshot> CaptureCore(CelestialInstanceId id, PhysicsSampleStamp expected, double? presentationSeconds,
            PhysicsSnapshotResult? shared = null, CelestialResult<CelestialBodySnapshot>? capturedBody = null) => runtime.WithPublication(() => {
            CelestialResult<CelestialViewSnapshot> Reject(string code) => CelestialResult.Reject<CelestialViewSnapshot>(new CelestialError(code, id));
            if (expected.WorldSessionId != runtime.WorldSessionId) return Reject("StaleSession");
            if (expected != runtime.CurrentStamp) return Reject("StaleSampleStamp");
            if (presentationSeconds.HasValue && (!double.IsFinite(presentationSeconds.Value) || presentationSeconds.Value > expected.PhysicalSeconds)) return Reject("TimeUnavailable");
            var body = capturedBody ?? runtime.Query(id);
            if (!body.Success || body.Value.Lifecycle is not (CelestialLifecycle.Ready or CelestialLifecycle.Removing))
            { cache.Forget(id); return Reject("BodyNotReady"); }
            var sampled = shared ?? runtime.Sample(new(runtime.WorldSessionId, expected.TopologyRevision, new[] { id },
                presentationSeconds.HasValue ? PhysicsSamplePurpose.Presentation : PhysicsSamplePurpose.SimulationCommitted, presentationSeconds));
            if (!sampled.Success) return CelestialResult.Reject<CelestialViewSnapshot>(sampled.Errors.ToArray());
            using var owned = shared == null ? sampled.Snapshot : null;
            var snapshot = sampled.Snapshot!;
            bool validStamp = presentationSeconds.HasValue ? snapshot.Stamp.WorldSessionId == expected.WorldSessionId && snapshot.Stamp.ClockRevision == expected.ClockRevision &&
                snapshot.Stamp.TopologyRevision == expected.TopologyRevision && snapshot.Stamp.ReferenceFrameId == expected.ReferenceFrameId && snapshot.Stamp.StateRevision <= expected.StateRevision &&
                snapshot.Stamp.PhysicalSeconds == presentationSeconds.Value : snapshot.Stamp == expected;
            if (!validStamp || !snapshot.TryGetBodyDescriptor(id, out var descriptor) || descriptor.DescriptorRevision != body.Value.Description.DescriptorRevision ||
                descriptor.RadiusMeters != body.Value.Description.RadiusMeters || descriptor.GravitationalParameter != body.Value.Description.GravitationalParameter ||
                !snapshot.TryGetBodyState(id, out var motion)) return Reject("DescriptorStateMismatch");
            int? layerId = null;
            if (runtime.Surfaces != null)
            {
                var found = runtime.Surfaces.Find(id);
                if (found.Success) layerId = found.Value.LayerId;
                else if (found.Errors.Any(error => error.Code != "SurfaceNotCreated" && error.Code != "SurfaceNotReady")) return CelestialResult.Reject<CelestialViewSnapshot>(found.Errors.ToArray());
            }
            if (shared == null) cache.Prune();
            var visual = cache.Get(body.Value.Description, layerId);
            return CelestialResult.Ok(new CelestialViewSnapshot(body.Value.Description, snapshot.Stamp, motion,
                visual.NativeRadius, visual.Key, visual.Visuals));
        });
    }
}
