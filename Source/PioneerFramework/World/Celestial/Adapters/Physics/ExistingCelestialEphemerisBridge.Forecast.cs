using System;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed partial class ExistingCelestialEphemerisBridge
    {
        public CelestialResult<CelestialBodyForecast> Forecast(CelestialForecastRequest request)
        {
            lock (gate)
            {
                CelestialResult<CelestialBodyForecast> Fail(string code) => CelestialResult.Reject<CelestialBodyForecast>(new CelestialError(code, request.InstanceId));
                if (request.WorldSessionId != SessionId) return Fail("StaleSession");
                if (request.ExpectedTopologyRevision != CommittedStamp.TopologyRevision) return Fail("RevisionConflict");
                if (!double.IsFinite(request.PhysicalSeconds) || request.PhysicalSeconds < 0) return Fail("TimeUnavailable");
                if (!bodies.TryGetValue(request.InstanceId, out var body)) return Fail("BodyMissing");
                if (body.Registration.Descriptor.DescriptorRevision != request.ExpectedDescriptorRevision) return Fail("DescriptorRevisionConflict");
                // Evaluate the committed model only. A prepared graph and pending registrations stay private.
                var sample = Snapshot(bodies, CommittedStamp with { PhysicalSeconds = request.PhysicalSeconds }, new[] { request.InstanceId }, PhysicsSampleKind.Committed);
                if (!sample.Success) return CelestialResult.Reject<CelestialBodyForecast>(sample.Errors.ToArray());
                using var snapshot = sample.Snapshot!;
                snapshot.TryGetBodyState(request.InstanceId, out var motion);
                var local = body.Parameters.SampleParent(request.PhysicalSeconds);
                return CelestialResult.Ok(new CelestialBodyForecast(CommittedStamp, request.PhysicalSeconds,
                    body.Registration.Descriptor, body.Registration.ParentInstanceId, motion, local.Position, local.Velocity) {
                        Source = new(ProviderId, ContractVersion) });
            }
        }
    }
}
