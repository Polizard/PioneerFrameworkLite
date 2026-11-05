using PioneerFramework.Celestial;

namespace PioneerFramework
{
    public partial class SpaceEntityGroup
    {
        // Same analytic ship-circle expression used by committed integration; the planning path has no cache writes.
        private DVector3 CircularLocalPosition(double currentTick)
        {
            double deltaAngle = circularAngularVelocity * (currentTick - circularStartTick);
            double angle = circularStartAngle + deltaAngle;
            double cosA = System.Math.Cos(angle);
            double sinA = System.Math.Sin(angle);

            DVector3 localPos;
            if (circularOrbitNormal.sqrMagnitude > 0.001 && circularRadialRef.sqrMagnitude > 0.001)
            {
                // 使用轨道平面：pos = r * (radialRef * cos(θ) + yDir * sin(θ))
                DVector3 yDir = DVector3.Cross(circularOrbitNormal, circularRadialRef).normalized;
                localPos = circularRadius * (circularRadialRef * cosA + yDir * sinA);
            }
            else
            {
                // 旧存档兼容：XZ 平面
                localPos = new DVector3(System.Math.Cos(angle) * circularRadius, 0, System.Math.Sin(angle) * circularRadius);
            }

            return localPos;
        }

        internal CelestialResult<(DVector3 Position, DVector3 Velocity)> CircularStateForPlanning(CelestialBodyManager manager, double seconds)
        {
            var offset = manager.FrameOffsetForPlanning(CelestialBodyId, seconds);
            if (!offset.Success) return CelestialResult.Reject<(DVector3, DVector3)>(System.Linq.Enumerable.ToArray(offset.Errors));
            var position = CircularLocalPosition(seconds);
            if (circularCentralBodyId >= 0) position = FrameConvert.BodyToSystem((BodyVec3)position, (SystemVec3)offset.Value.Position);
            var radial = FrameConvert.SystemToBody((SystemVec3)position, (SystemVec3)offset.Value.Position).normalized.v;
            var tangent = circularOrbitNormal.sqrMagnitude > 0.001 ? DVector3.Cross(circularOrbitNormal, radial).normalized
                : new DVector3(-radial.z, 0, radial.x).normalized;
            var velocity = tangent * (circularAngularVelocity * circularRadius);
            if (circularCentralBodyId >= 0) velocity += offset.Value.Velocity;
            return CelestialResult.Ok((position, velocity));
        }
    }
}
