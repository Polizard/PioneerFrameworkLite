using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PioneerFramework.Celestial
{
    public sealed partial class ExistingEphemerisParameters
    {
        public bool UsesAuthoritativeRotation { get; private set; }
        public DVector3 RotationAxis { get; private set; } = DVector3.up;
        public double RotationPhaseRadians { get; private set; }
        public bool Retrograde { get; private set; }
        public string SynchronousTargetReference { get; private set; } = "";
        internal static PhysicsInitialization CreateInitialization(CelestialMotionXml motion)
            => new(ExistingCelestialEphemerisBridge.Id, "existing.circular.v2", 2, JsonSerializer.SerializeToUtf8Bytes(ToMotionData(motion)));

        private static MotionData ToMotionData(CelestialMotionXml motion)
        {
            var rotation = motion.rotation;
            return new MotionData(motion.orbitRadiusMeters, motion.orbitPeriodSeconds, motion.orbitPhaseRadians,
                new[] { motion.orbitNormalX, motion.orbitNormalY, motion.orbitNormalZ }, motion.bodyType, motion.frameOriginReference,
                new[] { rotation.axisX, rotation.axisY, rotation.axisZ }, rotation.periodSeconds, rotation.phaseRadians, rotation.retrograde, rotation.synchronousTargetReference);
        }
        private static CelestialResult<ExistingEphemerisParameters> ParseV2(PhysicsInitialization initialization)
        {
            if (initialization.ProviderId != ExistingCelestialEphemerisBridge.Id || initialization.ModeId != "existing.circular.v2" || initialization.Version != 2)
                return CelestialResult.Reject<ExistingEphemerisParameters>(new CelestialError("UnsupportedInitialization"));
            try
            {
                CelestialJsonFields.RejectDuplicates(initialization.Payload.Span);
                var data = JsonSerializer.Deserialize<MotionData>(initialization.Payload.Span, MotionOptions) ?? throw new ArgumentException("Missing motion.");
                return CelestialResult.Ok(FromMotionData(data));
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            { return CelestialResult.Reject<ExistingEphemerisParameters>(new CelestialError("InvalidInitialization", Detail: exception.Message)); }
        }
        private static ExistingEphemerisParameters FromMotionData(MotionData data)
        {
            if (data.OrbitNormal == null || data.OrbitNormal.Length != 3 || data.RotationAxis == null || data.RotationAxis.Length != 3)
                throw new ArgumentException("Invalid axis.");
            var readable = new CelestialMotionXml {
                orbitRadiusMeters = data.OrbitRadiusMeters, orbitPeriodSeconds = data.OrbitPeriodSeconds, orbitPhaseRadians = data.OrbitPhaseRadians,
                orbitNormalX = data.OrbitNormal[0], orbitNormalY = data.OrbitNormal[1], orbitNormalZ = data.OrbitNormal[2], bodyType = data.BodyType,
                frameOriginReference = data.FrameOriginReference, rotation = new() { axisX = data.RotationAxis[0], axisY = data.RotationAxis[1], axisZ = data.RotationAxis[2],
                    periodSeconds = data.RotationPeriodSeconds, phaseRadians = data.RotationPhaseRadians, retrograde = data.Retrograde, synchronousTargetReference = data.SynchronousTargetReference } };
            var errors = readable.ConfigErrors().ToArray(); if (errors.Length > 0) throw new ArgumentException(string.Join(",", errors));
            return new ExistingEphemerisParameters(new Data {
                orbitRadius = data.OrbitRadiusMeters, orbitPeriod = data.OrbitPeriodSeconds, initialPhase = data.OrbitPhaseRadians, orbitNormal = data.OrbitNormal,
                dayLengthTicks = data.RotationPeriodSeconds ?? (data.OrbitPeriodSeconds > 0 ? data.OrbitPeriodSeconds : 60000), bodyType = data.BodyType, frameOriginReference = data.FrameOriginReference }) {
                    UsesAuthoritativeRotation = true, RotationAxis = new DVector3(data.RotationAxis[0], data.RotationAxis[1], data.RotationAxis[2]).normalized,
                    RotationPhaseRadians = data.RotationPhaseRadians, Retrograde = data.Retrograde, SynchronousTargetReference = data.SynchronousTargetReference };
        }

        internal (DQuaternion Orientation, DVector3 AngularVelocity) Rotation(double seconds, DVector3 targetOffset, DVector3 targetVelocity)
        {
            if (SynchronousTargetReference.Length == 0)
            {
                double omega = (Retrograde ? 1 : -1) * 2 * Math.PI / DayLengthTicks;
                var north = RotationAxis;
                double dot = DVector3.Dot(DVector3.up, north);
                DQuaternion align;
                if (dot < 0)
                {
                    // Negative-Y half angles use 1-dot; 1+dot loses the declared transverse axis near the antipode.
                    var cross = DVector3.Cross(DVector3.up, north);
                    double scale = Math.Max(Math.Abs(cross.x), Math.Abs(cross.z));
                    if (scale == 0) align = new(1, 0, 0, 0);
                    else
                    {
                        double scaledX = cross.x / scale, scaledZ = cross.z / scale;
                        double scaledLength = Math.Sqrt(scaledX * scaledX + scaledZ * scaledZ);
                        double halfSine = Math.Sqrt((1 - dot) * .5);
                        align = new(scaledX / scaledLength * halfSine, 0, scaledZ / scaledLength * halfSine,
                            scale * scaledLength / (2 * halfSine));
                    }
                }
                else { var cross = DVector3.Cross(DVector3.up, north); double length = Math.Sqrt(2 * (1 + dot)); align = new(cross.x / length, cross.y / length, cross.z / length, length / 2); }
                return (DQuaternion.AngleAxis(RotationPhaseRadians + omega * seconds, north) * align, north * omega);
            }
            double distance = targetOffset.magnitude;
            if (!(distance > 0) || !double.IsFinite(distance)) throw new ArgumentException("InvalidSynchronousTargetPosition");
            var forward = targetOffset / distance;
            var forwardDerivative = (targetVelocity - forward * DVector3.Dot(forward, targetVelocity)) / distance;
            var z = -forward; var zd = -forwardDerivative;
            var rawX = DVector3.Cross(RotationAxis, z); double lengthX = rawX.magnitude;
            if (!(lengthX > 1e-12)) throw new ArgumentException("SynchronousTargetOnRotationAxis");
            var x = rawX / lengthX;
            var rawXd = DVector3.Cross(RotationAxis, zd);
            var xd = (rawXd - x * DVector3.Dot(x, rawXd)) / lengthX;
            var y = DVector3.Cross(z, x); var yd = DVector3.Cross(zd, x) + DVector3.Cross(z, xd);
            var angular = .5 * (DVector3.Cross(x, xd) + DVector3.Cross(y, yd) + DVector3.Cross(z, zd));
            return (FromBasis(x, y, z) * DQuaternion.AngleAxis(RotationPhaseRadians, DVector3.up), angular);
        }
        private static DQuaternion FromBasis(DVector3 x, DVector3 y, DVector3 z)
        {
            double trace = x.x + y.y + z.z;
            if (trace > 0) { double s = Math.Sqrt(trace + 1) * 2; return new((y.z - z.y) / s, (z.x - x.z) / s, (x.y - y.x) / s, .25 * s); }
            if (x.x > y.y && x.x > z.z) { double s = Math.Sqrt(1 + x.x - y.y - z.z) * 2; return new(.25 * s, (y.x + x.y) / s, (z.x + x.z) / s, (y.z - z.y) / s); }
            if (y.y > z.z) { double s = Math.Sqrt(1 + y.y - x.x - z.z) * 2; return new((y.x + x.y) / s, .25 * s, (z.y + y.z) / s, (z.x - x.z) / s); }
            else { double s = Math.Sqrt(1 + z.z - x.x - y.y) * 2; return new((z.x + x.z) / s, (z.y + y.z) / s, .25 * s, (x.y - y.x) / s); }
        }
        private static readonly JsonSerializerOptions MotionOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
        private sealed record MotionData(double OrbitRadiusMeters, double OrbitPeriodSeconds, double OrbitPhaseRadians, double[] OrbitNormal,
            string BodyType, string FrameOriginReference, double[] RotationAxis, double? RotationPeriodSeconds, double RotationPhaseRadians,
            bool Retrograde, string SynchronousTargetReference);
    }
}
