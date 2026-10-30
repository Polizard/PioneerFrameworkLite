using System;
using System.Linq;
using PioneerFramework.Celestial;

namespace PioneerFramework
{
    public partial class CelestialBodyManager
    {
        public CelestialResult<CelestialBodyForecast> ForecastBody(int handle, double seconds)
        {
            var body = GetBodyById(handle);
            if (body == null) return CelestialResult.Reject<CelestialBodyForecast>(new CelestialError("BodyMissing"));
            if (celestialRuntime == null || celestialRuntime.Physics is not ICelestialPhysicsForecastProvider provider)
                return CelestialResult.Reject<CelestialBodyForecast>(new CelestialError("ForecastUnavailable", body.InstanceId));
            var stamp = CommittedMotionStamp;
            var result = provider.Forecast(new(celestialRuntime.WorldSessionId, stamp.TopologyRevision, body.InstanceId, body.DescriptorRevision, seconds));
            if (!result.Success) return result;
            var value = result.Value;
            var q = value.Motion.Orientation;
            double norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (value.Source != new CelestialForecastSource(celestialRuntime.Physics.ProviderId, celestialRuntime.Physics.ContractVersion) ||
                value.BasedOn != stamp || value.PhysicalSeconds != seconds || value.Descriptor.Id != body.InstanceId ||
                value.Descriptor.DescriptorRevision != body.DescriptorRevision || value.Motion.DescriptorRevision != body.DescriptorRevision ||
                !value.Motion.IsValid || !value.Motion.Position.IsFinite() || !value.Motion.LinearVelocity.IsFinite() ||
                !value.Motion.AngularVelocity.IsFinite() || !double.IsFinite(norm) || Math.Abs(norm - 1) > 1e-12 ||
                !value.PositionRelativeToParent.IsFinite() || !value.VelocityRelativeToParent.IsFinite())
                return CelestialResult.Reject<CelestialBodyForecast>(new CelestialError("ForecastRevisionMismatch", body.InstanceId));
            return result;
        }

        private bool IsInPreparedOrCommittedInstant(double seconds)
            => celestialRuntime == null ||
                celestialIntervalSampler != null && seconds >= celestialIntervalSampler.StartSeconds && seconds <= celestialIntervalSampler.EndSeconds ||
                seconds == CommittedMotionStamp.PhysicalSeconds;

        internal CelestialResult<CelestialMotionState> MotionForPlanning(int handle, double seconds)
        {
            if (IsInPreparedOrCommittedInstant(seconds)) return CelestialResult.Ok(GetBodyMotion(handle, seconds));
            var forecast = ForecastBody(handle, seconds);
            return forecast.Success ? CelestialResult.Ok(forecast.Value.Motion) : CelestialResult.Reject<CelestialMotionState>(forecast.Errors.ToArray());
        }

        // Ships retain coordinates relative to their explicit reference-frame body.
        // An ancestral frame uses the original local-sum order, including the default Moon/Earth case.
        internal (DVector3 Position, DVector3 Velocity) BodyFrameOffset(int handle, double seconds)
        {
            var body = GetBodyById(handle) ?? throw new InvalidOperationException("BodyMissing");
            int frame = GetReferenceFrameId(handle);
            if (handle == frame) return (DVector3.zero, DVector3.zero);
            var current = body;
            int remaining = bodiesById.Count;
            var position = DVector3.zero; var velocity = DVector3.zero;
            while (current != null && current.bodyId != frame)
            {
                if (remaining-- <= 0) throw new InvalidOperationException("CelestialParentCycle");
                position += GetBodyPositionParent(current.bodyId, seconds);
                velocity += GetBodyVelocityParent(current.bodyId, seconds);
                current = current.ParentBodyId < 0 ? null : GetBodyById(current.ParentBodyId);
            }
            if (current != null) return (position, velocity);
            // Both states share the provider's absolute reference frame. Subtract that frame body's state once.
            var absolute = GetBodyMotion(handle, seconds); var origin = GetBodyMotion(frame, seconds);
            return (absolute.Position - origin.Position, absolute.LinearVelocity - origin.LinearVelocity);
        }

        internal CelestialResult<(DVector3 Position, DVector3 Velocity)> FrameOffsetForPlanning(int handle, double seconds)
        {
            if (IsInPreparedOrCommittedInstant(seconds)) return CelestialResult.Ok(BodyFrameOffset(handle, seconds));
            var body = GetBodyById(handle);
            if (body == null) return CelestialResult.Reject<(DVector3, DVector3)>(new CelestialError("BodyMissing"));
            int frame = GetReferenceFrameId(handle);
            if (handle == frame) return CelestialResult.Ok((DVector3.zero, DVector3.zero));
            var current = body;
            var position = DVector3.zero; var velocity = DVector3.zero;
            int remaining = bodiesById.Count;
            while (current != null && current.bodyId != frame)
            {
                if (remaining-- <= 0) return CelestialResult.Reject<(DVector3, DVector3)>(new CelestialError("ParentCycle"));
                var forecast = ForecastBody(current.bodyId, seconds);
                if (!forecast.Success) return CelestialResult.Reject<(DVector3, DVector3)>(forecast.Errors.ToArray());
                position += forecast.Value.PositionRelativeToParent;
                velocity += forecast.Value.VelocityRelativeToParent;
                current = current.ParentBodyId < 0 ? null : GetBodyById(current.ParentBodyId);
            }
            if (current != null) return CelestialResult.Ok((position, velocity));
            var absolute = MotionForPlanning(handle, seconds); var origin = MotionForPlanning(frame, seconds);
            if (!absolute.Success || !origin.Success) return CelestialResult.Reject<(DVector3, DVector3)>(absolute.Errors.Concat(origin.Errors).ToArray());
            return CelestialResult.Ok((absolute.Value.Position - origin.Value.Position, absolute.Value.LinearVelocity - origin.Value.LinearVelocity));
        }
    }
}
